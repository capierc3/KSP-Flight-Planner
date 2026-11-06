using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Haulers
{
    /// <summary>
    /// The physics route flyer: flies a hauler for real while it's loaded with physics running near the player, by
    /// writing its controls every physics tick (the input hook kRPC uses to fly ships that aren't the one being flown).
    /// It faces the next burn, and flies the burn itself with the ship's engines: started half its length early, steered
    /// along the Δv still to go, cut off when that's gone (docs/MATH.md §11.4–11.5). Arrival and docking come next. While
    /// it flies, the rails flyer waits; the course check runs once the ship is back on rails, and absorbs the small error
    /// a real burn leaves.
    /// </summary>
    internal static class PhysicsFlyer
    {
        private static readonly Dictionary<uint, Pilot> Pilots = new Dictionary<uint, Pilot>();

        /// <summary>How long before a burn starts time warp is stopped for a hauler near the player (game s), to turn and settle.</summary>
        internal const double TurnLeadS = 90;

        /// <summary>What the physics flyer is doing with this route's ship now; null when it isn't flying it.</summary>
        internal static string Status(HaulerRoute route) => Pilots.TryGetValue(route.VesselId, out var p) ? p.Status : null;

        /// <summary>The physics flyer has the next burn in hand (the rails flyer leaves it alone).</summary>
        internal static bool FliesBurns(HaulerRoute route) => Flies(route);

        /// <summary>The physics flyer is flying this route's ship and hasn't given up on it.</summary>
        internal static bool Flies(HaulerRoute route) => Pilots.TryGetValue(route.VesselId, out var p) && !p.GaveUp;

        /// <summary>A hauler's physics comes on this far from the ship being flown (m), not KSP's 200 m: see HaulerShip.RaisePhysicsRange.</summary>
        internal const float PhysicsRangeM = 2200;

        /// <summary>Takes up or lets go of the route's ship, every frame in flight.</summary>
        internal static void Tick(HaulerRoute route)
        {
            var ship = HaulerShip.Find(route.VesselId);
            // Switched off to fly it by hand: back on once the player has left it and it's on rails again.
            if (!route.Autopilot && ship != null && ship != FlightGlobals.ActiveVessel && !CourseCheck.OffRails(ship))
            {
                route.Autopilot = true;
                HaulerScenario.Note(route, "Autopilot back on (you've left it).", false);
            }
            // Docked (the ship may have just merged into the station, its own vessel gone): done.
            if (route.State == RouteState.Arrived && route.DockingPortId != 0 && HaulerShip.PortDocked(route.DockingPortId))
            {
                route.State = RouteState.Docked;
                HaulerScenario.Note(route, "Docked at " + route.TargetName + ".", true);
            }
            if (route.BackingOff && ship != null && !CourseCheck.OffRails(ship)) route.BackingOff = false; // on rails: nothing to bump into
            var docking = route.State == RouteState.Arrived && ship != null && ship.loaded && HasRcs(ship);
            var active = route.State == RouteState.Flying || route.State == RouteState.Approaching || docking;
            if (active && ship != null) HaulerShip.RaisePhysicsRange(ship, PhysicsRangeM);
            if (route.State == RouteState.Flying && route.Autopilot && ship != null && ship.loaded) StopWarpForBurn(route, ship);

            var wanted = route.Autopilot && active && CourseCheck.OffRails(ship);
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
                HaulerScenario.Note(route, "Autopilot has it (physics on).", false);
            }
            pilot?.Plan();
        }

        /// <summary>
        /// A hauler loaded near the player but packed by time warp would have its burn made in one jump; stopping warp a
        /// little before the burn starts unpacks it, so the autopilot can turn it and fly the burn for real.
        /// </summary>
        private static void StopWarpForBurn(HaulerRoute route, Vessel ship)
        {
            if (TimeWarp.fetch == null || TimeWarp.CurrentRateIndex == 0) return;
            var burn = route.Burns.FirstOrDefault(b => !b.Done);
            if (burn == null) return;
            var now = Planetarium.GetUniversalTime();
            var start = burn.Ut - BurnSeconds(ship, burn.DeltaV) / 2;
            // A frame at this rate covers about rate × 0.1 s of game time: stop early enough not to jump past.
            if (now < start - TurnLeadS - TimeWarp.CurrentRate * 0.1 || now > burn.Ut) return;
            TimeWarp.SetRate(0, true);
            HaulerScenario.Note(route, "Time warp stopped: burn " + (route.Burns.IndexOf(burn) + 1) + " starts in " + Clock(start - now) + ".", true);
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
        internal static double BurnSeconds(Vessel v, double deltaV)
        {
            var thrust = LitThrust(v, out var isp);
            if (thrust <= 0)
            {
                var engine = HaulerShip.MainEngine(v);
                if (engine == null) return 0;
                thrust = engine.ThrustKn;
                isp = engine.IspVacuum;
            }
            var ve = isp * HaulerShip.G0;
            return HaulerShip.Mass(v) * ve / thrust * (1 - Math.Exp(-deltaV / ve));
        }

        /// <summary>The lit engines' full thrust (kN, thrust limiters applied) and their thrust-weighted vacuum Isp.</summary>
        private static double LitThrust(Vessel v, out double isp)
        {
            double thrust = 0, flow = 0;
            foreach (var part in v.parts)
            foreach (var e in part.FindModulesImplementing<ModuleEngines>())
            {
                if (!e.EngineIgnited || e.flameout) continue;
                var f = e.maxThrust * e.thrustPercentage / 100.0;
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
            return power / 6 / Math.Max(1e-3, HaulerShip.Mass(v));
        }

        /// <summary>One ship being flown: what it aims for (set each frame), and the controls written each physics tick.</summary>
        private sealed class Pilot
        {
            /// <summary>A burn is done when less than this is left (m/s).</summary>
            private const double DoneMs = 0.05;

            /// <summary>The engines only fire with the ship pointed within this of the Δv still to go.</summary>
            private const float FireWithinDeg = 5f;

            internal readonly Vessel Vessel;
            private readonly HaulerRoute _route;
            private readonly Attitude _attitude = new Attitude();
            private Vector3d _aim; // world direction the engines should push; zero = just hold still
            private float _throttle;
            internal string Status = "";
            internal bool GaveUp;

            // The burn being flown: the orbit it should leave the ship on, and how it's going.
            private HaulerBurn _burn, _checkedFor;
            private Orbit _goal;
            private double _startUt, _startDv, _bestLeft = double.MaxValue;
            private int _number;
            private double _stalledSince = -1;

            // What the autopilot wrote last tick, to tell the player's hands from its own.
            private float _lastPitch, _lastYaw, _lastRoll, _lastThrottle, _lastX, _lastY, _lastZ;

            internal Pilot(HaulerRoute route, Vessel vessel)
            {
                _route = route;
                Vessel = vessel;
                Vessel.OnFlyByWire += Fly;
                // Stock SAS on the ship being flown would fight the autopilot.
                if (Vessel == FlightGlobals.ActiveVessel) Vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            }

            internal void Release()
            {
                if (Vessel != null) Vessel.OnFlyByWire -= Fly;
                if (_burn != null)
                    HaulerScenario.Note(_route, "Burn " + _number + " cut short (physics went off): the course check takes it from here.", false);
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
                if (burn == null)
                {
                    _aim = Vector3d.zero;
                    _throttle = 0;
                    Status = "holding still: the burns are done";
                    return;
                }
                _number = _route.Burns.IndexOf(burn) + 1;
                var length = BurnSeconds(Vessel, burn.DeltaV);
                if (length <= 0)
                {
                    GiveUp("no engine with fuel to burn with");
                    return;
                }
                var start = burn.Ut - length / 2;
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
                // Until then, face the burn as its node axes are now.
                var orbit = Vessel.orbit;
                HaulerShip.Axes(orbit.getRelativePositionAtUT(now), orbit.getOrbitalVelocityAtUT(now), out var prograde, out var normal, out var radial);
                var dv = prograde * burn.Prograde + normal * burn.Normal + radial * burn.Radial;
                _aim = dv.magnitude > 0.01 ? dv.normalized.xzy : Vector3d.zero; // orbit frame → world, as KSP places ships
                _throttle = 0;
                Status = "facing burn " + _number + " (" + burn.Title + ", " + burn.DeltaV.ToString("0") + " m/s, " + length.ToString("0") + " s) in " +
                         Clock(start - now) + ": " + _attitude.ErrorDeg.ToString("0.0") + "° off" + (_attitude.Problem != null ? "; " + _attitude.Problem : "");
                if (_attitude.Problem != null && now > burn.Ut) GiveUp(_attitude.Problem);
            }

            /// <summary>
            /// Starts flying a burn: works out the orbit it should leave the ship on (the same one the rails flyer would give
            /// it), lights the engines if none is lit, and from now on steers along the difference.
            /// </summary>
            private void StartBurn(HaulerBurn burn, double now)
            {
                var orbit = Vessel.orbit;
                var pos = orbit.getRelativePositionAtUT(burn.Ut);
                var vel = orbit.getOrbitalVelocityAtUT(burn.Ut);
                HaulerShip.Axes(pos, vel, out var prograde, out var normal, out var radial);
                _goal = new Orbit();
                _goal.UpdateFromStateVectors(pos, vel + prograde * burn.Prograde + normal * burn.Normal + radial * burn.Radial, orbit.referenceBody, burn.Ut);
                _burn = burn;
                burn.Done = true; // the rails flyer must not make it again; if physics goes off midway, the course check catches what's left
                _startUt = now;
                _startDv = Left().magnitude;
                _bestLeft = _startDv;
                _stalledSince = -1;
                if (LitThrust(Vessel, out _) <= 0) LightMainEngines();
                HaulerScenario.Note(_route, "Burn " + _number + " (" + burn.Title + "): flying it for real, " + _startDv.ToString("0.0") + " m/s to go" +
                                            (now > burn.Ut ? ", " + (now - burn.Ut).ToString("0") + " s late" : "") + ".", true);
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
                _aim = size > 1e-6 ? left.normalized.xzy : Vector3d.zero;
                var thrust = LitThrust(Vessel, out _);
                var accel = thrust / Math.Max(1e-3, HaulerShip.Mass(Vessel)); // m/s² at full throttle

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
                var left = _burn != null && _goal != null && Vessel.orbit.referenceBody == _goal.referenceBody ? Left().magnitude : double.NaN;
                HaulerScenario.Note(_route, "Burn " + _number + " flown in " + (now - _startUt).ToString("0") + " s: " +
                                            (double.IsNaN(left) ? "" : (_startDv - left).ToString("0.0") + " of " + _startDv.ToString("0.0") + " m/s, " +
                                                                     left.ToString("0.00") + " m/s off") +
                                            (problem != null ? "; " + problem : "") + ". The course check tidies up once it's back on rails.", problem != null);
                _burn = null;
                _goal = null;
            }

            /// <summary>Coming in, it's done when it's this close to the spot off the port (m) and this slow (m/s).</summary>
            private const double ArrivedM = 20, StillMs = 0.3;

            /// <summary>The braking it plans on: half the engines' push, and no more than this (m/s²), so the braking burn is long enough to watch.</summary>
            private const double MaxBrake = 2.5;

            /// <summary>The engines fire when the velocity it wants is this far off (m/s), and stop once it's within StopFiringMs.</summary>
            private const double FireMs = 0.6, StopFiringMs = 0.2;

            /// <summary>Close in, the speed it wants is the distance over this (s): a long, gentle braking burn.</summary>
            private const double EaseS = 10;

            private bool _firing, _lit;

            /// <summary>
            /// Coming in to the port: the speed it wants falls off towards the spot 150 m out so it can always stop there
            /// with the braking it plans on, and the engines make up the difference, pointed along it. While it's only
            /// coasting it faces backwards along its drift, ready to brake: the flip, then the braking burn
            /// (docs/MATH.md §11.6). Stopped there, the route has arrived, and docking takes over.
            /// </summary>
            private void Approach()
            {
                var station = HaulerShip.Find(_route.TargetId);
                var port = HaulerShip.FindPort(station, _route.ApproachPortId);
                if (port == null)
                {
                    GiveUp("the port it was coming in to is gone or taken");
                    return;
                }
                var axis = (Vector3d)port.nodeTransform.forward;
                var spot = (Vector3d)port.nodeTransform.position + axis * RailsFlyer.StandoffM;
                var r = Vessel.CoMD - spot;
                var v = Vessel.obt_velocity - station.obt_velocity;
                var d = r.magnitude;
                var speed = v.magnitude;

                if (!_lit && LitThrust(Vessel, out _) <= 0)
                {
                    LightMainEngines();
                    _lit = true;
                }
                var accel = LitThrust(Vessel, out _) / Math.Max(1e-3, HaulerShip.Mass(Vessel));
                if (d < ArrivedM && speed < StillMs)
                {
                    _throttle = 0;
                    _aim = Vector3d.zero;
                    _route.State = RouteState.Arrived;
                    HaulerScenario.Note(_route, "Stopped " + d.ToString("0") + " m off the spot in front of " + station.vesselName + "'s " +
                                                port.part.partInfo.title + " at " + speed.ToString("0.00") + " m/s: docking.", true);
                    return;
                }

                var brake = Math.Min(0.5 * accel, MaxBrake);
                var wantSpeed = Math.Min(RailsFlyer.ComeInMs, Math.Min(Math.Sqrt(2 * brake * Math.Max(0, d - 2)), d / EaseS));
                var want = d > 1e-3 ? -r / d * wantSpeed : Vector3d.zero;
                var dv = want - v;
                var off = dv.magnitude;
                _firing = _firing ? off > StopFiringMs : off > FireMs;
                if (_firing) _aim = dv / off;
                else if (speed > 0.5) _aim = -v / speed; // face back along the drift, ready to brake
                _throttle = _firing && _attitude.ErrorDeg < 8 && accel > 0 ? (float)Math.Max(0.02, Math.Min(1, off / accel)) : 0;
                Status = "coming in: " + (d + RailsFlyer.StandoffM).ToString("0") + " m from the port, " + speed.ToString("0.0") + " m/s, " +
                         (_throttle > 0 ? "braking, throttle " + (_throttle * 100).ToString("0") + "%" : _firing ? "turning to brake" : "coasting");
            }

            // ---------------- Leaving (docs/MATH.md §11.8) ----------------

            /// <summary>How fast it backs away from the station it left (m/s), and how far at least (m).</summary>
            private const double BackOffMs = 1, BackOffM = 30;

            /// <summary>Gives up backing off after this long (real s).</summary>
            private const float BackOffGiveUpS = 300;

            private float _backOffSince = -1;

            /// <summary>
            /// Just undocked: straight back out along the station port's axis on RCS at 1 m/s, holding still, until it's
            /// its own size plus 20 m clear (30 m at least); only then does it turn for its first burn. Without RCS it just
            /// drifts clear on the undocking push. A burn that's due ends it early, once it's 15 m out.
            /// </summary>
            private void BackOff(double now)
            {
                var stack = HaulerShip.VesselOfPart(_route.BackOffPortId);
                var port = HaulerShip.FindPort(stack, _route.BackOffPortId);
                if (port == null || stack == Vessel)
                {
                    _route.BackingOff = false;
                    return;
                }
                if (_backOffSince < 0) _backOffSince = Time.time;
                var axis = (Vector3d)port.nodeTransform.forward;
                var away = (Vessel.CoMD - (Vector3d)port.nodeTransform.position).magnitude;
                var clear = Math.Max(BackOffM, HaulerShip.Size(Vessel) + 20);
                var burn = _route.Burns.FirstOrDefault(b => !b.Done);
                var due = burn != null && now >= burn.Ut - BurnSeconds(Vessel, burn.DeltaV) / 2 - 30;
                if (away >= clear || Time.time - _backOffSince > BackOffGiveUpS || due && away > 15)
                {
                    _route.BackingOff = false;
                    _translating = false;
                    _station = null;
                    HaulerScenario.Note(_route, "Backed off " + away.ToString("0") + " m from " + stack.vesselName + ": on its way.", false);
                    return;
                }
                _aim = Vector3d.zero;
                _throttle = 0;
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
            private readonly SignCheck _translateSign = new SignCheck("RCS");
            private Vector3d _lastRelVel;
            private Vector3 _lastMove;

            /// <summary>
            /// Docks with RCS, as MechJeb's docking autopilot does: out round the station (keeping clear of both ships'
            /// sizes) to a point straight out from the port, lined up with it, then in along the port's axis, slowing to
            /// 0.15 m/s, so the magnets pull it in. The ship's port faces the station's all the while.
            /// </summary>
            private void Dock()
            {
                var station = HaulerShip.Find(_route.TargetId);
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
                if (_dockSince < 0)
                {
                    _dockSince = Time.time;
                    _throttle = 0;
                    Vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
                    _route.DockingPortId = _myPort.part.persistentId;
                    HaulerScenario.Note(_route, "Docking for real: " + _myPort.part.partInfo.title + " to " + station.vesselName + "'s " +
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
                var clear = HaulerShip.Size(station) + HaulerShip.Size(Vessel) + ClearM;
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
                _myPort != null && _theirPort != null && _myPort.vessel == Vessel && _theirPort.vessel == _station && _theirPort.otherNode == null;

            /// <summary>The station port it came in to, if still free and one of its ports fits; otherwise the nearest pair.</summary>
            private bool PickDockingPorts(Vessel station)
            {
                var theirs = HaulerShip.FindPort(station, _route.ApproachPortId);
                var mine = theirs != null ? HaulerShip.PortFor(Vessel, theirs) : null;
                if (mine == null && !HaulerShip.PickPorts(Vessel, station, out mine, out theirs)) return false;
                _myPort = mine;
                _theirPort = theirs;
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
                if (!_translating || _station == null)
                {
                    s.X = s.Y = s.Z = 0;
                    _lastMove = Vector3.zero;
                    return;
                }
                var reference = Vessel.ReferenceTransform;
                var rel = Vessel.obt_velocity - _station.obt_velocity;
                _translateSign.Learn((Vector3)(Quaternion.Inverse(reference.rotation) * (Vector3)(rel - _lastRelVel)), _lastMove);
                _lastRelVel = rel;
                var dv = _wantVel - rel;
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

            private void GiveUp(string why)
            {
                if (GaveUp) return;
                GaveUp = true;
                _throttle = 0;
                var fallback = _route.State == RouteState.Arrived ? "it jumps to the port and docks the old way"
                    : _route.State == RouteState.Approaching ? "it's parked beside the station instead"
                    : "the burns are made the old way, in one jump";
                HaulerScenario.Note(_route, "Autopilot gave up (" + why + "): " + fallback + ".", true);
            }

            /// <summary>Lights the main engine kind (the one the rails flyer burns with) when no engine is lit.</summary>
            private void LightMainEngines()
            {
                var main = HaulerShip.MainEngine(Vessel);
                if (main == null) return;
                foreach (var part in Vessel.parts)
                {
                    if (part.partInfo == null || part.partInfo.title != main.Part) continue;
                    foreach (var e in part.FindModulesImplementing<ModuleEngines>())
                        if (!e.EngineIgnited) e.Activate();
                }
                HaulerScenario.Note(_route, "Lit the " + main.Part + ".", false);
            }

            /// <summary>Every physics tick: the player's hands on the controls switch the autopilot off; otherwise it steers.</summary>
            private void Fly(FlightCtrlState s)
            {
                if (Vessel == FlightGlobals.ActiveVessel && PlayerSteering(s))
                {
                    _route.Autopilot = false;
                    s.mainThrottle = 0;
                    HaulerScenario.Note(_route, "Autopilot off: you took the controls.", true);
                    return;
                }
                var reference = Vessel.ReferenceTransform;
                Quaternion target;
                if (_myPort != null && _theirPort != null && _station != null)
                    // Docking: the ship's port faces the station's, roll left as it is.
                    target = Quaternion.FromToRotation(_myPort.nodeTransform.forward, -_theirPort.nodeTransform.forward) * reference.rotation;
                else
                    target = _aim == Vector3d.zero
                        ? reference.rotation
                        : Quaternion.FromToRotation(ThrustAxis(), (Vector3)_aim) * reference.rotation; // shortest turn, roll left as it is
                _attitude.Drive(Vessel, target, s);
                s.mainThrottle = _throttle;
                Translate(s);
                _lastPitch = s.pitch;
                _lastYaw = s.yaw;
                _lastRoll = s.roll;
                _lastThrottle = s.mainThrottle;
                _lastX = s.X;
                _lastY = s.Y;
                _lastZ = s.Z;
            }

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

            /// <summary>
            /// The player moved a control: something differs from what the autopilot wrote last tick and isn't at rest.
            /// (Comparing with its own last values works whether or not KSP resets the controls between ticks.)
            /// </summary>
            private bool PlayerSteering(FlightCtrlState s) =>
                Moved(s.pitch, _lastPitch) || Moved(s.yaw, _lastYaw) || Moved(s.roll, _lastRoll) ||
                Moved(s.X, _lastX) || Moved(s.Y, _lastY) || Moved(s.Z, _lastZ) ||
                s.mainThrottle > 0.01f && Math.Abs(s.mainThrottle - _lastThrottle) > 0.01f;

            private static bool Moved(float now, float last) => Math.Abs(now) > 0.05f && Math.Abs(now - last) > 0.05f;
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
            internal Vector3 Sign = Vector3.one;

            internal SignCheck(string what) => _what = what;

            /// <param name="change">How the motion changed since last tick, on each axis.</param>
            /// <param name="command">What was asked for last tick, on each axis, before the sign.</param>
            internal void Learn(Vector3 change, Vector3 command)
            {
                for (var i = 0; i < 3; i++)
                {
                    if (Math.Abs(command[i]) < 0.5f || Math.Abs(change[i]) < 1e-4f) continue;
                    _score[i] = Math.Min(15, _score[i] + (Math.Sign(change[i]) == Math.Sign(command[i]) ? 1 : -1));
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
