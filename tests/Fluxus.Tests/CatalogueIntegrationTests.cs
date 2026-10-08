using System.Net;
using System.Text.Json;
using Fluxus.Core;
using Fluxus.Infrastructure.Persistence;
using Fluxus.Infrastructure.Sources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fluxus.Tests;

public sealed class TestCatalogue : IAsyncLifetime
{
    private readonly string database = "fluxus_test_" + Guid.NewGuid().ToString("N");
    private readonly string adminConnection = Environment.GetEnvironmentVariable("FLUXUS_TEST_CONNECTION") ??
        "Host=127.0.0.1;Port=15432;Database=postgres;Username=fluxus;Password=local-development-only;SSL Mode=Disable;GSS Encryption Mode=Disable;Timeout=10";
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        // Identifier is generated locally from a fixed prefix and a GUID, never user input.
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await create.ExecuteNonQueryAsync();
        var connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = database }.ConnectionString;
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Testing")
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.RemoveAll<NpgsqlDataSource>();
                services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connection).UseNetTopologySuite().Build());
            }));
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CatalogueDb>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<ImportCoordinator>().RunAsync("Fixture", 10, 40, CancellationToken.None);
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null) await Factory.DisposeAsync();
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}

public sealed class CatalogueIntegrationTests(TestCatalogue catalogue) : IClassFixture<TestCatalogue>
{
    [Fact]
    public async Task SaleRentalAndOwnershipFiltersStayInSyncWithMapAndPriceSorting()
    {
        using var scope = catalogue.Factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<CatalogueDb>();
        var parser = services.GetRequiredService<AuctionParser>();
        var rental = parser.ParseDetail(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "auction-rental-338040.html")),
            new("338040", "337512", new Uri("https://www.evarzytynes.lt/evs/pages/auction.do?id=338040&number=337512"), ""))
            with
        { SourceId = "rental-filter-test" };
        var land = rental.Items[0] with { OfferType = "sale", ShareRaw = null, ShareNumerator = null, ShareDenominator = null };
        var missing = rental with { SourceId = "unknown-share-test", Number = "999998", InitialPriceEur = null, Items = [land] };
        var store = services.GetRequiredService<ICatalogueStore>();
        foreach (var input in new[] { rental, missing })
            await store.SaveAuctionAsync(input, new Dictionary<string, ParcelResolution> { [land.SourceKey] = new("not_found") }, CancellationToken.None);
        try
        {
            using var rents = JsonDocument.Parse(await catalogue.Client.GetStringAsync("/api/catalog/map?offerType=rent"));
            var rent = Assert.Single(rents.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("not_applicable", rent.GetProperty("ownershipKind").GetString());
            Assert.Equal(0.11m, rent.GetProperty("soldAreaHa").GetDecimal());
            Assert.Equal(8m, rent.GetProperty("initialPriceEur").GetDecimal());
            foreach (var kind in new[] { "full", "partial", "unknown" })
            {
                using var filtered = JsonDocument.Parse(await catalogue.Client.GetStringAsync($"/api/catalog/map?offerType=sale&ownershipKind={kind}"));
                var cards = filtered.RootElement.GetProperty("items").EnumerateArray().ToArray();
                Assert.NotEmpty(cards);
                Assert.All(cards, card =>
                {
                    Assert.Equal("sale", card.GetProperty("offerType").GetString());
                    Assert.Equal(kind, card.GetProperty("ownershipKind").GetString());
                });
                Assert.All(filtered.RootElement.GetProperty("features").EnumerateArray(), feature =>
                    Assert.Contains(cards, card => card.GetProperty("id").GetString() == feature.GetProperty("id").GetString()));
            }
            using var noOwnershipRental = JsonDocument.Parse(await catalogue.Client.GetStringAsync("/api/catalog/map?offerType=rent&ownershipKind=full"));
            Assert.Empty(noOwnershipRental.RootElement.GetProperty("items").EnumerateArray());
            foreach (var sort in new[] { "price_asc", "price_desc" })
            {
                using var sorted = JsonDocument.Parse(await catalogue.Client.GetStringAsync($"/api/catalog/map?offerType=all&sort={sort}"));
                var cards = sorted.RootElement.GetProperty("items").EnumerateArray().ToArray();
                Assert.Equal(JsonValueKind.Null, cards[^1].GetProperty("initialPriceEur").ValueKind);
                var prices = cards.Where(c => c.GetProperty("initialPriceEur").ValueKind != JsonValueKind.Null)
                    .Select(c => c.GetProperty("initialPriceEur").GetDecimal()).ToArray();
                Assert.Equal(sort == "price_asc" ? prices.Order().ToArray() : prices.OrderDescending().ToArray(), prices);
            }
            foreach (var invalid in new[] { "offerType=invalid", "ownershipKind=invalid", "sort=invalid" })
                Assert.Equal(HttpStatusCode.BadRequest, (await catalogue.Client.GetAsync($"/api/catalog/map?{invalid}")).StatusCode);
        }
        finally
        {
            await db.Auctions.Where(a => a.SourceId == rental.SourceId || a.SourceId == missing.SourceId).ExecuteDeleteAsync();
            await db.Parcels.Where(p => p.UniqueNumber == land.UniqueNumber && !db.Items.Any(i => i.ParcelId == p.Id)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task RepeatedImportsKeepStableOffersAndOneCanonicalParcelForTwoShares()
    {
        using var scope = catalogue.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogueDb>();
        var ids = await db.Items.OrderBy(i => i.Id).Select(i => i.Id).ToArrayAsync();
        var fetchedAt = await db.Parcels.OrderBy(p => p.Id).Select(p => p.GeometryFetchedAt).ToArrayAsync();
        var firstSeen = await db.Items.OrderBy(i => i.Id).Select(i => i.FirstSeenAt).ToArrayAsync();
        Assert.All(firstSeen, stamp => Assert.NotNull(stamp));
        Assert.All(await db.Items.Select(i => new { i.FirstSeenAt, i.Auction.ObservedAt }).ToArrayAsync(),
            item => Assert.Equal(item.ObservedAt, item.FirstSeenAt));
        var summary = await scope.ServiceProvider.GetRequiredService<ImportCoordinator>().RunAsync("Fixture", 10, 40, CancellationToken.None);
        Assert.Equal(10, summary.Auctions); Assert.Equal(11, summary.Items); Assert.Equal(11, summary.Mapped);
        Assert.Equal(ids, await db.Items.OrderBy(i => i.Id).Select(i => i.Id).ToArrayAsync());
        Assert.Equal(fetchedAt, await db.Parcels.OrderBy(p => p.Id).Select(p => p.GeometryFetchedAt).ToArrayAsync());
        Assert.Equal(firstSeen, await db.Items.OrderBy(i => i.Id).Select(i => i.FirstSeenAt).ToArrayAsync());
        Assert.Equal(10, await db.Parcels.CountAsync());
        var shares = await db.Items.Where(i => i.Auction.SourceId == "337928").ToArrayAsync();
        Assert.Equal(2, shares.Length); Assert.Single(shares.Select(i => i.ParcelId).Distinct());
        Assert.All(shares, item => Assert.Equal("1/2", item.Share));
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM parcels WHERE NOT ST_Covers(\"Geometry\", \"MarkerPoint\") OR ST_SRID(\"Geometry\") <> 4326";
        await db.Database.OpenConnectionAsync();
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task FailedDetailReadKeepsThePreviousAuctionAndMarksRunPartial()
    {
        using var scope = catalogue.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogueDb>();
        var saved = await db.Auctions.AsNoTracking().SingleAsync(a => a.SourceId == "337829");
        var observations = await db.AuctionObservations.CountAsync(o => o.AuctionId == saved.Id);
        var importer = new ImportCoordinator(new FailingSource(scope.ServiceProvider.GetRequiredService<IAuctionSource>()),
            scope.ServiceProvider.GetRequiredService<IParcelProvider>(), scope.ServiceProvider.GetRequiredService<ICatalogueStore>());
        var summary = await importer.RunAsync("Fixture", 10, 40, CancellationToken.None);
        Assert.Equal(1, summary.FailedAuctions); Assert.False(summary.DiscoveryComplete);
        var retained = await db.Auctions.AsNoTracking().SingleAsync(a => a.SourceId == saved.SourceId);
        Assert.Equal(saved.Id, retained.Id); Assert.Equal(saved.InitialPriceEur, retained.InitialPriceEur);
        Assert.Equal(saved.ObservedAt, retained.ObservedAt);
        Assert.Equal(observations, await db.AuctionObservations.CountAsync(o => o.AuctionId == saved.Id));
        Assert.All(await db.Items.Where(i => i.AuctionId == saved.Id).ToArrayAsync(), i => Assert.True(i.IsPresent));
        Assert.Equal("partial", (await db.ImportRuns.OrderByDescending(r => r.StartedAt).FirstAsync()).Outcome);
        Assert.Contains(await db.ImportIssues.ToListAsync(), issue => issue.SourceId == saved.SourceId);
    }

    [Fact]
    public async Task ApiFiltersAndGeometryPreserveAuctionPriceAndDistinctOffers()
    {
        using var map = JsonDocument.Parse(await catalogue.Client.GetStringAsync("/api/catalog/map"));
        var body = map.RootElement;
        Assert.Equal(10, body.GetProperty("meta").GetProperty("auctions").GetInt32());
        Assert.Equal(11, body.GetProperty("features").GetArrayLength());
        Assert.True(body.GetProperty("meta").GetProperty("fixtureMode").GetBoolean());
        var cards = body.GetProperty("items").EnumerateArray().ToArray();
        var mixed = cards.First(i => i.GetProperty("number").GetString() == "337179");
        using var detail = JsonDocument.Parse(await catalogue.Client.GetStringAsync($"/api/auctions/{mixed.GetProperty("auctionId").GetString()}"));
        Assert.True(detail.RootElement.GetProperty("containsNonLandAssets").GetBoolean());
        Assert.Equal(mixed.GetProperty("initialPriceEur").GetDecimal(), detail.RootElement.GetProperty("initialPriceEur").GetDecimal());
        var shares = cards.Where(i => i.GetProperty("number").GetString() == "337250").ToArray();
        using var first = JsonDocument.Parse(await catalogue.Client.GetStringAsync($"/api/auction-parcels/{shares[0].GetProperty("id").GetString()}/geometry"));
        using var second = JsonDocument.Parse(await catalogue.Client.GetStringAsync($"/api/auction-parcels/{shares[1].GetProperty("id").GetString()}/geometry"));
        Assert.Equal("MultiPolygon", first.RootElement.GetProperty("geometry").GetProperty("type").GetString());
        Assert.Equal(first.RootElement.GetProperty("geometry").GetRawText(), second.RootElement.GetProperty("geometry").GetRawText());
        using var filtered = JsonDocument.Parse(await catalogue.Client.GetStringAsync("/api/catalog/map?maxPriceEur=1"));
        Assert.Empty(filtered.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await catalogue.Client.GetAsync("/api/catalog/map?minSoldAreaHa=2&maxSoldAreaHa=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await catalogue.Client.GetAsync("/api/catalog/map?status=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await catalogue.Client.GetAsync($"/api/auction-parcels/{Guid.NewGuid()}/geometry")).StatusCode);
    }

    [Fact]
    public async Task RefreshPreservesOldAdditionDatesAndDoesNotInventDatesForLegacyOffers()
    {
        using var scope = catalogue.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogueDb>();
        var options = scope.ServiceProvider.GetRequiredService<SourceOptions>();
        var observed = options.FixtureObservedAt;
        var originals = await db.Items.OrderBy(i => i.Id).Take(2).Select(i => new { i.Id, i.FirstSeenAt }).ToArrayAsync();
        var old = observed!.Value.AddDays(-10);
        await db.Items.Where(i => i.Id == originals[0].Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.FirstSeenAt, old));
        await db.Items.Where(i => i.Id == originals[1].Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.FirstSeenAt, (DateTimeOffset?)null));
        try
        {
            options.FixtureObservedAt = observed.Value.AddDays(1);
            await scope.ServiceProvider.GetRequiredService<ImportCoordinator>().RunAsync("Fixture", 10, 40, CancellationToken.None);
            var refreshed = await db.Items.AsNoTracking().Include(i => i.Auction).Where(i => i.Id == originals[0].Id).SingleAsync();
            Assert.True(refreshed.Auction.ObservedAt > observed);
            Assert.Equal(old.ToUnixTimeMilliseconds(), refreshed.FirstSeenAt!.Value.ToUnixTimeMilliseconds());
            Assert.Null(await db.Items.Where(i => i.Id == originals[1].Id).Select(i => i.FirstSeenAt).SingleAsync());
            using var response = JsonDocument.Parse(await catalogue.Client.GetStringAsync("/api/catalog/map"));
            var card = response.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == originals[0].Id);
            Assert.Equal(old.ToUnixTimeMilliseconds(), card.GetProperty("firstSeenAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
        }
        finally
        {
            options.FixtureObservedAt = observed;
            foreach (var item in originals)
                await db.Items.Where(i => i.Id == item.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.FirstSeenAt, item.FirstSeenAt));
            await scope.ServiceProvider.GetRequiredService<ImportCoordinator>().RunAsync("Fixture", 10, 40, CancellationToken.None);
        }
    }

    [Fact]
    public async Task ImportLockBlocksConcurrentRunsAndIsReleased()
    {
        using var firstScope = catalogue.Factory.Services.CreateScope();
        using var secondScope = catalogue.Factory.Services.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<ICatalogueStore>();
        var second = secondScope.ServiceProvider.GetRequiredService<ICatalogueStore>();
        var lease = await first.TryAcquireImportLockAsync(CancellationToken.None);
        Assert.NotNull(lease);
        try { Assert.Null(await second.TryAcquireImportLockAsync(CancellationToken.None)); }
        finally { await lease.DisposeAsync(); }
        await using var reacquired = await second.TryAcquireImportLockAsync(CancellationToken.None);
        Assert.NotNull(reacquired);
    }

    [Fact]
    public async Task UnresolvedOfferRemainsInListWithoutInventedCoordinatesOrArea()
    {
        using var scope = catalogue.Factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<CatalogueDb>();
        var source = await services.GetRequiredService<IAuctionSource>().GetListingAsync(0, false, CancellationToken.None);
        var original = await services.GetRequiredService<IAuctionSource>().GetDetailAsync(source.Items[0], CancellationToken.None);
        var land = original.Items[0] with
        {
            UniqueNumber = null,
            CadastralNumber = null,
            TotalAreaHa = null,
            SourceSoldAreaHa = null,
            Issues = [new("missing_identifier", "parcel")]
        };
        var auction = original with { SourceId = "unresolved-test", Number = "999999", Items = [land] };
        await services.GetRequiredService<ICatalogueStore>().SaveAuctionAsync(auction,
            new Dictionary<string, ParcelResolution> { [land.SourceKey] = new("missing_identifier") }, CancellationToken.None);
        try
        {
            using var map = JsonDocument.Parse(await catalogue.Client.GetStringAsync("/api/catalog/map"));
            var item = map.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("number").GetString() == auction.Number);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("soldAreaHa").ValueKind);
            Assert.Equal(1, map.RootElement.GetProperty("meta").GetProperty("unresolved").GetInt32());
            var itemId = item.GetProperty("id").GetString();
            Assert.DoesNotContain(map.RootElement.GetProperty("features").EnumerateArray(), f => f.GetProperty("id").GetString() == itemId);
            using var geometry = JsonDocument.Parse(await catalogue.Client.GetStringAsync($"/api/auction-parcels/{itemId}/geometry"));
            Assert.Equal(JsonValueKind.Null, geometry.RootElement.GetProperty("geometry").ValueKind);
            using var filtered = JsonDocument.Parse(await catalogue.Client.GetStringAsync("/api/catalog/map?minSoldAreaHa=0.001"));
            Assert.DoesNotContain(filtered.RootElement.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == itemId);
        }
        finally
        {
            await db.Auctions.Where(a => a.SourceId == auction.SourceId).ExecuteDeleteAsync();
        }
    }

    private sealed class FailingSource(IAuctionSource source) : IAuctionSource
    {
        public Task<ListingPage> GetListingAsync(int page, bool broad, CancellationToken ct) => source.GetListingAsync(page, broad, ct);
        public Task<ParsedAuction> GetDetailAsync(ListingItem item, CancellationToken ct) => item.SourceId == "337829"
            ? throw new FormatException("Simulated source layout change.") : source.GetDetailAsync(item, ct);
    }
}
