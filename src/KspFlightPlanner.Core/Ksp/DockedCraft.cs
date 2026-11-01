namespace KspFlightPlanner.Core.Ksp;

/// <summary>
/// One vessel that docked into a stack: its parts (cut off at the docking port), and the name and type it had before it
/// docked. <see cref="Craft"/> is that piece as a vessel of its own, at the stack's place and orbit.
/// </summary>
/// <param name="HasCommand">It has a crew pod or probe core.</param>
/// <param name="HasEngine">It has an engine of its own.</param>
public sealed record DockedPiece(string Name, string Type, SavedVessel Craft, bool HasCommand, bool HasEngine)
{
    /// <summary>
    /// Whether the piece counts as a craft of its own unless the player says otherwise: a ship, lander, rover or plane
    /// with a command part and an engine. Station modules, fuel tanks and other parts brought up on a probe core don't,
    /// even with an engine (they flew themselves up): their type says what they are.
    /// </summary>
    public bool IsCraftByDefault => DockedCraft.CraftTypes.Contains(Type) && HasCommand && HasEngine;

    /// <summary>The player's choice when there is one (true: its own craft; false: part of the stack), else the default.</summary>
    public bool IsCraft(bool? choice) => choice ?? IsCraftByDefault;
}

/// <summary>
/// Telling the vessels in a docked stack apart. KSP merges vessels that dock into one, but each docking port keeps the
/// vessel it came with (DOCKEDVESSEL: its name, type and root part). Cutting the part tree at every docked port pair
/// gives back one piece per vessel that docked. docs/SAVE-AND-LIVE-GAME.md §4.
/// </summary>
public static class DockedCraft
{
    /// <summary>Vessel types that are craft of their own when docked (with a command part and an engine).</summary>
    public static readonly IReadOnlySet<string> CraftTypes = new HashSet<string>(StringComparer.Ordinal) { "Ship", "Lander", "Rover", "Plane" };

    /// <summary>The save id given to a piece of a docked stack: the stack's id and the piece's root part.</summary>
    public static string PieceId(string stackId, string rootUid) => $"{stackId}#{rootUid}";

    /// <summary>
    /// The vessels docked into <paramref name="stack"/> other than the one it's named for (the one holding its root part),
    /// in part order. Empty when nothing is docked.
    /// </summary>
    public static IReadOnlyList<DockedPiece> Pieces(SavedVessel stack)
    {
        var parts = stack.Parts;
        if (!parts.Any(p => p.DockingPorts.Any(d => d.IsDocked))) return [];

        // Join each part to its parent, except across a docked pair of ports: those are the joins docking made.
        var group = Enumerable.Range(0, parts.Count).ToArray();
        int Find(int i) { while (group[i] != i) i = group[i] = group[group[i]]; return i; }
        for (var i = 0; i < parts.Count; i++)
        {
            var parent = parts[i].ParentIndex;
            if (parent == i || parent < 0 || parent >= parts.Count || DockedPair(parts[i], parts[parent])) continue;
            group[Find(i)] = Find(parent);
        }

        var rootGroup = Find(Math.Clamp(stack.RootIndex, 0, parts.Count - 1));
        var pieces = new List<DockedPiece>();
        foreach (var members in Enumerable.Range(0, parts.Count).GroupBy(Find).Where(g => g.Key != rootGroup))
        {
            var indices = members.ToList();
            var uids = indices.Select(i => parts[i].Uid).ToHashSet();
            var ports = indices.SelectMany(i => parts[i].DockingPorts).Where(d => d.CameWith is not null).ToList();
            // The port that remembers this piece's own root part names it; another port here may remember a stack it
            // docked onto later.
            var info = ports.Select(d => d.CameWith!).FirstOrDefault(c => uids.Contains(c.RootUid)) ?? ports.Select(d => d.CameWith!).FirstOrDefault();
            var rootUid = info is not null && uids.Contains(info.RootUid) ? info.RootUid : parts[indices[0]].Uid;

            // The piece's own parts, re-numbered, hanging from its root.
            var renumber = indices.Select((old, i) => (old, i)).ToDictionary(x => x.old, x => x.i);
            var root = indices.FindIndex(i => parts[i].Uid == rootUid);
            var own = indices.Select(i => parts[i] with
            {
                ParentIndex = renumber.TryGetValue(parts[i].ParentIndex, out var p) && !DockedPair(parts[i], parts[parts[i].ParentIndex]) ? p : renumber[i],
            }).ToList();
            var name = info?.Name ?? $"{stack.Name} (docked part)";
            var type = info?.Type ?? "";
            var craft = stack with
            {
                Id = PieceId(stack.Id, rootUid),
                Name = name,
                Type = type,
                Parts = own,
                RootIndex = Math.Max(0, root),
                DockedNames = new HashSet<string>(),
                DockedInId = stack.Id,
            };
            pieces.Add(new DockedPiece(name, type, craft, own.Any(p => p.IsCommand), own.Any(p => p.Engines.Count > 0)));
        }
        return pieces;
    }

    /// <summary>Two parts joined by docking: a port on one is docked to the other.</summary>
    private static bool DockedPair(SavedPart a, SavedPart b) =>
        a.DockingPorts.Any(d => d.IsDocked && d.DockedToUid == b.Uid) || b.DockingPorts.Any(d => d.IsDocked && d.DockedToUid == a.Uid);

    /// <summary>
    /// The save's vessels and, after each docked stack, the craft docked in it (by default, or by
    /// <paramref name="choice"/>: the player's say on a piece, by name; null for the default).
    /// </summary>
    public static IEnumerable<SavedVessel> WithDockedCraft(this KspSave save, Func<string, bool?>? choice = null)
    {
        foreach (var vessel in save.Vessels)
        {
            yield return vessel;
            foreach (var piece in Pieces(vessel))
                if (piece.IsCraft(choice?.Invoke(piece.Name))) yield return piece.Craft;
        }
    }

    /// <summary>
    /// A vessel by save id, or a piece of a docked stack by its <see cref="PieceId"/> (whatever it counts as). A piece that
    /// has since undocked is found as its own vessel (KSP keeps its root part, so the part's uid carries over), and one
    /// docked somewhere else as a piece of that stack. Null if gone.
    /// </summary>
    public static SavedVessel? FindVessel(this KspSave save, string id)
    {
        if (save.Vessels.FirstOrDefault(v => v.Id == id) is { } vessel) return vessel;
        var hash = id.IndexOf('#');
        if (hash <= 0) return null;
        if (save.Vessels.FirstOrDefault(v => v.Id == id[..hash]) is { } stack && Pieces(stack).FirstOrDefault(p => p.Craft.Id == id) is { } piece)
            return piece.Craft;
        var rootUid = id[(hash + 1)..];
        if (save.Vessels.FirstOrDefault(v => v.RootIndex < v.Parts.Count && v.Parts[v.RootIndex].Uid == rootUid) is { } undocked)
            return undocked;
        return save.Vessels.Where(v => v.Parts.Any(p => p.DockingPorts.Any(d => d.IsDocked)))
            .SelectMany(Pieces).FirstOrDefault(p => p.Craft.Id.EndsWith("#" + rootUid, StringComparison.Ordinal))?.Craft;
    }
}
