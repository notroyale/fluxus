using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fluxus.Infrastructure.Landscape;

public sealed record LidarPreparationRequest(LandscapeAsset[] Tiles, double[] Bounds, string VerticalDatum,
    string DatumEvidence, string SourceUrl, string LicenseUrl, int SurveyYearFrom, int SurveyYearTo,
    LandscapeAsset? HorizontalCrsDeclaration = null, string? HorizontalCrsEvidence = null,
    bool DatumVerified = false, bool PreviewOnly = false);

// Raw preparation is deliberately separate from registering a scoring bundle: cover and
// context still need verified provenance, coverage and a compatible source release.
public sealed class LidarPreparation(PdalRunner pdal, GdalRunner gdal, string inputRoot, string outputRoot,
    long minimumFreeBytes = 2L * 1024 * 1024 * 1024)
{
    public static void Validate(LidarPreparationRequest request)
    {
        ValidateBounds(request.Bounds);
        if (request.Tiles is not { Length: > 0 and <= 64 } || request.Tiles.Any(t =>
                t is null || string.IsNullOrWhiteSpace(t.Path) || t.Sha256 is not { Length: 64 } || t.Sha256.Any(c => !Uri.IsHexDigit(c)) ||
                Path.GetExtension(t.Path).ToLowerInvariant() is not (".las" or ".laz")) ||
            string.IsNullOrWhiteSpace(request.VerticalDatum) || string.IsNullOrWhiteSpace(request.DatumEvidence) ||
            !Https(request.SourceUrl) || !Https(request.LicenseUrl) || request.SurveyYearFrom < 2000 ||
            request.SurveyYearTo < request.SurveyYearFrom || request.SurveyYearTo > DateTime.UtcNow.Year)
            throw new LandscapeInputException("invalid_lidar_preparation_request");
        if (request.Tiles.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Tiles.Length)
            throw new LandscapeInputException("duplicate_lidar_tile");
        if (!request.DatumVerified && !request.PreviewOnly)
            throw new LandscapeInputException("height_datum_unverified");
        if (request.HorizontalCrsDeclaration is { } declaration &&
            (Path.GetExtension(declaration.Path).ToLowerInvariant() != ".prj" ||
             declaration.Sha256 is not { Length: 64 } || declaration.Sha256.Any(c => !Uri.IsHexDigit(c)) ||
             string.IsNullOrWhiteSpace(request.HorizontalCrsEvidence)))
            throw new LandscapeInputException("invalid_horizontal_crs_evidence");
    }

    public static void ValidateBounds(double[] bounds)
    {
        if (bounds is not { Length: 4 } || bounds.Any(x => !double.IsFinite(x)) ||
            bounds[0] < 200000 || bounds[2] > 800000 || bounds[1] < 5900000 || bounds[3] > 6300000 ||
            bounds[0] >= bounds[2] || bounds[1] >= bounds[3] ||
            (Math.Ceiling(bounds[2] / 2) - Math.Floor(bounds[0] / 2)) *
            (Math.Ceiling(bounds[3] / 2) - Math.Floor(bounds[1] / 2)) > 25_000_000)
            throw new LandscapeInputException("preparation_bounds_budget_exceeded");
    }

    public async Task<string> PrepareAsync(LidarPreparationRequest request, string destination, CancellationToken ct)
    {
        Validate(request);
        var output = LandscapeBundleReader.ConfinedPath(outputRoot, destination);
        if (Directory.Exists(output) || File.Exists(output)) throw new LandscapeInputException("preparation_output_exists");
        Directory.CreateDirectory(outputRoot);
        var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(outputRoot))! : Path.GetFullPath(outputRoot));
        if (minimumFreeBytes < 0) throw new ArgumentOutOfRangeException(nameof(minimumFreeBytes));
        if (drive.AvailableFreeSpace < minimumFreeBytes) throw new LandscapeInputException("preparation_disk_budget_exceeded");
        var declaredCrs = false;
        if (request.HorizontalCrsDeclaration is { } declaration)
        {
            var declarationPath = LandscapeBundleReader.ConfinedPath(inputRoot, declaration.Path);
            if (new FileInfo(declarationPath).Length > 64 * 1024) throw new LandscapeInputException("crs_declaration_budget_exceeded");
            await using var declarationFile = File.OpenRead(declarationPath);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(declarationFile, ct));
            if (!hash.Equals(declaration.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new LandscapeInputException("source_checksum_mismatch");
            var epsg = (await gdal.RunAsync("gdalsrsinfo", ["-o", "epsg", declarationPath], ct)).Trim();
            if (epsg != "EPSG:3346") throw new LandscapeInputException("lidar_crs_mismatch");
            declaredCrs = true;
        }
        var tiles = new List<object>(); var resolved = new List<(string Path, bool DefaultCrs)>(); long bytes = 0, points = 0;
        foreach (var tile in request.Tiles)
        {
            var path = LandscapeBundleReader.ConfinedPath(inputRoot, tile.Path);
            bytes += new FileInfo(path).Length;
            if (bytes > 8L * 1024 * 1024 * 1024) throw new LandscapeInputException("lidar_input_budget_exceeded");
            await using var file = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            if (!hash.Equals(tile.Sha256, StringComparison.OrdinalIgnoreCase)) throw new LandscapeInputException("source_checksum_mismatch");
            using var info = JsonDocument.Parse(await pdal.RunAsync(["info", "--summary", path], ct));
            using var metadata = JsonDocument.Parse(await pdal.RunAsync(["info", "--metadata", path], ct));
            var summary = info.RootElement.GetProperty("summary");
            var srs = summary.TryGetProperty("srs", out var reference) && reference.TryGetProperty("wkt", out var wkt)
                ? wkt.GetString() ?? "" : "";
            var useDeclaredCrs = RequiresDeclaredCrs(srs, declaredCrs);
            var count = summary.GetProperty("num_points").GetInt64();
            points += count;
            if (count <= 0 || points > 800_000_000) throw new LandscapeInputException("lidar_point_budget_exceeded");
            // A horizontal LAS CRS alone cannot prove a height datum; preserve supplied audit evidence.
            tiles.Add(new { path = tile.Path, sha256 = hash, bytes = file.Length, points = count,
                horizontalCrsFromDeclaration = useDeclaredCrs,
                summary = info.RootElement.Clone(), metadata = metadata.RootElement.Clone() });
            resolved.Add((path, useDeclaredCrs));
        }
        var stage = Path.Combine(Path.GetFullPath(outputRoot), ".preparation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var bounds = new[] { Math.Floor(request.Bounds[0] / 2) * 2, Math.Floor(request.Bounds[1] / 2) * 2,
                Math.Ceiling(request.Bounds[2] / 2) * 2, Math.Ceiling(request.Bounds[3] / 2) * 2 };
            foreach (var name in new[] { "ground", "surface" })
            {
                var stages = new List<object>();
                for (var i = 0; i < resolved.Count; i++)
                {
                    var reader = new Dictionary<string, object> { ["type"] = "readers.las", ["filename"] = resolved[i].Path,
                        ["tag"] = "tile" + i, ["threads"] = 2 };
                    if (resolved[i].DefaultCrs) reader["default_srs"] = "EPSG:3346";
                    stages.Add(reader);
                }
                stages.Add(new { type = "filters.merge", inputs = Enumerable.Range(0, resolved.Count).Select(i => "tile" + i).ToArray() });
                stages.Add(new { type = "filters.crop", bounds = PdalBounds(bounds) });
                stages.Add(new { type = "filters.expression", expression = name == "ground"
                    ? "Classification == 2 && Withheld == 0" : "Classification >= 2 && Classification <= 6 && Withheld == 0" });
                stages.Add(new { type = "writers.gdal", filename = Path.Combine(stage, name + ".tif"),
                    resolution = 2, radius = 2 * Math.Sqrt(2), origin_x = bounds[0], origin_y = bounds[1],
                    width = (int)((bounds[2] - bounds[0]) / 2), height = (int)((bounds[3] - bounds[1]) / 2),
                    output_type = name == "ground" ? "idw" : "max", window_size = 0, nodata = -9999,
                    data_type = "float", gdalopts = "COMPRESS=DEFLATE,TILED=YES" });
                var pipeline = Path.Combine(stage, name + "-pipeline.json");
                await File.WriteAllTextAsync(pipeline, LandscapeJson.Write(new { pipeline = stages }), ct);
                await pdal.RunAsync(["pipeline", "--stream", pipeline], ct);
            }
            var assets = new Dictionary<string, LandscapeAsset>();
            foreach (var name in new[] { "ground", "surface" })
            {
                var path = Path.Combine(stage, name + ".tif");
                using var info = JsonDocument.Parse(await gdal.RunAsync("gdalinfo", ["-json", path], ct));
                if (info.RootElement.GetProperty("bands").GetArrayLength() != 1) throw new LandscapeInputException("prepared_raster_invalid");
                await using var file = File.OpenRead(path);
                assets[name] = new(name + ".tif", Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)), 2);
            }
            var report = new { status = request.PreviewOnly ? "terrain_preview_not_verified_not_registered" : "terrain_prepared_not_registered", epsg = 3346, bounds, request.VerticalDatum,
                request.DatumEvidence, request.SourceUrl, request.LicenseUrl, request.SurveyYearFrom, request.SurveyYearTo,
                request.PreviewOnly, request.DatumVerified, request.HorizontalCrsDeclaration, request.HorizontalCrsEvidence,
                pdalVersion = (await pdal.RunAsync(["--version"], ct)).Trim(),
                gdalVersion = (await gdal.RunAsync("gdalinfo", ["--version"], ct)).Trim(), tiles, assets,
                preparation = "Streamed classified LAS/LAZ, ground class 2 IDW; surface classes 2–6 maximum; withheld/noise/unclassified excluded. 2 m grid, radius 2√2 m; no fallback gap filling. Tile edges merged before rasterization. Horizontal CRS may use a checksum-verified provider declaration only when absent from a tile. Height verification is recorded separately in DatumVerified; EPSG:3346 does not establish a vertical datum." };
            await File.WriteAllTextAsync(Path.Combine(stage, "terrain-preparation.json"), LandscapeJson.Write(report), ct);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            Directory.Move(stage, output);
            return output;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public static bool RequiresDeclaredCrs(string embeddedWkt, bool verifiedDeclaration)
    {
        if (string.IsNullOrWhiteSpace(embeddedWkt))
        {
            if (!verifiedDeclaration) throw new LandscapeInputException("lidar_crs_missing");
            return true;
        }
        if (!(embeddedWkt.Contains("AUTHORITY[\"EPSG\",\"3346\"]") || embeddedWkt.Contains("ID[\"EPSG\",3346]")))
            throw new LandscapeInputException("lidar_crs_mismatch");
        return false; // A provider declaration never overrides conflicting embedded coordinates.
    }

    private static bool Https(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https";
    private static string PdalBounds(double[] b) => FormattableString.Invariant($"([{b[0]:G17},{b[2]:G17}],[{b[1]:G17},{b[3]:G17}])");
}
