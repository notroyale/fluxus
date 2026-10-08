using System.Net;
using System.Text.Json;
using Fluxus.Api;
using Fluxus.Core;
using Fluxus.Infrastructure.Geometry;
using Fluxus.Infrastructure.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fluxus.Tests;

public sealed class CatalogueRefreshTests(TestCatalogue catalogue) : IClassFixture<TestCatalogue>
{
    [Fact]
    public async Task FixtureModeCannotTriggerARefresh()
    {
        using var status = JsonDocument.Parse(await catalogue.Client.GetStringAsync("/api/catalog/refresh"));
        Assert.False(status.RootElement.GetProperty("enabled").GetBoolean());
        using var request = StartRequest();
        Assert.Equal(HttpStatusCode.Forbidden, (await catalogue.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task BackgroundRefreshIsSingleFlightAndReportsAcceptedRecordsWithoutClaimingCompleteDiscovery()
    {
        var fixtureOptions = catalogue.Factory.Services.GetRequiredService<SourceOptions>();
        var source = new BlockedSource(new EvarzytynesSource(new HttpClient(), new AuctionParser(), fixtureOptions));
        await using var factory = catalogue.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<SourceOptions>();
            services.AddSingleton(new SourceOptions { Mode = "Live", ManualImportsEnabled = true, MaxAuctions = 10 });
            services.RemoveAll<IAuctionSource>();
            services.AddSingleton<IAuctionSource>(source);
            services.RemoveAll<IParcelProvider>();
            services.AddSingleton<IParcelProvider>(new GeoportalProvider(new HttpClient(), fixtureOptions));
        }));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/catalog/refresh", null)).StatusCode);
        using var crossSite = StartRequest();
        crossSite.Headers.Add("Origin", "https://another-site.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(crossSite)).StatusCode);

        using var request = StartRequest();
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = accepted.RootElement.GetProperty("id").GetGuid();
        using var duplicate = StartRequest();
        using var secondResponse = await client.SendAsync(duplicate);
        using var second = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        Assert.Equal(id, second.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        source.Release.TrySetResult();

        var refresh = factory.Services.GetRequiredService<CatalogueRefresh>();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (refresh.Snapshot.Status is "queued" or "running") await Task.Delay(20, budget.Token);
        var final = refresh.Snapshot;
        Assert.Equal("completed", final.Status);
        Assert.Equal(10, final.Auctions);
        Assert.Equal(11, final.Items);
        Assert.False(final.DiscoveryComplete);
        Assert.NotNull(final.FinishedAt);
        Assert.Equal(1, source.ListingRequests);
        using var cooldown = StartRequest();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(cooldown)).StatusCode);
    }

    [Fact]
    public async Task SourceFailureLeavesTheCatalogueAvailableAndDoesNotReportSuccess()
    {
        await using var factory = catalogue.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<SourceOptions>();
            services.AddSingleton(new SourceOptions { Mode = "Live", ManualImportsEnabled = true, MaxAuctions = 10 });
            services.RemoveAll<IAuctionSource>();
            services.AddSingleton<IAuctionSource>(new FailingSource());
        }));
        using var client = factory.CreateClient();
        using var request = StartRequest();
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(request)).StatusCode);
        var refresh = factory.Services.GetRequiredService<CatalogueRefresh>();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (refresh.Snapshot.Status is "queued" or "running") await Task.Delay(20, budget.Token);
        Assert.Equal("failed", refresh.Snapshot.Status);
        Assert.Equal("source_failed", refresh.Snapshot.ErrorCode);
        using var catalog = JsonDocument.Parse(await client.GetStringAsync("/api/catalog/map"));
        Assert.Equal(10, catalog.RootElement.GetProperty("meta").GetProperty("auctions").GetInt32());
    }

    [Fact]
    public async Task RetryBecomesAvailableAfterCooldownAndProgressIsReset()
    {
        var clock = new RefreshClock();
        var refresh = new CatalogueRefresh(new SourceOptions { Mode = "Live", ManualImportsEnabled = true }, clock);
        Assert.Equal("accepted", refresh.Start());
        var first = refresh.Snapshot.Id;
        await refresh.ReadAsync(CancellationToken.None);
        refresh.MarkRunning();
        refresh.Report(new("saving", 4, 6, 5, 1));
        refresh.Finish(new(4, 6, 5, 1, false));
        Assert.Equal("cooldown", refresh.Start());
        clock.Now = clock.Now.AddMinutes(1);
        Assert.Equal("accepted", refresh.Start());
        Assert.NotEqual(first, refresh.Snapshot.Id);
        Assert.Equal(0, refresh.Snapshot.Auctions);
        Assert.Null(refresh.Snapshot.FinishedAt);
    }

    [Fact]
    public async Task ARunningExternalImportIsReportedWithoutStartingSourceRequests()
    {
        await using var factory = catalogue.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<SourceOptions>();
            services.AddSingleton(new SourceOptions { Mode = "Live", ManualImportsEnabled = true });
        }));
        using var scope = factory.Services.CreateScope();
        await using var lease = await scope.ServiceProvider.GetRequiredService<ICatalogueStore>()
            .TryAcquireImportLockAsync(CancellationToken.None);
        Assert.NotNull(lease);
        using var client = factory.CreateClient();
        using var request = StartRequest();
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(request)).StatusCode);
        var refresh = factory.Services.GetRequiredService<CatalogueRefresh>();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (refresh.Snapshot.Status is "queued" or "running") await Task.Delay(20, budget.Token);
        Assert.Equal("failed", refresh.Snapshot.Status);
        Assert.Equal("import_busy", refresh.Snapshot.ErrorCode);
    }

    [Fact]
    public void ACompleteEmptyListingIsSuccessfulRatherThanASourceFailure()
    {
        var refresh = new CatalogueRefresh(new SourceOptions { Mode = "Live", ManualImportsEnabled = true }, TimeProvider.System);
        refresh.Start();
        refresh.Finish(new(0, 0, 0, 0, true));
        Assert.Equal("completed", refresh.Snapshot.Status);
        Assert.Null(refresh.Snapshot.ErrorCode);
        Assert.True(refresh.Snapshot.DiscoveryComplete);
    }

    private static HttpRequestMessage StartRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/refresh");
        request.Headers.Add("X-Fluxus-Refresh", "1");
        return request;
    }
    private sealed class RefreshClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class BlockedSource(IAuctionSource source) : IAuctionSource
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ListingRequests { get; private set; }
        public async Task<ListingPage> GetListingAsync(int page, bool broad, CancellationToken ct)
        {
            ListingRequests++;
            await Release.Task.WaitAsync(ct);
            return await source.GetListingAsync(page, broad, ct);
        }
        public Task<ParsedAuction> GetDetailAsync(ListingItem item, CancellationToken ct) => source.GetDetailAsync(item, ct);
    }
    private sealed class FailingSource : IAuctionSource
    {
        public Task<ListingPage> GetListingAsync(int page, bool broad, CancellationToken ct) => throw new HttpRequestException("Test source unavailable");
        public Task<ParsedAuction> GetDetailAsync(ListingItem item, CancellationToken ct) => throw new NotSupportedException();
    }
}
