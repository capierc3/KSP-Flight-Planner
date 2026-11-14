using System;
using System.Collections.Generic;
using System.Linq;

namespace KspFlightPlanner.PowerHeat
{
    /// <summary>
    /// A craft's charge through a day and a night, and its core heat against its radiators (MATH.md §13.3–13.4). The same
    /// sums serve the editor (what-if switches, a chosen place) and the Station window (what's on now, where it is now).
    /// </summary>
    internal sealed class PowerBudget
    {
        // ---- Power (EC/s) ----
        public double SolarBest;   // panels switched on, facing the Sun, at this distance from it
        public double SolarFixed;  // the part of SolarBest from fixed (non-tracking) panels
        public double SolarNow = -1; // loaded craft: what the panels make this moment
        public double Generators;  // RTGs: day and night
        public double FuelCells;   // the most the fuel cells switched on can make
        public double Use;         // every load switched on
        public double Distance;    // share of the panels' rating at this distance from the Sun

        // ---- Day and night (s) ----
        public double Night, Day;

        // ---- Batteries (EC) ----
        public double Stored, Capacity;

        // ---- Heat (kW) ----
        public double HeatNeed, HeatHave;
        public readonly List<string> HeatProblems = new List<string>();

        /// <summary>Charge drawn from the batteries through the night (EC/s): loads less generators, less the fuel cells' help.</summary>
        public double NightDrain => Math.Max(0, Use - Generators - FuelCells);

        /// <summary>The fuel cells' share at night (EC/s): they only cover what the generators can't.</summary>
        public double NightCells => Math.Min(FuelCells, Math.Max(0, Use - Generators));

        /// <summary>Spare charge in full sun (EC/s), fuel cells off: what refills the batteries.</summary>
        public double DaySurplus => SolarBest + Generators - Use;

        /// <summary>Charge the night takes from the batteries (EC).</summary>
        public double NightNeed => NightDrain * Night;

        /// <summary>How long full batteries last at night (s); infinity when nothing drains them.</summary>
        public double FullLasts => NightDrain > 1e-9 ? Capacity / NightDrain : double.PositiveInfinity;

        /// <summary>Time in full sun to win the night's charge back (s); infinity when the day makes nothing spare.</summary>
        public double Refill => NightNeed <= 0 ? 0 : DaySurplus > 1e-9 ? NightNeed / DaySurplus : double.PositiveInfinity;

        public bool NightOk => NightNeed <= Capacity + 1e-6;
        public bool DayOk => Refill <= Day + 1e-6;

        /// <summary>
        /// Adds up the craft. <paramref name="on"/> decides what's switched on (the editor's what-ifs; null = as the parts
        /// are). <paramref name="sunDistance"/> is the distance from the Sun the panels are rated against (m).
        /// </summary>
        internal static PowerBudget Of(PowerParts parts, double sunDistance, double night, double day, Func<PowerItem, bool> on = null, Func<string, bool> converterOn = null, Func<Radiator, bool> radiatorOn = null)
        {
            on = on ?? (i => i.On);
            radiatorOn = radiatorOn ?? (r => r.On);
            var b = new PowerBudget { Night = night, Day = day, Stored = parts.Stored, Capacity = parts.Capacity };
            var anyNow = false;
            foreach (var i in parts.Items)
            {
                if (!on(i)) continue;
                switch (i.Kind)
                {
                    case PowerKind.Solar:
                        var scale = DistanceScale(i, sunDistance);
                        b.Distance = scale;
                        b.SolarBest += i.Rate * scale;
                        if (!i.Tracking) b.SolarFixed += i.Rate * scale;
                        if (i.Now >= 0)
                        {
                            anyNow = true;
                            b.SolarNow = Math.Max(0, b.SolarNow) + i.Now;
                        }
                        break;
                    case PowerKind.Generator: b.Generators += i.Rate; break;
                    case PowerKind.FuelCell: b.FuelCells += i.Rate; break;
                    default: b.Use += i.Rate; break;
                }
            }
            if (!anyNow) b.SolarNow = -1;
            b.Heat(parts, converterOn, radiatorOn);
            return b;
        }

        /// <summary>A panel's share of its rating at this distance from the Sun: its own power curve, or the inverse-square law without one.</summary>
        internal static double DistanceScale(PowerItem panel, double sunDistance)
        {
            if (sunDistance <= 0) return 1;
            if (panel.PowerCurve != null && panel.PowerCurve.Curve != null && panel.PowerCurve.Curve.length > 0)
                return Math.Max(0, panel.PowerCurve.Evaluate((float)sunDistance));
            const double kerbin = 13599840256;
            return kerbin * kerbin / (sunDistance * sunDistance);
        }

        /// <summary>
        /// Core heat against radiators. Each core asks for its running converters' heat at the goal, added up, but can't draw
        /// more than MaxCoolant. Radiators pool their cooling for every core, except parent-only panels, which only reach
        /// the part they're mounted on and its other children.
        /// </summary>
        private void Heat(PowerParts parts, Func<string, bool> converterOn, Func<Radiator, bool> radiatorOn)
        {
            var radiators = parts.Radiators.Where(radiatorOn).ToList();
            var pooled = radiators.Where(r => !r.ParentOnly).Sum(r => r.Kw);
            var cores = parts.Cores.Select(c => new { Core = c, Need = converterOn == null ? c.NeedKw : PowerReader.NeedOf(c, converterOn) }).Where(c => c.Need > 0).ToList();
            HeatNeed = cores.Sum(c => Math.Min(c.Need, c.Core.MaxKw));
            var local = 0.0;
            foreach (var c in cores)
            {
                var near = radiators.Where(r => r.ParentOnly && r.Reaches.Contains(c.Core.PartId)).Sum(r => r.Kw);
                local += Math.Min(near, Math.Min(c.Need, c.Core.MaxKw));
                if (c.Need > c.Core.MaxKw + 1e-6)
                    HeatProblems.Add(c.Core.Title + " makes " + PowerReader.Num(c.Need, "0") + " kW but can only shed " + PowerReader.Num(c.Core.MaxKw, "0") + " kW: switch a converter off");
            }
            HeatHave = pooled + local;
            var unreached = radiators.Where(r => r.ParentOnly && !cores.Any(c => r.Reaches.Contains(c.Core.PartId))).ToList();
            if (cores.Count > 0 && unreached.Count > 0)
                HeatProblems.Add(unreached.Count + " radiator panel" + (unreached.Count == 1 ? "" : "s") + " (" + string.Join(", ", unreached.Select(r => r.Title).Distinct()) +
                                 ") only cool the part they're mounted on, and no drill or converter is next to them");
        }
    }
}
