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

            // The crew rules (none yet): checked before anything is undocked. A docked craft is asked about as its stack.
            if (!HaulerRules.CanFly(route, ship, out var notAllowed)) throw new ArgumentException(vessel + " can't fly this route: " + notAllowed);

            var now = Planetarium.GetUniversalTime();
            if (route.Burns[0].Ut < now - 5) throw new ArgumentException("The first burn's time has passed: plan again.");
            if (meetUt < route.Burns[route.Burns.Count - 1].Ut) throw new ArgumentException("The meeting can't come before the last burn.");

            // Flown as planned in KSP's own prediction, does it meet the target? A plan that already misses isn't started.
            var meeting = HaulerShip.PredictMeeting(ship, meet, route.Burns, now, meetUt);
            // A miss of a few km over days of flying (the planner's maths and KSP's drift apart across a change of sphere of
            // influence) is what the course corrections on the way are for: started, and corrected at the first check
            // (after backing off, after undocking on rails, or next frame). Only a plan that misses badly isn't started.
            if (meeting.Problem != null || meeting.GapM > StartMissM)
                throw new ArgumentException("Flown as planned, KSP's own prediction " + (meeting.Problem ?? "has it meet " + meet.vesselName + " " + meeting + " off") +
                                            ": not started.");
            var correctOnTheWay = meeting.GapM > CourseCheck.Tolerance(meetUt - now) || meeting.SpeedMs > CourseCheck.OffCourseMs;

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

            // What it carries, from its own command parts (a docked craft's, not its stack's): the ports it may use (§11.15).
            route.Role = dockedPort != null ? HaulerPorts.RoleOf(HaulerShip.DockedCraftParts(dockedPort))
                : railsStack != null ? RailsDocking.CraftRole(railsStack, vessel)
                : HaulerPorts.RoleOf(ship);

            // A short trip (meeting within the hour) books its port before it undocks, and stays docked without one: the
            // station it's going to will look much the same when it gets there (docs/MATH.md §11.14). A long trip books on
            // the way in.
            if (docked && meetUt - now <= PortBooking.BookLeadS)
            {
                var ports = dockedPort != null ? new List<string> { dockedPort.part.partInfo.name } : new List<string> { RailsDocking.CraftPortPart(railsStack, vessel) };
                if (ports[0] == null || !PortBooking.Try(route, ship, meet, ports))
                    throw new ArgumentException("No free port open to " + HaulerPorts.RolePlural(route.Role) + " at " + meet.vesselName + " for when it gets there (in " + KSPUtil.PrintTimeCompact(meetUt - now, false) +
                                                "): " + vessel + " stays docked. Send it again once a port there is free.");
            }

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
                var station = stationPort != null ? stationPort.part.vessel : stack;
                route.FromId = station.persistentId;
                route.FromName = station.vesselName;
            }

            // Docked in a stack nobody's near: undocked on rails, 60 m ahead of the station on its orbit.
            if (railsStack != null)
            {
                var stationName = railsStack.vesselName;
                route.FromId = railsStack.persistentId; // the station keeps its id; the craft split off gets a new one
                route.FromName = stationName;
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

            // Free-flying when it started: it left from the body it's round.
            if (!docked) route.FromName = ship.mainBody.bodyName;
            route.Pilots.AddRange(ship.GetVesselCrew().Where(c => c.trait == "Pilot").Select(c => c.name));

            // Usually started while the player is flying the ship: off rails already, so it's checked again once it's left.
            route.Disturbed = CourseCheck.OffRails(ship) || CourseCheck.OffRails(meet);

            // A ship planned again keeps what happened before on its log (found by name too: undocking gives a new id).
            var old = scenario.Routes.LastOrDefault(r => r.VesselId == ship.persistentId || r.VesselName == vessel);
            if (old != null) route.Log.AddRange(old.Log);
            // Planned again on the way (off course): still the same trip, from where it first left.
            if (old != null && old.State != RouteState.Docked && old.TargetId == route.TargetId && (old.FromId != 0 || old.FromName.Length > 0))
            {
                route.FromId = old.FromId;
                route.FromName = old.FromName;
            }
            // Planned again to the same station: it keeps the port it booked.
            if (old != null && old.State != RouteState.Docked && old.TargetId == route.TargetId && route.BookedPortId == 0) route.BookedPortId = old.BookedPortId;
            scenario.Routes.RemoveAll(r => r.VesselId == ship.persistentId || r.VesselName == vessel);
            if (dockedPort != null) route.Log.Add("Undocked from " + stack.vesselName + "." + ready);
            route.Log.Add((old != null && old.State == RouteState.OffCourse ? "Planned again: " : "Started: ") + route.Burns.Count + " burns, " +
                          route.Burns.Sum(b => b.DeltaV).ToString("0") + " m/s planned of " + have.ToString("0") + " m/s in the tanks, with " + engine + "; " + HaulerShip.Mass(ship).ToString("0.00") +
                          " t; KSP's prediction meets " + meet.vesselName + " within " + meeting + (route.Way.Length > 0 ? "; way: " + route.Way : ""));
            scenario.Routes.Add(route);
            // Undocked on rails 60 m ahead of where the plan started: checked now (a course correction, or a plan from here).
            if (railsStack != null) CourseCheck.Check(route, ship, now, "After undocking on rails", expected: true);
            // Free-flying and already off by more than the course check allows: corrected now (docked ones are checked
            // once they've backed off).
            else if (correctOnTheWay && !docked) route.CheckSoon = ship.mainBody.bodyName;
            if (correctOnTheWay) route.Log.Add("KSP's prediction has it meet " + meet.vesselName + " " + meeting + " off: a course correction puts it right on the way.");
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

        /// <summary>
        /// The pilots' log, one line per pilot and trip, tab-separated: kerbal, from (station or body), to, times flown,
        /// game time last flown.
        /// </summary>
        [KRPCProcedure]
        public static IList<string> PilotFlights()
        {
            var list = new List<string>();
            var scenario = HaulerScenario.Instance;
            if (scenario == null) return list;
            foreach (var kerbal in scenario.PilotLog.Kerbals)
                foreach (var f in scenario.PilotLog.Flights(kerbal))
                    list.Add(string.Join(Tab, new[] { kerbal, f.FromName, f.ToName, f.TimesFlown.ToString(CultureInfo.InvariantCulture), R(f.LastFlownUt) }));
            return list;
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

        /// <summary>A plan whose meeting KSP predicts further off than this (m) isn't started; under it, it's corrected on the way.</summary>
        private const double StartMissM = 50000;

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
            return scenario == null ? 0 : scenario.Routes.RemoveAll(r => r.State == RouteState.Docked || r.State == RouteState.Stopped);
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
