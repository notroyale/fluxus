using Fluxus.Core;

namespace Fluxus.Infrastructure.Sources;

public sealed class SourceOptions
{
    public string Mode { get; set; } = "Fixture";
    public string FixturePath { get; set; } = "Fixtures";
    public DateTimeOffset? FixtureObservedAt { get; set; }
    public int DelayMilliseconds { get; set; } = 2000;
    public int MaxAuctions { get; set; } = 10;
    public int MaxPages { get; set; } = 40;
    public bool ScheduledImportsEnabled { get; set; }
    public bool ManualImportsEnabled { get; set; }
    public int IntervalMinutes { get; set; } = 60;
}

public sealed class EvarzytynesSource(HttpClient http, AuctionParser parser, SourceOptions options) : IAuctionSource
{
    public static Uri ListingUrl(int page, bool broad = false) => new(
        $"https://www.evarzytynes.lt/evs/pages/auctions.do?estateType=1&{(broad ? "" : "estateSubtype=1&")}listType=1&page={page}&stateType=PASKELBTA-IR-VYKSTA");

    public async Task<string> FetchAsync(Uri uri, CancellationToken ct)
    {
        await Task.Delay(options.DelayMilliseconds, ct);
        using var response = await http.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<ListingPage> GetListingAsync(int page, bool broad, CancellationToken ct)
    {
        if (options.Mode == "Fixture")
        {
            var path = Path.Combine(options.FixturePath, "listing.html");
            return parser.ParseListing(await File.ReadAllTextAsync(path, ct), page) with { NextPage = null };
        }
        return parser.ParseListing(await FetchAsync(ListingUrl(page, broad), ct), page);
    }

    public async Task<ParsedAuction> GetDetailAsync(ListingItem item, CancellationToken ct)
    {
        var html = options.Mode == "Fixture"
            ? await File.ReadAllTextAsync(Path.Combine(options.FixturePath, $"auction-{item.SourceId}.html"), ct)
            : await FetchAsync(item.SourceUrl, ct);
        return parser.ParseDetail(html, item);
    }
}
