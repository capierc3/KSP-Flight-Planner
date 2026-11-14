using System;
using System.Collections.Generic;
using System.Linq;
using CommNet;
using KspFlightPlanner.KerbinResources;
using CrewLog = KspFlightPlanner.KerbinResources.FlightLog;
using DutyShift = KspFlightPlanner.KerbinResources.DutyShift;

namespace KspFlightPlanner.Traffic
{
    /// <summary>
    /// The controller rules for route craft with no pilot aboard (docs/TRAFFIC-CONTROLLER.md "Controllers"). The probe core
    /// flies; a Traffic Controller on duty uploads each burn beforehand and checks the course after it, clears craft in and
    /// out of their station's keep-out zone, and hands flights to the next SOI's controllers.
    /// <list type="bullet">
    /// <item>Stage A: upload, after-burn check, missed burns, departure and docking clearance.</item>
    /// <item>Stage B: a CommNet link to the probe at upload and for the check; hand-offs need a link between controllers.</item>
    /// <item>Stage C: each controller has slots by experience; burns and craft in the zone take them; the most urgent win.</item>
    /// <item>Stage D: departure approval: every burn checked before it leaves, and the player asked if one won't be covered.</item>
    /// </list>
    /// </summary>
    internal static partial class CrewRules
    {
        internal const string Capture = "capture", Correction = "correction", Phasing = "phasing", Transfer = "transfer", Departure = "departure";

        private static TrafficSettings Settings => TrafficSettings.Current;

        // ---------------- Who needs a controller, and what kind of burn ----------------

        /// <summary>The controller rules apply to this route: they're on, and no pilot is aboard (a probe, or a craft with crew but no pilot).</summary>
        internal static bool NeedsController(Route route) => NeedsController(route.Crew);

        /// <summary>The same for a craft's crew list (names).</summary>
        internal static bool NeedsController(IEnumerable<string> crew) =>
            Settings.ControllerRules && !(crew ?? Enumerable.Empty<string>()).Any(k => CrewLog.TraitOf(k) == "Pilot");

        /// <summary>The kind of burn, from the title the planner gave it ("Capture at Mun", "Course correction to Kerbin"…).</summary>
        internal static string KindOf(string title)
        {
            var t = (title ?? "").ToLowerInvariant();
            if (t.Contains("capture")) return Capture;
            if (t.Contains("correction")) return Correction;
            if (t.StartsWith("leave") || t.Contains("transfer") || t.Contains("escape") || t.Contains("intercept")) return Transfer;
            return Phasing;
        }

        /// <summary>
        /// A burn's priority, 1 (most urgent) to 10 (can wait hours). Critical burns, the ones whose skipping would lose the
        /// ship, are always 1; the rest come from the settings (correction 3, phasing 5, transfer 7, departure 10 to start with).
        /// </summary>
        internal static int PriorityOf(string kind, bool critical)
        {
            if (critical) return 1;
            var s = Settings;
            return kind == Correction ? s.PriorityCorrection : kind == Transfer ? s.PriorityTransfer : kind == Departure ? s.PriorityDeparture : s.PriorityPhasing;
        }

        /// <summary>Would skipping this burn lose the ship? Worked out on the orbit it's on now, so asked once it's round the burn's body.</summary>
        internal static bool IsCritical(Vessel ship, RouteBurn burn) => IsCritical(ship.orbitDriver.orbit, burn);

        /// <summary>
        /// Would skipping this burn lose the ship, <paramref name="orbit"/> being the path it's on when the burn comes? It
        /// would when, coasting on without it, the ship escapes the body it would stay round after it (a capture), or dives
        /// into the air or the ground where the burn would have kept it clear. docs/MATH.md §11.17.
        /// </summary>
        internal static bool IsCritical(Orbit orbit, RouteBurn burn)
        {
            var pos = orbit.getRelativePositionAtUT(burn.Ut);
            var vel = orbit.getOrbitalVelocityAtUT(burn.Ut);
            Ships.Axes(pos, vel, out var prograde, out var normal, out var radial);
            var after = new Orbit();
            after.UpdateFromStateVectors(pos, vel + prograde * burn.Prograde + normal * burn.Normal + radial * burn.Radial, orbit.referenceBody, burn.Ut);
            return Escapes(orbit) && !Escapes(after) || Low(orbit, burn.Ut) && !Low(after, burn.Ut);
        }

        private static bool Escapes(Orbit o) => o.eccentricity >= 1 || o.ApR > o.referenceBody.sphereOfInfluence;

        /// <summary>Periapsis in the air or the ground, still ahead (an escape path whose periapsis has passed is clear of it).</summary>
        private static bool Low(Orbit o, double ut)
        {
            var body = o.referenceBody;
            if (o.PeR >= body.Radius + (body.atmosphere ? body.atmosphereDepth : 0)) return false;
            return !(o.eccentricity >= 1 && o.getObtAtUT(ut) > 0);
        }

        /// <summary>
        /// Works out, for each burn still to come, the SOI it happens in (KSP's patched conics along the planned path, burns
        /// applied), its kind, whether it's critical, its priority and its slot length. Used to plan the controllers' load
        /// ahead (stage C) and for departure approval (stage D). <paramref name="isCraft"/>: <paramref name="ship"/> is the
        /// craft itself (its mass gives the burns' lengths); a docked craft's stack isn't, so a two-minute slot stands in.
        /// </summary>
        internal static void PlanControl(Route route, Vessel ship, double now, bool isCraft)
        {
            var before = new List<RouteBurn>();
            foreach (var b in route.Burns.OrderBy(x => x.Ut))
            {
                if (!b.Done && b.Ut >= now)
                {
                    try
                    {
                        var o = Ships.PathAt(ship, before, now, b.Ut);
                        b.PlannedBody = o.referenceBody.bodyName;
                        b.Kind = KindOf(b.Title);
                        b.Critical = b.Kind == Capture || IsCritical(o, b);
                        b.Priority = PriorityOf(b.Kind, b.Critical);
                    }
                    catch (Exception e) { UnityEngine.Debug.LogWarning("[KspFlightPlanner] Planning controllers for " + route.VesselName + ": " + e.Message); }
                    b.SlotSeconds = isCraft ? Math.Max(PhysicsFlyer.MinBurnS, PhysicsFlyer.BurnSeconds(ship, b.DeltaV)) : DefaultSlotS;
                }
                before.Add(b);
            }
        }

        /// <summary>A burn's slot length when its craft's engines can't be read yet (s).</summary>
        private const double DefaultSlotS = 120;

        // ---------------- Who controls ----------------

        private static string HomeBody => FlightGlobals.GetHomeBody()?.bodyName ?? "Kerbin";

        /// <summary>Traffic Controllers on duty covering a body's SOI: those on its stations, and for the home body those at KSC.</summary>
        internal static IEnumerable<DutyShift> ControllersFor(string body)
        {
            var duty = KerbinResourcesScenario.Instance?.Duty;
            if (duty == null) return Enumerable.Empty<DutyShift>();
            var home = HomeBody;
            return duty.OnDuty.Where(s => IsTrafficController(s) && (s.AtKsc ? body == home : s.Body == body)).ToList();
        }

        /// <summary>Traffic Controllers on duty aboard this station, in its own parts.</summary>
        internal static IEnumerable<DutyShift> ControllersAt(Vessel station)
        {
            var duty = KerbinResourcesScenario.Instance?.Duty;
            if (duty == null || station == null) return Enumerable.Empty<DutyShift>();
            return duty.OnDuty.Where(s => IsTrafficController(s) && !s.AtKsc && !s.InDockedShip && s.PostId == station.persistentId).ToList();
        }

        /// <summary>A kerbal's shift if they're on duty as a Traffic Controller now; null otherwise.</summary>
        private static DutyShift OnDutyController(string kerbal)
        {
            if (string.IsNullOrEmpty(kerbal)) return null;
            var shift = KerbinResourcesScenario.Instance?.Duty.ShiftOf(kerbal);
            return IsTrafficController(shift) ? shift : null;
        }

        /// <summary>How a controller is named on the radio: "KSC Control (Jebediah Rockmore)", "Mun Station Control (…)".</summary>
        internal static string CallName(string post, string kerbal) => (string.IsNullOrEmpty(post) ? DutyShift.Ksc : post) + " Control (" + kerbal + ")";

        internal static string CallName(DutyShift s) => CallName(s.AtKsc ? DutyShift.Ksc : s.PostName, s.Kerbal);

        private static string PostOf(DutyShift s) => s.AtKsc ? DutyShift.Ksc : s.PostName;

        // ---------------- Stage B: CommNet links ----------------

        /// <summary>CommNet is on in this save and running in this scene; without it every link counts as there.</summary>
        private static bool CommNetOn() => Settings.RequireLink && CommNetScenario.CommNetEnabled && CommNetNetwork.Instance != null && CommNetNetwork.Instance.CommNet != null;

        /// <summary>Is there a CommNet path between two craft now (relays allowed)?</summary>
        private static bool Path(Vessel a, Vessel b)
        {
            if (a == null || b == null) return false;
            if (a == b) return true;
            var ca = a.connection?.Comm;
            var cb = b.connection?.Comm;
            if (ca == null || cb == null) return false;
            return CommNetNetwork.Instance.CommNet.FindPath(ca, new CommPath(), cb);
        }

        /// <summary>
        /// Does this controller have a CommNet link to the craft now? From KSC: the craft's link home (KSP's ground stations).
        /// From a station: a path from the station to the craft. Always, with CommNet off or the setting off.
        /// </summary>
        internal static bool Linked(DutyShift controller, Vessel craft)
        {
            if (controller == null || craft == null) return false;
            if (!CommNetOn()) return true;
            if (controller.AtKsc) return craft.connection != null && craft.connection.IsConnectedHome;
            return Path(Ships.Find(controller.PostId), craft);
        }

        /// <summary>Can two controllers talk, to hand a flight over? KSC to a station: the station's link home; station to station: a path between them.</summary>
        internal static bool Linked(DutyShift a, DutyShift b)
        {
            if (a == null || b == null) return false;
            if (!CommNetOn() || a.AtKsc && b.AtKsc || a.PostId == b.PostId && !a.AtKsc && !b.AtKsc) return true;
            if (a.AtKsc || b.AtKsc)
            {
                var station = Ships.Find(a.AtKsc ? b.PostId : a.PostId);
                return station?.connection != null && station.connection.IsConnectedHome;
            }
            return Path(Ships.Find(a.PostId), Ships.Find(b.PostId));
        }

        /// <summary>
        /// Hand-off: the route's flight is known to the controllers of <paramref name="body"/>'s SOI. It is if that's where it
        /// was last controlled, or nobody has controlled it yet. Otherwise the controller who last had it (or another in that
        /// SOI) hands it to one here who has a link to the craft, over a link between the two; both get it on their log. With
        /// nobody left in the old SOI to hand it over, one here picks it up on their own. False (with why) when it can't be
        /// handed over yet: nobody here with a link to it, or no link between the two controllers.
        /// </summary>
        internal static bool HandOff(Route route, Vessel ship, string body, double now, out string why)
        {
            why = "";
            if (string.IsNullOrEmpty(route.KnownTo) || route.KnownTo == body) return true;
            var takers = ControllersFor(body).Where(c => Linked(c, ship)).ToList();
            if (takers.Count == 0)
            {
                why = ControllersFor(body).Any() ? "no controller round " + body + " has a CommNet link to it" : "no Traffic Controller on duty for " + body + "'s SOI";
                return false;
            }
            var giver = OnDutyController(route.HandledBy);
            if (giver == null || (giver.AtKsc ? HomeBody : giver.Body) != route.KnownTo) giver = ControllersFor(route.KnownTo).FirstOrDefault();
            if (giver == null)
            {
                var first = takers[0];
                TrafficScenario.Note(route, CallName(first) + ": picked it up coming into " + body + "'s SOI (nobody round " + route.KnownTo + " to hand it over).", false);
                route.KnownTo = body;
                route.HandledBy = first.Kerbal;
                route.HandledByPost = PostOf(first);
                return true;
            }
            var taker = takers.FirstOrDefault(t => Linked(giver, t));
            if (taker == null)
            {
                why = CallName(giver) + " has no CommNet link to " + body + "'s controllers to hand it over";
                return false;
            }
            var kr = KerbinResourcesScenario.Instance;
            kr?.Control.HandedOff(kr.Duty, giver.Kerbal, taker.Kerbal, ship, now);
            TrafficScenario.Note(route, CallName(giver) + " hands it over to " + CallName(taker) + " for " + body + "'s SOI.", true);
            route.KnownTo = body;
            route.HandledBy = taker.Kerbal;
            route.HandledByPost = PostOf(taker);
            return true;
        }

        // ---------------- Stage C: capacity ----------------

        /// <summary>How many things a controller has handled: burns, hand-offs and clearances on their log (Kerbin Resources).</summary>
        internal static int Handled(string kerbal) => KerbinResourcesScenario.Instance?.Control.Of(kerbal).Count() ?? 0;

        /// <summary>How many slots a controller has, by experience: 2 under 10 handled, 4 under 20, 8 from 20 (settings).</summary>
        internal static int Capacity(string kerbal)
        {
            var s = Settings;
            var n = Handled(kerbal);
            return n >= s.VeteranFrom ? s.CapacityVeteran : n >= s.MidFrom ? s.CapacityMid : s.CapacityNew;
        }

        /// <summary>A burn's slot: from half its length before its time to half after, plus a minute to check it (docs/MATH.md §11.17).</summary>
        internal static double SlotStart(RouteBurn b) => b.Ut - Math.Max(b.SlotSeconds, PhysicsFlyer.MinBurnS) / 2;

        internal static double SlotEnd(RouteBurn b) => b.Ut + Math.Max(b.SlotSeconds, PhysicsFlyer.MinBurnS) / 2 + Settings.SlotExtraSeconds;

        private static IEnumerable<Route> Routes => TrafficScenario.Instance?.Routes ?? Enumerable.Empty<Route>();

        /// <summary>The burns uploaded to a controller and not made yet, with their routes.</summary>
        private static IEnumerable<KeyValuePair<Route, RouteBurn>> Booked(string kerbal) =>
            Routes.SelectMany(r => r.Burns.Where(b => !b.Done && b.Uploaded && b.Controller == kerbal).Select(b => new KeyValuePair<Route, RouteBurn>(r, b)));

        /// <summary>How many craft a controller is handling inside their station's zone now.</summary>
        internal static int ZoneCraft(string kerbal) => Routes.Count(r => r.ZoneController == kerbal && Occupies(r));

        /// <summary>A craft in the zone counts against a slot booked this soon (s).</summary>
        private const double ZoneLookS = 300;

        /// <summary>A controller's load between two times: their uploaded burns whose slots overlap it, and craft in their zone if it's soon.</summary>
        internal static int Load(string kerbal, double from, double to, double now, RouteBurn except = null)
        {
            var burns = Booked(kerbal).Count(x => x.Value != except && SlotStart(x.Value) < to && SlotEnd(x.Value) > from);
            return burns + (from < now + ZoneLookS ? ZoneCraft(kerbal) : 0);
        }

        /// <summary>The most a controller has on at once from now till <paramref name="until"/>: their load at each slot's start.</summary>
        internal static int PeakLoad(string kerbal, double now, double until)
        {
            var peak = Load(kerbal, now, now + 1, now);
            foreach (var x in Booked(kerbal))
            {
                var t = Math.Max(now, SlotStart(x.Value));
                if (t < until) peak = Math.Max(peak, Load(kerbal, t, t + 1, now));
            }
            return peak;
        }

        /// <summary>
        /// A controller with a free slot for this burn, least loaded first. With none free, one whose slot can be taken from a
        /// less urgent burn (a higher priority number): that burn is returned in <paramref name="bump"/>, to lose its upload.
        /// Null when every slot is held by burns as urgent or more.
        /// </summary>
        private static DutyShift SlotFor(List<DutyShift> candidates, RouteBurn burn, double now, out KeyValuePair<Route, RouteBurn> bump)
        {
            bump = default;
            double from = SlotStart(burn), to = SlotEnd(burn);
            var free = candidates.Where(c => Load(c.Kerbal, from, to, now, burn) < Capacity(c.Kerbal)).OrderBy(c => Load(c.Kerbal, from, to, now, burn)).FirstOrDefault();
            if (free != null) return free;
            DutyShift best = null;
            foreach (var c in candidates)
                foreach (var x in Booked(c.Kerbal))
                {
                    if (x.Value == burn || x.Value.Priority <= burn.Priority || SlotStart(x.Value) >= to || SlotEnd(x.Value) <= from) continue;
                    if (bump.Value == null || x.Value.Priority > bump.Value.Priority || x.Value.Priority == bump.Value.Priority && x.Value.Ut > bump.Value.Ut)
                    {
                        bump = x;
                        best = c;
                    }
                }
            return best;
        }

        // ---------------- Upload and the check after ----------------

        /// <summary>
        /// Uploads a probe's burn. A Traffic Controller covering the SOI it's in sends it up, if they know of the flight (stage B
        /// hand-offs), have a CommNet link to it (stage B) and a free slot for it (stage C; a less urgent burn may be bumped).
        /// Asked from the upload lead before the burn until it's uploaded, and once more when the burn is due
        /// (<paramref name="atBurn"/>), which also picks again if the probe has moved into another SOI or its controller has
        /// gone off duty. True when the burn may be made. Routes that need no controller always may.
        /// </summary>
        internal static bool Upload(Route route, RouteBurn burn, Vessel ship, double now, bool atBurn)
        {
            if (!NeedsController(route)) return true;
            var body = ship.orbitDriver.orbit.referenceBody.bodyName;
            if (burn.Uploaded && burn.ControllerBody == body && OnDutyController(burn.Controller) != null) return true;
            var number = route.Burns.IndexOf(burn) + 1;
            string why;
            if (!HandOff(route, ship, body, now, out why))
                return CantUpload(route, burn, body, number, why, now, atBurn);

            var kind = KindOf(burn.Title);
            burn.Kind = kind;
            burn.Critical = kind == Capture || IsCritical(ship, burn);
            burn.Priority = PriorityOf(kind, burn.Critical);
            if (Ships.MainEngine(ship) != null) burn.SlotSeconds = Math.Max(PhysicsFlyer.MinBurnS, PhysicsFlyer.BurnSeconds(ship, burn.DeltaV));

            var all = ControllersFor(body).ToList();
            var linked = all.Where(c => Linked(c, ship)).ToList();
            if (linked.Count == 0)
                return CantUpload(route, burn, body, number, all.Count == 0 ? "no Traffic Controller on duty for " + body + "'s SOI" : "no controller round " + body + " has a CommNet link to it", now, atBurn);
            var controller = SlotFor(linked, burn, now, out var bump);
            if (controller == null)
                return CantUpload(route, burn, body, number, body + "'s controllers are fully booked with burns as urgent or more", now, atBurn);
            if (bump.Value != null)
            {
                var bumped = bump.Value;
                bumped.Uploaded = false;
                bumped.Controller = "";
                TrafficScenario.Note(bump.Key, CallName(controller) + ": burn " + (bump.Key.Burns.IndexOf(bumped) + 1) + " (" + bumped.Title + ", priority " + bumped.Priority +
                                               ") gave its slot to " + route.VesselName + "'s " + kind + " (priority " + burn.Priority + "). It's uploaded again if a slot frees before it, or it's missed.", true);
            }
            var again = burn.Uploaded;
            burn.Uploaded = true;
            burn.Controller = controller.Kerbal;
            burn.ControllerBody = body;
            burn.ControllerPost = PostOf(controller);
            route.KnownTo = body;
            route.HandledBy = controller.Kerbal;
            route.HandledByPost = PostOf(controller);
            route.NoControllerFor = null;
            TrafficScenario.Note(route, CallName(controller) + ": burn " + number + " (" + burn.Title + ", " + burn.DeltaV.ToString("0") + " m/s) " +
                                        (again ? "uploaded again" : "uploaded") + ", " + kind + ", priority " + burn.Priority +
                                        (burn.Critical ? " (it can't be missed)" : "") + "; " + Load(controller.Kerbal, SlotStart(burn), SlotEnd(burn), now) + " of " +
                                        Capacity(controller.Kerbal) + " slots in use then.", true);
            return true;
        }

        /// <summary>The burn can't be uploaded now: said once per reason (before the burn); false.</summary>
        private static bool CantUpload(Route route, RouteBurn burn, string body, int number, string why, double now, bool atBurn)
        {
            if (!atBurn && route.NoControllerFor != why)
            {
                route.NoControllerFor = why;
                TrafficScenario.Note(route, "Burn " + number + " (" + burn.Title + ") can't be uploaded: " + why + ". Due in " +
                                            KSPUtil.PrintTimeCompact(Math.Max(0, burn.Ut - now), false) + ": it's missed unless that changes before then.", true);
            }
            burn.LastProblem = why;
            return false;
        }

        /// <summary>Why the last upload try failed, for the missed-burn line.</summary>
        internal static string UploadProblem(RouteBurn burn, string body) =>
            string.IsNullOrEmpty(burn.LastProblem) ? "no Traffic Controller on duty for " + body + "'s SOI uploaded it" : burn.LastProblem;

        /// <summary>
        /// The controller's check after a probe's burn (from <paramref name="fromUt"/>): the course checked from where the burn
        /// left it (planned again if it went wrong) and the burn put on the log. It needs a controller covering the SOI with a
        /// CommNet link to the probe (stage B): without one, the check waits until one has it (<see cref="TryPendingCheck"/>).
        /// </summary>
        internal static void AfterBurn(Route route, RouteBurn burn, Vessel ship, double seconds, double fromUt, double now)
        {
            var number = route.Burns.IndexOf(burn) + 1;
            var checker = Checker(burn, ship);
            if (checker == null)
            {
                route.PendingCheck = number;
                route.PendingCheckSeconds = seconds;
                TrafficScenario.Note(route, "Burn " + number + " made, but no controller has a CommNet link to check it: it's checked once one does.", true);
                return;
            }
            Check(route, burn, ship, checker, seconds, fromUt, now);
        }

        /// <summary>Who checks a burn: its controller if still on duty with a link, else another covering the SOI with one.</summary>
        private static DutyShift Checker(RouteBurn burn, Vessel ship)
        {
            var own = OnDutyController(burn.Controller);
            if (own != null && Linked(own, ship)) return own;
            return ControllersFor(ship.orbitDriver.orbit.referenceBody.bodyName).FirstOrDefault(c => Linked(c, ship));
        }

        private static void Check(Route route, RouteBurn burn, Vessel ship, DutyShift checker, double seconds, double fromUt, double now)
        {
            var number = route.Burns.IndexOf(burn) + 1;
            var onCourse = CourseCheck.Check(route, ship, fromUt, "After burn " + number);
            var kr = KerbinResourcesScenario.Instance;
            var credited = (kr?.Control.Burned(kr.Duty, burn.Controller, ship, burn.Kind, burn.Priority, seconds, now)) ??
                           kr?.Control.Burned(kr.Duty, checker.Kerbal, ship, burn.Kind, burn.Priority, seconds, now);
            TrafficScenario.Note(route, CallName(checker) + ": burn " + number + " checked, " + (onCourse ? "on track." : "something went wrong: planning it again."), !onCourse);
        }

        /// <summary>A burn's check that waited for a link: made once a controller has one (tried every couple of seconds).</summary>
        internal static void TryPendingCheck(Route route, double now)
        {
            var i = route.PendingCheck - 1;
            if (i < 0 || i >= route.Burns.Count) { route.PendingCheck = 0; return; }
            var ship = Ships.Find(route.VesselId);
            if (ship == null) return;
            var burn = route.Burns[i];
            var checker = Checker(burn, ship);
            if (checker == null) return;
            route.PendingCheck = 0;
            Check(route, burn, ship, checker, route.PendingCheckSeconds, now, now);
        }

        /// <summary>
        /// An off-course probe isn't planned again while nobody covers the SOI it's in: its new plan's next burn would be
        /// missed too. It waits for a Traffic Controller to go on duty there. True (with why) while it waits.
        /// </summary>
        internal static bool HoldsReplan(Route route, Vessel ship, out string why)
        {
            why = "";
            if (!NeedsController(route)) return false;
            var body = ship.orbitDriver.orbit.referenceBody.bodyName;
            if (ControllersFor(body).Any()) return false;
            why = "no Traffic Controller on duty for " + body + "'s SOI: put a pilot on duty " +
                  (body == HomeBody ? "at KSC or on a station round " + body : "on a station round " + body) + " to plan it again";
            return true;
        }

        // ---------------- Stage D: departure approval ----------------

        /// <summary>
        /// The burns of a new trip that won't be covered, one line each: no Traffic Controller on duty in the SOI it happens
        /// in, or that SOI's controllers fully booked then by burns as urgent or more (planned burns of other routes counted
        /// too, uploaded or not). Needs <see cref="PlanControl"/> first. Empty when every burn is covered. CommNet links
        /// can't be known ahead, so they aren't part of it.
        /// </summary>
        internal static List<string> Uncovered(Route route, double now)
        {
            var gaps = new List<string>();
            for (var i = 0; i < route.Burns.Count; i++)
            {
                var b = route.Burns[i];
                if (b.Done || string.IsNullOrEmpty(b.PlannedBody)) continue;
                var name = "Burn " + (i + 1) + " (" + b.Title + ", " + KSPUtil.PrintTimeCompact(Math.Max(0, b.Ut - now), false) + " from now) round " + b.PlannedBody;
                var cs = ControllersFor(b.PlannedBody).ToList();
                if (cs.Count == 0)
                {
                    gaps.Add(name + ": no Traffic Controller on duty there" + (b.Critical ? " (a burn it can't miss)" : "") + ".");
                    continue;
                }
                var supply = cs.Sum(c => Capacity(c.Kerbal));
                double from = SlotStart(b), to = SlotEnd(b);
                var demand = Routes.Where(r => r.VesselName != route.VesselName && r.State != RouteState.Stopped && !r.Finished)
                    .SelectMany(r => r.Burns).Count(x => !x.Done && x.Priority > 0 && x.Priority <= b.Priority &&
                                                         (x.Uploaded ? x.ControllerBody : x.PlannedBody) == b.PlannedBody && SlotStart(x) < to && SlotEnd(x) > from);
                if (demand >= supply) gaps.Add(name + ": its controllers are fully booked then (" + demand + " of " + supply + " slots by burns as urgent or more).");
            }
            return gaps;
        }

        // ---------------- The keep-out zone ----------------

        /// <summary>
        /// Does this route keep its zone controller busy now? A controller handles one craft inside their station's keep-out
        /// zone at a time: one cleared to dock, from its clearance until it has docked; one cleared to leave, while it backs
        /// off and climbs out, until it's past the zone's edge (or packed or unloaded: nobody near it to watch it out). It
        /// takes one of their slots too.
        /// </summary>
        internal static bool Occupies(Route r)
        {
            if (string.IsNullOrEmpty(r.ZoneController)) return false;
            switch (r.State)
            {
                case RouteState.Approaching:
                case RouteState.Arrived:
                    return r.ArrivalCleared && r.ZoneStationId == r.TargetId;
                case RouteState.Flying:
                    var ship = Ships.Find(r.VesselId);
                    var station = Ships.Find(r.ZoneStationId);
                    if (ship == null || station == null || !ship.loaded || ship.packed) return false;
                    return r.BackingOff || (ship.GetWorldPos3D() - station.GetWorldPos3D()).magnitude < PhysicsFlyer.ZoneRadius(station, ship) + ZoneMarginM;
                default:
                    return false;
            }
        }

        /// <summary>A craft leaving counts as out of the zone this far past its edge (m).</summary>
        private const double ZoneMarginM = 30;

        /// <summary>The craft a controller is handling in their station's zone now (route craft names; empty when free).</summary>
        internal static List<string> HandlingNow(DutyShift controller, Vessel station)
        {
            if (controller == null || station == null) return new List<string>();
            return Routes.Where(r => r.ZoneController == controller.Kerbal && r.ZoneStationId == station.persistentId && Occupies(r)).Select(r => r.VesselName).ToList();
        }

        /// <summary>
        /// A free controller aboard the station for a craft in the zone: none in the zone already, and a slot free for the
        /// next five minutes; least loaded first. Null with none, and why: who's busy with what.
        /// </summary>
        private static DutyShift FreeControllerAt(Vessel station, double now, out string busy)
        {
            busy = "";
            var all = ControllersAt(station).ToList();
            var free = all.Where(c => HandlingNow(c, station).Count == 0 && Load(c.Kerbal, now, now + ZoneLookS, now) < Capacity(c.Kerbal))
                .OrderBy(c => Load(c.Kerbal, now, now + ZoneLookS, now)).FirstOrDefault();
            if (free != null) return free;
            busy = string.Join("; ", all.Select(c =>
            {
                var zone = HandlingNow(c, station);
                return CallName(c) + (zone.Count > 0 ? " is busy with " + string.Join(", ", zone) : " has all " + Capacity(c.Kerbal) + " slots booked");
            }));
            return null;
        }

        /// <summary>
        /// Departure clearance, asked before a craft undocks from a station. A station with a Traffic Controller on duty
        /// clears it through a free one (returned; the caller notes them on the route and logs it once it has left). With
        /// every one busy, false: it stays docked and waits (a held departure). A station with none on duty is uncontrolled
        /// airspace: the craft leaves on its own (controller null, true). <paramref name="why"/> is the line for the log.
        /// </summary>
        internal static bool ClearDeparture(Vessel station, double now, out DutyShift controller, out string why)
        {
            controller = null;
            why = "";
            if (!Settings.ControllerRules || station == null) return true;
            if (!ControllersAt(station).Any())
            {
                why = station.vesselName + " has no Traffic Controller on duty: uncontrolled airspace, leaving on its own.";
                return true;
            }
            controller = FreeControllerAt(station, now, out var busy);
            if (controller == null)
            {
                why = busy;
                return false;
            }
            why = CallName(controller) + ": cleared to leave.";
            return true;
        }

        /// <summary>Puts a departure clearance on the controller's log, once the craft has left.</summary>
        internal static void LogClearance(DutyShift controller, string craft, uint craftId, double now)
        {
            var kr = KerbinResourcesScenario.Instance;
            if (controller != null) kr?.Control.Cleared(kr.Duty, controller.Kerbal, craft, craftId, now);
        }

        /// <summary>
        /// Docking clearance, asked by a craft held at the keep-out zone's edge (in physics) or about to dock on rails. A
        /// station with a Traffic Controller on duty clears it through a free one. A probe is then flown in by that
        /// controller; a crewed craft's pilot flies it in. With no controller on duty, the station is uncontrolled airspace:
        /// a crewed craft docks on its own (controller null, true), but a probe has nobody to fly it in and waits (false,
        /// with why). With every controller busy, it waits too. Nothing is logged here: see <see cref="LogArrival"/>.
        /// </summary>
        internal static bool ClearArrival(Route route, Vessel station, double now, out DutyShift controller, out string why)
        {
            controller = null;
            why = "";
            if (!Settings.ControllerRules || station == null) return true;
            var probe = NeedsController(route);
            if (ControllersAt(station).Any())
            {
                controller = FreeControllerAt(station, now, out var busy);
                if (controller == null)
                {
                    why = busy;
                    return false;
                }
                why = CallName(controller) + (probe ? ": cleared to dock, flying it in." : ": cleared to dock.");
                return true;
            }
            if (!probe)
            {
                why = station.vesselName + " has no Traffic Controller on duty: uncontrolled airspace, docking on its own.";
                return true;
            }
            why = "no Traffic Controller on duty aboard " + station.vesselName + " to fly it in";
            return false;
        }

        /// <summary>A docking clearance granted: said on the route's log and put on the controller's (none: uncontrolled airspace).</summary>
        internal static void LogArrival(Route route, DutyShift controller, string why, Vessel ship, double now)
        {
            route.ArrivalCleared = true;
            route.ZoneController = controller?.Kerbal;
            route.ZoneStationId = route.TargetId;
            if (why.Length > 0) TrafficScenario.Note(route, why, controller != null);
            var kr = KerbinResourcesScenario.Instance;
            if (controller != null) kr?.Control.Cleared(kr.Duty, controller.Kerbal, route.VesselName, ship?.persistentId ?? route.VesselId, now, "docking");
        }

        /// <summary>
        /// On rails, just before docking (arriving unwatched, or parked and nobody near): asks for docking clearance. True
        /// when it may dock (logged); false when it must wait, saying why once per reason.
        /// </summary>
        internal static bool MayDockOnRails(Route route, Vessel ship, Vessel station)
        {
            if (route.ArrivalCleared) return true;
            var now = Planetarium.GetUniversalTime();
            if (!ClearArrival(route, station, now, out var controller, out var why))
            {
                if (route.ArrivalHeldNote != why) TrafficScenario.Note(route, "Can't dock yet: " + why + ".", true);
                route.ArrivalHeldNote = why;
                return false;
            }
            route.ArrivalHeldNote = null;
            LogArrival(route, controller, why, ship, now);
            return true;
        }
    }
}
