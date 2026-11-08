namespace KspFlightPlanner.Core.Ksp;

/// <summary>
/// Which vessel each linked location follows now. A vessel that docks or undocks can come back under a new save id (KSP
/// gives the piece that comes off a new <c>pid</c>, and so does the add-on's rails undock), so a link whose id has gone
/// from the save is moved to the vessel it became, when that's clear. docs/SAVE-AND-LIVE-GAME.md §5.
/// </summary>
public static class VesselRelink
{
    /// <summary>
    /// The vessel each of <paramref name="linked"/> follows in <paramref name="now"/>, by location id. One whose id is
    /// still there keeps it. One whose id has gone moves to, in order: the vessel or docked piece with its root part (a
    /// piece id names it; otherwise the vessel as <paramref name="before"/> had it does), else the one vessel in
    /// <paramref name="craft"/> with its name and type that no other location follows (and, when it vanished since
    /// <paramref name="before"/>, that is new since then). Two lost locations pointing at the same vessel both stay lost.
    /// Locations not in the result can't be found.
    /// </summary>
    /// <param name="now">The save as read now.</param>
    /// <param name="craft">Its vessels with docked craft split out (<see cref="DockedCraft.WithDockedCraft"/>).</param>
    /// <param name="before">The previous reading of the save, if any: what the lost vessel was.</param>
    public static IReadOnlyDictionary<long, SavedVessel> Follow(IEnumerable<CustomLocation> linked, KspSave now,
        IReadOnlyList<SavedVessel> craft, KspSave? before)
    {
        var byId = craft.Where(v => v.Id.Length > 0).GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.First());
        var result = new Dictionary<long, SavedVessel>();
        var lost = new List<CustomLocation>();
        foreach (var location in linked)
        {
            if (location.KspVesselId is not { Length: > 0 } id) continue;
            if (byId.TryGetValue(id, out var vessel) || (vessel = now.FindVessel(id)) is not null && vessel.Id == id)
                result[location.Id] = vessel;
            else
                lost.Add(location);
        }
        if (lost.Count == 0) return result;

        var taken = result.Values.Select(v => v.Id).ToHashSet();
        var beforeIds = before is null ? null : AllIds(before);
        var found = lost.Select(l => (Location: l, Vessel: Match(l, now, craft, before, beforeIds, taken)))
            .Where(p => p.Vessel is not null).ToList();
        foreach (var group in found.GroupBy(p => p.Vessel!.Id).Where(g => g.Count() == 1))
            result[group.Single().Location.Id] = group.Single().Vessel!;
        return result;
    }

    private static SavedVessel? Match(CustomLocation location, KspSave now, IReadOnlyList<SavedVessel> craft, KspSave? before,
        HashSet<string>? beforeIds, HashSet<string> taken)
    {
        var id = location.KspVesselId!;
        SavedVessel? Free(SavedVessel? v) => v is not null && !taken.Contains(v.Id) ? v : null;

        // A docked piece's id carries its root part: FindVessel follows that part off the stack, or onto another.
        if (id.Contains('#') && Free(now.FindVessel(id)) is { } piece) return piece;

        // A whole vessel: its root part, as the last reading had it, is in the craft it became.
        var old = before?.FindVessel(id);
        if (old is not null && RootUid(old) is { } rootUid && Free(WithRoot(now, rootUid)) is { } same) return same;

        // Else by name and type: the one unfollowed craft of that name (new since the last reading, when that had it).
        var name = old?.Name ?? location.Name;
        var matches = craft.Where(v => v.Name == name && (old is null || v.Type == old.Type) && !taken.Contains(v.Id)
                                       && (old is null || beforeIds?.Contains(v.Id) != true))
            .Take(2).ToList();
        return matches is [var only] ? only : null;
    }

    /// <summary>The vessel whose root part has this uid, else the docked piece rooted at it. Part uids are unique in a save.</summary>
    private static SavedVessel? WithRoot(KspSave save, string rootUid) =>
        save.Vessels.FirstOrDefault(v => RootUid(v) == rootUid)
        ?? save.Vessels.SelectMany(DockedCraft.Pieces).FirstOrDefault(p => p.Craft.Id.EndsWith("#" + rootUid, StringComparison.Ordinal))?.Craft;

    private static string? RootUid(SavedVessel vessel) =>
        vessel.RootIndex >= 0 && vessel.RootIndex < vessel.Parts.Count && vessel.Parts[vessel.RootIndex].Uid is { Length: > 0 } uid ? uid : null;

    /// <summary>Every id in the save: its vessels and every docked piece, craft or not.</summary>
    private static HashSet<string> AllIds(KspSave save) =>
        save.Vessels.Select(v => v.Id).Concat(save.Vessels.SelectMany(DockedCraft.Pieces).Select(p => p.Craft.Id)).ToHashSet();
}
