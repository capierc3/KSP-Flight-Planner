using System.Globalization;

namespace KspFlightPlanner.App;

internal static class Format
{
    public static string Distance(double? metres) =>
        metres is { } m ? m.ToString("N1", CultureInfo.CurrentCulture) + " m" : "n/a";

    public static string Speed(double metresPerSecond) =>
        metresPerSecond.ToString("N2", CultureInfo.CurrentCulture) + " m/s";

    public static string Duration(double seconds)
    {
        var hours = Math.Floor(seconds / 3600);
        var minutes = Math.Floor((seconds - hours * 3600) / 60);
        var secs = seconds - hours * 3600 - minutes * 60;
        return $"{hours:0}h {minutes:0}m {secs:0.0}s";
    }

    public static bool TryParseDistance(string? text, out double metres)
    {
        var cleaned = (text ?? "").Trim().TrimEnd('m', 'M').Trim();
        return double.TryParse(cleaned, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out metres);
    }
}
