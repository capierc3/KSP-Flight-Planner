using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Haulers
{
    /// <summary>
    /// The on-rails route flyer: flies a route while the ship is packed (the tracking station, map view from another ship,
    /// time warp, or far from the player). Every burn is an instant change of orbit applied at its own planned time, even
    /// when time warp has already gone past it: the new orbit is pinned to the burn's time, so the ship ends up exactly
    /// where it would be had it burnt on time. Overdue events are applied in order, so nothing is missed after a long warp
    /// or a scene change. At the meeting it parks the ship beside the target; docking places it at a port (a stand-in
    /// until the physics flyer flies the approach for real). docs/MATH.md §11.1–11.2.
    /// </summary>
    internal static class RailsFlyer
    {
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
        internal static void Dock(HaulerRoute route)
        {
            var ship = HaulerShip.Find(route.VesselId);
            var station = HaulerShip.Find(route.TargetId);

            if (route.DockingSince >= 0)
            {
                // Docked: the ship has become part of the station (its own vessel is gone, its port is the station's).
                if (HaulerShip.PortDocked(route.DockingPortId))
                {
                    route.State = RouteState.Docked;
                    HaulerScenario.Note(route, "Docked at " + route.TargetName + ".", true);
                    return;
                }
                if (Time.time - route.DockingSince > DockWaitS)
                {
                    route.DockingSince = -1;
                    HaulerScenario.Note(route, "Didn't dock: the magnets didn't catch it. It's beside the port: dock by hand.", true);
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
                HaulerScenario.Note(route, "Couldn't dock: " + station.vesselName + " has no free port that fits " + ship.vesselName + "'s.", true);
                return;
            }
            try
            {
                HaulerShip.PlaceForDocking(ship, mine, station, theirs);
                route.DockingPortId = mine.part.persistentId;
                route.DockingSince = Time.time;
                HaulerScenario.Note(route, "Docking: " + mine.part.partInfo.title + " to " + station.vesselName + "'s " + theirs.part.partInfo.title + ".", false);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                HaulerScenario.Note(route, "Couldn't dock: " + e.Message, true);
            }
        }

        internal static void Step(HaulerRoute route, double now)
        {
            var ship = HaulerShip.Find(route.VesselId);
            if (ship == null)
            {
                HaulerScenario.Stop(route, route.VesselName + " is gone (recovered, destroyed or merged by docking).");
                return;
            }
            route.VesselName = ship.vesselName;
            try
            {
                var burn = route.Burns.FirstOrDefault(b => !b.Done);
                var ut = burn != null ? burn.Ut : route.MeetUt;
                // Still off rails (the player is flying it or near it) when a burn comes up: check from where it is now.
                if (route.Disturbed)
                {
                    if (!CourseCheck.Check(route, ship, Math.Min(now, ut), "Before " + (burn != null ? "burn " + (route.Burns.IndexOf(burn) + 1) : "the meeting"))) return;
                    route.Disturbed = CourseCheck.OffRails(ship) || CourseCheck.OffRails(HaulerShip.Find(route.TargetId));
                }
                if (burn != null) DoBurn(route, ship, burn);
                else Arrive(route, ship);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                HaulerScenario.Stop(route, e.Message);
            }
        }

        private static void DoBurn(HaulerRoute route, Vessel ship, HaulerBurn burn)
        {
            burn.Done = true;
            var number = route.Burns.IndexOf(burn) + 1;
            var engine = HaulerShip.MainEngine(ship);
            if (engine == null)
            {
                HaulerScenario.Stop(route, "Burn " + number + ": " + ship.vesselName + " has no engine with fuel left.");
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
            if (got < planned - 0.5) HaulerScenario.Stop(route, "Out of fuel at burn " + number + ": got " + got.ToString("0") + " of " + planned.ToString("0") + " m/s.");
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
                HaulerScenario.Stop(route, route.TargetName + " is gone.");
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
    }
}
