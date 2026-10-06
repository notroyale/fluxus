using System.Text.Json;
using Fluxus.Core;
using Fluxus.Infrastructure.Geometry;
using Fluxus.Infrastructure.Sources;

namespace Fluxus.Tests;

public sealed class ParserTests
{
    private readonly AuctionParser parser = new();
    private static string Fixture(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
    private static ListingItem Source(string id, string number) => new(id, number,
        new Uri($"https://www.evarzytynes.lt/evs/pages/auction.do?id={id}&number={number}"), "");

    [Fact]
    public void RentalAuctionUsesRentalShareAndAreaWithoutOfferingOwnership()
    {
        var html = Fixture("auction-rental-338040.html");
        var auction = parser.ParseDetail(html, Source("338040", "337512"));
        var land = Assert.Single(auction.Items);
        Assert.Equal("rent", land.OfferType);
        Assert.Equal("1/1", land.ShareRaw);
        Assert.Equal("not_applicable", land.OwnershipKind);
        Assert.Equal(0.11m, land.SoldAreaHa);
        Assert.Equal("source", land.SoldAreaBasis);
        Assert.Equal(8m, auction.InitialPriceEur);
        Assert.Equal("rent", Assert.Single(parser.ParseDetail(parser.SanitizeDetail(html), Source("338040", "337512")).Items).OfferType);
    }

    [Fact]
    public void LeaseMentionInDescriptionDoesNotTurnASaleIntoARental()
    {
        var html = Fixture("auction-337829.html").Replace("</ul>",
            "<li><span class=\"left\">Aprašymas:</span><span class=\"right\">Žemės sklypas nuomojamas pagal sutartį.</span></li></ul>");
        var land = Assert.Single(parser.ParseDetail(html, Source("337829", "337123")).Items);
        Assert.Equal("sale", land.OfferType);
        Assert.Equal("partial", land.OwnershipKind);
    }

    [Theory]
    [InlineData("1/1", "full")]
    [InlineData("2/2", "full")]
    [InlineData("1/2", "partial")]
    [InlineData("2/1", "unknown")]
    [InlineData("0/1", "unknown")]
    [InlineData("Nenurodyta", "unknown")]
    public void OwnershipRequiresAValidExplicitFraction(string share, string expected)
    {
        var html = Fixture("auction-337829.html").Replace("3/4", share);
        var land = Assert.Single(parser.ParseDetail(html, Source("337829", "337123")).Items);
        Assert.Equal(expected, land.OwnershipKind);
    }

    [Fact]
    public void EveryCapturedLandSectionMatchesTheValidationReport()
    {
        using var report = JsonDocument.Parse(Fixture("validation.json"));
        var expected = report.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(11, expected.Length);
        foreach (var group in expected.GroupBy(i => i.GetProperty("SourceId").GetString()!))
        {
            var auction = parser.ParseDetail(Fixture($"auction-{group.Key}.html"), Source(group.Key, group.First().GetProperty("Number").GetString()!));
            Assert.Equal(group.Count(), auction.Items.Count);
            foreach (var row in group)
            {
                var item = Assert.Single(auction.Items, i => i.SourceKey == row.GetProperty("SourceKey").GetString());
                Assert.Equal(row.GetProperty("UniqueNumber").GetString(), item.UniqueNumber);
                Assert.Equal(row.GetProperty("CadastralNumber").GetString(), item.CadastralNumber);
                Assert.Equal(row.GetProperty("TotalAreaHa").GetDecimal(), item.TotalAreaHa);
                Assert.Equal(row.GetProperty("SoldAreaHa").GetDecimal(), item.SoldAreaHa);
                var resolution = GeoportalProvider.Parse(Fixture($"parcel-{item.UniqueNumber}.json"), item);
                Assert.Equal("resolved_exact", resolution.Status);
                Assert.True(ArcGisGeometry.FromGeoJson(resolution.GeoJson!).IsValid);
            }
        }
    }

    [Fact]
    public void MixedBundleDoesNotTreatBuildingIdentifiersAsLand()
    {
        var auction = parser.ParseDetail(Fixture("auction-337852.html"), Source("337852", "337147"));
        Assert.True(auction.ContainsNonLandAssets);
        var land = Assert.Single(auction.Items);
        Assert.Equal("440004370616", land.UniqueNumber);
        Assert.Equal(0.48m, land.TotalAreaHa);
        Assert.Equal(0.06m, land.SoldAreaHa);
        Assert.Equal("6/48", land.ShareRaw);
    }

    [Fact]
    public void TwoSharesOfSameParcelRemainDistinctSourceItems()
    {
        var auction = parser.ParseDetail(Fixture("auction-337928.html"), Source("337928", "337250"));
        Assert.Equal(2, auction.Items.Count);
        Assert.Equal(auction.Items[0].UniqueNumber, auction.Items[1].UniqueNumber);
        Assert.NotEqual(auction.Items[0].SourceKey, auction.Items[1].SourceKey);
    }

    [Fact]
    public void ExactAresOverrideRoundedParentheticalHectares()
    {
        var auction = parser.ParseDetail(Fixture("auction-337829.html"), Source("337829", "337123"));
        Assert.Equal(1.995m, Assert.Single(auction.Items).SoldAreaHa);
        Assert.Null(auction.LastBidEur);
    }

    [Fact]
    public void ObservedBidAndVilniusDateAreReadFromTheSource()
    {
        var auction = parser.ParseDetail(Fixture("auction-337872.html"), Source("337872", "337170"));
        Assert.Equal(15200m, auction.LastBidEur);
        Assert.Equal(TimeSpan.Zero, auction.EndsAt!.Value.Offset);
        Assert.Equal(6, auction.EndsAt.Value.Hour);
    }

    [Theory]
    [InlineData("10 800 Eur", 10800)]
    [InlineData("850,25 EUR", 850.25)]
    public void LithuanianMoneyIsNormalized(string raw, decimal expected) => Assert.Equal(expected, AuctionParser.Money(raw));

    [Theory]
    [InlineData("4720/0006:320", "4720/0006:0320")]
    [InlineData("0101/0044:0261", "0101/0044:0261")]
    [InlineData("4720/0006:320' OR 1=1", null)]
    public void CadastralIdentifiersAreCanonicalAndRejectQuerySyntax(string raw, string? expected) => Assert.Equal(expected, ParcelIdentifiers.Cadastral(raw));

    [Fact]
    public void ChangedLayoutDoesNotBecomeAnEmptySuccessfulCatalogue() =>
        Assert.Throws<FormatException>(() => parser.ParseListing("<html><body>Access denied</body></html>", 0));

    [Fact]
    public void PaginationUsesTheNextActualPageAndDeduplicatesCards()
    {
        var html = Fixture("listing.html").Replace("</body>", "<a class=\"paginationLink\" data-page=\"-1\"></a><a class=\"paginationLink\" data-page=\"1\"></a><a class=\"paginationLink\" data-page=\"30\"></a></body>");
        var listing = parser.ParseListing(html, 0);
        Assert.Equal(10, listing.Items.Count); Assert.Equal(1, listing.NextPage);
    }

    [Fact]
    public void AmbiguousDstTimesAreNotGuessed() =>
        Assert.Throws<FormatException>(() => AuctionParser.Date("2026-10-25 03:30:00"));

    [Fact]
    public void SanitizationRemovesOwnersAndDescriptionText()
    {
        var html = Fixture("auction-337829.html").Replace("</ul>", "<li><span class=\"left\">Savininkai:</span><span class=\"right\">PRIVATE_OWNER_SENTINEL</span></li><li><span class=\"left\">Aprašymas:</span><span class=\"right\">PRIVATE_CONTACT_SENTINEL</span></li></ul>");
        var sanitized = parser.SanitizeDetail(html);
        Assert.DoesNotContain("PRIVATE_", sanitized);
        Assert.Single(parser.ParseDetail(sanitized, Source("337829", "337123")).Items);
    }
}
