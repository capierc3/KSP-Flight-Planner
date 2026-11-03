using System.Globalization;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>
/// A KSP curve (FloatCurve): keys of "time value [inTangent outTangent]", read the way Unity's AnimationCurve reads
/// them (cubic Hermite between keys, flat beyond the ends). Keys without tangents get the slope to their neighbours.
/// </summary>
public sealed class FloatCurve
{
    private readonly (double T, double V, double In, double Out)[] _keys;

    public FloatCurve(IEnumerable<(double T, double V, double? In, double? Out)> keys)
    {
        var k = keys.OrderBy(x => x.T).ToArray();
        _keys = new (double, double, double, double)[k.Length];
        for (var i = 0; i < k.Length; i++)
        {
            var slope = Slope(k, i);
            _keys[i] = (k[i].T, k[i].V, k[i].In ?? slope, k[i].Out ?? slope);
        }
    }

    private static double Slope((double T, double V, double? In, double? Out)[] k, int i)
    {
        if (k.Length < 2) return 0;
        var (a, b) = (Math.Max(0, i - 1), Math.Min(k.Length - 1, i + 1));
        return k[b].T > k[a].T ? (k[b].V - k[a].V) / (k[b].T - k[a].T) : 0;
    }

    /// <summary>Reads the "key = t v [in out]" lines of a curve node.</summary>
    public static FloatCurve FromNode(ConfigNode node) => new(node.Values.Where(v => v.Key == "key").Select(v => Key(v.Value)).OfType<(double, double, double?, double?)>());

    /// <summary>Keys written as in a config: "0 1 0 0", "0.85 1.19 0.69 0.69", …</summary>
    public static FloatCurve FromKeys(params string[] keys) => new(keys.Select(Key).OfType<(double, double, double?, double?)>());

    private static (double, double, double?, double?)? Key(string text)
    {
        var n = text.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
        if (n.Length < 2 || n.Take(2).Any(double.IsNaN)) return null;
        return n.Length >= 4 && !double.IsNaN(n[2]) && !double.IsNaN(n[3]) ? (n[0], n[1], n[2], n[3]) : (n[0], n[1], null, null);
    }

    public double Evaluate(double t)
    {
        if (_keys.Length == 0) return 0;
        if (t <= _keys[0].T) return _keys[0].V;
        if (t >= _keys[^1].T) return _keys[^1].V;
        var i = 1;
        while (_keys[i].T < t) i++;
        var (a, b) = (_keys[i - 1], _keys[i]);
        var dt = b.T - a.T;
        var s = (t - a.T) / dt;
        var (s2, s3) = (s * s, s * s * s);
        return (2 * s3 - 3 * s2 + 1) * a.V + (s3 - 2 * s2 + s) * a.Out * dt + (-2 * s3 + 3 * s2) * b.V + (s3 - s2) * b.In * dt;
    }
}
