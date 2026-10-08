using System.Buffers.Binary;
using System.Security.Cryptography;
using Fluxus.Infrastructure.Landscape;

namespace Fluxus.Tests;

public sealed class TerrainPreviewTests
{
    [Fact]
    public async Task ReportsNeverLeakToOtherOffersAndBecomeUnavailableAfterSnapshotChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "fluxus-report-test-" + Guid.NewGuid().ToString("N"));
        var parcel = Guid.NewGuid(); var offer = Guid.NewGuid(); var observed = DateTimeOffset.UtcNow.AddDays(-2);
        var directory = Path.Combine(root, parcel.ToString()); Directory.CreateDirectory(directory);
        try
        {
            var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\nSynthetic boundary fixture\n%%EOF");
            await File.WriteAllBytesAsync(Path.Combine(directory, "preliminary-report.pdf"), pdf);
            var report = new PreliminaryReportManifest(offer, Convert.ToHexStringLower(SHA256.HashData(pdf)),
                observed.AddDays(1), observed);
            var manifest = new TerrainPreviewManifest(parcel, new string('a', 32), new string('b', 64),
                1440, 960, 20, 20, 2019, 2022, false, "Synthetic fixture", report);
            var preview = new TerrainPreview(manifest, []); var reader = new TerrainPreviewReader(root);
            Assert.NotNull(await reader.ReadReportAsync(preview, offer, observed, default));
            Assert.Null(await reader.ReadReportAsync(preview, Guid.NewGuid(), observed, default));
            Assert.Null(await reader.ReadReportAsync(preview, offer, observed.AddSeconds(1), default));
            var future = preview with { Manifest = manifest with { PreliminaryReport = report with { CreatedAt = DateTimeOffset.UtcNow.AddDays(1) } } };
            Assert.Null(await reader.ReadReportAsync(future, offer, observed, default));
            pdf[^1] = 0; await File.WriteAllBytesAsync(Path.Combine(directory, "preliminary-report.pdf"), pdf);
            Assert.Null(await reader.ReadReportAsync(preview, offer, observed, default));
            Assert.Null(await reader.ReadReportAsync(preview with { Manifest = manifest with { PreliminaryReport = null } }, offer, observed, default));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PreviewRequiresMatchingGeometryValidMetadataAndUnmodifiedImage()
    {
        var root = Path.Combine(Path.GetTempPath(), "fluxus-preview-test-" + Guid.NewGuid().ToString("N"));
        var parcel = Guid.NewGuid(); var geometry = new string('a', 32);
        var directory = Path.Combine(root, parcel.ToString());
        Directory.CreateDirectory(directory);
        try
        {
            // Only a PNG header is needed to exercise file-format and manifest boundary checks.
            var image = new byte[24]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(image, 0);
            "IHDR"u8.CopyTo(image.AsSpan(12));
            BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(16), 1440);
            BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(20), 960);
            var manifest = new TerrainPreviewManifest(parcel, geometry, Convert.ToHexStringLower(SHA256.HashData(image)),
                1440, 960, 20, 20, 2019, 2022, false, "Synthetic boundary fixture");
            var json = Path.Combine(directory, "preview.json"); var png = Path.Combine(directory, "terrain.png");
            await File.WriteAllTextAsync(json, LandscapeJson.Write(manifest)); await File.WriteAllBytesAsync(png, image);
            var reader = new TerrainPreviewReader(root);
            var preview = await reader.ReadAsync(parcel, geometry, default);
            Assert.NotNull(preview); Assert.False(preview.Manifest.HeightReferenceVerified);
            Assert.Null(await reader.ReadAsync(parcel, new string('b', 32), default));
            Assert.Null(await reader.ReadAsync(Guid.NewGuid(), geometry, default));
            image[0] = 0; await File.WriteAllBytesAsync(png, image);
            Assert.Null(await reader.ReadAsync(parcel, geometry, default));
            image[0] = 137; await File.WriteAllBytesAsync(png, image);
            await File.WriteAllTextAsync(json, LandscapeJson.Write(manifest with { VerticalExaggeration = 51 }));
            Assert.Null(await reader.ReadAsync(parcel, geometry, default));
            await File.WriteAllTextAsync(json, LandscapeJson.Write(manifest with { Width = 1400 }));
            Assert.Null(await reader.ReadAsync(parcel, geometry, default));
            await File.WriteAllTextAsync(json, "bad JSON");
            Assert.Null(await reader.ReadAsync(parcel, geometry, default));
            Assert.Null(await new TerrainPreviewReader(null).ReadAsync(parcel, geometry, default));
        }
        finally { Directory.Delete(root, true); }
    }
}
