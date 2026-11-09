using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Traffic
{
    /// <summary>
    /// The physics route flyer: flies a route craft for real while it's loaded with physics running near the player, by
    /// writing its controls every physics tick (the input hook kRPC uses to fly ships that aren't the one being flown).
    /// It faces the next burn, and flies the burn itself with the ship's engines: started half its length early, steered
    /// along the Δv still to go, cut off when that's gone (docs/MATH.md §11.4–11.5). Arrival and docking come next. While
    /// it flies, the rails flyer waits; the course check runs once the ship is back on rails, and absorbs the small error
    /// a real burn leaves.
    /// </summary>
    internal static class PhysicsFlyer
    {
        private static readonly Dictionary<uint, Pilot> Pilots = new Dictionary<uint, Pilot>();

        /// <summary>How long before a burn starts time warp is stopped for a route craft near the player (game s), to turn and settle.</summary>
        internal const double TurnLeadS = 30;

        /// <summary>What the physics flyer is doing with this route's ship now; null when it isn't flying it.</summary>
        internal static string Status(Route route) => Pilots.TryGetValue(route.VesselId, out var p) ? p.Status : null;

        /// <summary>The physics flyer has the next burn in hand (the rails flyer leaves it alone).</summary>
        internal static bool FliesBurns(Route route) => Flies(route);

        /// <summary>The physics flyer is flying this route's ship and hasn't given up on it.</summary>
        internal static bool Flies(Route route) => Pilots.TryGetValue(route.VesselId, out var p) && !p.GaveUp;

        /// <summary>
        /// Physics went off mid-burn (the player left, or warped): the rest of the burn goes back to the rails flyer to
        /// make (see <see cref="Pilot.HandBackBurn"/>). True when there was one.
        /// </summary>
        internal static bool HandBackCutBurn(Route route) => Pilots.TryGetValue(route.VesselId, out var p) && p.HandBackBurn();

        /// <summary>A station's keep-out zone reaches this far from its centre at least (m), farther for big stations and ships.</summary>
        internal const double ZoneM = 200;

        /// <summary>Route craft waiting their turn in the arrival lane wait this far apart (m), the first at the zone's edge.</summary>
        internal const double QueueGapM = 60;

        /// <summary>The keep-out zone's radius round this station for this ship (m): 200 m, or both sizes plus 50 m if bigger.</summary>
        internal static double ZoneRadius(Vessel station, Vessel ship) => Math.Max(ZoneM, Ships.Size(station) + Ships.Size(ship) + 50);

        /// <summary>Straight up from the station, away from the body it's round (world).</summary>
        internal static Vector3d Up(Vessel station) => (station.CoMD - station.mainBody.position).normalized;

        /// <summary>The station directs its traffic (a pilot aboard: <see cref="CrewRules.HasTrafficControl"/>).</summary>
        internal static bool Controlled(Vessel station) => station != null && CrewRules.HasTrafficControl(station, out _);

        /// <summary>
        /// Its place in the arrival lane at a controlled station: how many route craft coming in to it started before it
        /// (0 = first). Each waits <see cref="QueueGapM"/> further down the lane than the one ahead.
        /// </summary>
        internal static int QueuePlace(Route me, Vessel station)
        {
            var place = 0;
            if (TrafficScenario.Instance == null || station == null) return 0;
            foreach (var r in TrafficScenario.Instance.Routes)
            {
                if (r == me || r.TargetId != station.persistentId || r.State != RouteState.Approaching && r.State != RouteState.Arrived) continue;
                var ship = Ships.Find(r.VesselId);
                if (ship == null || !ship.loaded || !Pilots.TryGetValue(r.VesselId, out var p) || p.GaveUp) continue;
                if (r.QueueSince < me.QueueSince || r.QueueSince == me.QueueSince && r.VesselId < me.VesselId) place++;
            }
            return place;
        }

        /// <summary>
        /// Station traffic, in physics (docs/MATH.md §11.11): the craft <paramref name="me"/> gives way to at this station,
        /// as "name is doing what"; null when the way is clear (its clearance into the keep-out zone). One backing out of
        /// its ports (and climbing out the departure lane) always has the way; then one docking at it; then one that
        /// started coming in to it first (first come, first served). With <paramref name="leavingOnly"/> (it's docking
        /// already), only one leaving. A station without traffic control orders nobody: always null.
        /// </summary>
        internal static string GiveWayTo(Route me, Vessel station, bool leavingOnly = false)
        {
            if (TrafficScenario.Instance == null || station == null || !Controlled(station)) return null;
            foreach (var r in TrafficScenario.Instance.Routes)
            {
                if (r == me) continue;
                var ship = Ships.Find(r.VesselId);
                if (ship == null || !ship.loaded) continue;
                if (r.BackingOff && Ships.VesselOfPart(r.BackOffPortId) == station) return r.VesselName + " is leaving";
                if (leavingOnly || r.TargetId != station.persistentId) continue;
                if (!Pilots.TryGetValue(r.VesselId, out var p) || p.GaveUp) continue;
                if (r.State == RouteState.Arrived && p.Docking) return r.VesselName + " is docking";
                if ((r.State == RouteState.Approaching || r.State == RouteState.Arrived) &&
                    (r.QueueSince < me.QueueSince || r.QueueSince == me.QueueSince && r.VesselId < me.VesselId))
                    return r.VesselName + " is coming in first";
            }
            return null;
        }

        /// <summary>A route craft's physics comes on this far from the ship being flown (m), not KSP's 200 m: see Ships.RaisePhysicsRange.</summary>
        internal const float PhysicsRangeM = 2200;

        /// <summary>Takes up or lets go of the route's ship, every frame in flight.</summary>
        internal static void Tick(Route route)
        {
            var ship = Ships.Find(route.VesselId);
            // Switched off to fly it by hand: back on once the player has left it and it's on rails again.
            if (!route.Autopilot && ship != null && ship != FlightGlobals.ActiveVessel && !CourseCheck.OffRails(ship))
            {
                route.Autopilot = true;
                TrafficScenario.Note(route, "Autopilot back on (you've left it).", false);
            }
            // Docked (the ship may have just merged into the station, its own vessel gone): done.
            if (route.State == RouteState.Arrived && route.DockingPortId != 0 && Ships.PortDocked(route.DockingPortId))
            {
                TrafficScenario.Note(route, "Docked at " + route.TargetName + "." + ShutDown(route.DockingPortId), true);
                TrafficScenario.Docked(route);
            }
            // Backing off near the player: no time warp until it's clear (warp would pack it 30 m from the station, on a path
            // that can swing back through it).
            if (route.BackingOff && ship != null && ship.loaded && TimeWarp.fetch != null && TimeWarp.CurrentRateIndex > 0 && TimeWarp.WarpMode == TimeWarp.Modes.HIGH)
            {
                CourseCheck.StopWarp();
                ScreenMessages.PostScreenMessage(route.VesselName + " is clearing the station: no time warp till it's clear", 3f, ScreenMessageStyle.UPPER_CENTER);
            }
            // Unloaded (the player left): nothing to bump into, so backing off ends here, checked as it would at its end.
            if (route.BackingOff && ship != null && !ship.loaded)
            {
                route.BackingOff = false;
                if (route.State == RouteState.Flying) CourseCheck.Check(route, ship, Planetarium.GetUniversalTime(), "After backing off", expected: true);
            }
            var docking = route.State == RouteState.Arrived && ship != null && ship.loaded && HasRcs(ship);
            // Docking near the player: no time warp till it's docked (warp would freeze it mid-move, and it drifts).
            if (docking && route.Autopilot && route.DockingPortId != 0 && !(Pilots.TryGetValue(route.VesselId, out var dp) && dp.GaveUp) &&
                TimeWarp.fetch != null && TimeWarp.CurrentRateIndex > 0 && TimeWarp.WarpMode == TimeWarp.Modes.HIGH)
            {
                CourseCheck.StopWarp();
                ScreenMessages.PostScreenMessage(route.VesselName + " is docking: no time warp till it's docked", 3f, ScreenMessageStyle.UPPER_CENTER);
            }
            var active = route.State == RouteState.Flying || route.State == RouteState.Approaching || docking;
            if (active && ship != null) Ships.RaisePhysicsRange(ship, PhysicsRangeM);
            if (route.State == RouteState.Flying && route.Autopilot && ship != null && ship.loaded) StopWarpForBurn(route, ship);

            var wanted = route.Autopilot && active && CourseCheck.OffRails(ship);
            // The crew rules: a ship the autopilot may not take is left alone, the reason on its log once.
            if (wanted && !CrewRules.MayTakeOver(route, ship, out var notAllowed))
            {
                wanted = false;
                if (route.NotAllowedNote != notAllowed) TrafficScenario.Note(route, "The autopilot can't take it: " + notAllowed, true);
                route.NotAllowedNote = notAllowed;
            }
            Pilots.TryGetValue(route.VesselId, out var pilot);
            if (pilot != null && (!wanted || pilot.Vessel != ship))
            {
                pilot.Release();
                Pilots.Remove(route.VesselId);
                pilot = null;
            }
            if (wanted && pilot == null)
            {
                pilot = new Pilot(route, ship);
                Pilots[route.VesselId] = pilot;
                TrafficScenario.Note(route, "Autopilot has it (physics on).", false);
            }
            pilot?.Plan();
        }

        /// <summary>
        /// A route craft loaded near the player but packed by time warp would have its burn made in one jump; stopping warp a
        /// little before the burn starts unpacks it, so the autopilot can turn it and fly the burn for real. Any warp: one
        /// the player started to reach another ship hours away too, KSP's "Warp to" included (it's cancelled, or it raises
        /// the rate again), and from then until the burn is done, not only until its node.
        /// </summary>
        private static void StopWarpForBurn(Route route, Vessel ship)
        {
            if (TimeWarp.fetch == null || TimeWarp.CurrentRateIndex == 0) return;
            var burn = route.Burns.FirstOrDefault(b => !b.Done);
            if (burn == null) return;
            var now = Planetarium.GetUniversalTime();
            var start = burn.Ut - StartLead(ship, burn.DeltaV);
            // A frame at this rate covers about rate × 0.1 s of game time: stop early enough not to jump past.
            if (now < start - TurnLeadS - TimeWarp.CurrentRate * 0.1) return;
            // Packed by the warp its pilot is let go, so that isn't asked; one that gave up has it made on rails.
            if (now > burn.Ut + 600 || Pilots.TryGetValue(route.VesselId, out var p) && p.GaveUp) return;
            CourseCheck.StopWarp();
            var when = start > now ? "starts in " + Clock(start - now) : "is under way";
            TrafficScenario.Note(route, "Time warp stopped: burn " + (route.Burns.IndexOf(burn) + 1) + " " + when + ".", true);
            ScreenMessages.PostScreenMessage(route.VesselName + ": time warp stopped, burn " + (route.Burns.IndexOf(burn) + 1) + " " + when, 4f, ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>
        /// Docked: the craft's engines are shut down and its RCS thrusters switched off (its own parts only, found from its
        /// docking port), so nothing on it can fire by accident at the station. Returns what it did, for the log.
        /// </summary>
        internal static string ShutDown(uint portPartId)
        {
            var stack = Ships.VesselOfPart(portPartId);
            var port = Ships.FindPort(stack, portPartId);
            if (port == null || port.otherNode == null) return "";
            int engines = 0, thrusters = 0;
            foreach (var part in Ships.DockedCraftParts(port))
            {
                foreach (var e in part.FindModulesImplementing<ModuleEngines>())
                    if (e.EngineIgnited)
                    {
                        e.Shutdown();
                        engines++;
                    }
                foreach (var rcs in part.FindModulesImplementing<ModuleRCS>())
                    if (rcs.rcsEnabled)
                    {
                        rcs.rcsEnabled = false;
                        thrusters++;
                    }
            }
            return engines + thrusters == 0 ? "" : " Shut down " + engines + " engine" + (engines == 1 ? "" : "s") + " and switched off " + thrusters + " RCS block" + (thrusters == 1 ? "" : "s") + ".";
        }

        /// <summary>Lets go of every ship (leaving the scene).</summary>
        internal static void ReleaseAll()
        {
            foreach (var p in Pilots.Values) p.Release();
            Pilots.Clear();
        }

        /// <summary>
        /// How long the burn takes at full throttle with the engines that are lit (or the main engine kind, if none is):
        /// t = m₀·vₑ/F · (1 − e^(−Δv/vₑ)). Zero when there's no thrust to be had.
        /// </summary>
        internal static double BurnSeconds(Vessel v, double deltaV) => Math.Max(MinBurnS, FullBurnSeconds(v, deltaV));

        /// <summary>
        /// How long before the node a burn starts: the time to make the first half of its Δv, not half its time. The ship
        /// gets lighter as it burns, so the first half of the Δv takes longer than the second:
        /// t_half = t · (1 − e^(−Δv/2vₑ)) / (1 − e^(−Δv/vₑ)). Zero when it can't burn.
        /// </summary>
        internal static double StartLead(Vessel v, double deltaV)
        {
            var length = BurnSeconds(v, deltaV);
            if (length <= 0 || deltaV <= 0) return 0;
            var isp = 0.0;
            if (LitThrust(v, out var lit, fullLimiter: true) > 0) isp = lit;
            else if (Ships.MainEngine(v) is Ships.Engine main) isp = main.IspVacuum;
            if (isp <= 0) return length / 2;
            var ve = isp * Ships.G0;
            return length * (1 - Math.Exp(-deltaV / (2 * ve))) / (1 - Math.Exp(-deltaV / ve));
        }

        /// <summary>The shortest a burn is made to last (s): the thrust limiter is turned down for smaller ones.</summary>
        internal const double MinBurnS = 2;

        /// <summary>
        /// How long the burn takes with the engines at full thrust, thrust limiters at 100% (the autopilot sets them for
        /// each burn, whatever the player left them at): the lit engines, or the main engine kind if none is lit. Zero
        /// when there's no thrust to be had.
        /// </summary>
        private static double FullBurnSeconds(Vessel v, double deltaV)
        {
            var thrust = LitThrust(v, out var isp, fullLimiter: true);
            if (thrust <= 0)
            {
                var engine = Ships.MainEngine(v);
                if (engine == null) return 0;
                thrust = engine.ThrustKn;
                isp = engine.IspVacuum;
            }
            var ve = isp * Ships.G0;
            return Ships.Mass(v) * ve / thrust * (1 - Math.Exp(-deltaV / ve));
        }

        /// <summary>The lit engines' full thrust (kN, thrust limiters applied) and their thrust-weighted vacuum Isp.</summary>
        private static double LitThrust(Vessel v, out double isp, bool fullLimiter = false)
        {
            double thrust = 0, flow = 0;
            foreach (var part in v.parts)
            foreach (var e in part.FindModulesImplementing<ModuleEngines>())
            {
                if (!e.EngineIgnited || e.flameout) continue;
                var f = e.maxThrust * (fullLimiter ? 1.0 : e.thrustPercentage / 100.0);
                var i = e.atmosphereCurve.Evaluate(0f);
                if (f <= 0 || i <= 0) continue;
                thrust += f;
                flow += f / i;
            }
            isp = flow > 0 ? thrust / flow : 0;
            return thrust;
        }

        /// <summary>It has RCS thrusters (so it can fly a docking; without them the jump-to-the-port stand-in docks it).</summary>
        internal static bool HasRcs(Vessel v) => v.FindPartModulesImplementing<ModuleRCS>().Count > 0;

        /// <summary>
        /// The push its RCS can give on one axis, roughly (m/s²): all thrusters' power over the mass, shared out over the
        /// six directions. Only the scale of the translation controller; it chases the velocity it wants either way.
        /// </summary>
        private static double RcsAccel(Vessel v)
        {
            double power = 0;
            foreach (var rcs in v.FindPartModulesImplementing<ModuleRCS>())
                if (rcs.rcsEnabled) power += rcs.thrusterPower * Math.Max(1, rcs.thrusterTransforms.Count);
            return power / 6 / Math.Max(1e-3, Ships.Mass(v));
        }

        /// <summary>
        /// The Δv its RCS can give from the monopropellant on board (m/s): vacuum Isp of its thrusters (about 240 s) and the
        /// rocket equation. Used to decide whether a small burn can be made on RCS.
        /// </summary>
        internal static double RcsDeltaV(Vessel v)
        {
            var thrusters = v.FindPartModulesImplementing<ModuleRCS>();
            if (thrusters.Count == 0) return 0;
            var isp = thrusters.Max(t => t.atmosphereCurve.Evaluate(0f));
            double mono = 0;
            foreach (var p in v.parts)
                foreach (var r in p.Resources)
                    if (r.resourceName == "MonoPropellant" && r.flowState) mono += r.amount * r.info.density;
            var mass = Ships.Mass(v);
            return mono <= 0 || mono >= mass ? 0 : isp * Ships.G0 * Math.Log(mass / (mass - mono));
        }

        /// <summary>
        /// A burn this small (m/s) is made on RCS when the monopropellant gives at least four times it: no turning, fine
        /// control. Bigger ones go on the main engine: RCS's Isp is low and its tanks small (a 4.2 m/s correction on RCS took
        /// about 12 units of 150, 2026-11-06).
        /// </summary>
        internal const double RcsBurnMaxMs = 1;

        /// <summary>One ship being flown: what it aims for (set each frame), and the controls written each physics tick.</summary>
        private sealed class Pilot
        {
            /// <summary>A burn is done when less than this is left (m/s).</summary>
            private const double DoneMs = 0.05;

            /// <summary>The engines only fire with the ship pointed within this of the Δv still to go.</summary>
            private const float FireWithinDeg = 5f;

            internal readonly Vessel Vessel;
            private readonly Route _route;
            private readonly Attitude _attitude = new Attitude();
            private Vector3d _aim; // world direction the engines should push; zero = just hold still
            private float _throttle;
            internal string Status = "";
            internal bool GaveUp;

            /// <summary>Docking has started (it's moving to the port, not waiting its turn).</summary>
            internal bool Docking => _dockSince >= 0;

            // The burn being flown: the orbit it should leave the ship on, and how it's going.
            private RouteBurn _burn, _checkedFor, _leavesFor;
            private double _leavesUt = double.NaN;

            /// <summary>Facing a burn starts this long before it (game s); till then it just holds still.</summary>
            private const double FaceWithinS = 600;

            // A small burn made on RCS: the Δv still to go (world), pushed along without turning.
            private bool _rcsBurn;
            private Vector3d _rcsDv;
            private const double RcsBurnGiveUpS = 120;

            /// <summary>This burn is small enough for RCS, and the monopropellant gives at least four times it.</summary>
            private bool UseRcsFor(RouteBurn burn) =>
                burn.DeltaV < RcsBurnMaxMs && HasRcs(Vessel) && RcsDeltaV(Vessel) > 4 * burn.DeltaV;
            private Orbit _goal;
            private double _startUt, _startDv, _bestLeft = double.MaxValue;
            private int _number;
            private double _stalledSince = -1;

            internal Pilot(Route route, Vessel vessel)
            {
                _route = route;
                Vessel = vessel;
                Vessel.OnFlyByWire += Fly;
                _rcsWas = Vessel.ActionGroups[KSPActionGroup.RCS];
                // Stock SAS on the ship being flown would fight the autopilot.
                if (Vessel == FlightGlobals.ActiveVessel) Vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            }

            // The player's RCS switch when the autopilot took the ship: the autopilot turns RCS on only while it's moving
            // the ship sideways (a ship's RCS rarely pushes through its centre of mass, so holding a heading on RCS would
            // nudge it off course over a long wait), and puts it back when it lets go.
            private readonly bool _rcsWas;

            internal void Release()
            {
                if (Vessel != null) Vessel.OnFlyByWire -= Fly;
                RestoreLimiters();
                if (Vessel != null && Vessel.loaded)
                {
                    Vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, _rcsWas);
                    // Let go with SAS on, so a ship the autopilot stops flying holds still instead of tumbling.
                    Vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
                }
                if (_burn != null && !HandBackBurn())
                    TrafficScenario.Note(_route, "Burn " + _number + " cut short (physics went off): the course check takes it from here.", false);
                _burn = null;
            }

            /// <summary>Every frame: faces the next burn, starts it on time, and follows it through.</summary>
            internal void Plan()
            {
                var now = Planetarium.GetUniversalTime();
                if (GaveUp)
                {
                    // Hands off: the rails flyer (burns) or the stand-in (docking) does it the old way.
                    _aim = Vector3d.zero;
                    _throttle = 0;
                    _translating = false;
                    _myPort = _theirPort = null;
                    return;
                }
                if (SafetyStop()) return;
                if (_route.State == RouteState.Arrived)
                {
                    Dock();
                    return;
                }
                if (_route.BackingOff && _route.State == RouteState.Flying)
                {
                    BackOff(now);
                    return;
                }
                if (_route.State == RouteState.Approaching)
                {
                    Approach();
                    return;
                }
                if (_burn != null)
                {
                    FollowBurn(now);
                    return;
                }
                var burn = _route.Burns.FirstOrDefault(b => !b.Done);
                // A trip to an orbit: its last burn (the capture) is flown like any other, and with none left it's there.
                if (_route.EndsInOrbit && burn == null)
                {
                    _aim = Vector3d.zero;
                    _throttle = 0;
                    RailsFlyer.ArriveInOrbit(_route, Vessel);
                    return;
                }
                // The match at the meeting is never flown as a burn: centred on the meeting, half of it would come after
                // reaching the target's position, at speed (the crash of 2026-11-05). It brakes in to a stop instead,
                // starting early enough to stop with half the engines' push.
                if (!_route.EndsInOrbit && (burn == null || burn.Ut >= _route.MeetUt - 1))
                {
                    if (TimeToBrake(now)) StartApproach(burn);
                    else
                    {
                        _aim = Vector3d.zero;
                        _throttle = 0;
                        Status = "coasting in: braking to " + _route.TargetName + " starts in " + Clock(_brakeAt - now);
                    }
                    return;
                }
                _number = _route.Burns.IndexOf(burn) + 1;
                var length = BurnSeconds(Vessel, burn.DeltaV);
                if (length <= 0)
                {
                    GiveUp("no engine with fuel to burn with");
                    return;
                }
                var start = burn.Ut - StartLead(Vessel, burn.DeltaV);
                if (now >= start && _attitude.Problem == null)
                {
                    // Off rails since the last check (it has been all along, if the player stayed): is the plan still good?
                    // Real burns leave small errors, and this is where they're caught when the ship never goes back on rails.
                    if (_route.Disturbed && _checkedFor != burn)
                    {
                        _checkedFor = burn;
                        if (!CourseCheck.Check(_route, Vessel, now, "Before burn " + _number)) return;
                    }
                    StartBurn(burn, now);
                    return;
                }
                // Until then: hold still while it's far off; within 10 minutes, face the burn as it will be at its time (a
                // direction fixed in space, so the ship turns once and holds, rather than following the axes round the orbit).
                // A burn on RCS needs no turning at all.
                _throttle = 0;
                var orbit = Vessel.orbit;
                if (_leavesFor != burn)
                {
                    _leavesFor = burn;
                    _leavesUt = Ships.LeavesSphereUt(Vessel);
                }
                var onRcs = UseRcsFor(burn);
                if (burn.Ut - now > FaceWithinS || onRcs || !double.IsNaN(_leavesUt) && burn.Ut > _leavesUt) _aim = Vector3d.zero;
                else
                {
                    Ships.Axes(orbit.getRelativePositionAtUT(burn.Ut), orbit.getOrbitalVelocityAtUT(burn.Ut), out var prograde, out var normal, out var radial);
                    var dv = prograde * burn.Prograde + normal * burn.Normal + radial * burn.Radial;
                    _aim = dv.magnitude > 0.01 ? dv.normalized.xzy : Vector3d.zero; // orbit frame → world, as KSP places ships
                }
                Status = (_aim == Vector3d.zero ? "holding still for burn " : "facing burn ") + _number + " (" + burn.Title + ", " + burn.DeltaV.ToString("0") + " m/s, " +
                         (onRcs ? "on RCS" : length.ToString("0") + " s") + ") in " + Clock(start - now) +
                         (_aim == Vector3d.zero ? "" : ": " + _attitude.ErrorDeg.ToString("0.0") + "° off") + (_attitude.Problem != null ? "; " + _attitude.Problem : "");
                if (_attitude.Problem != null && now > burn.Ut) GiveUp(_attitude.Problem);
            }

            /// <summary>
            /// Starts flying a burn: works out the orbit it should leave the ship on (the same one the rails flyer would give
            /// it), lights the engines if none is lit, and from now on steers along the difference.
            /// </summary>
            private void StartBurn(RouteBurn burn, double now)
            {
                var orbit = Vessel.orbit;
                var pos = orbit.getRelativePositionAtUT(burn.Ut);
                var vel = orbit.getOrbitalVelocityAtUT(burn.Ut);
                Ships.Axes(pos, vel, out var prograde, out var normal, out var radial);
                _goal = new Orbit();
                _goal.UpdateFromStateVectors(pos, vel + prograde * burn.Prograde + normal * burn.Normal + radial * burn.Radial, orbit.referenceBody, burn.Ut);
                _burn = burn;
                burn.Done = true; // the rails flyer must not make it again; if physics goes off midway, the course check catches what's left
                _startUt = now;
                _startDv = Left().magnitude;
                _bestLeft = _startDv;
                _stalledSince = -1;
                _rcsBurn = UseRcsFor(burn);
                if (!_rcsBurn)
                {
                    if (LitThrust(Vessel, out _, fullLimiter: true) <= 0) LightMainEngines();
                    // Thrust limiters: full, or turned down so a small burn still lasts MinBurnS (the player's settings come back after).
                    var full = FullBurnSeconds(Vessel, burn.DeltaV);
                    var percent = full > 0 && full < MinBurnS ? (float)Math.Max(1, 100 * full / MinBurnS) : 100f;
                    SetLimiters(percent);
                    TrafficScenario.Note(_route, "Thrust limiters at " + percent.ToString("0") + "% for burn " + _number + ".", false);
                }
                TrafficScenario.Note(_route, "Burn " + _number + " (" + burn.Title + "): flying it for real" + (_rcsBurn ? " on RCS" : "") + ", " + _startDv.ToString("0.0") + " m/s to go" +
                                            (now > burn.Ut ? ", " + (now - burn.Ut).ToString("0") + " s late" : "") + ".", true);
            }

            /// <summary>
            /// Physics went off mid-burn: what's left of it (the goal orbit's velocity at the burn's time minus the ship's,
            /// as prograde, normal and radial on the orbit it has now) becomes the burn again, not done, and the rails flyer
            /// makes it at once, as it makes any burn. Dropping it left B half way out of the Mun on a path into it, paused
            /// off course (the test of 2026-11-08). False when there's none, or it left the body's sphere of influence.
            /// </summary>
            internal bool HandBackBurn()
            {
                if (_burn == null || _goal == null || Vessel == null || Vessel.orbit.referenceBody != _goal.referenceBody) return false;
                var burn = _burn;
                var left = Left();
                var orbit = Vessel.orbit;
                Ships.Axes(orbit.getRelativePositionAtUT(burn.Ut), orbit.getOrbitalVelocityAtUT(burn.Ut), out var prograde, out var normal, out var radial);
                burn.Prograde = Vector3d.Dot(left, prograde);
                burn.Normal = Vector3d.Dot(left, normal);
                burn.Radial = Vector3d.Dot(left, radial);
                burn.Done = left.magnitude < DoneMs;
                TrafficScenario.Note(_route, "Burn " + _number + " cut short (physics went off) with " + left.magnitude.ToString("0.0") + " of " + _startDv.ToString("0.0") +
                                            " m/s to go: " + (burn.Done ? "close enough." : "the rest is made on rails."), false);
                _burn = null;
                _goal = null;
                _throttle = 0;
                _rcsBurn = false;
                _rcsDv = Vector3d.zero;
                RestoreLimiters();
                // Made on rails like any burn, so no check from here first (it'd find the half-made burn off course).
                _route.Disturbed = false;
                return true;
            }

            /// <summary>The Δv still to go, in the orbit frame: the goal orbit's velocity at the burn's time minus the ship's.</summary>
            private Vector3d Left() => _goal.getOrbitalVelocityAtUT(_burn.Ut) - Vessel.orbit.getOrbitalVelocityAtUT(_burn.Ut);

            private void FollowBurn(double now)
            {
                if (Vessel.orbit.referenceBody != _goal.referenceBody)
                {
                    EndBurn(now, "left " + _goal.referenceBody.bodyName + "'s sphere of influence mid-burn");
                    return;
                }
                var left = Left();
                var size = left.magnitude;
                if (_rcsBurn)
                {
                    // On RCS: no turning; push along what's left, holding still.
                    _aim = Vector3d.zero;
                    _throttle = 0;
                    _rcsDv = left.xzy;
                    if (size < DoneMs || size > _bestLeft + 0.1 && _bestLeft < 1 || now - _startUt > RcsBurnGiveUpS)
                    {
                        EndBurn(now, size < 0.5 ? null : "RCS too weak to finish it");
                        return;
                    }
                    _bestLeft = Math.Min(_bestLeft, size);
                    Status = "burn " + _number + " on RCS: " + size.ToString("0.00") + " of " + _startDv.ToString("0.00") + " m/s to go";
                    return;
                }
                _aim = size > 1e-6 ? left.normalized.xzy : Vector3d.zero;
                var thrust = LitThrust(Vessel, out _);
                var accel = thrust / Math.Max(1e-3, Ships.Mass(Vessel)); // m/s² at full throttle

                // Done: nothing left, or it has gone past and the remainder is growing again (the last bit overshot).
                if (size < DoneMs || size > _bestLeft + 0.1 && _bestLeft < 1)
                {
                    EndBurn(now, null);
                    return;
                }
                _bestLeft = Math.Min(_bestLeft, size);

                // Out of fuel: full throttle and no thrust for 3 s.
                if (thrust <= 0)
                {
                    if (_stalledSince < 0) _stalledSince = now;
                    if (now - _stalledSince > 3)
                    {
                        EndBurn(now, "the engines have nothing left to burn");
                        return;
                    }
                }
                else _stalledSince = -1;

                // Pointed close enough: full throttle, easing off over the last second's worth so it stops on the mark.
                _throttle = _attitude.ErrorDeg > FireWithinDeg || accel <= 0 ? 0 : (float)Math.Max(0.02, Math.Min(1, size / accel));
                Status = "burn " + _number + ": " + size.ToString("0.0") + " of " + _startDv.ToString("0.0") + " m/s to go, throttle " +
                         (_throttle * 100).ToString("0") + "%" + (_attitude.ErrorDeg > FireWithinDeg ? " (turning: " + _attitude.ErrorDeg.ToString("0") + "° off)" : "");
                if (TimeWarp.CurrentRateIndex > 0 && TimeWarp.WarpMode == TimeWarp.Modes.LOW) TimeWarp.SetRate(0, true); // no physics warp mid-burn
            }

            private void EndBurn(double now, string problem)
            {
                _throttle = 0;
                _rcsBurn = false;
                _rcsDv = Vector3d.zero;
                RestoreLimiters();
                var left = _burn != null && _goal != null && Vessel.orbit.referenceBody == _goal.referenceBody ? Left().magnitude : double.NaN;
                TrafficScenario.Note(_route, "Burn " + _number + " flown in " + (now - _startUt).ToString("0") + " s: " +
                                            (double.IsNaN(left) ? "" : (_startDv - left).ToString("0.0") + " of " + _startDv.ToString("0.0") + " m/s, " +
                                                                     left.ToString("0.00") + " m/s off") +
                                            (problem != null ? "; " + problem : "") + ".", problem != null);
                _burn = null;
                _goal = null;
                // Checked straight away, while there's the most time to fix it: a real burn is never quite the instant one.
                CourseCheck.Check(_route, Vessel, now, "After burn " + _number);
            }

            // The thrust limiters the player had on each engine, while a burn has them set (null when not).
            private Dictionary<ModuleEngines, float> _savedLimits;

            /// <summary>Sets every engine's thrust limiter for a burn, keeping the player's settings to put back.</summary>
            private void SetLimiters(float percent)
            {
                if (_savedLimits == null) _savedLimits = new Dictionary<ModuleEngines, float>();
                foreach (var part in Vessel.parts)
                foreach (var e in part.FindModulesImplementing<ModuleEngines>())
                {
                    if (!_savedLimits.ContainsKey(e)) _savedLimits[e] = e.thrustPercentage;
                    e.thrustPercentage = percent;
                }
            }

            /// <summary>Puts the player's thrust limiters back.</summary>
            private void RestoreLimiters()
            {
                // The limiters stay where the autopilot last left them: the setting the player had when it took over (low,
                // for docking at the other station, say) is rarely what's wanted at the end of the trip (the player's call,
                // 2026-11-06).
                _savedLimits = null;
            }

            /// <summary>Coming in, it's done when it's this close to the spot off the port (m) and this slow (m/s).</summary>
            private const double ArrivedM = 20, StillMs = 0.3;

            /// <summary>The braking it plans on: half the engines' push, and no more than this (m/s²), so the braking burn is long enough to watch.</summary>
            private const double MaxBrake = 2.5;

            /// <summary>The engines fire when the velocity it wants is this far off (m/s), and stop once it's within StopFiringMs.</summary>
            private const double FireMs = 0.6, StopFiringMs = 0.2;

            /// <summary>Coming in, it stops at a hold point this far from the station at least (m), farther for big ships.</summary>
            private const double HoldM = 50;

            /// <summary>Close in, the time to go it keeps (s): closing speed about the distance over this.</summary>
            private const double TimeToGoS = 30;

            /// <summary>Close in and far too slow, it speeds up to the distance over this (s), and at least this (m/s).</summary>
            private const double SpeedUpS = 60, SpeedUpMinMs = 1;

            private bool _speedingUp;

            /// <summary>Corrections coming in smaller than this (m/s) are made on RCS; bigger ones on the main engine.</summary>
            private const double RcsFixMs = 1;

            private bool _fixSideways, _braking;

            /// <summary>Coming in slower than this (m/s), the thrust limiters go down so full throttle is <see cref="GentleAccel"/> (m/s²).</summary>
            private const double GentleBelowMs = 60, GentleAccel = 3;

            /// <summary>Slower than this (m/s) and within 300 m, the last of coming in is on RCS.</summary>
            private const double RcsInMs = 2;

            private float _approachLimit = -1;

            /// <summary>Close in, the speed it wants is the distance over this (s): a long, gentle braking burn.</summary>
            private const double EaseS = 10;

            private bool _lit;

            /// <summary>It has said the station it's coming in to has no traffic control.</summary>
            private bool _toldNoControl;

            /// <summary>
            /// Coming in to the port: the speed it wants falls off towards the spot 150 m out so it can always stop there
            /// with the braking it plans on, and the engines make up the difference, pointed along it. While it's only
            /// coasting it faces backwards along its drift, ready to brake: the flip, then the braking burn
            /// (docs/MATH.md §11.6). Stopped there, the route has arrived, and docking takes over.
            /// </summary>
            private void Approach()
            {
                var station = Ships.Find(_route.TargetId);
                if (station == null)
                {
                    GiveUp(_route.TargetName + " is gone");
                    return;
                }
                // Its booked port (§11.14), booked now if it has none yet; with none free, it waits out at the zone's edge.
                if (Time.time >= _route.NextBookTry) PortBooking.Try(_route, Vessel, station);
                var noPort = _route.BookedPortId == 0;
                if (!noPort && _route.ApproachPortId != _route.BookedPortId) _route.ApproachPortId = _route.BookedPortId;

                // It comes in to a hold point near the station (both ships' sizes plus 15 m out, 50 m at least), and stops
                // there; docking then takes it round to the port on RCS. With traffic control, the hold point is in the
                // arrival lane, straight below the station (towards the body); without, on the side it arrives from.
                var centre = station.GetWorldPos3D();
                var controlled = Controlled(station);
                if (_route.ApproachHoldDir == Vector3d.zero) _route.ApproachHoldDir = (Vessel.CoMD - centre).normalized;
                if (controlled) _route.ApproachHoldDir = -Up(station); // follows the station round its orbit
                else if (!_toldNoControl)
                {
                    _toldNoControl = true;
                    CrewRules.HasTrafficControl(station, out var noControl);
                    TrafficScenario.Note(_route, "No traffic control at " + station.vesselName + " (" + noControl + "): coming in on its own.", true);
                }
                // Not cleared in yet (another route craft leaving, docking or ahead of it): it waits at the keep-out zone's edge
                // in the arrival lane, each one in the queue a further 60 m down it.
                var way = GiveWayTo(_route, station) ?? (noPort ? "no free port yet" : null);
                var holdM = way != null
                    ? ZoneRadius(station, Vessel) + QueueGapM * QueuePlace(_route, station)
                    : Math.Max(HoldM, Ships.Size(station) + Ships.Size(Vessel) + 15);
                var hold = centre + _route.ApproachHoldDir * holdM;
                var dHold = (Vessel.CoMD - hold).magnitude;
                // The straight way to the hold point passes the station (arriving from above, the lane is below): steered
                // round it instead, 30° at a time, at the keep-out zone's edge with traffic control (outside the zone till
                // cleared), else just clear of both ships.
                var round = controlled ? ZoneRadius(station, Vessel) : Ships.Size(station) + Ships.Size(Vessel) + ClearM;
                var steerTo = SegmentClear(Vessel.CoMD, hold, centre, Math.Min(round, holdM)) ? hold : AroundStep(Vessel.CoMD, hold, centre, round);
                var r = Vessel.CoMD - steerTo;
                var d = r.magnitude;
                var v = Vessel.obt_velocity - station.obt_velocity;
                var speed = v.magnitude;

                if (!_lit && LitThrust(Vessel, out _) <= 0)
                {
                    LightMainEngines();
                    _lit = true;
                }
                // Slow (under 60 m/s): the thrust limiters down so full throttle is a gentle 3 m/s²; fast: full.
                var mass = Math.Max(1e-3, Ships.Mass(Vessel));
                var fullAccel = LitThrust(Vessel, out _, fullLimiter: true) / mass;
                var percent = speed < GentleBelowMs && fullAccel > 0 ? (float)Math.Max(1, Math.Min(100, 100 * GentleAccel / fullAccel)) : 100f;
                if (Math.Abs(percent - _approachLimit) > 1)
                {
                    SetLimiters(percent);
                    _approachLimit = percent;
                }
                var accel = LitThrust(Vessel, out _) / mass;
                if (AvoidStation(station, centre, v, speed, fullAccel))
                {
                    if (_approachLimit < 99)
                    {
                        SetLimiters(100f);
                        _approachLimit = 100f;
                    }
                    return;
                }
                if (dHold < ArrivedM && speed < StillMs && way != null)
                {
                    _throttle = 0;
                    _aim = Vector3d.zero;
                    _translating = false;
                    Status = (controlled ? "waiting in the arrival lane, " + (Vessel.CoMD - centre).magnitude.ToString("0") + " m below " : "waiting " + (Vessel.CoMD - centre).magnitude.ToString("0") + " m from ") +
                             station.vesselName + ": " + way;
                    return;
                }
                if (dHold < ArrivedM && speed < StillMs)
                {
                    _throttle = 0;
                    _aim = Vector3d.zero;
                    _translating = false;
                    _approachLimit = -1;
                    _route.State = RouteState.Arrived;
                    TrafficScenario.Note(_route, "Holding " + (Vessel.CoMD - centre).magnitude.ToString("0") + " m from " + station.vesselName + " at " +
                                                speed.ToString("0.00") + " m/s: docking.", true);
                    return;
                }

                // Far out: the intercept as KSP's own map marks it. The closest approach to the hold point, from both orbits
                // (twice a second); at checkpoints by the time to it (10, 5, 2 and 1 minute out, and straight away when coming
                // in starts), if it's off by more than 800, 200, 100 or 50 m, one correction solved to put the closest approach
                // on the hold point; coasting between, with time warp allowed till 20 s before the next checkpoint. Close in
                // (the last minute, or when it's time to brake), straight-line steering, which is accurate there.
                var now = Planetarium.GetUniversalTime();
                var offset = (Vector3d)(hold - centre).xzy;
                if (Time.time - _caAt > 0.5f)
                {
                    _caAt = Time.time;
                    _ca = Ships.ClosestApproach(Vessel, Vector3d.zero, station, offset, now, now + Math.Max(600, Math.Min(3600, _route.MeetUt - now + 900)), out _caUt);
                }
                var tca = _caUt - now;
                var stopDistance = speed * speed / (2 * Math.Max(0.1, 0.5 * fullAccel));
                var closeIn = tca < 60 || d < 1000 || d < 1.5 * stopDistance + 200 || double.IsInfinity(_ca);
                if (!closeIn)
                {
                    FarOut(station, v, speed, accel, tca, now, offset);
                    return;
                }
                _route.ApproachNextCheckUt = double.NaN;
                _correcting = false;

                // Close in, flown as a pilot flies the last of an intercept (docs/MATH.md §11.6): along the line, brake
                // whenever it's closing over a fifth faster than it wants (about the distance over 30 s, never faster than it
                // can stop, never faster than it already is); sideways, keep the pass within a tenth of the distance.
                var los = d > 1e-3 ? -r / d : Vector3d.zero;
                var closing = Vector3d.Dot(v, los);
                var sideways = v - los * closing;
                var toGo = closing > 0.2 ? d / closing : double.PositiveInfinity;
                var miss = closing > 0.2 ? sideways.magnitude * toGo : double.PositiveInfinity;
                var b = closing > RailsFlyer.ComeInMs ? 0.5 * fullAccel : Math.Min(0.5 * accel, MaxBrake);
                var wantClose = Math.Min(Math.Sqrt(2 * b * Math.Max(0, d - 2)), Math.Max(d / TimeToGoS, 0.3));
                wantClose = Math.Min(wantClose, Math.Max(closing, RailsFlyer.ComeInMs));

                var allowed = Math.Max(5, 0.1 * d);
                if (miss > allowed || closing <= 0.2) _fixSideways = true;
                var lateral = _fixSideways ? -sideways : Vector3d.zero;
                if (lateral.magnitude < 0.05) _fixSideways = false;

                var over = closing - wantClose;
                _braking = _braking ? over > 0.1 : over > Math.Max(1, 0.2 * wantClose);
                // Far too slow (under half the speed it should come in at; the distance over a minute, 1 m/s at least):
                // it speeds up. Crawling, it took most of an hour from 700 m, and over that long the orbit bends the
                // straight line so much that each correction left it further off (the test of 2026-11-08).
                var cruise = Math.Min(wantClose, Math.Max(SpeedUpMinMs, d / SpeedUpS));
                _speedingUp = !_braking && (_speedingUp ? closing < cruise - 0.1 : closing < 0.5 * cruise);
                var along = _braking || _speedingUp ? (_braking ? wantClose : cruise) - closing : closing <= 0.2 ? wantClose - closing : 0;
                var dv = los * along + lateral;
                var dvSize = dv.magnitude;
                var head = "close in: " + d.ToString("0") + " m to go, " + closing.ToString("0.0") + " m/s" + (_speedingUp ? " (speeding up to " + cruise.ToString("0.0") + ")" : "") + ", pass " +
                           (double.IsInfinity(miss) ? "–" : miss.ToString("0") + " m") + " off (" + allowed.ToString("0") + " allowed), ";

                if (dvSize < 0.05)
                {
                    _throttle = 0;
                    _translating = false;
                    if (speed > 0.5) _aim = -v / speed; // ready to brake
                    Status = head + "coasting";
                    return;
                }
                // Small fixes on RCS (no turning); braking and anything bigger on the main engine.
                if (dvSize < RcsFixMs && HasRcs(Vessel) && RcsDeltaV(Vessel) > 4 * dvSize)
                {
                    _station = station;
                    _translating = true;
                    _wantVel = v + dv;
                    _throttle = 0;
                    if (speed > 0.5) _aim = -v / speed;
                    Status = head + (along < 0 ? "braking" : "correcting") + " on RCS";
                    return;
                }
                _translating = false;
                _aim = dv / dvSize;
                _throttle = _attitude.ErrorDeg < 8 && accel > 0 ? (float)Math.Max(0.02, Math.Min(1, dvSize / accel)) : 0;
                Status = head + (_throttle > 0 ? (along < 0 ? "braking" : "correcting") + ", throttle " + (_throttle * 100).ToString("0") + "%" : "turning");
            }

            // ---------------- Leaving (docs/MATH.md §11.8) ----------------

            /// <summary>How fast it backs away from the station it left (m/s), and how far at least (m).</summary>
            private const double BackOffMs = 1, BackOffM = 30;

            /// <summary>Each separation push, backwards along the orbit (m/s), and how many at most.</summary>
            private const double SeparateMs = 0.5;
            private const int MaxPushes = 4;

            /// <summary>A separation push on the main engine waits till it's this far out (m), and pushes at this (m/s²).</summary>
            private const double EngineClearM = 100, PushAccel = 0.5;

            /// <summary>The craft's path must stay this far from the station (m) until its first burn.</summary>
            private const double PathClearM = 100;

            private int _pushes;

            /// <summary>Gives up backing off after this long (real s): long enough to climb out a departure lane.</summary>
            private const float BackOffGiveUpS = 600;

            /// <summary>Out the departure lane once this close to its top (m).</summary>
            private const double LaneDoneM = 10;

            /// <summary>
            /// Which way the top of the departure lane is from the station: the ship's own direction from it, raised to 45°
            /// above the station's horizon if it's lower (keeping its compass direction); straight up if it's directly below.
            /// </summary>
            private static Vector3d LaneExitDir(Vector3d fromCentre, Vector3d up)
            {
                var dir = fromCentre.normalized;
                var level = dir - up * Vector3d.Dot(dir, up);
                if (Vector3d.Dot(dir, up) >= Math.Sin(Math.PI / 4)) return dir;
                if (level.magnitude < 1e-3) return up;
                return (level.normalized * Math.Cos(Math.PI / 4) + up * Math.Sin(Math.PI / 4)).normalized;
            }

            /// <summary>The push at the top of the lane is done once it's within this of the velocity it wants (m/s).</summary>
            private const double LaneStillMs = 0.1;

            /// <summary>Its velocity (relative to the station) as it reached the top of the lane, which it keeps going at.</summary>
            private Vector3d? _laneExitVel;

            /// <summary>Fastest it climbs out the departure lane (m/s): 200 m in about 4 minutes, about 2 m/s of RCS start to stop.</summary>
            private const double LaneMs = 1;

            private bool _laneTop;

            private float _backOffSince = -1;

            /// <summary>
            /// Just undocked: straight back out along the station port's axis on RCS at 1 m/s, holding still, until it's
            /// its own size plus 20 m clear (30 m at least); only then does it turn for its first burn. Without RCS it just
            /// drifts clear on the undocking push. A burn that's due ends it early, once it's 15 m out.
            /// </summary>
            private void BackOff(double now)
            {
                var stack = Ships.VesselOfPart(_route.BackOffPortId);
                var port = Ships.FindPort(stack, _route.BackOffPortId);
                if (port == null || stack == Vessel)
                {
                    _route.BackingOff = false;
                    return;
                }
                if (_backOffSince < 0) _backOffSince = Time.time;
                var axis = (Vector3d)port.nodeTransform.forward;
                var away = (Vessel.CoMD - (Vector3d)port.nodeTransform.position).magnitude;
                var clear = Math.Max(BackOffM, Ships.Size(Vessel) + 20);
                var burn = _route.Burns.FirstOrDefault(b => !b.Done);
                var due = burn != null && now >= burn.Ut - StartLead(Vessel, burn.DeltaV) - 30;
                var rel = Vessel.obt_velocity - stack.obt_velocity;
                var drift = rel.magnitude;
                var timedOut = Time.time - _backOffSince > BackOffGiveUpS;
                _aim = Vector3d.zero;
                _throttle = 0;
                var pass = double.NaN;
                var lane = false;
                if (away >= clear && !timedOut && !due && HasRcs(Vessel) && Controlled(stack))
                {
                    // Traffic control: out the departure lane, to the top of the keep-out zone straight above the station
                    // (round it if the straight way is blocked), then on without stopping, pushed to drift clear (below).
                    _station = stack;
                    var centre = stack.CoMD;
                    // The top of the lane: out to the zone's edge on its own side of the station, raised to at least 45°
                    // above the station's horizon, so it never crosses back over the station to sit straight above it (the
                    // test of 2026-11-07 went north, over, then south to line up).
                    var exit = centre + LaneExitDir(Vessel.CoMD - centre, Up(stack)) * ZoneRadius(stack, Vessel);
                    var round = Ships.Size(stack) + Ships.Size(Vessel) + ClearM;
                    var here = Vessel.CoMD;
                    var goal = SegmentClear(here, exit, centre, round) ? exit : AroundStep(here, exit, centre, round);
                    var toGo = (exit - here).magnitude;
                    // Reaching the top is for good: it keeps going from there, never back to the climb (going back to it
                    // when it overshot made it climb and stop round and round, the test of 2026-11-07).
                    if (!_laneTop && toGo <= LaneDoneM) _laneTop = true;
                    if (!_laneTop)
                    {
                        var to = goal - here;
                        var d = to.magnitude;
                        _translating = true;
                        _wantVel = d > 0.05 ? to / d * Math.Min(LaneMs, Math.Max(0.2, Math.Min(Math.Sqrt(2 * 0.5 * RcsAccel(Vessel) * d), d / 10))) : Vector3d.zero;
                        Status = "climbing out the departure lane above " + stack.vesselName + ": " + toGo.ToString("0") + " m to go" + (goal == exit ? "" : " (round the station)");
                        return;
                    }
                    // At the top: no stopping. It keeps its climbing speed and adds the separation push backwards along the
                    // orbit (0.5 m/s at a time, §11.8): climbing alone would bring it back to the station an orbit later;
                    // with the push it drifts steadily away. Its path is then checked to stay 100 m clear till its first
                    // burn, pushing again if not, and the plan is checked (and made again) from here.
                    if (_laneExitVel == null) _laneExitVel = rel;
                    if (_pushes == 0) _pushes = 1;
                    var laneWant = _laneExitVel.Value - stack.obt_velocity.normalized * (SeparateMs * _pushes);
                    if ((laneWant - rel).magnitude > LaneStillMs)
                    {
                        _translating = true;
                        _wantVel = laneWant;
                        Status = "out of the departure lane above " + stack.vesselName + ", pushing clear: " + (laneWant - rel).magnitude.ToString("0.00") + " m/s to go";
                        return;
                    }
                    _translating = false;
                    var laneUntil = burn != null ? burn.Ut - StartLead(Vessel, burn.DeltaV) : now + 3600;
                    pass = Ships.ClosestPass(Vessel, stack, now + 20, Math.Max(now + 60, laneUntil), onceFartherThanM: 200);
                    if (pass < PathClearM && _pushes < MaxPushes)
                    {
                        _pushes++;
                        return;
                    }
                    lane = true;
                }
                else if (away >= clear && !timedOut && !due)
                {
                    _station = stack;
                    // Clear: straight into a push backwards along the orbit, with no stop first (stopping dead under 0.03 m/s
                    // on RCS hung on for minutes, the test of 2026-11-08): it changes its backing-off speed for the push (0.5 m/s at a time): a slightly lower, faster orbit that drifts
                    // steadily away and never swings back through the station, as 30 m and nearly still on rails can. On RCS
                    // when the monopropellant covers it comfortably (no turning near the station; 0.5 m/s is about 1.6 units
                    // on 30 t); else on the main engine, gently, once it's 100 m clear.
                    var backwards = -stack.obt_velocity.normalized;
                    if (_pushes == 0) _pushes = 1;
                    var want = backwards * (SeparateMs * _pushes);
                    var need = want - rel;
                    if (need.magnitude > LaneStillMs)
                    {
                        if (HasRcs(Vessel) && RcsDeltaV(Vessel) > 4 * want.magnitude)
                        {
                            _translating = true;
                            _wantVel = want;
                            Status = "pushing clear of " + stack.vesselName + " on RCS: " + drift.ToString("0.00") + " of " + want.magnitude.ToString("0.0") + " m/s";
                            return;
                        }
                        _translating = false;
                        if (away < EngineClearM)
                        {
                            Status = "drifting out to " + EngineClearM.ToString("0") + " m before pushing clear on the engine: " + away.ToString("0") + " m";
                            return;
                        }
                        if (LitThrust(Vessel, out _, fullLimiter: true) <= 0) LightMainEngines();
                        var full = LitThrust(Vessel, out _, fullLimiter: true) / Math.Max(1e-3, Ships.Mass(Vessel));
                        if (full > 0) SetLimiters((float)Math.Max(1, Math.Min(100, 100 * PushAccel / full)));
                        var accel = LitThrust(Vessel, out _) / Math.Max(1e-3, Ships.Mass(Vessel));
                        _aim = need.normalized;
                        _throttle = _attitude.ErrorDeg < 5 && accel > 0 ? (float)Math.Max(0.02, Math.Min(1, need.magnitude / accel)) : 0;
                        Status = "pushing clear of " + stack.vesselName + " on the engine: " + drift.ToString("0.00") + " of " + want.magnitude.ToString("0.0") + " m/s";
                        return;
                    }
                    _throttle = 0;
                    // Its path left alone until the first burn starts, once it's 200 m out: clear of the station by 100 m? If not, push again.
                    var until = burn != null ? burn.Ut - StartLead(Vessel, burn.DeltaV) : now + 3600;
                    pass = Ships.ClosestPass(Vessel, stack, now + 20, Math.Max(now + 60, until), onceFartherThanM: 200);
                    if (pass < PathClearM && _pushes < MaxPushes)
                    {
                        _pushes++;
                        return;
                    }
                }
                if (away >= clear || timedOut || due && away > 15)
                {
                    _route.BackingOff = false;
                    _translating = false;
                    _station = null;
                    TrafficScenario.Note(_route, "Backed off " + away.ToString("0") + " m from " + stack.vesselName + (lane ? " out the departure lane" : "") + ", drifting " + drift.ToString("0.00") + " m/s" +
                                                (double.IsNaN(pass) ? "" : "; its path stays " + (double.IsInfinity(pass) ? "far" : pass.ToString("0") + " m") + " clear of it") + ".", false);
                    // The plan was made from the station's orbit, before backing off moved it: check it now, and plan it again
                    // from here if need be, while there's still time before the first burn.
                    CourseCheck.Check(_route, Vessel, now, "After backing off", expected: true);
                    return;
                }
                if (!HasRcs(Vessel))
                {
                    _translating = false;
                    Status = "drifting clear of " + stack.vesselName + ": " + away.ToString("0") + " of " + clear.ToString("0") + " m";
                    return;
                }
                Vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
                _station = stack;
                _translating = true;
                _wantVel = axis * Math.Min(BackOffMs, Math.Max(0.2, (clear - away) / 5));
                Status = "backing off from " + stack.vesselName + ": " + away.ToString("0") + " of " + clear.ToString("0") + " m";
            }

            // ---------------- Docking (docs/MATH.md §11.7) ----------------

            /// <summary>Fastest it moves round the station and out to the port's axis (m/s), and on the final approach.</summary>
            private const double MoveMs = 2, FinalMs = 0.8;

            /// <summary>Slowest it closes on the port (m/s), so the magnets can always catch it.</summary>
            private const double ContactMs = 0.15;

            /// <summary>Clearance kept round the station on the way to its port (m), on top of both ships' sizes.</summary>
            private const double ClearM = 10;

            /// <summary>The ports must face each other within this before the final approach (degrees).</summary>
            private const float AlignedDeg = 3f;

            /// <summary>Gives up (the stand-in docks it) after this long (real s).</summary>
            private const float DockGiveUpS = 600f;

            private Vessel _station;
            private ModuleDockingNode _myPort, _theirPort;
            private Vector3d _wantVel; // world velocity relative to the station it should have now
            private bool _translating;
            private float _dockSince = -1;
            // KSP's RCS input pushes the other way on all three axes: the tests flipped y (2026-11-05), then x and z (2026-11-06).
            private readonly SignCheck _translateSign = new SignCheck("RCS", new Vector3(-1, -1, -1));
            private Vector3d _lastRelVel;
            private Vessel _lastFrame;
            private Vector3 _lastMove;

            // The pilot's safety stop (docs/MATH.md §11.13): what it's stopped for, when it looks again (real time), how
            // many looks so far, and whether it has said it's blocked.
            private Vessel _stopFor;
            private float _stopUntil;
            private int _stopTries;
            private bool _toldBlocked;

            /// <summary>Something comes within this of the ship (m, between their sizes), now or in the next <see cref="LookAheadS"/>: it stops.</summary>
            internal const double StopM = 30;

            /// <summary>Stopped, it goes on once nothing is within <see cref="StopM"/> plus this (m), so it doesn't stop and start at the edge.</summary>
            private const double ClearOfStopM = 10;

            /// <summary>How far ahead it looks for something coming close (s), at both ships' speeds now.</summary>
            private const double LookAheadS = 20;

            /// <summary>Backing away from something coming closer (m/s, on top of matching its speed).</summary>
            private const double BackAwayMs = 0.5;

            /// <summary>Coming in faster than this to the station (m/s), it's still braking on its engines: no safety stop yet.</summary>
            private const double BrakingFastMs = 5;

            /// <summary>Stopped, it looks again this often (real s); after this many looks it says it's blocked, and keeps holding.</summary>
            private const float StopWaitS = 10f;
            private const int StopTriesBeforeBlocked = 6;

            /// <summary>
            /// The pilot's own reaction near a station, under any traffic control: coming in, docking or backing off, if
            /// anything that isn't its station (or a route craft the station traffic already orders with it) comes within 30 m,
            /// now or in the next 20 s, it stops: engines off, still beside it, backing away if it's coming closer. It looks
            /// again every 10 s and goes on once it's clear; after a minute it says it's blocked, and keeps holding.
            /// </summary>
            private bool SafetyStop()
            {
                var leaving = _route.BackingOff && _route.State == RouteState.Flying;
                if (!leaving && _route.State != RouteState.Approaching && _route.State != RouteState.Arrived)
                {
                    _stopFor = null;
                    return false;
                }
                var station = leaving ? Ships.VesselOfPart(_route.BackOffPortId) : Ships.Find(_route.TargetId);
                if (_stopFor != null && !_stopFor.loaded) _stopFor = null; // gone out of range, or destroyed
                // Still braking in fast: RCS couldn't hold it beside anything, and the braking is stopping it anyway.
                if (_stopFor == null && _route.State == RouteState.Approaching && station != null && (Vessel.obt_velocity - station.obt_velocity).magnitude > BrakingFastMs)
                    return false;
                if (_stopFor == null)
                {
                    var hazard = Hazard(station, StopM, out var gap);
                    if (hazard == null) return false;
                    _stopFor = hazard;
                    _stopUntil = Time.time + StopWaitS;
                    _stopTries = 1;
                    _toldBlocked = false;
                    TrafficScenario.Note(_route, "Safety stop: " + hazard.vesselName + " is " + gap.ToString("0") + " m away" +
                                                (Closing(hazard) ? " and coming closer; backing away." : "; waiting for it to clear."), true);
                }
                else if (Time.time >= _stopUntil)
                {
                    var hazard = Hazard(station, StopM + ClearOfStopM, out _);
                    if (hazard == null)
                    {
                        TrafficScenario.Note(_route, "Clear of " + _stopFor.vesselName + ": going on.", false);
                        _stopFor = null;
                        return false;
                    }
                    _stopFor = hazard;
                    _stopUntil = Time.time + StopWaitS;
                    if (++_stopTries > StopTriesBeforeBlocked && !_toldBlocked)
                    {
                        _toldBlocked = true;
                        TrafficScenario.Note(_route, "Blocked by " + hazard.vesselName + ": holding till it moves.", true);
                    }
                }
                // Hold: engines off, attitude held, still against its station (not against what it stopped for: matching
                // that, it followed it away, 5 m behind for good, in the test of 2026-11-08); backing away while it comes closer.
                var closing = Closing(_stopFor);
                if (station != null) _station = station;
                _aim = Vector3d.zero;
                _throttle = 0;
                _rcsBurn = false;
                _translating = HasRcs(Vessel);
                _wantVel = closing ? OutOfPath(_stopFor) * BackAwayMs : Vector3d.zero; // relative to the station (see Translate)
                if (_dockSince >= 0) _dockSince += Time.deltaTime; // waiting doesn't count towards giving up docking
                var dist = Math.Max(0, (Vessel.CoMD - _stopFor.CoMD).magnitude - Ships.Size(Vessel) - Ships.Size(_stopFor));
                Status = "safety stop: " + _stopFor.vesselName + " " + dist.ToString("0") + " m away" + (closing ? ", backing away" : ", waiting") +
                         (_toldBlocked ? " (blocked)" : "") + (_translating ? "" : "; no RCS to hold still with");
                return true;
            }

            /// <summary>
            /// Which way to back away from something coming closer: sideways out of its path (across the way it's moving
            /// relative to this ship), not straight away along it, where it keeps coming (the test of 2026-11-08). Head on,
            /// any way across it.
            /// </summary>
            private Vector3d OutOfPath(Vessel other)
            {
                var fromIt = Vessel.CoMD - other.CoMD;
                var rel = other.obt_velocity - Vessel.obt_velocity;
                if (rel.sqrMagnitude < 1e-6) return fromIt.normalized;
                var along = rel.normalized;
                var across = fromIt - along * Vector3d.Dot(fromIt, along);
                if (across.magnitude < 1)
                {
                    across = Vector3d.Cross(along, Vector3d.up);
                    if (across.sqrMagnitude < 1e-6) across = Vector3d.Cross(along, Vector3d.right);
                }
                return across.normalized;
            }

            /// <summary>It's coming closer (or the ship is closing on it).</summary>
            private bool Closing(Vessel other) => Vector3d.Dot(other.CoMD - Vessel.CoMD, other.obt_velocity - Vessel.obt_velocity) < 0;

            /// <summary>
            /// The nearest thing that comes within <paramref name="withinM"/> of the ship (between their sizes), now or at
            /// its closest in the next 20 s, moving straight on as both are now; null if none. Its station and route craft the
            /// station traffic orders with this one (coming in to it, docking at it or leaving it) don't count.
            /// </summary>
            private Vessel Hazard(Vessel station, double withinM, out double gapNow)
            {
                Vessel worst = null;
                gapNow = double.MaxValue;
                var mySize = Ships.Size(Vessel);
                foreach (var v in FlightGlobals.VesselsLoaded)
                {
                    if (v == Vessel || v == station || v.LandedOrSplashed || v.vesselType == VesselType.Flag) continue;
                    if (station != null && InTrafficAt(v, station)) continue;
                    if (GivesWayToMe(v)) continue;
                    var r = v.CoMD - Vessel.CoMD;
                    var rel = v.obt_velocity - Vessel.obt_velocity;
                    var sizes = mySize + Ships.Size(v);
                    var t = rel.sqrMagnitude > 1e-6 ? Math.Max(0, Math.Min(LookAheadS, -Vector3d.Dot(r, rel) / rel.sqrMagnitude)) : 0;
                    var gap = r.magnitude - sizes;
                    var closest = (r + rel * t).magnitude - sizes;
                    if (Math.Min(gap, closest) >= withinM || Math.Max(0, gap) >= gapNow) continue;
                    worst = v;
                    gapNow = Math.Max(0, gap);
                }
                return worst;
            }

            /// <summary>
            /// Another route craft already stopped (for anything), which this one outranks: it goes on past while the other holds
            /// (or backs away). The order is the same for every route craft, so they never wait round in a circle: the one at the
            /// top always goes. One leaving a station outranks one that isn't; then the one whose next burn comes first; then
            /// the lower ship id.
            /// </summary>
            private bool GivesWayToMe(Vessel v)
            {
                if (!Pilots.TryGetValue(v.persistentId, out var other) || other.GaveUp || other.StoppedFor == null) return false;
                return Outranks(_route, Vessel.persistentId, other._route, v.persistentId);
            }

            /// <summary>The safety stop's order between two route craft (see <see cref="GivesWayToMe"/>): true when the first goes first.</summary>
            internal static bool Outranks(Route me, uint myId, Route other, uint otherId)
            {
                if (me.BackingOff != other.BackingOff) return me.BackingOff;
                var mine = NextBurnUt(me);
                var theirs = NextBurnUt(other);
                if (mine != theirs) return mine < theirs;
                return myId < otherId;
            }

            /// <summary>When its next burn is (game s); none left, never.</summary>
            private static double NextBurnUt(Route route) => route.Burns.FirstOrDefault(b => !b.Done)?.Ut ?? double.MaxValue;

            /// <summary>What its safety stop is holding it for (null when it isn't stopped).</summary>
            internal Vessel StoppedFor => _stopFor;

            /// <summary>
            /// A route craft coming in to, docking at or leaving <paramref name="station"/>, when the station directs its traffic:
            /// the traffic orders it with this one. At a station without traffic control, nothing orders them, so they're
            /// kept apart by their safety stops like anything else.
            /// </summary>
            private static bool InTrafficAt(Vessel v, Vessel station)
            {
                var scenario = TrafficScenario.Instance;
                if (scenario == null || !Controlled(station)) return false;
                foreach (var r in scenario.Routes)
                {
                    if (r.VesselId != v.persistentId) continue;
                    if ((r.State == RouteState.Approaching || r.State == RouteState.Arrived) && r.TargetId == station.persistentId) return true;
                    if (r.BackingOff && Ships.VesselOfPart(r.BackOffPortId) == station) return true;
                }
                return false;
            }

            /// <summary>
            /// Docks with RCS, as MechJeb's docking autopilot does: out round the station (keeping clear of both ships'
            /// sizes) to a point straight out from the port, lined up with it, then in along the port's axis, slowing to
            /// 0.15 m/s, so the magnets pull it in. The ship's port faces the station's all the while.
            /// </summary>
            private void Dock()
            {
                var station = Ships.Find(_route.TargetId);
                if (station == null || !CourseCheck.OffRails(station))
                {
                    _translating = false;
                    Status = "waiting for the station's physics to come on";
                    return;
                }
                _station = station;
                if (!PortsStillGood() && !PickDockingPorts(station))
                {
                    GiveUp("no free port on " + station.vesselName + " that one of its ports fits");
                    return;
                }
                // The magnets have it: hands off while they pull it in (reading the caught port as taken made it give up
                // at the last moment, the test of 2026-11-07).
                if (Catching)
                {
                    _translating = false;
                    Status = "docking: the magnets have it";
                    return;
                }
                // Station traffic: wait its turn before starting; once moving, hold still while one leaves.
                var way = GiveWayTo(_route, station, leavingOnly: _dockSince >= 0);
                if (way != null)
                {
                    _translating = true;
                    _wantVel = Vector3d.zero;
                    if (_dockSince >= 0) _dockSince += Time.deltaTime; // waiting doesn't count towards giving up
                    Status = "docking: holding still, " + way;
                    return;
                }
                if (_dockSince < 0)
                {
                    _dockSince = Time.time;
                    _throttle = 0;
                    Vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
                    _route.DockingPortId = _myPort.part.persistentId;
                    TrafficScenario.Note(_route, "Docking for real: " + _myPort.part.partInfo.title + " to " + station.vesselName + "'s " +
                                                _theirPort.part.partInfo.title + ".", true);
                }
                if (Time.time - _dockSince > DockGiveUpS)
                {
                    GiveUp("docking took more than " + (DockGiveUpS / 60).ToString("0") + " minutes");
                    return;
                }

                var axis = (Vector3d)_theirPort.nodeTransform.forward;
                var portPos = (Vector3d)_theirPort.nodeTransform.position;
                var mine = (Vector3d)_myPort.nodeTransform.position;
                var along = Vector3d.Dot(mine - portPos, axis);
                var lateral = mine - portPos - axis * along;
                _lateral = lateral;
                var off = lateral.magnitude;
                var aligned = _attitude.ErrorDeg < AlignedDeg;
                _translating = true;

                // In front of the port, on its axis and facing it: straight in, the sideways error closed as it goes.
                if (along > 0 && off < Math.Max(0.5, along / 10) && aligned)
                {
                    var speed = Math.Min(FinalMs, Math.Max(ContactMs, along / 20));
                    _wantVel = -axis * speed - lateral / 5;
                    Status = "docking: " + along.ToString("0.0") + " m to go, " + off.ToString("0.00") + " m off the axis, closing at " + speed.ToString("0.00") + " m/s";
                    return;
                }

                // Otherwise to the point straight out from the port, round the station if the straight way is blocked.
                var clear = Ships.Size(station) + Ships.Size(Vessel) + ClearM;
                var entry = portPos + axis * Math.Max(clear, 30);
                var here = Vessel.CoMD;
                var centre = station.CoMD;
                string leg;
                Vector3d goal;
                if (SegmentClear(here, entry, centre, clear))
                {
                    goal = entry;
                    leg = aligned || (entry - here).magnitude > 3 ? "out to the port's axis" : "lining up with the port";
                }
                else
                {
                    goal = AroundStep(here, entry, centre, clear);
                    leg = "round the station";
                }
                var to = goal - here;
                var d = to.magnitude;
                var want = Math.Min(MoveMs, Math.Min(Math.Sqrt(2 * 0.5 * RcsAccel(Vessel) * d), d / 10));
                _wantVel = d > 0.05 ? to / d * want : Vector3d.zero;
                Status = "docking: " + leg + ", " + d.ToString("0") + " m to go" + (aligned ? "" : " (turning: " + _attitude.ErrorDeg.ToString("0") + "° off)");
            }

            private bool PortsStillGood() =>
                _myPort != null && _theirPort != null && _myPort.vessel == Vessel && _theirPort.vessel == _station && (_theirPort.otherNode == null || Catching);

            /// <summary>The two ports' magnets have caught each other: docking is KSP's from here.</summary>
            private bool Catching => _theirPort != null && _myPort != null && _theirPort.otherNode == _myPort;

            /// <summary>The station port it came in to, if still free and one of its ports fits; otherwise the nearest pair.</summary>
            private bool PickDockingPorts(Vessel station)
            {
                var claimed = TrafficScenario.PortsNotFor(_route, station);
                var theirs = Ships.FindPort(station, _route.ApproachPortId != 0 ? _route.ApproachPortId : _route.BookedPortId);
                var mine = theirs != null && !claimed.Contains(theirs.part.persistentId) ? Ships.PortFor(Vessel, theirs) : null;
                if (mine == null && !Ships.PickPorts(Vessel, station, out mine, out theirs, claimed)) return false;
                _myPort = mine;
                _theirPort = theirs;
                _route.ApproachPortId = theirs.part.persistentId; // claimed, so no other route craft heads for it
                _route.BookedPortId = theirs.part.persistentId;
                _wantRight = Vector3.zero;
                return true;
            }

            /// <summary>The straight way from a to b keeps at least <paramref name="clear"/> from the centre.</summary>
            private static bool SegmentClear(Vector3d a, Vector3d b, Vector3d centre, double clear)
            {
                var ab = b - a;
                var t = ab.sqrMagnitude > 1e-9 ? Math.Max(0, Math.Min(1, Vector3d.Dot(centre - a, ab) / ab.sqrMagnitude)) : 0;
                return (a + ab * t - centre).magnitude >= clear * 0.95;
            }

            /// <summary>The next point round the station towards the entry point: 30° further round, at the clearance distance (or further out, if it's there already).</summary>
            private static Vector3d AroundStep(Vector3d here, Vector3d entry, Vector3d centre, double clear)
            {
                var from = (Vector3)(here - centre);
                var to = (Vector3)(entry - centre);
                var angle = Vector3.Angle(from, to);
                var step = angle > 30 ? Vector3.Slerp(from.normalized, to.normalized, 30 / angle) : to.normalized;
                return centre + (Vector3d)step * Math.Max(clear, from.magnitude);
            }

            /// <summary>
            /// Every physics tick while docking: RCS makes up the difference between the velocity it wants and the one it
            /// has, relative to the station, along the ship's own axes (X right, Z along the nose, Y the third, as KSP's
            /// RCS reads them). Nothing under 2 cm/s, to save monopropellant.
            /// </summary>
            private void Translate(FlightCtrlState s)
            {
                var reference = Vessel.ReferenceTransform;
                Vector3d dv;
                if (_rcsBurn)
                    dv = _rcsDv; // a small burn on RCS: what's left of it (gravity makes the sign check useless here, so none)
                else if (_translating && (_station ?? _stopFor) != null)
                {
                    // Relative to the station (or what it's stopped for, with no station), gravity cancels, so how the ship moves shows the axes' signs.
                    var frame = _station ?? _stopFor;
                    var rel = Vessel.obt_velocity - frame.obt_velocity;
                    if (frame != _lastFrame) _lastRelVel = rel; // a new frame: no jump to learn from
                    _lastFrame = frame;
                    _translateSign.Learn((Vector3)(Quaternion.Inverse(reference.rotation) * (Vector3)(rel - _lastRelVel)), _lastMove);
                    _lastRelVel = rel;
                    dv = _wantVel - rel;
                }
                else
                {
                    s.X = s.Y = s.Z = 0;
                    _lastMove = Vector3.zero;
                    return;
                }
                if (dv.magnitude < 0.02)
                {
                    s.X = s.Y = s.Z = 0;
                    _lastMove = Vector3.zero;
                    return;
                }
                var accel = (float)Math.Max(0.01, RcsAccel(Vessel));
                var local = Quaternion.Inverse(reference.rotation) * (Vector3)(dv / 0.5); // reach it in about half a second
                var move = new Vector3(Mathf.Clamp(local.x / accel, -1, 1), Mathf.Clamp(local.y / accel, -1, 1), Mathf.Clamp(local.z / accel, -1, 1));
                var sign = _translateSign.Sign;
                s.X = move.x * sign.x;
                s.Z = move.y * sign.y;
                s.Y = move.z * sign.z;
                _lastMove = move;
            }

            private double _brakeAt;

            /// <summary>
            /// Time to start braking in to the target: the speed to lose over half the engines' push, plus a minute's margin,
            /// before the meeting (or now, if it's closer than that already).
            /// </summary>
            private bool TimeToBrake(double now)
            {
                var target = Ships.Find(_route.TargetId);
                if (target == null) return false;
                var speed = (Vessel.obt_velocity - target.obt_velocity).magnitude;
                var accel = Math.Max(0.1, LitThrust(Vessel, out _, fullLimiter: true) / Math.Max(1e-3, Ships.Mass(Vessel)));
                if (accel <= 0.1)
                {
                    var main = Ships.MainEngine(Vessel);
                    if (main != null) accel = main.ThrustKn / Math.Max(1e-3, Ships.Mass(Vessel));
                }
                _brakeAt = _route.MeetUt - speed / (0.5 * accel) - 60;
                return now >= _brakeAt || (Vessel.CoMD - target.GetWorldPos3D()).magnitude < 20000;
            }

            /// <summary>Switches the route to coming in (the match's own Δv is what the braking burns, with real fuel).</summary>
            private void StartApproach(RouteBurn match)
            {
                if (match != null) match.Done = true;
                var target = Ships.Find(_route.TargetId);
                _route.State = RouteState.Approaching;
                _route.ApproachStage = -1;
                _route.ApproachHoldDir = Vector3d.zero;
                _route.ApproachNextCheckUt = double.NaN;
                _route.ApproachSince = Time.time;
                _route.QueueSince = Time.time;
                _route.ApproachPortId = target != null && target.loaded && Ships.PickStationPort(Vessel, target, TrafficScenario.PortsNotFor(_route, target)) is ModuleDockingNode port ? port.part.persistentId : 0;
                SetLimiters(100f);
                TrafficScenario.Note(_route, "Braking in to " + _route.TargetName + " instead of the match burn, " +
                                            (target != null ? ((Vessel.CoMD - target.GetWorldPos3D()).magnitude / 1000).ToString("0.0") + " km out at " +
                                                              (Vessel.obt_velocity - target.obt_velocity).magnitude.ToString("0") + " m/s" : "") + ".", true);
            }

            /// <summary>
            /// The collision guard: heading at the station (its closest pass inside both ships' sizes plus 20 m) with less
            /// than the time it needs to stop, it burns flat out back along its drift and away from the station's centre,
            /// whatever else it was doing. True while it's doing that.
            /// </summary>
            private bool AvoidStation(Vessel station, Vector3d centre, Vector3d v, double speed, double accel)
            {
                if (speed < 0.5 || accel <= 0) return false;
                var r = Vessel.CoMD - centre;
                var tca = -Vector3d.Dot(r, v) / (speed * speed);
                if (tca <= 0) return false;
                var pass = r + v * tca;
                var clear = Ships.Size(station) + Ships.Size(Vessel) + 20;
                if (pass.magnitude >= clear || tca > speed / (0.8 * accel) + 5) return false;
                var side = pass.magnitude > 1 ? pass.normalized : Vector3d.Cross(v, Vector3d.up).normalized;
                _aim = (-v / speed + side).normalized;
                _throttle = _attitude.ErrorDeg < 30 ? 1f : 0f;
                Status = "AVOIDING " + station.vesselName + ": closest pass " + pass.magnitude.ToString("0") + " m in " + tca.ToString("0") + " s at " + speed.ToString("0") + " m/s";
                if (!_warnedAvoid)
                {
                    _warnedAvoid = true;
                    TrafficScenario.Note(_route, "Collision guard: heading at " + station.vesselName + " at " + speed.ToString("0") + " m/s, burning away.", true);
                }
                return true;
            }

            private bool _warnedAvoid;

            private double _ca = double.PositiveInfinity, _caUt;
            private float _caAt = -10;
            private bool _correcting;
            private Orbit _correctGoal; // the orbit a correction coming in should leave it on, and when it was worked out
            private double _correctUt;

            /// <summary>The checkpoints by the time to closest approach (s), and the miss each allows (m).</summary>
            private static readonly double[] CheckS = { 600, 300, 120, 60 };
            private static readonly double[] AllowedM = { 800, 200, 100, 50 };

            /// <summary>
            /// Far out: coast, and at each checkpoint (by the time to KSP's closest approach) correct once if the closest
            /// approach misses the hold point by more than that checkpoint allows; the correction is solved through the
            /// orbits, so it follows the curve two nearby orbits make, not a straight line.
            /// </summary>
            private void FarOut(Vessel station, Vector3d v, double speed, double accel, double tca, double now, Vector3d offset)
            {
                var stage = 0;
                while (stage < CheckS.Length && tca <= CheckS[stage]) stage++;
                var allowed = stage < AllowedM.Length ? AllowedM[Math.Max(0, stage - 1)] : AllowedM[AllowedM.Length - 1];
                if (stage == 0) allowed = AllowedM[0];
                // Next checkpoint: the next time boundary below the time to go (the last is the start of close in, at 60 s).
                var nextAt = stage < CheckS.Length ? now + tca - CheckS[stage] : now + tca - 60;
                _route.ApproachNextCheckUt = nextAt;

                if (stage != _route.ApproachStage)
                {
                    _route.ApproachStage = stage;
                    if (_ca > allowed && !_correcting)
                    {
                        var aimUt = Math.Max(_caUt, now + 90);
                        if (Ships.AimAt(Vessel, station, offset, aimUt) is Vector3d fix)
                        {
                            // Flown as a burn: towards the orbit the correction should leave it on, a fixed Δv in space. Chasing
                            // a velocity relative to the station instead kept RCS on, fighting the curve two nearby orbits make
                            // (2026-11-06).
                            var o = Vessel.orbit;
                            _correctGoal = new Orbit();
                            _correctGoal.UpdateFromStateVectors(o.getRelativePositionAtUT(now), o.getOrbitalVelocityAtUT(now) + fix, o.referenceBody, now);
                            _correctUt = now;
                            _correcting = true;
                            TrafficScenario.Note(_route, "Coming in: closest approach " + _ca.ToString("0") + " m in " + Clock(tca) + " (" + allowed.ToString("0") +
                                                        " allowed): correcting " + fix.magnitude.ToString("0.0") + " m/s.", false);
                        }
                    }
                }

                var head = "closest approach " + (_ca < 10000 ? _ca.ToString("0") + " m" : (_ca / 1000).ToString("0.0") + " km") + " in " + Clock(tca) +
                           " (" + allowed.ToString("0") + " m allowed, next check in " + Clock(nextAt - now) + "), ";
                if (_correcting && _correctGoal != null && Vessel.orbit.referenceBody == _correctGoal.referenceBody)
                {
                    var need = (_correctGoal.getOrbitalVelocityAtUT(_correctUt) - Vessel.orbit.getOrbitalVelocityAtUT(_correctUt)).xzy;
                    _translating = false;
                    if (need.magnitude < 0.05)
                    {
                        _correcting = false;
                        _rcsBurn = false;
                        _rcsDv = Vector3d.zero;
                        _throttle = 0;
                        _caAt = -10; // see where it goes now
                    }
                    else if (need.magnitude < RcsFixMs && HasRcs(Vessel) && RcsDeltaV(Vessel) > 4 * need.magnitude)
                    {
                        _rcsBurn = true;
                        _rcsDv = need;
                        _throttle = 0;
                        Status = head + "correcting on RCS, " + need.magnitude.ToString("0.00") + " m/s to go";
                        return;
                    }
                    else
                    {
                        _rcsBurn = false;
                        _aim = need.normalized;
                        _throttle = _attitude.ErrorDeg < 5 && accel > 0 ? (float)Math.Max(0.02, Math.Min(1, need.magnitude / accel)) : 0;
                        Status = head + "correcting, " + need.magnitude.ToString("0.0") + " m/s to go";
                        return;
                    }
                }
                _translating = false;
                _throttle = 0;
                if (speed > 0.5) _aim = -v / speed; // facing back along its drift, ready to brake
                Status = head + "coasting";
            }

            private void GiveUp(string why)
            {
                if (GaveUp) return;
                GaveUp = true;
                _throttle = 0;
                var fallback = _route.State == RouteState.Arrived ? "it jumps to the port and docks the old way"
                    : _route.State == RouteState.Approaching ? "it's parked beside the station instead"
                    : "the burns are made the old way, in one jump";
                TrafficScenario.Note(_route, "Autopilot gave up (" + why + "): " + fallback + ".", true);
            }

            /// <summary>Lights the main engine kind (the one the rails flyer burns with) when no engine is lit.</summary>
            private void LightMainEngines()
            {
                var main = Ships.MainEngine(Vessel);
                if (main == null) return;
                foreach (var part in Vessel.parts)
                {
                    if (part.partInfo == null || part.partInfo.title != main.Part) continue;
                    foreach (var e in part.FindModulesImplementing<ModuleEngines>())
                        if (!e.EngineIgnited) e.Activate();
                }
                TrafficScenario.Note(_route, "Lit the " + main.Part + ".", false);
            }

            /// <summary>
            /// Every physics tick: it steers. Only the Traffic Controller panel's switch turns the autopilot off, not the player's
            /// controls: reading those, what was left on them from the last ship (or the craft's own last throttle)
            /// turned it off on switching to it, in the tests of 2026-11-07 and 2026-11-08.
            /// </summary>
            private void Fly(FlightCtrlState s)
            {
                var reference = Vessel.ReferenceTransform;
                var wantRcs = _translating || _rcsBurn;
                if (wantRcs && !_rcsBalanced)
                {
                    // The first time it needs RCS: balance it, so a push doesn't turn the ship too.
                    _rcsBalanced = true;
                    if (RcsBalance.Balance(Vessel, out var before, out var after) && after < before)
                        TrafficScenario.Note(_route, "Balanced its RCS: turning per push " + (100 * before).ToString("0.#") + "% → " + (100 * after).ToString("0.#") + "%.", false);
                }
                if (Vessel.ActionGroups[KSPActionGroup.RCS] != wantRcs) Vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, wantRcs);
                Quaternion target;
                if (_myPort != null && _theirPort != null && _station != null)
                    target = DockingAttitude(reference);
                else
                    target = _aim == Vector3d.zero
                        ? reference.rotation
                        : Quaternion.FromToRotation(ThrustAxis(), (Vector3)_aim) * reference.rotation; // shortest turn, roll left as it is
                _attitude.Drive(Vessel, target, s);
                s.mainThrottle = _throttle;
                Translate(s);
            }

            /// <summary>
            /// Docking: the ship's port faces the station's, and it's rolled about the port's axis so the sideways error lies
            /// along its X axis (one pair of RCS thrusters fixes it, not two at an angle); the nearer of the two ways round.
            /// The roll is held while the error is under half a metre, where its direction means little: kept as a direction
            /// in space, the ship's X axis, not as an angle from its roll now (an angle kept that way stays ahead of the ship
            /// however far it rolls: the test of 2026-11-06 spun slowly 8° off, 35 m out, and never went in).
            /// </summary>
            private Quaternion DockingAttitude(Transform reference)
            {
                var axis = (Vector3)_theirPort.nodeTransform.forward;
                var facing = Quaternion.FromToRotation(_myPort.nodeTransform.forward, -axis) * reference.rotation;
                var right = Vector3.ProjectOnPlane(facing * Vector3.right, axis);
                var lateral = Vector3.ProjectOnPlane((Vector3)_lateral, axis);
                if (lateral.magnitude > 0.5f)
                {
                    // Along or against the error, whichever is nearer.
                    _wantRight = Vector3.Dot(lateral, right) >= 0 ? lateral.normalized : -lateral.normalized;
                }
                else if (_wantRight == Vector3.zero)
                    _wantRight = right.normalized; // close in from the start: the roll it has
                var hold = Vector3.ProjectOnPlane(_wantRight, axis);
                if (hold.sqrMagnitude < 1e-6f) return facing;
                return Quaternion.AngleAxis(Vector3.SignedAngle(right, hold, axis), axis) * facing;
            }

            private Vector3 _wantRight; // world direction the ship's X axis is rolled to while docking
            private bool _rcsBalanced;
            private Vector3d _lateral;

            /// <summary>
            /// Which way the engines push the ship (world): the sum over the main engines' nozzles (thrust is along each
            /// nozzle's −forward), or the control part's nose when there's no engine.
            /// </summary>
            private Vector3 ThrustAxis()
            {
                Vector3 lit = Vector3.zero, all = Vector3.zero;
                foreach (var part in Vessel.parts)
                foreach (var e in part.FindModulesImplementing<ModuleEngines>())
                foreach (var t in e.thrustTransforms)
                {
                    if (t == null) continue;
                    all -= t.forward;
                    if (e.EngineIgnited) lit -= t.forward;
                }
                // The lit engines; before any is lit (they're lit when the burn starts), all of them.
                var sum = lit.sqrMagnitude > 1e-6f ? lit : all;
                return sum.sqrMagnitude > 1e-6f ? sum.normalized : Vessel.ReferenceTransform.up;
            }
        }

        /// <summary>
        /// Turns a ship to an orientation and holds it, on each axis separately (pitch, roll, yaw in the ship's control
        /// frame): the turn rate it wants falls off as the error closes, so it can always stop in time with the torque it
        /// has, and the controls make up the difference between that rate and the one it has (docs/MATH.md §11.4).
        /// </summary>
        private sealed class Attitude
        {
            /// <summary>Fraction of the full torque the stopping profile counts on: margin for lag and estimates.</summary>
            private const float BrakeShare = 0.6f;

            /// <summary>Close in, the wanted rate is the error over this (s): a smooth finish instead of the √ profile's kink.</summary>
            private const float SettleS = 1.5f;

            /// <summary>The fastest it turns, rad/s.</summary>
            private const float MaxRate = 0.5f;

            /// <summary>How quickly the controls chase the wanted rate (s).</summary>
            private const float RateLagS = 0.25f;

            internal float ErrorDeg;
            internal string Problem;

            // Each axis's sign, checked against how the ship really turns, in case the convention is the other way.
            private readonly SignCheck _sign = new SignCheck("turning");
            private Vector3 _lastRate;
            private Vector3 _lastControl;
            private bool _started;

            internal void Drive(Vessel v, Quaternion target, FlightCtrlState s)
            {
                var reference = v.ReferenceTransform;
                // The error as a rotation in the ship's own frame: x pitch, y roll, z yaw (KSP's control axes).
                var error = Quaternion.Inverse(reference.rotation) * target;
                error.ToAngleAxis(out var angle, out var axis);
                if (angle > 180) angle -= 360;
                if (float.IsNaN(axis.x) || float.IsInfinity(axis.x)) axis = Vector3.zero;
                ErrorDeg = Math.Abs(angle);
                var e = axis * (angle * Mathf.Deg2Rad);

                var rate = (Vector3)v.angularVelocity; // the same frame and axes
                var torque = AvailableTorque(v);
                var inertia = (Vector3)v.MOI;
                Learn(rate);

                var control = Vector3.zero;
                Problem = null;
                for (var i = 0; i < 3; i++)
                {
                    if (torque[i] < 1e-4f || inertia[i] < 1e-6f)
                    {
                        Problem = "it has no reaction wheels or RCS torque to turn with";
                        continue;
                    }
                    var accel = torque[i] / inertia[i]; // the most it can speed up or slow its turn, rad/s²
                    var wantedRate = Math.Sign(e[i]) * Math.Min(MaxRate, Math.Min(Mathf.Sqrt(2 * BrakeShare * accel * Math.Abs(e[i])), Math.Abs(e[i]) / SettleS));
                    var wantedAccel = (wantedRate - rate[i]) / RateLagS;
                    control[i] = Mathf.Clamp(wantedAccel / accel, -1, 1);
                }

                // KSP turns the ship against its control input: torque = −(pitch, roll, yaw) × each axis's torque.
                var sign = _sign.Sign;
                s.pitch = -control.x * sign.x;
                s.roll = -control.y * sign.y;
                s.yaw = -control.z * sign.z;
                _lastControl = control;
            }

            private void Learn(Vector3 rate)
            {
                if (_started) _sign.Learn(rate - _lastRate, _lastControl);
                _lastRate = rate;
                _started = true;
            }

            /// <summary>
            /// The torque the ship can make on each axis (kN·m) from every part that says (reaction wheels, RCS that's on,
            /// engine gimbals, control surfaces): the smaller of the two directions, to be safe.
            /// </summary>
            private static Vector3 AvailableTorque(Vessel v)
            {
                var total = Vector3.zero;
                foreach (var part in v.parts)
                foreach (var module in part.Modules)
                {
                    if (!(module is ITorqueProvider provider)) continue;
                    provider.GetPotentialTorque(out var pos, out var neg);
                    total += new Vector3(Math.Min(Math.Abs(pos.x), Math.Abs(neg.x)), Math.Min(Math.Abs(pos.y), Math.Abs(neg.y)), Math.Min(Math.Abs(pos.z), Math.Abs(neg.z)));
                }
                return total;
            }
        }

        /// <summary>
        /// Checks each axis's sign against the ship's real response: a strong command should speed the motion up in its
        /// own direction. If an axis keeps doing the opposite (15 ticks more often than not), its sign is flipped and said
        /// in the log, so a wrong guess at KSP's convention costs a moment's wobble, not a spin or a crash.
        /// </summary>
        private sealed class SignCheck
        {
            private readonly string _what;
            private Vector3 _score;
            private readonly bool[] _sure = new bool[3];
            internal Vector3 Sign = Vector3.one;

            internal SignCheck(string what) : this(what, Vector3.one) { }

            internal SignCheck(string what, Vector3 start)
            {
                _what = what;
                Sign = start;
            }

            /// <param name="change">How the motion changed since last tick, on each axis.</param>
            /// <param name="command">What was asked for last tick, on each axis, before the sign.</param>
            internal void Learn(Vector3 change, Vector3 command)
            {
                for (var i = 0; i < 3; i++)
                {
                    // Once an axis has shown its direction (15 more agreements than not), it's settled for good: later
                    // disagreements are pushes the controls didn't make (docking magnets, a bump), not a wrong sign.
                    if (_sure[i] || Math.Abs(command[i]) < 0.5f || Math.Abs(change[i]) < 1e-4f) continue;
                    _score[i] = Math.Min(15, _score[i] + (Math.Sign(change[i]) == Math.Sign(command[i]) ? 1 : -1));
                    if (_score[i] >= 15) _sure[i] = true;
                    if (_score[i] > -15) continue;
                    Sign[i] = -Sign[i];
                    _score[i] = 0;
                    Debug.LogWarning("[KspFlightPlanner] Autopilot: " + _what + " axis " + "xyz"[i] + " moved the other way; its sign is flipped.");
                }
            }
        }

        private static string Clock(double s)
        {
            if (s < 0) return "now";
            var t = TimeSpan.FromSeconds(Math.Min(s, 1e9));
            return s >= 3600 ? (int)t.TotalHours + " h " + t.Minutes + " min" : s >= 60 ? t.Minutes + " min " + t.Seconds + " s" : t.Seconds + " s";
        }
    }
}
