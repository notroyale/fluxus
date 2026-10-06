using NetTopologySuite.Geometries;

namespace Fluxus.Infrastructure.Landscape;

public static class LandscapeSampling
{
    public static Coordinate[] Points(MultiPolygon polygon, int maximum = 16)
    {
        if (maximum is < 1 or > 16 || polygon.IsEmpty || !polygon.IsValid)
            throw new LandscapeInputException("invalid_parcel_geometry");
        var bounds = polygon.EnvelopeInternal;
        var candidates = new List<Coordinate> { polygon.PointOnSurface.Coordinate };
        for (var i = 1; i <= 2048; i++)
        {
            var coordinate = new Coordinate(bounds.MinX + Halton(i, 2) * bounds.Width,
                bounds.MinY + Halton(i, 3) * bounds.Height);
            if (polygon.Contains(polygon.Factory.CreatePoint(coordinate))) candidates.Add(coordinate);
        }
        var chosen = new List<Coordinate> { candidates[0] };
        while (chosen.Count < maximum)
        {
            var next = candidates.Select(c => (Point: c, Distance: chosen.Min(p => p.Distance(c))))
                .OrderByDescending(p => p.Distance).First();
            if (next.Distance < 10) break;
            chosen.Add(next.Point);
        }
        return chosen.ToArray();
    }
    private static double Halton(int n, int basis)
    {
        double result = 0, fraction = 1;
        while (n > 0) { fraction /= basis; result += fraction * (n % basis); n /= basis; }
        return result;
    }
}
