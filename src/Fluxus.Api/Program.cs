using Fluxus.Api;
using Fluxus.Core;
using Fluxus.Infrastructure.Geometry;
using Fluxus.Infrastructure.Persistence;
using Fluxus.Infrastructure.Sources;
using Microsoft.EntityFrameworkCore;
using Npgsql;

if (args.Contains("--validate-sources"))
{
    await Fluxus.Api.SourceValidation.RunAsync(Path.GetFullPath("tests/fixtures"), CancellationToken.None);
    return;
}

var commands = new[] { "--migrate", "--import-once" };
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args.Where(arg => !commands.Contains(arg)).ToArray(),
    ContentRootPath = Path.GetDirectoryName(typeof(Program).Assembly.Location)
});
var options = builder.Configuration.GetSection("Source").Get<SourceOptions>() ?? new();
if (options.Mode is not ("Live" or "Fixture") || options.DelayMilliseconds < 0 || options.MaxAuctions < 1 ||
    options.MaxPages < 1 || options.IntervalMinutes < 30) throw new InvalidOperationException("Invalid Source configuration.");
options.FixturePath = Path.GetFullPath(options.FixturePath, AppContext.BaseDirectory);
if (options.Mode == "Fixture")
{
    using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(options.FixturePath, "validation.json")));
    options.FixtureObservedAt = report.RootElement.GetProperty("observedAt").GetDateTimeOffset().ToUniversalTime();
}
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AuctionParser>();
builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString("Catalogue") ??
    throw new InvalidOperationException("Catalogue connection is required.")).UseNetTopologySuite().Build());
builder.Services.AddDbContext<CatalogueDb>((services, db) => db.UseNpgsql(services.GetRequiredService<NpgsqlDataSource>(),
    postgres => postgres.UseNetTopologySuite().MigrationsAssembly("Fluxus.Api")));
builder.Services.AddHttpClient<IAuctionSource, EvarzytynesSource>(http =>
{
    http.Timeout = TimeSpan.FromSeconds(40); http.DefaultRequestHeaders.UserAgent.ParseAdd("FluxusPrototype/0.1");
});
builder.Services.AddHttpClient<IParcelProvider, GeoportalProvider>(http => http.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddScoped<ICatalogueStore, PostgresCatalogueStore>();
builder.Services.AddScoped<ImportCoordinator>();
builder.Services.AddHostedService<ImportScheduler>();
builder.Services.AddProblemDetails();
var app = builder.Build();
if (args.Contains("--migrate") || args.Contains("--import-once"))
{
    using var scope = app.Services.CreateScope();
    var ct = CancellationToken.None;
    if (args.Contains("--migrate")) await scope.ServiceProvider.GetRequiredService<CatalogueDb>().Database.MigrateAsync(ct);
    if (args.Contains("--import-once"))
    {
        var summary = await scope.ServiceProvider.GetRequiredService<ImportCoordinator>().RunAsync(options.Mode, options.MaxAuctions, options.MaxPages, ct);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary));
        if (summary.FailedAuctions > 0 || summary.Auctions == 0) Environment.ExitCode = 1;
    }
    return;
}
app.UseExceptionHandler();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (CatalogueDb db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503));
app.MapCatalogue();
app.MapLandscape();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
