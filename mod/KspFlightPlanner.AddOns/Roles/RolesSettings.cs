namespace KspFlightPlanner.Roles
{
    /// <summary>
    /// Survey Engineers and Science Officers, per save, in KSP's difficulty settings (section "KSP Flight Planner").
    /// docs/KERBIN-RESOURCES.md "Survey and science roles", docs/SURVEY.md "When a body is done".
    /// The planner reads the survey's figures (<see cref="DonePercent"/>, <see cref="HideLandmarks"/>) from the save.
    /// </summary>
    public sealed class RolesSettings : GameParameters.CustomParameterNode
    {
        public override string Title => "Crew roles";
        public override string Section => "KSP Flight Planner";
        public override string DisplaySection => "KSP Flight Planner";
        public override int SectionOrder => 5;
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        [GameParameters.CustomParameterUI("Survey needs a Survey Engineer", toolTip = "The ore survey builds only while an engineer is on duty in a station's lab (or at KSC for Kerbin), and they can work linked probes' scanners")]
        public bool SurveyEngineers = true;

        [GameParameters.CustomIntParameterUI("Body done at % mapped", minValue = 50, maxValue = 100, toolTip = "A body's survey is done when the narrow-band has covered this much of its surface and every biome has its report")]
        public int DonePercent = 95;

        [GameParameters.CustomFloatParameterUI("Writing the survey report (hours)", minValue = 0.5f, maxValue = 48f, displayFormat = "F1", toolTip = "Game time on duty to write a body's final survey report")]
        public float ReportHours = 6f;

        [GameParameters.CustomParameterUI("Hide landmarks until surveyed", toolTip = "Anomalies and natural features stay hidden in the planner until a narrow-band strip covers them, and anomalies show as Unknown object until visited")]
        public bool HideLandmarks = true;

        [GameParameters.CustomFloatParameterUI("Visit identifies an anomaly within (km)", minValue = 0.5f, maxValue = 20f, displayFormat = "F1", toolTip = "A craft this close to a spotted anomaly, landed or flying, identifies it")]
        public float VisitKm = 3f;

        [GameParameters.CustomParameterUI("Science Officers work probes", toolTip = "A scientist on duty in a station's lab (or at KSC for Kerbin) runs and sends linked probes' experiments while you fly something else")]
        public bool ScienceOfficers = true;

        [GameParameters.CustomIntParameterUI("Sign in to a probe (s)", minValue = 0, maxValue = 120)]
        public int SignInSeconds = 15;

        [GameParameters.CustomIntParameterUI("Run an experiment (s)", minValue = 0, maxValue = 120)]
        public int RunSeconds = 10;

        [GameParameters.CustomIntParameterUI("Sign out of a probe (s)", minValue = 0, maxValue = 120)]
        public int SignOutSeconds = 15;

        /// <summary>This save's settings (defaults when there's no game).</summary>
        internal static RolesSettings Current =>
            HighLogic.CurrentGame?.Parameters?.CustomParams<RolesSettings>() ?? new RolesSettings();
    }
}
