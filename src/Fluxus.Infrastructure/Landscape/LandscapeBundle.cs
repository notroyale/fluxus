using System.Security.Cryptography;
using System.Text.Json;

namespace Fluxus.Infrastructure.Landscape;

public sealed record LandscapeAsset(string Path, string Sha256, double ResolutionM);
public sealed record LandscapeBundle(string Provider, string Release, string SourceUrl, string LicenseUrl,
    string Attribution, int SurveyYearFrom, int SurveyYearTo, string VerticalDatum, int Epsg,
    bool Substituted, string Preparation, LandscapeAsset Ground, LandscapeAsset Surface,
    LandscapeAsset Cover, LandscapeAsset Buildings, LandscapeAsset MajorRoads, double[] ContextBounds);

public static class LandscapeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)
        ?? throw new FormatException("Invalid landscape JSON.");
}

public sealed class LandscapeInputException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public static class LandscapeBundleReader
{
    public static async Task<(LandscapeBundle Bundle, string Json, string Hash)> ReadAsync(
        string manifestPath, string inputRoot, CancellationToken ct)
    {
        var path = ConfinedPath(inputRoot, manifestPath);
        if (new FileInfo(path).Length > 128 * 1024) throw new LandscapeInputException("manifest_too_large");
        var bundle = LandscapeJson.Read<LandscapeBundle>(await File.ReadAllTextAsync(path, ct));
        Validate(bundle);
        // Resolve relative paths once; registration snapshots an immutable manifest with content hashes.
        LandscapeAsset Resolve(LandscapeAsset a) => a with { Path = ConfinedPath(inputRoot,
            Path.GetFullPath(a.Path, Path.GetDirectoryName(path)!)) };
        bundle = bundle with { Ground = Resolve(bundle.Ground), Surface = Resolve(bundle.Surface),
            Cover = Resolve(bundle.Cover), Buildings = Resolve(bundle.Buildings), MajorRoads = Resolve(bundle.MajorRoads) };
        await VerifyAsync(bundle, inputRoot, ct);
        var json = LandscapeJson.Write(bundle);
        return (bundle, json, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))));
    }

    public static void Validate(LandscapeBundle b)
    {
        if (b.Epsg != 3346 || b.SurveyYearFrom < 2000 || b.SurveyYearTo < b.SurveyYearFrom ||
            b.SurveyYearTo > DateTime.UtcNow.Year || string.IsNullOrWhiteSpace(b.VerticalDatum) ||
            string.IsNullOrWhiteSpace(b.Provider) || string.IsNullOrWhiteSpace(b.Release) ||
            string.IsNullOrWhiteSpace(b.Attribution) || string.IsNullOrWhiteSpace(b.Preparation) ||
            !IsHttps(b.SourceUrl) || !IsHttps(b.LicenseUrl) || b.ContextBounds is not { Length: 4 } ||
            b.ContextBounds.Any(x => !double.IsFinite(x)) || b.ContextBounds[0] >= b.ContextBounds[2] ||
            b.ContextBounds[1] >= b.ContextBounds[3] || b.Ground is null || b.Surface is null ||
            b.Cover is null || b.Buildings is null || b.MajorRoads is null)
            throw new LandscapeInputException("invalid_source_provenance");
        foreach (var asset in Assets(b))
            if (asset.Sha256 is not { Length: 64 } || asset.Sha256.Any(c => !Uri.IsHexDigit(c)) ||
                string.IsNullOrWhiteSpace(asset.Path) || !double.IsFinite(asset.ResolutionM) || asset.ResolutionM < 0)
                throw new LandscapeInputException("invalid_asset_manifest");
        if (b.Ground.ResolutionM is <= 0 or > 2 || b.Surface.ResolutionM is <= 0 or > 5 ||
            b.Cover.ResolutionM is <= 0 or > 5)
            throw new LandscapeInputException("source_resolution_unsupported");
        if (new[] { b.Ground, b.Surface, b.Cover }.Any(a => !string.Equals(Path.GetExtension(a.Path), ".tif", StringComparison.OrdinalIgnoreCase)) ||
            new[] { b.Buildings, b.MajorRoads }.Any(a => !string.Equals(Path.GetExtension(a.Path), ".geojson", StringComparison.OrdinalIgnoreCase)))
            throw new LandscapeInputException("unsupported_asset_format");
    }

    public static async Task VerifyAsync(LandscapeBundle b, string root, CancellationToken ct)
    {
        Validate(b);
        foreach (var a in Assets(b))
        {
            var path = ConfinedPath(root, a.Path);
            if (!File.Exists(path)) throw new LandscapeInputException("source_asset_missing");
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (!hash.Equals(a.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new LandscapeInputException("source_checksum_mismatch");
        }
    }

    public static string ConfinedPath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path, fullRoot);
        if (!full.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new LandscapeInputException("path_outside_input_root");
        return full;
    }
    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https";
    private static IEnumerable<LandscapeAsset> Assets(LandscapeBundle b) => [b.Ground, b.Surface, b.Cover, b.Buildings, b.MajorRoads];
}
