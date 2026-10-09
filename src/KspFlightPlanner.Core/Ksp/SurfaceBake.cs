using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>
/// What a body looks like in the player's game, sampled over kRPC ("baked"): terrain height and biome on a
/// latitude/longitude grid, its rotation, and its air. Rows run from 90° S to 90° N, columns from 180° W eastward,
/// one every <see cref="StepDeg"/> degrees; the grid wraps around in longitude. Heights are metres above the datum
/// (sea level); the sea floor is below 0.
/// </summary>
/// <param name="InitialRotationDeg">The body's rotation angle at UT 0 (kRPC InitialRotation), in degrees.</param>
/// <param name="Biomes">Biome index for each grid point into <paramref name="BiomeNames"/>; <see cref="NoBiome"/> where unknown.</param>
/// <param name="Atmosphere">Pressure and density every few km up to the top of the atmosphere; empty for airless bodies.</param>
public sealed record SurfaceBake(
    string BodyName, double StepDeg, double InitialRotationDeg, double RotationPeriodS, double RadiusM, double Mu,
    DateTime BakedUtc, float[] Heights, byte[] Biomes, IReadOnlyList<string> BiomeNames, IReadOnlyList<AtmospherePoint> Atmosphere)
{
    public const byte NoBiome = 255;

    public int Rows => GridRows(StepDeg);
    public int Columns => GridColumns(StepDeg);

    public static int GridRows(double stepDeg) => (int)Math.Round(180 / stepDeg) + 1;
    public static int GridColumns(double stepDeg) => (int)Math.Round(360 / stepDeg);

    /// <summary>Latitude of grid row <paramref name="row"/>.</summary>
    public static double RowLatitude(double stepDeg, int row) => Math.Min(90, -90 + row * stepDeg);

    /// <summary>Longitude of grid column <paramref name="column"/>.</summary>
    public static double ColumnLongitude(double stepDeg, int column) => -180 + column * stepDeg;

    /// <summary>The grid is complete (a cancelled bake keeps the rows it finished, the rest NaN).</summary>
    public bool IsComplete => Heights.Length == Rows * Columns && !Heights.Any(float.IsNaN);

    /// <summary>
    /// Terrain height at any spot, read between the four surrounding grid points (bilinear, docs/MATH.md §1.9).
    /// Null where the grid has no data.
    /// </summary>
    public double? HeightAt(double latitudeDeg, double longitudeDeg)
    {
        if (Heights.Length != Rows * Columns) return null; // no solid surface (gas giants, the star)
        var (r0, c0, c1, fr, fc) = Cell(latitudeDeg, longitudeDeg);
        var r1 = Math.Min(r0 + 1, Rows - 1);
        double H(int r, int c) => Heights[r * Columns + c];
        var h00 = H(r0, c0); var h01 = H(r0, c1); var h10 = H(r1, c0); var h11 = H(r1, c1);
        var value = (1 - fr) * ((1 - fc) * h00 + fc * h01) + fr * ((1 - fc) * h10 + fc * h11);
        return double.IsNaN(value) ? null : value;
    }

    /// <summary>The biome at the grid point nearest the spot, or null when unknown.</summary>
    public string? BiomeAt(double latitudeDeg, double longitudeDeg)
    {
        if (Biomes.Length != Rows * Columns) return null;
        var row = (int)Math.Round((Math.Clamp(latitudeDeg, -90, 90) + 90) / StepDeg);
        var column = Wrap((int)Math.Round((longitudeDeg + 180) / StepDeg), Columns);
        var index = Biomes[Math.Min(row, Rows - 1) * Columns + column];
        return index < BiomeNames.Count ? BiomeNames[index] : null;
    }

    /// <summary>The highest and lowest grid points: (height, latitude, longitude).</summary>
    public ((double H, double Lat, double Lon) Highest, (double H, double Lat, double Lon) Lowest)? Extremes()
    {
        int hi = -1, lo = -1;
        for (var i = 0; i < Heights.Length; i++)
        {
            if (float.IsNaN(Heights[i])) continue;
            if (hi < 0 || Heights[i] > Heights[hi]) hi = i;
            if (lo < 0 || Heights[i] < Heights[lo]) lo = i;
        }
        if (hi < 0) return null;
        (double, double, double) Point(int i) => (Heights[i], RowLatitude(StepDeg, i / Columns), ColumnLongitude(StepDeg, i % Columns));
        return (Point(hi), Point(lo));
    }

    private (int Row, int Col0, int Col1, double FracRow, double FracCol) Cell(double latitudeDeg, double longitudeDeg)
    {
        var r = (Math.Clamp(latitudeDeg, -90, 90) + 90) / StepDeg;
        var r0 = Math.Min((int)Math.Floor(r), Rows - 2);
        var c = (longitudeDeg + 180) / StepDeg;
        var cFloor = Math.Floor(c);
        var c0 = Wrap((int)cFloor, Columns);
        return (r0, c0, Wrap(c0 + 1, Columns), Math.Clamp(r - r0, 0, 1), c - cFloor);
    }

    private static int Wrap(int i, int n) => ((i % n) + n) % n;
}
