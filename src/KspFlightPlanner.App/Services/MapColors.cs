namespace KspFlightPlanner.App.Services;

/// <summary>Colours for the flattened body maps (ore survey, science), as Bgra32 pixels.</summary>
public static class MapColors
{
    /// <summary>Nothing there (unmapped, or nothing to run): dark grey.</summary>
    public const uint None = 0xFF2B2B2B;

    /// <summary>Blue (none) through green and yellow to red (the most on the body).</summary>
    public static uint Heat(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double r, g, b;
        if (t < 0.5) { r = 0; g = t * 2; b = 1 - t * 2; }
        else { r = (t - 0.5) * 2; g = 1 - (t - 0.5) * 2 * 0.6; b = 0; }
        return 0xFF000000 | (uint)(r * 230 + 25) << 16 | (uint)(g * 200 + 30) << 8 | (uint)(b * 200 + 40);
    }

    /// <summary>A colour washed toward grey.</summary>
    public static uint Dull(uint color)
    {
        uint Mix(int shift) => ((((color >> shift) & 0xFF) + 0x50) / 2) << shift;
        return 0xFF000000 | Mix(16) | Mix(8) | Mix(0);
    }
}
