using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KRPC.Service;
using KRPC.Service.Attributes;

namespace KspFlightPlanner.Haulers
{
    /// <summary>
    /// kRPC service "PlannerHaulers": hand a ship a planned trip and let the game fly it in the background
    /// (<see cref="HaulerScenario"/> does the flying). The planner plans; this only follows the plan.
    /// </summary>
    [KRPCService(Name = "PlannerHaulers", GameScene = GameScene.All)]
    public static class HaulerService
    {
        /// <summary>
        /// Starts <paramref name="vessel"/> on a trip to meet <paramref name="target"/>. <paramref name="burns"/> holds
        /// four numbers per burn, in time order: game time, then prograde, normal and radial Δv in m/s (KSP's node axes);
        /// <paramref name="titles"/> names each burn for the log and screen. At <paramref name="meetUt"/> the ship is parked
        /// beside the target. Ships are named exactly as in the game. Replaces any route the ship already had.
        /// </summary>
        [KRPCProcedure]
        public static string StartRoute(string vessel, string target, IList<double> burns, IList<string> titles, double meetUt, string way)
        {
            var scenario = RequireScenario();
            // A ship on its own, or one docked in another vessel (a shuttle at its station), found by its own name; that
            // one is undocked once the plan has been checked, and backs off before its first burn.
            Vessel ship;
            Vessel stack = null;
            Vessel railsStack = null; // docked in a stack that isn't loaded: undocked on rails (RailsDocking)
            ModuleDockingNode dockedPort = null;
            try
            {
                ship = HaulerShip.FindByName(vessel);
            }
            catch (ArgumentException) when ((dockedPort = HaulerShip.FindDockedCraftPort(vessel, out stack)) != null
                                            || (railsStack = RailsDocking.FindDockedIn(vessel)) != null)
            {
                ship = stack ?? railsStack;
            }
            var meet = HaulerShip.FindByName(target);
            if (ship == meet && dockedPort == null) throw new ArgumentException("A ship can't fly to meet itself.");
            if (stack == meet) throw new ArgumentException(vessel + " is docked at " + target + " already.");
            if (!HaulerShip.InSpace(ship)) throw new ArgumentException(vessel + " isn't in space (" + ship.situation + ").");
            if (burns.Count == 0 || burns.Count % 4 != 0) throw new ArgumentException("Burns come as four numbers each: time, prograde, normal, radial.");
            var docked = dockedPort != null || railsStack != null;
            var engine = docked ? null : HaulerShip.MainEngine(ship);
            if (!docked && engine == null) throw new ArgumentException(vessel + " has no engine with fuel on board.");

            var route = new HaulerRoute
            {
                VesselId = ship.persistentId,
                VesselName = ship.vesselName,
                TargetId = meet.persistentId,
                TargetName = meet.vesselName,
                MeetUt = meetUt,
                Way = way ?? "",
            };
            for (var i = 0; i < burns.Count; i += 4)
                route.Burns.Add(new HaulerBurn
                {
                    Ut = burns[i],
                    Prograde = burns[i + 1],
                    Normal = burns[i + 2],
                    Radial = burns[i + 3],
                    Title = titles != null && i / 4 < titles.Count ? titles[i / 4] : "",
                });
            route.Burns.Sort((a, b) => a.Ut.CompareTo(b.Ut));

            var now = Planetarium.GetUniversalTime();
            if (route.Burns[0].Ut < now - 5) throw new ArgumentException("The first burn's time has passed: plan again.");
            if (meetUt < route.Burns[route.Burns.Count - 1].Ut) throw new ArgumentException("The meeting can't come before the last burn.");

            // Flown as planned in KSP's own prediction, does it meet the target? A plan that already misses isn't started.
            var meeting = HaulerShip.PredictMeeting(ship, meet, route.Burns, now, meetUt);
            if (meeting.Problem != null || meeting.GapM > CourseCheck.Tolerance(meetUt - now) || meeting.SpeedMs > CourseCheck.OffCourseMs)
                throw new ArgumentException("Flown as planned, KSP's own prediction " + (meeting.Problem ?? "has it meet " + meet.vesselName + " " + meeting + " off") +
                                            ": not started.");

            // Enough fuel for the whole trip, with a margin for real burns' losses and the braking in? A ship that can't get
            // there isn't sent (nor, when a replan is the one asking, replanned onto a trip it can't finish).
            var need = route.Burns.Sum(b => b.DeltaV);
            var have = dockedPort != null ? HaulerShip.DeltaVLeft(HaulerShip.DockedCraftParts(dockedPort), out _)
                : railsStack != null ? double.PositiveInfinity // known once it's undocked, below
                : HaulerShip.DeltaVLeft(ship, out _);
            var needWithMargin = need * (1 + FuelMarginShare) + FuelMarginMs;
            if (have < needWithMargin)
                throw new ArgumentException(vessel + " has " + have.ToString("0") + " m/s of fuel; this trip needs " + need.ToString("0") + " m/s (" +
                                            needWithMargin.ToString("0") + " with the margin for real burns): refuel it or plan a cheaper trip.");

            // Docked: undock it now (it's loaded, the player is flying the stack); the plan was checked on the stack's orbit,
            // which is the ship's until it moves off.
            var ready = "";
            if (dockedPort != null)
            {
                var stationPort = dockedPort.otherNode;
                dockedPort.Undock();
                ship = dockedPort.part.vessel;
                ready = HaulerShip.ReadyToFly(ship);
                engine = HaulerShip.MainEngine(ship);
                if (engine == null) throw new ArgumentException(vessel + " is undocked, but has no engine with fuel on board: not started.");
                route.VesselId = ship.persistentId;
                route.VesselName = vessel;
                route.ShipPortPartId = dockedPort.part.persistentId;
                route.BackOffPortId = stationPort != null ? stationPort.part.persistentId : 0;
                route.BackingOff = route.BackOffPortId != 0;
            }

            // Docked in a stack nobody's near: undocked on rails, 60 m ahead of the station on its orbit.
            if (railsStack != null)
            {
                var stationName = railsStack.vesselName;
                ship = RailsDocking.Undock(railsStack, vessel, out var why);
                if (ship == null) throw new ArgumentException("Couldn't undock " + vessel + " on rails: " + why);
                meet = HaulerShip.Find(meet.persistentId) ?? meet;
                engine = HaulerShip.MainEngine(ship);
                have = HaulerShip.DeltaVLeft(ship, out _);
                if (engine == null) throw new ArgumentException(vessel + " is undocked, but has no engine with fuel on board: not started.");
                if (have < needWithMargin)
                    throw new ArgumentException(vessel + " is undocked, but has " + have.ToString("0") + " m/s of fuel; this trip needs " + needWithMargin.ToString("0") +
                                                " with the margin: not started.");
                route.VesselId = ship.persistentId;
                route.VesselName = vessel;
                route.Log.Add("Undocked from " + stationName + " on rails; engines lit, RCS on.");
            }

            // Usually started while the player is flying the ship: off rails already, so it's checked again once it's left.
            route.Disturbed = CourseCheck.OffRails(ship) || CourseCheck.OffRails(meet);

            // A ship planned again keeps what happened before on its log (found by name too: undocking gives a new id).
            var old = scenario.Routes.LastOrDefault(r => r.VesselId == ship.persistentId || r.VesselName == vessel);
            if (old != null) route.Log.AddRange(old.Log);
            scenario.Routes.RemoveAll(r => r.VesselId == ship.persistentId || r.VesselName == vessel);
            if (dockedPort != null) route.Log.Add("Undocked from " + stack.vesselName + "." + ready);
            route.Log.Add((old != null && old.State == RouteState.OffCourse ? "Planned again: " : "Started: ") + route.Burns.Count + " burns, " +
                          route.Burns.Sum(b => b.DeltaV).ToString("0") + " m/s planned of " + have.ToString("0") + " m/s in the tanks, with " + engine + "; " + HaulerShip.Mass(ship).ToString("0.00") +
                          " t; KSP's prediction meets " + meet.vesselName + " within " + meeting + (route.Way.Length > 0 ? "; way: " + route.Way : ""));
            scenario.Routes.Add(route);
            // Undocked on rails 60 m ahead of where the plan started: checked now (a course correction, or a plan from here).
            if (railsStack != null) CourseCheck.Check(route, ship, now, "After undocking on rails", expected: true);
            ScreenMessages.PostScreenMessage(ship.vesselName + (old != null && old.State == RouteState.OffCourse ? " was planned again and is" : " is") +
                                             " flying to " + meet.vesselName + " on its own", 6f, ScreenMessageStyle.UPPER_CENTER);
            return route.Summary();
        }

        /// <summary>One line per route: who's going where and how far along.</summary>
        [KRPCProcedure]
        public static IList<string> Routes()
        {
            var scenario = HaulerScenario.Instance;
            return scenario == null ? new List<string>() : scenario.Routes.Select(r => r.Summary()).ToList();
        }

        /// <summary>What happened on a ship's route so far, one line per event.</summary>
        [KRPCProcedure]
        public static IList<string> RouteLog(string vessel)
        {
            var route = FindRoute(vessel);
            return route == null ? new List<string>() : new List<string>(route.Log);
        }

        /// <summary>Stops a ship's route where it is (the ship stays on its current orbit). False if it had none.</summary>
        [KRPCProcedure]
        public static bool CancelRoute(string vessel)
        {
            var scenario = HaulerScenario.Instance;
            var route = FindRoute(vessel);
            if (scenario == null || route == null) return false;
            scenario.Routes.Remove(route);
            return true;
        }

        /// <summary>
        /// The off-course routes waiting to be planned again, one line each, tab-separated: ship, target, game time now,
        /// then for the ship and then the target: body, semi-major axis (m), eccentricity, inclination, LAN and argument of
        /// periapsis (degrees), mean anomaly at epoch (radians), epoch (s). KSP's own orbit elements, as the save has them.
        /// </summary>
        [KRPCProcedure]
        public static IList<string> ReplanRequests()
        {
            var list = new List<string>();
            var scenario = HaulerScenario.Instance;
            if (scenario == null) return list;
            var now = Planetarium.GetUniversalTime();
            foreach (var r in scenario.Routes.Where(r => r.State == RouteState.OffCourse))
            {
                var ship = HaulerShip.Find(r.VesselId);
                var target = HaulerShip.Find(r.TargetId);
                if (ship == null || target == null) continue;
                // Then the way it was planned ("Meets soonest"…) and the Δv in its tanks, so the planner keeps the way if the fuel allows.
                list.Add(string.Join(Tab, new[] { ship.vesselName, target.vesselName, R(now), Elements(ship.orbitDriver.orbit), Elements(target.orbitDriver.orbit),
                    r.Way, R(HaulerShip.DeltaVLeft(ship, out _)) }));
            }
            return list;
        }

        /// <summary>A line from the planner about a ship's route (why it was planned the way it was), shown in the game and logged.</summary>
        [KRPCProcedure]
        public static void RouteNote(string vessel, string text)
        {
            var route = FindRoute(vessel);
            if (route != null) HaulerScenario.Note(route, text, true);
        }

        /// <summary>The planner couldn't plan an off-course route again (yet): shown in the game, once per reason.</summary>
        [KRPCProcedure]
        public static void ReplanProblem(string vessel, string why)
        {
            var route = FindRoute(vessel);
            if (route != null && route.State == RouteState.OffCourse) CourseCheck.ReplanProblem(route, why);
        }

        private const string Tab = "\t";

        /// <summary>The fuel margin a trip must have on top of its planned Δv: this share of it, plus this many m/s.</summary>
        private const double FuelMarginShare = 0.05, FuelMarginMs = 30;

        private static string R(double x) => x.ToString("R", CultureInfo.InvariantCulture);

        private static string Elements(Orbit o) =>
            string.Join(Tab, new[] { o.referenceBody.bodyName, R(o.semiMajorAxis), R(o.eccentricity), R(o.inclination), R(o.LAN), R(o.argumentOfPeriapsis),
                R(o.meanAnomalyAtEpoch), R(o.epoch) });

        /// <summary>Forgets finished and stopped routes.</summary>
        [KRPCProcedure]
        public static int ClearFinished()
        {
            var scenario = HaulerScenario.Instance;
            return scenario == null ? 0 : scenario.Routes.RemoveAll(r => r.State != RouteState.Flying);
        }

        private static HaulerRoute FindRoute(string vessel)
        {
            var scenario = HaulerScenario.Instance;
            return scenario?.Routes.LastOrDefault(r => r.VesselName == vessel);
        }

        private static HaulerScenario RequireScenario()
        {
            var scenario = HaulerScenario.Instance;
            if (scenario == null) throw new InvalidOperationException("Load a save first (in flight, the tracking station or the space centre).");
            return scenario;
        }
    }
}
