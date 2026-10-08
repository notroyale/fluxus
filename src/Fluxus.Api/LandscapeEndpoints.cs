using System.Security.Cryptography;
using System.Text.Json;
using Fluxus.Core.Landscape;
using Fluxus.Infrastructure.Landscape;
using Fluxus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fluxus.Api;

public sealed record LandscapeSummary(Guid? AnalysisId, string Status, int? Score, string? ScoreProfile,
    string Quality, bool IsStale, int? SurveyYearFrom, int? SurveyYearTo, DateTimeOffset? ComputedAt);

public static class LandscapeEndpoints
{
    public static LandscapeSummary Summary(AuctionItemEntity item)
    {
        var parcel = item.ResolutionStatus == "resolved_exact" ? item.Parcel : null;
        var analysis = parcel?.CurrentLandscapeAnalysis;
        var source = analysis is not null ? LandscapeJson.Read<LandscapeBundle>(analysis.Dataset.Manifest) : null;
        return new(analysis?.Id, parcel?.LandscapeStatus ?? "unavailable", parcel?.LandscapeScore,
            analysis?.ScoreProfile, analysis?.Quality ?? "unknown", parcel?.LandscapeIsStale ?? false,
            source?.SurveyYearFrom, source?.SurveyYearTo, analysis?.ComputedAt);
    }

    public static void MapLandscape(this WebApplication app)
    {
        app.MapGet("/api/auction-parcels/{id:guid}/landscape", async (Guid id, CatalogueDb db, TerrainPreviewReader previews, CancellationToken ct) =>
        {
            var item = await db.Items.AsNoTracking().Include(i => i.Auction).Include(i => i.Parcel!).ThenInclude(p => p.CurrentLandscapeAnalysis!)
                .ThenInclude(a => a.Dataset).Include(i => i.Parcel!).ThenInclude(p => p.CurrentLandscapeJob)
                .SingleOrDefaultAsync(i => i.Id == id, ct);
            if (item is null) return Results.NotFound();
            var parcel = item.ResolutionStatus == "resolved_exact" ? item.Parcel : null;
            var analysis = parcel?.CurrentLandscapeAnalysis;
            var preview = parcel?.Geometry is null ? null : await previews.ReadAsync(parcel.Id, parcel.LandscapeGeometryHash, ct);
            var report = preview is null ? null : await previews.ReadReportAsync(preview, item.Id, item.Auction.ObservedAt, ct);
            var source = analysis is not null ? LandscapeJson.Read<LandscapeBundle>(analysis.Dataset.Manifest) : null;
            var viewpoints = analysis is not null ? LandscapeJson.Read<LandscapeViewpoint[]>(analysis.Viewpoints) : [];
            double[]? observer = null;
            if (analysis?.VisibilityArtifact is not null && viewpoints.Length > 0)
            {
                var median = LandscapeJson.Read<LandscapeMetrics>(analysis.Metrics).VisibleAreaKm2;
                var point = viewpoints.OrderBy(v => Math.Abs(v.VisibleAreaKm2 - (median ?? 0))).First();
                var json = await db.Database.SqlQuery<string>($"SELECT ST_AsGeoJSON(ST_Transform(ST_SetSRID(ST_MakePoint({point.X}, {point.Y}),3346),4326)) AS \"Value\"").SingleAsync(ct);
                observer = JsonDocument.Parse(json).RootElement.GetProperty("coordinates").EnumerateArray().Select(c => c.GetDouble()).ToArray();
            }
            return Results.Ok(new
            {
                item.Id, parcelId = parcel?.Id, summary = Summary(item),
                components = analysis is null ? null : new { analysis.Views, analysis.Terrain, analysis.Nature, analysis.Seclusion },
                metrics = analysis is null ? null : LandscapeJson.Read<LandscapeMetrics>(analysis.Metrics),
                viewpoints,
                limitations = analysis is null ? new[] { parcel is null ? "parcel_location_unverified" : "source_data_not_loaded" }
                    : LandscapeJson.Read<string[]>(analysis.Limitations),
                source = source is null ? null : new { source.Provider, source.Release, source.SourceUrl,
                    source.LicenseUrl, source.Attribution, source.VerticalDatum, source.Preparation,
                    groundResolutionM = source.Ground.ResolutionM, surfaceResolutionM = source.Surface.ResolutionM,
                    analysis!.AnalysisVersion },
                failureCode = parcel?.CurrentLandscapeJob?.FailureCode,
                preliminaryReport = report is null ? null : new
                {
                    url = $"/api/auction-parcels/{item.Id}/preliminary-report/{report.Manifest.Sha256.ToLowerInvariant()}.pdf",
                    report.Manifest.CreatedAt, report.Manifest.OfferObservedAt
                },
                terrainPreview = preview is null ? null : new
                {
                    url = $"/api/landscape/parcels/{parcel!.Id}/terrain/{preview.Manifest.ImageSha256.ToLowerInvariant()}.png",
                    preview.Manifest.Width, preview.Manifest.Height, preview.Manifest.VerticalExaggeration,
                    preview.Manifest.MeshResolutionM, preview.Manifest.SurveyYearFrom, preview.Manifest.SurveyYearTo,
                    preview.Manifest.HeightReferenceVerified, preview.Manifest.Attribution
                },
                visibility = analysis?.VisibilityArtifact is null || parcel!.LandscapeIsStale ? null : new
                {
                    url = $"/api/landscape/analyses/{analysis.Id}/visibility.png",
                    coordinates = LandscapeJson.Read<double[][]>(analysis.VisibilityCoordinates!),
                    observer, eyeHeightM = 1.7, radiusM = 3000
                }
            });
        });

        app.MapGet("/api/auction-parcels/{id:guid}/preliminary-report/{hash}.pdf", async (Guid id, string hash,
            CatalogueDb db, TerrainPreviewReader previews, CancellationToken ct) =>
        {
            if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) return Results.NotFound();
            var item = await db.Items.AsNoTracking().Include(i => i.Auction).Include(i => i.Parcel)
                .SingleOrDefaultAsync(i => i.Id == id && i.ResolutionStatus == "resolved_exact", ct);
            var parcel = item?.Parcel;
            var preview = parcel?.Geometry is null ? null : await previews.ReadAsync(parcel.Id, parcel.LandscapeGeometryHash, ct);
            var report = preview is null ? null : await previews.ReadReportAsync(preview, id, item!.Auction.ObservedAt, ct);
            if (report is null || !hash.Equals(report.Manifest.Sha256, StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
            return Results.File(report.Pdf, "application/pdf", entityTag:
                new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{report.Manifest.Sha256.ToLowerInvariant()}\""));
        });

        app.MapGet("/api/landscape/parcels/{id:guid}/terrain/{hash}.png", async (Guid id, string hash,
            CatalogueDb db, TerrainPreviewReader previews, CancellationToken ct) =>
        {
            if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) return Results.NotFound();
            var parcel = await db.Parcels.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id &&
                p.ResolutionStatus == "resolved_exact" && p.Geometry != null, ct);
            var preview = parcel is null ? null : await previews.ReadAsync(id, parcel.LandscapeGeometryHash, ct);
            if (preview is null || !hash.Equals(preview.Manifest.ImageSha256, StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
            return Results.File(preview.Image, "image/png", entityTag:
                new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{preview.Manifest.ImageSha256.ToLowerInvariant()}\""));
        });

        app.MapGet("/api/landscape/analyses/{id:guid}/visibility.png", async (Guid id, CatalogueDb db,
            IConfiguration config, CancellationToken ct) =>
        {
            var analysis = await db.LandscapeAnalyses.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
            if (analysis?.VisibilityArtifact is null) return Results.NotFound();
            var root = config["Landscape:ArtifactRoot"] ?? Path.Combine(AppContext.BaseDirectory, "landscape-artifacts");
            var file = Path.Combine(Path.GetFullPath(root), id.ToString(), "visibility.png");
            if (!File.Exists(file) || new FileInfo(file).Length > 4 * 1024 * 1024) return Results.NotFound();
            await using var stream = File.OpenRead(file);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (hash != analysis.VisibilitySha256) return Results.Problem("Visibility artifact is unavailable.", statusCode: 503);
            return Results.File(file, "image/png", entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{hash}\""));
        });
    }
}
