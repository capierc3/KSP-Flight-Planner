using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Haulers
{
    /// <summary>
    /// What the hauler runner does to a ship without flying it: move it onto another orbit, burn its fuel, and park it
    /// beside another ship. Works on ships that aren't loaded (the usual case: the player is elsewhere or in the
    /// tracking station) and on loaded ones (it puts them on rails first).
    ///
    /// Orbit vectors here are in KSP's orbit frame (z towards the body's north pole), where the usual right-handed maths
    /// holds, so a burn's axes are built the same way as the planner's NodeBurn (docs/MATH.md §1.8). Nothing here
    /// swaps axes: only Unity's world frame is y-up, and nothing here touches it.
    /// </summary>
    internal static class HaulerShip
    {
        /// <summary>KSP's standard gravity for Isp, m/s².</summary>
        public const double G0 = 9.80665;

        public static Vessel Find(uint persistentId)
        {
            foreach (var v in FlightGlobals.Vessels)
                if (v != null && v.persistentId == persistentId) return v;
            return null;
        }

        /// <summary>The one ship with this exact name (debris and flags left out); throws if there's none or several.</summary>
        public static Vessel FindByName(string name)
        {
            var found = FlightGlobals.Vessels
                .Where(v => v != null && v.vesselName == name && v.vesselType != VesselType.Debris && v.vesselType != VesselType.Flag
                            && v.vesselType != VesselType.SpaceObject)
                .ToList();
            if (found.Count == 0) throw new ArgumentException("No ship called \"" + name + "\" in this save.");
            if (found.Count > 1) throw new ArgumentException(found.Count + " ships are called \"" + name + "\": rename one so they can be told apart.");
            return found[0];
        }

        /// <summary>A ship the runner may move: in space, not landed or splashed down.</summary>
        public static bool InSpace(Vessel v) =>
            v.situation == Vessel.Situations.ORBITING || v.situation == Vessel.Situations.ESCAPING || v.situation == Vessel.Situations.SUB_ORBITAL;

        // ---------------- Orbits ----------------

        /// <summary>Prograde, normal and radial-out at a point of an orbit (orbit frame).</summary>
        public static void Axes(Vector3d pos, Vector3d vel, out Vector3d prograde, out Vector3d normal, out Vector3d radial)
        {
            prograde = vel.normalized;
            normal = Vector3d.Cross(pos, vel).normalized;
            radial = Vector3d.Cross(prograde, normal); // in the orbit plane, away from the body
        }

        /// <summary>
        /// Puts <paramref name="vessel"/> on the orbit through <paramref name="pos"/>/<paramref name="vel"/> around
        /// <paramref name="body"/> at <paramref name="ut"/>. The time may be in the past (time warp jumped past the burn):
        /// KSP then places the ship where that orbit has it now, exactly as if it had happened on time.
        /// The way HyperEdit sets a ship's orbit: copy the new elements into the ship's own orbit object.
        /// </summary>
        public static void SetOrbit(Vessel vessel, Vector3d pos, Vector3d vel, CelestialBody body, double ut)
        {
            if (!IsFinite(pos) || !IsFinite(vel))
                throw new InvalidOperationException("The new orbit came out invalid (NaN): left the ship as it was.");

            var target = new Orbit();
            target.UpdateFromStateVectors(pos, vel, body, ut);
            SetOrbit(vessel, target);
        }

        /// <summary>
        /// Puts <paramref name="vessel"/> on <paramref name="leader"/>'s exact orbit, <paramref name="behindS"/> seconds
        /// behind it: the same elements with only the mean anomaly moved back. Same size, shape and tilt means the same
        /// period, so the gap stays put for as long as both are on rails (copying the leader's velocity at a point behind it
        /// gives a slightly different orbit, and the gap swings and drifts: the first test's flaw).
        /// </summary>
        public static void FollowOrbit(Vessel vessel, Orbit leader, double behindS)
        {
            var o = new Orbit(leader.inclination, leader.eccentricity, leader.semiMajorAxis, leader.LAN, leader.argumentOfPeriapsis,
                leader.meanAnomalyAtEpoch - leader.meanMotion * behindS, leader.epoch, leader.referenceBody);
            SetOrbit(vessel, o);
        }

        private static void SetOrbit(Vessel vessel, Orbit target)
        {
            if (double.IsNaN(target.semiMajorAxis) || double.IsNaN(target.eccentricity) || double.IsNaN(target.meanAnomalyAtEpoch))
                throw new InvalidOperationException("The new orbit came out invalid (NaN): left the ship as it was.");

            // A loaded ship has to be on rails to be moved; KSP takes it off again by itself when it's safe.
            if (vessel.loaded && !vessel.packed) vessel.GoOnRails();
            if (HighLogic.LoadedSceneIsFlight)
            {
                try { OrbitPhysicsManager.HoldVesselUnpack(60); } catch { /* not in this scene */ }
            }

            var driver = vessel.orbitDriver;
            var orbit = driver.orbit;
            var oldBody = orbit.referenceBody;
            orbit.inclination = target.inclination;
            orbit.eccentricity = target.eccentricity;
            orbit.semiMajorAxis = target.semiMajorAxis;
            orbit.LAN = target.LAN;
            orbit.argumentOfPeriapsis = target.argumentOfPeriapsis;
            orbit.meanAnomalyAtEpoch = target.meanAnomalyAtEpoch;
            orbit.epoch = target.epoch;
            orbit.referenceBody = target.referenceBody;
            orbit.Init();
            orbit.UpdateFromUT(Planetarium.GetUniversalTime());
            if (oldBody != orbit.referenceBody && driver.OnReferenceBodyChange != null) driver.OnReferenceBodyChange(orbit.referenceBody);
            driver.pos = orbit.pos.xzy;
            driver.vel = orbit.vel;
            if (oldBody != orbit.referenceBody)
                GameEvents.onVesselSOIChanged.Fire(new GameEvents.HostedFromToAction<Vessel, CelestialBody>(vessel, oldBody, orbit.referenceBody));

            // What the save writes for a ship that isn't loaded.
            if (vessel.protoVessel != null) vessel.protoVessel.orbitSnapShot = new OrbitSnapshot(orbit);
        }

        // ---------------- Checking the plan ----------------

        /// <summary>Where a check says the ship ends up at the meeting.</summary>
        internal sealed class Meeting
        {
            public double GapM, SpeedMs;

            /// <summary>Set when it doesn't even end up round the target's body.</summary>
            public string Problem;

            public override string ToString() =>
                Problem ?? (GapM < 10_000 ? GapM.ToString("0") + " m" : (GapM / 1000).ToString("0.0") + " km") + " at " + SpeedMs.ToString("0.0") + " m/s";
        }

        /// <summary>
        /// Where the ship would meet <paramref name="target"/> at <paramref name="meetUt"/> if the burns still to come are
        /// flown as planned from its orbit now (from <paramref name="fromUt"/>). Uses KSP's own patched conics, the same
        /// prediction the map view draws, so moving into and out of moons' spheres of influence is followed as KSP will fly
        /// it. Nothing is planned here: the burns are applied exactly as given, as <see cref="HaulerScenario"/> will.
        /// </summary>
        public static Meeting PredictMeeting(Vessel ship, Vessel target, IEnumerable<HaulerBurn> burns, double fromUt, double meetUt)
        {
            var o = PathAt(ship, burns, fromUt, meetUt);
            var to = target.orbitDriver.orbit;
            if (o.referenceBody != to.referenceBody)
                return new Meeting { Problem = "ends up round " + o.referenceBody.bodyName + ", not " + to.referenceBody.bodyName };
            return new Meeting
            {
                GapM = (o.getRelativePositionAtUT(meetUt) - to.getRelativePositionAtUT(meetUt)).magnitude,
                SpeedMs = (o.getOrbitalVelocityAtUT(meetUt) - to.getOrbitalVelocityAtUT(meetUt)).magnitude,
            };
        }

        /// <summary>
        /// How close two ships' paths come (m) between two times, both left alone on rails: each orbit rebuilt from its
        /// position and velocity now and sampled 400 times. Used after backing off a station, so the hauler isn't left
        /// on a path that swings back through it. Infinity when they're round different bodies.
        /// </summary>
        /// <summary>
        /// The closest the ship comes to a point fixed beside the target (the target's position plus
        /// <paramref name="offset"/>, in the orbit frame) between two times, on their orbits as they are now (with
        /// <paramref name="dv"/>, orbit frame, added to the ship's velocity now): the same closest approach KSP marks on its
        /// map, by 240 samples and then a golden-section search round the nearest. Infinity round different bodies.
        /// </summary>
        public static double ClosestApproach(Vessel ship, Vector3d dv, Vessel target, Vector3d offset, double fromUt, double toUt, out double atUt)
        {
            atUt = fromUt;
            var now = Planetarium.GetUniversalTime();
            var so = ship.orbitDriver.orbit;
            var o = new Orbit();
            o.UpdateFromStateVectors(so.getRelativePositionAtUT(now), so.getOrbitalVelocityAtUT(now) + dv, so.referenceBody, now);
            var to = target.orbitDriver.orbit;
            if (o.referenceBody != to.referenceBody || toUt <= fromUt) return double.PositiveInfinity;
            double Gap(double t) => (o.getRelativePositionAtUT(t) - (to.getRelativePositionAtUT(t) + offset)).magnitude;
            const int samples = 240;
            var best = 0;
            var bestGap = double.PositiveInfinity;
            for (var i = 0; i <= samples; i++)
            {
                var g = Gap(fromUt + (toUt - fromUt) * i / samples);
                if (g < bestGap) { bestGap = g; best = i; }
            }
            double lo = fromUt + (toUt - fromUt) * Math.Max(0, best - 1) / samples, hi = fromUt + (toUt - fromUt) * Math.Min(samples, best + 1) / samples;
            const double phi = 0.6180339887;
            for (var i = 0; i < 40; i++)
            {
                double a = hi - phi * (hi - lo), b = lo + phi * (hi - lo);
                if (Gap(a) < Gap(b)) hi = b; else lo = a;
            }
            atUt = (lo + hi) / 2;
            return Gap(atUt);
        }

        /// <summary>
        /// The Δv to add now (orbit frame) so the ship is at the point beside the target at <paramref name="atUt"/>:
        /// Newton's method on the three components, slopes by nudging each by 0.05 m/s, until it's within 10 m. Null when it
        /// doesn't get there in 10 steps, or it would take more than 50 m/s.
        /// </summary>
        public static Vector3d? AimAt(Vessel ship, Vessel target, Vector3d offset, double atUt)
        {
            var now = Planetarium.GetUniversalTime();
            var so = ship.orbitDriver.orbit;
            var r0 = so.getRelativePositionAtUT(now);
            var v0 = so.getOrbitalVelocityAtUT(now);
            var to = target.orbitDriver.orbit;
            if (so.referenceBody != to.referenceBody || atUt <= now) return null;
            var goal = to.getRelativePositionAtUT(atUt) + offset;
            Vector3d Miss(Vector3d dv)
            {
                var o = new Orbit();
                o.UpdateFromStateVectors(r0, v0 + dv, so.referenceBody, now);
                return o.getRelativePositionAtUT(atUt) - goal;
            }
            var x = Vector3d.zero;
            for (var i = 0; i < 10; i++)
            {
                var g = Miss(x);
                if (g.magnitude < 10) return x.magnitude <= 50 ? (Vector3d?)x : null;
                const double h = 0.05;
                var cx = (Miss(x + new Vector3d(h, 0, 0)) - g) / h;
                var cy = (Miss(x + new Vector3d(0, h, 0)) - g) / h;
                var cz = (Miss(x + new Vector3d(0, 0, h)) - g) / h;
                var det = Vector3d.Dot(cx, Vector3d.Cross(cy, cz));
                if (Math.Abs(det) < 1e-12) return null;
                var step = new Vector3d(Vector3d.Dot(-g, Vector3d.Cross(cy, cz)), Vector3d.Dot(cx, Vector3d.Cross(-g, cz)), Vector3d.Dot(cx, Vector3d.Cross(cy, -g))) / det;
                if (step.magnitude > 20) step = step.normalized * 20;
                x += step;
            }
            return null;
        }

        public static double ClosestPass(Vessel a, Vessel b, double fromUt, double toUt, double onceFartherThanM = 0)
        {
            var now = Planetarium.GetUniversalTime();
            var oa = Copy(a.orbitDriver.orbit, now);
            var ob = Copy(b.orbitDriver.orbit, now);
            if (oa.referenceBody != ob.referenceBody || toUt <= fromUt) return double.PositiveInfinity;
            // Only the part of the path after it has first got farther than onceFartherThanM counts: that's where it could
            // swing back. (Counting from the start, a ship just backed off 30-70 m away always looked too close, and it
            // pushed four times, 2 m/s, in the test of 2026-11-06.)
            var closest = double.PositiveInfinity;
            var overall = double.PositiveInfinity;
            var away = onceFartherThanM <= 0;
            for (var i = 0; i <= 400; i++)
            {
                var t = fromUt + (toUt - fromUt) * i / 400;
                var d = (oa.getRelativePositionAtUT(t) - ob.getRelativePositionAtUT(t)).magnitude;
                overall = Math.Min(overall, d);
                if (!away && d > onceFartherThanM) away = true;
                if (away) closest = Math.Min(closest, d);
            }
            return double.IsPositiveInfinity(closest) ? overall : closest;
        }

        /// <summary>When the ship leaves its body's sphere of influence on its present path (KSP's patched conics); NaN if it doesn't.</summary>
        public static double LeavesSphereUt(Vessel ship)
        {
            var now = Planetarium.GetUniversalTime();
            var o = Copy(ship.orbitDriver.orbit, now);
            o.StartUT = now;
            PatchedConics.CalculatePatch(o, new Orbit(), now, new PatchedConics.SolverParameters(), null);
            return o.patchEndTransition == Orbit.PatchTransitionType.ESCAPE ? o.EndUT : double.NaN;
        }

        /// <summary>
        /// The patch of the ship's path it's on at <paramref name="toUt"/>, with the burns (those up to then, in time
        /// order) applied as the rails flyer applies them, carried between them by KSP's patched conics.
        /// </summary>
        public static Orbit PathAt(Vessel ship, IEnumerable<HaulerBurn> burns, double fromUt, double toUt)
        {
            var o = Copy(ship.orbitDriver.orbit, fromUt);
            var t = fromUt;
            var ordered = burns.OrderBy(b => b.Ut).ToList();
            // Rebuilt once more a minute along, before coasting on (unless a burn or the end comes first), so the check
            // and the correction solver (which has a burn there) predict the same way. (The 25 km "misses" leaving the Mun
            // that this was first meant for were real: a burn's last 0.05 m/s; see CourseCheck.Correct.)
            var settle = fromUt + 60;
            if (settle < toUt && (ordered.Count == 0 || ordered[0].Ut > settle))
            {
                o = Coast(o, fromUt, settle);
                o = Copy(o, settle);
                t = settle;
            }
            foreach (var b in ordered)
            {
                if (b.Ut > toUt) break;
                o = Coast(o, t, b.Ut);
                var pos = o.getRelativePositionAtUT(b.Ut);
                var vel = o.getOrbitalVelocityAtUT(b.Ut);
                Axes(pos, vel, out var prograde, out var normal, out var radial);
                var after = new Orbit();
                after.UpdateFromStateVectors(pos, vel + prograde * b.Prograde + normal * b.Normal + radial * b.Radial, o.referenceBody, b.Ut);
                o = after;
                t = Math.Max(t, b.Ut);
            }
            return Coast(o, t, toUt);
        }

        /// <summary>
        /// A copy of the orbit through its position and velocity at <paramref name="ut"/>, as the burns are applied. Copying
        /// its elements instead went wrong on an escape path (leaving the Mun): the check called a ship on course 1,368 km
        /// off in the test of 2026-11-06, while the same path rebuilt from its position and velocity met within 76 m.
        /// </summary>
        private static Orbit Copy(Orbit o, double ut)
        {
            var copy = new Orbit();
            copy.UpdateFromStateVectors(o.getRelativePositionAtUT(ut), o.getOrbitalVelocityAtUT(ut), o.referenceBody, ut);
            return copy;
        }

        /// <summary>
        /// The patch of <paramref name="o"/>'s path the ship is on at <paramref name="toUt"/>, following SOI changes. Each
        /// patch gets its start and end set first, as KSP's own solver does (one period on, or none on an escape path):
        /// KSP only counts an encounter between the two, and with the end left at 0 no moon was ever met (the Kerbin → Mun
        /// trip of 2026-11-06 was refused as "ends up round Kerbin"; leaving a moon never looks at the end, so it worked).
        /// </summary>
        private static Orbit Coast(Orbit o, double fromUt, double toUt)
        {
            var pars = new PatchedConics.SolverParameters();
            for (var i = 0; i < 8 && toUt > fromUt; i++)
            {
                var next = new Orbit();
                o.StartUT = fromUt;
                o.EndUT = o.eccentricity < 1 ? fromUt + o.period : double.PositiveInfinity;
                PatchedConics.CalculatePatch(o, next, fromUt, pars, null);
                if (o.patchEndTransition != Orbit.PatchTransitionType.ENCOUNTER && o.patchEndTransition != Orbit.PatchTransitionType.ESCAPE) return o;
                if (o.EndUT >= toUt || next.referenceBody == null) return o;
                fromUt = o.EndUT;
                o = next;
            }
            return o;
        }

        private static bool IsFinite(Vector3d v) =>
            !(double.IsNaN(v.x) || double.IsNaN(v.y) || double.IsNaN(v.z) || double.IsInfinity(v.x) || double.IsInfinity(v.y) || double.IsInfinity(v.z));

        /// <summary>
        /// Rough size of a ship: the furthest part from its origin, in metres. Unloaded ships keep each part's position
        /// relative to the ship, so this works without loading it.
        /// </summary>
        public static double Size(Vessel v)
        {
            double far = 0;
            if (v.loaded)
            {
                foreach (var p in v.parts)
                    far = Math.Max(far, (p.transform.position - v.transform.position).magnitude);
            }
            else if (v.protoVessel != null)
            {
                foreach (var p in v.protoVessel.protoPartSnapshots)
                    far = Math.Max(far, p.position.magnitude);
            }
            return far;
        }

        // ---------------- Docking ----------------

        /// <summary>
        /// Just undocked for a trip: every engine on it lit (at zero throttle) and its RCS thrusters and RCS switched on,
        /// so the autopilot, or a player who takes over mid-trip, finds it ready to fly. Docking shut them down. The
        /// thrust limiters are left as they are. Returns what it did, for the log. docs/MATH.md §11.8.
        /// </summary>
        public static string ReadyToFly(Vessel v)
        {
            int engines = 0, thrusters = 0;
            foreach (var part in v.parts)
            {
                foreach (var e in part.FindModulesImplementing<ModuleEngines>())
                    if (!e.EngineIgnited)
                    {
                        e.Activate();
                        engines++;
                    }
                foreach (var rcs in part.FindModulesImplementing<ModuleRCS>())
                    if (!rcs.rcsEnabled)
                    {
                        rcs.rcsEnabled = true;
                        thrusters++;
                    }
            }
            if (v.ctrlState != null) v.ctrlState.mainThrottle = 0;
            v.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
            return engines + thrusters == 0 ? "" : " Lit " + engines + " engine" + (engines == 1 ? "" : "s") + " and switched on " + thrusters + " RCS block" + (thrusters == 1 ? "" : "s") + ".";
        }

        /// <summary>A docking port free to dock: not docked, not shielded shut.</summary>
        internal static bool IsFree(ModuleDockingNode n) =>
            n != null && n.nodeTransform != null && n.otherNode == null &&
            (n.fsm != null ? n.fsm.currentStateName : n.state) == "Ready";

        private static bool Fits(ModuleDockingNode a, ModuleDockingNode b) =>
            a.nodeTypes != null && b.nodeTypes != null && a.nodeTypes.Count > 0 ? a.nodeTypes.Overlaps(b.nodeTypes) : a.nodeType == b.nodeType;

        /// <summary>
        /// The pair of free ports to dock with: on the station, the free port nearest the ship that one of the ship's free
        /// ports fits, leaving out ports in <paramref name="claimed"/> (other haulers'). Both ships must be loaded. False
        /// when there's no such pair.
        /// </summary>
        public static bool PickPorts(Vessel ship, Vessel station, out ModuleDockingNode shipPort, out ModuleDockingNode stationPort, ICollection<uint> claimed = null)
        {
            shipPort = null;
            stationPort = null;
            var mine = ship.FindPartModulesImplementing<ModuleDockingNode>().Where(IsFree).ToList();
            var best = double.MaxValue;
            foreach (var theirs in station.FindPartModulesImplementing<ModuleDockingNode>().Where(IsFree))
            {
                if (claimed != null && claimed.Contains(theirs.part.persistentId)) continue;
                var match = mine.FirstOrDefault(m => Fits(m, theirs));
                if (match == null) continue;
                var d = (theirs.nodeTransform.position - ship.transform.position).sqrMagnitude;
                if (d < best) { best = d; shipPort = match; stationPort = theirs; }
            }
            return shipPort != null;
        }

        /// <summary>The part names of a ship's free docking ports, loaded or not (unloaded: from its saved parts).</summary>
        public static List<string> FreePortParts(Vessel ship)
        {
            if (ship.loaded)
                return ship.FindPartModulesImplementing<ModuleDockingNode>().Where(IsFree).Select(n => n.part.partInfo.name).ToList();
            return RailsDocking.FreePorts(ship).Select(p => p.Value).ToList();
        }

        /// <summary>
        /// The station's free port to come in to, for a ship that may not be loaded (its ports read from the part
        /// designs): of the free ports one of the ship's fits, the one facing most towards where the ship is now, so it
        /// arrives from its own side; ports in <paramref name="claimed"/> (other haulers') left out. Null when none fits.
        /// The station must be loaded.
        /// </summary>
        public static ModuleDockingNode PickStationPort(Vessel ship, Vessel station, ICollection<uint> claimed = null)
        {
            var mine = ship.loaded
                ? ship.FindPartModulesImplementing<ModuleDockingNode>().Where(IsFree).ToList()
                : ship.protoVessel.protoPartSnapshots.Where(p => p.partInfo != null && p.partInfo.partPrefab != null)
                    .SelectMany(p => p.partInfo.partPrefab.FindModulesImplementing<ModuleDockingNode>()).ToList();
            var toShip = (Vector3)(ship.GetWorldPos3D() - station.GetWorldPos3D()).normalized;
            ModuleDockingNode best = null;
            var bestFacing = float.MinValue;
            foreach (var theirs in station.FindPartModulesImplementing<ModuleDockingNode>().Where(IsFree))
            {
                if (claimed != null && claimed.Contains(theirs.part.persistentId)) continue;
                if (!mine.Any(m => Fits(m, theirs))) continue;
                var facing = Vector3.Dot(theirs.nodeTransform.forward, toShip);
                if (facing > bestFacing) { bestFacing = facing; best = theirs; }
            }
            return best;
        }

        /// <summary>
        /// The docking port of a craft docked inside another (loaded) vessel, found by the craft's own name as KSP keeps it
        /// on its port (for undocking it as it was); and the vessel it's docked in. Null when there's none.
        /// </summary>
        public static ModuleDockingNode FindDockedCraftPort(string name, out Vessel stack)
        {
            stack = null;
            foreach (var v in FlightGlobals.VesselsLoaded)
            foreach (var node in v.FindPartModulesImplementing<ModuleDockingNode>())
            {
                if (node.otherNode == null || node.vesselInfo == null || node.vesselInfo.name != name) continue;
                stack = v;
                return node;
            }
            return null;
        }

        /// <summary>The loaded ship this part is in now; null when it isn't loaded.</summary>
        public static Vessel VesselOfPart(uint partPersistentId)
        {
            foreach (var v in FlightGlobals.VesselsLoaded)
            foreach (var p in v.parts)
                if (p.persistentId == partPersistentId) return v;
            return null;
        }

        /// <summary>The ship's free port that fits the station's port; null when none does. The ship must be loaded.</summary>
        public static ModuleDockingNode PortFor(Vessel ship, ModuleDockingNode stationPort) =>
            !IsFree(stationPort) ? null : ship.FindPartModulesImplementing<ModuleDockingNode>().Where(IsFree).FirstOrDefault(m => Fits(m, stationPort));

        /// <summary>The docking port on this (loaded) ship whose part has this id; null if it's gone or taken.</summary>
        public static ModuleDockingNode FindPort(Vessel v, uint partPersistentId)
        {
            if (v == null || !v.loaded) return null;
            foreach (var p in v.parts)
                if (p.persistentId == partPersistentId) return p.FindModuleImplementing<ModuleDockingNode>();
            return null;
        }

        /// <summary>
        /// Lets physics run on this ship from further away in orbit: KSP draws a ship from 2.25 km but only turns its
        /// physics on within 200 m (packs it again past 350 m), so a hauler coming in would be frozen until it was nearly
        /// there. Only the pack/unpack distances change (changing those and the load distances in one physics tick isn't
        /// safe), kept inside the load distance; a copy, so other ships keep KSP's defaults.
        /// </summary>
        public static void RaisePhysicsRange(Vessel v, float unpackM)
        {
            var orbit = v.vesselRanges.orbit;
            unpackM = Math.Min(unpackM, orbit.load - 50);
            if (orbit.unpack >= unpackM) return;
            var ranges = new VesselRanges(v.vesselRanges);
            ranges.orbit.unpack = unpackM;
            ranges.orbit.pack = Math.Min(unpackM + 150, ranges.orbit.unload - 50);
            v.vesselRanges = ranges;
        }

        /// <summary>
        /// Turns and moves the (loaded, unpacked) ship so its port faces the station's port, a hand's width in front of
        /// it, at the station's speed, so the ports' magnets pull it in, as Routine Mission Manager places its arrivals.
        /// </summary>
        public static void PlaceForDocking(Vessel ship, ModuleDockingNode shipPort, Vessel station, ModuleDockingNode stationPort)
        {
            var theirs = stationPort.nodeTransform;
            var mine = shipPort.nodeTransform;

            // Point the ship's port straight back at the station's, its "up" along the station port's.
            var want = Quaternion.LookRotation(-theirs.forward, theirs.up);
            var have = Quaternion.LookRotation(mine.forward, mine.up);
            ship.SetRotation(want * Quaternion.Inverse(have) * ship.transform.rotation);

            // Inside the magnets' reach but not touching.
            var gap = Math.Max(0.1f, 0.5f * Math.Min(shipPort.acquireRange, stationPort.acquireRange));
            var portSpot = theirs.position + theirs.forward * gap;
            ship.SetPosition(ship.transform.position + (portSpot - mine.position));

            ship.ChangeWorldVelocity(station.obt_velocity - ship.obt_velocity);
            foreach (var p in ship.parts)
                if (p.Rigidbody != null) p.Rigidbody.angularVelocity = Vector3.zero;
        }

        /// <summary>
        /// Whether the docking port with this part id has docked. Looked for in every loaded ship: KSP keeps one of the
        /// two ships when they join (usually the station, by type, but not always), so either may be gone.
        /// </summary>
        public static bool PortDocked(uint partPersistentId)
        {
            foreach (var v in FlightGlobals.Vessels)
            {
                if (v == null || !v.loaded) continue;
                foreach (var p in v.parts)
                {
                    if (p.persistentId != partPersistentId) continue;
                    var node = p.FindModuleImplementing<ModuleDockingNode>();
                    var state = node == null ? "" : node.fsm != null ? node.fsm.currentStateName : node.state;
                    // Joined: both ports in one vessel now, or KSP says docked. Not just "pointing at the other port": that's
                    // already so while the magnets pull it in, and the engines were "shut down" before there was a dock
                    // (2026-11-06).
                    return node != null && (node.otherNode != null && node.otherNode.vessel == node.vessel || state.StartsWith("Docked"));
                }
            }
            return false;
        }

        // ---------------- Fuel ----------------

        /// <summary>One tank's amount of one resource, on a loaded part or in an unloaded ship's saved parts.</summary>
        private sealed class Tank
        {
            public PartResource Live;
            public ProtoPartResourceSnapshot Saved;
            public double Amount
            {
                get => Live != null ? Live.amount : Saved.amount;
                set { if (Live != null) Live.amount = value; else Saved.amount = value; }
            }
        }

        /// <summary>
        /// The tanks of a ship, by resource. Looked up again every time: KSP rebuilds an unloaded ship's saved parts when
        /// it loads or unloads, so a kept reference can go stale (Background Resource Processing's lesson).
        /// Locked tanks (flow off) are left out, as the engines couldn't draw from them.
        /// </summary>
        private static Dictionary<string, List<Tank>> Tanks(Vessel v)
        {
            var tanks = new Dictionary<string, List<Tank>>();
            void Add(string name, Tank t)
            {
                if (!tanks.TryGetValue(name, out var list)) tanks[name] = list = new List<Tank>();
                list.Add(t);
            }
            if (v.loaded)
            {
                foreach (var p in v.parts)
                    foreach (var r in p.Resources)
                        if (r.flowState) Add(r.resourceName, new Tank { Live = r });
            }
            else
            {
                foreach (var p in v.protoVessel.protoPartSnapshots)
                    foreach (var r in p.resources)
                        if (r.flowState) Add(r.resourceName, new Tank { Saved = r });
            }
            return tanks;
        }

        public static double Amount(Vessel v, string resource) =>
            Tanks(v).TryGetValue(resource, out var list) ? list.Sum(t => t.Amount) : 0;

        /// <summary>The ship's whole mass in tonnes: parts plus everything in them.</summary>
        public static double Mass(Vessel v)
        {
            if (v.loaded) return v.GetTotalMass();
            double mass = 0;
            foreach (var p in v.protoVessel.protoPartSnapshots)
            {
                mass += p.mass;
                foreach (var r in p.resources)
                {
                    var def = PartResourceLibrary.Instance.GetDefinition(r.resourceName);
                    if (def != null) mass += r.amount * def.density;
                }
            }
            return mass;
        }

        /// <summary>The engine a route burns with: its vacuum Isp and what it burns, in what ratio.</summary>
        public sealed class Engine
        {
            public string Part;
            public int Count;
            public double ThrustKn;
            public double IspVacuum;
            public List<KeyValuePair<string, double>> Propellants = new List<KeyValuePair<string, double>>(); // name → ratio (units)

            public override string ToString() =>
                Count + "× " + Part + " (" + IspVacuum.ToString("0") + " s, burns " + string.Join(" + ", Propellants.Select(p => p.Key).ToArray()) + ")";
        }

        /// <summary>
        /// The engine the ship burns with: of its engine parts that have fuel on board (air-breathers left out), the kind
        /// with the most thrust in all. Null when it has none. One kind only, so a mixed stack burns at that engine's Isp.
        /// </summary>
        public static Engine MainEngine(Vessel v)
        {
            var tanks = Tanks(v);
            var parts = v.loaded
                ? v.parts.Select(p => new KeyValuePair<string, Part>(p.partInfo.name, p))
                : v.protoVessel.protoPartSnapshots.Where(p => p.partInfo != null).Select(p => new KeyValuePair<string, Part>(p.partInfo.name, p.partInfo.partPrefab));
            return PickEngine(parts, name => tanks.TryGetValue(name, out var list) ? list.Sum(t => t.Amount) : 0);
        }

        /// <summary>The main engine kind among these parts (by name), counting only fuel the amounts say is there.</summary>
        private static Engine PickEngine(IEnumerable<KeyValuePair<string, Part>> parts, Func<string, double> amount)
        {
            var kinds = new Dictionary<string, Engine>();
            foreach (var kv in parts)
            {
                if (kv.Value == null) continue;
                foreach (var e in kv.Value.FindModulesImplementing<ModuleEngines>())
                {
                    var burned = e.propellants.Where(p => !p.ignoreForIsp).ToList();
                    if (burned.Count == 0 || burned.Any(p => p.name == "IntakeAir" || p.name == "IntakeAtm")) continue;
                    if (burned.Any(p => amount(p.name) <= 0)) continue;
                    var key = kv.Key + "/" + e.engineID;
                    if (!kinds.TryGetValue(key, out var kind))
                    {
                        kinds[key] = kind = new Engine
                        {
                            Part = kv.Value.partInfo != null ? kv.Value.partInfo.title : kv.Key,
                            IspVacuum = e.atmosphereCurve.Evaluate(0f),
                            Propellants = burned.Select(p => new KeyValuePair<string, double>(p.name, p.ratio)).ToList(),
                        };
                    }
                    kind.Count++;
                    kind.ThrustKn += e.maxThrust;
                }
            }
            return kinds.Values.Where(k => k.IspVacuum > 0).OrderByDescending(k => k.ThrustKn).FirstOrDefault();
        }

        /// <summary>
        /// The Δv the ship can still make with its main engine kind, in vacuum, from the fuel it has: as many full sets of
        /// the engine's propellants (in its ratio) as the scarcest one allows, burnt off its whole mass (rocket equation).
        /// Zero with no engine or fuel. docs/MATH.md §11.2.
        /// </summary>
        public static double DeltaVLeft(Vessel v, out Engine engine)
        {
            engine = MainEngine(v);
            if (engine == null) return 0;
            var tanks = Tanks(v);
            return DeltaVLeft(engine, name => tanks.TryGetValue(name, out var list) ? list.Sum(t => t.Amount) : 0, Mass(v));
        }

        /// <summary>The same for a craft docked in a loaded stack, counting only its own parts (see <see cref="DockedCraftParts"/>).</summary>
        public static double DeltaVLeft(IList<Part> parts, out Engine engine)
        {
            var amounts = new Dictionary<string, double>();
            double mass = 0;
            foreach (var p in parts)
            {
                mass += p.mass + p.GetResourceMass();
                foreach (var r in p.Resources)
                    if (r.flowState) amounts[r.resourceName] = (amounts.TryGetValue(r.resourceName, out var a) ? a : 0) + r.amount;
            }
            double Amount(string name) => amounts.TryGetValue(name, out var a) ? a : 0;
            engine = PickEngine(parts.Where(p => p.partInfo != null).Select(p => new KeyValuePair<string, Part>(p.partInfo.name, p)), Amount);
            return engine == null ? 0 : DeltaVLeft(engine, Amount, mass);
        }

        private static double DeltaVLeft(Engine engine, Func<string, double> amount, double mass)
        {
            var sets = engine.Propellants.Min(p => p.Value > 0 ? amount(p.Key) / p.Value : double.MaxValue);
            var perSet = engine.Propellants.Sum(p => p.Value * (PartResourceLibrary.Instance.GetDefinition(p.Key)?.density ?? 0));
            var fuel = Math.Min(sets * perSet, mass * 0.999);
            return fuel <= 0 ? 0 : engine.IspVacuum * G0 * Math.Log(mass / (mass - fuel));
        }

        /// <summary>
        /// A docked craft's own parts, from its docking port: KSP hangs a docked craft's parts off its port, under the
        /// station's port (or the other way round, when the craft kept the root), so it's that branch, or everything but
        /// the station's branch.
        /// </summary>
        public static List<Part> DockedCraftParts(ModuleDockingNode craftPort)
        {
            var other = craftPort.otherNode != null ? craftPort.otherNode.part : null;
            var craft = craftPort.part;
            var list = new List<Part>();
            void Branch(Part p, List<Part> into) { into.Add(p); foreach (var c in p.children) Branch(c, into); }
            if (other != null && craft.parent == other) Branch(craft, list);
            else if (other != null && other.parent == craft)
            {
                var station = new List<Part>();
                Branch(other, station);
                var not = new HashSet<Part>(station);
                list.AddRange(craft.vessel.parts.Where(p => !not.Contains(p)));
            }
            else Branch(craft, list);
            return list;
        }

        /// <summary>
        /// Burns the fuel for <paramref name="deltaV"/> m/s with <paramref name="engine"/> (rocket equation), taking it
        /// from every tank in proportion to what each holds. When there isn't enough, burns what there is. Returns the
        /// Δv actually got and says what it used.
        /// </summary>
        public static double Burn(Vessel v, Engine engine, double deltaV, out string used)
        {
            used = "";
            if (deltaV <= 0) return 0;
            var tanks = Tanks(v);
            var m0 = Mass(v);
            var ve = engine.IspVacuum * G0;
            var fuelTonnes = m0 * (1 - Math.Exp(-deltaV / ve));

            // Split the fuel mass between the propellants by the engine's ratio (in units) times each one's density.
            var defs = engine.Propellants.Select(p => PartResourceLibrary.Instance.GetDefinition(p.Key)).ToList();
            var perRatio = engine.Propellants.Select((p, i) => p.Value * defs[i].density).Sum();
            var want = engine.Propellants.Select(p => fuelTonnes / perRatio * p.Value).ToList(); // units of each
            var share = 1.0;
            for (var i = 0; i < want.Count; i++)
            {
                var have = tanks.TryGetValue(engine.Propellants[i].Key, out var list) ? list.Sum(t => t.Amount) : 0;
                if (want[i] > have) share = Math.Min(share, want[i] <= 0 ? 0 : have / want[i]);
            }

            var parts = new List<string>();
            for (var i = 0; i < want.Count; i++)
            {
                var take = want[i] * share;
                var list = tanks[engine.Propellants[i].Key];
                var have = list.Sum(t => t.Amount);
                foreach (var t in list)
                    t.Amount = have <= 0 ? 0 : Math.Max(0, t.Amount - take * t.Amount / have);
                parts.Add(take.ToString("0.#") + " " + engine.Propellants[i].Key);
            }
            used = string.Join(", ", parts.ToArray());
            var burnt = fuelTonnes * share;
            return share >= 1 ? deltaV : ve * Math.Log(m0 / (m0 - burnt));
        }
    }
}
