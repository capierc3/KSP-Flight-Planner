using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Science
{
    /// <summary>
    /// "Scan now" (docs/SCIENCE.md stage 4): when the vessel you're flying enters a biome or situation where an experiment
    /// aboard still pays at least the threshold, a screen message says what and how much. Once per entry, never every frame.
    /// It never runs an experiment; that stays the player's call. Values follow docs/SCIENCE.md, "What a run of an experiment earns": base × the body's
    /// multiplier × (1 − earned ÷ cap), after the copies aboard. An uncrewed vessel only ever sends its data home, so it is
    /// judged by the sent value (× the part's transmit fraction); a crewed one by the recovered value, with the sent one shown too.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class ScanNow : MonoBehaviour
    {
        /// <summary>How often to look where the vessel is, in real seconds.</summary>
        const float LookEvery = 0.5f;

        float _nextLook;
        string _where;
        Guid _vesselId;

        void Update()
        {
            if (Time.unscaledTime < _nextLook) return;
            _nextLook = Time.unscaledTime + LookEvery;

            var vessel = FlightGlobals.ActiveVessel;
            var settings = ScienceSettings.Current;
            if (vessel == null || ResearchAndDevelopment.Instance == null || !settings.ScanNow || vessel.mainBody == null)
            {
                _where = null;
                return;
            }

            var situation = ScienceUtil.GetExperimentSituation(vessel);
            var body = vessel.mainBody;
            var biome = !string.IsNullOrEmpty(vessel.landedAt)
                ? Vessel.GetLandedAtString(vessel.landedAt)
                : ScienceUtil.GetExperimentBiome(body, vessel.latitude, vessel.longitude);
            var where = body.bodyName + "|" + situation + "|" + biome;
            if (vessel.id == _vesselId && where == _where) return;
            _vesselId = vessel.id;
            _where = where;

            var pays = WhatPays(vessel, body, situation, biome, settings.PaysAtLeast);
            if (pays.Count == 0) return;
            var place = string.IsNullOrEmpty(biome) ? body.bodyName : body.bodyName + " " + ScienceUtil.GetBiomedisplayName(body, biome);
            var line = "Scan now: over " + place + " (" + Label(situation) + "): " + string.Join(", ", pays.ToArray());
            ScreenMessages.PostScreenMessage(line, 8f, ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>"Gravity Scan 34" for each experiment aboard that pays at least the threshold here, most first.</summary>
        static List<string> WhatPays(Vessel vessel, CelestialBody body, ExperimentSituations situation, string biome, float threshold)
        {
            var carried = new Dictionary<string, List<ScienceData>>();
            foreach (var container in vessel.FindPartModulesImplementing<IScienceDataContainer>())
                foreach (var data in container.GetData() ?? new ScienceData[0])
                {
                    if (data == null || string.IsNullOrEmpty(data.subjectID)) continue;
                    List<ScienceData> list;
                    if (!carried.TryGetValue(data.subjectID, out list)) carried[data.subjectID] = list = new List<ScienceData>();
                    list.Add(data);
                }

            var modules = vessel.FindPartModulesImplementing<ModuleScienceExperiment>()
                .Where(m => !m.Inoperable && !string.IsNullOrEmpty(m.experimentID)).ToList();
            var ids = modules.Select(m => m.experimentID).ToList();
            var probe = vessel.GetCrewCount() == 0;
            if (probe) ids.Remove("crewReport");
            else
            {
                // A kerbal can step out: EVA report anywhere, surface sample on the ground.
                ids.Add("evaReport");
                ids.Add("surfaceSample");
            }

            var found = new List<KeyValuePair<double, string>>();
            foreach (var id in ids.Distinct())
            {
                var e = ResearchAndDevelopment.GetExperiment(id);
                if (e == null || !e.IsAvailableWhile(situation, body)) continue;
                var subjectId = e.id + "@" + body.name + situation + (e.BiomeIsRelevantWhile(situation) ? (biome ?? "").Replace(" ", "") : "");
                var subject = ResearchAndDevelopment.GetSubjectByID(subjectId);
                var multiplier = subject != null ? subject.subjectValue : Multiplier(body, situation);
                var cap = subject != null ? subject.scienceCap : e.scienceCap * multiplier;
                double earned = subject != null ? subject.science : 0;
                var earnedSent = earned;
                List<ScienceData> copies;
                if (carried.TryGetValue(subjectId, out copies))
                    foreach (var data in copies)
                    {
                        earned += Value(data.dataAmount, e.dataScale, multiplier, earned, cap);
                        earnedSent += Value(data.dataAmount, e.dataScale, multiplier, earnedSent, cap) * data.baseTransmitValue;
                    }
                var xmit = Transmit(modules, id);
                var recovered = Value(e.baseValue * e.dataScale, e.dataScale, multiplier, earned, cap);
                var sent = Value(e.baseValue * e.dataScale, e.dataScale, multiplier, earnedSent, cap) * xmit;
                var judged = probe ? sent : recovered;
                if (judged < threshold) continue;
                var title = e.experimentTitle + (id == "evaReport" || id == "surfaceSample" ? " (EVA)" : "");
                found.Add(new KeyValuePair<double, string>(judged, probe
                    ? title + " " + Points(sent) + " sent"
                    : title + " " + Points(recovered) + " (sent " + Points(sent) + ")"));
            }
            return found.OrderByDescending(f => f.Key).Select(f => f.Value).ToList();
        }

        internal static string Points(double value)
        {
            return value >= 10 ? value.ToString("N0") : value.ToString("0.#");
        }

        /// <summary>The share of a result's value the experiment earns when sent home: the best of the vessel's parts, else the kerbal's EVA part.</summary>
        static double Transmit(List<ModuleScienceExperiment> modules, string id)
        {
            var own = modules.Where(m => m.experimentID == id).Select(m => (double)m.xmitDataScalar).DefaultIfEmpty(-1).Max();
            if (own >= 0) return own;
            var eva = PartLoader.getPartInfoByName("kerbalEVA");
            if (eva != null && eva.partPrefab != null)
                foreach (var m in eva.partPrefab.Modules.OfType<ModuleScienceExperiment>())
                    if (m.experimentID == id) return m.xmitDataScalar;
            return 1;
        }

        /// <summary>KSP's value of recovered data: data ÷ dataScale × multiplier × (1 − earned ÷ cap), never below 0.</summary>
        internal static double Value(double data, double dataScale, double multiplier, double earned, double cap)
        {
            return cap <= 0 || dataScale <= 0 ? 0 : Math.Max(0, data / dataScale * multiplier * (1 - earned / cap));
        }

        internal static float Multiplier(CelestialBody body, ExperimentSituations situation)
        {
            var v = body.scienceValues;
            switch (situation)
            {
                case ExperimentSituations.SrfLanded: return v.LandedDataValue;
                case ExperimentSituations.SrfSplashed: return v.SplashedDataValue;
                case ExperimentSituations.FlyingLow: return v.FlyingLowDataValue;
                case ExperimentSituations.FlyingHigh: return v.FlyingHighDataValue;
                case ExperimentSituations.InSpaceLow: return v.InSpaceLowDataValue;
                case ExperimentSituations.InSpaceHigh: return v.InSpaceHighDataValue;
                default: return 0;
            }
        }

        internal static string Label(ExperimentSituations situation)
        {
            switch (situation)
            {
                case ExperimentSituations.SrfLanded: return "landed";
                case ExperimentSituations.SrfSplashed: return "splashed";
                case ExperimentSituations.FlyingLow: return "flying low";
                case ExperimentSituations.FlyingHigh: return "flying high";
                case ExperimentSituations.InSpaceLow: return "space low";
                case ExperimentSituations.InSpaceHigh: return "space high";
                default: return situation.ToString();
            }
        }
    }
}
