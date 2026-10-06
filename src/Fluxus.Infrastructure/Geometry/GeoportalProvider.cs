using System.Text.Json;
using Fluxus.Core;

namespace Fluxus.Infrastructure.Geometry;

public sealed class GeoportalProvider(HttpClient http, Sources.SourceOptions options) : IParcelProvider, IParcelBatchProvider
{
    public const string Layer = "https://geoportal.lt/arcgis/rest/services/geoportal_teikeju/RC_sklypai_opendata/MapServer/0";

    public async Task<IReadOnlyList<ParcelResolution>> ResolveBatchAsync(IReadOnlyList<LandItem> items, CancellationToken ct)
    {
        if (options.Mode == "Fixture")
        {
            var fixtures = new List<ParcelResolution>();
            foreach (var item in items) fixtures.Add(await ResolveAsync(item, ct));
            return fixtures;
        }
        var results = items.Select(_ => new ParcelResolution("missing_identifier")).ToArray();
        await ResolveGroupAsync("unikalus_nr", Enumerable.Range(0, items.Count).Where(i => items[i].UniqueNumber != null));
        await ResolveGroupAsync("kadastro_nr", Enumerable.Range(0, items.Count).Where(i => items[i].CadastralNumber != null &&
            (items[i].UniqueNumber == null || results[i].Status == "not_found")));
        return results;

        async Task ResolveGroupAsync(string field, IEnumerable<int> indexes)
        {
            foreach (var chunk in indexes.ToArray().Chunk(20))
            {
                var requested = chunk.Select(i => items[i]).ToArray();
                IReadOnlyList<ParcelResolution> group;
                try
                {
                    var values = requested.Select(item => field == "unikalus_nr" ? ParcelIdentifiers.Unique(item.UniqueNumber) :
                        ParcelIdentifiers.Cadastral(item.CadastralNumber)).Distinct().ToArray();
                    if (values.Any(v => v == null)) throw new FormatException("Invalid parcel batch identifier.");
                    var where = Uri.EscapeDataString($"{field} IN ({string.Join(',', values.Select(v => $"'{v}'"))})");
                    await Task.Delay(options.DelayMilliseconds, ct);
                    var json = await http.GetStringAsync($"{Layer}/query?where={where}&outFields=unikalus_nr,kadastro_nr,sav_kodas,sav_pavadinimas&returnGeometry=true&outSR=4326&f=json", ct);
                    group = ParseBatch(json, field, requested);
                }
                catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
                { group = requested.Select(_ => new ParcelResolution("provider_unavailable", Reason: "request_failed")).ToArray(); }
                catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
                { group = requested.Select(_ => new ParcelResolution("invalid_geometry", Reason: "invalid_provider_response")).ToArray(); }
                for (var i = 0; i < chunk.Length; i++) results[chunk[i]] = group[i];
            }
        }
    }

    public static IReadOnlyList<ParcelResolution> ParseBatch(string json, string field, IReadOnlyList<LandItem> requested)
    {
        if (field is not ("unikalus_nr" or "kadastro_nr")) throw new ArgumentException("Unsupported parcel field.", nameof(field));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out _)) return requested.Select(_ => new ParcelResolution("provider_unavailable", Reason: "arcgis_error")).ToArray();
        if (root.TryGetProperty("exceededTransferLimit", out var limit) && limit.GetBoolean())
            return requested.Select(_ => new ParcelResolution("ambiguous", Reason: "transfer_limit")).ToArray();
        var features = root.GetProperty("features").EnumerateArray().ToArray();
        return requested.Select(item =>
        {
            var identifier = field == "unikalus_nr" ? item.UniqueNumber : item.CadastralNumber;
            var matching = features.Where(f =>
            {
                var attribute = f.GetProperty("attributes").GetProperty(field);
                var raw = attribute.ValueKind == JsonValueKind.String ? attribute.GetString() : null;
                return (field == "unikalus_nr" ? ParcelIdentifiers.Unique(raw) : ParcelIdentifiers.Cadastral(raw)) == identifier;
            }).ToArray();
            if (matching.Length == 0) return new ParcelResolution("not_found");
            if (matching.Length > 1) return new ParcelResolution("ambiguous");
            try { return Parse(JsonSerializer.Serialize(new { features = matching, spatialReference = root.GetProperty("spatialReference") }), item); }
            catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
            { return new ParcelResolution("invalid_geometry", Reason: "invalid_provider_response"); }
        }).ToArray();
    }

    public async Task<ParcelResolution> ResolveAsync(LandItem item, CancellationToken ct)
    {
        if (item.UniqueNumber is null && item.CadastralNumber is null) return new("missing_identifier");
        try
        {
            if (options.Mode == "Fixture")
            {
                var file = Path.Combine(options.FixturePath, $"parcel-{item.UniqueNumber ?? item.CadastralNumber?.Replace('/', '-').Replace(':', '-')}.json");
                return File.Exists(file) ? Parse(await File.ReadAllTextAsync(file, ct), item) : new("not_found");
            }
            if (item.UniqueNumber is { } unique)
            {
                var resolved = await QueryAsync("unikalus_nr", unique, item, ct);
                if (resolved.Status != "not_found") return resolved;
            }
            return item.CadastralNumber is { } cadastral ? await QueryAsync("kadastro_nr", cadastral, item, ct) : new("not_found");
        }
        catch (Exception exception) when (exception is HttpRequestException ||
            exception is TaskCanceledException && !ct.IsCancellationRequested)
        { return new("provider_unavailable", Reason: "request_failed"); }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        { return new("invalid_geometry", Reason: "invalid_provider_response"); }
    }

    public async Task<string> FetchGeometryAsync(LandItem item, CancellationToken ct)
    {
        var field = item.UniqueNumber is null ? "kadastro_nr" : "unikalus_nr";
        var value = item.UniqueNumber ?? item.CadastralNumber ?? throw new FormatException("Missing identifier.");
        var normalized = field == "unikalus_nr" ? ParcelIdentifiers.Unique(value) : ParcelIdentifiers.Cadastral(value);
        if (normalized is null) throw new FormatException("Invalid parcel query identifier.");
        var where = Uri.EscapeDataString($"{field}='{normalized}'");
        var url = $"{Layer}/query?where={where}&outFields=unikalus_nr,kadastro_nr,sav_kodas,sav_pavadinimas&returnGeometry=true&outSR=4326&f=json";
        await Task.Delay(options.DelayMilliseconds, ct);
        return await http.GetStringAsync(url, ct);
    }

    private async Task<ParcelResolution> QueryAsync(string field, string value, LandItem item, CancellationToken ct)
    {
        var queryItem = field == "kadastro_nr" ? item with { UniqueNumber = null, CadastralNumber = value } : item;
        return Parse(await FetchGeometryAsync(queryItem, ct), item);
    }

    public static ParcelResolution Parse(string json, LandItem requested)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out _)) return new("provider_unavailable", Reason: "arcgis_error");
        if (root.TryGetProperty("exceededTransferLimit", out var exceeded) && exceeded.GetBoolean()) return new("ambiguous");
        var features = root.GetProperty("features");
        if (features.GetArrayLength() == 0) return new("not_found");
        if (features.GetArrayLength() != 1) return new("ambiguous");
        var reference = root.GetProperty("spatialReference");
        if (!reference.TryGetProperty("wkid", out var srid) || srid.GetInt32() != 4326)
            throw new FormatException("Provider did not transform geometry to EPSG:4326.");
        var feature = features[0];
        var attributes = feature.GetProperty("attributes");
        string? Read(string key) => attributes.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var unique = ParcelIdentifiers.Unique(Read("unikalus_nr"));
        var cadastral = ParcelIdentifiers.Cadastral(Read("kadastro_nr"));
        if (requested.UniqueNumber is { } expectedUnique && unique != expectedUnique ||
            requested.CadastralNumber is { } expectedCad && cadastral != expectedCad)
            return new("identifier_conflict");
        var geometry = ArcGisGeometry.Read(feature.GetProperty("geometry").GetProperty("rings"));
        return new("resolved_exact", ArcGisGeometry.Write(geometry), Read("sav_kodas"), Read("sav_pavadinimas"));
    }
}
