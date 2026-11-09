using System.Collections.Generic;

namespace KspFlightPlanner.Refueler
{
    /// <summary>
    /// The Refueler's own part of the save: the player's marks on docked vessels, by name. True for a station tank (its
    /// fuel is the station's), false for a craft of its own. Vessels without a mark follow the rule (docs/MATH.md §11.9).
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT)]
    public sealed class RefuelerScenario : ScenarioModule
    {
        internal static RefuelerScenario Instance { get; private set; }

        internal readonly Dictionary<string, bool> StationMarks = new Dictionary<string, bool>();

        public override void OnAwake() => Instance = this;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public override void OnSave(ConfigNode node)
        {
            foreach (var mark in StationMarks)
            {
                var n = node.AddNode("STATIONMARK");
                n.AddValue("vessel", mark.Key);
                n.AddValue("station", mark.Value);
            }
        }

        public override void OnLoad(ConfigNode node)
        {
            StationMarks.Clear();
            foreach (var n in node.GetNodes("STATIONMARK"))
                if (n.GetValue("vessel") is string name && bool.TryParse(n.GetValue("station"), out var station)) StationMarks[name] = station;
        }
    }
}
