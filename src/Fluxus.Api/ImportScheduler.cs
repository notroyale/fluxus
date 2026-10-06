using Fluxus.Core;
using Fluxus.Infrastructure.Sources;

namespace Fluxus.Api;

public sealed class ImportScheduler(IServiceScopeFactory scopes, SourceOptions options, ILogger<ImportScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.ScheduledImportsEnabled || options.Mode != "Live") return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.IntervalMinutes));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                budget.CancelAfter(TimeSpan.FromMinutes(Math.Min(45, options.IntervalMinutes)));
                var summary = await scope.ServiceProvider.GetRequiredService<ImportCoordinator>()
                    .RunAsync(options.Mode, options.MaxAuctions, options.MaxPages, budget.Token);
                logger.LogInformation("Import completed: {Auctions} auctions, {Items} items, {Mapped} mapped; discovery complete {Complete}",
                    summary.Auctions, summary.Items, summary.Mapped, summary.DiscoveryComplete);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning("Import failed ({Reason}); retaining the previous catalogue", exception.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
