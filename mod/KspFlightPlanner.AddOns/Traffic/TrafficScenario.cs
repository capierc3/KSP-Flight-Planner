using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Traffic
{
    /// <summary>
    /// Runs the routes inside the game, every frame, in flight, the tracking station and the space centre. The
    /// routes are kept in the save (this is a scenario module), so they carry on after a restart and rewind with a
    /// quickload or revert.
    ///
    /// It decides who flies each route: <see cref="RailsFlyer"/> while the ship is on rails, <see cref="PhysicsFlyer"/>
    /// while it's loaded with physics running near the player (so far it only points the ship at its next burn; the rails
    /// flyer still makes the burns), and <see cref="CourseCheck"/> in between, after the ship or its target has been off
    /// rails.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.TRACKSTATION, GameScenes.SPACECENTER)]
    public sealed class TrafficScenario : ScenarioModule
    {
        internal static TrafficScenario Instance { get; private set; }

        internal readonly List<Route> Routes = new List<Route>();

        /// <summary>A route has docked at its target: done, and the trip goes on its crew's flight log.</summary>
        internal static void Docked(Route route)
        {
            route.State = RouteState.Docked;
            Credit(route);
        }

        /// <summary>A trip to an orbit has made its last burn: done, and the trip goes on its crew's flight log.</summary>
        internal static void InOrbit(Route route)
        {
            route.State = RouteState.InOrbit;
            Credit(route);
        }

        /// <summary>
        /// Puts a finished route on Kerbin Resources' flight log, once, with the kerbals aboard when it started (docs/MATH.md
        /// §11.12). Any trip Kerbin Resources had open for the craft (it saw it undock) is dropped: this is that trip.
        /// </summary>
        private static void Credit(Route route)
        {
            var log = KerbinResources.KerbinResourcesScenario.Instance?.Log;
            if (route.Credited || log == null) return;
            route.Credited = true;
            log.DropOpen(route.VesselId);
            if (route.Crew.Count == 0) return;
            var now = Planetarium.GetUniversalTime();
            var flight = new KerbinResources.Flight
            {
                DepartedUt = route.StartedUt > 0 ? route.StartedUt : now,
                ArrivedUt = now,
                FromId = route.FromId,
                FromName = route.FromName,
                ToId = route.TargetId,
                ToName = route.TargetName,
                Vessel = route.VesselName,
                Autopilot = true,
                Crew = route.Crew.Select(k => new KerbinResources.FlightCrew { Name = k, Trait = KerbinResources.FlightLog.TraitOf(k) }).ToList(),
            };
            log.Record(flight);
            KerbinResources.KerbinResourcesScenario.Announce(flight);
            Note(route, "On the flight log: " + string.Join(", ", route.Crew) + ", " + route.FromName + " → " + route.TargetName + ".", false);
        }

        /// <summary>A route is flying this craft (Kerbin Resources leaves its undockings and dockings to <see cref="Credit"/>).</summary>
        internal static bool FliesRoute(Vessel v) =>
            Instance != null && v != null && Instance.Routes.Any(r => r.VesselId == v.persistentId &&
                (r.State == RouteState.Flying || r.State == RouteState.Arrived || r.State == RouteState.Approaching || r.State == RouteState.Waiting));

        /// <summary>
        /// Station ports other route craft have claimed (part persistent ids): the one each has booked (§11.14), the one each is
        /// coming in to or docking at, and the one each has just left while it backs off. A route craft picks a port no other
        /// has (docs/MATH.md §11.11).
        /// </summary>
        internal static HashSet<uint> ClaimedPorts(Route except)
        {
            var claimed = new HashSet<uint>();
            if (Instance == null) return claimed;
            foreach (var r in Instance.Routes)
            {
                if (r == except) continue;
                if ((r.State == RouteState.Approaching || r.State == RouteState.Arrived) && r.ApproachPortId != 0) claimed.Add(r.ApproachPortId);
                if (r.BackingOff && r.BackOffPortId != 0) claimed.Add(r.BackOffPortId);
                if (r.BookedPortId != 0 && !r.Finished && r.State != RouteState.Stopped) claimed.Add(r.BookedPortId);
            }
            return claimed;
        }

        /// <summary>
        /// The station's ports this route's craft may not pick: those other route craft have claimed, and those not open to
        /// its role (docs/MATH.md §11.15).
        /// </summary>
        internal static HashSet<uint> PortsNotFor(Route route, Vessel station)
        {
            var shut = ClaimedPorts(route);
            shut.UnionWith(PortAccess.NotOpenTo(station, route.Role));
            return shut;
        }

        public override void OnAwake()
        {
            Instance = this;
            KerbinResources.KerbinResourcesScenario.LoggedElsewhere = FliesRoute;
            KerbinResources.KerbinResourcesScenario.CardRows = CrewRules.CardRows;
            KerbinResources.DutyRoster.JobOf = CrewRules.JobOf;
            KerbinResources.KerbinResourcesScenario.KerbalRenamed += OnKerbalRenamed;
            GameEvents.onVesselGoOffRails.Add(OnVesselGoOffRails);
            GameEvents.onVesselSOIChanged.Add(OnVesselSoiChanged);
            GameEvents.onPartCouple.Add(OnCouple);
            GameEvents.onVesselsUndocking.Add(OnUndocking);
        }

        // Docked craft made quiet (DockedQuiet): the craft-side part of a docking seen this frame, quietened once KSP has
        // joined the two; and an undocking, after which quiet craft that are free again are woken (both a frame later).
        private uint _quietPart;
        private int _quietFrame = -1;
        private int _wakeFrame = -1;

        /// <summary>
        /// Any docking, the player's or the autopilot's: the lighter of the two craft is the one docking, and its side's
        /// port part is noted, to be made quiet once KSP has joined them.
        /// </summary>
        private void OnCouple(GameEvents.FromToAction<Part, Part> couple)
        {
            var a = couple.from?.vessel;
            var b = couple.to?.vessel;
            if (a == null || b == null || a == b) return;
            var craftPart = a.GetTotalMass() <= b.GetTotalMass() ? couple.from : couple.to;
            if (craftPart.FindModuleImplementing<ModuleDockingNode>() == null) return; // a claw, not a port
            _quietPart = craftPart.persistentId;
            _quietFrame = Time.frameCount + 2;
        }

        /// <summary>Any undocking, the player's or the autopilot's: quiet craft that are free again are woken shortly.</summary>
        private void OnUndocking(Vessel oldVessel, Vessel newVessel)
        {
            _wakeFrame = Time.frameCount + 2;
            if (oldVessel == null || newVessel == null || oldVessel == newVessel) return;
            _undocked = (oldVessel.GetTotalMass() <= newVessel.GetTotalMass() ? oldVessel : newVessel).persistentId;
        }

        // The lighter half of the last undocking (the craft that left), for craft docked before quiet records were kept.
        private uint _undocked;

        /// <summary>The docking and undocking follow-ups, a couple of frames after KSP's event.</summary>
        private void QuietAndWake()
        {
            if (_quietFrame >= 0 && Time.frameCount >= _quietFrame)
            {
                _quietFrame = -1;
                var vessel = Ships.VesselOfPart(_quietPart);
                var port = vessel?.parts.FirstOrDefault(p => p.persistentId == _quietPart)?.FindModulesImplementing<ModuleDockingNode>().FirstOrDefault(n => n.otherNode != null);
                var did = DockedQuiet.Quiet(port);
                if (did.Length > 0) ScreenMessages.PostScreenMessage((port.vesselInfo?.name ?? "The docked craft") + " docked:" + did, 5f, ScreenMessageStyle.UPPER_CENTER);
            }
            if (_wakeFrame >= 0 && Time.frameCount >= _wakeFrame)
            {
                _wakeFrame = -1;
                var woken = DockedQuiet.WakeUndocked();
                // A route craft docked before quiet records were kept (its engines and RCS shut down then, nothing noted):
                // readied to fly whole, as the autopilot does when it undocks one.
                var left = Ships.Find(_undocked);
                _undocked = 0;
                if (left != null && left.loaded && !woken.Contains(left.persistentId) &&
                    Routes.Any(r => r.State == RouteState.Docked && r.VesselName == left.vesselName))
                {
                    var ready = Ships.ReadyToFly(left);
                    if (ready.Length > 0) ScreenMessages.PostScreenMessage(left.vesselName + " undocked:" + ready, 5f, ScreenMessageStyle.UPPER_CENTER);
                }
            }
        }

        /// <summary>
        /// A flying route craft moved into another body's sphere of influence: its plan is checked straight away (next frame),
        /// while a miss is cheapest to put right, and from a point where nothing's left to predict across.
        /// </summary>
        private void OnVesselSoiChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> change)
        {
            if (change.host == null) return;
            foreach (var route in Routes)
                if (route.State == RouteState.Flying && route.VesselId == change.host.persistentId)
                {
                    route.CheckSoon = change.to != null ? change.to.bodyName : "";
                    route.HandOffDue = true; // to the new SOI's controllers (stage B)
                }
        }

        /// <summary>A kerbal was renamed: routes' crew lists follow.</summary>
        private void OnKerbalRenamed(string from, string to)
        {
            foreach (var r in Routes)
                for (var i = 0; i < r.Crew.Count; i++)
                    if (r.Crew[i] == from) r.Crew[i] = to;
        }

        private void OnDestroy()
        {
            KerbinResources.KerbinResourcesScenario.KerbalRenamed -= OnKerbalRenamed;
            PhysicsFlyer.ReleaseAll();
            GameEvents.onVesselGoOffRails.Remove(OnVesselGoOffRails);
            GameEvents.onVesselSOIChanged.Remove(OnVesselSoiChanged);
            GameEvents.onPartCouple.Remove(OnCouple);
            GameEvents.onVesselsUndocking.Remove(OnUndocking);
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// A ship's physics came on (the player is flying it or near it): if it's a flying route craft or its target, something
        /// may move it now, so its plan is checked again once it's back on rails. Nothing is checked until then.
        /// </summary>
        private void OnVesselGoOffRails(Vessel v)
        {
            if (v == null) return;
            foreach (var route in Routes)
            {
                if (route.State != RouteState.Flying || route.Disturbed || (v.persistentId != route.VesselId && (route.TargetId == 0 || v.persistentId != route.TargetId))) continue;
                route.Disturbed = true;
                Note(route, v.vesselName + " is off rails: the plan is checked again once it's back on them.", false);
            }
        }

        public override void OnSave(ConfigNode node)
        {
            foreach (var r in Routes) r.Save(node.AddNode("ROUTE"));
            DockedQuiet.Save(node);
        }

        public override void OnLoad(ConfigNode node)
        {
            DockedQuiet.Load(node);
            Routes.Clear();
            foreach (var n in node.GetNodes("ROUTE"))
            {
                try { Routes.Add(Route.Load(n)); }
                catch (Exception e) { Debug.LogError("[KspFlightPlanner] Couldn't read a route: " + e.Message); }
            }
        }

        private void Update()
        {
            // Give the scene a moment to set up its ships' orbits before moving any.
            if (Time.timeSinceLevelLoad < 1f || FlightGlobals.fetch == null || FlightGlobals.Vessels == null) return;
            if (HighLogic.LoadedSceneIsFlight) QuietAndWake();
            var now = Planetarium.GetUniversalTime();
            // A copy: a held departure that leaves replaces its own route in the list.
            foreach (var route in Routes.ToList())
            {
                // Docked and holding for a controller to clear it to leave: undocks once one is free.
                if (route.State == RouteState.Held)
                {
                    WatchHeld(route, now);
                    continue;
                }
                // Just undocked: KSP may have given the ship a new id; find it again by its port.
                if (route.ShipPortPartId != 0 && Ships.Find(route.VesselId) == null && HighLogic.LoadedSceneIsFlight
                    && Ships.VesselOfPart(route.ShipPortPartId) is Vessel again)
                    route.VesselId = again.persistentId;
                // Just into another sphere of influence: still on course?
                if (route.CheckSoon != null)
                {
                    var into = route.CheckSoon;
                    route.CheckSoon = null;
                    if (route.State == RouteState.Flying && Ships.Find(route.VesselId) is Vessel moved)
                        CourseCheck.Check(route, moved, now, "Now round " + into);
                }
                // Back on rails (or unloaded) after being disturbed: still on course?
                if (route.State == RouteState.Flying && route.Disturbed) CourseCheck.CheckIfResting(route, now);
                // An hour from the meeting: a port booked at the station (or, with none free, tried again every few seconds).
                if (route.State == RouteState.Flying && PortBooking.Due(route, now))
                    PortBooking.Try(route, Ships.Find(route.VesselId), Ships.Find(route.TargetId));
                // Due at a station the player is at: lined up early, while it's still out of sight.
                if (route.State == RouteState.Flying) RailsFlyer.WatchArrival(route, now);
                // No free port when it got there: waiting behind the station for one.
                if (route.State == RouteState.Waiting) PortBooking.WatchWaiting(route, now);
                // From the upload lead before a probe's next burn: a controller uploads it (every 2 s until one does).
                if (route.State == RouteState.Flying) TryUpload(route, now);
                // Into a new SOI: handed to its controllers; a burn made out of contact: checked once there's a link.
                if (route.State == RouteState.Flying) WatchControl(route, now);
                // A few events in one frame at most; whatever is left goes next frame.
                for (var i = 0; i < 4 && route.State == RouteState.Flying && route.NextUt <= now; i++)
                    RailsFlyer.Step(route, now);
                if (HighLogic.LoadedSceneIsFlight) PhysicsFlyer.Tick(route);
                if (route.State == RouteState.Approaching) RailsFlyer.WatchApproach(route);
                // The jump-to-the-port stand-in docks it when the physics flyer can't (no RCS, or it gave up); with the
                // autopilot switched off, the player docks it.
                if (route.State == RouteState.Arrived && HighLogic.LoadedSceneIsFlight && route.Autopilot && !PhysicsFlyer.Flies(route)) RailsFlyer.Dock(route);
                // Parked with nobody near it or the station (any scene): docked on rails.
                if (route.State == RouteState.Arrived) RailsFlyer.DockWhileAway(route);
                if (route.State == RouteState.OffCourse) CourseCheck.WatchOffCourse(route);
            }
        }

        /// <summary>
        /// A held departure (docked, no free controller at its station when it was sent): every 2 s it asks again, and once a
        /// controller is free, or none is on duty any more (uncontrolled airspace), it's started as sent and undocks.
        /// </summary>
        private static void WatchHeld(Route route, double now)
        {
            // Waiting for the player's answer to departure approval (stage D): asked on screen, once per scene.
            if (route.AwaitingApproval)
            {
                if (!route.ApprovalShown) AskApproval(route);
                return;
            }
            if (Time.realtimeSinceStartup < route.NextHeldTry) return;
            route.NextHeldTry = Time.realtimeSinceStartup + 2f;
            // Docked at a station: cleared to leave first. Free-flying (held only for approval): off it goes.
            if (route.FromId != 0)
            {
                var station = Ships.Find(route.FromId);
                if (station == null)
                {
                    Stop(route, route.FromName + " is gone.");
                    return;
                }
                if (!CrewRules.ClearDeparture(station, now, out _, out var why))
                {
                    route.ReplanNote = why;
                    return;
                }
            }
            try { TrafficService.Resume(route); }
            catch (Exception e)
            {
                Debug.LogException(e);
                Stop(route, "couldn't leave " + route.FromName + " after holding: " + e.Message);
            }
        }

        /// <summary>
        /// Departure approval (docs/TRAFFIC-CONTROLLER.md "Controllers", stage D): some of a probe trip's burns won't have a
        /// Traffic Controller. The player is asked whether to fly it uncontrolled (those burns will be missed and the trip
        /// planned again), or keep it where it is.
        /// </summary>
        private static void AskApproval(Route route)
        {
            route.ApprovalShown = true;
            var docked = route.FromId != 0;
            var msg = route.VesselName + " to " + route.TargetName + " is a probe flight, and these burns won't have a Traffic Controller:\n\n" +
                      route.ApprovalNote + "\n\nA burn without a controller is missed: the probe coasts past it, and the trip is planned again once a controller covers where it is. Fly it uncontrolled?";
            PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new MultiOptionDialog("KspFlightPlannerApproval", msg, "Departure approval: " + route.VesselName, HighLogic.UISkin,
                    new DialogGUIButton("Fly it uncontrolled", () =>
                    {
                        route.AwaitingApproval = false;
                        route.ApprovalNote = "";
                        route.NextHeldTry = 0;
                        Note(route, "You approved it to fly with burns uncontrolled.", false);
                    }),
                    new DialogGUIButton(docked ? "Stay docked" : "Don't start", () =>
                        Stop(route, docked ? "stays docked: you chose not to fly it uncontrolled." : "not started: you chose not to fly it uncontrolled."))),
                false, HighLogic.UISkin);
        }

        /// <summary>
        /// A flying probe's controllers, every couple of seconds: just into a new SOI, its flight is handed to that SOI's
        /// controllers (said once if it can't be yet; an upload tries again); a burn made with no controller linked to check
        /// it is checked once one is (stage B).
        /// </summary>
        private static void WatchControl(Route route, double now)
        {
            if ((!route.HandOffDue && route.PendingCheck == 0) || Time.realtimeSinceStartup < route.NextHandOffTry || !CrewRules.NeedsController(route)) return;
            route.NextHandOffTry = Time.realtimeSinceStartup + 2f;
            var ship = Ships.Find(route.VesselId);
            if (ship == null) return;
            if (route.HandOffDue)
            {
                var body = ship.orbitDriver.orbit.referenceBody.bodyName;
                if (CrewRules.HandOff(route, ship, body, now, out var why)) route.HandOffDue = false;
                else if (route.NoControllerFor != why)
                {
                    route.NoControllerFor = why;
                    Note(route, "Not handed over to " + body + "'s controllers yet: " + why + ".", true);
                }
            }
            if (route.PendingCheck > 0) CrewRules.TryPendingCheck(route, now);
        }

        /// <summary>
        /// A probe's next burn, inside the upload lead and not uploaded yet: a controller covering its SOI uploads it
        /// (docs/TRAFFIC-CONTROLLER.md "Controllers"). Tried every 2 s (real time) till one does or the burn comes.
        /// </summary>
        private static void TryUpload(Route route, double now)
        {
            if (Time.realtimeSinceStartup < route.NextUploadTry || !CrewRules.NeedsController(route)) return;
            route.NextUploadTry = Time.realtimeSinceStartup + 2f;
            var burn = route.Burns.FirstOrDefault(b => !b.Done);
            if (burn == null || burn.Uploaded || now < burn.Ut - TrafficSettings.Current.UploadLeadMinutes * 60.0 || now >= burn.Ut) return;
            var ship = Ships.Find(route.VesselId);
            if (ship != null) CrewRules.Upload(route, burn, ship, now, atBurn: false);
        }

        /// <summary>
        /// A burn nobody could upload: not made, now or later. The craft coasts on, time warp stops, and the trip goes off
        /// course to be planned again (held until a controller covers its SOI: <see cref="CrewRules.HoldsReplan"/>).
        /// </summary>
        internal static void MissBurn(Route route, RouteBurn burn, Vessel ship, string why)
        {
            burn.Done = true;
            burn.Missed = true;
            burn.Kind = CrewRules.KindOf(burn.Title);
            var number = route.Burns.IndexOf(burn) + 1;
            Note(route, "Missed burn " + number + " (" + burn.Title + ", " + burn.DeltaV.ToString("0") + " m/s): " + why + ". It coasts on.", true);
            CourseCheck.Missed(route, ship, "missed burn " + number + " (" + why + ")");
        }

        internal static void Note(Route route, string line, bool onScreen)
        {
            route.Log.Add(line);
            Debug.Log("[KspFlightPlanner] " + route.VesselName + ": " + line);
            if (onScreen) ScreenMessages.PostScreenMessage(route.VesselName + ": " + line, 8f, ScreenMessageStyle.UPPER_CENTER);
        }

        internal static void Stop(Route route, string why)
        {
            route.State = RouteState.Stopped;
            route.Log.Add("Stopped: " + why);
            Debug.LogWarning("[KspFlightPlanner] " + route.VesselName + ": stopped: " + why);
            ScreenMessages.PostScreenMessage(route.VesselName + " stopped its route: " + why, 10f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
