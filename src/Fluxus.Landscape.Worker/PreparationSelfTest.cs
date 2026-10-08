using System.Security.Cryptography;
using Fluxus.Infrastructure.Landscape;

namespace Fluxus.Landscape.Worker;

public static class PreparationSelfTest
{
    public static async Task RunAsync(CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), "fluxus-preparation-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pdal = new PdalRunner(); var gdal = new GdalRunner(); var tiles = new List<LandscapeAsset>();
            for (var tile = 0; tile < 2; tile++)
            {
                var csv = Path.Combine(root, $"tile-{tile}.csv");
                await using (var writer = new StreamWriter(csv))
                {
                    await writer.WriteLineAsync("X,Y,Z,Classification,Withheld");
                    for (var row = 0; row < 100; row++)
                        for (var col = tile * 50; col < (tile + 1) * 50; col++)
                        {
                            if (row is >= 40 and < 60 && col is >= 40 and < 60) continue;
                            var x = 500001 + 2 * col; var y = 6100001 + 2 * row;
                            await writer.WriteLineAsync($"{x},{y},100,2,0");
                            if (row is >= 10 and < 20 && col is >= 10 and < 20)
                            {
                                await writer.WriteLineAsync($"{x},{y},150,6,0");
                                await writer.WriteLineAsync($"{x},{y},1000,7,0"); // Noise must be excluded.
                                await writer.WriteLineAsync($"{x},{y},900,6,1"); // Withheld obstruction must be excluded.
                            }
                        }
                }
                var las = Path.Combine(root, $"tile-{tile}.laz");
                var pipeline = Path.Combine(root, $"tile-{tile}.json");
                var lasWriter = new Dictionary<string, object> { ["type"] = "writers.las", ["filename"] = las,
                    ["minor_version"] = 4, ["dataformat_id"] = 6, ["compression"] = true };
                if (tile == 0) lasWriter["a_srs"] = "EPSG:3346";
                await File.WriteAllTextAsync(pipeline, LandscapeJson.Write(new { pipeline = new object[]
                {
                    new { type = "readers.text", filename = csv },
                    lasWriter
                } }), ct);
                await pdal.RunAsync(["pipeline", "--stream", pipeline], ct);
                await using var file = File.OpenRead(las);
                tiles.Add(new(las, Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)), 0));
            }
            var request = new LidarPreparationRequest(tiles.ToArray(), [500000, 6100000, 500200, 6100200],
                "SYNTHETIC metre heights", "SYNTHETIC CONTROLLED FIXTURE ONLY", "https://example.org/synthetic", "https://example.org/synthetic", 2025, 2025, DatumVerified: true);
            // Tiny synthetic fixtures use a 1 MiB reserve inside the test-only 128 MiB tmpfs.
            var preparer = new LidarPreparation(pdal, gdal, root, Path.Combine(root, "outputs"), 1024 * 1024);
            try { await preparer.PrepareAsync(request, "missing-crs", ct); throw new InvalidOperationException("Missing CRS accepted without evidence"); }
            catch (LandscapeInputException error) when (error.Code == "lidar_crs_missing") { }
            var declaration = Path.Combine(root, "provider.prj");
            await File.WriteAllTextAsync(declaration, await gdal.RunAsync("gdalsrsinfo", ["-o", "wkt1", "EPSG:3346"], ct), ct);
            await using (var file = File.OpenRead(declaration))
                request = request with { HorizontalCrsDeclaration = new(declaration, Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)), 0),
                    HorizontalCrsEvidence = "SYNTHETIC controlled missing-CRS companion fixture" };
            var result = await preparer.PrepareAsync(request, "fixture", ct);
            async Task<AsciiGrid> Grid(string name)
            {
                var ascii = Path.Combine(root, name + ".asc");
                await gdal.RunAsync("gdal_translate", ["-q", "-of", "AAIGrid", Path.Combine(result, name + ".tif"), ascii], ct);
                return await AsciiGrid.ReadAsync(ascii, ct);
            }
            var ground = await Grid("ground"); var surface = await Grid("surface");
            Require(ground.Width == 100 && ground.Height == 100 && ground.CellSize == 2, "Aligned 2 m raster dimensions");
            Require(ground.Values[ground.Index(500025, 6100025)] == 100, "Buildings must not raise the ground");
            Require(surface.Values[surface.Index(500025, 6100025)] == 150, "Building surface preserved; noise/withheld excluded");
            Require(!ground.Valid(ground.Index(500101, 6100101)) && !surface.Valid(surface.Index(500101, 6100101)), "Survey hole remains NoData");
            Require(ground.Valid(ground.Index(500099, 6100161)) && ground.Valid(ground.Index(500101, 6100161)), "Merged tile seam remains continuous");
            try { await preparer.PrepareAsync(request with { Tiles = [tiles[0] with { Sha256 = new string('0', 64) }] }, "bad-hash", ct); throw new InvalidOperationException("Checksum mismatch accepted"); }
            catch (LandscapeInputException error) when (error.Code == "source_checksum_mismatch") { }
            try { await preparer.PrepareAsync(request, "fixture", ct); throw new InvalidOperationException("Existing output overwritten"); }
            catch (LandscapeInputException error) when (error.Code == "preparation_output_exists") { }
            Console.WriteLine(await pdal.RunAsync(["--version"], ct));
            Console.WriteLine("Preparation self-test passed: classified LAZ, declared missing CRS, two-tile seam, building, noise/withheld exclusion, NoData, checksums and immutable output. No catalogue database was accessed.");
        }
        catch (LandscapeInputException error)
        {
            // This command contains only owned synthetic inputs, so tool diagnostics are safe here.
            Console.Error.WriteLine(error.Data["toolError"]);
            throw;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
