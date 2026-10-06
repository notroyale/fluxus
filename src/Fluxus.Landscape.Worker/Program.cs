using Fluxus.Infrastructure.Landscape;
using Fluxus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Fluxus.Landscape.Worker;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
var ct = shutdown.Token;
var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Catalogue") ??
    "Host=127.0.0.1;Port=15432;Database=fluxus;Username=fluxus;Password=local-development-only;SSL Mode=Disable;GSS Encryption Mode=Disable";
var inputRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("Landscape__InputRoot") ?? ".local/landscape-inputs");
var artifactRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("Landscape__ArtifactRoot") ?? ".local/landscape-artifacts");
var dbOptions = new DbContextOptionsBuilder<CatalogueDb>().UseNpgsql(connection, o => o.UseNetTopologySuite()).Options;
string? Option(string name) => Array.IndexOf(args, name) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
try
{
    if (args.Contains("--self-test")) { await LandscapeSelfTest.RunAsync(ct); return; }
    await using var db = new CatalogueDb(dbOptions);
    var store = new LandscapeStore(db, TimeProvider.System);
    if (args.Contains("--list-parcels"))
    {
        Console.WriteLine(LandscapeJson.Write(await db.Parcels.AsNoTracking().Where(p => p.Geometry != null && p.ResolutionStatus == "resolved_exact")
            .OrderBy(p => p.Id).Select(p => new { p.Id, p.CadastralNumber, p.UniqueNumber, p.MunicipalityName, p.LandscapeStatus }).ToListAsync(ct)));
        return;
    }
    if (Option("--register") is { } manifest)
    {
        var ids = (Option("--parcels") ?? throw new ArgumentException("--parcels must specify canonical parcel UUIDs."))
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToArray();
        var bundle = await LandscapeBundleReader.ReadAsync(manifest, inputRoot, ct);
        var datasetId = await store.RegisterAsync(bundle.Json, bundle.Hash, ct);
        Console.WriteLine(LandscapeJson.Write(new { datasetId, queued = await store.EnqueueAsync(datasetId, ids, ct) }));
        return;
    }
    if (!args.Contains("--once") && !args.Contains("--watch"))
    {
        Console.WriteLine("Usage: --list-parcels | --register manifest.json --parcels uuid,uuid | --once | --watch | --self-test");
        Environment.ExitCode = 1; return;
    }
    var processor = new LandscapeProcessor(new GdalRunner(Environment.GetEnvironmentVariable("Landscape__GdalBin")), inputRoot, artifactRoot);
    do
    {
        var claim = await store.ClaimAsync(ct);
        if (claim is null)
        {
            if (args.Contains("--once")) { Console.WriteLine("No eligible landscape jobs."); return; }
            await Task.Delay(TimeSpan.FromSeconds(10), ct); continue;
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            var result = await processor.ProcessAsync(claim, budget.Token);
            var published = await store.PublishAsync(claim, result, ct);
            Console.WriteLine(LandscapeJson.Write(new { claim.JobId, published, result.Status, result.Score }));
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var code = error is LandscapeInputException input ? input.Code : error is OperationCanceledException ? "processing_timeout" : "processing_failed";
            var retryable = code is "processing_timeout" or "processing_failed";
            await store.FailAsync(claim, code, retryable, ct);
            Console.WriteLine(LandscapeJson.Write(new { claim.JobId, failure = code, retryable }));
            if (args.Contains("--once")) Environment.ExitCode = 1;
        }
    } while (!args.Contains("--once") && !ct.IsCancellationRequested);
}
catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
catch (Exception error)
{
    Console.Error.WriteLine(error is LandscapeInputException input ? input.Code : error.Message);
    Environment.ExitCode = 1;
}
