using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.PowerHeat
{
    /// <summary>What only a craft in flight knows: the Sun now and the charge actually flowing.</summary>
    internal sealed class LivePower
    {
        public Sunlight.Now Sun;

        /// <summary>Net charge flow measured from the batteries (EC/s), or NaN while it can't be measured (full, empty, paused).</summary>
        public double Measured = double.NaN;
    }

    /// <summary>
    /// Measures a craft's net charge flow from its batteries: the change in stored charge over the last few seconds of game
    /// time. It catches everything, modded parts and wheels included, but not while the batteries are full or empty.
    /// </summary>
    internal static class PowerMeter
    {
        private sealed class Track
        {
            public readonly Queue<KeyValuePair<double, double>> Samples = new Queue<KeyValuePair<double, double>>();
        }

        private static readonly Dictionary<uint, Track> Tracks = new Dictionary<uint, Track>();

        internal static double Sample(uint vesselId, double ut, double stored, double capacity)
        {
            if (!Tracks.TryGetValue(vesselId, out var t)) Tracks[vesselId] = t = new Track();
            if (t.Samples.Count > 0 && ut < t.Samples.Last().Key) t.Samples.Clear(); // reverted or reloaded
            if (t.Samples.Count == 0 || ut > t.Samples.Last().Key) t.Samples.Enqueue(new KeyValuePair<double, double>(ut, stored));
            while (t.Samples.Count > 2 && ut - t.Samples.Peek().Key > 10) t.Samples.Dequeue();
            if (stored >= capacity - 0.01 || stored <= 0.01 || t.Samples.Count < 2) return double.NaN;
            var first = t.Samples.Peek();
            var span = ut - first.Key;
            return span > 0.5 ? (stored - first.Value) / span : double.NaN;
        }
    }

    /// <summary>The Power and Heat sections, drawn the same in the editor window and the Station window.</summary>
    internal static class PowerView
    {
        /// <summary>
        /// Power: what the panels, generators and fuel cells make, what's used, and whether the batteries last the night.
        /// <paramref name="toggle"/> (editor) lets each item be switched for a what-if; null shows them as they are.
        /// </summary>
        internal static void DrawPower(PowerBudget b, PowerParts parts, string where, LivePower live, Func<PowerItem, bool> isOn, Action<PowerItem> toggle, ref bool showParts, Action<IList<PowerItem>> cycle = null)
        {
            if (parts.Items.Count == 0 && parts.Capacity <= 0)
            {
                GUILayout.Label("   Nothing here makes, stores or uses electric charge.", Wrap());
                return;
            }
            var made = new List<string>();
            if (b.SolarBest > 0 || parts.Items.Any(i => i.Kind == PowerKind.Solar))
                made.Add("panels " + Ec(b.SolarBest) + " in full sun" + (b.Distance > 0 && Math.Abs(b.Distance - 1) > 0.005 ? " (" + Pct(b.Distance) + " of their rating out here)" : "") +
                         (b.SolarNow >= 0 ? ", " + Ec(b.SolarNow) + " right now" : ""));
            if (b.Generators > 0) made.Add("generators " + Ec(b.Generators) + " day and night");
            if (b.FuelCells > 0) made.Add("fuel cells up to " + Ec(b.FuelCells) + " (they burn fuel, so only cover what's missing)");
            GUILayout.Label("   Makes: " + (made.Count == 0 ? "nothing" : string.Join("; ", made)), Wrap());
            GUILayout.Label("   Uses: " + Ec(b.Use) + " with what's switched on", Wrap());
            if (live != null && !double.IsNaN(live.Measured))
                GUILayout.Label("   Measured from the batteries: " + (live.Measured >= 0 ? "+" : "−") + Ec(Math.Abs(live.Measured)) + (live.Measured >= 0 ? " charging" : " draining"), Wrap());
            GUILayout.Label("   Batteries: " + PowerReader.Num(b.Stored, "N0") + " of " + PowerReader.Num(b.Capacity, "N0") + " EC", Wrap());

            // The night.
            GUILayout.Label("   <b>Night " + where + ": " + Span(b.Night) + "</b>, day " + Span(b.Day), RichWrap());
            if (b.NightDrain <= 1e-9)
                GUILayout.Label("      " + Good("Covered:") + " the generators" + (b.NightCells > 0 ? " and fuel cells (" + Ec(b.NightCells) + ")" : "") + " carry the loads in the dark.", RichWrap());
            else
            {
                GUILayout.Label("      The batteries give " + Ec(b.NightDrain) + (b.NightCells > 0 ? " (fuel cells add " + Ec(b.NightCells) + ")" : "") + ": the night takes " +
                                PowerReader.Num(b.NightNeed, "N0") + " EC of " + PowerReader.Num(b.Capacity, "N0") + ". " +
                                (b.NightOk ? Good("Lasts the night") + " (full batteries last " + Span(b.FullLasts) + ")."
                                           : Bad("Runs out") + " after " + Span(b.FullLasts) + ": " + PowerReader.Num(b.NightNeed - b.Capacity, "N0") + " EC more battery, or " + Ec(b.NightDrain - b.Capacity / Math.Max(1, b.Night)) + " less use, would carry it."), RichWrap());
                GUILayout.Label("      " + (b.DaySurplus <= 1e-9 ? Bad("The day makes nothing spare:") + " the panels don't cover the loads even in full sun."
                                                                  : b.DayOk ? Good("The day refills it") + " in " + Span(b.Refill) + " of full sun."
                                                                            : Bad("The day is too short to refill it:") + " it takes " + Span(b.Refill) + " at " + Ec(b.DaySurplus) + " spare."), RichWrap());
            }
            if (b.SolarFixed > 0)
                GUILayout.Label("      " + Ec(b.SolarFixed) + " of that is fixed panels: they make it only while facing the Sun, much less as it crosses the sky.", Wrap());

            // Live: the Sun now.
            var sun = live?.Sun;
            if (sun != null)
            {
                string line;
                if (sun.Never) line = sun.Sunlit ? "In sunlight, with no night coming here." : "In the dark, with no sunrise coming here.";
                else if (sun.Sunlit) line = "In sunlight: sunset in " + Span(sun.UntilChange) + ", then " + Span(sun.NightLength) + " of night.";
                else
                {
                    var drain = !double.IsNaN(live.Measured) && live.Measured < 0 ? -live.Measured : b.NightDrain;
                    var lasts = drain > 1e-9 ? b.Stored / drain : double.PositiveInfinity;
                    line = "In the dark: sunrise in " + Span(sun.UntilChange) + ". " +
                           (drain <= 1e-9 ? Good("Nothing is draining the batteries.")
                                          : lasts >= sun.UntilChange ? Good("The charge lasts") + " (" + Span(lasts) + " at " + Ec(drain) + ")."
                                                                     : Bad("The charge runs out") + " in " + Span(lasts) + ", " + Span(sun.UntilChange - lasts) + " before sunrise.");
                }
                GUILayout.Label("   <b>Now:</b> " + line, RichWrap());
            }

            if (GUILayout.Button((showParts ? "      ▼ " : "      ▶ ") + "Parts (" + parts.Items.Select(i => i.Group.Length > 0 ? i.Group : i.Key).Distinct().Count() + ")", Link())) showParts = !showParts;
            if (!showParts) return;
            foreach (var kind in parts.Items.GroupBy(i => i.Kind).OrderBy(g => g.Key))
            foreach (var row in kind.GroupBy(i => i.Group.Length > 0 ? i.Group : i.Key))
            {
                var items = row.ToList();
                GUILayout.BeginHorizontal();
                GUILayout.Space(30);
                if (items.Count == 1)
                {
                    var i = items[0];
                    var on = isOn(i);
                    if (toggle != null)
                    {
                        if (GUILayout.Button(on ? "On" : "Off", GUILayout.Width(40))) toggle(i);
                    }
                    else GUILayout.Label(on ? "on" : "off", GUILayout.Width(30));
                    GUILayout.Label(i.Title + ": " + (i.Kind == PowerKind.Load ? "uses " : "makes ") + Ec(i.Rate) + KindNote(i), Wrap());
                }
                else
                {
                    // A part with several recipes (an ISRU): one switch that picks which runs.
                    var running = items.Where(isOn).ToList();
                    var label = running.Count == 0 ? "Off" : running.Count == 1 ? running[0].Option : running.Count + " at once";
                    if (cycle != null)
                    {
                        if (GUILayout.Button(label, GUILayout.Width(110))) cycle(items);
                    }
                    else GUILayout.Label(running.Count == 0 ? "off" : string.Join(", ", running.Select(r => r.Option)), GUILayout.Width(110));
                    var verb = items[0].Kind == PowerKind.Load ? "uses " : "makes ";
                    GUILayout.Label(items[0].PartTitle + ": " + (running.Count == 0 ? "off; " + items.Count + " recipes at " + Ec(items.Max(x => x.Rate)) + " each"
                                                                                    : verb + Ec(running.Sum(x => x.Rate)) + (running.Count > 1 ? " (" + running.Count + " recipes running together, each adds its heat)" : "")), Wrap());
                }
                GUILayout.EndHorizontal();
            }
        }

        private static string KindNote(PowerItem i)
        {
            switch (i.Kind)
            {
                case PowerKind.Solar: return (i.Tracking ? "" : ", fixed") + " at Kerbin's distance" + (i.Now >= 0 ? ", " + Ec(i.Now) + " now" : "");
                case PowerKind.Generator: return ", day and night";
                case PowerKind.FuelCell: return ", burning fuel";
                default: return "";
            }
        }

        /// <summary>Heat: what the drills and converters need from the radiators at their goal temperature, and what the radiators give.</summary>
        internal static void DrawHeat(PowerBudget b, PowerParts parts, Func<string, bool> converterOn, Func<Radiator, bool> radiatorOn, Action<Radiator> toggleRadiator)
        {
            if (parts.Cores.Count == 0)
            {
                GUILayout.Label("   No drills or converters here make core heat" + (parts.Radiators.Count > 0 ? "; the radiators only cool ordinary part heat." : "."), Wrap());
                return;
            }
            var verdict = b.HeatNeed <= 1e-9 ? "nothing running needs cooling"
                : b.HeatHave + 1e-6 >= b.HeatNeed ? Good("enough") + ": the cores hold at their goal"
                : Bad("short " + PowerReader.Num(b.HeatNeed - b.HeatHave, "0") + " kW") + ": the cores heat past their goal, slowing the converters";
            GUILayout.Label("   Cooling needed " + PowerReader.Num(b.HeatNeed, "0") + " kW, radiators give " + PowerReader.Num(b.HeatHave, "0") + " kW: " + verdict + ".", RichWrap());
            foreach (var c in parts.Cores)
            {
                var need = converterOn == null ? c.NeedKw : PowerReader.NeedOf(c, converterOn);
                var running = c.Converters.Count(k => converterOn == null ? k.Value : converterOn(k.Key));
                var text = "      " + c.Title + ": " + (running == 0 ? "off" : "needs " + PowerReader.Num(need, "0") + " kW at " + PowerReader.Num(c.GoalK, "N0") + " K") +
                           " (draws at most " + PowerReader.Num(c.MaxKw, "0") + " kW; stops at " + PowerReader.Num(c.ShutdownK, "N0") + " K)";
                if (c.CoreK >= 0) text += "; core " + PowerReader.Num(c.CoreK, "N0") + " K" + (c.Efficiency >= 0 ? ", running at " + Pct(c.Efficiency) : "");
                GUILayout.Label(text, Wrap());
            }
            foreach (var p in b.HeatProblems) GUILayout.Label("      ⚠ " + p + ".", Wrap());
            foreach (var r in parts.Radiators)
            {
                var on = radiatorOn(r);
                GUILayout.BeginHorizontal();
                GUILayout.Space(30);
                if (toggleRadiator != null)
                {
                    if (GUILayout.Button(on ? "On" : "Off", GUILayout.Width(40))) toggleRadiator(r);
                }
                else GUILayout.Label(on ? "on" : "off", GUILayout.Width(30));
                GUILayout.Label(r.Title + ": " + PowerReader.Num(r.Kw, "0") + " kW" + (r.ParentOnly ? ", only the part it's on and that part's children" : ""), Wrap());
                GUILayout.EndHorizontal();
            }
            if (b.HeatHave + 1e-6 < b.HeatNeed)
                GUILayout.Label("      Engineers aboard cut the heat a little; folded or switched-off radiators give nothing.", Wrap());
        }

        /// <summary>One line for a module: what it makes and uses, whether it lasts the night on its own, and its cooling.</summary>
        internal static string Summary(PowerBudget b, PowerParts p)
        {
            if (p.Items.Count == 0 && p.Capacity <= 0 && p.Cores.Count == 0) return "no power parts";
            var made = b.SolarBest + b.Generators;
            var s = "makes " + Ec(made) + (b.FuelCells > 0 ? " (+" + Ec(b.FuelCells) + " cells)" : "") + ", uses " + Ec(b.Use) + ", " + PowerReader.Num(b.Capacity, "N0") + " EC: ";
            if (b.NightDrain <= 1e-9) s += Good("night covered");
            else if (!b.NightOk) s += Bad("runs out after " + Span(b.FullLasts) + " of night");
            else if (!b.DayOk) s += Bad("lasts the night, day too short to refill");
            else s += Good("lasts the night");
            if (b.HeatNeed > 1e-9)
                s += "; " + (b.HeatHave + 1e-6 >= b.HeatNeed ? Good("cooling enough") : Bad("cooling short " + PowerReader.Num(b.HeatNeed - b.HeatHave, "0") + " kW"));
            return s;
        }

        internal static string Ec(double x) => PowerReader.Num(x, x >= 100 ? "N0" : x >= 10 ? "0.0" : "0.##") + " EC/s";

        internal static string Pct(double x) => PowerReader.Num(100 * x, "0") + "%";

        internal static string Span(double s) =>
            double.IsInfinity(s) ? "forever" : s < 1 ? "0s" : KSPUtil.dateTimeFormatter.PrintTime(s, 2, false);

        private static string Good(string s) => "<color=#8BE28B>" + s + "</color>";

        private static string Bad(string s) => "<color=#FF8A6A>" + s + "</color>";

        private static GUIStyle _wrap, _richWrap, _link;

        internal static GUIStyle Wrap() => _wrap ?? (_wrap = new GUIStyle(GUI.skin.label) { wordWrap = true });

        internal static GUIStyle RichWrap() => _richWrap ?? (_richWrap = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });

        private static GUIStyle Link() => _link ?? (_link = new GUIStyle(GUI.skin.label) { richText = true });
    }
}
