using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KspFlightPlanner.KerbinResources
{
    /// <summary>
    /// One thing a controller oversaw: a probe's burn, or a flight handed over to another controller. Written by Traffic
    /// Controller when it happens. Kinds and burn kinds are plain words so the rules that read them can change without
    /// the save changing (docs/KERBIN-RESOURCES.md "Controller log").
    /// </summary>
    internal sealed class ControlEntry
    {
        internal const string Burn = "burn";
        internal const string HandOff = "handoff";

        public double Ut;
        public string Kerbal = "";

        /// <summary><see cref="Burn"/> or <see cref="HandOff"/>.</summary>
        public string Kind = Burn;

        /// <summary>The body whose SOI the controller covered.</summary>
        public string Body = "";

        /// <summary>Where the controller was on duty: a station (its vessel persistent id), or 0 and "KSC".</summary>
        public uint PostId;
        public string PostName = "";

        /// <summary>The probe craft overseen.</summary>
        public uint VesselId;
        public string Vessel = "";

        /// <summary>For a burn: what it was (capture, correction, phasing, departure, docking) and its priority, 1 most urgent to 10.</summary>
        public string BurnKind = "";
        public int Priority;

        /// <summary>For a burn: how long it took (s), the time it held the controller's slot not counted.</summary>
        public double BurnSeconds;

        /// <summary>For a hand-off: the other controller (who took it, if this kerbal gave it; who gave it, if they took it), and their body.</summary>
        public string Other = "";
        public string OtherBody = "";

        /// <summary>For a hand-off: this kerbal gave the flight away (true) or took it (false).</summary>
        public bool Gave;

        internal void Save(ConfigNode n)
        {
            n.AddValue("ut", Ut.ToString("R", CultureInfo.InvariantCulture));
            n.AddValue("kerbal", Kerbal);
            n.AddValue("kind", Kind);
            n.AddValue("body", Body);
            n.AddValue("postId", PostId);
            n.AddValue("postName", PostName);
            n.AddValue("vesselId", VesselId);
            n.AddValue("vessel", Vessel);
            if (Kind == Burn)
            {
                n.AddValue("burnKind", BurnKind);
                n.AddValue("priority", Priority);
                n.AddValue("burnSeconds", BurnSeconds.ToString("R", CultureInfo.InvariantCulture));
            }
            else
            {
                n.AddValue("other", Other);
                n.AddValue("otherBody", OtherBody);
                n.AddValue("gave", Gave);
            }
        }

        internal static ControlEntry Load(ConfigNode n) => new ControlEntry
        {
            Ut = Flight.Num(n, "ut"),
            Kerbal = n.GetValue("kerbal") ?? "",
            Kind = n.GetValue("kind") ?? Burn,
            Body = n.GetValue("body") ?? "",
            PostId = Flight.Id(n, "postId"),
            PostName = n.GetValue("postName") ?? "",
            VesselId = Flight.Id(n, "vesselId"),
            Vessel = n.GetValue("vessel") ?? "",
            BurnKind = n.GetValue("burnKind") ?? "",
            Priority = int.TryParse(n.GetValue("priority"), out var p) ? p : 0,
            BurnSeconds = Flight.Num(n, "burnSeconds"),
            Other = n.GetValue("other") ?? "",
            OtherBody = n.GetValue("otherBody") ?? "",
            Gave = n.GetValue("gave") == "True",
        };
    }

    /// <summary>
    /// Every burn and hand-off a controller oversaw, in the order they happened: the controllers' experience, kept as
    /// events rather than a score, so Traffic Controller's capacity rules (and their settings) can count it however they
    /// like and recount it when they change. Only a kerbal on duty (<see cref="DutyRoster"/>) gets entries.
    /// </summary>
    internal sealed class ControlLog
    {
        private readonly List<ControlEntry> _entries = new List<ControlEntry>();

        internal IReadOnlyList<ControlEntry> Entries => _entries;

        internal IEnumerable<ControlEntry> Of(string kerbal) => _entries.Where(e => e.Kerbal == kerbal);

        internal int BurnsOf(string kerbal) => _entries.Count(e => e.Kerbal == kerbal && e.Kind == ControlEntry.Burn);

        internal int HandOffsOf(string kerbal) => _entries.Count(e => e.Kerbal == kerbal && e.Kind == ControlEntry.HandOff);

        /// <summary>
        /// A controller oversaw a probe's burn. Logged only if they're on duty now; their post and body come from the duty
        /// roster. Returns the entry, or null if they aren't on duty.
        /// </summary>
        internal ControlEntry Burned(DutyRoster duty, string kerbal, Vessel probe, string burnKind, int priority, double burnSeconds, double now)
        {
            var shift = duty.ShiftOf(kerbal);
            if (shift == null) return null;
            var e = new ControlEntry
            {
                Ut = now, Kerbal = kerbal, Kind = ControlEntry.Burn, Body = shift.Body, PostId = shift.PostId, PostName = shift.PostName,
                VesselId = probe?.persistentId ?? 0, Vessel = probe?.vesselName ?? "", BurnKind = burnKind ?? "", Priority = priority, BurnSeconds = burnSeconds,
            };
            _entries.Add(e);
            return e;
        }

        /// <summary>
        /// A flight handed from one controller to another (leaving one SOI for the next). Each side gets an entry if they're
        /// on duty. Returns how many entries were logged (0 to 2).
        /// </summary>
        internal int HandedOff(DutyRoster duty, string giver, string taker, Vessel probe, double now)
        {
            var from = duty.ShiftOf(giver);
            var to = duty.ShiftOf(taker);
            var logged = 0;
            if (from != null)
            {
                _entries.Add(new ControlEntry
                {
                    Ut = now, Kerbal = giver, Kind = ControlEntry.HandOff, Body = from.Body, PostId = from.PostId, PostName = from.PostName,
                    VesselId = probe?.persistentId ?? 0, Vessel = probe?.vesselName ?? "", Other = taker ?? "", OtherBody = to?.Body ?? "", Gave = true,
                });
                logged++;
            }
            if (to != null)
            {
                _entries.Add(new ControlEntry
                {
                    Ut = now, Kerbal = taker, Kind = ControlEntry.HandOff, Body = to.Body, PostId = to.PostId, PostName = to.PostName,
                    VesselId = probe?.persistentId ?? 0, Vessel = probe?.vesselName ?? "", Other = giver ?? "", OtherBody = from?.Body ?? "", Gave = false,
                });
                logged++;
            }
            return logged;
        }

        internal void RenameKerbal(string from, string to)
        {
            foreach (var e in _entries)
            {
                if (e.Kerbal == from) e.Kerbal = to;
                if (e.Other == from) e.Other = to;
            }
        }

        internal void Save(ConfigNode node)
        {
            foreach (var e in _entries) e.Save(node.AddNode("CONTROL"));
        }

        internal void Load(ConfigNode node)
        {
            _entries.Clear();
            if (node == null) return;
            foreach (var n in node.GetNodes("CONTROL"))
            {
                var e = ControlEntry.Load(n);
                if (e.Kerbal.Length > 0) _entries.Add(e);
            }
        }
    }
}
