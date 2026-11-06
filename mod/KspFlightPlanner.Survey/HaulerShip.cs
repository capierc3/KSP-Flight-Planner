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
            var o = Copy(ship.orbitDriver.orbit);
            var t = fromUt;
            foreach (var b in burns)
            {
                if (b.Ut > meetUt) break;
                o = Coast(o, t, b.Ut);
                var pos = o.getRelativePositionAtUT(b.Ut);
                var vel = o.getOrbitalVelocityAtUT(b.Ut);
                Axes(pos, vel, out var prograde, out var normal, out var radial);
                var after = new Orbit();
                after.UpdateFromStateVectors(pos, vel + prograde * b.Prograde + normal * b.Normal + radial * b.Radial, o.referenceBody, b.Ut);
                o = after;
                t = Math.Max(t, b.Ut);
            }
            o = Coast(o, t, meetUt);

            var to = target.orbitDriver.orbit;
            if (o.referenceBody != to.referenceBody)
                return new Meeting { Problem = "ends up round " + o.referenceBody.bodyName + ", not " + to.referenceBody.bodyName };
            return new Meeting
            {
                GapM = (o.getRelativePositionAtUT(meetUt) - to.getRelativePositionAtUT(meetUt)).magnitude,
                SpeedMs = (o.getOrbitalVelocityAtUT(meetUt) - to.getOrbitalVelocityAtUT(meetUt)).magnitude,
            };
        }

        private static Orbit Copy(Orbit o) =>
            new Orbit(o.inclination, o.eccentricity, o.semiMajorAxis, o.LAN, o.argumentOfPeriapsis, o.meanAnomalyAtEpoch, o.epoch, o.referenceBody);

        /// <summary>The patch of <paramref name="o"/>'s path the ship is on at <paramref name="toUt"/>, following SOI changes.</summary>
        private static Orbit Coast(Orbit o, double fromUt, double toUt)
        {
            var pars = new PatchedConics.SolverParameters();
            for (var i = 0; i < 8 && toUt > fromUt; i++)
            {
                var next = new Orbit();
                o.StartUT = fromUt;
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

        /// <summary>A docking port free to dock: not docked, not shielded shut.</summary>
        private static bool IsFree(ModuleDockingNode n) =>
            n != null && n.nodeTransform != null && n.otherNode == null &&
            (n.fsm != null ? n.fsm.currentStateName : n.state) == "Ready";

        private static bool Fits(ModuleDockingNode a, ModuleDockingNode b) =>
            a.nodeTypes != null && b.nodeTypes != null && a.nodeTypes.Count > 0 ? a.nodeTypes.Overlaps(b.nodeTypes) : a.nodeType == b.nodeType;

        /// <summary>
        /// The pair of free ports to dock with: on the station, the free port nearest the ship that one of the ship's free
        /// ports fits. Both ships must be loaded. False when there's no such pair.
        /// </summary>
        public static bool PickPorts(Vessel ship, Vessel station, out ModuleDockingNode shipPort, out ModuleDockingNode stationPort)
        {
            shipPort = null;
            stationPort = null;
            var mine = ship.FindPartModulesImplementing<ModuleDockingNode>().Where(IsFree).ToList();
            var best = double.MaxValue;
            foreach (var theirs in station.FindPartModulesImplementing<ModuleDockingNode>().Where(IsFree))
            {
                var match = mine.FirstOrDefault(m => Fits(m, theirs));
                if (match == null) continue;
                var d = (theirs.nodeTransform.position - ship.transform.position).sqrMagnitude;
                if (d < best) { best = d; shipPort = match; stationPort = theirs; }
            }
            return shipPort != null;
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
                    return node != null && (node.otherNode != null || state.StartsWith("Docked") || state.StartsWith("PreAttached"));
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
            var kinds = new Dictionary<string, Engine>();
            foreach (var kv in parts)
            {
                if (kv.Value == null) continue;
                foreach (var e in kv.Value.FindModulesImplementing<ModuleEngines>())
                {
                    var burned = e.propellants.Where(p => !p.ignoreForIsp).ToList();
                    if (burned.Count == 0 || burned.Any(p => p.name == "IntakeAir" || p.name == "IntakeAtm")) continue;
                    if (burned.Any(p => !tanks.ContainsKey(p.name) || tanks[p.name].Sum(t => t.Amount) <= 0)) continue;
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
