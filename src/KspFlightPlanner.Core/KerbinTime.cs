namespace KspFlightPlanner.Core;

/// <summary>
/// Converts universal time (seconds since game start) to the in-game calendar.
/// Kerbin calendar: 6-hour days, 426-day years. Year 1, Day 1 is UT 0.
/// </summary>
public sealed record GameCalendar(int HoursPerDay, int DaysPerYear)
{
    public static readonly GameCalendar Kerbin = new(6, 426);
    public static readonly GameCalendar Earth = new(24, 365);

    public double SecondsPerDay => HoursPerDay * 3600.0;
    public double SecondsPerYear => SecondsPerDay * DaysPerYear;

    public double ToUniversalTime(int year, int day, double hourOfDay = 0) =>
        (year - 1) * SecondsPerYear + (day - 1) * SecondsPerDay + hourOfDay * 3600;

    /// <summary>A date such as "Year 1, Day 236, 3:12:05".</summary>
    public string FormatDate(double ut)
    {
        var year = (int)Math.Floor(ut / SecondsPerYear);
        var rem = ut - year * SecondsPerYear;
        var day = (int)Math.Floor(rem / SecondsPerDay);
        rem -= day * SecondsPerDay;
        return $"Year {year + 1}, Day {day + 1}, {FormatClock(rem)}";
    }

    /// <summary>A date without the clock, such as "Year 1, Day 236".</summary>
    public string FormatDay(double ut)
    {
        var year = (int)Math.Floor(ut / SecondsPerYear);
        var day = (int)Math.Floor((ut - year * SecondsPerYear) / SecondsPerDay);
        return $"Year {year + 1}, Day {day + 1}";
    }

    /// <summary>A duration such as "1y 23d 4h 05m".</summary>
    public string FormatDuration(double seconds)
    {
        var sign = seconds < 0 ? "-" : "";
        seconds = Math.Abs(seconds);
        var years = (int)Math.Floor(seconds / SecondsPerYear);
        seconds -= years * SecondsPerYear;
        var days = (int)Math.Floor(seconds / SecondsPerDay);
        seconds -= days * SecondsPerDay;
        var hours = (int)Math.Floor(seconds / 3600);
        var minutes = (int)Math.Floor((seconds - hours * 3600) / 60);
        var parts = years > 0 ? $"{years}y {days}d {hours}h {minutes:00}m"
            : days > 0 ? $"{days}d {hours}h {minutes:00}m"
            : $"{hours}h {minutes:00}m";
        return sign + parts;
    }

    private static string FormatClock(double seconds)
    {
        var h = (int)Math.Floor(seconds / 3600);
        var m = (int)Math.Floor((seconds - h * 3600) / 60);
        var s = (int)Math.Floor(seconds - h * 3600 - m * 60);
        return $"{h}:{m:00}:{s:00}";
    }
}
