using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Tests;

/// <summary>
/// Survey Engineers (docs/KERBIN-RESOURCES.md "Survey and science roles", docs/SURVEY.md "When a body is done"): one craft
/// watched at a time, a body done, landmarks found by the survey, and the add-on's records in the save.
/// </summary>
public sealed class SurveyRolesTests
{
    private const double MinmusRadius = 60_000;
    private const double MinmusMu = 1.7658e9;

    private static SurveyMap Map()
    {
        const double step = 1;
        int rows = SurfaceBake.GridRows(step), columns = SurfaceBake.GridColumns(step);
        var biomes = Enumerable.Range(0, rows * columns).Select(i => (short)(SurfaceBake.ColumnLongitude(step, i % columns) >= 0 ? 1 : 0)).ToArray();
        var flat = new float[rows * columns];
        Array.Fill(flat, 0.05f);
        return new SurveyMap("Minmus", step, MinmusRadius, 40_400, 0, flat, flat, biomes, ["West", "East"], DateTime.UtcNow);
    }

    private static OrbitalElements Polar(double meanAnomaly = 0) => new(MinmusRadius + 30_000, 0, 90, 0, 0, meanAnomaly, 0);

    private static SurveyCoverage.CrewCraft Craft(string name, OrbitalElements orbit) => new(name, orbit, orbit, double.MaxValue, false, true);

    private static int Mapped(SurveyCoverage c) => c.Estimate.Count(t => !float.IsNaN(t));

    [Fact]
    public void An_engineer_watches_one_craft_while_it_still_maps_new_ground()
    {
        // Two probes on the same polar orbit, half an orbit apart. Both map new ground at first, so the engineer stays with
        // the first: only its strip is mapped, as if the second weren't there.
        var map = Map();
        var period = Kepler.OrbitalPeriod(Polar(), MinmusMu);
        var crew = new SurveyCoverage(1);
        var watches = new List<SurveyWatch>();
        crew.FollowCrew([Craft("A", Polar()), Craft("B", Polar(Math.PI))], 0, period / 3, map, MinmusMu, "Bill", watches);

        var alone = new SurveyCoverage(1);
        alone.Follow(Polar(), 0, period / 3, map, MinmusMu, rough: false, estimate: true);

        Assert.Equal(Mapped(alone), Mapped(crew));
        var watch = Assert.Single(watches);
        Assert.Equal(("Bill", "A", 0.0), (watch.Kerbal, watch.Craft, watch.FromUt));
    }

    [Fact]
    public void Over_mapped_ground_the_engineer_switches_to_the_craft_over_new_ground()
    {
        var map = Map();
        var period = Kepler.OrbitalPeriod(Polar(), MinmusMu);
        var equatorial = new OrbitalElements(MinmusRadius + 30_000, 0, 0, 0, 0, 0, 0);
        // A's track is already mapped; B (equatorial) crosses new ground.
        var start = new SurveyCoverage(1);
        start.Follow(Polar(), 0, period, map, MinmusMu, rough: false, estimate: true);

        var expected = new SurveyCoverage(1, null, (float[])start.Estimate.Clone());
        var bAdds = expected.Follow(equatorial, 0, period, map, MinmusMu, rough: false, estimate: true);

        var crew = new SurveyCoverage(1, null, (float[])start.Estimate.Clone());
        var watches = new List<SurveyWatch>();
        var added = crew.FollowCrew([Craft("A", Polar()), Craft("B", equatorial)], 0, period, map, MinmusMu, "Bill", watches);

        Assert.True(bAdds > 0);
        Assert.Equal(bAdds, added);
        Assert.Equal("B", watches[0].Craft);
    }

    [Fact]
    public void Crediting_needs_the_same_engineer_on_the_same_body_and_craft_in_both_readings()
    {
        var map = Map();
        var probe = new SurveyCraftState("p1", "Probe", Polar(), false, true);
        var other = new SurveyCraftState("p2", "Other", Polar(Math.PI), false, true);
        var from = new SurveyCrewReading(0, "Bill", "Minmus", [probe, other], true);
        var to = new SurveyCrewReading(600, "Bill", "Minmus", [probe], true);

        var c = new SurveyCoverage(1);
        Assert.True(c.CreditCrew(from, to, map, MinmusMu) > 0); // only the probe in both
        Assert.Equal(0, new SurveyCoverage(1).CreditCrew(from, to with { Kerbal = "Bob" }, map, MinmusMu));
        Assert.Equal(0, new SurveyCoverage(1).CreditCrew(from, to with { BodyName = "Mun" }, map, MinmusMu));
        Assert.Equal(0, new SurveyCoverage(1).CreditCrew(from, to with { Crafts = [] }, map, MinmusMu));
    }

    [Fact]
    public void Area_covered_counts_by_area_and_a_body_is_done_at_the_setting_with_every_biome_reported()
    {
        var map = Map();
        var c = new SurveyCoverage(1);
        int columns = SurfaceBake.GridColumns(1);
        for (var i = 0; i < c.Estimate.Length; i++)
            if (SurfaceBake.RowLatitude(1, i / columns) >= 60) c.Estimate[i] = 1;
        // Above 60° N is (1 − sin 60°) / 2 = 6.7% of a sphere, though it's a sixth of the grid's rows.
        Assert.Equal((1 - Math.Sin(Math.PI / 3)) / 2, SurveyReports.AreaCovered(c, SurveyLevel.Estimate), 2);

        Array.Fill(c.Estimate, 1f);
        var biomes = SurveyReports.Build(map, c, "Minmus", new HashSet<(string, string)>());
        Assert.True(SurveyReports.IsDone(c, biomes, 95));
        Assert.False(SurveyReports.IsDone(c, [biomes[0] with { EstimateUt = null }, biomes[1]], 95));
    }

    [Fact]
    public void Landmarks_are_hidden_spotted_and_identified_by_the_survey()
    {
        Landmark[] wiki =
        [
            new("Mohole", LandmarkSurvey.Feature, 10, 10),
            new("KSC Launch Pad", LandmarkSurvey.LaunchSite, -0.1, -74.6),
            new("Monolith", LandmarkSurvey.Anomaly, -20, -20, "A tall black slab"),
        ];
        SurveyAnomaly[] game = [new("Minmus", "Monolith00", "Monolith", -20, -20), new("Minmus", "UFO", "UFO", 40, 120)];
        var c = new SurveyCoverage(1);
        var none = LandmarkSurvey.For("Minmus", wiki, game, c, [], hide: true);
        Assert.Equal(["KSC Launch Pad"], none.Where(l => l.Shown).Select(l => l.Name));
        Assert.Equal(4, none.Count); // the wiki's anomaly is replaced by the game's two

        // Cover the Mohole and both anomalies; visit the UFO.
        int columns = SurfaceBake.GridColumns(1);
        foreach (var (lat, lon) in new[] { (10, 10), (-20, -20), (40, 120) })
            c.Estimate[(lat + 90) * columns + (lon + 180)] = 5;
        var seen = LandmarkSurvey.For("Minmus", wiki, game, c, [new AnomalyVisit("Minmus", "UFO", 9, "Rover")], hide: true);
        Assert.Contains(seen, l => l is { Name: "Mohole", Stage: LandmarkStage.Spotted });
        var monolith = Assert.Single(seen, l => l.LatitudeDeg == -20);
        Assert.Equal((LandmarkStage.Spotted, null), (monolith.Stage, monolith.Note));
        Assert.StartsWith("Unknown object", monolith.Name);
        Assert.Contains(seen, l => l is { Name: "UFO", Stage: LandmarkStage.Identified });
        Assert.Equal(5, LandmarkSurvey.CoveredAt(c, 10, 10));

        var all = LandmarkSurvey.For("Minmus", wiki, game, new SurveyCoverage(1), [], hide: false);
        Assert.All(all, l => Assert.Equal(LandmarkStage.Known, l.Stage));
        Assert.Contains(all, l => l is { Name: "Monolith", Note: "A tall black slab" }); // the wiki's note follows the game's name
    }

    [Fact]
    public void The_add_ons_records_are_read_from_the_save_and_its_lines()
    {
        var save = KspSaveReader.Parse("""
            GAME
            {
                Title = Test
                Mode = SCIENCE_SANDBOX
                PARAMETERS
                {
                    RolesSettings
                    {
                        SurveyEngineers = True
                        DonePercent = 90
                        HideLandmarks = False
                    }
                }
                FLIGHTSTATE
                {
                    UT = 1000
                    VESSEL
                    {
                        pid = ABCDEF0123456789ABCDEF0123456789
                        name = Probe S-1
                        type = Probe
                        sit = ORBITING
                        ORBIT
                        {
                            SMA = 90000
                            ECC = 0
                            INC = 90
                            LPE = 0
                            LAN = 0
                            MNA = 0
                            EPH = 0
                            REF = 3
                            IDENT = Squad/Minmus
                        }
                        PART
                        {
                            name = SurveyScanner
                            MODULE
                            {
                                name = ModuleResourceScanner
                            }
                            MODULE
                            {
                                name = ModuleKerbNetAccess
                            }
                        }
                    }
                }
                SCENARIO
                {
                    name = RolesScenario
                    WORKER
                    {
                        kerbal = Bill Rockmore
                        post = Min Station
                        postPid = 11111111111111111111111111111111
                        body = Minmus
                        ksc = False
                        CRAFT
                        {
                            pid = abcdef0123456789abcdef0123456789
                            name = Probe S-1
                        }
                    }
                    REPORT
                    {
                        body = Mun
                        writer = Bill Rockmore
                        writer = Bob Pinecrest
                        doneUt = 500
                        needS = 21600
                        workedS = 21600
                        lastUt = 900
                        filedUt = 900
                        filedBy = Bob Pinecrest
                    }
                    VISIT
                    {
                        body = Mun
                        name = Monolith01
                        ut = 800
                        vessel = Rover
                    }
                }
            }
            """);
        Assert.Equal(new RolesRules(true, 90, false), save.Roles);
        var worker = Assert.Single(save.SurveyWorkers);
        Assert.Equal(("Bill Rockmore", "Min Station", "Minmus", false), (worker.Kerbal, worker.PostName, worker.BodyName, worker.AtKsc));
        var job = Assert.Single(save.SurveyReportJobs);
        Assert.Equal((true, 900.0, "Bob Pinecrest", 2), (job.Filed, job.FiledUt!.Value, job.FiledBy, job.Writers.Count));
        Assert.Equal("Monolith01", Assert.Single(save.AnomalyVisits).Name);

        // The worker's craft is found by its save id whatever the case, with its narrow-band.
        var reading = SurveyCrewReading.Of(worker, save, 1000);
        var craft = Assert.Single(reading.Crafts);
        Assert.Equal(("Probe S-1", true, false), (craft.Name, craft.HasNarrowBand, craft.HasSurveyScanner));
        Assert.Empty(SurveyCrewReading.Of(worker with { BodyName = "Mun" }, save, 1000).Crafts);

        var lines = RolesReader.Workers(["Val\tKSC\t\tKerbin\t1\tp1|Sat A,p2|Sat B"]);
        Assert.Equal(["Sat A", "Sat B"], Assert.Single(lines).Crafts.Select(c => c.Name));
        Assert.True(lines[0].AtKsc);
        var writing = Assert.Single(RolesReader.Reports(["Minmus\t100\tNaN\t\tBill,Bob\t60\t21600"]));
        Assert.False(writing.Filed);
        Assert.Equal(new SurveyAnomaly("Mun", "UFO", "UFO", 1.5, -2), Assert.Single(RolesReader.Anomalies(["Mun\tUFO\t\t1.5\t-2"])));
    }
}
