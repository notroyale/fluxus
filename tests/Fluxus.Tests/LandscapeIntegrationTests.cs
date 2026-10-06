using System.Net;
using System.Text.Json;
using Fluxus.Core.Landscape;
using Fluxus.Infrastructure.Landscape;
using Fluxus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fluxus.Tests;

public sealed class LandscapeIntegrationTests(TestCatalogue catalogue) : IClassFixture<TestCatalogue>
{
    [Fact]
    public async Task JobsReuseCanonicalParcelsPublishOnceAndKeepLastGoodResults()
    {
        using var scope = catalogue.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogueDb>();
        var clock = new MutableClock();
        var store = new LandscapeStore(db, clock);
        var shares = await db.Items.Where(i => i.Auction.SourceId == "337928").ToArrayAsync();
        Assert.Equal(2, shares.Length); var parcelId = shares[0].ParcelId!.Value;
        var asset = new LandscapeAsset("/test-only/input.tif", new string('a', 64), 2);
        var vector = asset with { Path = "/test-only/buildings.geojson", ResolutionM = 0 };
        var bundle = new LandscapeBundle("Synthetic test fixture", "TEST ONLY", "https://example.org/source", "https://example.org/license",
            "Synthetic test fixture", 2025, 2025, "test-height-datum", 3346, false, "test fixture", asset, asset, asset, vector, vector,
            [200000, 5900000, 800000, 6300000]);
        var dataset = await store.RegisterAsync(LandscapeJson.Write(bundle), new string('a', 64), CancellationToken.None);
        Assert.Equal(dataset, await store.RegisterAsync(LandscapeJson.Write(bundle), new string('a', 64), CancellationToken.None));
        Assert.Equal(1, await store.EnqueueAsync(dataset, [parcelId], CancellationToken.None));
        Assert.Equal(0, await store.EnqueueAsync(dataset, [parcelId], CancellationToken.None));
        var claim = await store.ClaimAsync(CancellationToken.None); Assert.NotNull(claim);
        Assert.Null(await store.ClaimAsync(CancellationToken.None));
        Assert.True(claim.Geometry.EnvelopeInternal.MinX > 200000);
        Assert.False(await store.PublishAsync(claim with { LeaseToken = Guid.NewGuid() }, Analysis(82), CancellationToken.None));
        Assert.True(await store.PublishAsync(claim, Analysis(82), CancellationToken.None));
        Assert.False(await store.PublishAsync(claim, Analysis(82), CancellationToken.None));
        foreach (var share in shares)
        {
            using var detail = JsonDocument.Parse(await catalogue.Client.GetStringAsync($"/api/auction-parcels/{share.Id}/landscape"));
            Assert.Equal(82, detail.RootElement.GetProperty("summary").GetProperty("score").GetInt32());
            Assert.Equal(parcelId, detail.RootElement.GetProperty("parcelId").GetGuid());
        }
        using (var filtered = JsonDocument.Parse(await catalogue.Client.GetStringAsync("/api/catalog/map?minLandscapeScore=80&ownershipKind=partial&sort=landscape_desc")))
        {
            Assert.Equal(2, filtered.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal(2, filtered.RootElement.GetProperty("features").GetArrayLength());
        }
        foreach (var sort in new[] { "landscape_asc", "landscape_desc" })
        {
            using var sorted = JsonDocument.Parse(await catalogue.Client.GetStringAsync($"/api/catalog/map?sort={sort}"));
            var cards = sorted.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(82, cards[0].GetProperty("landscape").GetProperty("score").GetInt32());
            Assert.Equal(JsonValueKind.Null, cards[^1].GetProperty("landscape").GetProperty("score").ValueKind);
        }
        // A newer source queues a replacement but keeps the old immutable analysis.
        var replacement = await store.RegisterAsync(LandscapeJson.Write(bundle with { Release = "v2" }), new string('b', 64), CancellationToken.None);
        await store.EnqueueAsync(replacement, [parcelId], CancellationToken.None);
        using (var stale = JsonDocument.Parse(await catalogue.Client.GetStringAsync($"/api/auction-parcels/{shares[0].Id}/landscape")))
        {
            Assert.True(stale.RootElement.GetProperty("summary").GetProperty("isStale").GetBoolean());
            Assert.Equal(JsonValueKind.Null, stale.RootElement.GetProperty("summary").GetProperty("score").ValueKind);
        }
        var firstAttempt = await store.ClaimAsync(CancellationToken.None); Assert.NotNull(firstAttempt);
        clock.Advance(TimeSpan.FromMinutes(21));
        var reclaimed = await store.ClaimAsync(CancellationToken.None); Assert.NotNull(reclaimed);
        Assert.NotEqual(firstAttempt.LeaseToken, reclaimed.LeaseToken);
        Assert.False(await store.PublishAsync(firstAttempt, Analysis(50), CancellationToken.None));
        await store.FailAsync(reclaimed, "processing_timeout", true, CancellationToken.None);
        Assert.Null(await store.ClaimAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(5));
        var thirdAttempt = await store.ClaimAsync(CancellationToken.None); Assert.NotNull(thirdAttempt);
        await store.FailAsync(thirdAttempt, "processing_timeout", true, CancellationToken.None);
        Assert.Equal("failed", (await db.LandscapeJobs.AsNoTracking().SingleAsync(j => j.Id == thirdAttempt.JobId)).Status);
        var parcel = await db.Parcels.AsNoTracking().Include(p => p.CurrentLandscapeAnalysis).SingleAsync(p => p.Id == parcelId);
        Assert.Equal(82, parcel.CurrentLandscapeAnalysis!.Score); Assert.Null(parcel.LandscapeScore);
        Assert.Equal(1, await db.LandscapeAnalyses.CountAsync());
        using var scored = JsonDocument.Parse(await catalogue.Client.GetStringAsync("/api/catalog/map?minLandscapeScore=0"));
        Assert.Empty(scored.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task UnknownOffersAndInvalidLandscapeFiltersHaveExplicitResponses()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await catalogue.Client.GetAsync($"/api/auction-parcels/{Guid.NewGuid()}/landscape")).StatusCode);
        foreach (var invalid in new[] { "minLandscapeScore=-1", "minLandscapeScore=101", "minLandscapeScore=1.5", "landscapeStatus=invalid" })
            Assert.Equal(HttpStatusCode.BadRequest, (await catalogue.Client.GetAsync("/api/catalog/map?" + invalid)).StatusCode);
        using var scope = catalogue.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogueDb>();
        var id = await db.Items.Where(i => i.Parcel!.CurrentLandscapeAnalysisId == null).Select(i => i.Id).FirstAsync();
        using var unscored = JsonDocument.Parse(await catalogue.Client.GetStringAsync($"/api/auction-parcels/{id}/landscape"));
        Assert.Equal(JsonValueKind.Null, unscored.RootElement.GetProperty("summary").GetProperty("score").ValueKind);
        Assert.Equal("not_analyzed", unscored.RootElement.GetProperty("summary").GetProperty("status").GetString());
    }

    private static LandscapeAnalysisEntity Analysis(int score) => new()
    {
        Status = "ready", Score = score, Views = score, Terrain = score, Nature = score, Seclusion = score, Quality = "high",
        Metrics = LandscapeJson.Write(new LandscapeMetrics(12, .8, .6, 40, 8, .7, 100, 300, .03, 700, 3, 1000, .2, .1, 8, 16)),
        Limitations = "[\"test_only\"]"
    };
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
