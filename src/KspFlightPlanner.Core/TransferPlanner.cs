using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

public enum ArrivalMode
{
    /// <summary>Burn into a circular orbit at the arrival altitude.</summary>
    Circularize,
    /// <summary>Use the destination's atmosphere to capture; no capture burn budgeted.</summary>
    Aerobrake,
    /// <summary>Fly past without capturing.</summary>
    Flyby,
}

public sealed record TransferRequest(
    CelestialBody Origin,
    CelestialBody Destination,
    CelestialBody Parent,
    double ParkingAltitudeM,
    double ArrivalAltitudeM,
    double EarliestDepartureUt,
    ArrivalMode Arrival = ArrivalMode.Circularize)
{
    /// <summary>
    /// The return leg: depart from this trip's destination orbit no earlier than
    /// <paramref name="earliestDepartureUt"/> and arrive back at the origin's parking orbit.
    /// </summary>
    public TransferRequest Reverse(double earliestDepartureUt, ArrivalMode arrival) =>
        new(Destination, Origin, Parent, ArrivalAltitudeM, ParkingAltitudeM, earliestDepartureUt, arrival);
}

/// <summary>Where to perform the ejection burn, relative to the origin body's prograde/retrograde direction.</summary>
public sealed record EjectionBurn(double AngleDeg, string Reference, double InclinationDeg)
{
    public override string ToString() => $"{AngleDeg:0.0}° before {Reference}";
}

/// <summary>Idealised transfer between circular, coplanar orbits (uses each body's semi-major axis).</summary>
public sealed record HohmannEstimate(
    double PhaseAngleDeg,
    double TransferTimeS,
    double SynodicPeriodS,
    double EjectionDv,
    double CaptureDv,
    EjectionBurn Ejection);

public sealed record TransferWindow(
    double DepartureUt,
    double ArrivalUt,
    double PhaseAngleDeg,
    double DepartureVInf,
    double ArrivalVInf,
    double EjectionDv,
    double CaptureDv,
    EjectionBurn Ejection)
{
    public double FlightTimeS => ArrivalUt - DepartureUt;
    public double TotalDv => EjectionDv + CaptureDv;
}

/// <summary>Δv for every (departure, flight time) pair searched. Values are NaN where no solution exists.</summary>
public sealed record PorkchopPlot(
    double DepartureStartUt,
    double DepartureEndUt,
    double FlightTimeMinS,
    double FlightTimeMaxS,
    int Columns,
    int Rows,
    double[] TotalDv)
{
    public double DepartureAt(int column) => DepartureStartUt + (DepartureEndUt - DepartureStartUt) * column / (Columns - 1);
    public double FlightTimeAt(int row) => FlightTimeMinS + (FlightTimeMaxS - FlightTimeMinS) * row / (Rows - 1);
    public double this[int column, int row] => TotalDv[row * Columns + column];
}

public sealed record TransferPlan(HohmannEstimate Hohmann, TransferWindow BestWindow, PorkchopPlot Porkchop);

/// <summary>
/// Plans an interplanetary transfer between two bodies orbiting the same parent.
/// The best window is found by solving Lambert's problem over a grid of departure times and
/// flight times (a "porkchop plot"), then refining around the cheapest point.
/// Δv figures assume a circular parking orbit and an Oberth-efficient burn at periapsis.
/// </summary>
public static class TransferPlanner
{
    private const double RadToDeg = 180 / Math.PI;

    public static TransferPlan Plan(TransferRequest request, int columns = 160, int rows = 120)
    {
        Validate(request);
        var hohmann = Hohmann(request);

        // One synodic period always contains a window; search a little past it.
        var depStart = request.EarliestDepartureUt;
        var depEnd = depStart + hohmann.SynodicPeriodS * 1.1;
        var tofMin = hohmann.TransferTimeS * 0.4;
        var tofMax = hohmann.TransferTimeS * 1.6;

        var grid = new double[columns * rows];
        Parallel.For(0, columns, c =>
        {
            var dep = depStart + (depEnd - depStart) * c / (columns - 1);
            var originState = StateOf(request.Origin, request.Parent, dep);
            for (var r = 0; r < rows; r++)
            {
                var tof = tofMin + (tofMax - tofMin) * r / (rows - 1);
                grid[r * columns + c] = Cost(request, originState, dep, tof);
            }
        });
        var porkchop = new PorkchopPlot(depStart, depEnd, tofMin, tofMax, columns, rows, grid);

        // Refine: repeatedly zoom a small grid around the best point.
        var best = BestCell(porkchop);
        double bestDep = porkchop.DepartureAt(best.Column), bestTof = porkchop.FlightTimeAt(best.Row);
        double depStep = (depEnd - depStart) / (columns - 1), tofStep = (tofMax - tofMin) / (rows - 1);
        var bestCost = porkchop[best.Column, best.Row];
        for (var iteration = 0; iteration < 8; iteration++)
        {
            double centreDep = bestDep, centreTof = bestTof;
            for (var i = -5; i <= 5; i++)
            for (var j = -5; j <= 5; j++)
            {
                var dep = Math.Max(depStart, centreDep + i * depStep / 5);
                var tof = Math.Max(1, centreTof + j * tofStep / 5);
                var cost = Cost(request, StateOf(request.Origin, request.Parent, dep), dep, tof);
                if (cost < bestCost) (bestCost, bestDep, bestTof) = (cost, dep, tof);
            }
            depStep /= 3;
            tofStep /= 3;
        }

        var window = Evaluate(request, bestDep, bestTof)
            ?? throw new InvalidOperationException("No transfer solution found.");
        return new TransferPlan(hohmann, window, porkchop);
    }

    public static HohmannEstimate Hohmann(TransferRequest request)
    {
        Validate(request);
        var mu = request.Parent.Mu;
        var r1 = request.Origin.Orbit!.SemiMajorAxisM;
        var r2 = request.Destination.Orbit!.SemiMajorAxisM;
        var aTransfer = (r1 + r2) / 2;

        var transferTime = Math.PI * Math.Sqrt(aTransfer * aTransfer * aTransfer / mu);
        var destPeriod = OrbitMath.Period(mu, r2);
        var originPeriod = OrbitMath.Period(mu, r1);
        var phase = NormalizeDeg(180 - 360 * transferTime / destPeriod);
        var synodic = 1 / Math.Abs(1 / originPeriod - 1 / destPeriod);

        var vInfDep = Math.Abs(OrbitMath.VisViva(mu, r1, aTransfer) - OrbitMath.CircularVelocity(mu, r1));
        var vInfArr = Math.Abs(OrbitMath.VisViva(mu, r2, aTransfer) - OrbitMath.CircularVelocity(mu, r2));
        var outward = r2 > r1;

        return new HohmannEstimate(
            phase,
            transferTime,
            synodic,
            EjectionDv(request, vInfDep),
            CaptureDv(request, vInfArr),
            EjectionBurnFor(request, vInfDep, outward ? 0 : Math.PI, 0));
    }

    /// <summary>Full transfer details for a specific departure time and flight time.</summary>
    public static TransferWindow? Evaluate(TransferRequest request, double departureUt, double flightTimeS)
    {
        var parent = request.Parent;
        var origin = StateOf(request.Origin, parent, departureUt);
        var dest = StateOf(request.Destination, parent, departureUt + flightTimeS);
        if (Lambert.Solve(origin.Position, dest.Position, flightTimeS, parent.Mu) is not { } solution) return null;

        var vInfDep = solution.DepartureVelocity - origin.Velocity;
        var vInfArr = solution.ArrivalVelocity - dest.Velocity;

        // Express the departure excess velocity in the origin's orbital frame.
        var prograde = origin.Velocity.Normalized;
        var normal = Vector3d.Cross(origin.Position, origin.Velocity).Normalized;
        var forward = Vector3d.Cross(normal, prograde);
        var inPlaneAngle = Math.Atan2(Vector3d.Dot(vInfDep, forward), Vector3d.Dot(vInfDep, prograde));
        var outOfPlane = Math.Asin(Math.Clamp(Vector3d.Dot(vInfDep.Normalized, normal), -1, 1));

        var destNow = StateOf(request.Destination, parent, departureUt);
        var radialOut = origin.Position.Normalized;
        var ahead = Vector3d.Cross(normal, radialOut);
        var phase = Math.Atan2(Vector3d.Dot(destNow.Position, ahead), Vector3d.Dot(destNow.Position, radialOut)) * RadToDeg;

        return new TransferWindow(
            departureUt,
            departureUt + flightTimeS,
            NormalizeDeg(phase),
            vInfDep.Length,
            vInfArr.Length,
            EjectionDv(request, vInfDep.Length),
            CaptureDv(request, vInfArr.Length),
            EjectionBurnFor(request, vInfDep.Length, inPlaneAngle, outOfPlane * RadToDeg));
    }

    /// <summary>Δv to leave a circular parking orbit with hyperbolic excess speed <paramref name="vInf"/>.</summary>
    public static double HyperbolicBurnDv(double mu, double orbitRadius, double vInf) =>
        OrbitMath.HyperbolicBurnDv(mu, orbitRadius, vInf);

    private static double EjectionDv(TransferRequest req, double vInf) =>
        HyperbolicBurnDv(req.Origin.Mu, req.Origin.EquatorialRadiusM + req.ParkingAltitudeM, vInf);

    private static double CaptureDv(TransferRequest req, double vInf) => req.Arrival switch
    {
        ArrivalMode.Circularize => HyperbolicBurnDv(req.Destination.Mu, req.Destination.EquatorialRadiusM + req.ArrivalAltitudeM, vInf),
        _ => 0,
    };

    /// <summary>
    /// The burn point is θ∞ before the escape asymptote, where θ∞ = acos(−1/e) is the hyperbola's
    /// true anomaly at infinity. Reported relative to prograde (outward transfers) or retrograde (inward).
    /// </summary>
    private static EjectionBurn EjectionBurnFor(TransferRequest req, double vInf, double asymptoteAngleRad, double inclinationDeg)
    {
        var rp = req.Origin.EquatorialRadiusM + req.ParkingAltitudeM;
        var e = 1 + rp * vInf * vInf / req.Origin.Mu;
        var thetaInf = Math.Acos(-1 / e);
        var burnAngleDeg = (asymptoteAngleRad - thetaInf) * RadToDeg;

        var outward = Math.Cos(asymptoteAngleRad) >= 0;
        var referenceDeg = outward ? 0 : 180;
        var before = ((referenceDeg - burnAngleDeg) % 360 + 360) % 360;
        return new EjectionBurn(before, outward ? "prograde" : "retrograde", inclinationDeg);
    }

    private static double Cost(TransferRequest req, StateVector origin, double dep, double tof)
    {
        var dest = StateOf(req.Destination, req.Parent, dep + tof);
        if (Lambert.Solve(origin.Position, dest.Position, tof, req.Parent.Mu) is not { } s) return double.NaN;
        var dv = EjectionDv(req, (s.DepartureVelocity - origin.Velocity).Length);
        if (req.Arrival == ArrivalMode.Circularize) dv += CaptureDv(req, (s.ArrivalVelocity - dest.Velocity).Length);
        return dv;
    }

    private static (int Column, int Row) BestCell(PorkchopPlot plot)
    {
        var best = (0, 0);
        var bestValue = double.PositiveInfinity;
        for (var r = 0; r < plot.Rows; r++)
        for (var c = 0; c < plot.Columns; c++)
        {
            var v = plot[c, r];
            if (v < bestValue) (bestValue, best) = (v, (c, r));
        }
        if (double.IsPositiveInfinity(bestValue)) throw new InvalidOperationException("No transfer solution found.");
        return best;
    }

    private static StateVector StateOf(CelestialBody body, CelestialBody parent, double ut) =>
        Kepler.StateAt(body.Orbit!, parent.Mu, ut);

    private static void Validate(TransferRequest req)
    {
        if (req.Origin.Orbit is null || req.Destination.Orbit is null)
            throw new ArgumentException("Both bodies need orbital data.");
        if (req.Origin.ParentId != req.Parent.Id || req.Destination.ParentId != req.Parent.Id)
            throw new ArgumentException("Origin and destination must orbit the same parent body.");
        if (req.Origin.Id == req.Destination.Id)
            throw new ArgumentException("Origin and destination must be different bodies.");
        if (req.ParkingAltitudeM <= 0 || req.ArrivalAltitudeM <= 0)
            throw new ArgumentException("Orbit altitudes must be above the surface.");
    }

    private static double NormalizeDeg(double deg) => ((deg + 180) % 360 + 360) % 360 - 180;
}
