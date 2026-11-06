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
                    HaulerScenario.Note(route, "Docked at " + route.TargetName + "." + PhysicsFlyer.ShutDown(route.DockingPortId), true);
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
                // The autopilot is flying it (the ship is near the player with physics on): it owns the route, burns and
                // meeting alike; a check or a burn from here would fight it (the crash of 2026-11-05).
                if (PhysicsFlyer.Flies(route)) return;
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
            var head = "Arrived: " + miss + (used.Length > 0 ? "; final approach used " + used : "") + "; " + HaulerShip.Mass(ship).ToString("0.00") + " t left";

            if (LineUp(route, ship, target, head)) return;
            // Nobody near either ship: docked on rails, on the saved records, as KSP would leave them docked.
            if (!ship.loaded && !target.loaded)
            {
                var targetName = target.vesselName;
                if (RailsDocking.Dock(ship, target, out var why) != null)
                {
                    route.State = RouteState.Docked;
                    HaulerScenario.Note(route, head + "; docked at " + targetName + " on rails.", false);
                    ScreenMessages.PostScreenMessage(route.VesselName + " has docked at " + targetName, 6f, ScreenMessageStyle.UPPER_CENTER);
                    return;
                }
                head += "; couldn't dock on rails (" + why + ")";
            }
            Park(route, ship, target, head);
        }

        /// <summary>
        /// Parks the ship on the target's own orbit, a few seconds behind it, so the gap holds until the player comes to
        /// dock: under 200 m, where KSP turns physics on for a ship near the one being flown, so docking can start; 100 m
        /// at least.
        /// </summary>
        internal static void Park(HaulerRoute route, Vessel ship, Vessel target, string head)
        {
            var to = target.orbitDriver.orbit;
            var speed = to.getOrbitalVelocityAtUT(Planetarium.GetUniversalTime()).magnitude;
            var behind = Math.Min(MaxParkM, Math.Max(100, 2 * (HaulerShip.Size(target) + HaulerShip.Size(ship)) + 50));
            HaulerShip.FollowOrbit(ship, to, behind / speed);
            route.State = RouteState.Arrived;
            HaulerScenario.Note(route, head + "; parked " + behind.ToString("0") + " m behind " + route.TargetName + ".", false);
            ScreenMessages.PostScreenMessage(route.VesselName + " has arrived at " + route.TargetName + ": it docks when you're nearby", 8f, ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>How far out from the port the ship is lined up (m): just outside the 2.25 km at which KSP draws ships.</summary>
        internal const double LineUpM = 2400;

        /// <summary>The spot it comes in to and stops at, this far straight out from the port (m).</summary>
        internal const double StandoffM = 150;

        /// <summary>How fast it comes in along the port's axis (m/s, relative to the target).</summary>
        internal const double ComeInMs = 20;

        /// <summary>
        /// Meeting a station the player is at (it's loaded) with the autopilot on: instead of parking, the ship is put
        /// on the axis of a free port that fits, 2.4 km straight out, coming in at 20 m/s. That's just outside the range
        /// at which KSP draws ships, so it's done out of sight; the ship then comes into view, and the physics flyer
        /// brakes it to a stop 150 m off the port (docs/MATH.md §11.6). Placed relative to the target's own orbit, so
        /// only the 2.4 km offset goes through the world/orbit swap. False (and nothing done) when it can't be.
        /// </summary>
        private static bool LineUp(HaulerRoute route, Vessel ship, Vessel target, string head)
        {
            if (!HighLogic.LoadedSceneIsFlight || !route.Autopilot || !target.loaded || ship.loaded) return false;
            var port = HaulerShip.PickStationPort(ship, target);
            if (port == null) return false;

            var now = Planetarium.GetUniversalTime();
            var axis = (Vector3d)port.nodeTransform.forward;
            var spot = (Vector3d)port.nodeTransform.position + axis * (StandoffM + LineUpM);
            var to = target.orbitDriver.orbit;
            var pos = to.getRelativePositionAtUT(now) + (spot - target.GetWorldPos3D()).xzy;
            var vel = to.getOrbitalVelocityAtUT(now) + (-axis * ComeInMs).xzy;
            HaulerShip.SetOrbit(ship, pos, vel, to.referenceBody, now);
            HaulerShip.RaisePhysicsRange(ship, PhysicsFlyer.PhysicsRangeM);

            route.State = RouteState.Approaching;
            route.ApproachStage = -1;
            route.ApproachHoldDir = Vector3d.zero;
            route.ApproachNextCheckUt = double.NaN;
            route.ApproachPortId = port.part.persistentId;
            route.ApproachSince = Time.time;
            CourseCheck.StopWarp();
            HaulerScenario.Note(route, head + "; coming in to " + target.vesselName + "'s " + port.part.partInfo.title + " from " +
                                       ((StandoffM + LineUpM) / 1000).ToString("0.0") + " km out at " + ComeInMs.ToString("0") + " m/s.", false);
            ScreenMessages.PostScreenMessage(route.VesselName + " is coming in to " + route.TargetName + ": watch for it", 8f, ScreenMessageStyle.UPPER_CENTER);
            return true;
        }

        /// <summary>A ship due at a station the player is at is lined up this long before the meeting (game s) at the latest...</summary>
        private const double LineUpEarlyS = 600;

        /// <summary>...or once it's this close (m), whichever comes first: well before KSP loads it at 2.25 km.</summary>
        private const double LineUpEarlyM = 30000;

        /// <summary>
        /// Every frame in flight for a flying route: when the player is at the target (it's loaded) and the ship isn't, and
        /// all that's left is the match at the meeting, the ship is lined up on a port now, while it's still out of sight,
        /// rather than at the meeting time. Otherwise it can come into KSP's range on its intercept, hundreds of m/s fast,
        /// with no room to stop (the crash of 2026-11-05). The match's fuel is paid as the rails flyer would.
        /// </summary>
        internal static void WatchArrival(HaulerRoute route, double now)
        {
            if (!HighLogic.LoadedSceneIsFlight || !route.Autopilot) return;
            var left = route.Burns.Where(b => !b.Done).ToList();
            if (left.Count > 1 || left.Count == 1 && left[0].Ut < route.MeetUt - 1) return;
            var ship = HaulerShip.Find(route.VesselId);
            var target = HaulerShip.Find(route.TargetId);
            if (ship == null || target == null || ship.loaded || !target.loaded) return;
            if (route.MeetUt - now > LineUpEarlyS && (ship.GetWorldPos3D() - target.GetWorldPos3D()).magnitude > LineUpEarlyM) return;

            var used = "";
            var engine = HaulerShip.MainEngine(ship);
            foreach (var b in left)
            {
                if (engine != null) HaulerShip.Burn(ship, engine, b.DeltaV, out used);
                b.Done = true;
            }
            var head = "Due at " + target.vesselName + " (you're there): lined up early" + (used.Length > 0 ? ", the match used " + used : "") + "; " +
                       HaulerShip.Mass(ship).ToString("0.00") + " t left";
            if (!LineUp(route, ship, target, head)) Park(route, ship, target, head);
        }

        /// <summary>Coming in, time warp stops this long before the next checkpoint (game s).</summary>
        internal const double WarpStopS = 20;

        /// <summary>How long a lined-up ship may take to come into physics range before it's parked instead (real s).</summary>
        private const float ComeInGiveUpS = 300;

        /// <summary>
        /// Every frame while a ship is coming in: time warp stays off (on rails it would sail through the station), and
        /// if the player has gone, the ship never came into physics range, or the autopilot can't fly it, it's parked.
        /// </summary>
        internal static void WatchApproach(HaulerRoute route)
        {
            var ship = HaulerShip.Find(route.VesselId);
            var target = HaulerShip.Find(route.TargetId);
            if (ship == null || target == null)
            {
                HaulerScenario.Stop(route, (ship == null ? route.VesselName : route.TargetName) + " is gone.");
                return;
            }
            string why = null;
            // Time warp packs it on purpose: the "never came into physics range" clock only runs without warp.
            if (TimeWarp.fetch != null && TimeWarp.CurrentRateIndex > 0) route.ApproachSince = Time.time;
            // Gone from both: the player is near neither the station nor the ship (riding along keeps the ship loaded).
            // Packed by time warp isn't gone: warping while riding the ship in (the station not yet loaded) parked it beside
            // the station, a jump, in the test of 2026-11-06. Only when the ship is unloaded too.
            if (!HighLogic.LoadedSceneIsFlight || !target.loaded && !ship.loaded) why = "you left";
            else if (!CourseCheck.OffRails(ship) && Time.time - route.ApproachSince > ComeInGiveUpS) why = "it never came into physics range";
            else if (CourseCheck.OffRails(ship) && !PhysicsFlyer.Flies(route) && route.Autopilot) why = "the autopilot can't fly it";
            if (why != null)
            {
                Park(route, ship, target, "Coming in stopped (" + why + ")");
                return;
            }
            // Time warp is fine while it coasts between checkpoints far out; it stops 20 s before the next one, and there's
            // none close in (the last minute), where it brakes and steers.
            if (TimeWarp.fetch != null && TimeWarp.CurrentRateIndex > 0 && TimeWarp.WarpMode == TimeWarp.Modes.HIGH)
            {
                var next = route.ApproachNextCheckUt;
                var now = Planetarium.GetUniversalTime();
                if (double.IsNaN(next) || now >= next - WarpStopS - TimeWarp.CurrentRate * 0.1)
                {
                    CourseCheck.StopWarp();
                    ScreenMessages.PostScreenMessage(route.VesselName + " is coming in: " + (double.IsNaN(next) ? "no time warp close in" : "checkpoint coming up"), 3f,
                        ScreenMessageStyle.UPPER_CENTER);
                }
            }
        }
    }
}
