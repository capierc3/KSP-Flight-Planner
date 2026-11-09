using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.Tests;

public sealed class VesselRelinkTests
{
    private const string Command = "MODULE\n{\nname = ModuleCommand\n}";
    private const string Engine = "MODULE\n{\nname = ModuleEngines\nthrustPercentage = 100\n}";

    private static string Part(string name, string uid, int parent, params string[] modules) =>
        $"PART\n{{\nname = {name}\nuid = {uid}\nparent = {parent}\nmass = 0.5\n{string.Join("\n", modules)}\n}}";

    private static string Port(string state, string dockedTo, string? cameWith = null, string? type = null, string? rootUid = null) =>
        "MODULE\n{\nname = ModuleDockingNode\n" + $"state = {state}\ndockUId = {dockedTo}\n" +
        (cameWith is null ? "" : $"DOCKEDVESSEL\n{{\nvesselName = {cameWith}\nvesselType = {type}\nrootUId = {rootUid}\n}}\n") + "}";

    private static string Vessel(string pid, string name, string type, params string[] parts) => string.Join("\n",
        "VESSEL", "{", $"pid = {pid}", $"name = {name}", $"type = {type}", "sit = ORBITING", "root = 0",
        "ORBIT", "{", "SMA = 700000", "ECC = 0.001", "INC = 0", "LPE = 0", "LAN = 0", "MNA = 0", "EPH = 0", "REF = 1", "}",
        string.Join("\n", parts), "}");

    private static KspSave Save(params string[] vessels) => KspSaveReader.Parse(string.Join("\n",
        "GAME", "{", "Title = Test", "Mode = SANDBOX", "FLIGHTSTATE", "{", "UT = 1000", string.Join("\n", vessels), "}", "}", ""));

    private static CustomLocation Linked(long id, string name, string vesselId) =>
        new(id, name, "Kerbin", 100_000, 100_000, IsPlaced: true) { KspSave = "Protogen Hauler Test", KspVesselId = vesselId };

    private static IReadOnlyDictionary<long, string> Follow(KspSave now, KspSave? before, params CustomLocation[] linked) =>
        VesselRelink.Follow(linked, now, now.WithDockedCraft().ToList(), before).ToDictionary(p => p.Key, p => p.Value.Id);

    // The station on its own, and with the shuttle (root part 301) docked to its port.
    private static string Station() => Vessel("station", "Kerb Station", "Station",
        Part("cupola", "100", 0, Command), Part("dockingPort2", "101", 0, Port("Ready", "0")));

    private static string StationWithShuttle() => Vessel("station", "Kerb Station", "Station",
        Part("cupola", "100", 0, Command),
        Part("dockingPort2", "101", 0, Port("Docked (dockee)", "200", "Kerb Station", "Station", "100")),
        Part("dockingPort2", "200", 1, Port("Docked (docker)", "101", "Kerb Shuttle", "Ship", "301")),
        Part("mk1-3pod", "301", 2, Command),
        Part("LiquidEngineRK-7", "302", 3, Engine));

    private static string Shuttle(string pid, string name = "Kerb Shuttle", string type = "Ship", string rootUid = "301") =>
        Vessel(pid, name, type, Part("mk1-3pod", rootUid, 0, Command), Part("LiquidEngineRK-7", rootUid + "2", 0, Engine),
            Part("dockingPort2", rootUid + "3", 0, Port("Ready", "0")));

    [Fact]
    public void A_location_whose_vessel_is_still_there_keeps_it()
    {
        var save = Save(Station(), Shuttle("s1"));
        Assert.Equal("s1", Follow(save, save, Linked(1, "Kerb Shuttle", "s1"))[1]);
    }

    [Fact]
    public void A_docked_shuttle_follows_its_root_part_off_the_station_under_its_new_id()
    {
        // Linked while docked (a piece id), then undocked on rails: a new pid, same root part.
        var before = Save(StationWithShuttle());
        var now = Save(Station(), Shuttle("fresh-pid"));
        Assert.Equal("fresh-pid", Follow(now, before, Linked(1, "Kerb Shuttle", "station#301"))[1]);
        // Also with no earlier reading (the planner was closed when it undocked).
        Assert.Equal("fresh-pid", Follow(now, null, Linked(1, "Kerb Shuttle", "station#301"))[1]);
    }

    [Fact]
    public void A_shuttle_that_docks_follows_its_root_part_into_the_station()
    {
        var before = Save(Station(), Shuttle("s1"));
        var now = Save(StationWithShuttle());
        Assert.Equal("station#301", Follow(now, before, Linked(1, "Kerb Shuttle", "s1"))[1]);
    }

    [Fact]
    public void A_location_renamed_in_the_planner_follows_the_vessel_by_its_name_in_the_save()
    {
        // Undocked in physics with its parts given new uids here, so only the name and type tie it.
        var before = Save(Station(), Shuttle("s1"));
        var now = Save(Station(), Shuttle("s2", rootUid: "777"));
        Assert.Equal("s2", Follow(now, before, Linked(1, "Shuttle One", "s1"))[1]);
        // With no earlier reading there's only the location's own name to go by.
        Assert.False(Follow(now, null, Linked(1, "Shuttle One", "s1")).ContainsKey(1));
        Assert.Equal("s2", Follow(now, null, Linked(1, "Kerb Shuttle", "s1"))[1]);
    }

    [Fact]
    public void A_twin_already_linked_elsewhere_isnt_taken_for_the_lost_one()
    {
        // Two craft of the same name; one still has its id, the other came back as s3.
        var now = Save(Shuttle("s1", rootUid: "500"), Shuttle("s3", rootUid: "777"));
        var follows = Follow(now, null, Linked(1, "Kerb Shuttle", "s1"), Linked(2, "Kerb Shuttle", "s2"));
        Assert.Equal("s1", follows[1]);
        Assert.Equal("s3", follows[2]);
    }

    [Fact]
    public void Two_unlinked_craft_with_the_name_leave_it_unlinked()
    {
        var before = Save(Shuttle("s1", rootUid: "500"));
        var now = Save(Shuttle("s2", rootUid: "777"), Shuttle("s3", rootUid: "888"));
        Assert.Empty(Follow(now, before, Linked(1, "Kerb Shuttle", "s1")));
    }

    [Fact]
    public void Two_lost_locations_pointing_at_one_craft_both_stay_lost()
    {
        var now = Save(Shuttle("s3", rootUid: "777"));
        Assert.Empty(Follow(now, null, Linked(1, "Kerb Shuttle", "s1"), Linked(2, "Kerb Shuttle", "s2")));
    }

    [Fact]
    public void A_craft_of_another_type_or_one_there_all_along_isnt_the_lost_vessel()
    {
        // Same name, but a Probe now.
        var before = Save(Shuttle("s1", rootUid: "500"));
        Assert.Empty(Follow(Save(Shuttle("p1", type: "Probe", rootUid: "777")), before, Linked(1, "Kerb Shuttle", "s1")));
        // An unlinked vessel of that name that was already in the last reading didn't come from it (s1 was destroyed).
        var withTwin = Save(Shuttle("s1", rootUid: "500"), Shuttle("twin", rootUid: "600"));
        Assert.Empty(Follow(Save(Shuttle("twin", rootUid: "600")), withTwin, Linked(1, "Kerb Shuttle", "s1")));
    }
}
