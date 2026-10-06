using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Fluxus.Core.Landscape;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Fluxus.Infrastructure.Landscape;

public sealed record LandscapeViewpoint(double X, double Y, double EyeHeightM, double VisibleAreaKm2,
    double DistantDirectionFraction, double NaturalDiversity, double VisibleWaterFraction);

public sealed class LandscapeProcessor(GdalRunner gdal, string inputRoot, string artifactRoot)
{
    private const double Radius = 3000;
    private static string N(double x) => x.ToString("G17", CultureInfo.InvariantCulture);

    public async Task<LandscapeAnalysisEntity> ProcessAsync(LandscapeClaim claim, CancellationToken ct)
    {
        var b = claim.Bundle;
        await LandscapeBundleReader.VerifyAsync(b, inputRoot, ct);
        foreach (var asset in new[] { b.Ground, b.Surface, b.Cover }) await ValidateRasterAsync(asset, ct);
        var polygon = claim.Geometry;
        var bounds = polygon.EnvelopeInternal;
        if (bounds.Width > 3000 || bounds.Height > 3000) throw new LandscapeInputException("parcel_budget_exceeded");
        var context = b.ContextBounds;
        if (bounds.MinX - 1500 < context[0] || bounds.MinY - 1500 < context[1] ||
            bounds.MaxX + 1500 > context[2] || bounds.MaxY + 1500 > context[3])
            throw new LandscapeInputException("context_coverage_incomplete");
        var staging = Path.Combine(artifactRoot, ".staging", claim.JobId.ToString(), claim.LeaseToken.ToString());
        Directory.CreateDirectory(staging);
        try
        {
            var expanded = new Envelope(bounds); expanded.ExpandBy(Radius + 20);
            var ground = await GridAsync(b.Ground.Path, "ground", expanded, 5, "bilinear", staging, ct);
            var surface = await GridAsync(b.Surface.Path, "surface", expanded, 5, "max", staging, ct);
            var cover = await GridAsync(b.Cover.Path, "cover", expanded, 5, "near", staging, ct);
            if (ground.Width != surface.Width || ground.Height != surface.Height || ground.MinX != surface.MinX ||
                ground.MinY != surface.MinY || ground.Width != cover.Width || ground.MinX != cover.MinX || ground.MinY != cover.MinY || ground.Height != cover.Height)
                throw new LandscapeInputException("grids_not_aligned");
            var observer = polygon.PointOnSurface.Coordinate;
            var buildings = await VectorAsync(b.Buildings.Path, ct);
            var roads = await VectorAsync(b.MajorRoads.Path, ct);
            var observerPoint = polygon.Factory.CreatePoint(observer);
            var nearestBuilding = buildings.Count == 0 ? 1000 : Math.Min(1000, buildings.Min(g => g.Distance(observerPoint)));
            var buildingCount = buildings.Count(g => g.Distance(observerPoint) <= 1000);
            var nearestRoad = roads.Count == 0 ? 1500 : Math.Min(1500, roads.Min(g => g.Distance(observerPoint)));
            var nature = Nature(cover, observer);
            var terrain = Terrain(ground, observer);
            var parcelBounds = new Envelope(bounds); parcelBounds.ExpandBy(10);
            var fine = await GridAsync(b.Ground.Path, "parcel-ground", parcelBounds, 2, "bilinear", staging, ct);
            var gentle = GentleGround(fine, polygon);
            var viewpoints = new List<(LandscapeViewpoint Metrics, string Raster)>();
            var limitations = new List<string> { "preference_profile_uncalibrated", "access_rights_unverified", "buildability_unverified", "single_season_surface" };
            foreach (var point in LandscapeSampling.Points(polygon))
            {
                ct.ThrowIfCancellationRequested();
                if (!ground.CompleteDisk(point.X, point.Y, Radius + 5) || !surface.CompleteDisk(point.X, point.Y, Radius + 5)) continue;
                var index = ground.Index(point.X, point.Y);
                var raster = Path.Combine(staging, $"view-{viewpoints.Count}.tif");
                // Ground-relative eye height, even if the observer cell contains a tree/building.
                var height = ground.Values[index] + 1.7 - surface.Values[index];
                await gdal.RunAsync("gdal_viewshed", ["-q", "-ox", N(point.X), "-oy", N(point.Y),
                    "-oz", N(height), "-tz", "0", "-md", N(Radius), "-vv", "255", "-iv", "0",
                    "-ov", "0", "-a_nodata", "0", "-cc", "0.85714", Path.Combine(staging, "surface.tif"), raster], ct);
                var ascii = Path.Combine(staging, $"view-{viewpoints.Count}.asc");
                await gdal.RunAsync("gdal_translate", ["-q", "-of", "AAIGrid", raster, ascii], ct);
                var visible = await AsciiGrid.ReadAsync(ascii, ct);
                viewpoints.Add((View(visible, cover, point), raster));
            }
            var ordered = viewpoints.OrderBy(v => v.Metrics.VisibleAreaKm2).ToArray();
            var sufficient = ordered.Length >= 3 || polygon.Area < 400 && ordered.Length > 0;
            if (!sufficient) limitations.Add("insufficient_complete_viewpoints");
            if (ordered.Length < 3 && ordered.Length > 0) limitations.Add("sparse_viewpoint_sampling");
            if (b.Substituted) limitations.Add("source_substitution");
            double? Median(Func<LandscapeViewpoint, double> select) => sufficient ? MedianValue(ordered.Select(v => select(v.Metrics))) : null;
            var metrics = new LandscapeMetrics(Median(v => v.VisibleAreaKm2), Median(v => v.DistantDirectionFraction),
                Median(v => v.NaturalDiversity), terrain.Relief, terrain.Tpi, nature.Fraction,
                nature.Forest, nature.Water, Median(v => v.VisibleWaterFraction), nearestBuilding, buildingCount,
                nearestRoad, gentle.TotalHa, gentle.LargestHa, ordered.Length, ordered.Length > 0 ? ordered[^1].Metrics.VisibleAreaKm2 : null);
            if (nature.Fraction is null) limitations.Add("land_cover_incomplete");
            if (terrain.Relief is null) limitations.Add("ground_context_incomplete");
            if (gentle.TotalHa is null) limitations.Add("parcel_ground_incomplete");
            var scores = LandscapeScore.Calculate(metrics);
            var analysis = new LandscapeAnalysisEntity
            {
                Status = scores.Overall.HasValue ? "ready" : "partial", Score = scores.Overall,
                Views = scores.Views, Terrain = scores.Terrain, Nature = scores.Nature, Seclusion = scores.Seclusion,
                Metrics = LandscapeJson.Write(metrics), Viewpoints = LandscapeJson.Write(ordered.Select(v => v.Metrics)),
                Limitations = LandscapeJson.Write(limitations),
                Quality = scores.Overall.HasValue ? LandscapeScore.Quality(b.SurveyYearFrom, ordered.Length, b.Substituted, DateTime.UtcNow.Year) : "unknown"
            };
            // Publish a ground-referenced, obstruction-aware estimate from the point nearest the median view.
            if (scores.Overall.HasValue)
                await PublishImageAsync(analysis, ordered.OrderBy(v => Math.Abs(v.Metrics.VisibleAreaKm2 - metrics.VisibleAreaKm2!.Value)).First().Raster, staging, ct);
            return analysis;
        }
        finally
        {
            // Only delete this job's staging path, never an input or published artifact directory.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private async Task ValidateRasterAsync(LandscapeAsset asset, CancellationToken ct)
    {
        using var info = JsonDocument.Parse(await gdal.RunAsync("gdalinfo", ["-json", asset.Path], ct));
        var root = info.RootElement;
        var wkt = root.GetProperty("coordinateSystem").GetProperty("wkt").GetString() ?? "";
        var transform = root.GetProperty("geoTransform").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        if (!(wkt.Contains("ID[\"EPSG\",3346]") || wkt.Contains("AUTHORITY[\"EPSG\",\"3346\"]")) ||
            transform.Length != 6 || transform[2] != 0 || transform[4] != 0 ||
            Math.Abs(transform[1]) > asset.ResolutionM * 1.001 || Math.Abs(transform[5]) > asset.ResolutionM * 1.001 ||
            root.GetProperty("bands").GetArrayLength() != 1)
            throw new LandscapeInputException("raster_metadata_mismatch");
    }

    private async Task<AsciiGrid> GridAsync(string input, string name, Envelope bounds, double cell,
        string resampling, string staging, CancellationToken ct)
    {
        if ((bounds.Width / cell + 2) * (bounds.Height / cell + 2) > 4_000_000)
            throw new LandscapeInputException("grid_budget_exceeded");
        var raster = Path.Combine(staging, name + ".tif");
        await gdal.RunAsync("gdalwarp", ["-q", "-overwrite", "-t_srs", "EPSG:3346", "-te",
            N(bounds.MinX), N(bounds.MinY), N(bounds.MaxX), N(bounds.MaxY), "-tr", N(cell), N(cell),
            "-tap", "-r", resampling, "-ot", "Float32", "-dstnodata", "-9999", "-wm", "128", input, raster], ct);
        var ascii = Path.Combine(staging, name + ".asc");
        await gdal.RunAsync("gdal_translate", ["-q", "-of", "AAIGrid", raster, ascii], ct);
        var grid = await AsciiGrid.ReadAsync(ascii, ct);
        // A permissive raster resampler can interpolate across missing input pixels. Preserve
        // an independently resampled validity mask: any contributing NoData pixel invalidates
        // the normalized cell, including when reducing a finer ground/surface source.
        var maskVrt = Path.Combine(staging, name + "-mask.vrt");
        await gdal.RunAsync("gdal_translate", ["-q", "-of", "VRT", "-b", "mask,1", "-a_nodata", "none", input, maskVrt], ct);
        var maskRaster = Path.Combine(staging, name + "-mask.tif");
        await gdal.RunAsync("gdalwarp", ["-q", "-overwrite", "-t_srs", "EPSG:3346", "-te",
            N(bounds.MinX), N(bounds.MinY), N(bounds.MaxX), N(bounds.MaxY), "-tr", N(cell), N(cell),
            "-tap", "-r", "min", "-ot", "Float32", "-dstnodata", "-9999", "-wm", "128", maskVrt, maskRaster], ct);
        var maskAscii = Path.Combine(staging, name + "-mask.asc");
        await gdal.RunAsync("gdal_translate", ["-q", "-of", "AAIGrid", maskRaster, maskAscii], ct);
        var mask = await AsciiGrid.ReadAsync(maskAscii, ct);
        if (mask.Width != grid.Width || mask.Height != grid.Height || mask.MinX != grid.MinX || mask.MinY != grid.MinY)
            throw new LandscapeInputException("validity_mask_not_aligned");
        for (var i = 0; i < grid.Values.Length; i++) if (mask.Values[i] != 255) grid.Values[i] = grid.NoData;
        return grid;
    }

    private static async Task<List<NtsGeometry>> VectorAsync(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new LandscapeInputException("context_budget_exceeded");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        var root = document.RootElement;
        if (root.GetProperty("type").GetString() != "FeatureCollection") throw new LandscapeInputException("invalid_context_vectors");
        var reader = new GeoJsonReader();
        var geometries = root.GetProperty("features").EnumerateArray()
            .Select(f => reader.Read<NtsGeometry>(f.GetProperty("geometry").GetRawText())).ToList();
        if (geometries.Count > 200_000 || geometries.Any(g => g is null || g.IsEmpty || !g.IsValid ||
            g.Coordinates.Any(c => !double.IsFinite(c.X) || !double.IsFinite(c.Y) || c.X is < 200000 or > 800000 || c.Y is < 5900000 or > 6300000)))
            throw new LandscapeInputException("invalid_context_vectors");
        return geometries;
    }

    public static LandscapeViewpoint View(AsciiGrid visible, AsciiGrid cover, Coordinate point)
    {
        var classes = new int[4]; var sectors = new bool[36]; var count = 0; var water = 0; var unknown = false;
        for (var i = 0; i < visible.Values.Length; i++)
        {
            if (visible.Values[i] != 255) continue;
            var p = visible.Center(i);
            var distance = Math.Sqrt(Math.Pow(p.X - point.X, 2) + Math.Pow(p.Y - point.Y, 2));
            if (distance > Radius) continue;
            count++;
            if (distance >= 1500)
                sectors[(int)((Math.Atan2(p.Y - point.Y, p.X - point.X) + 2 * Math.PI) % (2 * Math.PI) / (Math.PI / 18)) % 36] = true;
            var index = cover.Index(p.X, p.Y);
            if (!cover.Valid(index) || cover.Values[index] is < 0 or > 3 || cover.Values[index] != Math.Truncate(cover.Values[index])) { unknown = true; continue; }
            var category = (int)cover.Values[index]; classes[category]++;
            if (category == 3) water++;
        }
        // A missing cover cell cannot be counted as human cover or a verified zero water fraction.
        if (unknown) throw new LandscapeInputException("visible_cover_incomplete");
        var natural = classes.Skip(1).Sum(); double diversity = 0;
        if (natural > 0)
            foreach (var n in classes.Skip(1).Where(n => n > 0))
            { var fraction = n / (double)natural; diversity -= fraction * Math.Log(fraction) / Math.Log(3); }
        return new(point.X, point.Y, 1.7, count * visible.CellSize * visible.CellSize / 1_000_000,
            sectors.Count(v => v) / 36.0, diversity, count > 0 ? water / (double)count : 0);
    }

    private static (double? Fraction, double? Forest, double? Water) Nature(AsciiGrid cover, Coordinate center)
    {
        if (!cover.CompleteDisk(center.X, center.Y, 1000)) return (null, null, null);
        int all = 0, natural = 0; double forest = 1000, water = 1000;
        for (var i = 0; i < cover.Values.Length; i++)
        {
            var p = cover.Center(i); var distance = Math.Sqrt(Math.Pow(p.X - center.X, 2) + Math.Pow(p.Y - center.Y, 2));
            if (distance > 1000) continue;
            var category = cover.Values[i];
            if (category is < 0 or > 3 || category != Math.Truncate(category)) return (null, null, null);
            all++; if (category > 0) natural++;
            if (category == 3) water = Math.Min(water, distance);
            // Forest-edge distance, including observers standing inside forest.
            if (category == 1 && Neighbours(cover, i).Any(n => cover.Valid(n) && cover.Values[n] != 1)) forest = Math.Min(forest, distance);
        }
        return (all > 0 ? natural / (double)all : null, forest, water);
    }

    private static (double? Relief, double? Tpi) Terrain(AsciiGrid ground, Coordinate center)
    {
        // TPI's 500 m local windows require an extra margin beyond the 1 km metric disk.
        if (!ground.CompleteDisk(center.X, center.Y, 1800)) return (null, null);
        var stride = ground.Width + 1;
        var sums = new double[stride * (ground.Height + 1)];
        var counts = new int[sums.Length];
        for (var row = 0; row < ground.Height; row++)
            for (var col = 0; col < ground.Width; col++)
            {
                var i = row * ground.Width + col; var s = (row + 1) * stride + col + 1;
                sums[s] = (ground.Valid(i) ? ground.Values[i] : 0) + sums[s - 1] + sums[s - stride] - sums[s - stride - 1];
                counts[s] = (ground.Valid(i) ? 1 : 0) + counts[s - 1] + counts[s - stride] - counts[s - stride - 1];
            }
        var elevations = new List<double>(); var tpis = new List<double>(); var reach = (int)(500 / ground.CellSize);
        for (var i = 0; i < ground.Values.Length; i++)
        {
            var p = ground.Center(i);
            if (Math.Pow(p.X - center.X, 2) + Math.Pow(p.Y - center.Y, 2) > 1_000_000) continue;
            var row = i / ground.Width; var col = i % ground.Width;
            var left = Math.Max(0, col - reach); var right = Math.Min(ground.Width, col + reach + 1);
            var top = Math.Max(0, row - reach); var bottom = Math.Min(ground.Height, row + reach + 1);
            var sum = sums[bottom * stride + right] - sums[top * stride + right] - sums[bottom * stride + left] + sums[top * stride + left];
            var count = counts[bottom * stride + right] - counts[top * stride + right] - counts[bottom * stride + left] + counts[top * stride + left];
            if (count != (right - left) * (bottom - top)) return (null, null);
            elevations.Add(ground.Values[i]); tpis.Add(ground.Values[i] - sum / count);
        }
        if (elevations.Count == 0) return (null, null);
        elevations.Sort(); var mean = tpis.Average();
        return (elevations[(int)((elevations.Count - 1) * .9)] - elevations[(int)((elevations.Count - 1) * .1)],
            Math.Sqrt(tpis.Average(t => Math.Pow(t - mean, 2))));
    }

    public static (double? TotalHa, double? LargestHa) GentleGround(AsciiGrid ground, MultiPolygon polygon)
    {
        var gentle = new HashSet<int>();
        var inside = 0;
        for (var i = 0; i < ground.Values.Length; i++)
        {
            var p = ground.Center(i);
            if (!polygon.Contains(polygon.Factory.CreatePoint(new Coordinate(p.X, p.Y)))) continue;
            inside++;
            var ns = Neighbours(ground, i).ToArray();
            if (ns.Length != 4 || !ground.Valid(i) || ns.Any(n => !ground.Valid(n))) return (null, null);
            var col = i % ground.Width; var row = i / ground.Width;
            if (col == 0 || row == 0 || col == ground.Width - 1 || row == ground.Height - 1) return (null, null);
            var dx = (ground.Values[i + 1] - ground.Values[i - 1]) / (2 * ground.CellSize);
            var dy = (ground.Values[i + ground.Width] - ground.Values[i - ground.Width]) / (2 * ground.CellSize);
            if (Math.Atan(Math.Sqrt(dx * dx + dy * dy)) <= 8 * Math.PI / 180) gentle.Add(i);
        }
        var total = gentle.Count; var largest = 0;
        if (inside == 0) return (null, null);
        while (gentle.Count > 0)
        {
            var queue = new Queue<int>(); var start = gentle.First(); gentle.Remove(start); queue.Enqueue(start); var count = 0;
            while (queue.TryDequeue(out var current))
            {
                count++;
                foreach (var n in Neighbours(ground, current)) if (gentle.Remove(n)) queue.Enqueue(n);
            }
            largest = Math.Max(largest, count);
        }
        var hectares = ground.CellSize * ground.CellSize / 10000;
        return (total * hectares, largest * hectares);
    }

    private static IEnumerable<int> Neighbours(AsciiGrid grid, int i)
    {
        if (i % grid.Width > 0) yield return i - 1;
        if (i % grid.Width < grid.Width - 1) yield return i + 1;
        if (i / grid.Width > 0) yield return i - grid.Width;
        if (i / grid.Width < grid.Height - 1) yield return i + grid.Width;
    }
    private static double MedianValue(IEnumerable<double> values)
    { var sorted = values.Order().ToArray(); return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2; }

    private async Task PublishImageAsync(LandscapeAnalysisEntity analysis, string raster, string staging, CancellationToken ct)
    {
        var projected = Path.Combine(staging, "visibility-3857.tif");
        await gdal.RunAsync("gdalwarp", ["-q", "-t_srs", "EPSG:3857", "-r", "near", "-srcnodata", "0", "-dstnodata", "0", raster, projected], ct);
        using var info = JsonDocument.Parse(await gdal.RunAsync("gdalinfo", ["-json", projected], ct));
        var root = info.RootElement; var size = root.GetProperty("size").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var transform = root.GetProperty("geoTransform").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var wkt = System.Security.SecurityElement.Escape(root.GetProperty("coordinateSystem").GetProperty("wkt").GetString());
        var bands = string.Join("", new[] { ("Red", 30), ("Green", 135), ("Blue", 100), ("Alpha", 180) }.Select((color, i) =>
            $"<VRTRasterBand dataType=\"Byte\" band=\"{i + 1}\"><ColorInterp>{color.Item1}</ColorInterp><ComplexSource><SourceFilename relativeToVRT=\"1\">visibility-3857.tif</SourceFilename><SourceBand>1</SourceBand><ScaleRatio>{N(color.Item2 / 255.0)}</ScaleRatio></ComplexSource></VRTRasterBand>"));
        var vrt = Path.Combine(staging, "visibility.vrt");
        await File.WriteAllTextAsync(vrt, $"<VRTDataset rasterXSize=\"{size[0]}\" rasterYSize=\"{size[1]}\"><SRS>{wkt}</SRS><GeoTransform>{string.Join(',', transform.Select(N))}</GeoTransform>{bands}</VRTDataset>", ct);
        var finalDir = Path.Combine(artifactRoot, analysis.Id.ToString()); Directory.CreateDirectory(finalDir);
        var png = Path.Combine(finalDir, "visibility.png");
        await gdal.RunAsync("gdal_translate", ["-q", "-of", "PNG", "-outsize", "1024", "1024", vrt, png], ct);
        await using (var stream = File.OpenRead(png)) analysis.VisibilitySha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        analysis.VisibilityArtifact = analysis.Id + "/visibility.png";
        var minX = transform[0]; var maxY = transform[3]; var maxX = minX + transform[1] * size[0]; var minY = maxY + transform[5] * size[1];
        double[] LonLat(double x, double y) => [x / 6378137 * 180 / Math.PI, (2 * Math.Atan(Math.Exp(y / 6378137)) - Math.PI / 2) * 180 / Math.PI];
        analysis.VisibilityCoordinates = LandscapeJson.Write(new[] { LonLat(minX, maxY), LonLat(maxX, maxY), LonLat(maxX, minY), LonLat(minX, minY) });
    }
}
