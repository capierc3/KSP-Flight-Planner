using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KRPC.Service;
using KRPC.Service.Attributes;

namespace KspFlightPlanner.Traffic
{
    /// <summary>
    /// kRPC service "PlannerTraffic": hand a ship a planned trip and let the game fly it in the background
    /// (<see cref="TrafficScenario"/> does the flying). The planner plans; this only follows the plan.
    /// </summary>
    [KRPCService(Name = "PlannerTraffic", GameScene = GameScene.All)]
    public static class TrafficService
    {
        /// <summary>
        /// Starts <paramref name="vessel"/> on a trip to meet <paramref name="target"/>. <paramref name="burns"/> holds
        /// four numbers per burn, in time order: game time, then prograde, normal and radial Δv in m/s (KSP's node axes);
        /// <paramref name="titles"/> names each burn for the log and screen. At <paramref name="meetUt"/> the ship is parked
        /// beside the target. Ships are named exactly as in the game. Replaces any route the ship already had.
        /// </summary>
        [KRPCProcedure]
        public static string StartRoute(string vessel, string target, IList<double> burns, IList<string> titles, double meetUt, string way) =>
            Start(vessel, target, burns, titles, meetUt, way, null, null);

        /// <summary>
        /// Starts <paramref name="vessel"/> on a trip that ends in an orbit round <paramref name="body"/> rather than at a
        /// craft: no meeting, no port, no docking; once the last burn (the capture) is made, it's there. <paramref name="burns"/>
        /// and <paramref name="titles"/> as for <see cref="StartRoute"/>. <paramref name="orbit"/> is the planned orbit after the
        /// last burn as seven numbers, KSP's own elements: semi-major axis (m), eccentricity, inclination, LAN and argument of
        /// periapsis (degrees), mean anomaly at epoch (radians), epoch (s). docs/MATH.md §11.16.
        /// </summary>
        [KRPCProcedure]
        public static string StartOrbitRoute(string vessel, string body, IList<double> burns, IList<string> titles, IList<double> orbit, string way) =>
            Start(vessel, null, burns, titles, double.NaN, way, body, orbit);

        /// <summary>
        /// The two kinds of trip: to meet <paramref name="target"/> at <paramref name="meetUt"/>, or (target null) to the
        /// orbit <paramref name="goal"/> round <paramref name="goalBody"/>, reached at the last burn.
        /// </summary>
        /// <param name="resumed">A held departure leaving now it's cleared (<see cref="Resume"/>): its plan may have gone
        /// stale while it waited, and it leaves anyway, to be planned again once it's clear.</param>
        private static string Start(string vessel, string target, IList<double> burns, IList<string> titles, double meetUt, string way, string goalBody, IList<double> goal,
            bool resumed = false)
        {
            var scenario = RequireScenario();
            var toOrbit = target == null;
            // A ship on its own, or one docked in another vessel (a shuttle at its station), found by its own name; that
            // one is undocked once the plan has been checked, and backs off before its first burn.
            Vessel ship;
            Vessel stack = null;
            Vessel railsStack = null; // docked in a stack that isn't loaded: undocked on rails (RailsDocking)
            ModuleDockingNode dockedPort = null;
            try
            {
                ship = Ships.FindByName(vessel);
            }
            catch (ArgumentException) when ((dockedPort = Ships.FindDockedCraftPort(vessel, out stack)) != null
                                            || (railsStack = RailsDocking.FindDockedIn(vessel)) != null)
            {
                ship = stack ?? railsStack;
            }
            var meet = toOrbit ? null : Ships.FindByName(target);
            if (!toOrbit && ship == meet && dockedPort == null) throw new ArgumentException("A ship can't fly to meet itself.");
            if (!toOrbit && stack == meet) throw new ArgumentException(vessel + " is docked at " + target + " already.");
            if (!Ships.InSpace(ship)) throw new ArgumentException(vessel + " isn't in space (" + ship.situation + ").");
            if (burns.Count == 0 || burns.Count % 4 != 0) throw new ArgumentException("Burns come as four numbers each: time, prograde, normal, radial.");
            var docked = dockedPort != null || railsStack != null;
            var engine = docked ? null : Ships.MainEngine(ship);
            if (!docked && engine == null) throw new ArgumentException(vessel + " has no engine with fuel on board.");

            var route = new Route
            {
                VesselId = ship.persistentId,
                VesselName = ship.vesselName,
                TargetId = toOrbit ? 0 : meet.persistentId,
                TargetName = toOrbit ? goalBody + " orbit" : meet.vesselName,
                MeetUt = meetUt,
                Way = way ?? "",
            };
            for (var i = 0; i < burns.Count; i += 4)
                route.Burns.Add(new RouteBurn
                {
                    Ut = burns[i],
                    Prograde = burns[i + 1],
                    Normal = burns[i + 2],
                    Radial = burns[i + 3],
                    Title = titles != null && i / 4 < titles.Count ? titles[i / 4] : "",
                });
            route.Burns.Sort((a, b) => a.Ut.CompareTo(b.Ut));

            // A trip to an orbit: the planned orbit stands in for the target, reached at the last burn (§11.16).
            Orbit to;
            if (toOrbit)
            {
                if (goal == null || goal.Count != 7)
                    throw new ArgumentException("The orbit comes as seven numbers: semi-major axis, eccentricity, inclination, LAN, argument of periapsis, mean anomaly at epoch, epoch.");
                route.GoalBody = goalBody ?? "";
                route.GoalSma = goal[0];
                route.GoalEcc = goal[1];
                route.GoalInc = goal[2];
                route.GoalLan = goal[3];
                route.GoalArgPe = goal[4];
                route.GoalMeanAnomaly = goal[5];
                route.GoalEpoch = goal[6];
                route.MeetUt = meetUt = route.Burns[route.Burns.Count - 1].Ut;
                if (!(route.GoalEcc < 1) || !(route.GoalSma > 0))
                    throw new ArgumentException("The planned orbit round " + goalBody + " isn't closed (it escapes): background trips end in a closed orbit.");
                to = route.GoalOrbit();
                if (to == null) throw new ArgumentException("There's no body called " + goalBody + " in this game.");
                // On rails nothing slows a ship in the air (KSP removes it instead), so the trip can't end in it or below the ground.
                var body = to.referenceBody;
                var lowest = body.atmosphere ? body.atmosphereDepth : 0;
                if (to.PeA < lowest)
                    throw new ArgumentException("The planned orbit's periapsis (" + (to.PeA / 1000).ToString("0.0") + " km) is " +
                                                (body.atmosphere ? "in " + body.bodyName + "'s air" : "below the ground") +
                                                ": a background trip can't aerobrake or land. Plan a higher orbit.");
            }
            else to = meet.orbitDriver.orbit;

            // The crew rules, checked before anything is undocked: the trip it would make (from its station, or the body it's
            // round) and the craft's own crew (a docked craft's, not its station's). Planned again on the way (off course),
            // it's the same trip, cleared when it started.
            var earlier = scenario.Routes.LastOrDefault(r => r.VesselName == vessel || (!docked && r.VesselId == ship.persistentId));
            if (earlier != null && !earlier.Finished && earlier.State != RouteState.Stopped && earlier.TargetId == route.TargetId &&
                earlier.GoalBody == route.GoalBody && earlier.StartedUt > 0)
                route.StartedUt = earlier.StartedUt;
            route.FromId = docked ? (stack ?? railsStack).persistentId : 0;
            route.FromName = docked ? (stack ?? railsStack).vesselName : ship.mainBody.bodyName;
            var craftCrew = dockedPort != null ? Ships.DockedCraftParts(dockedPort).SelectMany(p => p.protoModuleCrew).Select(c => c.name).ToList()
                : railsStack != null ? RailsDocking.CraftCrew(railsStack, vessel)
                : ship.GetVesselCrew().Select(c => c.name).ToList();
            if (!CrewRules.CanFly(route, craftCrew, out var notAllowed))
            {
                // Said in game too, so it's seen from the flight scene, not only in the planner.
                UnityEngine.Debug.Log("[KspFlightPlanner] " + vessel + ": route not started: " + notAllowed);
                ScreenMessages.PostScreenMessage(vessel + " can't fly this route: " + notAllowed, 10f, ScreenMessageStyle.UPPER_CENTER);
                throw new ArgumentException(vessel + " can't fly this route: " + notAllowed);
            }

            var now = Planetarium.GetUniversalTime();
            // Held at its station past its first burn (or too close to it to back off first): the plan is stale. It leaves
            // anyway; the burns due in the next two minutes are dropped, and once it's clear of the station the course check
            // has the planner plan the trip again from where it is.
            var stale = false;
            if (resumed && route.Burns[0].Ut < now + StaleLeadS)
            {
                foreach (var b in route.Burns)
                    if (b.Ut < now + StaleLeadS) b.Done = true;
                stale = true;
            }
            else if (route.Burns[0].Ut < now - 5) throw new ArgumentException("The first burn's time has passed: plan again.");
            if (meetUt < route.Burns[route.Burns.Count - 1].Ut) throw new ArgumentException("The meeting can't come before the last burn.");

            // Flown as planned in KSP's own prediction, does it meet the target (or reach its orbit)? A plan that already
            // misses isn't started.
            var meeting = Ships.PredictMeeting(ship, to, route.Burns.Where(b => !b.Done).ToList(), now, meetUt);
            // A miss of a few km over days of flying (the planner's maths and KSP's drift apart across a change of sphere of
            // influence) is what the course corrections on the way are for: started, and corrected at the first check
            // (after backing off, after undocking on rails, or next frame). Only a plan that misses badly isn't started.
            if (!stale && (meeting.Problem != null || meeting.GapM > StartMissM))
                throw new ArgumentException("Flown as planned, KSP's own prediction " + (meeting.Problem ?? "has it " + (toOrbit ? "reach the planned " + goalBody + " orbit " : "meet " + meet.vesselName + " ") + meeting + " off") +
                                            ": not started.");
            var correctOnTheWay = stale || meeting.GapM > CourseCheck.Tolerance(meetUt - now) || meeting.SpeedMs > CourseCheck.OffCourseMs;

            // Enough fuel for the whole trip, with a margin for real burns' losses and the braking in? A ship that can't get
            // there isn't sent (nor, when a replan is the one asking, replanned onto a trip it can't finish).
            var need = route.Burns.Sum(b => b.DeltaV);
            var have = dockedPort != null ? Ships.DeltaVLeft(Ships.DockedCraftParts(dockedPort), out _)
                : railsStack != null ? double.PositiveInfinity // known once it's undocked, below
                : Ships.DeltaVLeft(ship, out _);
            var needWithMargin = need * (1 + FuelMarginShare) + FuelMarginMs;
            if (have < needWithMargin)
                throw new ArgumentException(vessel + " has " + have.ToString("0") + " m/s of fuel; this trip needs " + need.ToString("0") + " m/s (" +
                                            needWithMargin.ToString("0") + " with the margin for real burns): refuel it or plan a cheaper trip.");

            // What it carries, from its own command parts (a docked craft's, not its stack's): the ports it may use (§11.15).
            route.Role = dockedPort != null ? PortAccess.RoleOf(Ships.DockedCraftParts(dockedPort))
                : railsStack != null ? RailsDocking.CraftRole(railsStack, vessel)
                : PortAccess.RoleOf(ship);

            // A short trip (meeting within the hour) books its port before it undocks, and stays docked without one: the
            // station it's going to will look much the same when it gets there (docs/MATH.md §11.14). A long trip books on
            // the way in.
            if (!toOrbit && docked && meetUt - now <= PortBooking.BookLeadS)
            {
                var ports = dockedPort != null ? new List<string> { dockedPort.part.partInfo.name } : new List<string> { RailsDocking.CraftPortPart(railsStack, vessel) };
                if (ports[0] == null || !PortBooking.Try(route, ship, meet, ports))
                    throw new ArgumentException("No free port open to " + PortAccess.RolePlural(route.Role) + " at " + meet.vesselName + " for when it gets there (in " + KSPUtil.PrintTimeCompact(meetUt - now, false) +
                                                "): " + vessel + " stays docked. Send it again once a port there is free.");
            }

            // Departure approval (stage D): a probe trip whose burns won't all have a controller (none on duty in that SOI,
            // or its controllers fully booked then) waits for the player to say whether to fly it uncontrolled. A trip
            // planned again on the way isn't asked again.
            if (!resumed && route.StartedUt <= 0 && TrafficSettings.Current.DepartureApproval && CrewRules.NeedsController(craftCrew))
            {
                CrewRules.PlanControl(route, ship, now, isCraft: !docked);
                var gaps = CrewRules.Uncovered(route, now);
                if (gaps.Count > 0)
                    return Hold(scenario, route, vessel, docked ? stack ?? railsStack : null,
                        "waiting for your approval: " + gaps.Count + " burn" + (gaps.Count == 1 ? "" : "s") + " without a controller", now, string.Join("\n", gaps.ToArray()));
            }

            // Departure clearance (docs/TRAFFIC-CONTROLLER.md "Controllers"): a station with a Traffic Controller on duty
            // clears the craft through one of them; without one it's uncontrolled airspace, and it leaves on its own.
            KerbinResources.DutyShift clearedBy = null;
            var clearance = "";
            // With every controller there busy with another craft in the keep-out zone, it stays docked and waits its turn.
            if (docked && !CrewRules.ClearDeparture(stack ?? railsStack, now, out clearedBy, out clearance))
            {
                if (resumed) throw new ArgumentException("no controller free after all (" + clearance + ")");
                return Hold(scenario, route, vessel, stack ?? railsStack, clearance, now);
            }

            // Docked: undock it now (it's loaded, the player is flying the stack); the plan was checked on the stack's orbit,
            // which is the ship's until it moves off.
            var ready = "";
            if (dockedPort != null)
            {
                var stationPort = dockedPort.otherNode;
                dockedPort.Undock();
                ship = dockedPort.part.vessel;
                ready = Ships.ReadyToFly(ship);
                engine = Ships.MainEngine(ship);
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
                if (meet != null) meet = Ships.Find(meet.persistentId) ?? meet;
                engine = Ships.MainEngine(ship);
                have = Ships.DeltaVLeft(ship, out _);
                if (engine == null) throw new ArgumentException(vessel + " is undocked, but has no engine with fuel on board: not started.");
                if (have < needWithMargin)
                    throw new ArgumentException(vessel + " is undocked, but has " + have.ToString("0") + " m/s of fuel; this trip needs " + needWithMargin.ToString("0") +
                                                " with the margin: not started.");
                route.VesselId = ship.persistentId;
                route.VesselName = vessel;
                // Put 60 m ahead on the station's orbit, it is where the station would be a moment later. Flown at the
                // planned times, every burn would come a little further round the body than planned, turning the whole
                // path a hair; over a days-long trip that missed by km (6.5 km Mun → Kerbin, 87 km Minmus → Mun, 2026-11-07)
                // and called it off course. Its burns come that moment sooner instead, so it flies the planned path
                // itself (docs/MATH.md §11.10).
                var lead = RailsDocking.UndockLeadS(ship.orbitDriver.orbit);
                foreach (var b in route.Burns) b.Ut -= lead;
                route.Log.Add("Undocked from " + stationName + " on rails; engines lit, RCS on; burns brought " + lead.ToString("0.00") + " s forward for the " +
                              RailsDocking.UndockAheadM.ToString("0") + " m it's ahead.");
            }

            // The station it's leaving (0 when it was free-flying), for its controller's watch over it in the zone.
            var departId = docked ? route.FromId : 0;

            // Free-flying when it started: it left from the body it's round.
            if (!docked) route.FromName = ship.mainBody.bodyName;
            route.Crew.AddRange(ship.GetVesselCrew().Where(c => c != null).Select(c => c.name));
            route.StartedUt = now;

            // Usually started while the player is flying the ship: off rails already, so it's checked again once it's left.
            route.Disturbed = CourseCheck.OffRails(ship) || CourseCheck.OffRails(meet);

            // A probe's burns: the SOI each happens in, its kind, priority and slot, for the controllers' load (stage C).
            if (CrewRules.NeedsController(route)) CrewRules.PlanControl(route, ship, now, isCraft: true);
            // Cleared out by a station's controller: they have the flight, and hand it to the next SOI's (stage B).
            if (clearedBy != null)
            {
                route.KnownTo = ship.mainBody.bodyName;
                route.HandledBy = clearedBy.Kerbal;
                route.HandledByPost = clearedBy.AtKsc ? KerbinResources.DutyShift.Ksc : clearedBy.PostName;
            }

            // A ship planned again keeps what happened before on its log (found by name too: undocking gives a new id).
            var old = scenario.Routes.LastOrDefault(r => r.VesselId == ship.persistentId || r.VesselName == vessel);
            if (old != null) route.Log.AddRange(old.Log);
            // Planned again on the way (off course): still the same trip, from where it first left.
            var sameTrip = old != null && !old.Finished && old.TargetId == route.TargetId && old.GoalBody == route.GoalBody;
            if (sameTrip && (old.FromId != 0 || old.FromName.Length > 0))
            {
                route.FromId = old.FromId;
                route.FromName = old.FromName;
            }
            if (sameTrip && old.StartedUt > 0) route.StartedUt = old.StartedUt;
            // Planned again on the way: its controllers still know of it.
            if (sameTrip && route.KnownTo.Length == 0)
            {
                route.KnownTo = old.KnownTo;
                route.HandledBy = old.HandledBy;
                route.HandledByPost = old.HandledByPost;
            }
            // Planned again to the same station: it keeps the port it booked.
            if (sameTrip && !toOrbit && route.BookedPortId == 0) route.BookedPortId = old.BookedPortId;
            scenario.Routes.RemoveAll(r => r.VesselId == ship.persistentId || r.VesselName == vessel);
            if (dockedPort != null) route.Log.Add("Undocked from " + stack.vesselName + "." + ready);
            route.Log.Add((old != null && old.State == RouteState.OffCourse ? "Planned again: " : "Started: ") + route.Burns.Count + " burns, " +
                          route.Burns.Sum(b => b.DeltaV).ToString("0") + " m/s planned of " + have.ToString("0") + " m/s in the tanks, with " + engine + "; " + Ships.Mass(ship).ToString("0.00") +
                          " t; KSP's prediction " + CourseCheck.Meets(route) + " within " + meeting + (route.Way.Length > 0 ? "; way: " + route.Way : ""));
            scenario.Routes.Add(route);
            if (clearance.Length > 0)
            {
                TrafficScenario.Note(route, clearance, true);
                CrewRules.LogClearance(clearedBy, vessel, ship.persistentId, now);
            }
            // Its controller watches it out of the zone, and takes no other craft there till it's out.
            if (clearedBy != null)
            {
                route.ZoneController = clearedBy.Kerbal;
                route.ZoneStationId = departId;
            }
            if (stale) route.Log.Add("Its plan went stale while it held for clearance: it's planned again once it's clear of " + route.FromName + ".");
            // Undocked on rails 60 m ahead of where the plan started (its burns brought forward to match): checked now.
            if (railsStack != null) CourseCheck.Check(route, ship, now, "After undocking on rails", expected: true);
            // Free-flying and already off by more than the course check allows: corrected now (docked ones are checked
            // once they've backed off).
            else if (correctOnTheWay && !docked) route.CheckSoon = ship.mainBody.bodyName;
            if (correctOnTheWay) route.Log.Add("KSP's prediction " + CourseCheck.Meets(route) + " " + meeting + " off: a course correction puts it right on the way.");
            ScreenMessages.PostScreenMessage(ship.vesselName + (old != null && old.State == RouteState.OffCourse ? " was planned again and is" : " is") +
                                             " flying to " + (toOrbit ? "an orbit round " + goalBody : meet.vesselName) + " on its own", 6f, ScreenMessageStyle.UPPER_CENTER);
            return route.Summary();
        }

        /// <summary>One line per route: who's going where and how far along.</summary>
        [KRPCProcedure]
        public static IList<string> Routes()
        {
            var scenario = TrafficScenario.Instance;
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
            var scenario = TrafficScenario.Instance;
            var route = FindRoute(vessel);
            if (scenario == null || route == null) return false;
            scenario.Routes.Remove(route);
            return true;
        }

        /// <summary>
        /// A held departure: no controller at the station is free to clear it, so it stays docked. Its route is kept as
        /// Held (the plan, the trip, the booked port), and it undocks by itself once one is free (<see cref="Resume"/>).
        /// </summary>
        private static string Hold(TrafficScenario scenario, Route route, string vessel, Vessel station, string busy, double now, string approval = null)
        {
            route.State = RouteState.Held;
            route.VesselId = 0;
            route.VesselName = vessel;
            if (station != null)
            {
                route.FromId = station.persistentId;
                route.FromName = station.vesselName;
            }
            route.WaitSince = now;
            route.ReplanNote = busy;
            route.AwaitingApproval = approval != null;
            route.ApprovalNote = approval ?? "";
            var old = scenario.Routes.LastOrDefault(r => r.VesselName == vessel);
            if (old != null) route.Log.AddRange(old.Log);
            scenario.Routes.RemoveAll(r => r.VesselName == vessel);
            scenario.Routes.Add(route);
            TrafficScenario.Note(route, approval != null
                ? "Holding" + (station != null ? " at " + station.vesselName : "") + ": " + busy + ". Answer in the game: fly it uncontrolled, or keep it here."
                : "Holding at " + station.vesselName + ": " + busy + ". It undocks by itself once a controller is free.", true);
            return route.Summary();
        }

        /// <summary>A held departure, now cleared: started as it was sent, undocking now. Its plan may have gone stale meanwhile.</summary>
        internal static string Resume(Route held)
        {
            var burns = new List<double>();
            var titles = new List<string>();
            foreach (var b in held.Burns)
            {
                burns.Add(b.Ut);
                burns.Add(b.Prograde);
                burns.Add(b.Normal);
                burns.Add(b.Radial);
                titles.Add(b.Title);
            }
            if (held.EndsInOrbit)
                return Start(held.VesselName, null, burns, titles, double.NaN, held.Way, held.GoalBody,
                    new List<double> { held.GoalSma, held.GoalEcc, held.GoalInc, held.GoalLan, held.GoalArgPe, held.GoalMeanAnomaly, held.GoalEpoch }, resumed: true);
            return Start(held.VesselName, held.TargetName, burns, titles, held.MeetUt, held.Way, null, null, resumed: true);
        }

        /// <summary>A held departure leaving with its first burn closer than this (s) is treated as stale: no time to back off first.</summary>
        private const double StaleLeadS = 120;

        /// <summary>
        /// The off-course routes waiting to be planned again, one line each, tab-separated: ship, target, game time now,
        /// then for the ship and then the target: body, semi-major axis (m), eccentricity, inclination, LAN and argument of
        /// periapsis (degrees), mean anomaly at epoch (radians), epoch (s). KSP's own orbit elements, as the save has them.
        /// </summary>
        [KRPCProcedure]
        public static IList<string> ReplanRequests()
        {
            var list = new List<string>();
            var scenario = TrafficScenario.Instance;
            if (scenario == null) return list;
            var now = Planetarium.GetUniversalTime();
            foreach (var r in scenario.Routes.Where(r => r.State == RouteState.OffCourse))
            {
                var ship = Ships.Find(r.VesselId);
                var target = Ships.Find(r.TargetId);
                if (ship == null || target == null) continue;
                // A probe with nobody covering its SOI waits: its new plan's next burn would be missed too.
                if (CrewRules.HoldsReplan(r, ship, out var held))
                {
                    CourseCheck.ReplanProblem(r, held);
                    continue;
                }
                // Then the way it was planned ("Meets soonest"…) and the Δv in its tanks, so the planner keeps the way if the fuel allows.
                list.Add(string.Join(Tab, new[] { ship.vesselName, target.vesselName, R(now), Elements(ship.orbitDriver.orbit), Elements(target.orbitDriver.orbit),
                    r.Way, R(Ships.DeltaVLeft(ship, out _)) }));
            }
            return list;
        }

        /// <summary>A line from the planner about a ship's route (why it was planned the way it was), shown in the game and logged.</summary>
        [KRPCProcedure]
        public static void RouteNote(string vessel, string text)
        {
            var route = FindRoute(vessel);
            if (route != null) TrafficScenario.Note(route, text, true);
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
            var scenario = TrafficScenario.Instance;
            return scenario == null ? 0 : scenario.Routes.RemoveAll(r => r.Finished || r.State == RouteState.Stopped);
        }

        private static Route FindRoute(string vessel)
        {
            var scenario = TrafficScenario.Instance;
            return scenario?.Routes.LastOrDefault(r => r.VesselName == vessel);
        }

        private static TrafficScenario RequireScenario()
        {
            var scenario = TrafficScenario.Instance;
            if (scenario == null) throw new InvalidOperationException("Load a save first (in flight, the tracking station or the space centre).");
            return scenario;
        }
    }
}
