using System.Globalization;
using System.Numerics;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>
/// KSP's drag rules from Physics.cfg (docs/ROCKETS-AND-MISSIONS.md, “A craft's drag from its parts”): two overall multipliers and the curves that turn a drag cube
/// face into drag, by Mach number and by which way the face points.
/// </summary>
public sealed record AeroPhysics(
    double DragMultiplier,
    double DragCubeMultiplier,
    FloatCurve Tip,
    FloatCurve Surface,
    FloatCurve Tail,
    FloatCurve Multiplier,
    FloatCurve Cd,
    FloatCurve CdPower,
    FloatCurve PseudoReynolds)
{
    /// <summary>Stock KSP 1.12's values, for when the install's Physics.cfg can't be read.</summary>
    public static readonly AeroPhysics Stock = new(8, 0.1,
        FloatCurve.FromKeys("0 1 0 0", "0.85 1.19 0.6960422 0.6960422", "1.1 2.83 0.730473 0.730473", "5 4 0 0"),
        FloatCurve.FromKeys("0 0.02 0 0", "0.85 0.02 0 0", "0.9 0.0152439 -0.07942077 -0.07942077", "1.1 0.0025 -0.005279571 -0.001936768",
            "2 0.002083333 -2.314833E-05 -2.314833E-05", "5 0.003333333 -0.000180556 -0.000180556", "25 0.001428571 -7.14286E-05 0"),
        FloatCurve.FromKeys("0 1 0 0", "0.85 1 0 0", "1.1 0.25 -0.02215106 -0.02487721", "1.4 0.22 -0.03391732 -0.03391732",
            "5 0.15 -0.001198566 -0.001198566", "25 0.14 0 0"),
        FloatCurve.FromKeys("0 0.5 0 0", "0.85 0.5 0 0", "1.1 1.3 0 -0.008100224", "2 0.7 -0.1104858 -0.1104858", "5 0.6 0 0",
            "10 0.85 0.02198264 0.02198264", "14 0.9 0.007694946 0.007694946", "25 0.95 0 0"),
        FloatCurve.FromKeys("0.05 0.0025 0.15 0.15", "0.4 0.15 0.3963967 0.3963967", "0.7 0.35 0.9066986 0.9066986", "0.75 0.45 3.213604 3.213604",
            "0.8 0.66 3.49833 3.49833", "0.85 0.8 2.212924 2.212924", "0.9 0.89 1.1 1.1", "1 1 1 1"),
        FloatCurve.FromKeys("0 1 0 0.00715953", "0.85 1.25 0.7780356 0.7780356", "1.1 2.5 0.2492796 0.2492796", "5 3 0 0"),
        FloatCurve.FromKeys("0 4 0 -2975.412", "0.0001 3 -251.1479 -251.1479", "0.01 2 -19.63584 -19.63584", "0.1 1.2 -0.7846036 -0.7846036",
            "1 1 0 0", "100 1 0 0", "200 0.82 0 0", "500 0.86 0.0001932119 0.0001932119", "1000 0.9 1.54299E-05 1.54299E-05", "10000 0.95 0 0"));

    /// <summary>Reads Physics.cfg; anything missing keeps the stock value.</summary>
    public static AeroPhysics Parse(string text)
    {
        var root = ConfigNode.Parse(text);
        FloatCurve Curve(string name, FloatCurve stock) => root.GetNode(name) is { } n && n.Values.Any(v => v.Key == "key") ? FloatCurve.FromNode(n) : stock;
        double Value(string key, double stock) =>
            double.TryParse(root.GetValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : stock;
        var s = Stock;
        return new AeroPhysics(Value("dragMultiplier", s.DragMultiplier), Value("dragCubeMultiplier", s.DragCubeMultiplier),
            Curve("DRAG_TIP", s.Tip), Curve("DRAG_SURFACE", s.Surface), Curve("DRAG_TAIL", s.Tail), Curve("DRAG_MULTIPLIER", s.Multiplier),
            Curve("DRAG_CD", s.Cd), Curve("DRAG_CD_POWER", s.CdPower), Curve("DRAG_PSEUDOREYNOLDS", s.PseudoReynolds));
    }

    /// <summary>
    /// How much a face's drag counts for, by which way it points: <paramref name="facing"/> is 1 pointing into the airflow
    /// (the tip curve), ½ side-on (surface), 0 pointing away (tail); blended linearly between, times the Mach multiplier.
    /// </summary>
    public double FacingMultiplier(double facing, double mach)
    {
        var surface = Surface.Evaluate(mach);
        var blend = facing <= 0.5
            ? Tail.Evaluate(mach) + (surface - Tail.Evaluate(mach)) * facing * 2
            : surface + (Tip.Evaluate(mach) - surface) * (facing * 2 - 1);
        return blend * Multiplier.Evaluate(mach);
    }

    /// <summary>A face's drag coefficient from its cube value: the Cd curve, raised to the Mach-dependent power.</summary>
    public double FaceCd(double cubeDrag, double mach) => Math.Pow(Math.Max(0, Cd.Evaluate(cubeDrag)), CdPower.Evaluate(mach));
}

/// <summary>
/// One of a part's drag cubes (KSP measures them when it loads): for each face (+X, −X, +Y, −Y, +Z, −Z in the part's own
/// axes) its area (m²) and a drag value from 0 to 1. Parts that change shape (panels, chutes, airbrakes) have one per shape.
/// </summary>
public sealed record DragCube(string Name, double[] Area, double[] Drag)
{
    public static readonly Vector3[] FaceNormals = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];

    /// <summary>"cube = Default, a,d,depth × 6, centre x,y,z, size x,y,z"; null when it doesn't read.</summary>
    public static DragCube? Parse(string value)
    {
        var parts = value.Split(',');
        if (parts.Length < 19) return null;
        var n = parts.Skip(1).Select(s => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
        var area = new double[6];
        var drag = new double[6];
        for (var f = 0; f < 6; f++)
        {
            (area[f], drag[f]) = (n[f * 3], n[f * 3 + 1]);
            if (!double.IsFinite(area[f]) || !double.IsFinite(drag[f])) return null;
        }
        return new DragCube(parts[0].Trim(), area, drag);
    }
}

/// <summary>A part's drag cubes and the way each of its stack nodes points (in the part's own axes).</summary>
public sealed record PartAero(string Name, IReadOnlyList<DragCube> Cubes, IReadOnlyDictionary<string, Vector3> StackNodes)
{
    // The shape a part has while flying folded up: stowed panels, a packed chute, a neutral airbrake.
    private static readonly string[] Stowed = ["Default", "", "neutral", "RETRACTED", "Retracted", "Clean", "PACKED", "A", "TypeA"];

    /// <summary>The cube for the part as it usually flies (see <see cref="Stowed"/>), else its first.</summary>
    public DragCube? Cube => Stowed.Select(n => Cubes.FirstOrDefault(c => c.Name == n)).FirstOrDefault(c => c is not null) ?? Cubes.FirstOrDefault();
}

/// <summary>Everything needed to work out a craft's drag: KSP's rules and every part's drag cubes.</summary>
public sealed record AeroData(AeroPhysics Physics, IReadOnlyDictionary<string, PartAero> Parts, string Source)
{
    public PartAero? Part(string name) => Parts.GetValueOrDefault(PartCatalog.NormalizeName(name));
}

/// <summary>
/// Reads drag data from a KSP install (docs/ROCKETS-AND-MISSIONS.md, “A craft's drag from its parts”): Physics.cfg for the rules, PartDatabase.cfg (written by KSP
/// in the install folder each time it loads) for the drag cubes, and the part configs (ModuleManager's cache when there is
/// one) for stack nodes and for cubes a config writes out itself.
/// </summary>
public static class AeroDataReader
{
    public const string PartDatabaseFile = "PartDatabase.cfg";

    /// <summary>Reads the install's data; null when it has no PartDatabase.cfg (KSP has never been run there).</summary>
    public static AeroData? Read(string installPath)
    {
        var database = Path.Combine(installPath, PartDatabaseFile);
        if (!File.Exists(database)) return null;
        var physicsFile = Path.Combine(installPath, "Physics.cfg");
        var physics = File.Exists(physicsFile) ? AeroPhysics.Parse(ReadShared(physicsFile)) : AeroPhysics.Stock;

        var cubes = new Dictionary<string, List<DragCube>>();
        foreach (var part in ConfigNode.Parse(ReadShared(database)).GetNodes("PART"))
        {
            var url = part.GetValue("url") ?? "";
            var name = PartCatalog.NormalizeName(url[(url.LastIndexOf('/') + 1)..]);
            var list = Cubes(part);
            if (name.Length > 0 && list.Count > 0) cubes[name] = list;
        }

        var nodes = new Dictionary<string, Dictionary<string, Vector3>>();
        foreach (var part in PartConfigs(installPath))
        {
            if (part.GetValue("name") is not { Length: > 0 } raw) continue;
            var name = PartCatalog.NormalizeName(raw);
            nodes[name] = StackNodes(part);
            if (!cubes.ContainsKey(name) && Cubes(part) is { Count: > 0 } own) cubes[name] = own;
        }

        var parts = cubes.Keys.Union(nodes.Keys).ToDictionary(n => n,
            n => new PartAero(n, cubes.GetValueOrDefault(n) ?? [], nodes.GetValueOrDefault(n) ?? new Dictionary<string, Vector3>()));
        return new AeroData(physics, parts, database);
    }

    private static List<DragCube> Cubes(ConfigNode part) =>
        part.GetNodes("DRAG_CUBE").SelectMany(c => c.Values.Where(v => v.Key == "cube")).Select(v => DragCube.Parse(v.Value)).OfType<DragCube>().ToList();

    /// <summary>"node_stack_bottom = 0, -0.318, 0, 0, -1, 0, 2" → bottom points (0, −1, 0).</summary>
    public static Dictionary<string, Vector3> StackNodes(ConfigNode part)
    {
        var result = new Dictionary<string, Vector3>();
        foreach (var (key, value) in part.Values)
        {
            if (!key.StartsWith("node_stack_", StringComparison.Ordinal)) continue;
            var n = value.Split(',').Select(s => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : float.NaN).ToArray();
            if (n.Length < 6 || n.Skip(3).Take(3).Any(float.IsNaN)) continue;
            var dir = new Vector3(n[3], n[4], n[5]);
            if (dir.LengthSquared() > 1e-6f) result[key["node_stack_".Length..]] = Vector3.Normalize(dir);
        }
        return result;
    }

    /// <summary>Every PART config: from ModuleManager's cache when there is one, else each .cfg under GameData.</summary>
    private static IEnumerable<ConfigNode> PartConfigs(string installPath)
    {
        var gameData = Path.Combine(installPath, "GameData");
        var cache = Path.Combine(gameData, PartCatalogReader.ModuleManagerCache);
        if (File.Exists(cache))
            return ConfigNode.Parse(ReadShared(cache)).Nodes.SelectMany(n => n.Name == "PART" ? [n] : n.GetNodes("PART"));
        if (!Directory.Exists(gameData)) return [];
        return Directory.EnumerateFiles(gameData, "*.cfg", SearchOption.AllDirectories).SelectMany(f =>
        {
            try { return ConfigNode.Parse(ReadShared(f)).GetNodes("PART").ToList(); }
            catch (IOException) { return []; }
        });
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
