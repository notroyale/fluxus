using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Fluxus.Core;

namespace Fluxus.Infrastructure.Sources;

public sealed partial class AuctionParser
{
    private static readonly Uri Origin = new("https://www.evarzytynes.lt");
    private static readonly TimeZoneInfo Vilnius = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vilnius");

    public ListingPage ParseListing(string html, int page)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var cards = document.QuerySelectorAll("ul.bid_list > li");
        var items = new List<ListingItem>();
        foreach (var card in cards)
        {
            var link = card.QuerySelector("h2.no a");
            if (link?.GetAttribute("href") is not { } href) continue;
            var url = new Uri(Origin, href);
            if (url.Host != Origin.Host || url.AbsolutePath != "/evs/pages/auction.do")
                throw new FormatException("Unexpected auction URL.");
            var id = SourceIdPattern().Match(url.Query).Groups[1].Value;
            var number = NumberPattern().Match(Text(link)).Groups[1].Value;
            if (id.Length == 0 || number.Length == 0) throw new FormatException("Missing auction identity.");
            items.Add(new(id, number, url, Text(card.QuerySelector(".list_box"))));
        }
        var isEmpty = document.QuerySelector(".no-results, .empty-results") != null ||
            Text(document.Body).Contains("Pagal pasirinktus kriterijus", StringComparison.OrdinalIgnoreCase) &&
            Text(document.Body).Contains("nerasta", StringComparison.OrdinalIgnoreCase);
        if (items.Count == 0 && !isEmpty) throw new FormatException("Auction listing layout was not recognized.");
        var next = document.QuerySelectorAll("a.paginationLink[data-page]")
            .Select(a => int.TryParse(a.GetAttribute("data-page"), out var value) ? value : -1)
            .Where(value => value > page).Order().Cast<int?>().FirstOrDefault();
        return new(items.DistinctBy(i => i.SourceId).ToArray(), next, isEmpty);
    }

    public ParsedAuction ParseDetail(string html, ListingItem source)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var metadata = document.QuerySelector(".load_block > .clearfix > ul.list") ??
            document.QuerySelector(".load_block ul.list") ?? throw new FormatException("Missing auction metadata.");
        if (!Text(document.Body).Contains($"Nr. {source.Number}", StringComparison.Ordinal))
            throw new FormatException("Auction number does not match the requested page.");
        var fields = Fields(metadata);
        foreach (var label in new[] { "Būsena", "Pradžios data", "Pabaigos data" })
            if (!fields.ContainsKey(label)) throw new FormatException($"Missing expected auction field: {label}.");
        if (!fields.ContainsKey("Pradinė pardavimo kaina") && !fields.ContainsKey("Pradinė nuomos kaina"))
            throw new FormatException("Missing expected auction price.");
        var rawStatus = Value(fields, "Būsena");
        var status = rawStatus?.Contains("Vykstan", StringComparison.OrdinalIgnoreCase) == true ? "running" :
            rawStatus?.Contains("Paskelbt", StringComparison.OrdinalIgnoreCase) == true ? "announced" :
            rawStatus?.Contains("Atšaukt", StringComparison.OrdinalIgnoreCase) == true ? "cancelled" :
            rawStatus?.Contains("Baigt", StringComparison.OrdinalIgnoreCase) == true ? "ended" : "unknown";
        var sections = document.QuerySelectorAll(".product_desc");
        if (sections.Length == 0) throw new FormatException("Missing property sections.");
        var items = new List<LandItem>();
        var nonLand = false;
        foreach (var section in sections)
        {
            var values = Fields(section);
            var heading = Text(section.QuerySelector("h2"));
            if (Value(values, "Turto potipis") != "Sklypai" && !heading.Contains("Žemės sklypas"))
            { nonLand = true; continue; }
            var issues = new List<ParseIssue>();
            var unique = ParcelIdentifiers.Unique(Value(values, "Unikalus Nr."));
            var cadastral = ParcelIdentifiers.Cadastral(Value(values, "Kadastrinis Nr."));
            if (unique is null && cadastral is null) issues.Add(new("missing_identifier", "parcel"));
            // Classify this property section, not arbitrary descriptions of leases on sold property.
            var offerType = heading.StartsWith("Nuomojamas turtas", StringComparison.OrdinalIgnoreCase) ||
                values.ContainsKey("Nuomojama turto dalis") ? "rent" :
                heading.StartsWith("Parduodamas turtas", StringComparison.OrdinalIgnoreCase) ||
                values.ContainsKey("Parduodama turto dalis") ? "sale" : "unknown";
            var shareRaw = Value(values, offerType == "rent" ? "Nuomojama turto dalis" : "Parduodama turto dalis");
            long? numerator = null, denominator = null;
            if (shareRaw is { })
            {
                var match = SharePattern().Match(shareRaw);
                if (match.Success && long.TryParse(match.Groups[1].Value, out var n) &&
                    long.TryParse(match.Groups[2].Value, out var d) && d > 0 && n > 0 && n <= d)
                { numerator = n; denominator = d; }
                else issues.Add(new("invalid_share", "share"));
            }
            var key = section.QuerySelector("[data-estate-id]")?.GetAttribute("data-estate-id") ?? unique ?? cadastral;
            if (key is null) throw new FormatException("Unresolved item has no stable section identity.");
            var description = Value(values, "Aprašymas");
            // Only retain the purpose line, not arbitrary descriptions containing personal information.
            var landUse = description is null ? null : PurposePattern().Match(description).Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(landUse)) landUse = null;
            var item = new LandItem(key, unique, cadastral, Value(values, "Adresas"),
                Area(Value(values, "Bendras turto plotas")), Area(Value(values, offerType == "rent" ? "Nuomojamo turto dalies plotas" : "Parduodamo turto dalies plotas")),
                numerator, denominator, shareRaw, landUse,
                Money(values.FirstOrDefault(p => p.Key.StartsWith(offerType == "rent" ? "Pradinė žemės sklypo nuomos kaina" : "Pradinė žemės sklypo pardavimo kaina")).Value), issues)
            { OfferType = offerType };
            if (item.HasAreaConflict) issues.Add(new("area_conflict", "sold_area"));
            items.Add(item);
        }
        if (items.Select(i => i.SourceKey).Distinct().Count() != items.Count)
            throw new FormatException("Duplicate property section identity.");
        if (items.Count == 0) throw new FormatException("Discovered land auction has no recognized land sections.");
        return new(source.SourceId, source.Number, source.SourceUrl.ToString(), status,
            Date(Value(fields, "Pradžios data")), Date(Value(fields, "Pabaigos data")),
            Money(Value(fields, "Pradinė pardavimo kaina") ?? Value(fields, "Pradinė nuomos kaina")),
            PositiveBid(Value(fields, "Paskutinė siūloma kaina") ?? Text(document.QuerySelector("#maxBid"))),
            nonLand, items);
    }

    public string SanitizeDetail(string html)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var number = NumberPattern().Match(Text(document.Body)).Value;
        var metadata = document.QuerySelector(".load_block ul.list")?.Clone(true) as IElement;
        if (metadata is null) throw new FormatException("Missing metadata to sanitize.");
        var allowed = new[] { "Būsena", "Pradžios data", "Pabaigos data", "Pradinė pardavimo kaina", "Pradinė nuomos kaina", "Paskutinė siūloma kaina" };
        Retain(metadata, allowed);
        var sections = document.QuerySelectorAll(".product_desc").Select(s => (IElement)s.Clone(true)).ToArray();
        foreach (var section in sections)
        {
            Retain(section, ["Parduodama turto dalis", "Bendras turto plotas", "Parduodamo turto dalies plotas",
                "Nuomojama turto dalis", "Nuomojamo turto dalies plotas",
                "Turto potipis", "Unikalus Nr.", "Kadastrinis Nr.", "Adresas",
                "Pradinė žemės sklypo pardavimo kaina iš bendros pradinės pardavimo kainos",
                "Pradinė žemės sklypo nuomos kaina iš bendros pradinės nuomos kainos"]);
            foreach (var node in section.QuerySelectorAll("a, img, script, .estate-pictures-pager")) node.Remove();
            foreach (var gallery in section.QuerySelectorAll(".gallery")) gallery.InnerHtml = "";
        }
        return $"<!doctype html><html lang=\"lt\"><body><h1>{number}</h1><span id=\"maxBid\">{Text(document.QuerySelector("#maxBid"))}</span><div class=\"load_block\"><div class=\"clearfix\">{metadata.OuterHtml}</div></div>{string.Join("", sections.Select(s => s.OuterHtml))}</body></html>";
    }

    private static void Retain(IElement root, string[] allowed)
    {
        foreach (var li in root.QuerySelectorAll("li").ToArray())
            if (!allowed.Contains(Text(li.QuerySelector("span.left")).TrimEnd(':'))) li.Remove();
    }

    private static Dictionary<string, string> Fields(IElement root) => root.QuerySelectorAll("li")
        .Where(li => li.QuerySelector("span.left") is not null && li.QuerySelector("span.right") is not null)
        .GroupBy(li => Text(li.QuerySelector("span.left")).TrimEnd(':'))
        .ToDictionary(g => g.Key, g => Text(g.First().QuerySelector("span.right")));

    private static string? Value(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
    private static string Text(INode? node) => WhitespacePattern().Replace(node?.TextContent ?? "", " ").Trim();

    public static decimal? Money(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "–" or "-" or "—") return null;
        var cleaned = WhitespacePattern().Replace(value, "").Replace("EUR", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Eur", "", StringComparison.OrdinalIgnoreCase).Replace(',', '.');
        return decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            && amount >= 0 ? amount : throw new FormatException("Invalid monetary amount.");
    }

    // The source uses 0 in #maxBid when no bid has been placed.
    private static decimal? PositiveBid(string? value) => Money(value) is > 0 and var amount ? amount : null;

    public static decimal? Area(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = AreaPattern().Match(value);
        if (!match.Success) return null;
        var amount = decimal.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        if (amount <= 0) return null;
        return match.Groups[2].Value switch { "ha" => amount, "a." or "a" => amount / 100, _ => amount / 10000 };
    }

    public static DateTimeOffset? Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTime.TryParseExact(value, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) ||
            Vilnius.IsAmbiguousTime(local) || Vilnius.IsInvalidTime(local))
            throw new FormatException("Invalid or ambiguous source date.");
        return new DateTimeOffset(local, Vilnius.GetUtcOffset(local)).ToUniversalTime();
    }

    [GeneratedRegex(@"[?&]id=(\d+)(?:&|$)")] private static partial Regex SourceIdPattern();
    [GeneratedRegex(@"Nr\.\s*(\d+)")] private static partial Regex NumberPattern();
    [GeneratedRegex(@"\s+")] private static partial Regex WhitespacePattern();
    [GeneratedRegex(@"^(\d+)\s*/\s*(\d+)$")] private static partial Regex SharePattern();
    [GeneratedRegex(@"([0-9]+(?:[.,][0-9]+)?)\s*(ha|a\.|a\b|kv\.\s*m|m²)")] private static partial Regex AreaPattern();
    [GeneratedRegex(@"(?:Daikto pagrindinė naudojimo paskirtis:|Žemės sklypo paskirtis/naudojimo būdas\(-ai\):)\s*([^;]+)")] private static partial Regex PurposePattern();
}
