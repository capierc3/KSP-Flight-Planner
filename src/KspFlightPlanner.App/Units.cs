using System.Globalization;
using KspFlightPlanner.Core;

namespace KspFlightPlanner.App;

/// <summary>Human-friendly formatting with automatic units, for the read-only data views.</summary>
internal static class Units
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    /// <summary>"85 km", "12.0 Mm", "5.263 Gm" (1 Mm = 1,000 km; 1 Gm = 1,000,000 km), plus the exact metres.</summary>
    public static string Distance(double metres)
    {
        var scaled = Math.Abs(metres) >= 1e9 ? $"{metres / 1e9:0.###} Gm"
            : Math.Abs(metres) >= 1e7 ? $"{metres / 1e6:0.##} Mm"
            : $"{metres / 1e3:#,0.###} km";
        return $"{scaled}  ({metres.ToString("#,0", Culture)} m)";
    }

    public static string Km(double metres) => (metres / 1000).ToString("#,0.###", Culture) + " km";

    public static string Speed(double metresPerSecond) => metresPerSecond.ToString("#,0.0", Culture) + " m/s";

    public static string Dv(double metresPerSecond) => metresPerSecond.ToString("#,0", Culture) + " m/s";

    /// <summary>A burn's length: "37s", "1m 29s", "1h 02m".</summary>
    public static string BurnTime(double seconds)
    {
        var s = (long)Math.Round(seconds);
        return s < 60 ? $"{s}s" : s < 3600 ? $"{s / 60}m {s % 60:00}s" : $"{s / 3600}h {s % 3600 / 60:00}m";
    }

    /// <summary>"0.497 t", "12.4 t", "1,250 t".</summary>
    public static string Tonnes(double tonnes) =>
        tonnes.ToString(Math.Abs(tonnes) < 10 ? "0.###" : Math.Abs(tonnes) < 1000 ? "#,0.#" : "#,0", Culture) + " t";

    public static string Degrees(double deg) => deg.ToString("0.###", Culture) + "°";

    /// <summary>"12.451° N, 39.196° E" (north and east positive).</summary>
    public static string LatLon(double latitudeDeg, double longitudeDeg) =>
        $"{Math.Abs(latitudeDeg).ToString("0.###", Culture)}° {(latitudeDeg < 0 ? "S" : "N")}, " +
        $"{Math.Abs(longitudeDeg).ToString("0.###", Culture)}° {(longitudeDeg < 0 ? "W" : "E")}";

    /// <summary>Kerbin-calendar duration plus seconds, e.g. "1y 375d 3h 50m  (17,315,400 s)".</summary>
    public static string Duration(double seconds) =>
        $"{GameCalendar.Kerbin.FormatDuration(seconds)}  ({seconds.ToString("#,0", Culture)} s)";

    public static string Scientific(double value, string unit) => value.ToString("0.#####E+0", Culture) + " " + unit;
}
