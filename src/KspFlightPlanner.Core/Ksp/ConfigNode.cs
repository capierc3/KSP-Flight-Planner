namespace KspFlightPlanner.Core.Ksp;

/// <summary>
/// KSP's text format for saves (.sfs), craft files (.craft) and part configs: nested <c>NAME { key = value }</c>
/// blocks. Keys can repeat, so values are kept in order.
/// </summary>
public sealed class ConfigNode(string name)
{
    public string Name { get; } = name;
    public List<KeyValuePair<string, string>> Values { get; } = [];
    public List<ConfigNode> Nodes { get; } = [];

    /// <summary>First value with this key, or null.</summary>
    public string? GetValue(string key)
    {
        foreach (var (k, v) in Values)
            if (k == key) return v;
        return null;
    }

    public ConfigNode? GetNode(string name) => Nodes.FirstOrDefault(n => n.Name == name);

    public IEnumerable<ConfigNode> GetNodes(string name) => Nodes.Where(n => n.Name == name);

    /// <summary>
    /// Parses a whole file. The result is an unnamed root whose children are the top-level nodes (e.g. GAME).
    /// Accepts the brace on its own line (as KSP writes it) or after the name, and ignores // comments.
    /// </summary>
    public static ConfigNode Parse(string text)
    {
        var root = new ConfigNode("");
        var stack = new Stack<ConfigNode>();
        stack.Push(root);
        string? pendingName = null;

        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            var comment = line.IndexOf("//");
            if (comment >= 0) line = line[..comment].TrimEnd();
            if (line.IsEmpty) continue;

            var equals = line.IndexOf('=');
            if (equals > 0)
            {
                stack.Peek().Values.Add(new(line[..equals].Trim().ToString(), line[(equals + 1)..].Trim().ToString()));
                continue;
            }

            if (line.SequenceEqual("}"))
            {
                pendingName = null;
                if (stack.Count > 1) stack.Pop();
                continue;
            }

            string? openName = null;
            if (line.SequenceEqual("{")) openName = pendingName ?? "";
            else if (line.EndsWith("{")) openName = line[..^1].Trim().ToString();

            if (openName is not null)
            {
                var node = new ConfigNode(openName);
                stack.Peek().Nodes.Add(node);
                stack.Push(node);
                pendingName = null;
            }
            else
            {
                pendingName = line.ToString();
            }
        }
        return root;
    }
}
