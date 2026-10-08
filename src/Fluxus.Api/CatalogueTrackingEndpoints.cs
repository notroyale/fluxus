using Fluxus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fluxus.Api;

public static class CatalogueTrackingEndpoints
{
    public static void MapCatalogueTracking(this WebApplication app)
    {
        app.MapGet("/api/catalog/shortlist", async (string ids, CatalogueDb db, CancellationToken ct) =>
        {
            var parts = ids.Split(',');
            if (parts.Length is < 1 or > 4 || parts.Any(p => !Guid.TryParse(p, out _)))
                return Results.Problem("Select between one and four valid offer IDs.", statusCode: 400);
            var selected = parts.Select(Guid.Parse).ToArray();
            if (selected.Distinct().Count() != selected.Length)
                return Results.Problem("Offer IDs must be unique.", statusCode: 400);
            var items = await db.Items.AsNoTracking().Include(i => i.Auction).Include(i => i.Parcel!)
                .ThenInclude(p => p.CurrentLandscapeAnalysis!).ThenInclude(a => a.Dataset)
                .Where(i => selected.Contains(i.Id)).ToListAsync(ct);
            var cards = items.ToDictionary(i => i.Id, CatalogueEndpoints.Card);
            return Results.Ok(new
            {
                items = selected.Where(cards.ContainsKey).Select(id => cards[id]),
                missingIds = selected.Where(id => !cards.ContainsKey(id))
            });
        });

        app.MapGet("/api/auctions/{id:guid}/history", async (Guid id, CatalogueDb db, CancellationToken ct) =>
        {
            if (!await db.Auctions.AnyAsync(a => a.Id == id, ct)) return Results.NotFound();
            var count = await db.AuctionObservations.CountAsync(o => o.AuctionId == id, ct);
            var history = await db.AuctionObservations.AsNoTracking().Where(o => o.AuctionId == id)
                .OrderByDescending(o => o.ObservedAt).ThenByDescending(o => o.Id).Take(50)
                .Select(o => new
                {
                    o.Id, o.ObservedAt, fixtureMode = o.Mode == "Fixture", o.Kind, o.Status, o.PreviousStatus,
                    o.InitialPriceEur, o.PreviousInitialPriceEur, o.LastBidEur, o.PreviousLastBidEur,
                    o.EndsAt, o.PreviousEndsAt, o.OfferCount, o.PriceChanged, o.BidChanged,
                    o.StatusChanged, o.DeadlineChanged, o.OffersChanged
                }).ToArrayAsync(ct);
            return Results.Ok(new { auctionId = id, observations = history, total = count, truncated = count > history.Length });
        });
    }
}
