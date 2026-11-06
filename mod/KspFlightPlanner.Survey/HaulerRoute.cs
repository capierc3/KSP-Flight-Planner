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

    /// <summary>
    /// Flying the burns; Arrived = parked beside the target, docking as soon as both are loaded; Docked; Stopped;
    /// OffCourse = a check found it would miss: paused, no more burns, until the planner sends a new plan;
    /// Approaching = met the target with the player there: lined up on a free port's axis, the physics flyer brings it in.
    /// </summary>
    internal enum RouteState { Flying, Arrived, Docked, Stopped, OffCourse, Approaching }

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

        /// <summary>
        /// The ship or the target has been off rails (physics running: the player flying it or near it) since the plan was
        /// last checked, so something may have moved it. Checked again once both are back on rails, or before the next burn.
        /// </summary>
        public bool Disturbed;

        /// <summary>The physics flyer may fly it when it's loaded near the player (the panel's switch; off once the player takes the controls).</summary>
        public bool Autopilot = true;

        /// <summary>Why the planner couldn't plan it again yet (off course only), so the same reason isn't shown twice.</summary>
        internal string ReplanNote;

        /// <summary>The body it was round when found off course: leaving it is worth stopping time warp for.</summary>
        internal string OffCourseBody;

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

        public string Summary()
        {
            var done = Burns.Count(b => b.Done);
            var head = VesselName + " → " + TargetName + ": ";
            switch (State)
            {
                case RouteState.Arrived: return head + "parked beside it, docks when you're nearby";
                case RouteState.Approaching: return head + "coming in to its port";
                case RouteState.Docked: return head + "docked";
                case RouteState.OffCourse: return head + "off course, waiting to be planned again" + (ReplanNote != null ? " (" + ReplanNote + ")" : "");
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
                Disturbed = node.GetValue("disturbed") == "True",
                Autopilot = node.GetValue("autopilot") != "False",
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
            // Saved mid-approach: meet again on load (it parks, or lines up again if the player is there).
            if (route.State == RouteState.Approaching) route.State = RouteState.Flying;
            var log = node.GetNode("LOG");
            if (log != null) route.Log.AddRange(log.GetValues("line"));
            return route;
        }
    }
}
