namespace Fluxus.Core.Landscape;

// Raw, physical measurements are retained independently of the preference profile.
public sealed record LandscapeMetrics(
    double? VisibleAreaKm2, double? DistantDirectionFraction, double? VisibleNaturalDiversity,
    double? ReliefP90P10M, double? TpiStandardDeviationM, double? NaturalCoverFraction,
    double? ForestEdgeDistanceM, double? WaterDistanceM, double? VisibleWaterFraction,
    double? NearestBuildingDistanceM, int? BuildingsWithin1Km, double? MajorRoadDistanceM,
    double? GentleGroundAreaHa, double? LargestGentlePatchHa, int ValidViewpoints,
    double? BestVisibleAreaKm2);

public sealed record LandscapeScores(string Profile, int? Overall, int? Views, int? Terrain,
    int? Nature, int? Seclusion);

public static class LandscapeScore
{
    public const string Profile = "balanced-retreat-v1";
    public const string AnalysisVersion = "gdal-grid-v2";

    public static LandscapeScores Calculate(LandscapeMetrics m)
    {
        Validate(m);
        var views = m.ValidViewpoints > 0 && m.VisibleAreaKm2 is { } area &&
            m.DistantDirectionFraction is { } directions && m.VisibleNaturalDiversity is { } diversity
            ? 100 * (.60 * Unit(Math.Log(1 + area) / Math.Log(21)) + .25 * directions + .15 * diversity)
            : (double?)null;
        var terrain = m.ReliefP90P10M is { } relief && m.TpiStandardDeviationM is { } tpi
            ? 100 * (.65 * Unit(relief / 60) + .35 * Unit(tpi / 12)) : (double?)null;
        var nature = m.NaturalCoverFraction is { } natural && m.ForestEdgeDistanceM is { } forest &&
            m.WaterDistanceM is { } water && m.VisibleWaterFraction is { } visibleWater
            ? 100 * (.50 * natural + .25 * Unit((1000 - forest) / 800) +
                .25 * (.5 * Unit(1 - water / 1000) + .5 * Unit(visibleWater / .02))) : (double?)null;
        var seclusion = m.NearestBuildingDistanceM is { } building && m.BuildingsWithin1Km is { } count &&
            m.MajorRoadDistanceM is { } road
            ? 100 * (.60 * (.5 * Unit((building - 50) / 950) + .5 * Math.Exp(-count / 20.0)) +
                .40 * Unit((road - 100) / 1400)) : (double?)null;
        // Never redistribute missing component weights or turn missing inputs into zero.
        var overall = views.HasValue && terrain.HasValue && nature.HasValue && seclusion.HasValue
            ? .35 * views + .20 * terrain + .30 * nature + .15 * seclusion : null;
        return new(Profile, Round(overall), Round(views), Round(terrain), Round(nature), Round(seclusion));
    }

    public static void Validate(LandscapeMetrics m)
    {
        double?[] nonnegative = [m.VisibleAreaKm2, m.ReliefP90P10M, m.TpiStandardDeviationM,
            m.ForestEdgeDistanceM, m.WaterDistanceM, m.NearestBuildingDistanceM, m.MajorRoadDistanceM,
            m.GentleGroundAreaHa, m.LargestGentlePatchHa, m.BestVisibleAreaKm2];
        double?[] fractions = [m.DistantDirectionFraction, m.VisibleNaturalDiversity,
            m.NaturalCoverFraction, m.VisibleWaterFraction];
        if (nonnegative.Any(x => x.HasValue && (!double.IsFinite(x.Value) || x < 0)) ||
            fractions.Any(x => x.HasValue && (!double.IsFinite(x.Value) || x < 0 || x > 1)) ||
            m.BuildingsWithin1Km < 0 || m.ValidViewpoints is < 0 or > 16 ||
            m.LargestGentlePatchHa > m.GentleGroundAreaHa)
            throw new ArgumentException("Invalid landscape measurements.");
    }

    public static string Quality(int oldestYear, int validViewpoints, bool substituted, int currentYear) =>
        validViewpoints == 0 ? "unknown" : substituted || validViewpoints < 3 || currentYear - oldestYear > 8
            ? "low" : currentYear - oldestYear > 3 ? "medium" : "high";

    private static double Unit(double x) => Math.Clamp(x, 0, 1);
    private static int? Round(double? value) => value is { } v ? (int)Math.Round(v, MidpointRounding.AwayFromZero) : null;
}
