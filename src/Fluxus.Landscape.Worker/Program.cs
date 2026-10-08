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
var preparationRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("Landscape__PreparationRoot") ?? ".local/landscape-prepared");
var dbOptions = new DbContextOptionsBuilder<CatalogueDb>().UseNpgsql(connection, o => o.UseNetTopologySuite()).Options;
string? Option(string name) => Array.IndexOf(args, name) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
try
{
    if (args.Contains("--self-test")) { await LandscapeSelfTest.RunAsync(ct); return; }
    if (args.Contains("--preparation-self-test")) { await PreparationSelfTest.RunAsync(ct); return; }
    if (Option("--prepare") is { } preparationManifest)
    {
        var path = LandscapeBundleReader.ConfinedPath(inputRoot, preparationManifest);
        if (new FileInfo(path).Length > 128 * 1024) throw new LandscapeInputException("manifest_too_large");
        var request = LandscapeJson.Read<LidarPreparationRequest>(await File.ReadAllTextAsync(path, ct));
        LidarPreparation.Validate(request);
        request = request with { Tiles = request.Tiles.Select(t => t with { Path = Path.GetFullPath(t.Path, Path.GetDirectoryName(path)!) }).ToArray(),
            HorizontalCrsDeclaration = request.HorizontalCrsDeclaration is { } declaration
                ? declaration with { Path = Path.GetFullPath(declaration.Path, Path.GetDirectoryName(path)!) } : null };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(30));
        var preparer = new LidarPreparation(new PdalRunner(Environment.GetEnvironmentVariable("Landscape__PdalBin")), new GdalRunner(), inputRoot, preparationRoot);
        Console.WriteLine(LandscapeJson.Write(new { output = await preparer.PrepareAsync(request, Option("--output") ?? "terrain", deadline.Token) }));
        return;
    }
    if (Option("--fetch-context") is { } pilotManifest)
    {
        var path = LandscapeBundleReader.ConfinedPath(inputRoot, pilotManifest);
        if (new FileInfo(path).Length > 128 * 1024) throw new LandscapeInputException("manifest_too_large");
        var pilot = LandscapeJson.Read<PilotArea>(await File.ReadAllTextAsync(path, ct));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Fluxus-Prototype/0.1");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(15));
        Console.WriteLine(LandscapeJson.Write(new { output = await new GeoportalContext(http, new GdalRunner(), preparationRoot)
            .FetchAsync(pilot, Option("--output") ?? "context", deadline.Token) }));
        return;
    }
    await using var db = new CatalogueDb(dbOptions);
    var store = new LandscapeStore(db, TimeProvider.System);
    if (Option("--pilot-plan") is { } pilotParcel)
    {
        Console.WriteLine(LandscapeJson.Write(new { output = await PilotExport.ExportAsync(db, Guid.Parse(pilotParcel), preparationRoot,
            Option("--output") ?? "pilot-request", ct) }));
        return;
    }
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
        Console.WriteLine("Usage: --list-parcels | --pilot-plan uuid --output folder | --prepare request.json --output folder | --fetch-context pilot-area.json --output folder | --register manifest.json --parcels uuid,uuid | --once | --watch | --self-test | --preparation-self-test");
        Environment.ExitCode = 1; return;
    }
    if (Environment.GetEnvironmentVariable("Landscape__ProcessingEnabled") is { } enabled && !bool.Parse(enabled))
    {
        Console.WriteLine("Landscape processing is disabled."); return;
    }
    var artifactQuota = Environment.GetEnvironmentVariable("Landscape__MaxArtifactBytes") is { } quota ? long.Parse(quota) : LandscapeArtifactStorage.DefaultQuotaBytes;
    var processor = new LandscapeProcessor(new GdalRunner(Environment.GetEnvironmentVariable("Landscape__GdalBin")), inputRoot, artifactRoot);
    var nextCleanup = DateTimeOffset.MinValue;
    do
    {
        if (DateTimeOffset.UtcNow >= nextCleanup)
        {
            var removed = await LandscapeArtifactStorage.CleanupAbandonedAsync(db, artifactRoot, DateTimeOffset.UtcNow, ct);
            if (removed > 0) Console.WriteLine(LandscapeJson.Write(new { abandonedArtifactsRemoved = removed }));
            nextCleanup = DateTimeOffset.UtcNow.AddHours(6);
        }
        try { LandscapeArtifactStorage.CheckBudget(artifactRoot, artifactQuota); }
        catch (LandscapeInputException error)
        {
            Console.WriteLine(LandscapeJson.Write(new { storageBlocked = error.Code }));
            if (args.Contains("--once")) { Environment.ExitCode = 1; return; }
            await Task.Delay(TimeSpan.FromSeconds(60), ct); continue;
        }
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
