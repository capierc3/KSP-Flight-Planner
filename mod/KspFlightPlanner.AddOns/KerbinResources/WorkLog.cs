using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KspFlightPlanner.KerbinResources
{
    /// <summary>
    /// One piece of survey or science work a kerbal did: a biome mapped, a landmark spotted, a body's survey report filed,
    /// or a probe experiment sent home. Written by Roles when it happens, kept as events like the controller log, so
    /// experience rules can count them later (docs/KERBIN-RESOURCES.md "Survey and science roles").
    /// </summary>
    internal sealed class WorkEntry
    {
        internal const string Survey = "survey";
        internal const string Spotted = "spotted";
        internal const string Report = "report";
        internal const string Science = "science";

        public double Ut;
        public string Kerbal = "";

        /// <summary><see cref="Survey"/>, <see cref="Spotted"/>, <see cref="Report"/> or <see cref="Science"/>.</summary>
        public string Kind = Survey;

        public string Body = "";

        /// <summary>Where they were on duty: a station's name, or "KSC".</summary>
        public string PostName = "";

        /// <summary>The craft whose instruments were used (a probe, or the station itself).</summary>
        public string Vessel = "";

        /// <summary>What it was: "Greater Flats, narrow-band", "Gravity Scan from Lowlands (space low)".</summary>
        public string Detail = "";

        /// <summary>For science: what it earned once sent.</summary>
        public double Value;

        internal void Save(ConfigNode n)
        {
            n.AddValue("ut", Ut.ToString("R", CultureInfo.InvariantCulture));
            n.AddValue("kerbal", Kerbal);
            n.AddValue("kind", Kind);
            n.AddValue("body", Body);
            n.AddValue("postName", PostName);
            n.AddValue("vessel", Vessel);
            n.AddValue("detail", Detail);
            if (Value != 0) n.AddValue("value", Value.ToString("R", CultureInfo.InvariantCulture));
        }

        internal static WorkEntry Load(ConfigNode n) => new WorkEntry
        {
            Ut = Flight.Num(n, "ut"),
            Kerbal = n.GetValue("kerbal") ?? "",
            Kind = n.GetValue("kind") ?? Survey,
            Body = n.GetValue("body") ?? "",
            PostName = n.GetValue("postName") ?? "",
            Vessel = n.GetValue("vessel") ?? "",
            Detail = n.GetValue("detail") ?? "",
            Value = n.HasValue("value") ? Flight.Num(n, "value") : 0,
        };
    }

    /// <summary>Every survey and science entry, oldest first.</summary>
    internal sealed class WorkLog
    {
        private readonly List<WorkEntry> _entries = new List<WorkEntry>();

        internal IReadOnlyList<WorkEntry> Entries => _entries;

        internal IEnumerable<WorkEntry> Of(string kerbal) => _entries.Where(e => e.Kerbal == kerbal);

        internal void Add(WorkEntry e)
        {
            if (e != null && e.Kerbal.Length > 0) _entries.Add(e);
        }

        /// <summary>The game went back (a quickload or revert): work after that never happened.</summary>
        internal void ForgetAfter(double ut) => _entries.RemoveAll(e => e.Ut > ut);

        internal void RenameKerbal(string from, string to)
        {
            foreach (var e in _entries)
                if (e.Kerbal == from) e.Kerbal = to;
        }

        /// <summary>The data card's lines: biomes mapped and reports filed, science runs sent and what they earned.</summary>
        internal IEnumerable<KeyValuePair<string, string>> CardRows(string kerbal)
        {
            var mine = Of(kerbal).ToList();
            var survey = mine.Where(e => e.Kind == WorkEntry.Survey).ToList();
            var reports = mine.Where(e => e.Kind == WorkEntry.Report).ToList();
            var spotted = mine.Count(e => e.Kind == WorkEntry.Spotted);
            if (survey.Count > 0 || reports.Count > 0 || spotted > 0)
            {
                var parts = new List<string>();
                if (survey.Count > 0) parts.Add(survey.Count + " biome report" + (survey.Count == 1 ? "" : "s"));
                if (spotted > 0) parts.Add(spotted + " landmark" + (spotted == 1 ? "" : "s") + " spotted");
                if (reports.Count > 0) parts.Add("survey reports filed for " + string.Join(", ", reports.Select(r => r.Body).Distinct().ToArray()));
                yield return new KeyValuePair<string, string>("Surveyed", string.Join("; ", parts.ToArray()));
            }
            var science = mine.Where(e => e.Kind == WorkEntry.Science).ToList();
            if (science.Count > 0)
                yield return new KeyValuePair<string, string>("Science sent",
                    science.Count + " run" + (science.Count == 1 ? "" : "s") + " from " +
                    string.Join(", ", science.Select(e => e.Vessel).Distinct().ToArray()) + ": " +
                    science.Sum(e => e.Value).ToString("0.#", CultureInfo.InvariantCulture) + " science");
        }

        internal void Save(ConfigNode node)
        {
            foreach (var e in _entries) e.Save(node.AddNode("ENTRY"));
        }

        internal void Load(ConfigNode node)
        {
            _entries.Clear();
            if (node == null) return;
            foreach (var n in node.GetNodes("ENTRY"))
            {
                var e = WorkEntry.Load(n);
                if (e.Kerbal.Length > 0) _entries.Add(e);
            }
        }
    }
}
