using System.Text.Json;
using Fluxus.Core;
using Fluxus.Infrastructure.Geometry;

namespace Fluxus.Tests;

public sealed class GeometryTests
{
    [Fact]
    public void ArcGisRingsPreserveHolesAndSeparateParts()
    {
        using var rings = JsonDocument.Parse("""
            [ [[23,54],[24,54],[24,55],[23,55],[23,54]],
              [[23.2,54.2],[23.8,54.2],[23.8,54.8],[23.2,54.8],[23.2,54.2]],
              [[25,55],[25.5,55],[25.5,55.5],[25,55.5],[25,55]] ]
            """);
        var geometry = ArcGisGeometry.Read(rings.RootElement);
        Assert.Equal(2, geometry.NumGeometries);
        Assert.Equal(1, ((NetTopologySuite.Geometries.Polygon)geometry.GetGeometryN(0)).NumInteriorRings);
        Assert.True(geometry.Contains(geometry.InteriorPoint));
        Assert.True(ArcGisGeometry.FromGeoJson(ArcGisGeometry.Write(geometry)).EqualsTopologically(geometry));
    }

    [Fact]
    public void ProjectedCoordinatesCannotBeMistakenForLongitudeLatitude()
    {
        using var rings = JsonDocument.Parse("[[[500000,6000000],[500010,6000000],[500010,6000010],[500000,6000000]]]");
        Assert.Throws<FormatException>(() => ArcGisGeometry.Read(rings.RootElement));
    }

    [Fact]
    public void AProviderFeatureWithDifferentIdentifiersIsNotMapped()
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "parcel-396000040206.json"));
        var request = new LandItem("item", "440000000001", null, null, null, null, null, null, null, null, null, []);
        Assert.Equal("identifier_conflict", GeoportalProvider.Parse(raw, request).Status);
    }

    [Fact]
    public void AnArcGisErrorWithHttp200IsAProviderFailure()
    {
        var request = new LandItem("item", "440000000001", null, null, null, null, null, null, null, null, null, []);
        Assert.Equal("provider_unavailable", GeoportalProvider.Parse("{\"error\":{\"code\":500}}", request).Status);
    }

    [Fact]
    public void ConflictingAreaRemainsUnknownForFiltering()
    {
        var item = new LandItem("item", null, null, null, 2, 2, 1, 2, "1/2", null, null, []);
        Assert.True(item.HasAreaConflict); Assert.Null(item.SoldAreaHa); Assert.Equal("conflict", item.SoldAreaBasis);
    }
}
