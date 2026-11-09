namespace KspFlightPlanner.Traffic
{
    /// <summary>Traffic Controller's crew-rule numbers, per save, in KSP's difficulty settings (section "KSP Flight Planner").</summary>
    public sealed class TrafficSettings : GameParameters.CustomParameterNode
    {
        public override string Title => "Traffic Controller";
        public override string Section => "KSP Flight Planner";
        public override string DisplaySection => "KSP Flight Planner";
        public override int SectionOrder => 2;
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        [GameParameters.CustomParameterUI("Pilots must know the route", toolTip = "A crewed route craft needs a pilot aboard who has flown that trip, in that direction")]
        public bool RouteQualification = true;

        [GameParameters.CustomIntParameterUI("Flights on a route to train others", minValue = 1, maxValue = 50)]
        public int MentorFlights = 5;

        [GameParameters.CustomIntParameterUI("Training rides to be cleared", minValue = 1, maxValue = 10, toolTip = "Rides along with a pilot who trains others")]
        public int TrainingRides = 1;

        /// <summary>This save's settings (defaults when there's no game).</summary>
        internal static TrafficSettings Current =>
            HighLogic.CurrentGame?.Parameters?.CustomParams<TrafficSettings>() ?? new TrafficSettings();
    }
}
