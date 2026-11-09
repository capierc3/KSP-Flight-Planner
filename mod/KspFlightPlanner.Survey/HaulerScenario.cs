using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Haulers
{
    /// <summary>
    /// Runs the haulers' routes inside the game, every frame, in flight, the tracking station and the space centre. The
    /// routes are kept in the save (this is a scenario module), so they carry on after a restart and rewind with a
    /// quickload or revert.
    ///
    /// It decides who flies each route: <see cref="RailsFlyer"/> while the ship is on rails, <see cref="PhysicsFlyer"/>
    /// while it's loaded with physics running near the player (so far it only points the ship at its next burn; the rails
    /// flyer still makes the burns), and <see cref="CourseCheck"/> in between, after the ship or its target has been off
    /// rails.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.TRACKSTATION, GameScenes.SPACECENTER)]
    public sealed class HaulerScenario : ScenarioModule
    {
        internal static HaulerScenario Instance { get; private set; }

        internal readonly List<HaulerRoute> Routes = new List<HaulerRoute>();

        /// <summary>Which trips each pilot has flown (docs/MATH.md §11.12).</summary>
        internal readonly PilotLog PilotLog = new PilotLog();

        /// <summary>
        /// The player's marks on docked vessels, by name: true for a station part (its fuel is the station's), false for
        /// a craft of its own. Vessels without a mark follow the rule (docs/MATH.md §11.9).
        /// </summary>
        internal readonly Dictionary<string, bool> StationMarks = new Dictionary<string, bool>();

        /// <summary>A route has docked at its target: done, and its pilots get the trip on their log.</summary>
        internal static void Docked(HaulerRoute route)
        {
            route.State = RouteState.Docked;
            Instance?.PilotLog.Credit(route, Planetarium.GetUniversalTime());
        }

        /// <summary>A trip to an orbit has made its last burn: done, and its pilots get the trip on their log.</summary>
        internal static void InOrbit(HaulerRoute route)
        {
            route.State = RouteState.InOrbit;
            Instance?.PilotLog.Credit(route, Planetarium.GetUniversalTime());
        }

        /// <summary>
        /// Station ports other haulers have claimed (part persistent ids): the one each has booked (§11.14), the one each is
        /// coming in to or docking at, and the one each has just left while it backs off. A hauler picks a port no other
        /// has (docs/MATH.md §11.11).
        /// </summary>
        internal static HashSet<uint> ClaimedPorts(HaulerRoute except)
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
        /// The station's ports this route's hauler may not pick: those other haulers have claimed, and those not open to
        /// its role (docs/MATH.md §11.15).
        /// </summary>
        internal static HashSet<uint> PortsNotFor(HaulerRoute route, Vessel station)
        {
            var shut = ClaimedPorts(route);
            shut.UnionWith(HaulerPorts.NotOpenTo(station, route.Role));
            return shut;
        }

        public override void OnAwake()
        {
            Instance = this;
            GameEvents.onVesselGoOffRails.Add(OnVesselGoOffRails);
            GameEvents.onVesselSOIChanged.Add(OnVesselSoiChanged);
        }

        /// <summary>
        /// A flying hauler moved into another body's sphere of influence: its plan is checked straight away (next frame),
        /// while a miss is cheapest to put right, and from a point where nothing's left to predict across.
        /// </summary>
        private void OnVesselSoiChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> change)
        {
            if (change.host == null) return;
            foreach (var route in Routes)
                if (route.State == RouteState.Flying && route.VesselId == change.host.persistentId)
                    route.CheckSoon = change.to != null ? change.to.bodyName : "";
        }

        private void OnDestroy()
        {
            PhysicsFlyer.ReleaseAll();
            GameEvents.onVesselGoOffRails.Remove(OnVesselGoOffRails);
            GameEvents.onVesselSOIChanged.Remove(OnVesselSoiChanged);
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// A ship's physics came on (the player is flying it or near it): if it's a flying hauler or its target, something
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
            PilotLog.Save(node.AddNode("PILOTLOG"));
            foreach (var mark in StationMarks)
            {
                var n = node.AddNode("STATIONMARK");
                n.AddValue("vessel", mark.Key);
                n.AddValue("station", mark.Value);
            }
        }

        public override void OnLoad(ConfigNode node)
        {
            Routes.Clear();
            PilotLog.Load(node.GetNode("PILOTLOG"));
            StationMarks.Clear();
            foreach (var n in node.GetNodes("STATIONMARK"))
                if (n.GetValue("vessel") is string name && bool.TryParse(n.GetValue("station"), out var station)) StationMarks[name] = station;
            foreach (var n in node.GetNodes("ROUTE"))
            {
                try { Routes.Add(HaulerRoute.Load(n)); }
                catch (Exception e) { Debug.LogError("[KspFlightPlanner] Couldn't read a hauler route: " + e.Message); }
            }
        }

        private void Update()
        {
            // Give the scene a moment to set up its ships' orbits before moving any.
            if (Time.timeSinceLevelLoad < 1f || FlightGlobals.fetch == null || FlightGlobals.Vessels == null) return;
            var now = Planetarium.GetUniversalTime();
            foreach (var route in Routes)
            {
                // Just undocked: KSP may have given the ship a new id; find it again by its port.
                if (route.ShipPortPartId != 0 && HaulerShip.Find(route.VesselId) == null && HighLogic.LoadedSceneIsFlight
                    && HaulerShip.VesselOfPart(route.ShipPortPartId) is Vessel again)
                    route.VesselId = again.persistentId;
                // Just into another sphere of influence: still on course?
                if (route.CheckSoon != null)
                {
                    var into = route.CheckSoon;
                    route.CheckSoon = null;
                    if (route.State == RouteState.Flying && HaulerShip.Find(route.VesselId) is Vessel moved)
                        CourseCheck.Check(route, moved, now, "Now round " + into);
                }
                // Back on rails (or unloaded) after being disturbed: still on course?
                if (route.State == RouteState.Flying && route.Disturbed) CourseCheck.CheckIfResting(route, now);
                // An hour from the meeting: a port booked at the station (or, with none free, tried again every few seconds).
                if (route.State == RouteState.Flying && PortBooking.Due(route, now))
                    PortBooking.Try(route, HaulerShip.Find(route.VesselId), HaulerShip.Find(route.TargetId));
                // Due at a station the player is at: lined up early, while it's still out of sight.
                if (route.State == RouteState.Flying) RailsFlyer.WatchArrival(route, now);
                // No free port when it got there: waiting behind the station for one.
                if (route.State == RouteState.Waiting) PortBooking.WatchWaiting(route, now);
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

        internal static void Note(HaulerRoute route, string line, bool onScreen)
        {
            route.Log.Add(line);
            Debug.Log("[KspFlightPlanner] " + route.VesselName + ": " + line);
            if (onScreen) ScreenMessages.PostScreenMessage(route.VesselName + ": " + line, 8f, ScreenMessageStyle.UPPER_CENTER);
        }

        internal static void Stop(HaulerRoute route, string why)
        {
            route.State = RouteState.Stopped;
            route.Log.Add("Stopped: " + why);
            Debug.LogWarning("[KspFlightPlanner] " + route.VesselName + ": stopped: " + why);
            ScreenMessages.PostScreenMessage(route.VesselName + " stopped its route: " + why, 10f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
