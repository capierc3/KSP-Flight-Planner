using KRPC.Client;
using KRPC.Client.Services.SpaceCenter;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.Services;

/// <summary>Why <see cref="LiveTelemetry.Read"/> had nothing: the streams no longer fit what's being flown.</summary>
public enum TelemetryEnd
{
    /// <summary>The player switched to another vessel (or the scene reloaded it, e.g. a quickload).</summary>
    OtherVessel,

    /// <summary>The vessel crossed into another body's sphere of influence: same flight, readings against the new body.</summary>
    OtherBody,

    /// <summary>A reading failed: the vessel is gone, or the game is between scenes.</summary>
    Lost,
}

/// <summary>
/// The flown vessel's readings for the flight log, streamed by kRPC: the game pushes them as they change, so reading
/// one costs nothing on the connection (unlike a call, which costs about a game frame). Opened for one vessel in one
/// sphere of influence (the surface readings are against that body); when the player switches vessel or the vessel
/// leaves the body, <see cref="Read"/> returns null and a new one has to be opened.
/// </summary>
public sealed class LiveTelemetry
{
    private readonly List<IDisposable> _streams = [];
    private readonly Vessel _vessel;
    private readonly CelestialBody _body;
    private readonly Func<double> _ut, _met, _mass, _altitude, _surfaceSpeed, _verticalSpeed, _orbitalSpeed, _apoapsis, _periapsis,
        _latitude, _longitude, _pitch, _heading, _thrust, _isp, _q, _g, _throttle, _warp, _sma, _ecc, _meanAnomaly, _inc, _lan, _argPe;
    private readonly Func<VesselSituation> _situation;
    private readonly Func<int> _stage;
    private readonly Func<Vessel> _active;
    private readonly Func<CelestialBody> _orbitBody;

    /// <summary>The vessel's name when the streams were opened.</summary>
    public string VesselName { get; }

    /// <summary>kRPC's id for the vessel: the same vessel keeps it while connected (unlike game time − MET, which drifts on rails).</summary>
    public ulong VesselId { get; }

    /// <summary>Ship, Probe, Lander… EVA for a kerbal outside, Debris for dropped parts.</summary>
    public string VesselType { get; }

    /// <summary>The body the surface readings are against.</summary>
    public string BodyName { get; }

    private LiveTelemetry(Connection c, Vessel vessel)
    {
        var space = c.SpaceCenter();
        _vessel = vessel;
        VesselName = vessel.Name;
        VesselId = vessel.id;
        VesselType = vessel.Type.ToString();
        var orbit = vessel.Orbit;
        _body = orbit.Body;
        BodyName = KspLink.PlannerBodyName(_body.Name);
        var flight = vessel.Flight(_body.ReferenceFrame); // speeds against the turning body: surface speeds
        var attitude = vessel.Flight(vessel.SurfaceReferenceFrame); // pitch and heading against the horizon and north
        var control = vessel.Control;

        _active = Add(c.AddStream(() => space.ActiveVessel));
        _orbitBody = Add(c.AddStream(() => orbit.Body));
        _ut = Add(c.AddStream(() => space.UT));
        _warp = AddFloat(c, () => space.WarpRate);
        _met = Add(c.AddStream(() => vessel.MET));
        _situation = Add(c.AddStream(() => vessel.Situation));
        _stage = Add(c.AddStream(() => control.CurrentStage));
        _mass = AddFloat(c, () => vessel.Mass);
        _thrust = AddFloat(c, () => vessel.Thrust);
        _isp = AddFloat(c, () => vessel.SpecificImpulse);
        _throttle = AddFloat(c, () => control.Throttle);
        _altitude = Add(c.AddStream(() => flight.MeanAltitude));
        _surfaceSpeed = Add(c.AddStream(() => flight.Speed));
        _verticalSpeed = Add(c.AddStream(() => flight.VerticalSpeed));
        _latitude = Add(c.AddStream(() => flight.Latitude));
        _longitude = Add(c.AddStream(() => flight.Longitude));
        _pitch = AddFloat(c, () => attitude.Pitch);
        _heading = AddFloat(c, () => attitude.Heading);
        _q = AddFloat(c, () => flight.DynamicPressure);
        _g = AddFloat(c, () => flight.GForce);
        _orbitalSpeed = Add(c.AddStream(() => orbit.Speed));
        _apoapsis = Add(c.AddStream(() => orbit.ApoapsisAltitude));
        _periapsis = Add(c.AddStream(() => orbit.PeriapsisAltitude));
        _sma = Add(c.AddStream(() => orbit.SemiMajorAxis));
        _ecc = Add(c.AddStream(() => orbit.Eccentricity));
        _meanAnomaly = Add(c.AddStream(() => orbit.MeanAnomaly));
        _inc = Add(c.AddStream(() => orbit.Inclination));
        _lan = Add(c.AddStream(() => orbit.LongitudeOfAscendingNode));
        _argPe = Add(c.AddStream(() => orbit.ArgumentOfPeriapsis));
    }

    /// <summary>Opens the streams for the vessel being flown, or returns null when there isn't one. Runs on a worker thread.</summary>
    internal static LiveTelemetry? Open(Connection c)
    {
        Vessel vessel;
        try { vessel = c.SpaceCenter().ActiveVessel; }
        catch (RPCException) { return null; }
        return new LiveTelemetry(c, vessel);
    }

    /// <summary>
    /// The vessel now, or null when these streams no longer fit (<paramref name="end"/> says why): the player switched
    /// vessel, it left the body, or it's gone.
    /// </summary>
    public FlightSample? Read(out TelemetryEnd end)
    {
        end = TelemetryEnd.Lost;
        try
        {
            if (_active() != _vessel) { end = TelemetryEnd.OtherVessel; return null; }
            if (_orbitBody() != _body) { end = TelemetryEnd.OtherBody; return null; }
            return new FlightSample(
                _ut(), _met(), BodyName, KspLink.SituationName(_situation()), _stage(),
                _altitude(), _surfaceSpeed(), _orbitalSpeed(), _verticalSpeed(), _apoapsis(), _periapsis(),
                _latitude(), _longitude(), _pitch(), _heading(), _throttle(),
                _mass(), _thrust(), _isp(), _q(), _g(), _warp())
            {
                SemiMajorAxisM = _sma(),
                Eccentricity = _ecc(),
                MeanAnomalyRad = _meanAnomaly(),
                InclinationDeg = _inc() * RadToDeg,
                LanDeg = _lan() * RadToDeg,
                ArgumentOfPeriapsisDeg = _argPe() * RadToDeg,
                SurfaceAttitude = true,
            };
        }
        catch (Exception ex) when (ex is RPCException or System.InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Stops the game sending the readings. Runs on a worker thread; a stream already gone is fine.</summary>
    internal void Close()
    {
        foreach (var stream in _streams)
        {
            try { stream.Dispose(); }
            catch (Exception) { /* the game or vessel has gone: nothing to stop */ }
        }
        _streams.Clear();
    }

    private const double RadToDeg = 180 / Math.PI;

    private Func<T> Add<T>(Stream<T> stream)
    {
        _streams.Add(new Remover(stream.Remove));
        return stream.Get;
    }

    // kRPC hands many readings over as float; the log keeps doubles.
    private Func<double> AddFloat(Connection c, System.Linq.Expressions.Expression<Func<float>> read)
    {
        var get = Add(c.AddStream(read));
        return () => get();
    }

    private sealed class Remover(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}
