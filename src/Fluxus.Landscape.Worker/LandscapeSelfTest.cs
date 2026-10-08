using System.Globalization;
using System.Security.Cryptography;
using Fluxus.Infrastructure.Landscape;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Fluxus.Landscape.Worker;

// Deliberately isolated from the catalogue: these synthetic measurements are never published.
public static class LandscapeSelfTest
{
    public static async Task RunAsync(CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), "fluxus-landscape-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var gdal = new GdalRunner();
        try
        {
            Console.WriteLine(await gdal.RunAsync("gdalinfo", ["--version"], ct));
            var ground = await RasterAsync("ground", 2, (_, _) => 100, root, gdal, ct);
            var cover = await RasterAsync("cover", 5, (_, _) => 2, root, gdal, ct);
            var vectors = Path.Combine(root, "context.geojson");
            await File.WriteAllTextAsync(vectors, "{\"type\":\"FeatureCollection\",\"features\":[]}", ct);
            var vector = await AssetAsync(vectors, 0, ct);
            var polygon = (MultiPolygon)new WKTReader().Read("MULTIPOLYGON (((499970 6099970, 500030 6099970, 500030 6100030, 499970 6100030, 499970 6099970)))");
            var results = new Dictionary<string, double>();
            foreach (var scenario in new[] { "flat", "ridge", "canopy", "valley", "building-screen", "nodata", "cover-gap" })
            {
                var scenarioGround = scenario == "valley"
                    ? await RasterAsync("valley-ground", 2, (x, _) => 100 + Math.Min(80, Math.Abs(x - 500000) / 20), root, gdal, ct) : ground;
                var surface = await RasterAsync(scenario, 5, (x, y) => scenario switch
                {
                    "ridge" when x is > 500500 and < 500550 => 180,
                    "canopy" when Math.Abs(x - 500000) < 100 && Math.Abs(y - 6100000) < 100 => 120,
                    "valley" => 100 + Math.Min(80, Math.Abs(x - 500000) / 20),
                    "building-screen" when x is > 500150 and < 500180 && Math.Abs(y - 6100000) < 200 => 150,
                    "nodata" when Math.Abs(x - 500800) < 20 && Math.Abs(y - 6100000) < 20 => -9999,
                    _ => 100
                }, root, gdal, ct);
                var bundle = new LandscapeBundle("SYNTHETIC TEST ONLY", scenario, "https://example.org/synthetic", "https://example.org/synthetic",
                    "SYNTHETIC TEST ONLY — never a live parcel assessment", 2025, 2025, "synthetic metres", 3346, false,
                    "Deterministic controlled terrain fixture", scenarioGround, surface, cover, vector, vector,
                    [496900, 6096900, 503100, 6103100]);
                if (scenario == "cover-gap") bundle = bundle with { Cover = await RasterAsync("cover-gap-input", 5,
                    (x, y) => Math.Abs(x - 500800) < 20 && Math.Abs(y - 6100000) < 20 ? -9999 : 2, root, gdal, ct) };
                var claim = new LandscapeClaim(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "synthetic", Guid.NewGuid(), bundle, polygon);
                var analysis = await new LandscapeProcessor(gdal, root, Path.Combine(root, "artifacts")).ProcessAsync(claim, ct);
                var metrics = LandscapeJson.Read<Fluxus.Core.Landscape.LandscapeMetrics>(analysis.Metrics);
                if (scenario == "nodata")
                {
                    Require(analysis.Score is null && analysis.Status == "partial" && metrics.ValidViewpoints == 0, "NoData must withhold score");
                }
                else if (scenario == "cover-gap")
                {
                    Require(analysis.Status == "partial" && analysis.Score is null && analysis.Views is null && analysis.Nature is null,
                        "Incomplete cover must withhold dependent components and score");
                    Require(metrics.VisibleAreaKm2 > 27 && metrics.ValidViewpoints == 16 && analysis.Terrain.HasValue && analysis.Seclusion.HasValue && analysis.VisibilityArtifact is not null,
                        "Verified visibility, terrain and seclusion must survive incomplete cover");
                }
                else
                {
                    Require(analysis.Status == "ready" && metrics.VisibleAreaKm2.HasValue && metrics.ValidViewpoints == 16, "Complete fixtures must produce sampled analyses");
                    Require(analysis.VisibilityArtifact is not null && File.Exists(Path.Combine(root, "artifacts", analysis.VisibilityArtifact)), "Visibility image must be published");
                    results[scenario] = metrics.VisibleAreaKm2!.Value;
                    if (scenario == "flat") Require(results[scenario] > 27 && metrics.ReliefP90P10M == 0, "Flat unobstructed view");
                    else if (scenario == "ridge") Require(results[scenario] < results["flat"] * .85, "Ridge must block distant views");
                    else if (scenario == "canopy") Require(results[scenario] < results["flat"] * .1, "Ground-level canopy observer must not see from treetop");
                    else if (scenario == "valley") Require(metrics.ReliefP90P10M > 20 && results[scenario] < results["flat"] * .95, "Valley relief and screening");
                    else Require(results[scenario] < results["flat"] * .95, "Building screen must obstruct views");
                }
                Console.WriteLine(LandscapeJson.Write(new { scenario, analysis.Status, analysis.Score, metrics.VisibleAreaKm2, metrics.ValidViewpoints }));
            }
            Console.WriteLine("Landscape GDAL self-test passed. No catalogue database was accessed.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task<LandscapeAsset> RasterAsync(string name, double cell, Func<double, double, double> value,
        string root, GdalRunner gdal, CancellationToken ct)
    {
        var width = (int)(6200 / cell);
        var ascii = Path.Combine(root, name + ".asc");
        await using (var writer = new StreamWriter(ascii))
        {
            await writer.WriteLineAsync($"ncols {width}\nnrows {width}\nxllcorner 496900\nyllcorner 6096900\ncellsize {cell}\nNODATA_value -9999");
            for (var row = 0; row < width; row++)
            {
                ct.ThrowIfCancellationRequested();
                var y = 6103100 - (row + .5) * cell;
                await writer.WriteLineAsync(string.Join(' ', Enumerable.Range(0, width)
                    .Select(col => value(496900 + (col + .5) * cell, y).ToString(CultureInfo.InvariantCulture))));
            }
        }
        var raster = Path.Combine(root, name + ".tif");
        await gdal.RunAsync("gdal_translate", ["-q", "-a_srs", "EPSG:3346", "-of", "GTiff", "-co", "COMPRESS=DEFLATE", ascii, raster], ct);
        File.Delete(ascii);
        return await AssetAsync(raster, cell, ct);
    }
    private static async Task<LandscapeAsset> AssetAsync(string path, double cell, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return new(path, Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)), cell);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
