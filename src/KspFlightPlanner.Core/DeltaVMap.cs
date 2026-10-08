namespace KspFlightPlanner.Core;

/// <summary>A stop on the Δv map (a body's surface, low orbit, intercept, ...).</summary>
public sealed record DvMapNode
{
    public long Id { get; init; }
    public required string Key { get; init; }
    public long? ParentId { get; init; }
    public required string BodyName { get; init; }
    public required string Kind { get; init; }
    public required string Label { get; init; }

    /// <summary>Δv of the edge to the parent node (same in both directions), after user edits.</summary>
    public double DvFromParent { get; init; }

    /// <summary>The published map value for the edge to the parent, before user edits.</summary>
    public double MapDvFromParent { get; init; }

    public bool IsOverridden => DvFromParent != MapDvFromParent;

    /// <summary>Extra Δv for the worst-case plane change on the edge to the parent.</summary>
    public double PlaneChangeDv { get; init; }

    /// <summary>Aerobraking can replace the burn when travelling parent → this node (away from Kerbin).</summary>
    public bool AerobrakeDown { get; init; }

    /// <summary>Aerobraking can replace the burn when travelling this node → parent (toward Kerbin).</summary>
    public bool AerobrakeUp { get; init; }

    /// <summary>
    /// Δv still needed on the aerobraking direction of this edge when aerobraking is used (0 for map edges;
    /// the deorbit burn for custom orbits, since only the circularization is replaced by the atmosphere).
    /// </summary>
    public double AerobrakeResidualDv { get; init; }

    public bool JetEnginesPossible { get; init; }
    public string? FlightTime { get; init; }

    /// <summary>Altitude for low/synchronous/custom orbits, when known.</summary>
    public double? AltitudeM { get; init; }

    /// <summary>
    /// Distance of this stop from the centre of its parent node's body, used to order the map by distance:
    /// low/custom orbits by radius, moons and planets by their orbit's semi-major axis, the star itself as 0,
    /// and a body's escape (SOI-edge) node at its SOI radius.
    /// </summary>
    public double? OrbitRadiusM { get; init; }

    /// <summary>Semi-major axis of this node's body around its own parent (e.g. Moho ≈ 5.26 Gm), for labels.</summary>
    public double? BodyOrbitRadiusM { get; init; }

    /// <summary>For user-defined locations: the custom_locations row id.</summary>
    public long? CustomLocationId { get; init; }
    public bool IsCustom => CustomLocationId is not null;

    public string DisplayName => $"{BodyName} — {Label}";
    public override string ToString() => DisplayName;
}

/// <summary>One edge of a route, in the direction of travel.</summary>
/// <param name="EdgeKey">Key of the child node that owns this edge; use it to edit the edge's Δv.</param>
/// <param name="AerobrakeDv">Δv still needed on this leg when aerobraking (usually 0).</param>
public sealed record RouteLeg(
    DvMapNode From, DvMapNode To, string EdgeKey, double DeltaV, double MapDeltaV,
    double PlaneChangeDv, bool CanAerobrake, string? FlightTime, double AerobrakeDv = 0)
{
    public bool IsOverridden => DeltaV != MapDeltaV;
    public override string ToString() => $"{From.DisplayName} → {To.DisplayName}";
}

/// <summary>
/// The Δv map as a tree rooted at Kerbin's surface. Routes between any two nodes go up to
/// their common ancestor and back down.
/// </summary>
public sealed class DeltaVMap
{
    private readonly Dictionary<long, DvMapNode> _byId;
    private readonly Dictionary<string, DvMapNode> _byKey;
    private readonly ILookup<long?, DvMapNode> _children;

    public DeltaVMap(IEnumerable<DvMapNode> nodes)
    {
        Nodes = nodes.ToList();
        _byId = Nodes.ToDictionary(n => n.Id);
        _byKey = Nodes.ToDictionary(n => n.Key);
        _children = Nodes.ToLookup(n => n.ParentId);
    }

    public IReadOnlyList<DvMapNode> Nodes { get; }
    public DvMapNode Root => _children[null].Single();

    public DvMapNode this[string key] => _byKey[key];
    public bool TryGet(string key, out DvMapNode node) => _byKey.TryGetValue(key, out node!);
    public IEnumerable<DvMapNode> ChildrenOf(DvMapNode node) => _children[node.Id];
    public DvMapNode? ParentOf(DvMapNode node) => node.ParentId is { } pid ? _byId[pid] : null;

    /// <summary>A copy with edge Δv replaced for the given child-node keys; other edges keep their map values.</summary>
    public DeltaVMap WithOverrides(IReadOnlyDictionary<string, double> dvByKey) =>
        new(Nodes.Select(n => n with { DvFromParent = dvByKey.TryGetValue(n.Key, out var dv) ? dv : n.MapDvFromParent }));

    public IReadOnlyList<RouteLeg> Route(DvMapNode from, DvMapNode to)
    {
        var fromChain = Ancestry(from);
        var toChain = Ancestry(to);
        var common = fromChain.First(n => toChain.Contains(n));

        var legs = new List<RouteLeg>();
        // Up: from → ... → common. Each step uses the moving node's edge to its parent.
        foreach (var node in fromChain.TakeWhile(n => n != common))
            legs.Add(new RouteLeg(node, _byId[node.ParentId!.Value], node.Key, node.DvFromParent, node.MapDvFromParent,
                node.PlaneChangeDv, node.AerobrakeUp, node.FlightTime, node.AerobrakeUp ? node.AerobrakeResidualDv : 0));
        // Down: common → ... → to.
        foreach (var node in toChain.TakeWhile(n => n != common).Reverse())
            legs.Add(new RouteLeg(_byId[node.ParentId!.Value], node, node.Key, node.DvFromParent, node.MapDvFromParent,
                node.PlaneChangeDv, node.AerobrakeDown, node.FlightTime, node.AerobrakeDown ? node.AerobrakeResidualDv : 0));
        return legs;
    }

    /// <summary>The node followed by its ancestors up to the root.</summary>
    private List<DvMapNode> Ancestry(DvMapNode node)
    {
        var chain = new List<DvMapNode> { node };
        while (chain[^1].ParentId is { } pid) chain.Add(_byId[pid]);
        return chain;
    }
}
