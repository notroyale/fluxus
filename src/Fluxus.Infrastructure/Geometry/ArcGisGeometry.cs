using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Fluxus.Infrastructure.Geometry;

public static class ArcGisGeometry
{
    private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

    public static MultiPolygon Read(JsonElement rings)
    {
        var ringPolygons = rings.EnumerateArray().Select(ring =>
        {
            var coordinates = ring.EnumerateArray().Select(p => new Coordinate(p[0].GetDouble(), p[1].GetDouble())).ToArray();
            if (coordinates.Length < 4 || !coordinates[0].Equals2D(coordinates[^1]) ||
                coordinates.Any(c => !double.IsFinite(c.X) || !double.IsFinite(c.Y) ||
                    c.X is < 20.5 or > 27 || c.Y is < 53.7 or > 56.6))
                throw new FormatException("Invalid rings or coordinates outside Lithuania.");
            var polygon = Factory.CreatePolygon(Factory.CreateLinearRing(coordinates));
            if (!polygon.IsValid || polygon.IsEmpty) throw new FormatException("Invalid parcel ring.");
            return polygon;
        }).OrderByDescending(p => p.Area).ToArray();
        if (ringPolygons.Length == 0) throw new FormatException("Empty parcel geometry.");

        // Classify by containment depth rather than assuming every ring is an exterior.
        var parents = new int[ringPolygons.Length];
        var depths = new int[ringPolygons.Length];
        Array.Fill(parents, -1);
        for (var i = 0; i < ringPolygons.Length; i++)
        {
            for (var j = i - 1; j >= 0; j--)
                if (ringPolygons[j].Contains(ringPolygons[i]))
                { parents[i] = j; depths[i] = depths[j] + 1; break; }
        }
        var polygons = new List<Polygon>();
        for (var i = 0; i < ringPolygons.Length; i++)
        {
            if (depths[i] % 2 != 0) continue;
            var holes = Enumerable.Range(0, ringPolygons.Length).Where(j => parents[j] == i && depths[j] % 2 == 1)
                .Select(j => (LinearRing)ringPolygons[j].ExteriorRing).ToArray();
            polygons.Add(Factory.CreatePolygon((LinearRing)ringPolygons[i].ExteriorRing, holes));
        }
        var result = Factory.CreateMultiPolygon(polygons.ToArray());
        if (!result.IsValid) throw new FormatException("Invalid parcel topology.");
        return result;
    }

    public static string Write(NetTopologySuite.Geometries.Geometry geometry) => new GeoJsonWriter().Write(geometry);

    public static MultiPolygon FromGeoJson(string json)
    {
        var geometry = new GeoJsonReader().Read<NetTopologySuite.Geometries.Geometry>(json);
        geometry.SRID = 4326;
        var polygon = geometry switch
        {
            MultiPolygon multi => multi,
            Polygon single => Factory.CreateMultiPolygon([single]),
            _ => throw new FormatException("Expected polygon geometry.")
        };
        if (!polygon.IsValid || polygon.IsEmpty) throw new FormatException("Invalid stored geometry.");
        return polygon;
    }
}
