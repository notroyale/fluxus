using System.Text.Json;
using AngleSharp.Html.Parser;
using Fluxus.Infrastructure.Geometry;
using Fluxus.Infrastructure.Sources;

namespace Fluxus.Api;

public static class SourceValidation
{
    public static async Task RunAsync(string fixturePath, CancellationToken ct)
    {
        Directory.CreateDirectory(fixturePath);
        var options = new SourceOptions { Mode = "Live" };
        var parser = new AuctionParser();
        using var auctionHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        auctionHttp.DefaultRequestHeaders.UserAgent.ParseAdd("FluxusPrototype/0.1");
        using var geometryHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var source = new EvarzytynesSource(auctionHttp, parser, options);
        var provider = new GeoportalProvider(geometryHttp, options);
        var listingHtml = await source.FetchAsync(EvarzytynesSource.ListingUrl(0), ct);
        var listing = parser.ParseListing(listingHtml, 0);
        // First nine offer rural land, fractions and mixed bundles; include a multi-land lot too.
        var multi = listing.Items.FirstOrDefault(i => i.Summary.Split("Žemės sklypas").Length > 2);
        var selected = listing.Items.Take(9).Concat(multi is null ? listing.Items.Skip(9).Take(1) : [multi]).DistinctBy(i => i.SourceId).ToArray();
        var rows = new List<object>();
        var cards = new List<string>();
        foreach (var item in selected)
        {
            var html = await source.FetchAsync(item.SourceUrl, ct);
            var parsed = parser.ParseDetail(html, item);
            var sanitized = parser.SanitizeDetail(html);
            var sanitizedParsed = parser.ParseDetail(sanitized, item);
            if (sanitizedParsed.Items.Count != parsed.Items.Count) throw new InvalidOperationException("Sanitization changed land sections.");
            await File.WriteAllTextAsync(Path.Combine(fixturePath, $"auction-{item.SourceId}.html"), sanitized, ct);
            cards.Add($"<li><h2 class=\"no\"><a href=\"{item.SourceUrl.PathAndQuery}\">Nr. {item.Number}</a></h2><div class=\"list_box\">Žemės sklypas</div></li>");
            foreach (var land in parsed.Items)
            {
                var resolution = new Fluxus.Core.ParcelResolution("missing_identifier");
                if (land.UniqueNumber is not null || land.CadastralNumber is not null)
                {
                    try
                    {
                        var json = await provider.FetchGeometryAsync(land, ct);
                        resolution = GeoportalProvider.Parse(json, land);
                        await File.WriteAllTextAsync(Path.Combine(fixturePath, $"parcel-{land.UniqueNumber ?? land.CadastralNumber?.Replace('/', '-').Replace(':', '-')}.json"), json, ct);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                    { resolution = new("provider_unavailable"); }
                }
                rows.Add(new
                {
                    parsed.SourceId,
                    parsed.Number,
                    land.SourceKey,
                    land.UniqueNumber,
                    land.CadastralNumber,
                    land.Address,
                    land.TotalAreaHa,
                    land.SoldAreaHa,
                    land.ShareRaw,
                    parsed.ContainsNonLandAssets,
                    resolution.Status,
                    land.Issues
                });
                Console.WriteLine($"{parsed.Number} / {land.SourceKey}: {resolution.Status}");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(fixturePath, "listing.html"), $"<!doctype html><html><body><ul class=\"bid_list\">{string.Join("", cards)}</ul></body></html>", ct);
        var broadHtml = await source.FetchAsync(EvarzytynesSource.ListingUrl(0, true), ct);
        var broad = parser.ParseListing(broadHtml, 0);
        using var listingDom = new HtmlParser().ParseDocument(listingHtml);
        var pages = listingDom.QuerySelectorAll("[data-page]").Select(e => e.GetAttribute("data-page")).Distinct().ToArray();
        await File.WriteAllTextAsync(Path.Combine(fixturePath, "validation.json"), JsonSerializer.Serialize(new
        {
            observedAt = DateTimeOffset.UtcNow,
            sampleAuctions = selected.Length,
            listingCount = listing.Items.Count,
            listing.NextPage,
            paginationValues = pages,
            broadFirstPageIds = broad.Items.Select(i => i.SourceId),
            landFirstPageIds = listing.Items.Select(i => i.SourceId),
            items = rows
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
        Console.WriteLine($"Validated {selected.Length} auctions; report: {fixturePath}/validation.json");
    }
}
