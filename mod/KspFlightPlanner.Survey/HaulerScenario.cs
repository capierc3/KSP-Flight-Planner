using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Haulers
{
    /// <summary>One planned burn of a route, in KSP's maneuver-node axes, at a game time.</summary>
    internal sealed class HaulerBurn
    {
        public double Ut;
        public double Prograde, Normal, Radial;
        public string Title = "";
        public bool Done;

        public double DeltaV => Math.Sqrt(Prograde * Prograde + Normal * Normal + Radial * Radial);
    }

    /// <summary>Flying the burns; Arrived = parked beside the target, docking as soon as both are loaded; Docked; Stopped.</summary>
    internal enum RouteState { Flying, Arrived, Docked, Stopped }

    /// <summary>
    /// A ship flying a planned trip without anyone at the controls: at each burn's time its orbit is changed as that burn
    /// would, and its fuel burnt; at the meeting time it's parked beside the target.
    /// </summary>
    internal sealed class HaulerRoute
    {
        public uint VesselId;
        public string VesselName = "";
        public uint TargetId;
        public string TargetName = "";
        public double MeetUt;
        public RouteState State = RouteState.Flying;
        public List<HaulerBurn> Burns = new List<HaulerBurn>();
        public List<string> Log = new List<string>();

        // Docking, in this scene only (not saved): when both ships were first seen loaded and unpacked, which port is
        // docking and since when (real time), and whether this scene already tried.
        internal float BothLoadedSince = -1;
        internal uint DockingPortId;
        internal float DockingSince = -1;
        internal bool DockTried;

        /// <summary>The next thing due: a burn's time, or the meeting once the burns are done.</summary>
        public double NextUt
        {
            get
            {
                var burn = Burns.FirstOrDefault(b => !b.Done);
                return burn != null ? burn.Ut : MeetUt;
            }
        }

        public string Summary()
        {
            var done = Burns.Count(b => b.Done);
            var head = VesselName + " → " + TargetName + ": ";
            switch (State)
            {
                case RouteState.Arrived: return head + "parked beside it, docks when you're nearby";
                case RouteState.Docked: return head + "docked";
                case RouteState.Stopped: return head + "stopped (" + (Log.Count > 0 ? Log[Log.Count - 1] : "") + ")";
                default: return head + done + " of " + Burns.Count + " burns done, next at UT " + NextUt.ToString("0", CultureInfo.InvariantCulture);
            }
        }

        public void Save(ConfigNode node)
        {
            node.AddValue("vesselId", VesselId);
            node.AddValue("vesselName", VesselName);
            node.AddValue("targetId", TargetId);
            node.AddValue("targetName", TargetName);
            node.AddValue("meetUt", MeetUt.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("state", State.ToString());
            foreach (var b in Burns)
            {
                var n = node.AddNode("BURN");
                n.AddValue("ut", b.Ut.ToString("R", CultureInfo.InvariantCulture));
                n.AddValue("prograde", b.Prograde.ToString("R", CultureInfo.InvariantCulture));
                n.AddValue("normal", b.Normal.ToString("R", CultureInfo.InvariantCulture));
                n.AddValue("radial", b.Radial.ToString("R", CultureInfo.InvariantCulture));
                n.AddValue("title", b.Title);
                n.AddValue("done", b.Done);
            }
            var log = node.AddNode("LOG");
            foreach (var line in Log) log.AddValue("line", line);
        }

        public static HaulerRoute Load(ConfigNode node)
        {
            double D(ConfigNode n, string key) => double.Parse(n.GetValue(key), CultureInfo.InvariantCulture);
            var route = new HaulerRoute
            {
                VesselId = uint.Parse(node.GetValue("vesselId"), CultureInfo.InvariantCulture),
                VesselName = node.GetValue("vesselName") ?? "",
                TargetId = uint.Parse(node.GetValue("targetId"), CultureInfo.InvariantCulture),
                TargetName = node.GetValue("targetName") ?? "",
                MeetUt = D(node, "meetUt"),
                State = (RouteState)Enum.Parse(typeof(RouteState), node.GetValue("state")),
            };
            foreach (var n in node.GetNodes("BURN"))
                route.Burns.Add(new HaulerBurn
                {
                    Ut = D(n, "ut"),
                    Prograde = D(n, "prograde"),
                    Normal = D(n, "normal"),
                    Radial = D(n, "radial"),
                    Title = n.GetValue("title") ?? "",
                    Done = n.GetValue("done") == "True",
                });
            var log = node.GetNode("LOG");
            if (log != null) route.Log.AddRange(log.GetValues("line"));
            return route;
        }
    }

    /// <summary>
    /// Runs the haulers' routes inside the game, every frame, in flight, the tracking station and the space centre. The
    /// routes are kept in the save (this is a scenario module), so they carry on after a restart and rewind with a
    /// quickload or revert.
    ///
    /// Every event is applied at its own planned time, even when time warp has already gone past it: the new orbit is
    /// pinned to the burn's time, so the ship ends up exactly where it would be had it burnt on time. Overdue events are
    /// applied in order, so nothing is missed after a long warp or a scene change.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.TRACKSTATION, GameScenes.SPACECENTER)]
    public sealed class HaulerScenario : ScenarioModule
    {
        internal static HaulerScenario Instance { get; private set; }

        internal readonly List<HaulerRoute> Routes = new List<HaulerRoute>();

        public override void OnAwake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public override void OnSave(ConfigNode node)
        {
            foreach (var r in Routes) r.Save(node.AddNode("ROUTE"));
        }

        public override void OnLoad(ConfigNode node)
        {
            Routes.Clear();
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
                // A few events in one frame at most; whatever is left goes next frame.
                for (var i = 0; i < 4 && route.State == RouteState.Flying && route.NextUt <= now; i++)
                    Step(route);
                if (route.State == RouteState.Arrived && HighLogic.LoadedSceneIsFlight) Dock(route);
            }
        }

        /// <summary>The furthest behind the target a ship is parked: inside KSP's 200 m unpack range in orbit.</summary>
        private const double MaxParkM = 180;

        /// <summary>How long both ships must sit loaded and unpacked before docking (real seconds): let physics settle.</summary>
        private const float SettleS = 2f;

        /// <summary>How long the magnets get to pull the ship in (real seconds) before giving up.</summary>
        private const float DockWaitS = 10f;

        /// <summary>
        /// Docking needs both ships loaded with physics running, so it happens when the player is near (flying either ship
        /// or something beside them, not in time warp). Then the ship is placed with its port just in front of a free port
        /// on the target, and the ports' own magnets finish the job. Tried once per scene; if it fails, it's left parked
        /// to dock by hand, and tried again the next time the scene loads.
        /// </summary>
        private static void Dock(HaulerRoute route)
        {
            var ship = HaulerShip.Find(route.VesselId);
            var station = HaulerShip.Find(route.TargetId);

            if (route.DockingSince >= 0)
            {
                // Docked: the ship has become part of the station (its own vessel is gone, its port is the station's).
                if (HaulerShip.PortDocked(route.DockingPortId))
                {
                    route.State = RouteState.Docked;
                    Note(route, "Docked at " + route.TargetName + ".", true);
                    return;
                }
                if (Time.time - route.DockingSince > DockWaitS)
                {
                    route.DockingSince = -1;
                    Note(route, "Didn't dock: the magnets didn't catch it. It's beside the port: dock by hand.", true);
                }
                return;
            }

            if (route.DockTried || ship == null || station == null) return;
            if (!ship.loaded || ship.packed || !station.loaded || station.packed || TimeWarp.CurrentRate > 1f)
            {
                route.BothLoadedSince = -1;
                return;
            }
            if (route.BothLoadedSince < 0) route.BothLoadedSince = Time.time;
            if (Time.time - route.BothLoadedSince < SettleS) return;

            route.DockTried = true;
            if (!HaulerShip.PickPorts(ship, station, out var mine, out var theirs))
            {
                Note(route, "Couldn't dock: " + station.vesselName + " has no free port that fits " + ship.vesselName + "'s.", true);
                return;
            }
            try
            {
                HaulerShip.PlaceForDocking(ship, mine, station, theirs);
                route.DockingPortId = mine.part.persistentId;
                route.DockingSince = Time.time;
                Note(route, "Docking: " + mine.part.partInfo.title + " to " + station.vesselName + "'s " + theirs.part.partInfo.title + ".", false);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Note(route, "Couldn't dock: " + e.Message, true);
            }
        }

        private static void Note(HaulerRoute route, string line, bool onScreen)
        {
            route.Log.Add(line);
            Debug.Log("[KspFlightPlanner] " + route.VesselName + ": " + line);
            if (onScreen) ScreenMessages.PostScreenMessage(route.VesselName + ": " + line, 8f, ScreenMessageStyle.UPPER_CENTER);
        }

        private static void Step(HaulerRoute route)
        {
            var ship = HaulerShip.Find(route.VesselId);
            if (ship == null)
            {
                Stop(route, route.VesselName + " is gone (recovered, destroyed or merged by docking).");
                return;
            }
            route.VesselName = ship.vesselName;
            try
            {
                var burn = route.Burns.FirstOrDefault(b => !b.Done);
                if (burn != null) DoBurn(route, ship, burn);
                else Arrive(route, ship);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Stop(route, e.Message);
            }
        }

        private static void DoBurn(HaulerRoute route, Vessel ship, HaulerBurn burn)
        {
            burn.Done = true;
            var number = route.Burns.IndexOf(burn) + 1;
            var engine = HaulerShip.MainEngine(ship);
            if (engine == null)
            {
                Stop(route, "Burn " + number + ": " + ship.vesselName + " has no engine with fuel left.");
                return;
            }

            var orbit = ship.orbitDriver.orbit;
            var body = orbit.referenceBody;
            var pos = orbit.getRelativePositionAtUT(burn.Ut);
            var vel = orbit.getOrbitalVelocityAtUT(burn.Ut);
            HaulerShip.Axes(pos, vel, out var prograde, out var normal, out var radial);

            var planned = burn.DeltaV;
            var got = HaulerShip.Burn(ship, engine, planned, out var used);
            var scale = planned > 0 ? got / planned : 0;
            var dv = (prograde * burn.Prograde + normal * burn.Normal + radial * burn.Radial) * scale;
            HaulerShip.SetOrbit(ship, pos, vel + dv, body, burn.Ut);

            var late = Planetarium.GetUniversalTime() - burn.Ut;
            var line = "Burn " + number + " (" + burn.Title + ") at " + body.bodyName + ": " + got.ToString("0") + " of " + planned.ToString("0") +
                       " m/s with " + engine + ", used " + used + "; " + HaulerShip.Mass(ship).ToString("0.00") + " t left" +
                       (late > 1 ? "; applied " + late.ToString("0") + " s late (time warp), placed where it would be" : "");
            route.Log.Add(line);
            Debug.Log("[KspFlightPlanner] " + route.VesselName + ": " + line);
            ScreenMessages.PostScreenMessage(route.VesselName + ": " + (burn.Title.Length > 0 ? burn.Title : "burn " + number) + ", " + got.ToString("0") + " m/s",
                6f, ScreenMessageStyle.UPPER_CENTER);
            if (got < planned - 0.5) Stop(route, "Out of fuel at burn " + number + ": got " + got.ToString("0") + " of " + planned.ToString("0") + " m/s.");
        }

        /// <summary>
        /// The meeting: how near the ship really came (the test's measure of how well KSP followed the plan), then parked
        /// a safe distance behind the target on its orbit, paying fuel for whatever speed was still left to match.
        /// </summary>
        private static void Arrive(HaulerRoute route, Vessel ship)
        {
            var target = HaulerShip.Find(route.TargetId);
            if (target == null)
            {
                Stop(route, route.TargetName + " is gone.");
                return;
            }
            route.TargetName = target.vesselName;
            var ut = route.MeetUt;
            var to = target.orbitDriver.orbit;
            var orbit = ship.orbitDriver.orbit;
            var body = to.referenceBody;
            var tPos = to.getRelativePositionAtUT(ut);
            var tVel = to.getOrbitalVelocityAtUT(ut);

            string miss;
            double closing = 0;
            if (orbit.referenceBody == body)
            {
                var gap = (orbit.getRelativePositionAtUT(ut) - tPos).magnitude;
                closing = (orbit.getOrbitalVelocityAtUT(ut) - tVel).magnitude;
                miss = "came within " + gap.ToString("0") + " m of it at " + closing.ToString("0.0") + " m/s";
            }
            else
            {
                miss = "was round " + orbit.referenceBody.bodyName + ", not " + body.bodyName + " (the plan drifted)";
            }

            var used = "";
            var engine = HaulerShip.MainEngine(ship);
            if (engine != null && closing > 0) HaulerShip.Burn(ship, engine, closing, out used);

            // On the target's own orbit, a few seconds behind it, so the gap holds until the player comes to dock. Under
            // 200 m, where KSP turns physics on for a ship near the one being flown, so docking can start; 100 m at least.
            var behind = Math.Min(MaxParkM, Math.Max(100, 2 * (HaulerShip.Size(target) + HaulerShip.Size(ship)) + 50));
            HaulerShip.FollowOrbit(ship, to, behind / tVel.magnitude);

            route.State = RouteState.Arrived;
            var line = "Arrived: " + miss + "; parked " + behind.ToString("0") + " m behind " + route.TargetName +
                       (used.Length > 0 ? "; final approach used " + used : "") + "; " + HaulerShip.Mass(ship).ToString("0.00") + " t left";
            route.Log.Add(line);
            Debug.Log("[KspFlightPlanner] " + route.VesselName + ": " + line);
            ScreenMessages.PostScreenMessage(route.VesselName + " has arrived at " + route.TargetName + ": it docks when you're nearby", 8f, ScreenMessageStyle.UPPER_CENTER);
        }

        private static void Stop(HaulerRoute route, string why)
        {
            route.State = RouteState.Stopped;
            route.Log.Add("Stopped: " + why);
            Debug.LogWarning("[KspFlightPlanner] " + route.VesselName + ": stopped: " + why);
            ScreenMessages.PostScreenMessage(route.VesselName + " stopped its route: " + why, 10f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
