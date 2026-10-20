using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.Tests;

public sealed class LiveSaveMatchTests
{
    private static KspSave Save(string mode, double ut, params string[] vessels) =>
        new("t", mode, ut, vessels.Select((n, i) => new SavedVessel(i.ToString(), n, "Probe", "ORBITING", null)).ToList());

    private static GameIdentity Game(string mode, double ut, params string[] vessels) =>
        new(mode, ut, vessels.Select(n => (n, n.EndsWith("Debris") ? "Debris" : "Probe")).ToList());

    // As the probe found them on 2026-10-20: a science-sandbox save and a sandbox one with no vessel names in common.
    private static readonly Dictionary<string, KspSave> Saves = new()
    {
        ["Protogen"] = Save("SCIENCE_SANDBOX", 1_329_923, "KerbLink mk1 A", "MunLink Mk1 A", "Looper Mk3", "Untitled Space Craft Debris"),
        ["SandBox"] = Save("SANDBOX", 29_388_990, "Kerbal X", "Untitled Space Craft", "Untitled Space Craft Debris"),
    };

    [Fact]
    public void Modes_are_the_same_whichever_way_they_are_written()
    {
        Assert.True(LiveSaveMatch.SameMode("ScienceSandbox", "SCIENCE_SANDBOX"));
        Assert.True(LiveSaveMatch.SameMode("Sandbox", "SANDBOX"));
        Assert.False(LiveSaveMatch.SameMode("Sandbox", "SCIENCE_SANDBOX"));
    }

    [Fact]
    public void The_chosen_save_fits_when_the_games_vessels_are_in_it()
    {
        var game = Game("ScienceSandbox", 1_329_932, "KerbLink mk1 A", "MunLink Mk1 A", "Looper Mk3", "Kerbal X Debris");
        Assert.Equal(new SaveVerdict(SaveVerdictKind.Chosen, "Protogen"), LiveSaveMatch.Judge("Protogen", Saves, game));
    }

    [Fact]
    public void Another_save_with_the_games_vessels_and_mode_is_the_one_being_played()
    {
        var game = Game("Sandbox", 29_388_990, "Kerbal X", "Untitled Space Craft", "Duna Test Debris");
        Assert.Equal(new SaveVerdict(SaveVerdictKind.Other, "SandBox"), LiveSaveMatch.Judge("Protogen", Saves, game));
    }

    [Fact]
    public void A_launch_since_the_save_was_written_doesnt_make_it_another_save()
    {
        // One vessel of three isn't in the file yet: two thirds is still over half.
        var game = Game("ScienceSandbox", 1_330_000, "KerbLink mk1 A", "MunLink Mk1 A", "Brand New Rocket");
        Assert.Equal(SaveVerdictKind.Chosen, LiveSaveMatch.Judge("Protogen", Saves, game).Kind);
    }

    [Fact]
    public void A_game_matching_no_save_rules_out_the_chosen_one_without_naming_another()
    {
        var game = Game("ScienceSandbox", 50_000, "Somebody Else's Station", "Another Probe");
        Assert.Equal(SaveVerdictKind.NotChosen, LiveSaveMatch.Judge("Protogen", Saves, game).Kind);
    }

    [Fact]
    public void Another_mode_alone_rules_out_the_chosen_save()
    {
        // A fresh sandbox game with only debris (or nothing) flying: SandBox is the only sandbox save.
        Assert.Equal(new SaveVerdict(SaveVerdictKind.Other, "SandBox"), LiveSaveMatch.Judge("Protogen", Saves, Game("Sandbox", 10)));
        Assert.Equal(SaveVerdictKind.NotChosen, LiveSaveMatch.Judge("Protogen", Saves, Game("Career", 10)).Kind);
        // Vessels couldn't be read: the mode still rules Protogen out, but there's nothing to name the save by.
        Assert.Equal(SaveVerdictKind.NotChosen, LiveSaveMatch.Judge("Protogen", Saves, new GameIdentity("Sandbox", 10, null)).Kind);
    }

    [Fact]
    public void Nothing_to_compare_says_nothing()
    {
        Assert.Equal(SaveVerdict.Unknown, LiveSaveMatch.Judge("Protogen", Saves, Game("ScienceSandbox", 10, "Kerbal X Debris")));
        Assert.Equal(SaveVerdict.Unknown, LiveSaveMatch.Judge("Protogen", Saves, new GameIdentity("ScienceSandbox", 10, null)));
        Assert.Equal(SaveVerdict.Unknown, LiveSaveMatch.Judge("Missing", Saves, Game("Sandbox", 10, "Kerbal X")));
    }

    [Fact]
    public void A_save_written_now_with_the_games_mode_and_time_was_written_by_the_game()
    {
        // SandBox was written 6 s after loading it, at its game time.
        var game = Game("Sandbox", 29_388_995);
        Assert.True(LiveSaveMatch.WrittenByGame(Saves["SandBox"], game));
        Assert.False(LiveSaveMatch.WrittenByGame(Saves["SandBox"], game with { Ut = 29_388_990 + 61 }));
        Assert.False(LiveSaveMatch.WrittenByGame(Saves["Protogen"], game with { Ut = 1_329_923 })); // right time, wrong mode
    }
}
