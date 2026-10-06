using System.Net;
using System.Text.Json;
using Fluxus.Core;
using Fluxus.Infrastructure.Geometry;
using Fluxus.Infrastructure.Sources;

namespace Fluxus.Tests;

public sealed class BatchGeometryTests
{
    private static string Fixture(string number) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"parcel-{number}.json"));
    private static LandItem Item(string unique, string? cadastral = null) =>
        new(unique, unique, cadastral, null, null, null, null, null, null, null, null, []);
    private static string Combine(params string[] identifiers)
    {
        var features = identifiers.Select(id => JsonSerializer.Deserialize<JsonElement>(Fixture(id)).GetProperty("features")[0]).ToArray();
        return JsonSerializer.Serialize(new { features, spatialReference = new { wkid = 4326 } });
    }

    [Fact]
    public void UnorderedBatchMatchesEachRequestedParcelIndependently()
    {
        var requested = new[] { Item("665500030065"), Item("396000040206"), Item("440000000001") };
        var result = GeoportalProvider.ParseBatch(Combine("396000040206", "665500030065"), "unikalus_nr", requested);
        Assert.Equal(GeoportalProvider.Parse(Fixture("665500030065"), requested[0]).GeoJson, result[0].GeoJson);
        Assert.Equal(GeoportalProvider.Parse(Fixture("396000040206"), requested[1]).GeoJson, result[1].GeoJson);
        Assert.Equal("not_found", result[2].Status); Assert.Null(result[2].GeoJson);
    }

    [Fact]
    public void DuplicateOrConflictingMatchesNeverBecomeAnExactGeometry()
    {
        var duplicated = GeoportalProvider.ParseBatch(Combine("396000040206", "396000040206"), "unikalus_nr", [Item("396000040206")]);
        Assert.Equal("ambiguous", duplicated[0].Status); Assert.Null(duplicated[0].GeoJson);
        var conflict = GeoportalProvider.ParseBatch(Combine("396000040206"), "unikalus_nr", [Item("396000040206", "0001/0001:0001")]);
        Assert.Equal("identifier_conflict", conflict[0].Status); Assert.Null(conflict[0].GeoJson);
    }

    [Fact]
    public void TruncatedProviderResponseCannotClaimCompleteExactMatches()
    {
        var result = GeoportalProvider.ParseBatch("{\"exceededTransferLimit\":true,\"features\":[]}", "unikalus_nr", [Item("396000040206")]);
        Assert.Equal("ambiguous", result[0].Status); Assert.Null(result[0].GeoJson);
    }

    [Fact]
    public async Task LiveProviderCapsRequestsAtTwentyAndSkipsMissingIdentifiers()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new GeoportalProvider(http, new SourceOptions { Mode = "Live", DelayMilliseconds = 0 });
        var requested = Enumerable.Range(1, 41).Select(i => Item(i.ToString("D12"))).Append(Item("unused") with { UniqueNumber = null }).ToArray();
        var result = await provider.ResolveBatchAsync(requested, CancellationToken.None);
        Assert.Equal(3, handler.Queries.Count);
        Assert.All(handler.Queries, query => Assert.InRange(query.Count(c => c == '\''), 2, 40));
        Assert.All(result.Take(41), match => Assert.Equal("not_found", match.Status));
        Assert.Equal("missing_identifier", result[^1].Status);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Queries.Add(Uri.UnescapeDataString(request.RequestUri!.Query));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"features\":[]}") });
        }
    }
}
