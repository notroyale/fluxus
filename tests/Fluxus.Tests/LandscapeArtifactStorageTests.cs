using Fluxus.Infrastructure.Landscape;
using Fluxus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fluxus.Tests;

public sealed class LandscapeArtifactStorageTests(TestCatalogue catalogue) : IClassFixture<TestCatalogue>
{
    [Fact]
    public void BudgetReservesWorkingSpaceAndNeverDeletesResultsToMeetQuota()
    {
        var root = Path.Combine(Path.GetTempPath(), "fluxus-budget-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "artifact"), [1, 2, 3, 4]);
            Assert.Equal(4, LandscapeArtifactStorage.CheckBudget(root, 10, 4));
            File.WriteAllBytes(Path.Combine(root, "artifact"), new byte[8]);
            var error = Assert.Throws<LandscapeInputException>(() => LandscapeArtifactStorage.CheckBudget(root, 10, 4));
            Assert.Equal("artifact_quota_exceeded", error.Code);
            Assert.Equal(8, new FileInfo(Path.Combine(root, "artifact")).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CleanupKeepsPublishedAnalysesAndActiveLeasesAndRemovesOnlyOldAbandonedOutputs()
    {
        using var scope = catalogue.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogueDb>(); var store = new LandscapeStore(db, TimeProvider.System);
        var parcels = await db.Parcels.Where(p => p.Geometry != null).OrderBy(p => p.Id).Select(p => p.Id).Take(2).ToArrayAsync();
        var a = new LandscapeAsset("/synthetic/ground.tif", new string('a', 64), 2);
        var bundle = new LandscapeBundle("Synthetic test", "TEST ONLY", "https://example.org/source", "https://example.org/licence",
            "Synthetic test", 2025, 2025, "synthetic metres", 3346, false, "Synthetic test", a, a, a,
            a with { Path = "/synthetic/context.geojson", ResolutionM = 0 }, a with { Path = "/synthetic/context.geojson", ResolutionM = 0 }, [200000,5900000,800000,6300000]);
        var dataset = await store.RegisterAsync(LandscapeJson.Write(bundle), new string('c', 64), default);
        await store.EnqueueAsync(dataset, parcels, default);
        var publishedClaim = (await store.ClaimAsync(default))!;
        var published = new LandscapeAnalysisEntity { Status = "partial" };
        Assert.True(await store.PublishAsync(publishedClaim, published, default));
        var active = (await store.ClaimAsync(default))!;
        var root = Path.Combine(Path.GetTempPath(), "fluxus-cleanup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string Old(string relative)
            {
                var path = Path.Combine(root, relative); Directory.CreateDirectory(path);
                var file = Path.Combine(path, "visibility.png"); File.WriteAllText(file, "synthetic test only");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3)); Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
                return path;
            }
            var good = Old(published.Id.ToString());
            var abandoned = Old(Guid.NewGuid().ToString());
            var arbitrary = Old("operator-notes");
            var activeStage = Old(Path.Combine(".staging", active.JobId.ToString(), active.LeaseToken.ToString()));
            var oldStage = Old(Path.Combine(".staging", Guid.NewGuid().ToString(), Guid.NewGuid().ToString()));
            var recent = Path.Combine(root, Guid.NewGuid().ToString()); Directory.CreateDirectory(recent);
            Assert.Equal(2, await LandscapeArtifactStorage.CleanupAbandonedAsync(db, root, DateTimeOffset.UtcNow, default));
            Assert.True(Directory.Exists(good)); Assert.True(Directory.Exists(activeStage));
            Assert.True(Directory.Exists(arbitrary)); Assert.True(Directory.Exists(recent));
            Assert.False(Directory.Exists(abandoned)); Assert.False(Directory.Exists(oldStage));
        }
        finally { Directory.Delete(root, true); }
    }
}
