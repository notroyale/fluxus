using Fluxus.Infrastructure.Geometry;
using Fluxus.Infrastructure.Landscape;
using System.Text.Json;

namespace Fluxus.Tests;

public sealed class LandscapePreparationTests
{
    private static LidarPreparationRequest Valid => new([new("tile.laz", new string('a', 64), 0)],
        [500000, 6100000, 506400, 6106400], "verified metres datum", "Provider height datum documentation",
        "https://example.org/source", "https://example.org/licence", 2025, 2025, DatumVerified: true);

    [Fact]
    public void PreparationRequiresEvidenceAndBoundedKnownInputs()
    {
        LidarPreparation.Validate(Valid);
        Assert.Throws<LandscapeInputException>(() => LidarPreparation.Validate(Valid with { DatumEvidence = "" }));
        Assert.Throws<LandscapeInputException>(() => LidarPreparation.Validate(Valid with { Tiles = [Valid.Tiles[0], Valid.Tiles[0]] }));
        Assert.Throws<LandscapeInputException>(() => LidarPreparation.Validate(Valid with { Bounds = [500000, 6100000, 520000, 6120000] }));
        Assert.Throws<LandscapeInputException>(() => LidarPreparation.Validate(Valid with { Tiles = [new("point.exe", new string('a', 64), 0)] }));
    }

    [Fact]
    public void UnverifiedHeightsRequireAnExplicitInspectionPreview()
    {
        Assert.Throws<LandscapeInputException>(() => LidarPreparation.Validate(Valid with { DatumVerified = false }));
        LidarPreparation.Validate(Valid with { DatumVerified = false, PreviewOnly = true });
        Assert.Throws<LandscapeInputException>(() => LidarPreparation.Validate(Valid with
            { HorizontalCrsDeclaration = new("provider.prj", new string('a', 64), 0) }));
        var many = Enumerable.Range(0, 64).Select(i => new LandscapeAsset($"tile-{i}.laz", new string('a', 64), 0)).ToArray();
        LidarPreparation.Validate(Valid with { Tiles = many });
        Assert.Throws<LandscapeInputException>(() => LidarPreparation.Validate(Valid with { Tiles = [.. many, new("tile-65.laz", new string('a', 64), 0)] }));
    }

    [Fact]
    public void ProviderProjectionFillsMissingCrsWithoutOverridingAConflictingEmbeddedCrs()
    {
        Assert.Throws<LandscapeInputException>(() => LidarPreparation.RequiresDeclaredCrs("", false));
        Assert.True(LidarPreparation.RequiresDeclaredCrs("", true));
        Assert.False(LidarPreparation.RequiresDeclaredCrs("PROJCS[AUTHORITY[\"EPSG\",\"3346\"]]", true));
        Assert.Throws<LandscapeInputException>(() => LidarPreparation.RequiresDeclaredCrs("PROJCS[AUTHORITY[\"EPSG\",\"4326\"]]", true));
    }

    [Theory]
    [InlineData(27, "ms0", 1)]
    [InlineData(27, "sd2", 2)]
    [InlineData(27, "sd11", 0)]
    [InlineData(27, "ms4", 0)]
    [InlineData(27, "sd4", 255)]
    [InlineData(27, "new-class", 255)]
    [InlineData(19, "hd1", 3)]
    [InlineData(21, "unknown", 2)]
    [InlineData(23, "unknown", 0)]
    public void ContextClassMappingKeepsUnverifiedCoverUnknown(int layer, string code, int category) =>
        Assert.Equal(category, GeoportalContext.CoverCategory(layer, code));

    [Fact]
    public void ProjectedArcGisRingsKeepHolesAndCannotMasqueradeAsWgs84()
    {
        using var rings = JsonDocument.Parse("[[[500000,6100000],[500100,6100000],[500100,6100100],[500000,6100100],[500000,6100000]],[[500020,6100020],[500080,6100020],[500080,6100080],[500020,6100080],[500020,6100020]]]");
        var geometry = ArcGisGeometry.Read(rings.RootElement, 3346);
        Assert.Equal(3346, geometry.SRID); Assert.Equal(6400, geometry.Area);
        Assert.Throws<FormatException>(() => ArcGisGeometry.Read(rings.RootElement));
    }

    [Fact]
    public async Task ContextDownloadRejectsTruncationAndMissingRequestedFeatures()
    {
        foreach (var result in new[]
        {
            "{\"spatialReference\":{\"wkid\":3346},\"exceededTransferLimit\":true,\"features\":[]}",
            "{\"spatialReference\":{\"wkid\":3346},\"features\":[]}",
            "{\"spatialReference\":{\"wkid\":4326},\"features\":[]}"
        })
        {
            using var http = new HttpClient(new Responses("{\"objectIds\":[12]}", result));
            var context = new GeoportalContext(http, new GdalRunner(), Path.GetTempPath());
            await Assert.ThrowsAsync<LandscapeInputException>(() => context.ReadLayerAsync(22, [500000, 6100000, 500200, 6100200], default));
        }
    }

    [Fact]
    public async Task ContextDownloadRechecksIdsAndDoesNotTreatAnEditingDatasetAsComplete()
    {
        using var http = new HttpClient(new Responses("{\"objectIds\":[12]}",
            "{\"spatialReference\":{\"wkid\":3346},\"features\":[{\"attributes\":{\"OBJECTID\":12}}]}", "{\"objectIds\":[12,13]}"));
        var context = new GeoportalContext(http, new GdalRunner(), Path.GetTempPath());
        var error = await Assert.ThrowsAsync<LandscapeInputException>(() => context.ReadLayerAsync(22, [500000, 6100000, 500200, 6100200], default));
        Assert.Equal("context_changed_during_download", error.Code);
    }

    [Fact]
    public async Task ContextDownloadKeepsCompleteEmptyObservationsDistinctFromFailure()
    {
        using var http = new HttpClient(new Responses("{\"objectIds\":null}", "{\"objectIds\":null}"));
        var context = new GeoportalContext(http, new GdalRunner(), Path.GetTempPath());
        Assert.Empty(await context.ReadLayerAsync(22, [500000, 6100000, 500200, 6100200], default));
    }

    private sealed class Responses(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> queue = new(responses);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(queue.Dequeue()) });
    }
}
