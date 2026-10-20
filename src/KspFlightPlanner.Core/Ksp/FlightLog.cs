using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>
/// The flown vessel at one moment, as the flight log records it (read from the game through kRPC). Situation names are
/// the save's (LANDED, FLYING…). Altitudes above sea level; Ap/Pe NaN when the path doesn't have one.
/// </summary>
public sealed record FlightSample(
    double Ut,
    double MetS,
    string Body,
    string Situation,
    int Stage,
    double AltitudeM,
    double SurfaceSpeed,
    double OrbitalSpeed,
    double VerticalSpeed,
    double ApoapsisAltitudeM,
    double PeriapsisAltitudeM,
    double LatitudeDeg,
    double LongitudeDeg,
    double PitchDeg,
    double HeadingDeg,
    double Throttle,
    double MassKg,
    double ThrustN,
    double IspS,
    double DynamicPressurePa,
    double GForce,
    double WarpRate)
{
    // The orbit's shape and where along it the craft is (the orbit around Body), so a stretch on rails can be worked out
    // again (FlightLogs.FillGaps). NaN in logs recorded before they were kept.
    public double SemiMajorAxisM { get; init; } = double.NaN;
    public double Eccentricity { get; init; } = double.NaN;
    public double MeanAnomalyRad { get; init; } = double.NaN;

    // Which way the orbit faces, in the same frame as the bodies' orbits (the save's), so the craft can be placed in
    // space for the flight map. NaN in logs recorded before they were kept.
    public double InclinationDeg { get; init; } = double.NaN;
    public double LanDeg { get; init; } = double.NaN;
    public double ArgumentOfPeriapsisDeg { get; init; } = double.NaN;

    /// <summary>
    /// Pitch and heading are against the local horizon and north. Logs recorded before 2026-10-15 read them against the
    /// planet's own axes instead, which isn't a pitch at all; those samples have this false.
    /// </summary>
    public bool SurfaceAttitude { get; init; }

    public bool HasOrbit => !double.IsNaN(SemiMajorAxisM) && !double.IsNaN(Eccentricity) && !double.IsNaN(MeanAnomalyRad);

    /// <summary>The whole orbit (shape and which way it faces), with this moment as its epoch; null if not all of it was kept.</summary>
    public OrbitalElements? Elements => HasOrbit && !double.IsNaN(InclinationDeg) && !double.IsNaN(LanDeg) && !double.IsNaN(ArgumentOfPeriapsisDeg)
        ? new OrbitalElements(SemiMajorAxisM, Eccentricity, InclinationDeg, LanDeg, ArgumentOfPeriapsisDeg, MeanAnomalyRad, Ut)
        : null;
}

public enum FlightLogEventKind
{
    Launch,
    Joined,
    Stage,
    Situation,
    Soi,
    Warp,
    Disconnected,
    Reconnected,
    SwitchedAway,
    Returned,
    Quickload,
    Ended,
}

/// <summary>Something that happened in a flight, for the log's timeline.</summary>
/// <param name="DurationS">For a gap in the recording (warp, disconnect, switching away): how long it lasted.</param>
public sealed record FlightLogEvent(double Ut, double MetS, FlightLogEventKind Kind, string Text, double DurationS = 0);

public enum FlightLogOutcome
{
    /// <summary>The vessel is still out there (or the log hasn't seen it end yet).</summary>
    InFlight,

    /// <summary>Gone from the game after standing on the ground or in the water: recovered, most likely.</summary>
    Recovered,

    /// <summary>Gone from the game while flying: crashed or blown up.</summary>
    Lost,

    /// <summary>The game went back to before the launch.</summary>
    Reverted,
}

/// <summary>
/// One flight of one vessel, recorded while the planner was connected to the game. The rocket it belongs to is the
/// vessel's name at launch; a flight is told apart from others of the same name by its launch time (game time − MET).
/// </summary>
public sealed record FlightLog
{
    public long Id { get; init; }
    public string VesselName { get; init; } = "";
    public double LaunchUt { get; init; }

    /// <summary>MET when recording started: 0 from the launch, more when the planner joined a flight already under way.</summary>
    public double JoinedAtMetS { get; init; }

    public FlightLogOutcome Outcome { get; init; }

    /// <summary>The planner flight (Flight pane) being flown with this vessel when recording started, if any.</summary>
    public long? FlightId { get; init; }

    /// <summary>Parts the vessel had on the pad (with its launch clamps); null when not seen there.</summary>
    public int? LaunchPartCount { get; init; }

    /// <summary>Mass at the first sample after lift-off, kg (0 for a flight joined under way).</summary>
    public double LaunchMassKg => FromLaunch && Samples.Count > 0 ? Samples[0].MassKg : 0;

    public IReadOnlyList<FlightSample> Samples { get; init; } = [];
    public IReadOnlyList<FlightLogEvent> Events { get; init; } = [];

    public bool FromLaunch => JoinedAtMetS <= 0;
    public double LastUt => Samples.Count > 0 ? Samples[^1].Ut : LaunchUt + JoinedAtMetS;
    public double DurationS => LastUt - LaunchUt;
    public double DeltaVUsed => FlightLogMath.DeltaVUsed(Samples);
    public double MaxAltitudeM => Samples.Count > 0 ? Samples.Max(s => s.AltitudeM) : 0;
    public string? LastSituation => Samples.Count > 0 ? Samples[^1].Situation : null;
    public string? LastBody => Samples.Count > 0 ? Samples[^1].Body : null;

    /// <summary>The bodies flown around, in order: "Kerbin › Mun › Kerbin".</summary>
    public string Route => FlightLogs.Route(Samples);

    /// <summary>
    /// The log without samples taken on rails (stored by recordings before 2026-10-17), whose readings can be from
    /// different moments. The stretches they covered are filled from the orbit instead.
    /// </summary>
    public FlightLog WithoutRailsSamples() =>
        Samples.Any(s => s.WarpRate > FlightLogRecorder.PhysicsWarpMax)
            ? this with { Samples = Samples.Where(s => s.WarpRate <= FlightLogRecorder.PhysicsWarpMax).ToList() }
            : this;

    public FlightLogSummary Summary => new(Id, VesselName, LaunchUt, JoinedAtMetS, Outcome, LastUt, DeltaVUsed, MaxAltitudeM,
        LastSituation, LastBody, Events) { Route = Route, LaunchPartCount = LaunchPartCount, LaunchMassKg = LaunchMassKg };

    /// <summary>Whether this log is the flight of a vessel with this name and MET at game time <paramref name="ut"/>.</summary>
    public bool IsFlightOf(string vesselName, double ut, double metS) => vesselName == VesselName && LaunchedAt(ut, metS);

    /// <summary>Whether a vessel with this MET at game time <paramref name="ut"/> was launched with this log's flight, whatever it's called now.</summary>
    public bool LaunchedAt(double ut, double metS) => Math.Abs(ut - metS - LaunchUt) <= FlightLogs.SameLaunchS;
}

/// <summary>A flight log without its samples: what the Flight log list shows.</summary>
public sealed record FlightLogSummary(long Id, string VesselName, double LaunchUt, double JoinedAtMetS, FlightLogOutcome Outcome,
    double LastUt, double DeltaVUsed, double MaxAltitudeM, string? LastSituation, string? LastBody, IReadOnlyList<FlightLogEvent> Events)
{
    /// <summary>The bodies flown around, in order (null in logs stored before routes were).</summary>
    public string? Route { get; init; }

    public int? LaunchPartCount { get; init; }
    public double LaunchMassKg { get; init; }

    public bool FromLaunch => JoinedAtMetS <= 0;
    public double DurationS => LastUt - LaunchUt;
}

/// <summary>
/// Builds a <see cref="FlightLog"/> one sample at a time: notes staging, situation and SOI changes and time warp as events,
/// and thins coasting outside the air to one sample every <see cref="CoastEveryS"/>.
/// </summary>
public sealed class FlightLogRecorder
{
    /// <summary>While coasting outside the air with the engines off, one sample is kept this often (game seconds).</summary>
    public const double CoastEveryS = 10;

    /// <summary>Warp faster than this is on rails: the game skips physics, so the log only gets a sample now and then.</summary>
    public const double PhysicsWarpMax = 4;

    /// <summary>Warps shorter than this (game seconds) aren't noted: nothing much can happen in them.</summary>
    public const double NotableWarpS = 60;

    private readonly List<FlightSample> _samples;
    private readonly List<FlightLogEvent> _events;
    private FlightSample? _pending; // last sample read but not kept, kept after all if the next one is worth keeping
    private double _warpStartUt = double.NaN;
    private double _warpMax;
    private FlightLog _log;

    /// <summary>Starts a new log from the first sample of a vessel (at the launch, or joining a flight under way).</summary>
    public FlightLogRecorder(string vesselName, FlightSample first, long? flightId)
    {
        // KSP starts a vessel's MET again when it splits off another (a satellite released in orbit), so a MET near 0
        // above the air is a separation, not a launch.
        var justStarted = first.MetS < FlightLogs.SameLaunchS;
        var inSpace = first.Situation is "ORBITING" or "SUB_ORBITAL" or "ESCAPING";
        var fromLaunch = justStarted && !inSpace;
        _log = new FlightLog
        {
            VesselName = vesselName,
            LaunchUt = first.Ut - first.MetS,
            JoinedAtMetS = fromLaunch ? 0 : Math.Max(first.MetS, MinJoinedMetS),
            FlightId = flightId,
        };
        _samples = [];
        _events = [];
        var where = $"{first.Situation.ToLowerInvariant().Replace('_', ' ')} at {first.Body}";
        Note(first, fromLaunch ? FlightLogEventKind.Launch : FlightLogEventKind.Joined,
            fromLaunch ? $"Launch from {first.Body}"
            : inSpace && first.MetS < SeparatedWithinS ? $"Separated from another vessel ({where})"
            : $"Recording started in flight ({where})");
        Add(first);
    }

    /// <summary>A vessel first seen in space this soon after its MET started has just split off another.</summary>
    public const double SeparatedWithinS = 60;

    // A log joined at MET 0 would read as one from the launch (FromLaunch is JoinedAtMetS ≤ 0).
    private const double MinJoinedMetS = 0.01;

    /// <summary>Carries on a log stored earlier (after a disconnect, or coming back to the vessel).</summary>
    public FlightLogRecorder(FlightLog log)
    {
        _log = log;
        _samples = [.. log.Samples];
        _events = [.. log.Events];
    }

    /// <summary>The log so far.</summary>
    public FlightLog Log => _log with { Samples = _samples.ToList(), Events = _events.ToList() };

    public long Id { get => _log.Id; set => _log = _log with { Id = value }; }
    public string VesselName => _log.VesselName;
    public double LaunchUt => _log.LaunchUt;
    public double DeltaVUsed => FlightLogMath.DeltaVUsed(_samples);

    /// <summary>Notes how many parts the vessel had on the pad (the design's fingerprint, with the launch mass).</summary>
    public void SetLaunchPartCount(int parts)
    {
        _log = _log with { LaunchPartCount = parts };
        Dirty = true;
    }
    public FlightSample? Last => _pending ?? (_samples.Count > 0 ? _samples[^1] : null);

    /// <summary>Whether anything was added since <see cref="MarkSaved"/>.</summary>
    public bool Dirty { get; private set; } = true;

    public void MarkSaved() => Dirty = false;

    /// <summary>
    /// Adds a sample. On rails (warp faster than ×4) it's only watched for the warp note, not kept: the game then moves the
    /// craft along its orbit, so the stretch is worked out from the orbit afterwards (FlightAnalysis.FillGaps), and kRPC
    /// doesn't refresh every reading in the same frame there, so a sample's time, MET and orbit can be from different
    /// moments far apart. The last sample before the warp is kept, to fill from.
    /// </summary>
    public void Add(FlightSample sample)
    {
        if (sample.WarpRate > PhysicsWarpMax)
        {
            Flush();
            TrackWarp(sample);
            return;
        }
        var kept = _samples.Count > 0 ? _samples[^1] : null;
        if (kept is not null && sample.Ut < kept.Ut - 1) Rewind(sample);
        kept = _samples.Count > 0 ? _samples[^1] : null;
        var previous = _pending ?? kept;
        if (previous is not null && sample.Ut <= previous.Ut) return; // the game is paused (e.g. the recovery dialog): nothing new

        if (previous is not null)
        {
            if (sample.Stage != previous.Stage) Note(sample, FlightLogEventKind.Stage, $"Stage {sample.Stage}");
            if (sample.Body != previous.Body) Note(sample, FlightLogEventKind.Soi, $"Entered {sample.Body}'s sphere of influence");
            if (sample.Situation != previous.Situation) Note(sample, FlightLogEventKind.Situation, SituationText(sample.Situation, sample.Body));
        }
        TrackWarp(sample);

        if (kept is null || Changing(sample, previous!))
        {
            // Keep the sample before too, so a burn starts from where the coast left off.
            if (_pending is not null) _samples.Add(_pending);
            _samples.Add(sample);
            _pending = null;
        }
        else if (sample.Ut - kept.Ut >= CoastEveryS)
        {
            _samples.Add(sample);
            _pending = null;
        }
        else _pending = sample;
        Dirty = true;
    }

    /// <summary>Adds an event at the last sample (e.g. the link dropped).</summary>
    public void Note(FlightLogEventKind kind, string text, double durationS = 0)
    {
        if (Last is { } last) Note(last, kind, text, durationS);
    }

    /// <summary>Keeps the sample held back while coasting, so the log ends where the vessel was last seen.</summary>
    public void Flush()
    {
        if (_pending is null) return;
        _samples.Add(_pending);
        _pending = null;
        Dirty = true;
    }

    /// <summary>Ends the flight.</summary>
    public void End(FlightLogOutcome outcome, string text)
    {
        Flush();
        _log = _log with { Outcome = outcome };
        Note(FlightLogEventKind.Ended, text);
        Dirty = true;
    }

    private void Note(FlightSample at, FlightLogEventKind kind, string text, double durationS = 0)
    {
        _events.Add(new FlightLogEvent(at.Ut, at.MetS, kind, text, durationS));
        Dirty = true;
    }

    /// <summary>The game went back in time (a quicksave loaded): what came after it never happened.</summary>
    private void Rewind(FlightSample sample)
    {
        _pending = null;
        _samples.RemoveAll(s => s.Ut > sample.Ut);
        _events.RemoveAll(e => e.Ut > sample.Ut && e.Kind is not (FlightLogEventKind.Launch or FlightLogEventKind.Joined));
        _warpStartUt = double.NaN;
        Note(sample, FlightLogEventKind.Quickload, "Loaded a quicksave: the flight after this point was dropped");
    }

    private void TrackWarp(FlightSample sample)
    {
        if (sample.WarpRate > PhysicsWarpMax)
        {
            if (double.IsNaN(_warpStartUt)) { _warpStartUt = sample.Ut; _warpMax = 0; }
            _warpMax = Math.Max(_warpMax, sample.WarpRate);
        }
        else if (!double.IsNaN(_warpStartUt))
        {
            var length = sample.Ut - _warpStartUt;
            if (length >= NotableWarpS)
                Note(sample, FlightLogEventKind.Warp, $"Time warp ×{_warpMax:#,0} for {FlightLogs.Duration(length)}: path worked out from the orbit", length);
            _warpStartUt = double.NaN;
        }
    }

    /// <summary>
    /// Dynamic pressure above this (Pa) is flying through air. Standing still on the ground still reads a trace
    /// (1.5 × 10⁻⁷ Pa), which isn't.
    /// </summary>
    public const double InAirPa = 1;

    /// <summary>Burning, in the air, or something changed: every sample is kept.</summary>
    private static bool Changing(FlightSample s, FlightSample previous) =>
        s.ThrustN > 0 || previous.ThrustN > 0
        || s.DynamicPressurePa > InAirPa || previous.DynamicPressurePa > InAirPa
        || s.Stage != previous.Stage || s.Situation != previous.Situation || s.Body != previous.Body
        || Math.Abs(s.MassKg - previous.MassKg) > 1e-3 * previous.MassKg;

    private static string SituationText(string situation, string body) => situation switch
    {
        "FLYING" => "Flying",
        "SUB_ORBITAL" => "Sub-orbital",
        "ORBITING" => $"In orbit of {body}",
        "ESCAPING" => $"Escaping {body}",
        "LANDED" => $"Landed on {body}",
        "SPLASHED" => $"Splashed down on {body}",
        "DOCKED" => "Docked",
        "PRELAUNCH" => "On the pad",
        _ => situation,
    };
}

/// <summary>Δv spent between samples. docs/MATH.md §8.5.</summary>
public static class FlightLogMath
{
    /// <summary>
    /// A mass drop more than this many times what the engines could have burned is parts falling off, not fuel.
    /// </summary>
    public const double DroppedPartsFactor = 1.5;

    /// <summary>Δv spent over the whole log, m/s.</summary>
    public static double DeltaVUsed(IReadOnlyList<FlightSample> samples)
    {
        var total = 0.0;
        for (var i = 1; i < samples.Count; i++) total += DeltaVBetween(samples[i - 1], samples[i]);
        return total;
    }

    /// <summary>
    /// Δv spent from <paramref name="a"/> to <paramref name="b"/>. With the engines running and only fuel gone, the rocket
    /// equation on the mass drop: g₀·Isp·ln(m_a/m_b), exact however far apart the samples are. When parts came off too
    /// (staging, or more mass gone than the engines could burn), thrust ÷ mass over the time between them instead.
    /// </summary>
    public static double DeltaVBetween(FlightSample a, FlightSample b)
    {
        if (a.ThrustN <= 0 && b.ThrustN <= 0) return 0;
        var dt = b.Ut - a.Ut;
        if (dt <= 0 || a.MassKg <= 0 || b.MassKg <= 0) return 0;
        var accel = (a.ThrustN / a.MassKg + b.ThrustN / b.MassKg) / 2 * dt;

        var isp = a.IspS > 0 && b.IspS > 0 ? (a.IspS + b.IspS) / 2 : Math.Max(a.IspS, b.IspS);
        if (a.Stage == b.Stage && isp > 0 && b.MassKg >= a.MassKg) return 0; // no fuel gone: the engines were only just lit or cut
        if (a.Stage != b.Stage || isp <= 0 || b.MassKg >= a.MassKg) return accel;
        var couldBurn = (a.ThrustN + b.ThrustN) / 2 / (isp * VesselAnalysis.G0) * dt;
        if (a.MassKg - b.MassKg > DroppedPartsFactor * couldBurn + 1) return accel;
        return VesselAnalysis.G0 * isp * Math.Log(a.MassKg / b.MassKg);
    }
}

/// <summary>Ending flights from what the save file says.</summary>
public static class FlightLogs
{
    /// <summary>Launch times this close (s) are the same launch: game time and MET are read a frame or two apart.</summary>
    public const double SameLaunchS = 2;

    /// <summary>
    /// An orbit that puts the craft further than this share of its distance from the body's centre from where its altitude
    /// does isn't the craft's orbit. Readings that do follow the craft agree to within 0.02 %.
    /// </summary>
    public const double OrbitFitTolerance = 0.01;

    /// <summary>
    /// Whether a sample's orbit readings have stopped following the craft. Streams opened while the game is putting a
    /// vessel back together (just after a quickload) can get the orbit it had for a moment then (around Kerbin, whatever
    /// it's really around), and that orbit never moves again. Two signs: the orbit puts the craft at another distance from
    /// the body's centre than its altitude does, or game time moved on and the mean anomaly didn't. On rails the readings
    /// can be from different moments, and on the pad the craft isn't going anywhere, so those aren't judged.
    /// docs/MATH.md §8.5.
    /// </summary>
    /// <param name="previous">The reading before, from the same streams (null for the first).</param>
    /// <param name="bodyRadiusM">The radius of the body the streams were opened against.</param>
    public static bool OrbitStale(FlightSample? previous, FlightSample now, double bodyRadiusM)
    {
        if (!now.HasOrbit || now.WarpRate > FlightLogRecorder.PhysicsWarpMax || now.Situation == "PRELAUNCH") return false;
        if (previous is { HasOrbit: true } p && p.WarpRate <= FlightLogRecorder.PhysicsWarpMax && now.Ut > p.Ut
            && now.MeanAnomalyRad == p.MeanAnomalyRad)
            return true;
        var (a, e, m) = (now.SemiMajorAxisM, now.Eccentricity, now.MeanAnomalyRad);
        var fromOrbit = e < 1
            ? a * (1 - e * Math.Cos(Kepler.SolveEccentricAnomaly(m, e)))
            : a * (1 - e * Math.Cosh(Kepler.SolveHyperbolicAnomaly(m, e)));
        var fromAltitude = bodyRadiusM + now.AltitudeM;
        if (!double.IsFinite(fromOrbit) || fromAltitude <= 0) return false; // can't judge (a parabola, say)
        return Math.Abs(fromOrbit - fromAltitude) > OrbitFitTolerance * fromAltitude;
    }

    /// <summary>
    /// How a flight still open in the log ended, judged from the save file KSP last wrote, or null if it's still going (or
    /// the save is older than the log). A flight with no vessel in the save launched when it was is gone: recovered if it
    /// was last seen on the ground or in the water, else lost. A save from before the launch means the flight was reverted.
    /// </summary>
    public static FlightLogOutcome? EndedIn(FlightLog log, KspSave file)
    {
        if (log.Outcome != FlightLogOutcome.InFlight) return null;
        if (file.Ut < log.LaunchUt - SameLaunchS) return FlightLogOutcome.Reverted;
        if (file.Ut < log.LastUt - SameLaunchS) return null; // written before the log's last sample: it can't tell yet
        if (StillIn(log, file)) return null;
        return log.LastSituation is "LANDED" or "SPLASHED" or "PRELAUNCH" ? FlightLogOutcome.Recovered : FlightLogOutcome.Lost;
    }

    /// <summary>
    /// Whether the save still has the log's vessel: one launched when it was (lct), under the log's name, or renamed but not
    /// debris, or the only one launched then. Not Ut − met: KSP only moves a vessel's met on while it's loaded, so a vessel
    /// left behind while the player flew another one and warped has a met that's days short of the file's time.
    /// </summary>
    private static bool StillIn(FlightLog log, KspSave file)
    {
        var launched = file.Vessels.Where(v => Math.Abs(LaunchUt(v, file) - log.LaunchUt) <= SameLaunchS).ToList();
        return launched.Count == 1 || launched.Any(v => v.Name == log.VesselName || v.Type != "Debris");
    }

    /// <summary>When a saved vessel was launched: its lct, or the file's time less its met in a save without one.</summary>
    private static double LaunchUt(SavedVessel v, KspSave file) => v.LaunchUt > 0 ? v.LaunchUt : file.Ut - v.MissionTimeS;

    /// <summary>The bodies a flight was around, in order, each once per visit: "Kerbin › Mun › Kerbin".</summary>
    public static string Route(IEnumerable<FlightSample> samples)
    {
        var route = new List<string>();
        foreach (var s in samples)
            if (route.Count == 0 || route[^1] != s.Body) route.Add(s.Body);
        return string.Join(" › ", route);
    }

    /// <summary>"42 s", "3m 20s", or the calendar's "2h 05m" / "3d 1h 00m" for longer.</summary>
    public static string Duration(double seconds) =>
        seconds < 60 ? $"{Math.Max(0, seconds):0} s"
        : seconds < 3600 ? $"{Math.Floor(seconds / 60):0}m {Math.Floor(seconds % 60):00}s"
        : GameCalendar.Kerbin.FormatDuration(seconds);

    public static string EndText(FlightLogOutcome outcome) => outcome switch
    {
        FlightLogOutcome.Recovered => "Recovered",
        FlightLogOutcome.Lost => "Lost",
        FlightLogOutcome.Reverted => "Reverted",
        _ => "",
    };
}
