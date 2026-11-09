using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KspFlightPlanner.KerbinResources
{
    /// <summary>
    /// One spell on duty: a kerbal at a post (KSC, or aboard a craft, usually a station) from when they went on duty to
    /// when they came off (NaN while still on). Every shift is kept, so the management update (shift lengths, fatigue) has
    /// the history to work from (docs/KERBIN-RESOURCES.md "Duty roster").
    /// </summary>
    internal sealed class DutyShift
    {
        internal const string Ksc = "KSC";

        public string Kerbal = "";

        /// <summary>
        /// The post: a station (the main craft of the stack the kerbal was aboard: its vessel persistent id and name), or 0
        /// and "KSC". A kerbal in a ship docked to a station is on duty at the station, sitting in the ship.
        /// </summary>
        public uint PostId;
        public string PostName = "";

        /// <summary>
        /// A part of the post's own craft (its persistent id, 0 at KSC). It follows the post through dockings, when the
        /// stack's id and name can change: the kerbal stays on duty while they're aboard the stack that part is in, so a
        /// kerbal who leaves in a ship undocking from the station comes off duty.
        /// </summary>
        public uint PostPartId;

        /// <summary>The craft the kerbal sits in now: the post itself, or a ship docked to it ("" at KSC). Kept up to date.</summary>
        public string Seat = "";

        /// <summary>Sitting in a ship docked to the post, not in the station's own parts (Traffic Controller's controllers must be in its own parts).</summary>
        public bool InDockedShip;

        /// <summary>The part the kerbal sits in now (its persistent id, 0 at KSC). Kept up to date.</summary>
        public uint SeatPartId;

        /// <summary>
        /// The job they're doing now ("Traffic Controller", "Shuttle pilot"), given by the add-on that gives that job
        /// (<see cref="DutyRoster.JobOf"/>); "" while no add-on has one for them, shown as <see cref="AwaitingOrders"/>.
        /// </summary>
        public string Job = "";

        /// <summary>Every job held during the shift, in order, each with when it started: the history shifts and fatigue will use.</summary>
        public readonly List<KeyValuePair<double, string>> Jobs = new List<KeyValuePair<double, string>>();

        internal const string AwaitingOrders = "Awaiting orders";

        /// <summary>The job as shown: the title, or "Awaiting orders".</summary>
        internal string JobTitle => string.IsNullOrEmpty(Job) ? AwaitingOrders : Job;

        /// <summary>Sets the job now; a change is added to the shift's job history.</summary>
        internal void SetJob(string job, double now)
        {
            job = job ?? "";
            if (Jobs.Count > 0 && Job == job) return;
            Job = job;
            Jobs.Add(new KeyValuePair<double, string>(now, job));
        }

        /// <summary>The body whose SOI the post is in (Kerbin for KSC).</summary>
        public string Body = "";

        public double StartUt;
        public double EndUt = double.NaN;

        /// <summary>Why the shift ended: "taken off" (by the player), "left the post", "dead", and so on.</summary>
        public string EndReason = "";

        internal bool OnNow => double.IsNaN(EndUt);

        internal bool AtKsc => PostId == 0 && PostPartId == 0;

        internal double Length(double now) => (OnNow ? now : EndUt) - StartUt;

        internal void Save(ConfigNode n)
        {
            n.AddValue("kerbal", Kerbal);
            n.AddValue("postId", PostId);
            n.AddValue("postName", PostName);
            n.AddValue("postPartId", PostPartId);
            n.AddValue("seat", Seat);
            n.AddValue("inDockedShip", InDockedShip);
            n.AddValue("seatPartId", SeatPartId);
            n.AddValue("job", Job);
            foreach (var j in Jobs)
            {
                var jn = n.AddNode("JOB");
                jn.AddValue("since", j.Key.ToString("R", CultureInfo.InvariantCulture));
                jn.AddValue("title", j.Value);
            }
            n.AddValue("body", Body);
            n.AddValue("start", StartUt.ToString("R", CultureInfo.InvariantCulture));
            if (!OnNow)
            {
                n.AddValue("end", EndUt.ToString("R", CultureInfo.InvariantCulture));
                n.AddValue("endReason", EndReason);
            }
        }

        internal static DutyShift Load(ConfigNode n)
        {
            var s = Read(n);
            foreach (var jn in n.GetNodes("JOB")) s.Jobs.Add(new KeyValuePair<double, string>(Flight.Num(jn, "since"), jn.GetValue("title") ?? ""));
            return s;
        }

        private static DutyShift Read(ConfigNode n) => new DutyShift
        {
            Kerbal = n.GetValue("kerbal") ?? "",
            PostId = Flight.Id(n, "postId"),
            PostName = n.GetValue("postName") ?? "",
            PostPartId = Flight.Id(n, "postPartId"),
            Seat = n.GetValue("seat") ?? "",
            InDockedShip = n.GetValue("inDockedShip") == "True",
            SeatPartId = Flight.Id(n, "seatPartId"),
            Job = n.GetValue("job") ?? "",
            Body = n.GetValue("body") ?? "",
            StartUt = Flight.Num(n, "start"),
            EndUt = n.HasValue("end") ? Flight.Num(n, "end") : double.NaN,
            EndReason = n.GetValue("endReason") ?? "",
        };
    }

    /// <summary>
    /// Where a kerbal is right now: at KSC, or aboard a craft (the stack, the part they're in, which craft of the stack
    /// that part came from), and the body it's at.
    /// </summary>
    internal struct KerbalWhereabouts
    {
        public bool AtKsc;
        public uint VesselId;
        public string VesselName;
        public uint PartId;
        public string Body;

        /// <summary>The craft of the stack the kerbal sits in, and the stack's main craft (the station) with one of its parts.</summary>
        public string Craft;
        public string MainCraft;
        public uint MainAnchor;

        /// <summary>In a ship docked to the stack's main craft, not in the main craft's own parts.</summary>
        public bool InDockedShip => !string.IsNullOrEmpty(Craft) && Craft != MainCraft;

        /// <summary>Is the part with this persistent id part of the craft the kerbal is aboard?</summary>
        public Func<uint, bool> CraftHasPart;
    }

    /// <summary>
    /// Who's on duty, and where: a kerbal at KSC (standing in for the tracking station, for Kerbin's SOI) or aboard a
    /// craft. Only kerbals on duty earn controller experience (<see cref="ControlLog"/>). KR keeps the roster; Traffic
    /// Controller decides who may control what (a pilot, in the station's own parts, with a CommNet link).
    ///
    /// A kerbal comes off duty by themselves when they leave their post: a KSC kerbal put on a ship, a station kerbal who
    /// leaves the station, or one who dies or goes missing. Checked every few seconds (<see cref="LookOver"/>).
    /// </summary>
    internal sealed class DutyRoster
    {
        private readonly List<DutyShift> _shifts = new List<DutyShift>();

        /// <summary>
        /// The job title for a kerbal on duty, from the add-on whose job it is (set by Traffic Controller: "Traffic
        /// Controller", "Shuttle pilot"). Null or "" when it has none for them: they're shown as awaiting orders. Asked when
        /// they go on duty and every few seconds after, as they move.
        /// </summary>
        internal static Func<DutyShift, string> JobOf;

        private static void UpdateJob(DutyShift s, double now)
        {
            string job = null;
            try { job = JobOf?.Invoke(s); }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[KspFlightPlanner] Kerbin Resources: job title for " + s.Kerbal + " failed: " + e.Message); }
            s.SetJob(job, now);
        }

        internal IReadOnlyList<DutyShift> Shifts => _shifts;

        /// <summary>Everyone on duty now.</summary>
        internal IEnumerable<DutyShift> OnDuty => _shifts.Where(s => s.OnNow);

        /// <summary>The kerbals on duty in this body's SOI now (at KSC for Kerbin, or aboard craft there).</summary>
        internal IEnumerable<DutyShift> OnDutyAt(string body) => OnDuty.Where(s => s.Body == body);

        /// <summary>A kerbal's shift now, or null if they're off duty.</summary>
        internal DutyShift ShiftOf(string kerbal) => _shifts.FirstOrDefault(s => s.OnNow && s.Kerbal == kerbal);

        internal IEnumerable<DutyShift> ShiftsOf(string kerbal) => _shifts.Where(s => s.Kerbal == kerbal);

        /// <summary>Total time a kerbal has been on duty (s), the shift now counted to now.</summary>
        internal double TimeOnDuty(string kerbal, double now) => ShiftsOf(kerbal).Sum(s => Math.Max(0, s.Length(now)));

        /// <summary>
        /// Puts a kerbal on duty where they are now: at KSC if they're there, otherwise aboard their craft. Tourists and
        /// applicants can't be, nor the dead or missing. Returns the new shift (or the one they're already on), or null with
        /// the reason.
        /// </summary>
        internal DutyShift PutOn(string kerbal, double now, out string why)
        {
            why = "";
            var existing = ShiftOf(kerbal);
            if (existing != null) return existing;
            var k = Kerbal(kerbal);
            if (k == null) { why = kerbal + " isn't on the roster"; return null; }
            if (k.type != ProtoCrewMember.KerbalType.Crew) { why = "only hired crew go on duty"; return null; }
            if (k.rosterStatus == ProtoCrewMember.RosterStatus.Dead || k.rosterStatus == ProtoCrewMember.RosterStatus.Missing) { why = kerbal + " is " + k.rosterStatus.ToString().ToLower(); return null; }
            var at = Locate(k);
            if (at == null) { why = "couldn't find where " + kerbal + " is"; return null; }
            var w = at.Value;
            var shift = w.AtKsc
                ? new DutyShift { Kerbal = kerbal, PostName = DutyShift.Ksc, Body = HomeBody(), StartUt = now }
                : new DutyShift
                {
                    Kerbal = kerbal, PostId = w.VesselId, PostName = string.IsNullOrEmpty(w.MainCraft) ? w.VesselName : w.MainCraft,
                    PostPartId = w.MainAnchor != 0 ? w.MainAnchor : w.PartId, Seat = w.Craft ?? "", InDockedShip = w.InDockedShip,
                    SeatPartId = w.PartId, Body = w.Body, StartUt = now,
                };
            _shifts.Add(shift);
            UpdateJob(shift, now);
            return shift;
        }

        /// <summary>Takes a kerbal off duty (no change if they weren't on).</summary>
        internal void TakeOff(string kerbal, double now, string reason = "taken off")
        {
            var s = ShiftOf(kerbal);
            if (s == null) return;
            s.EndUt = now;
            s.EndReason = reason;
        }

        /// <summary>
        /// Ends the shifts of kerbals no longer at their post, and keeps the rest's post up to date (a station's name or id
        /// changed by a docking, its SOI by a transfer).
        /// </summary>
        internal void LookOver(double now)
        {
            foreach (var s in OnDuty.ToList())
            {
                var k = Kerbal(s.Kerbal);
                if (k == null) { TakeOff(s.Kerbal, now, "left the roster"); continue; }
                if (k.rosterStatus == ProtoCrewMember.RosterStatus.Dead) { TakeOff(s.Kerbal, now, "dead"); continue; }
                if (k.rosterStatus == ProtoCrewMember.RosterStatus.Missing) { TakeOff(s.Kerbal, now, "missing"); continue; }
                var at = Locate(k);
                if (at == null) continue; // can't tell right now (between scenes): leave it
                var w = at.Value;
                if (s.AtKsc)
                {
                    if (!w.AtKsc) TakeOff(s.Kerbal, now, "left KSC");
                    else UpdateJob(s, now);
                    continue;
                }
                if (w.AtKsc || w.CraftHasPart == null || !w.CraftHasPart(s.PostPartId)) { TakeOff(s.Kerbal, now, "left " + s.PostName); continue; }
                s.PostId = w.VesselId;
                if (!string.IsNullOrEmpty(w.MainCraft)) s.PostName = w.MainCraft;
                s.Seat = w.Craft ?? "";
                s.InDockedShip = w.InDockedShip;
                s.SeatPartId = w.PartId;
                if (!string.IsNullOrEmpty(w.Body)) s.Body = w.Body;
                UpdateJob(s, now);
            }
        }

        internal void RenameKerbal(string from, string to)
        {
            foreach (var s in _shifts)
                if (s.Kerbal == from) s.Kerbal = to;
        }

        /// <summary>KSP gave a craft a new persistent id: posts aboard it follow.</summary>
        internal void Renumber(uint from, uint to)
        {
            foreach (var s in OnDuty)
                if (s.PostId == from) s.PostId = to;
        }

        private static ProtoCrewMember Kerbal(string name)
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return null;
            try { return roster[name]; }
            catch (Exception) { return null; }
        }

        /// <summary>The body KSC is on (Kerbin, or whatever the planet pack calls home).</summary>
        private static string HomeBody() => FlightGlobals.GetHomeBody()?.bodyName ?? "Kerbin";

        /// <summary>
        /// Where a kerbal is: at KSC (available, on the roster), or the craft and part they're in, from the craft in the
        /// game (loaded ones in flight by their parts, the rest from their saved records). Null when they can't be found
        /// (assigned to a craft that isn't there, as can happen while scenes change).
        /// </summary>
        internal static KerbalWhereabouts? Locate(ProtoCrewMember k)
        {
            if (k.rosterStatus == ProtoCrewMember.RosterStatus.Available) return new KerbalWhereabouts { AtKsc = true, Body = HomeBody() };
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.Vessels != null)
            {
                foreach (var v in FlightGlobals.Vessels)
                {
                    if (v == null) continue;
                    if (v.loaded)
                    {
                        if (!v.parts.Any(p => p.protoModuleCrew.Any(c => c.name == k.name))) continue;
                        var map = CraftMap.Of(v);
                        var craft = map.CraftOfKerbal(k.name, out var partId);
                        return new KerbalWhereabouts
                        {
                            VesselId = v.persistentId, VesselName = v.vesselName, PartId = partId, Body = v.mainBody?.bodyName ?? "",
                            Craft = craft, MainCraft = map.Main, MainAnchor = map.MainAnchor(), CraftHasPart = map.HasPart,
                        };
                    }
                    if (v.protoVessel != null && Find(v.protoVessel, k.name) is KerbalWhereabouts found)
                    {
                        found.Body = v.mainBody?.bodyName ?? found.Body;
                        return found;
                    }
                }
                return null;
            }
            var vessels = HighLogic.CurrentGame?.flightState?.protoVessels;
            if (vessels == null) return null;
            foreach (var pv in vessels)
                if (Find(pv, k.name) is KerbalWhereabouts found)
                    return found;
            return null;
        }

        /// <summary>A kerbal aboard a craft's saved record: its id and name, their part, and the body its orbit is round.</summary>
        private static KerbalWhereabouts? Find(ProtoVessel pv, string kerbal)
        {
            if (!pv.protoPartSnapshots.Any(p => p.protoModuleCrew.Any(c => c.name == kerbal))) return null;
            var map = CraftMap.Of(pv);
            var craft = map.CraftOfKerbal(kerbal, out var partId);
            var bodyIndex = pv.orbitSnapShot?.ReferenceBodyIndex ?? -1;
            var bodies = FlightGlobals.Bodies;
            var body = bodies != null && bodyIndex >= 0 && bodyIndex < bodies.Count ? bodies[bodyIndex].bodyName : "";
            return new KerbalWhereabouts
            {
                VesselId = pv.persistentId, VesselName = pv.vesselName, PartId = partId, Body = body,
                Craft = craft, MainCraft = map.Main, MainAnchor = map.MainAnchor(), CraftHasPart = map.HasPart,
            };
        }

        internal void Save(ConfigNode node)
        {
            foreach (var s in _shifts) s.Save(node.AddNode("SHIFT"));
        }

        internal void Load(ConfigNode node)
        {
            _shifts.Clear();
            if (node == null) return;
            foreach (var n in node.GetNodes("SHIFT"))
            {
                var s = DutyShift.Load(n);
                if (s.Kerbal.Length > 0) _shifts.Add(s);
            }
        }
    }
}
