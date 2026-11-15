using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.Tests;

/// <summary>Science gaps: reading R&amp;D and carried data from the save, and what's left per biome (docs/SCIENCE.md).</summary>
public sealed class ScienceTests
{
    private static readonly ScienceExperimentDef Gravity = new("gravityScan", "Gravity Scan", 20, 22, 3, 51, 51, false, false);
    private static readonly ScienceExperimentDef Infrared = new("infraredTelescope", "Infrared Telescope", 15, 22, 2, 32, 0, false, false);
    private static readonly ScienceExperimentDef Crew = new("crewReport", "Crew Report", 5, 5, 1, 63, 7, false, false);
    private static readonly ScienceExperimentDef Eva = new("evaReport", "EVA Report", 8, 8, 1, 63, 23, false, false);
    private static readonly ScienceExperimentDef Atmosphere = new("atmosphereAnalysis", "Atmosphere Analysis", 20, 24, 10, 13, 13, true, false);

    private static readonly ScienceBody Kerbin = new("Kerbin", 0.3, 0.4, 0.7, 0.9, 1, 1.5, true, true, true, 18_000, 70_000,
        ["Grasslands", "Highlands"], ["LaunchPad"]);
    private static readonly ScienceBody Minmus = new("Minmus", 5, 0, 0, 0, 4, 2.5, false, false, true, 0, 30_000,
        ["Lowlands", "Greater Flats"], []);
    private static readonly ScienceBody Sun = new("Sun", 0, 0, 1, 1, 11, 2, true, false, false, 18_000, 1e9, [], []);

    private static readonly ScienceCatalog Catalog = new([Gravity, Infrared, Crew, Eva, Atmosphere],
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["GravMax"] = ["gravityScan"],
            ["mk1pod.v2"] = ["crewReport"],
            ["kerbalEVA"] = ["evaReport"],
        },
        [Kerbin, Minmus, Sun], DateTime.UtcNow);

    private static readonly KspSave Save = KspSaveReader.Parse("""
        GAME
        {
            Title = Test
            Mode = SCIENCE_SANDBOX
            FLIGHTSTATE
            {
                UT = 1000
                VESSEL
                {
                    pid = probe
                    name = Gravity Probe
                    sit = ORBITING
                    PART
                    {
                        name = probeCoreOcto
                    }
                    PART
                    {
                        name = GravMax
                        MODULE
                        {
                            name = ModuleScienceExperiment
                            ScienceData
                            {
                                data = 60
                                subjectID = gravityScan@MinmusInSpaceLowLowlands
                            }
                        }
                    }
                    PART
                    {
                        name = mk1pod_v2
                    }
                }
            }
            SCENARIO
            {
                name = ResearchAndDevelopment
                sci = 100
                Science
                {
                    id = gravityScan@KerbinInSpaceHighGrasslands
                    sci = 16.8
                    cap = 33
                    sbv = 1.5
                }
                Science
                {
                    id = gravityScan@MinmusInSpaceLowLowlands
                    sci = 0
                    cap = 88
                    sbv = 4
                }
            }
        }
        """);

    private static IReadOnlyDictionary<string, IReadOnlyList<CarriedScience>> NoneCarried => new Dictionary<string, IReadOnlyList<CarriedScience>>();

    [Fact]
    public void SaveGivesSubjectsAndCarriedData()
    {
        Assert.Equal(16.8, Save.ScienceSubjects["gravityScan@KerbinInSpaceHighGrasslands"].Science, 6);
        Assert.Equal(1.5, Save.ScienceSubjects["gravityScan@KerbinInSpaceHighGrasslands"].BodyValue);
        var carried = Assert.Single(Save.Vessels[0].CarriedScience);
        Assert.Equal("gravityScan@MinmusInSpaceLowLowlands", carried.SubjectId);
        Assert.Equal(60, carried.Data);
    }

    [Fact]
    public void SubjectIdsDropSpacesFromBiomes()
    {
        Assert.Equal("gravityScan@MinmusInSpaceLowGreaterFlats", ScienceGaps.SubjectId("gravityScan", "Minmus", ScienceSituation.InSpaceLow, "Greater Flats"));
        Assert.Equal("infraredTelescope@KerbinInSpaceHigh", ScienceGaps.SubjectId("infraredTelescope", "Kerbin", ScienceSituation.InSpaceHigh, null));
    }

    [Fact]
    public void NextRunUsesWhatsLeftInTheSubject()
    {
        // Kerbin space high, Grasslands: 16.8 of 33 earned, so 1 − 16.8/33 = 0.491 left; a run is 20 × 1.5 × 0.491 = 14.73.
        var entry = ScienceGaps.Entry(Gravity, Kerbin, ScienceSituation.InSpaceHigh, "Grasslands", Save.ScienceSubjects, NoneCarried);
        Assert.Equal(14.727, entry.NextRun, 3);
        Assert.Equal(16.2, entry.Left, 6);

        // Highlands has no record: the whole cap, 22 × 1.5 = 33, and a first run pays 20 × 1.5 = 30.
        var fresh = ScienceGaps.Entry(Gravity, Kerbin, ScienceSituation.InSpaceHigh, "Highlands", Save.ScienceSubjects, NoneCarried);
        Assert.Equal(30, fresh.NextRun, 6);
        Assert.Equal(33, fresh.Cap, 6);
    }

    [Fact]
    public void CarriedCopiesComeHomeFirst()
    {
        // The probe carries one full run (60 data) for Minmus space low Lowlands: it'll earn 60/3 × 4 = 80 of 88,
        // leaving 1 − 80/88 = 0.0909, so another run pays 20 × 4 × 0.0909 = 7.27.
        var entry = ScienceGaps.Entry(Gravity, Minmus, ScienceSituation.InSpaceLow, "Lowlands", Save.ScienceSubjects, ScienceGaps.Carried(Save));
        Assert.Equal(1, entry.CarriedCopies);
        Assert.Equal(7.273, entry.NextRun, 3);
        Assert.Equal(8, entry.Left, 6);
    }

    [Fact]
    public void AboardCountsPartsAndCrew()
    {
        var probe = Save.Vessels[0];
        // No crew: the pod's crew report and the EVA report don't count.
        Assert.Equal(["gravityScan"], ScienceGaps.Aboard(probe, Catalog).Select(e => e.Id));

        var crewed = probe with { Parts = [.. probe.Parts.Take(2), probe.Parts[2] with { Crew = ["Jeb"] }] };
        Assert.Equal(["gravityScan", "crewReport", "evaReport"], ScienceGaps.Aboard(crewed, Catalog).Select(e => e.Id));
    }

    [Fact]
    public void TableHasBiomeRowsAndAnAnywhereRow()
    {
        var table = ScienceGaps.Table(Kerbin, [Gravity, Infrared], Save.ScienceSubjects, NoneCarried);
        // Gravity works landed, splashed and in space; the telescope in space high. No flying column.
        Assert.Equal([ScienceSituation.SrfLanded, ScienceSituation.SrfSplashed, ScienceSituation.InSpaceLow, ScienceSituation.InSpaceHigh], table.Situations);
        Assert.Equal(["Grasslands", "Highlands", "LaunchPad", ScienceGapTable.AnywhereRow], table.Rows.Select(r => r.Biome));

        var grasslands = table.Rows[0].Cell(ScienceSituation.InSpaceHigh)!;
        Assert.Equal("gravityScan", Assert.Single(grasslands.Entries).ExperimentId);
        // The telescope's subject has no biome, so it's only in the Anywhere row.
        var anywhere = table.Rows[^1].Cell(ScienceSituation.InSpaceHigh)!;
        Assert.Equal("infraredTelescope@KerbinInSpaceHigh", Assert.Single(anywhere.Entries).SubjectId);
        // The launch pad is a landed-only mini-biome.
        Assert.Empty(table.Rows[2].Cell(ScienceSituation.InSpaceHigh)!.Entries);
        Assert.Equal("gravityScan@KerbinSrfLandedLaunchPad", Assert.Single(table.Rows[2].Cell(ScienceSituation.SrfLanded)!.Entries).SubjectId);
    }

    [Fact]
    public void AirlessBodiesHaveNoFlyingOrAtmosphereExperiments()
    {
        var table = ScienceGaps.Table(Minmus, [Gravity, Atmosphere, Crew], Save.ScienceSubjects, NoneCarried);
        Assert.Equal([ScienceSituation.SrfLanded, ScienceSituation.InSpaceLow, ScienceSituation.InSpaceHigh], table.Situations);
        Assert.DoesNotContain(table.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Entries), e => e.ExperimentId == "atmosphereAnalysis");
    }

    [Fact]
    public void BodyWithoutBiomesGivesOneSubjectPerSituation()
    {
        var table = ScienceGaps.Table(Sun, [Gravity], Save.ScienceSubjects, NoneCarried);
        var row = Assert.Single(table.Rows);
        Assert.Equal(ScienceGapTable.AnywhereRow, row.Biome);
        Assert.Equal("gravityScan@SunInSpaceHigh", Assert.Single(row.Cell(ScienceSituation.InSpaceHigh)!.Entries).SubjectId);
    }
}

/// <summary>Reading the add-on's tab-separated answers.</summary>
public sealed class ScienceCatalogReaderTests
{
    [Fact]
    public void ParsesTheAddOnsLines()
    {
        var catalog = ScienceCatalogReader.Parse(
            ["gravityScan\tGravity Scan\t20\t22\t3\t51\t51\t0\t0"],
            ["sensorGravimeter\tgravityScan", "kerbalEVA\tevaReport,surfaceSample"],
            ["Kerbin\t0.3\t0.4\t0.7\t0.9\t1\t1.5\t1\t1\t1\t18000\t70000\tGrasslands|Highlands\tLaunchPad|Runway"],
            DateTime.UnixEpoch);
        var gravity = Assert.Single(catalog.Experiments);
        Assert.True(gravity.BiomeMatters(ScienceSituation.InSpaceHigh));
        Assert.Equal(3, gravity.DataScale);
        Assert.Equal(["evaReport", "surfaceSample"], catalog.PartExperiments["kerbalEVA"]);
        var kerbin = catalog.Body("Kerbin")!;
        Assert.Equal(1.5, kerbin.Multiplier(ScienceSituation.InSpaceHigh));
        Assert.Equal(["Grasslands", "Highlands"], kerbin.Biomes);
        Assert.Equal(["LaunchPad", "Runway"], kerbin.MiniBiomes);
        Assert.True(kerbin.HasOcean);
    }
}

/// <summary>The biome map: where each biome is, and what it pays in a situation.</summary>
public sealed class ScienceTransmitTests
{
    private static readonly ScienceExperimentDef Gravity = new("gravityScan", "Gravity Scan", 20, 22, 3, 51, 51, false, false);
    private static readonly ScienceBody Minmus = new("Minmus", 5, 0, 0, 0, 4, 2.5, false, false, true, 0, 30_000, ["Lowlands"], []);
    private static readonly IReadOnlyDictionary<string, SavedScienceSubject> Nothing = new Dictionary<string, SavedScienceSubject>();

    [Fact]
    public void Sending_home_earns_the_parts_share_and_fills_the_subject_only_that_much()
    {
        // A copy aboard (60 data, xmit 0.4): recovered it earns 80 of 88; sent it earns 32.
        var carried = new Dictionary<string, IReadOnlyList<CarriedScience>>
        {
            ["gravityScan@MinmusInSpaceLowLowlands"] = [new("gravityScan@MinmusInSpaceLowLowlands", 60, 0.4)],
        };
        var entry = ScienceGaps.Entry(Gravity, Minmus, ScienceSituation.InSpaceLow, "Lowlands", Nothing, carried, transmit: 0.4);
        Assert.Equal(80 * (1 - 80.0 / 88), entry.Recovered, 6);      // 7.27
        Assert.Equal(80 * (1 - 32.0 / 88) * 0.4, entry.Sent, 6);     // 20.36
        Assert.Equal(entry.Recovered, entry.NextRun, 9);

        var sent = ScienceGaps.Entry(Gravity, Minmus, ScienceSituation.InSpaceLow, "Lowlands", Nothing, carried, 0.4, ScienceCounting.Transmitted);
        Assert.Equal(sent.Sent, sent.NextRun, 9);
        Assert.Equal(88 - 32, sent.Left, 6);
    }

    [Fact]
    public void The_table_counts_the_way_it_is_asked()
    {
        var none = new Dictionary<string, IReadOnlyList<CarriedScience>>();
        var xmit = new Dictionary<string, double> { ["gravityScan"] = 0.4 };
        var sent = ScienceGaps.Table(Minmus, [Gravity], Nothing, none, xmit, ScienceCounting.Transmitted);
        Assert.Equal(32, sent.Rows[0].Cell(ScienceSituation.InSpaceLow)!.NextRun, 6);
        var both = ScienceGaps.Table(Minmus, [Gravity], Nothing, none, xmit, ScienceCounting.Both);
        Assert.Equal(80, both.Rows[0].Cell(ScienceSituation.InSpaceLow)!.NextRun, 6);
        Assert.Equal(32, both.Rows[0].Cell(ScienceSituation.InSpaceLow)!.Sent, 6);
    }

    [Fact]
    public void Transmit_fractions_come_from_the_parts_aboard_and_the_catalog_reads_them()
    {
        var catalog = ScienceCatalogReader.Parse(
            ["gravityScan\tGravity Scan\t20\t22\t3\t51\t51\t0\t0", "evaReport\tEVA Report\t8\t8\t1\t63\t23\t0\t0"],
            ["GravMax\tgravityScan\t0.4", "kerbalEVA\tevaReport,surfaceSample\t1,0.25", "oldPart\tgravityScan"],
            [], DateTime.UnixEpoch);
        Assert.Equal(0.4, catalog.PartTransmit["GravMax"]["gravityScan"]);
        Assert.Equal(0.25, catalog.PartTransmit["kerbalEVA"]["surfaceSample"]);
        Assert.False(catalog.PartTransmit.ContainsKey("oldPart"));

        var all = ScienceGaps.TransmitFractions(null, catalog);
        Assert.Equal(0.4, all["gravityScan"]);
        Assert.Equal(1, all["evaReport"]);
    }
}

public sealed class ScienceMapTests
{
    [Fact]
    public void BiomeAtFindsTheNearestGridPoint()
    {
        const double step = 10;
        int rows = SurfaceBake.GridRows(step), columns = SurfaceBake.GridColumns(step);
        // West half biome 0, east half biome 1; the south pole row has none.
        var biomes = Enumerable.Range(0, rows * columns)
            .Select(i => (short)(i / columns == 0 ? -1 : SurfaceBake.ColumnLongitude(step, i % columns) < 0 ? 0 : 1)).ToArray();
        var map = new ScienceBiomeMap("Minmus", step, 60_000, 40_400, 0, biomes, ["West", "East"], DateTime.UnixEpoch);
        Assert.Equal("West", map.BiomeAt(10, -90));
        Assert.Equal("East", map.BiomeAt(10, 90));
        Assert.Equal("East", map.BiomeAt(10, 450)); // longitudes wrap
        Assert.Null(map.BiomeAt(-90, 0));
    }

    [Fact]
    public void BiomeValuesAndTheBestSituation()
    {
        var gravity = new ScienceExperimentDef("gravityScan", "Gravity Scan", 20, 22, 3, 51, 51, false, false);
        var minmus = new ScienceBody("Minmus", 5, 0, 0, 0, 4, 2.5, false, false, true, 0, 30_000, ["Lowlands", "Highlands"], []);
        var subjects = new Dictionary<string, SavedScienceSubject>
        {
            ["gravityScan@MinmusSrfLandedLowlands"] = new("gravityScan@MinmusSrfLandedLowlands", 110, 110, 5),
            ["gravityScan@MinmusSrfLandedHighlands"] = new("gravityScan@MinmusSrfLandedHighlands", 110, 110, 5),
        };
        var table = ScienceGaps.Table(minmus, [gravity], subjects, new Dictionary<string, IReadOnlyList<CarriedScience>>());
        var low = ScienceMapping.BiomeValues(table, ScienceSituation.InSpaceLow);
        Assert.Equal(80, low["Lowlands"], 6);
        // Landed is all done, so space low (80 a biome) beats space high (50).
        Assert.Equal(ScienceSituation.InSpaceLow, ScienceMapping.BestSituation(table));
        Assert.Equal(0, ScienceMapping.BiomeValues(table, ScienceSituation.SrfLanded)["Highlands"]);
    }
}

public sealed class SciencePassTests
{
    private const double MinmusMu = 1.7658e9;
    private const double MinmusRadius = 60_000;
    private static readonly ScienceExperimentDef Gravity = new("gravityScan", "Gravity Scan", 20, 22, 3, 51, 51, false, false);
    private static readonly ScienceExperimentDef Magnetometer = new("magnetometer", "Magnetometer", 45, 45, 1, 48, 0, false, false);
    private static readonly ScienceBody Minmus = new("Minmus", 5, 0, 0, 0, 4, 2.5, false, false, true, 0, 30_000, ["North", "South"], []);
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<CarriedScience>> NoneCarried = new Dictionary<string, IReadOnlyList<CarriedScience>>();

    /// <summary>Northern hemisphere "North", southern "South"; Minmus's size and spin.</summary>
    private static ScienceBiomeMap Hemispheres()
    {
        const double step = 1;
        int rows = SurfaceBake.GridRows(step), columns = SurfaceBake.GridColumns(step);
        var biomes = Enumerable.Range(0, rows * columns).Select(i => (short)(SurfaceBake.RowLatitude(step, i / columns) >= 0 ? 0 : 1)).ToArray();
        return new ScienceBiomeMap("Minmus", step, MinmusRadius, 40_400, 0, biomes, ["North", "South"], DateTime.UnixEpoch);
    }

    private static ScienceGapTable Table(params ScienceExperimentDef[] experiments) =>
        ScienceGaps.Table(Minmus, experiments, new Dictionary<string, SavedScienceSubject>(), NoneCarried);

    [Fact]
    public void A_polar_orbit_alternates_hemispheres_in_space_low()
    {
        var orbit = new OrbitalElements(MinmusRadius + 20_000, 0, 90, 0, 0, 0, 0); // starts over the equator, heading north
        var period = Kepler.OrbitalPeriod(orbit, MinmusMu);
        var passes = SciencePasses.Upcoming(orbit, MinmusMu, Minmus, Hemispheres(), Table(Gravity), 0, orbits: 2);

        Assert.True(passes.Count >= 4);
        Assert.All(passes, p => Assert.Equal(ScienceSituation.InSpaceLow, p.Situation));
        Assert.All(passes, p => Assert.Equal(80, p.NextRun, 6)); // 20 × 4, nothing earned yet
        Assert.Equal("North", passes[0].Biome);
        Assert.Equal(0, passes[0].StartUt);
        for (var i = 1; i < passes.Count; i++)
        {
            Assert.NotEqual(passes[i - 1].Biome, passes[i].Biome);
            Assert.Equal(passes[i - 1].EndUt, passes[i].StartUt);
        }
        // Half an orbit over each hemisphere.
        Assert.Equal(period / 2, passes[1].EndUt - passes[1].StartUt, period * 0.02);
    }

    [Fact]
    public void An_elliptical_orbit_crosses_the_space_high_line()
    {
        // Periapsis 10 km, apoapsis 100 km: space low near periapsis, space high (above 30 km) the rest.
        var a = MinmusRadius + (10_000 + 100_000) / 2.0;
        var e = 45_000 / a;
        var orbit = new OrbitalElements(a, e, 0, 0, 0, 0, 0);
        var passes = SciencePasses.Upcoming(orbit, MinmusMu, Minmus, Hemispheres(), Table(Gravity), 0, orbits: 1);

        Assert.Contains(passes, p => p.Situation == ScienceSituation.InSpaceLow);
        Assert.Contains(passes, p => p.Situation == ScienceSituation.InSpaceHigh && Math.Abs(p.NextRun - 50) < 1e-6); // 20 × 2.5
    }

    [Fact]
    public void An_experiment_where_biome_does_not_matter_gives_one_stretch_per_situation()
    {
        var orbit = new OrbitalElements(MinmusRadius + 20_000, 0, 90, 0, 0, 0, 0);
        var passes = SciencePasses.Upcoming(orbit, MinmusMu, Minmus, Hemispheres(), Table(Magnetometer), 0, orbits: 2);

        var pass = Assert.Single(passes);
        Assert.Null(pass.Biome);
        Assert.Equal(180, pass.NextRun, 6); // 45 × 4
    }

    [Fact]
    public void Nothing_is_shown_past_where_the_orbit_meets_the_ground_or_where_it_is_done()
    {
        // Periapsis below the surface: stops there.
        var a = MinmusRadius + 10_000;
        var orbit = new OrbitalElements(a, 30_000 / a, 90, 0, 0, Math.PI, 0); // starts at apoapsis
        var period = Kepler.OrbitalPeriod(orbit, MinmusMu);
        var passes = SciencePasses.Upcoming(orbit, MinmusMu, Minmus, Hemispheres(), Table(Gravity), 0, orbits: 3);
        Assert.NotEmpty(passes);
        Assert.True(passes[^1].EndUt < period);

        // Everything earned already: no passes at all.
        var done = Minmus.Biomes.SelectMany(b => new[] { ScienceSituation.InSpaceLow, ScienceSituation.InSpaceHigh }
                .Select(s => ScienceGaps.SubjectId("gravityScan", "Minmus", s, b)))
            .ToDictionary(id => id, id => new SavedScienceSubject(id, 100, 100, 4));
        var finished = ScienceGaps.Table(Minmus, [Gravity], done, NoneCarried);
        var circular = new OrbitalElements(MinmusRadius + 20_000, 0, 90, 0, 0, 0, 0);
        Assert.Empty(SciencePasses.Upcoming(circular, MinmusMu, Minmus, Hemispheres(), finished, 0));
    }

    [Fact]
    public void The_situation_follows_the_altitude_lines()
    {
        var kerbin = new ScienceBody("Kerbin", 0.3, 0.4, 0.7, 0.9, 1, 1.5, true, true, true, 18_000, 250_000, [], []) { AtmosphereHeightM = 70_000 };
        Assert.Equal(ScienceSituation.FlyingLow, kerbin.InFlightAt(10_000));
        Assert.Equal(ScienceSituation.FlyingHigh, kerbin.InFlightAt(50_000));
        Assert.Equal(ScienceSituation.InSpaceLow, kerbin.InFlightAt(80_000));
        Assert.Equal(ScienceSituation.InSpaceHigh, kerbin.InFlightAt(300_000));
    }
}

