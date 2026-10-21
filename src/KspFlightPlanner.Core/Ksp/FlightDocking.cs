namespace KspFlightPlanner.Core.Ksp;

/// <summary>
/// Telling dockings and undockings apart from two listings of the game's vessels, one from before and one from after.
/// KSP merges two vessels that dock into one of them (the other is gone from the game), and makes a new vessel of the
/// part that undocks. docs/MATH.md §8.14.
/// </summary>
public static class FlightDocking
{
    /// <summary>
    /// A mass jump above this share of the vessel's mass (and <see cref="MinJumpKg"/>) from one reading to the next, with no
    /// staging, is another vessel joining it (or leaving it, for a drop). Burning fuel or mining ore never moves the mass
    /// this fast.
    /// </summary>
    public const double JumpShare = 0.002;

    /// <summary>The smallest mass change taken as a docking or undocking, kg.</summary>
    public const double MinJumpKg = 20;

    /// <summary>Readings further apart than this (game seconds) can't tell a jump from a slow change.</summary>
    public const double JumpWithinS = 3;

    /// <summary>
    /// Vessels this close in orbit size (share of the semi-major axis) and tilt (degrees) around the same body can be
    /// alongside each other. Rendezvous leaves a few m/s between them, a fraction of a percent in orbit size.
    /// </summary>
    public const double NearSmaShare = 0.05, NearInclinationDeg = 2;

    /// <summary>
    /// Whether the mass went up (+1) or down (−1) by a docking or undocking from one reading to the next, else 0. A jump up
    /// counts whatever the stage does: the docked stack's stage count is both vessels' (the station's went from 1 to 2 when
    /// its electrical module docked, 2026-10-21), and staging never adds mass. A drop with a change of stage is staging.
    /// </summary>
    public static int MassJump(FlightSample before, FlightSample after)
    {
        if (after.Ut <= before.Ut || after.Ut - before.Ut > JumpWithinS) return 0;
        if (before.WarpRate > FlightLogRecorder.PhysicsWarpMax || after.WarpRate > FlightLogRecorder.PhysicsWarpMax) return 0;
        if (before.Situation == "PRELAUNCH" || after.Situation == "PRELAUNCH") return 0;
        var change = after.MassKg - before.MassKg;
        var threshold = Math.Max(MinJumpKg, JumpShare * before.MassKg);
        return change > threshold ? 1 : change < -threshold && after.Stage == before.Stage ? -1 : 0;
    }

    /// <summary>
    /// Whether two vessels could be alongside each other: around the same body on much the same orbit, or both on the
    /// ground (the listing has no position for those).
    /// </summary>
    public static bool Near(LiveVessel a, LiveVessel b)
    {
        if (a.Orbit is not { } oa || b.Orbit is not { } ob) return a.Orbit is null && b.Orbit is null;
        if (oa.BodyName != ob.BodyName) return false;
        if (!double.IsFinite(oa.SemiMajorAxisM) || !double.IsFinite(ob.SemiMajorAxisM)) return false;
        return Math.Abs(oa.SemiMajorAxisM - ob.SemiMajorAxisM) <= NearSmaShare * Math.Abs(oa.SemiMajorAxisM)
            && Math.Abs(oa.InclinationDeg - ob.InclinationDeg) <= NearInclinationDeg;
    }

    /// <summary>
    /// The vessels gone from the game between the two listings that were alongside <paramref name="hostId"/>: the ones that
    /// docked into it (it's the one KSP kept). The kept vessel has to be in both listings: after a quickload every vessel
    /// can come back with a new id, which isn't a docking.
    /// </summary>
    public static IReadOnlyList<LiveVessel> Gone(IReadOnlyList<LiveVessel> before, IReadOnlyList<LiveVessel> after, ulong hostId)
    {
        var host = before.FirstOrDefault(v => v.KrpcId == hostId);
        if (host is null || after.All(v => v.KrpcId != hostId)) return [];
        var still = after.Select(v => v.KrpcId).ToHashSet();
        return before.Where(v => v.KrpcId != hostId && !still.Contains(v.KrpcId) && Near(v, host)).ToList();
    }

    /// <summary>
    /// The vessels new in the game between the two listings, alongside <paramref name="hostId"/>: the ones that came off it.
    /// It has to be in both listings (see <see cref="Gone"/>).
    /// </summary>
    public static IReadOnlyList<LiveVessel> Appeared(IReadOnlyList<LiveVessel> before, IReadOnlyList<LiveVessel> after, ulong hostId)
    {
        var host = after.FirstOrDefault(v => v.KrpcId == hostId);
        if (host is null || before.All(v => v.KrpcId != hostId)) return [];
        var was = before.Select(v => v.KrpcId).ToHashSet();
        return after.Where(v => v.KrpcId != hostId && !was.Contains(v.KrpcId) && Near(v, host)).ToList();
    }

    /// <summary>
    /// The open log of a vessel that's still a vessel of its own: the one last recorded with its kRPC id, else the latest
    /// one under its name (now or at launch). Null when it has none (launched before the planner was recording, say).
    /// </summary>
    /// <param name="krpcIdOf">The kRPC id of the vessel each log was last recorded with, where known.</param>
    /// <param name="nameNow">The name a log's vessel goes by in the save now, where known (it may have been renamed).</param>
    public static FlightLogSummary? LogOf(LiveVessel vessel, IEnumerable<FlightLogSummary> logs,
        IReadOnlyDictionary<long, ulong> krpcIdOf, Func<FlightLogSummary, string?>? nameNow = null)
    {
        var open = logs.Where(l => l.Outcome == FlightLogOutcome.InFlight && !FlightLogs.Away(l.Events)).ToList();
        return open.FirstOrDefault(l => krpcIdOf.TryGetValue(l.Id, out var id) && id == vessel.KrpcId)
            ?? open.Where(l => l.VesselName == vessel.Name || nameNow?.Invoke(l) == vessel.Name).MaxBy(l => l.LastUt);
    }

    /// <summary>
    /// A vessel first seen outside space within a minute of its MET starting: a new launch (which may share a name with a
    /// vessel docked somewhere), not one that undocked. Same rule as a log started from the launch.
    /// </summary>
    public static bool JustLaunched(FlightSample first) =>
        first.MetS < FlightLogRecorder.SeparatedWithinS && first.Situation is not ("ORBITING" or "SUB_ORBITAL" or "ESCAPING");

    /// <summary>
    /// The flight a vessel that has come off another one carries on: an away log (<see cref="FlightLogs.Away"/>) going by
    /// the vessel's name, preferring one docked into <paramref name="cameOffLogId"/>'s vessel. Null when there's none.
    /// </summary>
    public static FlightLogSummary? AwayLogNamed(string name, IEnumerable<FlightLogSummary> logs, long? cameOffLogId = null)
    {
        var away = logs.Where(l => l.Outcome == FlightLogOutcome.InFlight && FlightLogs.Away(l.Events)
            && FlightLogs.AwayName(l.VesselName, l.Events) == name).ToList();
        return away.FirstOrDefault(l => cameOffLogId is not null && l.DockedIn?.PartnerLogId == cameOffLogId)
            ?? away.MaxBy(l => l.LastUt);
    }
}
