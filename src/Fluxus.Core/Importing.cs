namespace Fluxus.Core;

public sealed record ParcelResolution(string Status, string? GeoJson = null,
    string? MunicipalityCode = null, string? MunicipalityName = null, string? Reason = null);

public interface IAuctionSource
{
    Task<ListingPage> GetListingAsync(int page, bool broad, CancellationToken cancellationToken);
    Task<ParsedAuction> GetDetailAsync(ListingItem item, CancellationToken cancellationToken);
}

public interface IParcelProvider
{
    Task<ParcelResolution> ResolveAsync(LandItem item, CancellationToken cancellationToken);
}

public interface IParcelBatchProvider
{
    // Results must preserve input order; each parcel is still matched and validated independently.
    Task<IReadOnlyList<ParcelResolution>> ResolveBatchAsync(IReadOnlyList<LandItem> items, CancellationToken cancellationToken);
}

public sealed record ImportSummary(int Auctions, int Items, int Mapped, int FailedAuctions, bool DiscoveryComplete);

public interface ICatalogueStore
{
    Task<IAsyncDisposable?> TryAcquireImportLockAsync(CancellationToken cancellationToken);
    Task BeginRunAsync(string mode, CancellationToken cancellationToken);
    Task<ParcelResolution?> GetCachedGeometryAsync(LandItem item, CancellationToken cancellationToken);
    Task SaveAuctionAsync(ParsedAuction auction, IReadOnlyDictionary<string, ParcelResolution> resolutions,
        CancellationToken cancellationToken);
    Task RecordIssueAsync(string sourceId, string code, CancellationToken cancellationToken);
    Task CompleteRunAsync(ImportSummary summary, CancellationToken cancellationToken);
}

public sealed class ImportCoordinator(IAuctionSource source, IParcelProvider provider, ICatalogueStore store)
{
    public async Task<ImportSummary> RunAsync(string mode, int maxAuctions, int maxPages, CancellationToken ct)
    {
        await using var importLock = await store.TryAcquireImportLockAsync(ct) ??
            throw new InvalidOperationException("Another import is already running.");
        await store.BeginRunAsync(mode, ct);
        var seen = new HashSet<string>();
        var resolved = new Dictionary<string, ParcelResolution>();
        var auctions = 0; var items = 0; var mapped = 0; var failed = 0; var complete = false;
        var pageNumber = 0;
        try
        {
            for (var pages = 0; pages < maxPages; pages++)
            {
                var page = await source.GetListingAsync(pageNumber, false, ct);
                var newItems = page.Items.Where(i => seen.Add(i.SourceId)).ToArray();
                if (newItems.Length == 0 && !page.IsEmpty)
                    throw new FormatException("Listing pagination repeated without new auction IDs.");
                var prepared = new List<(ListingItem Source, ParsedAuction Auction)>();
                foreach (var listingItem in newItems.Take(maxAuctions - auctions - failed))
                {
                    try
                    {
                        var auction = await source.GetDetailAsync(listingItem, ct);
                        foreach (var item in auction.Items)
                        {
                            var key = IdentifierKey(item);
                            if (!resolved.ContainsKey(key) && await store.GetCachedGeometryAsync(item, ct) is { } cached)
                                resolved[key] = cached;
                        }
                        prepared.Add((listingItem, auction));
                    }
                    catch (Exception exception) when (IsRecoverable(exception, ct))
                    {
                        failed++;
                        await store.RecordIssueAsync(listingItem.SourceId, exception.GetType().Name, ct);
                    }
                }
                var pending = prepared.SelectMany(p => p.Auction.Items).Where(i => !resolved.ContainsKey(IdentifierKey(i)))
                    .DistinctBy(IdentifierKey).ToArray();
                if (pending.Length > 0 && provider is IParcelBatchProvider batch)
                {
                    var results = await batch.ResolveBatchAsync(pending, ct);
                    if (results.Count != pending.Length) throw new InvalidOperationException("Parcel batch result count mismatch.");
                    for (var i = 0; i < pending.Length; i++) resolved[IdentifierKey(pending[i])] = results[i];
                }
                else
                    foreach (var item in pending) resolved[IdentifierKey(item)] = await provider.ResolveAsync(item, ct);
                foreach (var (listingItem, auction) in prepared)
                {
                    try
                    {
                        var resolutions = auction.Items.ToDictionary(i => i.SourceKey, i => resolved[IdentifierKey(i)]);
                        await store.SaveAuctionAsync(auction, resolutions, ct);
                        auctions++; items += auction.Items.Count;
                        mapped += resolutions.Values.Count(r => r.Status == "resolved_exact");
                    }
                    catch (Exception exception) when (IsRecoverable(exception, ct))
                    {
                        failed++;
                        await store.RecordIssueAsync(listingItem.SourceId, exception.GetType().Name, ct);
                    }
                }
                if (auctions + failed >= maxAuctions) break;
                if (page.NextPage is null) { complete = failed == 0; break; }
                pageNumber = page.NextPage.Value;
            }
        }
        finally
        {
            // Even cancelled/partial runs retain accepted records and a truthful outcome.
            await store.CompleteRunAsync(new(auctions, items, mapped, failed, complete), CancellationToken.None);
        }
        return new(auctions, items, mapped, failed, complete);
    }

    private static string IdentifierKey(LandItem item) => $"{item.UniqueNumber}|{item.CadastralNumber}";
    private static bool IsRecoverable(Exception exception, CancellationToken ct) =>
        exception is FormatException or HttpRequestException or TimeoutException ||
        exception is OperationCanceledException && !ct.IsCancellationRequested;
}
