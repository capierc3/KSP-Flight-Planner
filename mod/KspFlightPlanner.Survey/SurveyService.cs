using System;
using System.Collections.Generic;
using KRPC.Service;
using KRPC.Service.Attributes;

namespace KspFlightPlanner.Survey
{
    /// <summary>
    /// kRPC service "PlannerSurvey": the true resource map, as the game generates it for this save.
    /// It ignores whether the player has scanned a body, so the planner can reveal it at the pace a survey would find it.
    /// Abundances are fractions (0.05 = 5 %), the way KSP stores them.
    /// </summary>
    [KRPCService(Name = "PlannerSurvey", GameScene = GameScene.All)]
    public static class SurveyService
    {
        /// <summary>Most cells one grid call may ask for, so a big request can't freeze the game for long.</summary>
        public const int MaxGridCells = 20000;

        /// <summary>The add-on's version, so the planner can tell which functions it has.</summary>
        [KRPCProcedure]
        public static string Version()
        {
            return typeof(SurveyService).Assembly.GetName().Version.ToString();
        }

        /// <summary>Whether the game's resource map is up (it isn't on the main menu).</summary>
        [KRPCProcedure]
        public static bool Ready()
        {
            return ResourceMap.Instance != null && FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0;
        }

        /// <summary>
        /// Abundance of a resource (e.g. "Ore") at a spot on a body, in degrees, as a drill landed there would mine it.
        /// KSP fades abundance with altitude, and a drill asks at its own altitude, so this asks at the ground's height.
        /// </summary>
        [KRPCProcedure]
        public static double Abundance(string body, string resource, double latitude, double longitude)
        {
            var map = RequireMap();
            var cb = FindBody(body);
            return map.GetAbundance(Request(cb, resource, latitude, longitude, Ground(cb, latitude, longitude)));
        }

        /// <summary>
        /// Abundance at a spot as seen from an altitude above sea level, in metres. KerbNet asks at the craft's altitude,
        /// so this matches the "Tgt" figure KerbNet shows in orbit, which isn't what a drill on the ground gets.
        /// </summary>
        [KRPCProcedure]
        public static double AbundanceAtAltitude(string body, string resource, double latitude, double longitude, double altitude)
        {
            var map = RequireMap();
            return map.GetAbundance(Request(FindBody(body), resource, latitude, longitude, altitude));
        }

        /// <summary>Height of the ground above sea level at a spot, in metres.</summary>
        [KRPCProcedure]
        public static double GroundHeight(string body, double latitude, double longitude)
        {
            var cb = FindBody(body);
            return Ground(cb, latitude, longitude);
        }

        /// <summary>
        /// Ground-level abundances (as <see cref="Abundance"/>) on a latitude/longitude grid, row by row from latMin: rows × cols values, ends included
        /// (rows = 1 samples latMin only). At most <see cref="MaxGridCells"/> cells per call; ask in strips for more.
        /// </summary>
        [KRPCProcedure]
        public static IList<float> AbundanceGrid(string body, string resource,
            double latMin, double latMax, int rows, double lonMin, double lonMax, int cols)
        {
            return Grid(body, rows, cols, latMin, latMax, lonMin, lonMax,
                (map, cb, lat, lon) => map.GetAbundance(Request(cb, resource, lat, lon, Ground(cb, lat, lon))));
        }

        /// <summary>
        /// What the scanners show for a biome nobody has analysed yet, on the same grid as <see cref="AbundanceGrid"/>: KSP's
        /// value without its local variation, divided by 1.5, so one flat value per biome. Whether the biome is analysed
        /// doesn't change it.
        /// </summary>
        [KRPCProcedure]
        public static IList<float> EstimateGrid(string body, string resource,
            double latMin, double latMax, int rows, double lonMin, double lonMax, int cols)
        {
            return Grid(body, rows, cols, latMin, latMax, lonMin, lonMax, (map, cb, lat, lon) =>
            {
                var request = Request(cb, resource, lat, lon, Ground(cb, lat, lon));
                request.ExcludeVariance = true;
                return map.GetAbundance(request);
            });
        }

        /// <summary>The body's biomes, by the names KSP stores in the save (the indices <see cref="BiomeGrid"/> gives).</summary>
        [KRPCProcedure]
        public static IList<string> BiomeNames(string body)
        {
            var names = new List<string>();
            var map = FindBody(body).BiomeMap;
            if (map != null && map.Attributes != null)
                foreach (var a in map.Attributes) names.Add(a.name);
            return names;
        }

        /// <summary>The biome at each grid point as an index into <see cref="BiomeNames"/>, or -1 where there's none.</summary>
        [KRPCProcedure]
        public static IList<int> BiomeGrid(string body, double latMin, double latMax, int rows, double lonMin, double lonMax, int cols)
        {
            CheckGrid(rows, cols);
            var cb = FindBody(body);
            var attributes = cb.BiomeMap != null ? cb.BiomeMap.Attributes : null;
            var values = new List<int>(rows * cols);
            for (int r = 0; r < rows; r++)
            {
                double lat = Step(latMin, latMax, r, rows);
                for (int c = 0; c < cols; c++)
                {
                    double lon = Step(lonMin, lonMax, c, cols);
                    var biome = attributes == null ? null : ResourceUtilities.GetBiome(lat * Math.PI / 180, lon * Math.PI / 180, cb);
                    values.Add(biome == null ? -1 : Array.IndexOf(attributes, biome));
                }
            }
            return values;
        }

        /// <summary>Whether the body's orbital survey has been done (the ore overlay is on).</summary>
        [KRPCProcedure]
        public static bool IsBodyScanned(string body)
        {
            return RequireMap().IsPlanetScanned(FindBody(body).flightGlobalsIndex);
        }

        /// <summary>Whether a biome has been analysed from the ground (by its save name, as <see cref="BiomeNames"/> gives it).</summary>
        [KRPCProcedure]
        public static bool IsBiomeAnalysed(string body, string biome)
        {
            return RequireMap().IsBiomeUnlocked(FindBody(body).flightGlobalsIndex, biome);
        }

        delegate float Sample(ResourceMap map, CelestialBody body, double latitude, double longitude);

        static IList<float> Grid(string body, int rows, int cols, double latMin, double latMax, double lonMin, double lonMax, Sample sample)
        {
            CheckGrid(rows, cols);
            var map = RequireMap();
            var cb = FindBody(body);
            var values = new List<float>(rows * cols);
            for (int r = 0; r < rows; r++)
            {
                double lat = Step(latMin, latMax, r, rows);
                for (int c = 0; c < cols; c++) values.Add(sample(map, cb, lat, Step(lonMin, lonMax, c, cols)));
            }
            return values;
        }

        static void CheckGrid(int rows, int cols)
        {
            if (rows < 1 || cols < 1) throw new ArgumentException("rows and cols must be at least 1");
            if ((long)rows * cols > MaxGridCells)
                throw new ArgumentException("At most " + MaxGridCells + " cells per call; ask in strips");
        }

        static double Step(double from, double to, int i, int n)
        {
            return n == 1 ? from : from + (to - from) * i / (n - 1);
        }

        static double Ground(CelestialBody body, double latitude, double longitude)
        {
            return body.TerrainAltitude(latitude, longitude);
        }

        static AbundanceRequest Request(CelestialBody body, string resource, double latitude, double longitude, double altitude)
        {
            return new AbundanceRequest
            {
                BodyId = body.flightGlobalsIndex,
                ResourceName = resource,
                ResourceType = HarvestTypes.Planetary,
                Latitude = latitude,
                Longitude = longitude,
                Altitude = altitude,
                CheckForLock = false, // the true value, scanned or not
                ExcludeVariance = false,
            };
        }

        static ResourceMap RequireMap()
        {
            if (ResourceMap.Instance == null) throw new InvalidOperationException("The resource map isn't loaded yet; load a save first");
            return ResourceMap.Instance;
        }

        static CelestialBody FindBody(string name)
        {
            foreach (var b in FlightGlobals.Bodies)
                if (string.Equals(b.bodyName, name, StringComparison.OrdinalIgnoreCase)) return b;
            throw new ArgumentException("No body named " + name);
        }
    }
}
