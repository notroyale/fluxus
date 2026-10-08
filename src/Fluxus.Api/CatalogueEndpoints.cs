using System.Text.Json;
using Fluxus.Infrastructure.Geometry;
using Fluxus.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Fluxus.Api;

public sealed class CatalogueFilters
{
    public string? Status { get; set; }
    public string? MunicipalityCode { get; set; }
    public decimal? MinPriceEur { get; set; }
    public decimal? MaxPriceEur { get; set; }
    public decimal? MinSoldAreaHa { get; set; }
    public decimal? MaxSoldAreaHa { get; set; }
    public DateTimeOffset? EndsAfter { get; set; }
    public DateTimeOffset? EndsBefore { get; set; }
    public string? OfferType { get; set; }
    public string? OwnershipKind { get; set; }
    public string? Sort { get; set; }
    public int? MinLandscapeScore { get; set; }
    public string? LandscapeStatus { get; set; }

    public string[] Statuses()
    {
        var statuses = (Status ?? "announced,running").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (statuses.Length == 0 || statuses.Any(s => s is not ("announced" or "running" or "ended" or "cancelled" or "unknown")))
            throw new ArgumentException("Unsupported auction status.");
        return statuses;
    }

    public void Validate()
    {
        _ = Statuses();
        if (OfferType is not (null or "all" or "sale" or "rent" or "unknown"))
            throw new ArgumentException("Unsupported offer type.");
        if (OwnershipKind is not (null or "all" or "full" or "partial" or "unknown"))
            throw new ArgumentException("Unsupported ownership kind.");
        if (Sort is not (null or "ending" or "price_asc" or "price_desc" or "landscape_asc" or "landscape_desc"))
            throw new ArgumentException("Unsupported sort order.");
        if (MinLandscapeScore is < 0 or > 100)
            throw new ArgumentException("Landscape score must be between 0 and 100.");
        if (LandscapeStatus is not (null or "all" or "ready" or "not_analyzed" or "queued" or "processing" or "partial" or "unavailable" or "failed"))
            throw new ArgumentException("Unsupported landscape status.");
        if (MinPriceEur < 0 || MaxPriceEur < 0 || MinSoldAreaHa < 0 || MaxSoldAreaHa < 0 ||
            MinPriceEur > MaxPriceEur || MinSoldAreaHa > MaxSoldAreaHa || EndsAfter > EndsBefore)
            throw new ArgumentException("Invalid filter range.");
        if (MunicipalityCode is { } code && (code.Length != 2 || !code.All(char.IsAsciiDigit)))
            throw new ArgumentException("Invalid municipality code.");
    }
}

public sealed record ItemCard(Guid Id, Guid AuctionId, string Number, string Status, string? Address,
    string? MunicipalityCode, string? MunicipalityName, decimal? InitialPriceEur, decimal? LastBidEur,
    DateTimeOffset? EndsAt, decimal? TotalAreaHa, decimal? SoldAreaHa, string SoldAreaBasis,
    string? Share, bool ContainsNonLandAssets, string ResolutionStatus, string OfferType, string OwnershipKind,
    LandscapeSummary Landscape, DateTimeOffset? FirstSeenAt, DateTimeOffset ObservedAt,
    bool IsPresent, DateTimeOffset? MissingSince, DateTimeOffset? PriceChangedAt,
    decimal? PreviousInitialPriceEur, DateTimeOffset? StatusChangedAt, string? PreviousStatus,
    decimal? PricePerHaEur, decimal? ComponentPriceEur, int AuctionOfferCount);

public static class CatalogueEndpoints
{
    public static void MapCatalogue(this WebApplication app)
    {
        app.MapGet("/api/catalog/map", async ([AsParameters] CatalogueFilters filters, CatalogueDb db, CancellationToken ct) =>
        {
            try { filters.Validate(); }
            catch (ArgumentException exception) { return Results.Problem(exception.Message, statusCode: 400); }
            var statuses = filters.Statuses();
            var query = db.Items.AsNoTracking().Include(i => i.Auction).Include(i => i.Parcel!)
                .ThenInclude(p => p.CurrentLandscapeAnalysis!).ThenInclude(a => a.Dataset)
                .Where(i => i.IsPresent && statuses.Contains(i.Auction.Status));
            if (filters.MunicipalityCode is { } municipality) query = query.Where(i => i.Parcel != null && i.Parcel.MunicipalityCode == municipality);
            if (filters.MinPriceEur is { } minPrice) query = query.Where(i => i.Auction.InitialPriceEur >= minPrice);
            if (filters.MaxPriceEur is { } maxPrice) query = query.Where(i => i.Auction.InitialPriceEur <= maxPrice);
            if (filters.MinSoldAreaHa is { } minArea) query = query.Where(i => i.SoldAreaHa >= minArea);
            if (filters.MaxSoldAreaHa is { } maxArea) query = query.Where(i => i.SoldAreaHa <= maxArea);
            if (filters.EndsAfter is { } after) query = query.Where(i => i.Auction.EndsAt >= after);
            if (filters.EndsBefore is { } before) query = query.Where(i => i.Auction.EndsAt <= before);
            if (filters.OfferType is { } offerType && offerType != "all") query = query.Where(i => i.OfferType == offerType);
            if (filters.OwnershipKind is { } ownership && ownership != "all")
                query = query.Where(i => i.OfferType == "sale" && i.OwnershipKind == ownership);
            if (filters.MinLandscapeScore is { } minScore)
                query = query.Where(i => i.ResolutionStatus == "resolved_exact" && i.Parcel != null &&
                    !i.Parcel.LandscapeIsStale && i.Parcel.LandscapeStatus == "ready" && i.Parcel.LandscapeScore >= minScore);
            if (filters.LandscapeStatus is { } landscapeStatus && landscapeStatus != "all")
                query = landscapeStatus == "unavailable"
                    ? query.Where(i => i.ResolutionStatus != "resolved_exact" || i.Parcel == null || i.Parcel.LandscapeStatus == landscapeStatus)
                    : query.Where(i => i.ResolutionStatus == "resolved_exact" && i.Parcel != null && i.Parcel.LandscapeStatus == landscapeStatus &&
                        (landscapeStatus != "ready" || !i.Parcel.LandscapeIsStale));
            // Sort by the same whole-auction starting price shown in the cards; missing prices always come last.
            var ordered = filters.Sort switch
            {
                "price_asc" => query.OrderBy(i => i.Auction.InitialPriceEur == null).ThenBy(i => i.Auction.InitialPriceEur),
                "price_desc" => query.OrderBy(i => i.Auction.InitialPriceEur == null).ThenByDescending(i => i.Auction.InitialPriceEur),
                "landscape_asc" => query.OrderBy(i => i.Parcel == null || i.Parcel.LandscapeScore == null || i.Parcel.LandscapeIsStale || i.ResolutionStatus != "resolved_exact")
                    .ThenBy(i => i.Parcel != null && !i.Parcel.LandscapeIsStale && i.ResolutionStatus == "resolved_exact" ? i.Parcel.LandscapeScore : null),
                "landscape_desc" => query.OrderBy(i => i.Parcel == null || i.Parcel.LandscapeScore == null || i.Parcel.LandscapeIsStale || i.ResolutionStatus != "resolved_exact")
                    .ThenByDescending(i => i.Parcel != null && !i.Parcel.LandscapeIsStale && i.ResolutionStatus == "resolved_exact" ? i.Parcel.LandscapeScore : null),
                _ => query.OrderBy(i => i.Auction.EndsAt == null).ThenBy(i => i.Auction.EndsAt)
            };
            var items = await ordered.ThenBy(i => i.AuctionId).ThenBy(i => i.Id).Take(5001).ToListAsync(ct);
            if (items.Count > 5000) return Results.Problem("Narrow the filters to fewer than 5,000 parcel offers.", statusCode: 413);
            var cards = items.Select(Card).ToArray();
            var features = items.Where(i => i.Parcel?.MarkerPoint is not null).Select(i => new
            {
                type = "Feature",
                id = i.Id,
                geometry = new { type = "Point", coordinates = new[] { i.Parcel!.MarkerPoint!.X, i.Parcel.MarkerPoint.Y } },
                properties = Card(i)
            });
            var lastRun = await db.ImportRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
            return Results.Ok(new
            {
                type = "FeatureCollection",
                features,
                items = cards,
                meta = new
                {
                    auctions = cards.Select(i => i.AuctionId).Distinct().Count(),
                    items = cards.Length,
                    mapped = items.Count(i => i.Parcel?.MarkerPoint is not null),
                    unresolved = items.Count(i => i.Parcel?.MarkerPoint is null),
                    fixtureMode = items.Any(i => i.Auction.Mode == "Fixture") || lastRun?.Mode == "Fixture",
                    observedAt = items.Count > 0 ? items.Min(i => i.Auction.ObservedAt) : (DateTimeOffset?)null,
                    lastImportAt = lastRun?.FinishedAt,
                    lastImportOutcome = lastRun?.Outcome,
                    discoveryComplete = lastRun?.DiscoveryComplete ?? false
                }
            });
        });

        app.MapGet("/api/auctions/{id:guid}", async (Guid id, CatalogueDb db, CancellationToken ct) =>
        {
            var auction = await db.Auctions.AsNoTracking().Include(a => a.Items).ThenInclude(i => i.Parcel!)
                .ThenInclude(p => p.CurrentLandscapeAnalysis!).ThenInclude(a => a.Dataset).SingleOrDefaultAsync(a => a.Id == id, ct);
            return auction is null ? Results.NotFound() : Results.Ok(new
            {
                auction.Id,
                auction.Number,
                auction.SourceUrl,
                auction.Status,
                auction.StartsAt,
                auction.EndsAt,
                auction.InitialPriceEur,
                auction.LastBidEur,
                auction.ContainsNonLandAssets,
                auction.ObservedAt,
                fixtureMode = auction.Mode == "Fixture",
                items = auction.Items.Select(i => new
                {
                    card = Card(i),
                    i.UniqueNumber,
                    i.CadastralNumber,
                    i.LandUse,
                    i.ComponentPriceEur,
                    issues = JsonSerializer.Deserialize<JsonElement>(i.Issues)
                })
            });
        });

        app.MapGet("/api/auction-parcels/{id:guid}/geometry", async (Guid id, CatalogueDb db, CancellationToken ct) =>
        {
            var item = await db.Items.AsNoTracking().Include(i => i.Parcel).SingleOrDefaultAsync(i => i.Id == id, ct);
            if (item is null) return Results.NotFound();
            return Results.Ok(new
            {
                item.Id,
                item.ResolutionStatus,
                geometry = item.Parcel?.Geometry is { } geometry ? JsonSerializer.Deserialize<JsonElement>(ArcGisGeometry.Write(geometry)) : (JsonElement?)null,
                geometryFetchedAt = item.Parcel?.GeometryFetchedAt,
                provider = "Geoportal / Registrų centras"
            });
        });

        app.MapGet("/api/catalog/filters", async (CatalogueDb db, CancellationToken ct) => Results.Ok(await db.Parcels.AsNoTracking()
            .Where(p => p.MunicipalityCode != null).Select(p => new { code = p.MunicipalityCode, name = p.MunicipalityName })
            .Distinct().OrderBy(p => p.name).ToListAsync(ct)));

        app.MapGet("/api/catalog/status", async (CatalogueDb db, CancellationToken ct) => Results.Ok(await db.ImportRuns.AsNoTracking()
            .OrderByDescending(r => r.StartedAt).Select(r => new
            {
                r.Id,
                r.Mode,
                r.StartedAt,
                r.FinishedAt,
                r.Outcome,
                r.Auctions,
                r.Items,
                r.Mapped,
                r.FailedAuctions,
                r.DiscoveryComplete
            }).FirstOrDefaultAsync(ct)));
    }

    internal static ItemCard Card(AuctionItemEntity item) => new(item.Id, item.AuctionId, item.Auction.Number,
        item.Auction.Status, item.Address, item.Parcel?.MunicipalityCode, item.Parcel?.MunicipalityName,
        item.Auction.InitialPriceEur, item.Auction.LastBidEur, item.Auction.EndsAt, item.TotalAreaHa, item.SoldAreaHa,
        item.SoldAreaBasis, item.Share, item.Auction.ContainsNonLandAssets, item.ResolutionStatus, item.OfferType, item.OwnershipKind,
        LandscapeEndpoints.Summary(item), item.FirstSeenAt, item.Auction.ObservedAt,
        item.IsPresent, item.MissingSince, item.Auction.PriceChangedAt, item.Auction.PreviousInitialPriceEur,
        item.Auction.StatusChangedAt, item.Auction.PreviousStatus, PricePerHa(item), item.ComponentPriceEur, item.Auction.OfferCount);

    private static decimal? PricePerHa(AuctionItemEntity item)
    {
        if (!item.IsPresent || item.OfferType != "sale" || item.SoldAreaHa is not > 0 || item.SoldAreaBasis == "conflict") return null;
        // Never attribute the entire bundle price to an individual land share.
        var price = item.ComponentPriceEur ?? (item.Auction.OfferCount == 1 && !item.Auction.ContainsNonLandAssets
            ? item.Auction.InitialPriceEur : null);
        return price is >= 0 ? decimal.Round(price.Value / item.SoldAreaHa.Value, 2) : null;
    }
}
