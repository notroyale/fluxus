using System.Threading.Channels;
using Fluxus.Core;
using Fluxus.Infrastructure.Sources;

namespace Fluxus.Api;

public sealed record CatalogueRefreshStatus(bool Enabled, int MaxAuctions, Guid? Id = null,
    string Status = "idle", string? Phase = null, DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null, DateTimeOffset? RetryAt = null, int Auctions = 0,
    int Items = 0, int Mapped = 0, int FailedAuctions = 0, bool? DiscoveryComplete = null,
    string? ErrorCode = null);

// One in-process queue; the importer's database lock also excludes scheduled/CLI imports.
public sealed class CatalogueRefresh(SourceOptions options, TimeProvider clock) : IProgress<ImportProgress>
{
    private readonly object gate = new();
    private readonly Channel<Guid> queue = Channel.CreateBounded<Guid>(1);
    private CatalogueRefreshStatus current = new(options.ManualImportsEnabled && options.Mode == "Live", options.MaxAuctions);

    public CatalogueRefreshStatus Snapshot { get { lock (gate) return current; } }

    public string Start()
    {
        lock (gate)
        {
            if (!current.Enabled) return "disabled";
            if (current.Status is "queued" or "running") return "accepted";
            if (current.RetryAt > clock.GetUtcNow()) return "cooldown";
            var id = Guid.NewGuid();
            current = new(true, options.MaxAuctions, id, "queued", "listing", clock.GetUtcNow());
            if (!queue.Writer.TryWrite(id)) throw new InvalidOperationException("Refresh queue was unexpectedly full.");
            return "accepted";
        }
    }

    public ValueTask<Guid> ReadAsync(CancellationToken ct) => queue.Reader.ReadAsync(ct);

    public void MarkRunning()
    {
        lock (gate) current = current with { Status = "running" };
    }

    public void Report(ImportProgress progress)
    {
        lock (gate) current = current with
        {
            Phase = progress.Phase, Auctions = progress.Auctions, Items = progress.Items,
            Mapped = progress.Mapped, FailedAuctions = progress.FailedAuctions
        };
    }

    public void Finish(ImportSummary? summary, string? errorCode = null)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            current = current with
            {
                Status = errorCode is not null || summary is { Auctions: 0, DiscoveryComplete: false } ? "failed" : "completed",
                Phase = null, FinishedAt = now, RetryAt = now.AddMinutes(1),
                Auctions = summary?.Auctions ?? current.Auctions, Items = summary?.Items ?? current.Items,
                Mapped = summary?.Mapped ?? current.Mapped, FailedAuctions = summary?.FailedAuctions ?? current.FailedAuctions,
                DiscoveryComplete = summary?.DiscoveryComplete, ErrorCode = errorCode ?? (summary is { Auctions: 0, DiscoveryComplete: false } ? "no_auctions" : null)
            };
        }
    }
}

public sealed class CatalogueRefreshWorker(IServiceScopeFactory scopes, CatalogueRefresh refresh,
    SourceOptions options, ILogger<CatalogueRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await refresh.ReadAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            refresh.MarkRunning();
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                budget.CancelAfter(TimeSpan.FromMinutes(45));
                var summary = await scope.ServiceProvider.GetRequiredService<ImportCoordinator>()
                    .RunAsync(options.Mode, options.MaxAuctions, options.MaxPages, budget.Token, refresh);
                refresh.Finish(summary);
                logger.LogInformation("Manual catalogue refresh completed: {Auctions} auctions, {Failed} failed, discovery complete {Complete}",
                    summary.Auctions, summary.FailedAuctions, summary.DiscoveryComplete);
            }
            catch (ImportAlreadyRunningException) { refresh.Finish(null, "import_busy"); }
            catch (OperationCanceledException)
            {
                refresh.Finish(null, stoppingToken.IsCancellationRequested ? "interrupted" : "timed_out");
            }
            catch (Exception exception)
            {
                refresh.Finish(null, "source_failed");
                logger.LogWarning("Manual catalogue refresh failed ({Reason}); accepted records are retained", exception.GetType().Name);
            }
        }
    }
}

public static class CatalogueRefreshEndpoints
{
    public static void MapCatalogueRefresh(this WebApplication app)
    {
        app.MapGet("/api/catalog/refresh", (CatalogueRefresh refresh, HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(refresh.Snapshot);
        });
        app.MapPost("/api/catalog/refresh", (HttpRequest request, HttpResponse response, CatalogueRefresh refresh) =>
        {
            // No CORS is enabled. The custom header prevents cross-site form submissions.
            if (request.Headers["X-Fluxus-Refresh"] != "1" ||
                request.Headers.Origin is { Count: > 0 } origin &&
                (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri) || uri.Authority != request.Host.Value))
                return Results.Problem("Refresh must be requested by this application.", statusCode: 403);
            response.Headers.CacheControl = "no-store";
            var outcome = refresh.Start();
            if (outcome == "disabled") return Results.Problem("Live manual imports are disabled.", statusCode: 403);
            if (outcome == "cooldown")
            {
                response.Headers.RetryAfter = "60";
                return Results.Json(refresh.Snapshot, statusCode: 429);
            }
            return Results.Accepted("/api/catalog/refresh", refresh.Snapshot);
        });
    }
}
