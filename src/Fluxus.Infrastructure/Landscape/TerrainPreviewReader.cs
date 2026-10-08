using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fluxus.Infrastructure.Landscape;

public sealed record TerrainPreviewManifest(Guid ParcelId, string GeometryHash, string ImageSha256,
    int Width, int Height, double VerticalExaggeration, double MeshResolutionM,
    int SurveyYearFrom, int SurveyYearTo, bool HeightReferenceVerified, string Attribution,
    PreliminaryReportManifest? PreliminaryReport = null);
public sealed record PreliminaryReportManifest(Guid OfferId, string Sha256, DateTimeOffset CreatedAt, DateTimeOffset OfferObservedAt);
public sealed record PreliminaryReport(PreliminaryReportManifest Manifest, byte[] Pdf);
public sealed record TerrainPreview(TerrainPreviewManifest Manifest, byte[] Image);

// Inspection images are independent of analyses and never establish a score or source verification.
public sealed class TerrainPreviewReader(string? root)
{
    public async Task<TerrainPreview?> ReadAsync(Guid parcelId, string? geometryHash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(geometryHash)) return null;
        var directory = Path.Combine(Path.GetFullPath(root), parcelId.ToString());
        var manifestPath = Path.Combine(directory, "preview.json");
        var imagePath = Path.Combine(directory, "terrain.png");
        try
        {
            if (!Directory.Exists(directory) || Linked(root) || Linked(directory) ||
                !File.Exists(manifestPath) || !File.Exists(imagePath) || Linked(manifestPath) || Linked(imagePath)) return null;
            var json = await ReadBoundedAsync(manifestPath, 16 * 1024, ct);
            var manifest = JsonSerializer.Deserialize<TerrainPreviewManifest>(json, LandscapeJson.Options);
            if (manifest is null || manifest.ParcelId != parcelId || manifest.GeometryHash != geometryHash ||
                manifest.ImageSha256 is not { Length: 64 } || manifest.ImageSha256.Any(c => !Uri.IsHexDigit(c)) ||
                manifest.Width is < 1 or > 2048 || manifest.Height is < 1 or > 2048 ||
                !double.IsFinite(manifest.VerticalExaggeration) || manifest.VerticalExaggeration is < 1 or > 50 ||
                !double.IsFinite(manifest.MeshResolutionM) || manifest.MeshResolutionM is <= 0 or > 100 ||
                manifest.SurveyYearFrom < 2000 || manifest.SurveyYearTo < manifest.SurveyYearFrom ||
                manifest.SurveyYearTo > DateTime.UtcNow.Year || string.IsNullOrWhiteSpace(manifest.Attribution)) return null;
            var image = await ReadBoundedAsync(imagePath, 4 * 1024 * 1024, ct);
            if (image.Length < 24 || !image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                !image.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
                BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(16, 4)) != manifest.Width ||
                BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(20, 4)) != manifest.Height ||
                !Convert.ToHexStringLower(SHA256.HashData(image)).Equals(manifest.ImageSha256, StringComparison.OrdinalIgnoreCase)) return null;
            return new(manifest, image);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // An unavailable optional illustration must not prevent reading the report.
        }
    }

    public async Task<PreliminaryReport?> ReadReportAsync(TerrainPreview preview, Guid offerId,
        DateTimeOffset offerObservedAt, CancellationToken ct)
    {
        var report = preview.Manifest.PreliminaryReport;
        if (string.IsNullOrWhiteSpace(root) || report is null || report.OfferId != offerId ||
            report.OfferObservedAt != offerObservedAt || report.CreatedAt < report.OfferObservedAt ||
            report.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(5) || report.Sha256 is not { Length: 64 } ||
            report.Sha256.Any(c => !Uri.IsHexDigit(c))) return null;
        var directory = Path.Combine(Path.GetFullPath(root), preview.Manifest.ParcelId.ToString());
        var path = Path.Combine(directory, "preliminary-report.pdf");
        try
        {
            if (!File.Exists(path) || Linked(root) || Linked(directory) || Linked(path)) return null;
            var pdf = await ReadBoundedAsync(path, 8 * 1024 * 1024, ct);
            if (pdf.Length < 8 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8) ||
                !Convert.ToHexStringLower(SHA256.HashData(pdf)).Equals(report.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
            return new(report, pdf);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool Linked(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > limit) throw new IOException("Preview budget exceeded.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }
}
