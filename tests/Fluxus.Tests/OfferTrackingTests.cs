using System.Net;
using System.Text.Json;
using Fluxus.Core;
using Fluxus.Infrastructure.Persistence;
using Fluxus.Infrastructure.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fluxus.Tests;

public sealed class OfferTrackingTests(TestCatalogue catalogue) : IClassFixture<TestCatalogue>
{
    private static ParsedAuction Input(string sourceId, params LandItem[] items) => new(sourceId, "tracking-test",
        "https://www.evarzytynes.lt/evs/pages/auction.do?id=338057&number=337502", "running",
        DateTimeOffset.Parse("2026-10-01T00:00:00Z"), DateTimeOffset.Parse("2026-11-01T00:00:00Z"),
        10000m, null, false, items);

    private static LandItem Land(string key) => new(key, null, null, "Tracking test", 4m, 2m, 1, 2, "1/2", null, null, [])
        { OfferType = "sale" };

    private static Task Save(ICatalogueStore store, ParsedAuction input) => store.SaveAuctionAsync(input,
        input.Items.ToDictionary(i => i.SourceKey, _ => new ParcelResolution("missing_identifier")), CancellationToken.None);

    [Fact]
    public async Task ChangesKeepPreviousValuesAndUnchangedRefreshesDoNotDuplicateHistory()
    {
        using var scope = catalogue.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogueDb>();
        var store = scope.ServiceProvider.GetRequiredService<ICatalogueStore>();
        var options = scope.ServiceProvider.GetRequiredService<SourceOptions>();
        var originalTime = options.FixtureObservedAt;
        var input = Input("change-history-test", Land("one"));
        try
        {
            await Save(store, input);
            await Save(store, input);
            var auctionId = await db.Auctions.Where(a => a.SourceId == input.SourceId).Select(a => a.Id).SingleAsync();
            Assert.Equal(1, await db.AuctionObservations.CountAsync(o => o.AuctionId == auctionId));
            options.FixtureObservedAt = originalTime!.Value.AddHours(1);
            var changed = input with { InitialPriceEur = 8000m, LastBidEur = 8100m, Status = "ended", EndsAt = input.EndsAt!.Value.AddDays(1) };
            await Save(store, changed);
            await Save(store, changed);
            using var response = JsonDocument.Parse(await catalogue.Client.GetStringAsync($"/api/auctions/{auctionId}/history"));
            Assert.Equal(2, response.RootElement.GetProperty("total").GetInt32());
            var latest = response.RootElement.GetProperty("observations")[0];
            Assert.Equal("changed", latest.GetProperty("kind").GetString());
            Assert.Equal(10000m, latest.GetProperty("previousInitialPriceEur").GetDecimal());
            Assert.Equal(8000m, latest.GetProperty("initialPriceEur").GetDecimal());
            Assert.Equal("running", latest.GetProperty("previousStatus").GetString());
            Assert.True(latest.GetProperty("priceChanged").GetBoolean());
            Assert.True(latest.GetProperty("bidChanged").GetBoolean());
            Assert.True(latest.GetProperty("statusChanged").GetBoolean());
            Assert.True(latest.GetProperty("deadlineChanged").GetBoolean());
            using var map = JsonDocument.Parse(await catalogue.Client.GetStringAsync("/api/catalog/map?status=ended"));
            var card = Assert.Single(map.RootElement.GetProperty("items").EnumerateArray(), i => i.GetProperty("auctionId").GetGuid() == auctionId);
            Assert.Equal(10000m, card.GetProperty("previousInitialPriceEur").GetDecimal());
            Assert.Equal("running", card.GetProperty("previousStatus").GetString());
            Assert.NotEqual(JsonValueKind.Null, card.GetProperty("priceChangedAt").ValueKind);
            options.FixtureObservedAt = originalTime.Value.AddHours(2);
            await Save(store, changed with { InitialPriceEur = null });
            var row = await db.Auctions.AsNoTracking().SingleAsync(a => a.Id == auctionId);
            Assert.Null(row.InitialPriceEur);
            Assert.Equal(8000m, row.PreviousInitialPriceEur);
            Assert.Equal(3, await db.AuctionObservations.CountAsync(o => o.AuctionId == auctionId));
        }
        finally
        {
            options.FixtureObservedAt = originalTime;
            await db.Auctions.Where(a => a.SourceId == input.SourceId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task DetailOmissionRetainsIdentityForShortlistAndReappearanceKeepsFirstSeen()
    {
        using var scope = catalogue.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogueDb>();
        var store = scope.ServiceProvider.GetRequiredService<ICatalogueStore>();
        var input = Input("retained-offer-test", Land("one"), Land("two"));
        try
        {
            await Save(store, input);
            var auction = await db.Auctions.AsNoTracking().Include(a => a.Items).SingleAsync(a => a.SourceId == input.SourceId);
            var saved = auction.Items.Single(i => i.SourceKey == "two");
            await Save(store, input with { Items = [input.Items[0]] });
            var omitted = await db.Items.AsNoTracking().Include(i => i.Auction).SingleAsync(i => i.Id == saved.Id);
            Assert.False(omitted.IsPresent);
            Assert.NotNull(omitted.MissingSince);
            Assert.Equal("running", omitted.Auction.Status);
            using var map = JsonDocument.Parse(await catalogue.Client.GetStringAsync("/api/catalog/map"));
            Assert.DoesNotContain(map.RootElement.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == saved.Id);
            var unknown = Guid.NewGuid();
            var otherId = auction.Items.Single(i => i.SourceKey == "one").Id;
            using var shortlist = JsonDocument.Parse(await catalogue.Client.GetStringAsync($"/api/catalog/shortlist?ids={saved.Id},{otherId},{unknown}"));
            Assert.Equal(saved.Id, shortlist.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
            Assert.False(shortlist.RootElement.GetProperty("items")[0].GetProperty("isPresent").GetBoolean());
            Assert.Equal(unknown, shortlist.RootElement.GetProperty("missingIds")[0].GetGuid());
            await Save(store, input);
            var returned = await db.Items.AsNoTracking().SingleAsync(i => i.Id == saved.Id);
            Assert.True(returned.IsPresent);
            Assert.Null(returned.MissingSince);
            Assert.Equal(saved.FirstSeenAt, returned.FirstSeenAt);
            Assert.Equal(2, await db.Items.CountAsync(i => i.AuctionId == auction.Id));
            Assert.Equal(3, await db.AuctionObservations.CountAsync(o => o.AuctionId == auction.Id));
        }
        finally { await db.Auctions.Where(a => a.SourceId == input.SourceId).ExecuteDeleteAsync(); }
    }

    [Fact]
    public async Task PerHectareComparisonNeverUsesAnEntireBundleOrRentalPrice()
    {
        using var scope = catalogue.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogueDb>();
        var store = scope.ServiceProvider.GetRequiredService<ICatalogueStore>();
        var land = Land("one");
        var inputs = new[]
        {
            Input("comparison-single", land),
            Input("comparison-bundle", land, Land("two")),
            Input("comparison-mixed", land) with { ContainsNonLandAssets = true },
            Input("comparison-component", land with { ComponentPriceEur = 2000m }, Land("two")),
            Input("comparison-rent", land with { OfferType = "rent", ComponentPriceEur = 2000m }),
            Input("comparison-conflict", land with { SourceSoldAreaHa = 3m }),
        };
        try
        {
            foreach (var input in inputs) await Save(store, input);
            foreach (var input in inputs)
            {
                var id = await db.Items.Where(i => i.Auction.SourceId == input.SourceId && i.SourceKey == "one").Select(i => i.Id).SingleAsync();
                using var response = JsonDocument.Parse(await catalogue.Client.GetStringAsync($"/api/catalog/shortlist?ids={id}"));
                var price = response.RootElement.GetProperty("items")[0].GetProperty("pricePerHaEur");
                if (input.SourceId == "comparison-single") Assert.Equal(5000m, price.GetDecimal());
                else if (input.SourceId == "comparison-component") Assert.Equal(1000m, price.GetDecimal());
                else Assert.Equal(JsonValueKind.Null, price.ValueKind);
            }
        }
        finally
        {
            var sources = inputs.Select(i => i.SourceId).ToArray();
            await db.Auctions.Where(a => sources.Contains(a.SourceId)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task TrackingReadsAreBoundedAndRejectInvalidShortlists()
    {
        var id = Guid.NewGuid();
        foreach (var ids in new[] { "", "invalid", $"{id},{id}", string.Join(',', Enumerable.Range(0, 5).Select(_ => Guid.NewGuid())) })
            Assert.Equal(HttpStatusCode.BadRequest, (await catalogue.Client.GetAsync($"/api/catalog/shortlist?ids={ids}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await catalogue.Client.GetAsync($"/api/auctions/{id}/history")).StatusCode);
        using var scope = catalogue.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogueDb>();
        var input = Input("bounded-history-test", Land("one"));
        try
        {
            await Save(scope.ServiceProvider.GetRequiredService<ICatalogueStore>(), input);
            var auctionId = await db.Auctions.Where(a => a.SourceId == input.SourceId).Select(a => a.Id).SingleAsync();
            var at = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
            db.AuctionObservations.AddRange(Enumerable.Range(0, 51).Select(n => new AuctionObservationEntity
            {
                AuctionId = auctionId, ObservedAt = at.AddMinutes(n), Kind = "changed", InitialPriceEur = n, PriceChanged = true
            }));
            await db.SaveChangesAsync();
            using var response = JsonDocument.Parse(await catalogue.Client.GetStringAsync($"/api/auctions/{auctionId}/history"));
            Assert.Equal(52, response.RootElement.GetProperty("total").GetInt32());
            Assert.True(response.RootElement.GetProperty("truncated").GetBoolean());
            Assert.Equal(50, response.RootElement.GetProperty("observations").GetArrayLength());
            Assert.Equal(50m, response.RootElement.GetProperty("observations")[0].GetProperty("initialPriceEur").GetDecimal());
        }
        finally { await db.Auctions.Where(a => a.SourceId == input.SourceId).ExecuteDeleteAsync(); }
    }

    [Fact]
    public async Task OverdueRechecksConfirmStatusAndFailedReadsRetainTheSavedState()
    {
        using var scope = catalogue.Factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<CatalogueDb>();
        var store = services.GetRequiredService<ICatalogueStore>();
        var options = services.GetRequiredService<SourceOptions>();
        var input = Input("overdue-recheck-test", Land("one")) with { EndsAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var originalMode = options.Mode;
        var originalTime = options.FixtureObservedAt;
        try
        {
            options.FixtureObservedAt = DateTimeOffset.UtcNow.AddHours(-2);
            await Save(store, input);
            var auctionId = await db.Auctions.Where(a => a.SourceId == input.SourceId).Select(a => a.Id).SingleAsync();
            await db.Auctions.Where(a => a.Id == auctionId).ExecuteUpdateAsync(s => s.SetProperty(a => a.Mode, "Live"));
            options.Mode = "Live";
            var candidates = services.GetRequiredService<IAuctionRecheckCandidates>();
            Assert.Single(await candidates.GetRecheckCandidatesAsync(20, CancellationToken.None));
            var provider = services.GetRequiredService<IParcelProvider>();
            // A failed detail request leaves the previous status, price, and history intact.
            var failed = await new ImportCoordinator(new RecheckSource(input, true), provider, store, candidates)
                .RunAsync("Live", 10, 1, CancellationToken.None);
            Assert.Equal(1, failed.FailedAuctions);
            Assert.Equal("running", await db.Auctions.Where(a => a.Id == auctionId).Select(a => a.Status).SingleAsync());
            Assert.Equal(1, await db.AuctionObservations.CountAsync(o => o.AuctionId == auctionId));
            var confirmed = await new ImportCoordinator(new RecheckSource(input with { Status = "ended" }, false), provider, store, candidates)
                .RunAsync("Live", 10, 1, CancellationToken.None);
            Assert.Equal(1, confirmed.Auctions);
            Assert.Equal("ended", await db.Auctions.Where(a => a.Id == auctionId).Select(a => a.Status).SingleAsync());
            Assert.Equal(2, await db.AuctionObservations.CountAsync(o => o.AuctionId == auctionId));
            Assert.Empty(await candidates.GetRecheckCandidatesAsync(20, CancellationToken.None));
        }
        finally
        {
            options.Mode = originalMode; options.FixtureObservedAt = originalTime;
            await db.Auctions.Where(a => a.SourceId == input.SourceId).ExecuteDeleteAsync();
        }
    }

    private sealed class RecheckSource(ParsedAuction auction, bool fail) : IAuctionSource
    {
        public Task<ListingPage> GetListingAsync(int page, bool broad, CancellationToken ct) =>
            Task.FromResult(new ListingPage([], null, true));
        public Task<ParsedAuction> GetDetailAsync(ListingItem item, CancellationToken ct) =>
            fail ? throw new HttpRequestException("Test source failure") : Task.FromResult(auction);
    }
}
