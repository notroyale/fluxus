using System.Text.Json;
using Fluxus.Core;
using Fluxus.Infrastructure.Geometry;
using Fluxus.Infrastructure.Sources;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fluxus.Infrastructure.Persistence;

public sealed class PostgresCatalogueStore(CatalogueDb db, SourceOptions options, TimeProvider clock, NpgsqlDataSource dataSource) : ICatalogueStore, IAuctionRecheckCandidates
{
    private ImportRunEntity? run;

    public async Task<IReadOnlyList<ListingItem>> GetRecheckCandidatesAsync(int limit, CancellationToken ct)
    {
        if (options.Mode != "Live" || limit < 1) return [];
        var now = clock.GetUtcNow();
        var retryBefore = now.AddHours(-1);
        var candidates = await db.Auctions.AsNoTracking()
            .Where(a => a.Mode == "Live" && (a.Status == "announced" || a.Status == "running") &&
                a.EndsAt <= now && a.ObservedAt <= retryBefore)
            .OrderBy(a => a.ObservedAt).ThenBy(a => a.Id).Take(Math.Min(limit, 20))
            .Select(a => new { a.SourceId, a.Number, a.SourceUrl }).ToArrayAsync(ct);
        return candidates.Where(a => Uri.TryCreate(a.SourceUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && uri.Host == "www.evarzytynes.lt" && uri.AbsolutePath == "/evs/pages/auction.do")
            .Select(a => new ListingItem(a.SourceId, a.Number, new Uri(a.SourceUrl), "")).ToArray();
    }

    public async Task<IAsyncDisposable?> TryAcquireImportLockAsync(CancellationToken ct)
    {
        var connection = dataSource.CreateConnection();
        try
        {
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(738410062)", connection);
            if (await command.ExecuteScalarAsync(ct) is true) return new LockLease(connection);
            await connection.DisposeAsync();
            return null;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private sealed class LockLease(NpgsqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(738410062)", connection);
                await command.ExecuteNonQueryAsync();
            }
            finally { await connection.DisposeAsync(); }
        }
    }

    public async Task BeginRunAsync(string mode, CancellationToken ct)
    {
        await db.ImportRuns.Where(r => r.Outcome == "running").ExecuteUpdateAsync(
            setters => setters.SetProperty(r => r.Outcome, "abandoned"), ct);
        run = new ImportRunEntity { Mode = mode, StartedAt = clock.GetUtcNow() };
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct);
    }

    private async Task<ParcelEntity?> FindParcelAsync(LandItem item, CancellationToken ct)
    {
        if (item.UniqueNumber is null && item.CadastralNumber is null) return null;
        var persisted = await db.Parcels.Where(p =>
            item.UniqueNumber != null && p.UniqueNumber == item.UniqueNumber ||
            item.CadastralNumber != null && p.CadastralNumber == item.CadastralNumber).ToListAsync(ct);
        var matches = persisted.Concat(db.Parcels.Local.Where(p =>
            item.UniqueNumber != null && p.UniqueNumber == item.UniqueNumber ||
            item.CadastralNumber != null && p.CadastralNumber == item.CadastralNumber)).DistinctBy(p => p.Id).ToList();
        if (matches.Count > 1) throw new FormatException("Parcel identifiers refer to different records.");
        var match = matches.SingleOrDefault();
        if (match is not null && (item.UniqueNumber is not null && match.UniqueNumber is not null && item.UniqueNumber != match.UniqueNumber ||
            item.CadastralNumber is not null && match.CadastralNumber is not null && item.CadastralNumber != match.CadastralNumber))
            throw new FormatException("Parcel identity conflict.");
        return match;
    }

    public async Task<ParcelResolution?> GetCachedGeometryAsync(LandItem item, CancellationToken ct)
    {
        var parcel = await FindParcelAsync(item, ct);
        if (parcel?.Geometry is { } geometry && parcel.GeometryFetchedAt > clock.GetUtcNow().AddDays(-30))
            return new("resolved_exact", ArcGisGeometry.Write(geometry), parcel.MunicipalityCode, parcel.MunicipalityName, "cache_hit");
        if (parcel is not null && parcel.Geometry is null && parcel.AttemptedAt > clock.GetUtcNow().AddHours(
            parcel.ResolutionStatus == "not_found" ? -24 : -1))
            return new(parcel.ResolutionStatus);
        return null;
    }

    public async Task SaveAuctionAsync(ParsedAuction input, IReadOnlyDictionary<string, ParcelResolution> resolutions, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var auction = await db.Auctions.Include(a => a.Items).SingleOrDefaultAsync(a => a.SourceId == input.SourceId, ct);
        var isNew = auction is null;
        if (auction is null) { auction = new() { SourceId = input.SourceId }; db.Auctions.Add(auction); }
        var observedAt = options.Mode == "Fixture" ? options.FixtureObservedAt ?? clock.GetUtcNow() : clock.GetUtcNow();
        var keys = input.Items.Select(i => i.SourceKey).ToHashSet();
        var observation = new AuctionObservationEntity
        {
            AuctionId = auction.Id, ObservedAt = observedAt, Mode = options.Mode,
            Kind = isNew ? "added" : "changed",
            Status = input.Status, PreviousStatus = isNew ? null : auction.Status,
            InitialPriceEur = input.InitialPriceEur, PreviousInitialPriceEur = isNew ? null : auction.InitialPriceEur,
            LastBidEur = input.LastBidEur, PreviousLastBidEur = isNew ? null : auction.LastBidEur,
            EndsAt = input.EndsAt, PreviousEndsAt = isNew ? null : auction.EndsAt, OfferCount = input.Items.Count,
            PriceChanged = !isNew && auction.InitialPriceEur != input.InitialPriceEur,
            BidChanged = !isNew && auction.LastBidEur != input.LastBidEur,
            StatusChanged = !isNew && auction.Status != input.Status,
            DeadlineChanged = !isNew && auction.EndsAt != input.EndsAt,
            OffersChanged = !isNew && !keys.SetEquals(auction.Items.Where(i => i.IsPresent).Select(i => i.SourceKey))
        };
        if (isNew || observation.PriceChanged || observation.BidChanged || observation.StatusChanged ||
            observation.DeadlineChanged || observation.OffersChanged)
            db.AuctionObservations.Add(observation);
        if (observation.PriceChanged)
        {
            auction.PriceChangedAt = observedAt;
            auction.PreviousInitialPriceEur = auction.InitialPriceEur;
        }
        if (observation.StatusChanged)
        {
            auction.StatusChangedAt = observedAt;
            auction.PreviousStatus = auction.Status;
        }
        auction.OfferCount = input.Items.Count;
        auction.Number = input.Number; auction.SourceUrl = input.SourceUrl; auction.Status = input.Status;
        auction.StartsAt = input.StartsAt; auction.EndsAt = input.EndsAt;
        auction.InitialPriceEur = input.InitialPriceEur; auction.LastBidEur = input.LastBidEur;
        auction.ContainsNonLandAssets = input.ContainsNonLandAssets;
        auction.ObservedAt = observedAt;
        auction.Mode = options.Mode;
        foreach (var land in input.Items)
        {
            var resolution = resolutions[land.SourceKey];
            var item = auction.Items.SingleOrDefault(i => i.SourceKey == land.SourceKey);
            if (item is null)
            {
                item = new() { SourceKey = land.SourceKey, FirstSeenAt = auction.ObservedAt };
                auction.Items.Add(item);
            }
            item.UniqueNumber = land.UniqueNumber; item.CadastralNumber = land.CadastralNumber; item.Address = land.Address;
            item.IsPresent = true; item.MissingSince = null;
            item.TotalAreaHa = land.TotalAreaHa; item.SoldAreaHa = land.SoldAreaHa; item.SoldAreaBasis = land.SoldAreaBasis;
            item.Share = land.ShareRaw; item.LandUse = land.LandUse; item.ComponentPriceEur = land.ComponentPriceEur;
            item.OfferType = land.OfferType; item.OwnershipKind = land.OwnershipKind;
            item.Issues = JsonSerializer.Serialize(land.Issues);
            item.ResolutionStatus = resolution.Status;
            var parcel = await FindParcelAsync(land, ct);
            if (parcel is null && (land.UniqueNumber is not null || land.CadastralNumber is not null))
            {
                parcel = new() { UniqueNumber = land.UniqueNumber, CadastralNumber = land.CadastralNumber };
                db.Parcels.Add(parcel);
            }
            if (parcel is not null)
            {
                parcel.UniqueNumber ??= land.UniqueNumber; parcel.CadastralNumber ??= land.CadastralNumber;
                parcel.AttemptedAt = clock.GetUtcNow();
                if (resolution.Status == "resolved_exact" && resolution.GeoJson is { } geoJson)
                {
                    parcel.Geometry = ArcGisGeometry.FromGeoJson(geoJson);
                    if (resolution.Reason != "cache_hit") parcel.GeometryFetchedAt = clock.GetUtcNow();
                    parcel.MunicipalityCode = resolution.MunicipalityCode; parcel.MunicipalityName = resolution.MunicipalityName;
                    parcel.ResolutionStatus = "resolved_exact";
                }
                else if (parcel.Geometry is null) parcel.ResolutionStatus = resolution.Status;
                if (parcel.Geometry is not null) item.ResolutionStatus = "resolved_exact";
                item.Parcel = parcel;
            }
        }
        // A confirmed detail omission retains the saved offer and its identity for the shortlist.
        // Absence from a bounded listing scrape never infers an auction outcome.
        foreach (var missing in auction.Items.Where(i => i.IsPresent && !keys.Contains(i.SourceKey)))
        {
            missing.IsPresent = false;
            missing.MissingSince = observedAt;
        }
        await db.SaveChangesAsync(ct);
        // PostGIS computes the point on surface using the accepted polygon, including holes.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE parcels SET \"MarkerPoint\" = ST_PointOnSurface(\"Geometry\") WHERE \"Geometry\" IS NOT NULL AND \"Id\" IN (SELECT \"ParcelId\" FROM auction_items WHERE \"AuctionId\" = {auction.Id})", ct);
        // A changed polygon invalidates the current score, while its historical analysis remains available.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE parcels SET "LandscapeIsStale" = CASE WHEN "CurrentLandscapeAnalysisId" IS NOT NULL
              AND "LandscapeGeometryHash" IS DISTINCT FROM md5(ST_AsEWKB(ST_Normalize("Geometry"))) THEN true ELSE "LandscapeIsStale" END,
              "LandscapeScore" = CASE WHEN "LandscapeGeometryHash" IS DISTINCT FROM md5(ST_AsEWKB(ST_Normalize("Geometry"))) THEN NULL ELSE "LandscapeScore" END,
              "LandscapeStatus" = CASE WHEN "LandscapeGeometryHash" IS DISTINCT FROM md5(ST_AsEWKB(ST_Normalize("Geometry"))) THEN 'not_analyzed' ELSE "LandscapeStatus" END,
              "LandscapeGeometryHash" = md5(ST_AsEWKB(ST_Normalize("Geometry")))
            WHERE "Geometry" IS NOT NULL AND "Id" IN (SELECT "ParcelId" FROM auction_items WHERE "AuctionId" = {auction.Id})
            """, ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    public async Task RecordIssueAsync(string sourceId, string code, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        db.ImportIssues.Add(new() { RunId = run!.Id, SourceId = sourceId, Code = code });
        await db.SaveChangesAsync(ct);
    }

    public async Task CompleteRunAsync(ImportSummary summary, CancellationToken ct)
    {
        if (run is null) return;
        db.ChangeTracker.Clear();
        var record = await db.ImportRuns.SingleAsync(r => r.Id == run.Id, ct);
        record.FinishedAt = clock.GetUtcNow(); record.Auctions = summary.Auctions; record.Items = summary.Items;
        record.Mapped = summary.Mapped; record.FailedAuctions = summary.FailedAuctions; record.DiscoveryComplete = summary.DiscoveryComplete;
        record.Outcome = summary.DiscoveryComplete ? "complete" : summary.Auctions > 0 ? "partial" : "failed";
        await db.SaveChangesAsync(ct);
    }
}
