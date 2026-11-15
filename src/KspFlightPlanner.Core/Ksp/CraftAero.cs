using System.Numerics;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>Which way the craft points into the airflow.</summary>
public enum AeroAttitude
{
    /// <summary>Engines into the airflow (holding retrograde), as most craft aerobrake.</summary>
    EnginesFirst,
    /// <summary>Nose into the airflow (holding prograde).</summary>
    NoseFirst,
    /// <summary>Side-on to the airflow: the most drag the craft can make.</summary>
    SideOn,
}

/// <summary>
/// A craft's drag from its parts' drag cubes, the way KSP adds it up (docs/MATH.md §9.2): every face of every part, its
/// area less what the parts stacked on it cover, times how much drag a face pointing that way makes at that Mach number.
/// Lift from wings is left out.
/// </summary>
public sealed class CraftAero
{
    private readonly AeroPhysics _physics;
    private readonly List<(Vector3 Normal, double Area, double Drag)> _faces;

    private CraftAero(AeroPhysics physics, List<(Vector3, double, double)> faces, Vector3 nose, int partCount, IReadOnlyList<string> missing)
    {
        _physics = physics;
        _faces = faces;
        Nose = nose;
        PartCount = partCount;
        MissingParts = missing;
    }

    /// <summary>The way the craft's nose points, in the craft's axes (its command part's "up").</summary>
    public Vector3 Nose { get; }

    public int PartCount { get; }

    /// <summary>Parts with no drag data (procedural parts, or parts KSP hasn't loaded since they were installed).</summary>
    public IReadOnlyList<string> MissingParts { get; }

    /// <summary>A part as the drag model needs it: its id, catalog name, rotation in the craft, and stack links.</summary>
    public sealed record ShapePart(string Id, string Name, Quaternion Rotation, IReadOnlyList<PartLink> Links);

    public static CraftAero FromCraft(CraftFile craft, AeroData data) =>
        Build(craft.Parts.Select(p => new ShapePart(p.Id, p.Name, p.Rotation, p.StackLinks)).ToList(), 0, data);

    public static CraftAero FromVessel(SavedVessel vessel, AeroData data)
    {
        var parts = vessel.Parts.Select(p => new ShapePart(p.Uid, p.Name, p.Rotation, p.StackLinks)).ToList();
        var command = vessel.Parts.ToList().FindIndex(p => p.IsCommand);
        return Build(parts, command >= 0 ? command : Math.Clamp(vessel.RootIndex, 0, Math.Max(0, parts.Count - 1)), data);
    }

    /// <param name="noseIndex">The part whose "up" is the craft's nose.</param>
    public static CraftAero Build(IReadOnlyList<ShapePart> parts, int noseIndex, AeroData data)
    {
        var missing = new List<string>();
        var byId = new Dictionary<string, (ShapePart Part, PartAero? Aero, double[] Area)>();
        foreach (var p in parts)
        {
            var aero = data.Part(p.Name);
            var cube = aero?.Cube;
            if (cube is null && !missing.Contains(p.Name)) missing.Add(p.Name);
            byId.TryAdd(p.Id, (p, aero, cube is null ? new double[6] : (double[])cube.Area.Clone()));
        }

        // Stacked parts hide each other's touching faces: each pair once, the smaller of the two areas off both.
        var done = new HashSet<(string, string)>();
        foreach (var (id, a) in byId)
        {
            foreach (var link in a.Part.Links)
            {
                if (!byId.TryGetValue(link.PartId, out var b) || !done.Add(Pair(id, link.PartId))) continue;
                var faceA = NodeFace(a.Aero, link.Node);
                var back = b.Part.Links.FirstOrDefault(l => l.PartId == id);
                var faceB = back is not null ? NodeFace(b.Aero, back.Node)
                    : Facing(b.Part.Rotation, -Vector3.Transform(DragCube.FaceNormals[faceA], a.Part.Rotation));
                var overlap = Math.Min(a.Area[faceA], b.Area[faceB]);
                a.Area[faceA] -= overlap;
                b.Area[faceB] -= overlap;
            }
        }

        var faces = new List<(Vector3, double, double)>();
        foreach (var (_, a) in byId)
        {
            if (a.Aero?.Cube is not { } cube) continue;
            for (var f = 0; f < 6; f++)
                if (a.Area[f] > 1e-6)
                    faces.Add((Vector3.Normalize(Vector3.Transform(DragCube.FaceNormals[f], a.Part.Rotation)), a.Area[f], cube.Drag[f]));
        }
        var nose = parts.Count == 0 ? Vector3.UnitY : Vector3.Normalize(Vector3.Transform(Vector3.UnitY, parts[Math.Clamp(noseIndex, 0, parts.Count - 1)].Rotation));
        return new CraftAero(data.Physics, faces, nose, parts.Count, missing);
    }

    /// <summary>
    /// What's left of a design once every stage has gone: the parts no decoupler drops (sepI −1), and docking ports
    /// (KSP counts each as separating itself).
    /// </summary>
    public static CraftFile FinalStage(CraftFile craft) =>
        craft with { Parts = craft.Parts.Where(p => p.SeparationStage < 0 || p.Name.Contains("docking", StringComparison.OrdinalIgnoreCase)).ToList() };

    /// <summary>A design's mass with full tanks (t): each part's catalog mass and options, and what it carries.</summary>
    public static double MassT(CraftFile craft, PartCatalog catalog) =>
        craft.Parts.Sum(p => (catalog.Part(p.Name)?.MassT ?? 0) + p.ExtraMassT + p.Resources.Sum(r => r.Amount * catalog.Density(r.Name)));

    private static (string, string) Pair(string a, string b) => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);

    /// <summary>The cube face a stack node sits on: the node's direction from the part config, else top = +Y, bottom = −Y.</summary>
    private static int NodeFace(PartAero? aero, string node)
    {
        if (aero?.StackNodes.TryGetValue(node, out var dir) == true) return Facing(Quaternion.Identity, dir);
        return node.Contains("bottom", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
    }

    /// <summary>The face (of a part with this rotation) whose outward normal is closest to <paramref name="direction"/>.</summary>
    private static int Facing(Quaternion rotation, Vector3 direction)
    {
        var (best, bestDot) = (0, double.MinValue);
        for (var f = 0; f < 6; f++)
        {
            var dot = Vector3.Dot(Vector3.Transform(DragCube.FaceNormals[f], rotation), direction);
            if (dot > bestDot) (best, bestDot) = (f, dot);
        }
        return best;
    }

    /// <summary>
    /// The drag area (m²) with the craft moving along <paramref name="velocity"/> (in the craft's axes) at a Mach number:
    /// drag force = dynamic pressure × this × the thin-air multiplier (<see cref="AeroPhysics.PseudoReynolds"/>).
    /// </summary>
    public double DragArea(double mach, Vector3 velocity)
    {
        var v = Vector3.Normalize(velocity);
        var sum = 0.0;
        foreach (var (normal, area, drag) in _faces)
            sum += area * _physics.FacingMultiplier((Vector3.Dot(normal, v) + 1) / 2, mach) * _physics.FaceCd(drag, mach);
        return sum * _physics.DragCubeMultiplier * _physics.DragMultiplier;
    }

    public double DragArea(double mach, AeroAttitude attitude) => attitude switch
    {
        AeroAttitude.NoseFirst => DragArea(mach, Nose),
        AeroAttitude.EnginesFirst => DragArea(mach, -Nose),
        _ => Sideways().Max(d => DragArea(mach, d)),
    };

    /// <summary>Twelve directions square to the nose, to find the broadest side.</summary>
    private IEnumerable<Vector3> Sideways()
    {
        var helper = Math.Abs(Nose.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(Nose, helper));
        var w = Vector3.Cross(Nose, u);
        for (var i = 0; i < 12; i++)
        {
            var angle = i * Math.PI / 12;
            yield return u * (float)Math.Cos(angle) + w * (float)Math.Sin(angle);
        }
    }

    /// <summary>The thin-air multiplier on drag at this density × speed (kg/m²/s).</summary>
    public double PseudoReynolds(double densityTimesSpeed) => _physics.PseudoReynolds.Evaluate(densityTimesSpeed);

    /// <summary>Drag area by Mach number for one attitude, every ½ Mach to Mach 30, looked up in a straight line between.</summary>
    public DragTable Table(AeroAttitude attitude) =>
        new(Enumerable.Range(0, 61).Select(i => DragArea(i * 0.5, attitude)).ToArray(), 0.5, _physics.PseudoReynolds);
}

/// <summary>A craft's drag area by Mach number, with KSP's thin-air multiplier (docs/MATH.md §9.2).</summary>
public sealed class DragTable(double[] areas, double step, FloatCurve? pseudoReynolds)
{
    /// <summary>The same drag area at every speed, with no thin-air multiplier: for a drag area typed in.</summary>
    public static DragTable Constant(double area) => new([area, area], 1000, null);

    /// <summary>Drag area (m²) at a Mach number.</summary>
    public double At(double mach)
    {
        var x = Math.Clamp(mach / step, 0, areas.Length - 1);
        var i = Math.Min((int)x, areas.Length - 2);
        return areas[i] + (areas[i + 1] - areas[i]) * (x - i);
    }

    /// <summary>The same table with every drag area times <paramref name="factor"/> (to allow for the drag being off).</summary>
    public DragTable Scaled(double factor) => new(areas.Select(a => a * factor).ToArray(), step, pseudoReynolds);

    /// <summary>The thin-air multiplier at density × speed; 1 without the curve.</summary>
    public double ThinAir(double densityTimesSpeed) => pseudoReynolds?.Evaluate(densityTimesSpeed) ?? 1;
}
