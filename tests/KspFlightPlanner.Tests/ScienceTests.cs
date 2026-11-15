using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.Tests;

/// <summary>Science gaps: reading R&amp;D and carried data from the save, and what's left per biome (docs/SCIENCE.md, docs/MATH.md §14.1).</summary>
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
