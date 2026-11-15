namespace KspFlightPlanner.Refueler
{
    /// <summary>
    /// The Refueler's pump rates, per save, in KSP's difficulty settings (section "KSP Flight Planner"): how fast fuel flows
    /// through each size of docking port, in kg/s. docs/TRAFFIC-CONTROLLER.md, "Fuel transfer and RCS balance".
    /// </summary>
    public sealed class RefuelerSettings : GameParameters.CustomParameterNode
    {
        public override string Title => "Refueler";
        public override string Section => "KSP Flight Planner";
        public override string DisplaySection => "KSP Flight Planner";
        public override int SectionOrder => 3;
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        [GameParameters.CustomFloatParameterUI("Pump, Junior port (kg/s)", minValue = 0.1f, maxValue = 50f, displayFormat = "F1", toolTip = "Fuel flow through a Clamp-O-Tron Jr. sized port")]
        public float JuniorKgS = 1f;

        [GameParameters.CustomFloatParameterUI("Pump, standard port (kg/s)", minValue = 0.1f, maxValue = 100f, displayFormat = "F1", toolTip = "Fuel flow through a Clamp-O-Tron sized port (and ports of unknown size)")]
        public float StandardKgS = 5f;

        [GameParameters.CustomFloatParameterUI("Pump, Senior port (kg/s)", minValue = 0.1f, maxValue = 200f, displayFormat = "F1", toolTip = "Fuel flow through a Clamp-O-Tron Sr. sized port or bigger")]
        public float SeniorKgS = 20f;

        /// <summary>This save's settings (defaults when there's no game).</summary>
        internal static RefuelerSettings Current =>
            HighLogic.CurrentGame?.Parameters?.CustomParams<RefuelerSettings>() ?? new RefuelerSettings();
    }
}
