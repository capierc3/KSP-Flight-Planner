namespace KspFlightPlanner.Science
{
    /// <summary>
    /// "Scan now" alerts, per save, in KSP's difficulty settings (section "KSP Flight Planner"). docs/SCIENCE.md.
    /// </summary>
    public sealed class ScienceSettings : GameParameters.CustomParameterNode
    {
        public override string Title => "Science";
        public override string Section => "KSP Flight Planner";
        public override string DisplaySection => "KSP Flight Planner";
        public override int SectionOrder => 4;
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.CAREER | GameParameters.GameMode.SCIENCE;
        public override bool HasPresets => false;

        [GameParameters.CustomParameterUI("Scan now alerts", toolTip = "A message when the vessel you're flying enters a biome or situation where an experiment aboard still pays")]
        public bool ScanNow = true;

        [GameParameters.CustomFloatParameterUI("Alert when a run pays at least", minValue = 0.5f, maxValue = 50f, displayFormat = "F1", toolTip = "Science one run would earn once recovered")]
        public float PaysAtLeast = 1f;

        /// <summary>This save's settings (defaults when there's no game).</summary>
        internal static ScienceSettings Current =>
            HighLogic.CurrentGame?.Parameters?.CustomParams<ScienceSettings>() ?? new ScienceSettings();
    }
}
