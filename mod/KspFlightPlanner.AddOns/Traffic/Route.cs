using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Traffic
{
    /// <summary>One planned burn of a route, in KSP's maneuver-node axes, at a game time.</summary>
    internal sealed class RouteBurn
    {
        public double Ut;
        public double Prograde, Normal, Radial;
        public string Title = "";
        public bool Done;

        public double DeltaV => Math.Sqrt(Prograde * Prograde + Normal * Normal + Radial * Radial);
    }

    /// <summary>
    /// Flying the burns; Arrived = parked beside the target, docking as soon as both are loaded; Docked; Stopped;
    /// OffCourse = a check found it would miss: paused, no more burns, until the planner sends a new plan;
    /// Approaching = met the target with the player there: lined up on a free port's axis, the physics flyer brings it in;
    /// Waiting = met the target with no free port: parked 3 km behind it on its orbit till one frees up (PortBooking);
    /// InOrbit = a trip to an orbit (no target craft) has made its last burn: done, like Docked.
    /// </summary>
    internal enum RouteState { Flying, Arrived, Docked, Stopped, OffCourse, Approaching, Waiting, InOrbit }

    /// <summary>
    /// A ship flying a planned trip without anyone at the controls: at each burn's time its orbit is changed as that burn
    /// would, and its fuel burnt; at the meeting time it's parked beside the target.
    /// </summary>
    internal sealed class Route
    {
        public uint VesselId;
        public string VesselName = "";
        public uint TargetId;
        public string TargetName = "";
        public double MeetUt;

        /// <summary>
        /// A trip to an orbit round a body instead of to a craft (TargetId 0): the planned orbit after the last burn, in KSP's
        /// own elements (degrees, mean anomaly in radians, its epoch). It stands in for the target: the checks and course
        /// corrections aim the ship at where this orbit puts it at <see cref="MeetUt"/> (the last burn's time), and the last
        /// burn is the "match" onto it. docs/MATH.md §11.16.
        /// </summary>
        public string GoalBody = "";
        public double GoalSma, GoalEcc, GoalInc, GoalLan, GoalArgPe, GoalMeanAnomaly, GoalEpoch;

        /// <summary>The trip ends in an orbit (no meeting, no port, no docking).</summary>
        public bool EndsInOrbit => GoalBody.Length > 0;

        /// <summary>The planned orbit as a KSP orbit (null when it isn't an orbit trip, or the body isn't in this game).</summary>
        public Orbit GoalOrbit()
        {
            if (!EndsInOrbit) return null;
            var body = FlightGlobals.Bodies?.FirstOrDefault(b => b.bodyName == GoalBody);
            return body == null ? null : new Orbit(GoalInc, GoalEcc, GoalSma, GoalLan, GoalArgPe, GoalMeanAnomaly, GoalEpoch, body);
        }

        /// <summary>
        /// Where the trip left from, for the pilots' log: the station it undocked from (vessel persistent id and name), or
        /// 0 and the body it was round when it started free-flying.
        /// </summary>
        public uint FromId;
        public string FromName = "";

        /// <summary>The kerbals aboard when it started (names): the trip goes on each one's flight log (Kerbin Resources) when it docks.</summary>
        public List<string> Crew = new List<string>();

        /// <summary>When it first started (game time): the flight log's departure. Kept when it's planned again on the way.</summary>
        public double StartedUt;

        /// <summary>The trip has gone on the flight log (once per route).</summary>
        public bool Credited;

        /// <summary>What it carries (<see cref="ModuleTrafficRole"/>: crew & supplies, or tanker), read from its command parts when the route starts: the ports it may use.</summary>
        public string Role = ModuleTrafficRole.CrewSupplies;

        /// <summary>The station port booked for it (part persistent id; 0 = none yet): docs/MATH.md §11.14.</summary>
        public uint BookedPortId;

        /// <summary>Waiting for a free port since (game time), and whether it has said it's been a day.</summary>
        public double WaitSince;
        public bool ToldLongWait;

        /// <summary>When it next looks for a free port (real time; not saved).</summary>
        internal float NextBookTry;

        /// <summary>Which way of making the trip the player picked ("Meets soonest", "Cheapest"…; empty when there was one): replans keep it when the fuel allows.</summary>
        public string Way = "";
        public RouteState State = RouteState.Flying;
        public List<RouteBurn> Burns = new List<RouteBurn>();
        public List<string> Log = new List<string>();

        /// <summary>
        /// The ship or the target has been off rails (physics running: the player flying it or near it) since the plan was
        /// last checked, so something may have moved it. Checked again once both are back on rails, or before the next burn.
        /// </summary>
        public bool Disturbed;

        /// <summary>The physics flyer may fly it when it's loaded near the player (the panel's switch; off once the player takes the controls).</summary>
        public bool Autopilot = true;

        /// <summary>Not saved: parked and unwatched, when it next tries docking on rails (real s), and why the last try failed.</summary>
        public float AwayDockTry;
        public string AwayDockNote;

        /// <summary>Why the planner couldn't plan it again yet (off course only), so the same reason isn't shown twice.</summary>
        internal string ReplanNote;

        /// <summary>Why the crew rules last kept the autopilot off it (not saved), so it's said once.</summary>
        internal string NotAllowedNote;

        /// <summary>The body it was round when found off course: leaving it is worth stopping time warp for.</summary>
        internal string OffCourseBody;

        // Off course: since when (real time), for holding warp while the planner plans it again, and whether the player
        // has been told the planner isn't answering.
        internal float OffCourseSince = -1;

        /// <summary>It has just moved into this body's sphere of influence: checked next frame (not saved).</summary>
        internal string CheckSoon;
        internal bool WarnedNoPlanner;

        // Docking, in this scene only (not saved): when both ships were first seen loaded and unpacked, which port is
        // docking and since when (real time), and whether this scene already tried.
        internal float BothLoadedSince = -1;
        internal uint DockingPortId;
        internal float DockingSince = -1;
        internal bool DockTried;

        // Arriving with the player there (not saved: a save made mid-approach parks it on load): the station port it
        // comes in to (part id) and since when (real time).
        internal uint ApproachPortId;
        internal float ApproachSince = -1;
        // When it started coming in (real time; not saved, not moved by warp): its place in the queue at the station.
        internal float QueueSince;

        // Coming in (not saved, but kept while the pilot is remade after warp): the side of the station it holds at, the
        // checkpoint it's reached, and when the next one comes (game time), for stopping warp before it.
        internal Vector3d ApproachHoldDir;
        internal int ApproachStage = -1;
        internal double ApproachNextCheckUt = double.NaN;

        // Leaving a station it was docked at (not saved): its own port's part (to find the ship again if KSP renumbers
        // it after undocking), the station port it left (part id), and whether it's still backing off from it.
        internal uint ShipPortPartId;
        internal uint BackOffPortId;
        internal bool BackingOff;

        /// <summary>The next thing due: a burn's time, or the meeting once the burns are done.</summary>
        public double NextUt
        {
            get
            {
                var burn = Burns.FirstOrDefault(b => !b.Done);
                return burn != null ? burn.Ut : MeetUt;
            }
        }

        /// <summary>Finished: docked at its target, or in its orbit.</summary>
        public bool Finished => State == RouteState.Docked || State == RouteState.InOrbit;

        public string Summary()
        {
            var done = Burns.Count(b => b.Done);
            var head = VesselName + " → " + TargetName + ": ";
            switch (State)
            {
                case RouteState.Arrived:
                    // Loaded, it's docking now; else it docks on its own once someone's there (nothing for the player to do).
                    return head + (Ships.Find(VesselId) is { loaded: true } ? "docking" : "parked beside it; docks on rails while you're away from both");
                case RouteState.Approaching: return head + "coming in to its port";
                case RouteState.Waiting:
                    return head + "waiting 3 km behind it for a free port, since " + KSPUtil.dateTimeFormatter.PrintDateCompact(WaitSince, true, true) +
                           (ToldLongWait ? " (over a day: free a port there)" : "");
                case RouteState.Docked: return head + "docked";
                case RouteState.InOrbit: return head + "arrived in orbit";
                case RouteState.OffCourse: return head + "off course, waiting to be planned again" + (ReplanNote != null ? " (" + ReplanNote + ")" : "");
                case RouteState.Stopped: return head + "stopped (" + (Log.Count > 0 ? Log[Log.Count - 1] : "") + ")";
                default: return head + done + " of " + Burns.Count + " burns done, next " + KSPUtil.dateTimeFormatter.PrintDateCompact(NextUt, true, true);
            }
        }

        public void Save(ConfigNode node)
        {
            node.AddValue("vesselId", VesselId);
            node.AddValue("vesselName", VesselName);
            node.AddValue("targetId", TargetId);
            node.AddValue("targetName", TargetName);
            node.AddValue("meetUt", MeetUt.ToString("R", CultureInfo.InvariantCulture));
            if (EndsInOrbit)
            {
                var goal = node.AddNode("GOAL");
                goal.AddValue("body", GoalBody);
                goal.AddValue("sma", GoalSma.ToString("R", CultureInfo.InvariantCulture));
                goal.AddValue("ecc", GoalEcc.ToString("R", CultureInfo.InvariantCulture));
                goal.AddValue("inc", GoalInc.ToString("R", CultureInfo.InvariantCulture));
                goal.AddValue("lan", GoalLan.ToString("R", CultureInfo.InvariantCulture));
                goal.AddValue("argPe", GoalArgPe.ToString("R", CultureInfo.InvariantCulture));
                goal.AddValue("mEp", GoalMeanAnomaly.ToString("R", CultureInfo.InvariantCulture));
                goal.AddValue("epoch", GoalEpoch.ToString("R", CultureInfo.InvariantCulture));
            }
            node.AddValue("fromId", FromId);
            node.AddValue("fromName", FromName);
            foreach (var kerbal in Crew) node.AddValue("crew", kerbal);
            node.AddValue("startedUt", StartedUt.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("credited", Credited);
            node.AddValue("role", Role);
            node.AddValue("bookedPort", BookedPortId);
            node.AddValue("waitSince", WaitSince.ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("toldLongWait", ToldLongWait);
            node.AddValue("way", Way);
            node.AddValue("state", State.ToString());
            node.AddValue("disturbed", Disturbed);
            node.AddValue("autopilot", Autopilot);
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

        public static Route Load(ConfigNode node)
        {
            double D(ConfigNode n, string key) => double.Parse(n.GetValue(key), CultureInfo.InvariantCulture);
            var route = new Route
            {
                VesselId = uint.Parse(node.GetValue("vesselId"), CultureInfo.InvariantCulture),
                VesselName = node.GetValue("vesselName") ?? "",
                TargetId = uint.Parse(node.GetValue("targetId"), CultureInfo.InvariantCulture),
                TargetName = node.GetValue("targetName") ?? "",
                MeetUt = D(node, "meetUt"),
                FromId = uint.TryParse(node.GetValue("fromId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromId) ? fromId : 0,
                FromName = node.GetValue("fromName") ?? "",
                Credited = node.GetValue("credited") == "True",
                Role = node.GetValue("role") ?? ModuleTrafficRole.CrewSupplies,
                BookedPortId = uint.TryParse(node.GetValue("bookedPort"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var booked) ? booked : 0,
                WaitSince = double.TryParse(node.GetValue("waitSince"), NumberStyles.Float, CultureInfo.InvariantCulture, out var waitSince) ? waitSince : 0,
                ToldLongWait = node.GetValue("toldLongWait") == "True",
                Way = node.GetValue("way") ?? "",
                State = (RouteState)Enum.Parse(typeof(RouteState), node.GetValue("state")),
                Disturbed = node.GetValue("disturbed") == "True",
                Autopilot = node.GetValue("autopilot") != "False",
            };
            route.Crew.AddRange(node.GetValues("crew"));
            route.Crew.AddRange(node.GetValues("pilot")); // saves from before Kerbin Resources kept pilots only
            route.StartedUt = double.TryParse(node.GetValue("startedUt"), NumberStyles.Float, CultureInfo.InvariantCulture, out var started) ? started : 0;
            var goal = node.GetNode("GOAL");
            if (goal != null)
            {
                route.GoalBody = goal.GetValue("body") ?? "";
                route.GoalSma = D(goal, "sma");
                route.GoalEcc = D(goal, "ecc");
                route.GoalInc = D(goal, "inc");
                route.GoalLan = D(goal, "lan");
                route.GoalArgPe = D(goal, "argPe");
                route.GoalMeanAnomaly = D(goal, "mEp");
                route.GoalEpoch = D(goal, "epoch");
            }
            foreach (var n in node.GetNodes("BURN"))
                route.Burns.Add(new RouteBurn
                {
                    Ut = D(n, "ut"),
                    Prograde = D(n, "prograde"),
                    Normal = D(n, "normal"),
                    Radial = D(n, "radial"),
                    Title = n.GetValue("title") ?? "",
                    Done = n.GetValue("done") == "True",
                });
            // Saved mid-approach: meet again on load (it parks, or lines up again if the player is there).
            if (route.State == RouteState.Approaching) route.State = RouteState.Flying;
            var log = node.GetNode("LOG");
            if (log != null) route.Log.AddRange(log.GetValues("line"));
            return route;
        }
    }
}
