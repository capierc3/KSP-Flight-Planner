using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>The parts of a surface bake that stand in for a body's constants (no terrain grid).</summary>
public sealed record BakeSummary(string BodyName, DateTime BakedUtc, double RotationPeriodS, double RadiusM, double Mu,
    double? HighestM, IReadOnlyList<AtmospherePoint> Air)
{
    public static BakeSummary From(SurfaceBake bake) =>
        new(bake.BodyName, bake.BakedUtc, bake.RotationPeriodS, bake.RadiusM, bake.Mu, bake.Extremes()?.Highest.H, bake.Atmosphere);
}

/// <summary>
/// One value compared between the wiki (the shipped baseline) and the player's game (the surface bake), and which one
/// the planner uses. <see cref="Flagged"/>: they differ by more than the threshold, so the wiki value is kept.
/// </summary>
public sealed record DataCheck(string Value, double? Wiki, double? Game, string Unit, bool UsesGame, bool Flagged, double? Difference)
{
    public string UsedText => UsesGame ? "game" : "wiki";
}

/// <summary>
/// The planner's body data: the wiki baseline, with values from the player's game (the surface bake) laid over it where
/// the two agree within a threshold. Where they disagree by more, the wiki value is kept and the difference is flagged.
/// See docs/MATH.md §1.1.
/// </summary>
public static class BakeMerge
{
    /// <summary>Constants the game defines exactly (day, radius, μ): any real difference is a mod or a mistake.</summary>
    public const double ConstantThreshold = 0.05;

    /// <summary>Sampled values (heights, pressure): a 1° grid alone can be this far off.</summary>
    public const double SampledThreshold = 0.15;

    /// <summary>|game − wiki| / |wiki|; null when either is missing or the wiki value is 0.</summary>
    public static double? RelativeDifference(double? wiki, double? game) =>
        wiki is { } w && game is { } g && w != 0 ? Math.Abs(g - w) / Math.Abs(w) : null;

    public static CelestialBody Apply(CelestialBody wiki, BakeSummary? bake)
    {
        if (bake is null) return wiki;
        var checks = new List<DataCheck>();

        double Pick(string value, double w, double g, string unit, double threshold)
        {
            var diff = RelativeDifference(w, g);
            var usesGame = diff is null || diff <= threshold;
            checks.Add(new DataCheck(value, w, g, unit, usesGame, !usesGame, diff));
            return usesGame ? g : w;
        }

        var period = Math.Sign(wiki.RotationPeriodS == 0 ? 1 : wiki.RotationPeriodS)
                     * Pick("Sidereal day", Math.Abs(wiki.RotationPeriodS), bake.RotationPeriodS, "s", ConstantThreshold);
        var radius = Pick("Radius", wiki.EquatorialRadiusM, bake.RadiusM, "m", ConstantThreshold);
        var mu = Pick("μ (GM)", wiki.Mu, bake.Mu, "m³/s²", ConstantThreshold);

        // Highest point: the grid steps over sharp summits, so it can only read low. Within the threshold the higher of
        // the two is used (the safe side for parking orbits and terrain warnings).
        var highest = wiki.HighestElevationM;
        if (bake.HighestM is { } gamePeak)
        {
            var diff = RelativeDifference(wiki.HighestElevationM, gamePeak);
            var ok = diff is null || diff <= SampledThreshold;
            var used = !ok ? wiki.HighestElevationM : Math.Max(wiki.HighestElevationM ?? double.MinValue, gamePeak);
            checks.Add(new DataCheck("Highest point", wiki.HighestElevationM, gamePeak, "m",
                ok && used == gamePeak, !ok, diff));
            highest = used;
        }

        var atmosphere = wiki.Atmosphere;
        if (wiki.Atmosphere is { } air && bake.Air.Count >= 2 && bake.Air[0].AltitudeM == 0)
        {
            // The wiki's own pressure table is the baseline where there is one (its /Param sea-level value can disagree).
            var wikiPa = air.PressureTable.FirstOrDefault(p => p.AltitudeM == 0)?.PressurePa
                         ?? air.SeaLevelPressureAtm * Atmosphere.PascalsPerAtm;
            var gamePa = bake.Air[0].PressurePa;
            var diff = RelativeDifference(wikiPa, gamePa);
            var usesGame = diff is null || diff <= SampledThreshold;
            checks.Add(new DataCheck("Pressure at 0 m", wikiPa / 1000, gamePa / 1000, "kPa", usesGame, !usesGame, diff));
            if (usesGame)
                atmosphere = air with { SeaLevelPressureAtm = gamePa / Atmosphere.PascalsPerAtm, PressureTable = bake.Air };
        }

        return wiki with
        {
            RotationPeriodS = period,
            EquatorialRadiusM = radius,
            Mu = mu,
            MassKg = wiki.MassKg * mu / wiki.Mu,
            HighestElevationM = highest,
            Atmosphere = atmosphere,
            DataChecks = checks,
            BakedUtc = bake.BakedUtc,
        };
    }
}
