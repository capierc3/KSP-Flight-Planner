using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

public sealed class TechTreeTests : IDisposable
{
    // Trimmed from a real ModuleManager.ConfigCache (KSP 1.12.5).
    private const string Cache = """
        UrlConfig
        {
            parentUrl = Squad/Parts/Engine/liquidEngineLV-T45/liquidEngineLV-T45.cfg
            PART
            {
                name = liquidEngine2_v2
                title = LV-T45 "Swivel" Liquid Fuel Engine
                category = Engine
                TechRequired = basicRocketry
                description = Gimballed and reliable.
                bulkheadProfiles = size1, srf
                crashTolerance = 7
                maxTemp = 2000
                mass = 1.5
            }
        }
        UrlConfig
        {
            parentUrl = Squad/Resources/TechTree.cfg
            TechTree
            {
                RDNode
                {
                    id = start
                    title = Start
                    cost = 0
                    anyToUnlock = False
                    pos = -2568,1199,0
                }
                RDNode
                {
                    id = basicRocketry
                    title = Basic Rocketry
                    description = How hard can Rocket Science be anyway?
                    cost = 5
                    anyToUnlock = False
                    pos = -2385,1257,0
                    Parent
                    {
                        parentID = start
                        lineFrom = RIGHT
                        lineTo = LEFT
                    }
                }
            }
        }
        """;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;

    public TechTreeTests()
    {
        _db = new PlannerDatabase(_path);
        _db.Initialize();
    }

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
    }

    [Fact]
    public void Catalog_reads_the_tech_tree_and_what_unlocks_each_part()
    {
        var catalog = PartCatalogReader.Parse(Cache);
        Assert.Equal("basicRocketry", catalog.Part("liquidEngine2.v2")!.TechRequired);
        Assert.Equal(["start", "basicRocketry"], catalog.TechTree.Select(n => n.Id));
        var basic = catalog.TechTree[1];
        Assert.Equal(("Basic Rocketry", "How hard can Rocket Science be anyway?", 5.0, -2385.0, 1257.0, false),
            (basic.Title, basic.Description, basic.Cost, basic.X, basic.Y, basic.AnyToUnlock));
        Assert.Equal(["start"], basic.Parents);
    }

    [Fact]
    public void ModuleManager_tech_tree_file_wins_over_the_cache()
    {
        const string patched = """
            TechTree
            {
                RDNode
                {
                    id = start
                    title = Patched Start
                    cost = 0
                    pos = 0,0,0
                }
            }
            """;
        Assert.Equal("Patched Start", Assert.Single(PartCatalogReader.Parse(Cache, patched).TechTree).Title);
    }

    [Fact]
    public void Tech_tree_round_trips_through_the_database()
    {
        var repo = new PartCatalogRepository(_db);
        var catalog = PartCatalogReader.Parse(Cache);
        repo.Replace(catalog, "cache", DateTime.UtcNow);
        repo.Replace(catalog, "cache", DateTime.UtcNow); // replaces, doesn't duplicate

        var loaded = repo.Load();
        Assert.Equal(catalog.TechTree.Select(n => (n.Id, n.Title, n.Cost, n.X, n.Y, string.Join(",", n.Parents))),
            loaded.TechTree.Select(n => (n.Id, n.Title, n.Cost, n.X, n.Y, string.Join(",", n.Parents))));
        var swivel = loaded.Part("liquidEngine2.v2")!;
        Assert.Equal("basicRocketry", swivel.TechRequired);
        Assert.Equal(("Gimballed and reliable.", 7.0, 2000.0), (swivel.Description, swivel.CrashToleranceMs, swivel.MaxTempK));
        Assert.Equal(["size1", "srf"], swivel.BulkheadProfiles);
    }

    [Fact]
    public void Save_reader_reads_researched_tech_and_unlocked_parts()
    {
        const string save = """
            GAME
            {
                Title = Protogen
                Mode = SCIENCE_SANDBOX
                SCENARIO
                {
                    name = ResearchAndDevelopment
                    sci = 267.729675
                    Tech
                    {
                        id = start
                        state = Available
                        part = mk1pod.v2
                    }
                    Tech
                    {
                        id = basicRocketry
                        state = Available
                        part = liquidEngine2_v2
                    }
                }
            }
            """;
        var research = KspSaveReader.Parse(save).Research!;
        Assert.Equal(267.729675, research.Science, 6);
        Assert.Equal(["start", "basicRocketry"], research.ResearchedTech);
        Assert.True(research.IsUnlocked(PartCatalogReader.Parse(Cache).Part("liquidEngine2.v2")!)); // '_' read as '.'
        Assert.Contains("mk1pod.v2", research.UnlockedParts);

        Assert.Null(KspSaveReader.Parse("GAME\n{\n Mode = SANDBOX\n}").Research); // sandbox: everything goes
    }

    // start ─┬─ a (5) ── c (all of a, b)
    //        └─ b (10)
    //        cheap (1) ── d (any of cheap, pricey)
    //        pricey (50) ┘
    private static readonly TechNode[] Tree =
    [
        Node("start", 0),
        Node("a", 5, "start"),
        Node("b", 10, "start"),
        Node("c", 20, "a", "b"),
        Node("cheap", 1, "start"),
        Node("pricey", 50, "start"),
        Node("d", 30, ["cheap", "pricey"], any: true),
    ];

    private static TechNode Node(string id, double cost, params string[] parents) => Node(id, cost, parents, false);
    private static TechNode Node(string id, double cost, string[] parents, bool any) => new(id, id, null, cost, 0, 0, parents, any);

    [Fact]
    public void Planning_a_node_brings_in_what_it_needs()
    {
        var plan = new TechPlan(Tree, new HashSet<string> { "start" }, []);
        Assert.Equal(TechStatus.Researched, plan.Status("start"));
        Assert.Equal(TechStatus.Available, plan.Status("a"));
        Assert.Equal(TechStatus.Locked, plan.Status("c"));

        plan.Toggle("c");
        Assert.Equal(new HashSet<string> { "a", "b", "c" }, plan.Planned);
        Assert.Equal(35, plan.PlannedCost);

        plan.Toggle("d"); // any parent will do: the cheap one
        Assert.Contains("cheap", plan.Planned);
        Assert.DoesNotContain("pricey", plan.Planned);

        plan.Toggle("start"); // researched: nothing to plan
        Assert.DoesNotContain("start", plan.Planned);
    }

    [Fact]
    public void Unplanning_a_node_drops_what_relied_on_it()
    {
        var plan = new TechPlan(Tree, new HashSet<string> { "start" }, ["a", "b", "c", "cheap", "pricey", "d"]);
        plan.Toggle("a");
        Assert.Equal(new HashSet<string> { "b", "cheap", "pricey", "d" }, plan.Planned);

        plan.Toggle("cheap"); // d still has pricey
        Assert.Contains("d", plan.Planned);
        plan.Toggle("pricey");
        Assert.Equal(new HashSet<string> { "b" }, plan.Planned);
    }

    [Fact]
    public void A_stored_plan_drops_nodes_researched_since_or_no_longer_in_the_tree()
    {
        var plan = new TechPlan(Tree, new HashSet<string> { "start", "a" }, ["a", "c", "gone"]);
        Assert.Equal(new HashSet<string>(), plan.Planned); // c needs b, which isn't planned
        plan = new TechPlan(Tree, new HashSet<string> { "start", "a" }, ["a", "b", "c"]);
        Assert.Equal(new HashSet<string> { "b", "c" }, plan.Planned);
    }

    [Fact]
    public void Plans_are_kept_per_save()
    {
        var repo = new TechPlanRepository(_db);
        _db.ActiveSave = "Career";
        repo.Save(["a", "b"]);
        _db.ActiveSave = "Science";
        Assert.Empty(repo.Load());
        repo.Save(["c"]);
        repo.Save(["d"]); // replaces
        Assert.Equal(new HashSet<string> { "d" }, repo.Load());
        _db.ActiveSave = "Career";
        Assert.Equal(new HashSet<string> { "a", "b" }, repo.Load());
    }
}
