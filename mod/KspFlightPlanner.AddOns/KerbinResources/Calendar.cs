using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.KerbinResources
{
    /// <summary>
    /// Kerbin Resources' configuration from KerbinResources.cfg: the calendar, last names, call signs, birthplaces and life
    /// stages. Read once from KSP's game database; built-in fallbacks if the file is missing.
    /// </summary>
    internal sealed class KerbinResourcesConfig
    {
        internal readonly List<KeyValuePair<string, int>> Months = new List<KeyValuePair<string, int>>();
        internal readonly List<string> Weekdays = new List<string>();
        internal string FoundingWeekday = "";
        internal readonly List<string> LastNames = new List<string>();
        internal readonly List<string> CallSigns = new List<string>();
        internal readonly List<string> Birthplaces = new List<string>();   // "City, Nation"
        internal readonly List<KeyValuePair<string, int>> LifeStages = new List<KeyValuePair<string, int>>(); // name, from age; ascending
        internal int RetirementAge = 130;

        private static KerbinResourcesConfig _current;

        internal static KerbinResourcesConfig Current => _current ?? (_current = Load());

        private static KerbinResourcesConfig Load()
        {
            var c = new KerbinResourcesConfig();
            var node = GameDatabase.Instance?.GetConfigNodes("KSPFP_KERBINRESOURCES").FirstOrDefault();
            if (node == null) Debug.LogWarning("[KspFlightPlanner] Kerbin Resources: KerbinResources.cfg not found; using built-in names.");
            var cal = node?.GetNode("CALENDAR");
            if (cal != null)
            {
                foreach (var m in cal.GetNodes("MONTH"))
                    if (m.GetValue("name") is string name && int.TryParse(m.GetValue("days"), out var days) && days > 0)
                        c.Months.Add(new KeyValuePair<string, int>(name, days));
                c.Weekdays.AddRange(cal.GetValues("weekday"));
                c.FoundingWeekday = cal.GetValue("foundingWeekday") ?? "";
            }
            if (node?.GetNode("LASTNAMES") is ConfigNode last) c.LastNames.AddRange(last.GetValues("name").Where(n => n.Length > 0).Distinct());
            if (node?.GetNode("CALLSIGNS") is ConfigNode calls) c.CallSigns.AddRange(calls.GetValues("name").Where(n => n.Length > 0).Distinct());
            if (node?.GetNode("BIRTHPLACES") is ConfigNode places)
                foreach (var pl in places.GetNodes("PLACE"))
                {
                    var city = pl.GetValue("city") ?? "";
                    var nation = pl.GetValue("nation") ?? "";
                    var name = city.Length > 0 && nation.Length > 0 && city != nation ? city + ", " + nation : city.Length > 0 ? city : nation;
                    if (name.Length > 0) c.Birthplaces.Add(name);
                }
            if (node?.GetNode("LIFE") is ConfigNode life)
            {
                if (int.TryParse(life.GetValue("retirementAge"), out var retire)) c.RetirementAge = retire;
                foreach (var st in life.GetNodes("STAGE"))
                    if (st.GetValue("name") is string stage && int.TryParse(st.GetValue("from"), out var from)) c.LifeStages.Add(new KeyValuePair<string, int>(stage, from));
                c.LifeStages.Sort((a, b) => a.Value.CompareTo(b.Value));
            }
            if (c.LastNames.Count == 0) c.LastNames.AddRange(new[] { "Rockmore", "Boosterson", "Fairing", "Gimbalton", "Stagewell", "Thrustwood" });
            return c;
        }

        /// <summary>The life stage for an age ("Middle-aged"), "" with none configured.</summary>
        internal string LifeStage(int age)
        {
            var stage = "";
            foreach (var s in LifeStages) if (age >= s.Value) stage = s.Key;
            return stage;
        }
    }

    /// <summary>A day on the calendar.</summary>
    internal struct CalendarDate
    {
        public long Year;
        public int Month;    // 1-based; 0 when the save's time doesn't fit the calendar (plain day of year then)
        public int Day;      // 1-based, in the month (or in the year when Month is 0)
        public string MonthName;
        public string Weekday;
    }

    /// <summary>
    /// The calendar dates are shown in. Game time 0 (KSP's Year 1, Day 1) is the company's founding, a date chosen in the
    /// save's settings (3 Diznam 2263 by default). Every year has the same 426 days, so a date is one count of days since
    /// a year 0, split into years, months and days, and a weekday from the founding day's (docs/MATH.md §12.1). A save on
    /// Earth time (24 h days, 365-day years) doesn't fit the months: its dates are years and days, counted from the same
    /// founding year.
    /// </summary>
    internal static class Calendar
    {
        private static KerbinResourcesConfig Config => KerbinResourcesConfig.Current;

        private static double DaySeconds => KSPUtil.dateTimeFormatter.Day;
        private static double YearSeconds => KSPUtil.dateTimeFormatter.Year;

        /// <summary>Days in the calendar's year (0 with no months).</summary>
        private static int MonthDays => Config.Months.Sum(m => m.Value);

        /// <summary>The months fit the game's year (Kerbin time: 426 days of 6 h).</summary>
        internal static bool Fits => MonthDays > 0 && Math.Abs(YearSeconds - MonthDays * DaySeconds) < 1;

        /// <summary>Days in a year: the calendar's, or the game's (365 on Earth time).</summary>
        private static int YearDays => Fits ? MonthDays : (int)Math.Round(YearSeconds / DaySeconds);

        /// <summary>The founding day's place in the count of days since year 0, day 1.</summary>
        private static long FoundingDay()
        {
            var s = KerbinResourcesSettings.Current;
            long dayOfYear;
            if (Fits)
            {
                var month = Mathf.Clamp(s.FoundedMonth, 1, Config.Months.Count);
                var before = Config.Months.Take(month - 1).Sum(m => m.Value);
                dayOfYear = before + Mathf.Clamp(s.FoundedDay, 1, Config.Months[month - 1].Value) - 1;
            }
            else dayOfYear = 0;
            return s.FoundedYear * (long)YearDays + dayOfYear;
        }

        /// <summary>A game time's day in the count of days since year 0 (game times before 0 are days before the founding).</summary>
        internal static long DayNumber(double ut) => FoundingDay() + (long)Math.Floor(ut / DaySeconds);

        internal static CalendarDate Date(double ut)
        {
            var n = DayNumber(ut);
            var yearDays = YearDays;
            var year = FloorDiv(n, yearDays);
            var dayOfYear = (int)(n - year * yearDays);
            var date = new CalendarDate { Year = year, Day = dayOfYear + 1, MonthName = "" };
            if (Fits)
            {
                var m = 0;
                while (dayOfYear >= Config.Months[m].Value) dayOfYear -= Config.Months[m++].Value;
                date.Month = m + 1;
                date.Day = dayOfYear + 1;
                date.MonthName = Config.Months[m].Key;
            }
            if (Config.Weekdays.Count > 0)
            {
                var first = Math.Max(0, Config.Weekdays.IndexOf(Config.FoundingWeekday));
                var sinceFounding = (long)Math.Floor(ut / DaySeconds);
                date.Weekday = Config.Weekdays[(int)Mod(first + sinceFounding, Config.Weekdays.Count)];
            }
            return date;
        }

        /// <summary>"3 Diznam 2263", or "Year 2263, day 45" on Earth time.</summary>
        internal static string Print(double ut)
        {
            var d = Date(ut);
            return d.Month > 0 ? d.Day + " " + d.MonthName + " " + d.Year : "Year " + d.Year + ", day " + d.Day;
        }

        /// <summary>"Akant, 3 Diznam 2263 (10/03/2263)": with the weekday, and the date as month/day/year.</summary>
        internal static string PrintLong(double ut)
        {
            var d = Date(ut);
            return (d.Month > 0 && !string.IsNullOrEmpty(d.Weekday) ? d.Weekday + ", " : "") + Print(ut) + " (" + PrintNumeric(ut) + ")";
        }

        /// <summary>"10/03/2263": month/day/year (day of year/year, "045/2263", on Earth time).</summary>
        internal static string PrintNumeric(double ut)
        {
            var d = Date(ut);
            return d.Month > 0 ? d.Month.ToString("00") + "/" + d.Day.ToString("00") + "/" + d.Year : d.Day.ToString("000") + "/" + d.Year;
        }

        /// <summary>A length of time in whole years and days on the calendar: "1 year, 34 days", "217 days", "today".</summary>
        internal static string Between(double fromUt, double toUt)
        {
            var days = DayNumber(toUt) - DayNumber(fromUt);
            if (days <= 0) return "today";
            var years = days / YearDays;
            var rest = days % YearDays;
            var parts = new List<string>();
            if (years > 0) parts.Add(years + " year" + (years == 1 ? "" : "s"));
            if (rest > 0) parts.Add(rest + " day" + (rest == 1 ? "" : "s"));
            return string.Join(", ", parts);
        }

        /// <summary>Whole years from one game time to another (birthday reached or not): every year has the same days.</summary>
        internal static int YearsBetween(double fromUt, double toUt) => (int)FloorDiv(DayNumber(toUt) - DayNumber(fromUt), YearDays);

        internal static double Year => YearDays * DaySeconds;
        internal static double Day => DaySeconds;

        private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
        private static long Mod(long a, long b) => ((a % b) + b) % b;
    }
}
