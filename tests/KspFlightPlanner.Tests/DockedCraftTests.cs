using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.Tests;

public sealed class DockedCraftTests
{
    /// <summary>A PART node: <paramref name="modules"/> are extra MODULE nodes (already written out).</summary>
    private static string Part(string name, string uid, int parent, params string[] modules) =>
        $"PART\n{{\nname = {name}\nuid = {uid}\nparent = {parent}\nmass = 0.5\n{string.Join("\n", modules)}\n}}";

    private const string Command = "MODULE\n{\nname = ModuleCommand\n}";
    private const string Engine = "MODULE\n{\nname = ModuleEngines\nthrustPercentage = 100\n}";

    private static string Port(string state, string dockedTo, string? cameWith = null, string? type = null, string? rootUid = null) =>
        "MODULE\n{\nname = ModuleDockingNode\n" + $"state = {state}\ndockUId = {dockedTo}\n" +
        (cameWith is null ? "" : $"DOCKEDVESSEL\n{{\nvesselName = {cameWith}\nvesselType = {type}\nrootUId = {rootUid}\n}}\n") + "}";

    /// <summary>
    /// Shaped like the Protogen save's Kerb Station (2026-10-27): a station core with two ports, an electrical module
    /// (a Probe with a probe core, no engine) and a shuttle (a Ship with a pod and an engine) docked to them, and a fuel
    /// tank (a Probe with its own engine) docked to the shuttle's far end.
    /// </summary>
    private static readonly string Stack = string.Join("\n",
        "GAME", "{", "Title = Test", "Mode = SANDBOX", "FLIGHTSTATE", "{", "UT = 1000",
        "VESSEL", "{", "pid = station", "name = Kerb Station", "type = Station", "sit = ORBITING", "root = 0",
        "ORBIT", "{", "SMA = 700000", "ECC = 0.001", "INC = 0", "LPE = 0", "LAN = 0", "MNA = 0", "EPH = 0", "REF = 1", "}",
        Part("cupola", "100", 0, Command),                                                                     // 0
        Part("dockingPort2", "101", 0, Port("Docked (dockee)", "200", "Kerb Station", "Station", "100")),       // 1
        Part("dockingPort2", "102", 0, Port("Docked (dockee)", "300", "Kerb Station", "Station", "100")),       // 2
        Part("dockingPort2", "200", 1, Port("Docked (docker)", "101", "Kerb Station - Electrical", "Probe", "201")), // 3
        Part("probeCoreHex.v2", "201", 3, Command),                                                           // 4
        Part("batteryBankMini", "202", 4),                                                                    // 5
        Part("dockingPort2", "300", 2, Port("Docked (docker)", "102", "Kerb Shuttle mk1", "Ship", "301")),       // 6
        Part("mk1-3pod", "301", 6, Command),                                                                  // 7
        Part("LiquidEngineRK-7", "302", 7, Engine),                                                           // 8
        Part("dockingPort2", "303", 7, Port("Docked (dockee)", "400", "Kerb Station", "Station", "100")),       // 9
        Part("dockingPort2", "400", 9, Port("Docked (docker)", "303", "Kerbin Station - Fuel Tank", "Probe", "401")), // 10
        Part("probeCoreHex.v2", "401", 10, Command),                                                          // 11
        Part("engineLargeSkipper.v2", "402", 11, Engine),                                                     // 12
        "}",
        "VESSEL", "{", "pid = loner", "name = Lone Probe", "type = Probe", "sit = ORBITING", "root = 0",
        Part("probeCoreOcto", "900", 0, Command, Port("Ready", "0")),
        "}",
        "}", "}", "");

    [Fact]
    public void A_stack_is_cut_at_each_docked_port_into_the_vessels_that_docked()
    {
        var save = KspSaveReader.Parse(Stack);
        var station = save.Vessels.Single(v => v.Name == "Kerb Station");
        var pieces = DockedCraft.Pieces(station);

        Assert.Equal(["Kerb Station - Electrical", "Kerb Shuttle mk1", "Kerbin Station - Fuel Tank"], pieces.Select(p => p.Name));
        var shuttle = pieces[1];
        Assert.Equal("Ship", shuttle.Type);
        Assert.Equal(["dockingPort2", "mk1-3pod", "LiquidEngineRK-7", "dockingPort2"], shuttle.Craft.Parts.Select(p => p.Name));
        Assert.Equal("station#301", shuttle.Craft.Id);
        Assert.Equal("station", shuttle.Craft.DockedInId);
        Assert.Equal("mk1-3pod", shuttle.Craft.Parts[shuttle.Craft.RootIndex].Name);
        Assert.Equal(station.Orbit, shuttle.Craft.Orbit);
        // The fuel tank, docked to the shuttle, isn't part of it.
        Assert.Equal(["dockingPort2", "probeCoreHex.v2", "engineLargeSkipper.v2"], pieces[2].Craft.Parts.Select(p => p.Name));
        Assert.Empty(DockedCraft.Pieces(save.Vessels.Single(v => v.Name == "Lone Probe")));
    }

    [Fact]
    public void Only_ships_with_a_command_part_and_an_engine_count_as_craft_of_their_own()
    {
        var pieces = DockedCraft.Pieces(KspSaveReader.Parse(Stack).Vessels[0]);
        Assert.False(pieces[0].IsCraftByDefault); // a probe module with no engine
        Assert.True(pieces[1].IsCraftByDefault);  // the shuttle
        Assert.False(pieces[2].IsCraftByDefault); // a fuel tank that flew itself up: a Probe, engine or not
        Assert.True(pieces[2].IsCraft(true));
        Assert.False(pieces[1].IsCraft(false));
    }

    [Fact]
    public void The_save_lists_docked_craft_after_their_stack_and_finds_any_piece_by_id()
    {
        var save = KspSaveReader.Parse(Stack);
        Assert.Equal(["Kerb Station", "Kerb Shuttle mk1", "Lone Probe"], save.WithDockedCraft().Select(v => v.Name));
        Assert.Equal(["Kerb Station", "Kerb Station - Electrical", "Lone Probe"],
            save.WithDockedCraft(name => name switch { "Kerb Shuttle mk1" => false, "Kerb Station - Electrical" => true, _ => null }).Select(v => v.Name));

        Assert.Equal("Kerbin Station - Fuel Tank", save.FindVessel("station#401")?.Name);
        Assert.Equal("Kerb Station", save.FindVessel("station")?.Name);
        Assert.Null(save.FindVessel("station#999"));
        Assert.Null(save.FindVessel("gone#301"));
    }
}
