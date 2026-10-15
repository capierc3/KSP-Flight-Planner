namespace KspFlightPlanner.Core.Ksp;

/// <summary>A node of the R&amp;D tech tree (an RDNode in GameData).</summary>
/// <param name="Cost">Science points to research it.</param>
/// <param name="X">Where the R&amp;D building draws it: x to the right, y up.</param>
/// <param name="Parents">Nodes that lead to it.</param>
/// <param name="AnyToUnlock">One researched parent is enough; otherwise every parent is needed.</param>
public sealed record TechNode(string Id, string Title, string? Description, double Cost, double X, double Y, IReadOnlyList<string> Parents,
    bool AnyToUnlock);

/// <summary>
/// What a save has researched (its ResearchAndDevelopment scenario). Each researched node lists the parts the player can
/// build from it: in career only the ones bought, in science mode all of them.
/// </summary>
/// <param name="Science">Science points in the bank.</param>
public sealed record SavedResearch(double Science, IReadOnlySet<string> ResearchedTech, IReadOnlySet<string> UnlockedParts)
{
    public bool IsUnlocked(KspPart part) => UnlockedParts.Contains(part.Name);
}

/// <summary>Where a tech node stands for a save and its research plan.</summary>
public enum TechStatus
{
    /// <summary>Researched in the save.</summary>
    Researched,
    /// <summary>Not researched, but in the plan.</summary>
    Planned,
    /// <summary>Can be researched now: its parents are researched.</summary>
    Available,
    /// <summary>Its parents aren't researched yet.</summary>
    Locked,
}

/// <summary>
/// A research plan: the nodes the player means to research next, on top of what the save has. Planning a node brings
/// in the nodes it needs (the cheapest way through for nodes that need any one parent); unplanning one drops the planned
/// nodes that relied on it.
/// </summary>
public sealed class TechPlan
{
    private readonly IReadOnlyDictionary<string, TechNode> _nodes;
    private readonly IReadOnlySet<string> _researched;

    public TechPlan(IEnumerable<TechNode> tree, IReadOnlySet<string> researched, IEnumerable<string> planned)
    {
        _nodes = tree.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        _researched = researched;
        Planned = Prune(planned.Where(id => _nodes.ContainsKey(id) && !researched.Contains(id)).ToHashSet());
    }

    /// <summary>Planned nodes not yet researched.</summary>
    public IReadOnlySet<string> Planned { get; private set; }

    /// <summary>Science the planned nodes cost in all.</summary>
    public double PlannedCost => Planned.Sum(id => _nodes[id].Cost);

    public TechStatus Status(string id) =>
        _researched.Contains(id) ? TechStatus.Researched
        : Planned.Contains(id) ? TechStatus.Planned
        : _nodes.TryGetValue(id, out var node) && Reachable(node, _researched) ? TechStatus.Available
        : TechStatus.Locked;

    /// <summary>Researched, or will be once the plan is done.</summary>
    public bool Has(string id) => _researched.Contains(id) || Planned.Contains(id);

    /// <summary>Plans the node, or unplans it if it's already planned. Researched nodes stay as they are.</summary>
    public void Toggle(string id)
    {
        if (_researched.Contains(id) || !_nodes.ContainsKey(id)) return;
        if (Planned.Contains(id))
        {
            var without = Planned.ToHashSet();
            without.Remove(id);
            Planned = Prune(without);
        }
        else
        {
            var with = Planned.ToHashSet();
            with.UnionWith(Needed(id, new HashSet<string>(_researched.Concat(Planned))).Nodes);
            Planned = with;
        }
    }

    /// <summary>
    /// The nodes to research to get <paramref name="id"/>, given what's already <paramref name="have"/> — the node itself
    /// and any missing parents, choosing the cheapest parent when one is enough.
    /// </summary>
    private (HashSet<string> Nodes, double Cost) Needed(string id, HashSet<string> have)
    {
        if (have.Contains(id) || !_nodes.TryGetValue(id, out var node)) return ([], 0);
        var parents = node.Parents.Where(_nodes.ContainsKey).ToList();
        var nodes = new HashSet<string> { id };
        if (parents.Count > 0)
        {
            if (node.AnyToUnlock)
            {
                if (!parents.Any(have.Contains))
                    nodes.UnionWith(parents.Select(p => Needed(p, have)).MinBy(n => n.Cost).Nodes);
            }
            else
            {
                foreach (var p in parents) nodes.UnionWith(Needed(p, have).Nodes);
            }
        }
        return (nodes, nodes.Sum(n => _nodes[n].Cost));
    }

    /// <summary>Drops planned nodes whose parents won't be there, until every one left can be reached.</summary>
    private HashSet<string> Prune(IReadOnlySet<string> planned)
    {
        var kept = planned.ToHashSet();
        bool removed;
        do
        {
            var have = new HashSet<string>(_researched.Concat(kept));
            removed = kept.RemoveWhere(id => !Reachable(_nodes[id], have)) > 0;
        } while (removed);
        return kept;
    }

    private bool Reachable(TechNode node, IReadOnlySet<string> have)
    {
        var parents = node.Parents.Where(_nodes.ContainsKey).ToList();
        return parents.Count == 0 || (node.AnyToUnlock ? parents.Any(have.Contains) : parents.All(have.Contains));
    }
}
