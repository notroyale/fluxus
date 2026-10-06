using Fluxus.Core.Landscape;
using Fluxus.Infrastructure.Landscape;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Fluxus.Tests;

public sealed class LandscapeTests
{
    private static LandscapeMetrics Complete => new(12, .8, .6, 40, 8, .7, 100, 300, .03, 700, 3, 1000, .2, .1, 8, 16);

    [Fact]
    public void MissingViewsDoesNotRedistributeWeightsOrCreateAnOverallScore()
    {
        var scores = LandscapeScore.Calculate(Complete with { VisibleAreaKm2 = null });
        Assert.Null(scores.Views); Assert.Null(scores.Overall);
        Assert.NotNull(scores.Terrain); Assert.NotNull(scores.Nature); Assert.NotNull(scores.Seclusion);
    }

    [Fact]
    public void BetterPhysicalMetricsImproveTheirComponentsWithoutChangingOthers()
    {
        var original = LandscapeScore.Calculate(Complete);
        var moreViews = LandscapeScore.Calculate(Complete with { VisibleAreaKm2 = 20, DistantDirectionFraction = 1 });
        Assert.True(moreViews.Views > original.Views); Assert.True(moreViews.Overall > original.Overall);
        Assert.Equal(original.Nature, moreViews.Nature); Assert.Equal(original.Seclusion, moreViews.Seclusion);
        var moreSecluded = LandscapeScore.Calculate(Complete with { BuildingsWithin1Km = 0, NearestBuildingDistanceM = 1000, MajorRoadDistanceM = 1500 });
        Assert.True(moreSecluded.Seclusion > original.Seclusion);
        Assert.Equal(original.Views, moreSecluded.Views);
        Assert.Equal(original, LandscapeScore.Calculate(Complete with { BestVisibleAreaKm2 = 27 }));
    }

    [Fact]
    public void ScoresSaturateAndRejectInvalidMetrics()
    {
        var scores = LandscapeScore.Calculate(Complete with { VisibleAreaKm2 = 100, DistantDirectionFraction = 1, VisibleNaturalDiversity = 1 });
        Assert.Equal(100, scores.Views);
        Assert.Throws<ArgumentException>(() => LandscapeScore.Calculate(Complete with { WaterDistanceM = double.NaN }));
        Assert.Throws<ArgumentException>(() => LandscapeScore.Calculate(Complete with { NaturalCoverFraction = 1.1 }));
        Assert.Throws<ArgumentException>(() => LandscapeScore.Calculate(Complete with { BuildingsWithin1Km = -1 }));
        Assert.Throws<ArgumentException>(() => LandscapeScore.Calculate(Complete with { LargestGentlePatchHa = 1 }));
    }

    [Theory]
    [InlineData(2025, 8, false, "high")]
    [InlineData(2020, 8, false, "medium")]
    [InlineData(2015, 8, false, "low")]
    [InlineData(2025, 1, false, "low")]
    [InlineData(2025, 8, true, "low")]
    [InlineData(2025, 0, false, "unknown")]
    public void QualityDescribesSourceAgeAndSampling(int year, int points, bool substituted, string quality) =>
        Assert.Equal(quality, LandscapeScore.Quality(year, points, substituted, 2026));

    [Fact]
    public void SamplesStayInsideMultipartGeometryAndOutsideHoles()
    {
        var polygon = (MultiPolygon)new WKTReader().Read("MULTIPOLYGON (((0 0, 200 0, 200 200, 0 200, 0 0), (50 50, 150 50, 150 150, 50 150, 50 50)), ((400 0, 450 0, 450 50, 400 50, 400 0)))");
        var points = LandscapeSampling.Points(polygon);
        Assert.Equal(16, points.Length);
        Assert.Equal(points, LandscapeSampling.Points(polygon));
        Assert.All(points, p => Assert.True(polygon.Covers(polygon.Factory.CreatePoint(p))));
        Assert.Contains(points, p => p.X > 400);
        foreach (var point in points) Assert.All(points.Where(p => p != point), other => Assert.True(point.Distance(other) >= 10));
    }

    [Fact]
    public void NoDataAndOutOfBoundsCannotCountAsCompleteVisibilityInputs()
    {
        var grid = new AsciiGrid(20, 20, 0, 0, 5, -9999, Enumerable.Repeat(0d, 400).ToArray());
        Assert.True(grid.CompleteDisk(50, 50, 40)); // Zero elevation is valid.
        grid.Values[grid.Index(50, 50)] = grid.NoData;
        Assert.False(grid.CompleteDisk(50, 50, 40));
        Assert.False(grid.CompleteDisk(10, 10, 40));
        Assert.Equal(-1, grid.Index(-5, 10));
    }

    [Fact]
    public void GentleGroundRespectsParcelHolesAndMissingSlopeNeighbours()
    {
        var grid = new AsciiGrid(30, 30, 0, 0, 2, -9999, Enumerable.Repeat(100d, 900).ToArray());
        var polygon = (MultiPolygon)new WKTReader().Read("MULTIPOLYGON (((10 10, 50 10, 50 50, 10 50, 10 10), (20 20, 40 20, 40 40, 20 40, 20 20)))");
        var result = LandscapeProcessor.GentleGround(grid, polygon);
        Assert.Equal(.12, result.TotalHa!.Value, 8); Assert.Equal(result.TotalHa, result.LargestHa);
        grid.Values[grid.Index(12, 12)] = -9999;
        Assert.Null(LandscapeProcessor.GentleGround(grid, polygon).TotalHa);
    }

    [Fact]
    public void BundlePathsCannotEscapeTheConfiguredRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "fluxus-test-inputs");
        Assert.Throws<LandscapeInputException>(() => LandscapeBundleReader.ConfinedPath(root, "../elsewhere.tif"));
    }
}
