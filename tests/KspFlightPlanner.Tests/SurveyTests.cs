using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Tests;

/// <summary>The ore survey: reading the save, coverage from ground tracks, reports (docs/SURVEY.md, docs/MATH.md §10).</summary>
public sealed class SurveyTests
{
    private const double MinmusRadius = 60_000;
    private const double MinmusMu = 1.7658e9;
    private const double MinmusDay = 40_400;

    /// <summary>Two biomes: West (180° W to 0°) at 4% estimate, East at 7%; true ore rises toward 30° N 90° E.</summary>
    private static SurveyMap Map()
    {
        const double step = 1;
        int rows = SurfaceBake.GridRows(step), columns = SurfaceBake.GridColumns(step);
        var abundance = new float[rows * columns];
        var estimate = new float[rows * columns];
        var biomes = new short[rows * columns];
        for (var i = 0; i < abundance.Length; i++)
        {
            var lat = SurfaceBake.RowLatitude(step, i / columns);
            var lon = SurfaceBake.ColumnLongitude(step, i % columns);
            var east = lon >= 0;
            biomes[i] = (short)(east ? 1 : 0);
            estimate[i] = east ? 0.07f : 0.04f;
            var distance = Landing.SurfaceDistance(1, lat, lon, 30, 90);
            abundance[i] = (float)(0.12 - 0.02 * distance);
        }
        return new SurveyMap("Minmus", step, MinmusRadius, MinmusDay, 0, abundance, estimate, biomes, ["West", "East"], DateTime.UtcNow);
    }

    private static OrbitalElements Circular(double altitudeM, double inclinationDeg) =>
        new(MinmusRadius + altitudeM, 0, inclinationDeg, 0, 0, 0, 0);

    private static SurveyReading Reading(double ut, OrbitalElements orbit, bool engineer = true, bool survey = true, bool narrow = true, bool scanned = true) =>
        new(ut, "Minmus", orbit, engineer ? ["Bill Kerman"] : [], survey, narrow, scanned);

    private static int Index(SurveyMap map, double lat, double lon) =>
        (int)Math.Round(lat + 90) * map.Columns + (int)Math.Round(lon + 180) % map.Columns;

    [Fact]
    public void Save_gives_crew_traits_scanned_bodies_analysed_biomes_and_part_modules()
    {
        var save = KspSaveReader.Parse("""
            GAME
            {
                Title = Test
                Mode = SCIENCE_SANDBOX
                FLIGHTSTATE
                {
                    UT = 1000
                    VESSEL
                    {
                        pid = abc
                        name = Min Station
                        type = Station
                        sit = ORBITING
                        ORBIT
                        {
                            SMA = 90000
                            ECC = 0
                            INC = 85
                            LAN = 0
                            LPE = 0
                            MNA = 0
                            EPH = 0
                            REF = 3
                        }
                        PART
                        {
                            name = mk1pod.v2
                            crew = Bill Kerman
                            crew = Jebediah Kerman
                            MODULE
                            {
                                name = ModuleCommand
                            }
                        }
                        PART
                        {
                            name = SurveyScanner
                            MODULE
                            {
                                name = ModuleOrbitalSurveyor
                            }
                        }
                        PART
                        {
                            name = OrbitalScanner
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
                ROSTER
                {
                    KERBAL
                    {
                        name = Jebediah Kerman
                        trait = Pilot
                    }
                    KERBAL
                    {
                        name = Bill Kerman
                        trait = Engineer
                    }
                }
                SCENARIO
                {
                    name = ResourceScenario
                    RESOURCE_SETTINGS
                    {
                        PLANET_SCAN_DATA
                        {
                            PlanetId = 3
                        }
                        BIOME_LOCK_DATA
                        {
                            PlanetId = 3
                            BiomeName = Greater Flats
                        }
                    }
                }
            }
            """);

        Assert.Equal("Engineer", save.CrewTraits["Bill Kerman"]);
        Assert.Contains("Minmus", save.ScannedBodies);
        Assert.Contains(("Minmus", "Greater Flats"), save.AnalysedBiomes);
        var station = SurveyStation.From(save.Vessels[0], save);
        Assert.NotNull(station);
        Assert.Equal(["Bill Kerman"], station.Engineers);
        Assert.True(station.HasSurveyScanner);
        Assert.True(station.HasNarrowBand);
        Assert.True(station.IsSurveying);
    }

    [Fact]
    public void Surface_scanning_module_is_not_a_narrow_band()
    {
        var surface = new SavedPart("SurfaceScanner", 0.035, [], [], []) { ModuleNames = ["ModuleResourceScanner", "ModuleAnalysisResource", "ModuleBiomeScanner"] };
        Assert.False(SurveyStation.IsNarrowBand(surface));
        Assert.False(SurveyStation.IsSurveyScanner(surface));
    }

    [Fact]
    public void A_pass_covers_a_strip_five_degrees_either_side_of_the_ground_track()
    {
        var map = Map();
        var coverage = new SurveyCoverage(1);
        // A polar orbit over 0° longitude at UT 0 (θ₀ = 0, Ω = 0); a quarter orbit carries it from the equator to the pole.
        var orbit = Circular(30_000, 90);
        var quarter = Kepler.OrbitalPeriod(orbit, MinmusMu) / 4;
        coverage.Follow(orbit, 0, quarter, map, MinmusMu, rough: true, estimate: true);

        Assert.False(float.IsNaN(coverage.Estimate[Index(map, 0, 0)]));
        Assert.False(float.IsNaN(coverage.Estimate[Index(map, 0, 5)]));
        Assert.True(float.IsNaN(coverage.Estimate[Index(map, 0, 6)]));
        Assert.True(float.IsNaN(coverage.Estimate[Index(map, 0, 90)]));
        Assert.True(float.IsNaN(coverage.Estimate[Index(map, -10, 0)]));
        // The pole was reached at the end of the quarter, the equator at the start.
        Assert.Equal(0, coverage.Estimate[Index(map, 0, 0)]);
        Assert.InRange(coverage.Estimate[Index(map, 90, 0)], quarter * 0.9, quarter * 1.01);
    }

    [Fact]
    public void An_equatorial_orbit_never_maps_far_from_the_equator()
    {
        var map = Map();
        var coverage = new SurveyCoverage(1);
        coverage.Follow(Circular(30_000, 0), 0, 3 * MinmusDay, map, MinmusMu, rough: false, estimate: true);

        Assert.False(float.IsNaN(coverage.Estimate[Index(map, 0, 123)]));
        Assert.False(float.IsNaN(coverage.Estimate[Index(map, 4, -77)]));
        Assert.True(float.IsNaN(coverage.Estimate[Index(map, 6, -77)]));
        Assert.All(coverage.Rough, t => Assert.True(float.IsNaN(t)));
    }

    [Fact]
    public void A_polar_orbit_maps_everything_within_a_few_weeks()
    {
        // 50 km: the ground track moves 48.6° west each orbit; all of Minmus in about 8 Kerbin days. (A circular 30 km orbit
        // is almost exactly a tenth of Minmus's day, so its tracks nearly repeat and it takes about 92 days.)
        var map = Map();
        var coverage = new SurveyCoverage(1);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        coverage.Follow(Circular(50_000, 89), 0, 60 * 21_600.0, map, MinmusMu, rough: false, estimate: true);
        Assert.All(coverage.Estimate, t => Assert.False(float.IsNaN(t)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void The_narrow_band_maps_nothing_above_500_km()
    {
        var map = Map();
        var coverage = new SurveyCoverage(1);
        Assert.Equal(0, coverage.Follow(Circular(600_000, 90), 0, 10_000, map, MinmusMu, rough: false, estimate: true));
    }

    [Fact]
    public void Nothing_is_credited_unless_both_readings_have_an_engineer()
    {
        var map = Map();
        var orbit = Circular(30_000, 90);
        var coverage = new SurveyCoverage(1);
        Assert.Equal(0, coverage.Credit(Reading(0, orbit), Reading(5_000, orbit, engineer: false), map, MinmusMu));
        Assert.Equal(0, coverage.Credit(Reading(0, orbit, engineer: false), Reading(5_000, orbit), map, MinmusMu));
        Assert.True(coverage.Credit(Reading(0, orbit), Reading(5_000, orbit), map, MinmusMu) > 0);
    }

    [Fact]
    public void Rough_needs_the_survey_scanner_and_the_body_scanned_in_game()
    {
        var map = Map();
        var orbit = Circular(30_000, 90);
        var notScanned = new SurveyCoverage(1);
        notScanned.Credit(Reading(0, orbit, scanned: false), Reading(5_000, orbit, scanned: false), map, MinmusMu);
        Assert.All(notScanned.Rough, t => Assert.True(float.IsNaN(t)));
        Assert.Contains(notScanned.Estimate, t => !float.IsNaN(t));

        var noScanner = new SurveyCoverage(1);
        noScanner.Credit(Reading(0, orbit, survey: false), Reading(5_000, orbit), map, MinmusMu);
        Assert.All(noScanner.Rough, t => Assert.True(float.IsNaN(t)));
    }

    [Fact]
    public void A_changed_orbit_covers_the_second_half_of_the_time()
    {
        var map = Map();
        var polar = Circular(30_000, 90);
        var equatorial = Circular(30_000, 0);
        var coverage = new SurveyCoverage(1);
        coverage.Credit(Reading(0, polar), Reading(20_000, equatorial), map, MinmusMu);
        // The equatorial orbit only flew from 10,000 s on.
        var equatorOnly = Index(map, 0, 120);
        Assert.InRange(coverage.Estimate[equatorOnly], 10_000, 20_000);
    }

    [Fact]
    public void Going_back_in_time_forgets_later_coverage()
    {
        var map = Map();
        var coverage = new SurveyCoverage(1);
        coverage.Follow(Circular(30_000, 90), 0, 10_000, map, MinmusMu, rough: true, estimate: true);
        var before = coverage.Estimate.Count(t => !float.IsNaN(t));
        coverage.ForgetAfter(1_000);
        var after = coverage.Estimate.Count(t => !float.IsNaN(t));
        Assert.InRange(after, 1, before - 1);
        Assert.All(coverage.Estimate.Where(t => !float.IsNaN(t)), t => Assert.True(t <= 1_000));
        Assert.All(coverage.Rough.Where(t => !float.IsNaN(t)), t => Assert.True(t <= 1_000));
    }

    [Fact]
    public void Reports_come_at_half_coverage_and_best_spots_only_once_analysed()
    {
        var map = Map();
        var coverage = new SurveyCoverage(1);
        // Cover the whole East biome at UT 100 (narrow-band) and nothing of West.
        for (var i = 0; i < map.Count; i++)
            if (map.Biomes[i] == 1) coverage.Estimate[i] = 100;

        var before = SurveyReports.Build(map, coverage, "Minmus", new HashSet<(string, string)>());
        var east = before.Single(b => b.Biome == "East");
        var west = before.Single(b => b.Biome == "West");
        Assert.Equal(SurveyLevel.Estimate, east.Level);
        Assert.Equal("≈ 7%", east.Report);
        Assert.Equal(100, east.EstimateUt);
        Assert.Null(west.Level);
        Assert.Empty(east.Spots);

        var after = SurveyReports.Build(map, coverage, "Minmus", new HashSet<(string, string)> { ("Minmus", "East") });
        east = after.Single(b => b.Biome == "East");
        Assert.Equal(SurveyLevel.Surveyed, east.Level);
        Assert.Equal(3, east.Spots.Count);
        Assert.Equal(30, east.Spots[0].LatitudeDeg, 0.5);
        Assert.Equal(90, east.Spots[0].LongitudeDeg, 0.5);
        Assert.Equal(0.12, east.Spots[0].Abundance, 3);
        Assert.All(east.Spots.Skip(1), s =>
            Assert.True(Landing.SurfaceDistance(1, s.LatitudeDeg, s.LongitudeDeg, 30, 90) >= SurveyReports.SpotSpacingDeg * Math.PI / 180 - 1e-9));
    }

    [Theory]
    [InlineData(0.005, "Trace")]
    [InlineData(0.02, "Poor")]
    [InlineData(0.042, "Fair")]
    [InlineData(0.0703, "Good")]
    [InlineData(0.105, "Rich")]
    public void Rough_reports_in_words(double estimate, string word) => Assert.Equal(word, SurveyReports.Word(estimate));

    [Fact]
    public void Orbital_survey_needs_a_polar_orbit_inside_the_band()
    {
        var minmus = new CelestialBody { Name = "Minmus", EquatorialRadiusM = MinmusRadius, Mu = MinmusMu };
        Assert.Equal((25_000, 300_000), OrbitalSurveyRules.Band(MinmusRadius));

        SurveyStation Station(double altitude, double inclination, bool engineer = true) =>
            new("id", "Min Station", "Minmus", new SavedOrbit(MinmusRadius + altitude, 0, inclination, 0, 0, 0, 0, 3, null),
                engineer ? ["Bill Kerman"] : [], true, true);

        Assert.Empty(OrbitalSurveyRules.Problems(Station(30_000, 85), minmus));
        Assert.Empty(OrbitalSurveyRules.Problems(Station(30_000, 120), minmus)); // the game allows retrograde
        Assert.Empty(OrbitalSurveyRules.Problems(Station(30_000, 85, engineer: false), minmus)); // a probe can do it
        var problems = OrbitalSurveyRules.Problems(Station(20_000, 10), minmus);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("polar"));
        Assert.Contains(problems, p => p.Contains("between 25 km and 300 km"));
    }

    [Fact]
    public void The_engineer_suggests_true_spots_in_analysed_biomes_and_a_spot_deep_inside_the_others()
    {
        var map = Map();
        var coverage = new SurveyCoverage(1);
        Array.Fill(coverage.Estimate, 100f);
        var biomes = SurveyReports.Build(map, coverage, "Minmus", new HashSet<(string, string)> { ("Minmus", "East") });

        var candidates = SurveyReports.Candidates(map, coverage, biomes);
        Assert.Equal(4, candidates.Count); // East's three best spots, then one for West
        Assert.All(candidates.Take(3), c => Assert.True(c.Analysed && c.Biome == "East"));
        Assert.Equal(30, candidates[0].LatitudeDeg, 0.5);
        var west = candidates[3];
        Assert.False(west.Analysed);
        Assert.Equal("≈ 4%", west.Value);
        // West spans 180° W to 1° W: its deepest point is near the equator at 90° W, far from both edges.
        Assert.InRange(west.LongitudeDeg, -100, -80);
        Assert.InRange(west.LatitudeDeg, -10, 10);
    }

    [Fact]
    public void Unmapped_biomes_get_no_suggestion()
    {
        var map = Map();
        var coverage = new SurveyCoverage(1);
        var biomes = SurveyReports.Build(map, coverage, "Minmus", new HashSet<(string, string)>());
        Assert.Empty(SurveyReports.Candidates(map, coverage, biomes));
    }
}
