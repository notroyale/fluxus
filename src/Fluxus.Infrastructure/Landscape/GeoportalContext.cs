using System.Net.Http.Json;
using System.Text.Json;
using Fluxus.Infrastructure.Geometry;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Fluxus.Infrastructure.Landscape;

public sealed record PilotArea(Guid ParcelId, string? CadastralNumber, string? UniqueNumber,
    string GeometryHash, double[] RasterBounds, double[] ContextBounds, JsonElement ParcelGeometry,
    JsonElement OrderGeometryWgs84);

// Public read-only service; never submits orders or accepts Geoportal account terms.
public sealed class GeoportalContext(HttpClient http, GdalRunner gdal, string outputRoot)
{
    public const string Service = "https://www.geoportal.lt/arcgis/rest/services/NZT/GRPK/MapServer";
    private readonly GeometryFactory factory = new(new PrecisionModel(), 3346);

    public async Task<string> FetchAsync(PilotArea pilot, string destination, CancellationToken ct)
    {
        LidarPreparation.ValidateBounds(pilot.RasterBounds);
        LidarPreparation.ValidateBounds(pilot.ContextBounds);
        var output = LandscapeBundleReader.ConfinedPath(outputRoot, destination);
        if (Directory.Exists(output) || File.Exists(output)) throw new LandscapeInputException("preparation_output_exists");
        Directory.CreateDirectory(outputRoot);
        var stage = Path.Combine(Path.GetFullPath(outputRoot), ".context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var snapshots = new List<object>(); var cover = new List<object>();
            var unknown = new SortedSet<string>(StringComparer.Ordinal);
            var buildingCount = 0; var roadCount = 0;
            // Context vectors need only 1.5 km; cover needs the entire potential 3 km viewshed.
            foreach (var layer in new[] { 30, 27, 21, 19, 23, 24, 22, 11 })
            {
                var bounds = layer is 22 or 11 ? pilot.ContextBounds : pilot.RasterBounds;
                using var metadata = await GetAsync($"{Service}/{layer}?f=json", ct);
                var fields = metadata.RootElement.GetProperty("fields");
                if (!fields.EnumerateArray().Any(f => f.GetProperty("name").GetString() == "OBJECTID" && f.GetProperty("type").GetString() == "esriFieldTypeOID"))
                    throw new LandscapeInputException("context_schema_changed");
                var rows = await ReadLayerAsync(layer, bounds, ct);
                var shapes = rows.Select(f => Shape(f.GetProperty("geometry"), layer == 11)).ToArray();
                var features = new List<object>(); var ids = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < rows.Count; i++)
                {
                    var attributes = rows[i].GetProperty("attributes");
                    var topId = layer == 30 ? attributes.GetProperty("OBJECTID").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : attributes.GetProperty("TOP_ID").GetString();
                    if (string.IsNullOrWhiteSpace(topId) || !ids.Add(topId)) throw new LandscapeInputException("context_duplicate_or_missing_identity");
                    var code = layer == 30 ? "coverage" : attributes.GetProperty("GKODAS").GetString() ?? "";
                    var roadType = layer == 11 ? attributes.GetProperty("TIPAS").GetInt32() : 0;
                    var geometry = JsonDocument.Parse(new GeoJsonWriter().Write(shapes[i])).RootElement.Clone();
                    var feature = new { type = "Feature", geometry, properties = new { topId, code, roadType } };
                    features.Add(feature);
                    if (layer is 27 or 21 or 19 or 23 or 24)
                    {
                        var category = CoverCategory(layer, code);
                        if (category == 255) unknown.Add($"{layer}:{code}");
                        cover.Add(new { type = "Feature", geometry, properties = new { category, sourceLayer = layer, code, topId } });
                    }
                }
                if (layer == 30)
                {
                    var b = pilot.RasterBounds;
                    var area = factory.ToGeometry(new Envelope(b[0], b[2], b[1], b[3]));
                    var coverage = NetTopologySuite.Operation.Union.UnaryUnionOp.Union(shapes);
                    if (coverage is null || !coverage.Covers(area)) throw new LandscapeInputException("grpk_local_coverage_incomplete");
                }
                else if (layer == 22)
                {
                    buildingCount = features.Count;
                    await WriteFeaturesAsync(Path.Combine(stage, "buildings.geojson"), features, ct);
                }
                else if (layer == 11)
                {
                    // Renderer defines 1: motorway/trunk, 2: national road. Other roads
                    // remain in the audit snapshot, not this major-road seclusion metric.
                    var major = features.Where((_, i) => rows[i].GetProperty("attributes").GetProperty("TIPAS").GetInt32() is 1 or 2).ToArray();
                    roadCount = major.Length;
                    await WriteFeaturesAsync(Path.Combine(stage, "major-roads.geojson"), major, ct);
                }
                await File.WriteAllTextAsync(Path.Combine(stage, $"layer-{layer}.json"), LandscapeJson.Write(new { metadata = metadata.RootElement, rows }), ct);
                snapshots.Add(new { layer, count = rows.Count, bounds });
            }
            await WriteFeaturesAsync(Path.Combine(stage, "cover-polygons.geojson"), cover, ct);
            var rb = pilot.RasterBounds;
            await gdal.RunAsync("gdal_rasterize", ["-q", "-a", "category", "-init", "255", "-a_nodata", "255",
                "-ot", "Byte", "-a_srs", "EPSG:3346", "-te", N(rb[0]), N(rb[1]), N(rb[2]), N(rb[3]),
                "-tr", "5", "5", "-tap", "-co", "COMPRESS=DEFLATE", "-co", "TILED=YES",
                Path.Combine(stage, "cover-polygons.geojson"), Path.Combine(stage, "cover.tif")], ct);
            await File.WriteAllTextAsync(Path.Combine(stage, "context-preparation.json"), LandscapeJson.Write(new
            {
                status = "context_prepared_not_registered", sourceUrl = Service, fetchedAt = DateTimeOffset.UtcNow,
                epsg = 3346, pilot.ParcelId, pilot.GeometryHash, pilot.ContextBounds, pilot.RasterBounds,
                buildingCount, majorRoadCount = roadCount, unknownCoverCodes = unknown, snapshots,
                preparation = "GRPK IDs-first bounded queries; all IDs fetched and rechecked, TOP_ID unique. Coverage layer must cover raster bounds. Cover precedence: land cover, wetlands, water, built-up areas. Forest ms0=1; natural meadow sd2/sand sd42/wetland=2; water=3; orchard ms4/cultivated sd11/damaged sd7/built-up=0. Others and gaps=255 NoData. Major roads TIPAS 1/2 only. Public service snapshots can span edits; release/licence and alignment with LiDAR still require verification."
            }), ct);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!); Directory.Move(stage, output);
            return output;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public static int CoverCategory(int layer, string code) => layer switch
    {
        19 => 3,
        21 => 2,
        23 or 24 => 0,
        27 => code switch { "ms0" => 1, "sd2" or "sd42" => 2, "ms4" or "sd11" or "sd7" => 0, _ => 255 },
        _ => 255
    };

    public async Task<List<JsonElement>> ReadLayerAsync(int layer, double[] bounds, CancellationToken ct)
    {
        if (layer is not (30 or 27 or 21 or 19 or 23 or 24 or 22 or 11)) throw new ArgumentException("Unsupported GRPK context layer.");
        LidarPreparation.ValidateBounds(bounds);
        var geometry = Uri.EscapeDataString(LandscapeJson.Write(new { xmin = bounds[0], ymin = bounds[1], xmax = bounds[2], ymax = bounds[3], spatialReference = new { wkid = 3346 } }));
        var query = $"{Service}/{layer}/query?f=json&where=1%3D1&geometry={geometry}&geometryType=esriGeometryEnvelope&inSR=3346&spatialRel=esriSpatialRelIntersects";
        async Task<long[]> ReadIds()
        {
            using var response = await GetAsync(query + "&returnIdsOnly=true", ct);
            var root = response.RootElement;
            if (!root.TryGetProperty("objectIds", out var values)) throw new LandscapeInputException("context_ids_missing");
            var ids = values.ValueKind == JsonValueKind.Null ? [] : values.EnumerateArray().Select(v => v.GetInt64()).Order().ToArray();
            if (ids.Length > 50_000 || ids.Distinct().Count() != ids.Length) throw new LandscapeInputException("context_budget_exceeded");
            return ids;
        }
        var ids = await ReadIds(); var rows = new List<JsonElement>(); var seen = new HashSet<long>();
        foreach (var batch in ids.Chunk(200))
        {
            var fields = layer == 30 ? "OBJECTID" : "OBJECTID,TOP_ID,GKODAS" + (layer == 11 ? ",TIPAS" : "");
            using var result = await GetAsync($"{Service}/{layer}/query?f=json&objectIds={string.Join(',', batch)}&outFields={fields}&returnGeometry=true&outSR=3346", ct);
            var root = result.RootElement;
            if (!root.TryGetProperty("spatialReference", out var sr) ||
                !(sr.TryGetProperty("wkid", out var wkid) && wkid.GetInt32() == 3346 || sr.TryGetProperty("latestWkid", out var latest) && latest.GetInt32() == 3346))
                throw new LandscapeInputException("context_crs_mismatch");
            if (root.TryGetProperty("exceededTransferLimit", out var limit) && limit.GetBoolean()) throw new LandscapeInputException("context_query_truncated");
            foreach (var feature in root.GetProperty("features").EnumerateArray())
            {
                var id = feature.GetProperty("attributes").GetProperty("OBJECTID").GetInt64();
                if (!batch.Contains(id) || !seen.Add(id)) throw new LandscapeInputException("context_identity_mismatch");
                rows.Add(feature.Clone());
            }
            if (batch.Any(id => !seen.Contains(id))) throw new LandscapeInputException("context_query_incomplete");
            await Task.Delay(100, ct);
        }
        var recheckedIds = await ReadIds();
        if (!ids.SequenceEqual(recheckedIds)) throw new LandscapeInputException("context_changed_during_download");
        return rows;
    }

    private NtsGeometry Shape(JsonElement shape, bool lines)
    {
        if (!lines) return ArcGisGeometry.Read(shape.GetProperty("rings"), 3346);
        var paths = shape.GetProperty("paths").EnumerateArray().Select(path =>
            factory.CreateLineString(path.EnumerateArray().Select(p => new Coordinate(p[0].GetDouble(), p[1].GetDouble())).ToArray())).ToArray();
        var result = factory.CreateMultiLineString(paths);
        if (result.IsEmpty || !result.IsValid || result.Coordinates.Any(c => !double.IsFinite(c.X) || !double.IsFinite(c.Y) ||
                c.X is < 200000 or > 800000 || c.Y is < 5900000 or > 6300000))
            throw new LandscapeInputException("invalid_context_vectors");
        return result;
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var chunk = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > 16 * 1024 * 1024) throw new LandscapeInputException("context_response_budget_exceeded");
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }
        var document = JsonDocument.Parse(buffer.ToArray());
        if (document.RootElement.TryGetProperty("error", out _)) { document.Dispose(); throw new LandscapeInputException("geoportal_context_query_failed"); }
        return document;
    }

    private static Task WriteFeaturesAsync(string path, IEnumerable<object> features, CancellationToken ct) =>
        File.WriteAllTextAsync(path, LandscapeJson.Write(new { type = "FeatureCollection", features }), ct);
    private static string N(double value) => value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
}
