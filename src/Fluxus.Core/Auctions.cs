namespace Fluxus.Core;

public sealed record ParseIssue(string Code, string Field);

public sealed record ListingItem(string SourceId, string Number, Uri SourceUrl, string Summary);

public sealed record ListingPage(IReadOnlyList<ListingItem> Items, int? NextPage, bool IsEmpty);

public sealed record LandItem(
    string SourceKey,
    string? UniqueNumber,
    string? CadastralNumber,
    string? Address,
    decimal? TotalAreaHa,
    decimal? SourceSoldAreaHa,
    long? ShareNumerator,
    long? ShareDenominator,
    string? ShareRaw,
    string? LandUse,
    decimal? ComponentPriceEur,
    IReadOnlyList<ParseIssue> Issues)
{
    public string OfferType { get; init; } = "unknown";

    public string OwnershipKind => OfferType == "rent" ? "not_applicable" :
        ShareNumerator is > 0 && ShareDenominator is > 0 && ShareNumerator <= ShareDenominator
            ? ShareNumerator == ShareDenominator ? "full" : "partial" : "unknown";

    public decimal? DerivedSoldAreaHa => TotalAreaHa is { } area &&
        ShareNumerator is { } numerator && ShareDenominator is > 0
        ? area * numerator / ShareDenominator.Value : null;

    public bool HasAreaConflict => SourceSoldAreaHa is { } source && DerivedSoldAreaHa is { } derived &&
        Math.Abs(source - derived) > 0.0001m;

    public decimal? SoldAreaHa => HasAreaConflict ? null : SourceSoldAreaHa ?? DerivedSoldAreaHa;

    public string SoldAreaBasis => HasAreaConflict ? "conflict" : SourceSoldAreaHa.HasValue
        ? "source" : DerivedSoldAreaHa.HasValue ? "derived" : "unknown";
}

public sealed record ParsedAuction(
    string SourceId, string Number, string SourceUrl, string Status,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, decimal? InitialPriceEur,
    decimal? LastBidEur, bool ContainsNonLandAssets, IReadOnlyList<LandItem> Items);

public static class ParcelIdentifiers
{
    public static string? Unique(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = string.Concat(raw.Where(char.IsAsciiDigit));
        return digits.Length == 12 && raw.All(c => char.IsAsciiDigit(c) || c == '-' || char.IsWhiteSpace(c))
            ? digits : null;
    }

    public static string? Cadastral(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Trim().Split(['/', ':']);
        return parts.Length == 3 && parts[0].Length == 4 && parts[1].Length == 4 &&
            parts[2].Length is > 0 and <= 4 && parts.All(p => p.All(char.IsAsciiDigit))
            ? $"{parts[0]}/{parts[1]}:{parts[2].PadLeft(4, '0')}" : null;
    }
}
