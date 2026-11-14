namespace KspFlightPlanner.KerbinResources
{
    /// <summary>
    /// Kerbin Resources' numbers, per save, in KSP's difficulty settings (section "KSP Flight Planner"). The lists (calendar,
    /// last names, call signs) are in KerbinResources.cfg.
    /// </summary>
    public sealed class KerbinResourcesSettings : GameParameters.CustomParameterNode
    {
        public override string Title => "Kerbin Resources";
        public override string Section => "KSP Flight Planner";
        public override string DisplaySection => "KSP Flight Planner";
        public override int SectionOrder => 1;
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        [GameParameters.CustomIntParameterUI("Company founded: year", minValue = 1, maxValue = 9999, toolTip = "The calendar year of game day 1")]
        public int FoundedYear = 2263;

        [GameParameters.CustomIntParameterUI("Company founded: month", minValue = 1, maxValue = 12, toolTip = "10 = Diznam")]
        public int FoundedMonth = 10;

        [GameParameters.CustomIntParameterUI("Company founded: day", minValue = 1, maxValue = 36)]
        public int FoundedDay = 3;

        [GameParameters.CustomIntParameterUI("Youngest new kerbal (years)", minValue = 20, maxValue = 150, toolTip = "Kerbals start work at 35 and live about 200 years")]
        public int MinAge = 35;

        [GameParameters.CustomIntParameterUI("Oldest new kerbal (years)", minValue = 20, maxValue = 150)]
        public int MaxAge = 70;

        [GameParameters.CustomIntParameterUI("Youngest founding member (years)", minValue = 20, maxValue = 150, toolTip = "Jeb, Bill, Bob and Val")]
        public int FounderMinAge = 70;

        [GameParameters.CustomIntParameterUI("Oldest founding member (years)", minValue = 20, maxValue = 150)]
        public int FounderMaxAge = 100;

        [GameParameters.CustomIntParameterUI("Chance a new kerbal joins family (%)", minValue = 0, maxValue = 100, toolTip = "Shares a last name with a kerbal already on the roster")]
        public int FamilyChance = 15;

        [GameParameters.CustomIntParameterUI("Most kerbals in one family", minValue = 2, maxValue = 10)]
        public int MaxFamily = 4;

        [GameParameters.CustomParameterUI("New last names", toolTip = "Kerbals get a last name from KerbinResources.cfg instead of Kerman when they're created")]
        public bool NewLastNames = true;

        [GameParameters.CustomParameterUI("Call signs for pilots")]
        public bool CallSigns = true;

        /// <summary>This save's settings (defaults when there's no game).</summary>
        internal static KerbinResourcesSettings Current =>
            HighLogic.CurrentGame?.Parameters?.CustomParams<KerbinResourcesSettings>() ?? new KerbinResourcesSettings();
    }
}
