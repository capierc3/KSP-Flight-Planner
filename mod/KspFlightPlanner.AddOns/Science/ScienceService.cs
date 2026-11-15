using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KRPC.Service;
using KRPC.Service.Attributes;

namespace KspFlightPlanner.Science
{
    /// <summary>
    /// kRPC service "PlannerScience": what the game knows about science that the save doesn't hold — the experiment
    /// definitions, which parts run which, and each body's multipliers and biomes (docs/SCIENCE.md). Answers are lines of
    /// tab-separated fields, so the planner needs no kRPC types of ours. Numbers are invariant culture.
    /// </summary>
    [KRPCService(Name = "PlannerScience", GameScene = GameScene.All)]
    public static class ScienceService
    {
        /// <summary>The add-on's version, so the planner can tell which functions it has.</summary>
        [KRPCProcedure]
        public static string Version()
        {
            return typeof(ScienceService).Assembly.GetName().Version.ToString();
        }

        /// <summary>
        /// Every experiment: id, title, baseValue, scienceCap, dataScale, situationMask, biomeMask, requireAtmosphere,
        /// requireNoAtmosphere.
        /// </summary>
        [KRPCProcedure]
        public static IList<string> Experiments()
        {
            var lines = new List<string>();
            foreach (var id in ResearchAndDevelopment.GetExperimentIDs())
            {
                var e = ResearchAndDevelopment.GetExperiment(id);
                if (e == null) continue;
                lines.Add(Join(e.id, Clean(e.experimentTitle), N(e.baseValue), N(e.scienceCap), N(e.dataScale),
                    e.situationMask.ToString(CultureInfo.InvariantCulture), e.biomeMask.ToString(CultureInfo.InvariantCulture),
                    e.requireAtmosphere ? "1" : "0", e.requireNoAtmosphere ? "1" : "0"));
            }
            return lines;
        }

        /// <summary>Each part that runs experiments: part name, then its experiment ids joined by commas.</summary>
        [KRPCProcedure]
        public static IList<string> PartExperiments()
        {
            var lines = new List<string>();
            foreach (var part in PartLoader.LoadedPartsList)
            {
                if (part == null || part.partPrefab == null) continue;
                var ids = part.partPrefab.Modules.OfType<ModuleScienceExperiment>()
                    .Select(m => m.experimentID).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
                if (ids.Count > 0) lines.Add(Join(part.name, string.Join(",", ids.ToArray())));
            }
            return lines;
        }

        /// <summary>
        /// Every body: name, its six situation multipliers (landed, splashed, flying low, flying high, space low, space high),
        /// atmosphere, ocean, solid surface (1/0), flying-high and space-high altitudes in metres, biome names joined by "|",
        /// mini-biome tags joined by "|".
        /// </summary>
        [KRPCProcedure]
        public static IList<string> Bodies()
        {
            var lines = new List<string>();
            foreach (var b in FlightGlobals.Bodies)
            {
                var v = b.scienceValues;
                var biomes = b.BiomeMap != null && b.BiomeMap.Attributes != null
                    ? b.BiomeMap.Attributes.Select(a => a.name).ToArray() : new string[0];
                var minis = b.MiniBiomes != null ? b.MiniBiomes.Select(m => m.TagKey).Where(t => !string.IsNullOrEmpty(t)).ToArray() : new string[0];
                lines.Add(Join(b.bodyName,
                    N(v.LandedDataValue), N(v.SplashedDataValue), N(v.FlyingLowDataValue), N(v.FlyingHighDataValue),
                    N(v.InSpaceLowDataValue), N(v.InSpaceHighDataValue),
                    b.atmosphere ? "1" : "0", b.ocean ? "1" : "0", b.hasSolidSurface && !b.isStar ? "1" : "0",
                    N(v.flyingAltitudeThreshold), N(v.spaceAltitudeThreshold),
                    string.Join("|", biomes), string.Join("|", minis)));
            }
            return lines;
        }

        static string N(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        static string Clean(string text)
        {
            return (text ?? "").Replace('\t', ' ').Replace('\n', ' ');
        }

        static string Join(params string[] fields)
        {
            return string.Join("\t", fields);
        }
    }
}
