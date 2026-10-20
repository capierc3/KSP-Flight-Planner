namespace KspFlightPlanner.Core.Ksp;

/// <summary>What the running game says about itself, to tell which save it's playing (kRPC can't name the save).</summary>
/// <param name="Mode">kRPC's game mode (Sandbox, ScienceSandbox, Career…).</param>
/// <param name="Ut">Game time now.</param>
/// <param name="Vessels">Every vessel's name and type (Ship, Probe, Debris…); null when they couldn't be read.</param>
public sealed record GameIdentity(string Mode, double Ut, IReadOnlyList<(string Name, string Type)>? Vessels);

public enum SaveVerdictKind
{
    /// <summary>Nothing to go on (no vessels worth comparing, or nothing read).</summary>
    Unknown,

    /// <summary>The game is playing the chosen save.</summary>
    Chosen,

    /// <summary>The game is playing another save, named in <see cref="SaveVerdict.Save"/>.</summary>
    Other,

    /// <summary>The game isn't playing the chosen save, but which one it is can't be told.</summary>
    NotChosen,
}

public sealed record SaveVerdict(SaveVerdictKind Kind, string? Save = null)
{
    public static readonly SaveVerdict Unknown = new(SaveVerdictKind.Unknown);
}

/// <summary>
/// Tells which save the running game is playing. KSP writes the save it's playing every time the scene changes
/// (including a few seconds after loading it), so a save written now with the game's mode and time is the one. Before any
/// write, the game's vessels are looked for in each save. docs/MATH.md §8.12.
/// </summary>
public static class LiveSaveMatch
{
    /// <summary>A save written within this much game time (s) of the game's time now was written by the game now.</summary>
    public const double WrittenWithinS = 60;

    /// <summary>The chosen save fits if at least this share of the game's vessels are in it (some may be newer than the file).</summary>
    public const double ChosenShare = 0.5;

    /// <summary>Another save is the one being played if at least this share of the game's vessels are in it.</summary>
    public const double OtherShare = 0.8;

    /// <summary>kRPC's mode (ScienceSandbox) and the save's (SCIENCE_SANDBOX) name the same mode.</summary>
    public static bool SameMode(string liveMode, string saveMode) =>
        string.Equals(liveMode.Replace("_", ""), saveMode.Replace("_", ""), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the game wrote this save just now: same mode, and the file's game time is the game's.</summary>
    public static bool WrittenByGame(KspSave file, GameIdentity game) =>
        SameMode(game.Mode, file.Mode) && Math.Abs(file.Ut - game.Ut) <= WrittenWithinS;

    /// <summary>Vessels worth comparing: debris and asteroids have names any save could have.</summary>
    public static IReadOnlyList<string> Telling(IEnumerable<(string Name, string Type)> vessels) =>
        vessels.Where(v => v.Type is not ("Debris" or "SpaceObject")).Select(v => v.Name).ToList();

    /// <summary>The share of <paramref name="names"/> that the save has a vessel of that name for; null when there are none.</summary>
    public static double? Share(IReadOnlyList<string> names, KspSave save)
    {
        if (names.Count == 0) return null;
        var inSave = save.Vessels.Select(v => v.Name).ToHashSet();
        return (double)names.Count(inSave.Contains) / names.Count;
    }

    /// <summary>
    /// Which save the game is playing, from its mode and vessels. The chosen save fits if the mode is the same and at least
    /// half the game's vessels (not debris or asteroids) are in it. Otherwise another save is the one if it's the only one
    /// with the game's mode and at least 80 % of its vessels (or, with no vessels to go on, the only one with its mode).
    /// </summary>
    public static SaveVerdict Judge(string chosen, IReadOnlyDictionary<string, KspSave> saves, GameIdentity game)
    {
        var names = game.Vessels is { } vessels ? Telling(vessels) : [];
        var known = game.Vessels is not null;
        if (saves.TryGetValue(chosen, out var mine) && SameMode(game.Mode, mine.Mode))
        {
            if (!known || names.Count == 0) return SaveVerdict.Unknown;
            if (Share(names, mine) >= ChosenShare) return new(SaveVerdictKind.Chosen, chosen);
        }
        else if (!saves.ContainsKey(chosen)) return SaveVerdict.Unknown; // the chosen save can't be read: nothing to compare

        var sameMode = saves.Where(s => !string.Equals(s.Key, chosen, StringComparison.OrdinalIgnoreCase) && SameMode(game.Mode, s.Value.Mode)).ToList();
        var others = known && names.Count > 0
            ? sameMode.Where(s => Share(names, s.Value) >= OtherShare).ToList()
            : known ? sameMode : [];
        return others.Count == 1 ? new(SaveVerdictKind.Other, others[0].Key) : new(SaveVerdictKind.NotChosen);
    }
}
