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

        [GameParameters.CustomParameterUI("Controllers direct traffic", toolTip = "A probe's burns need a Traffic Controller on duty in that SOI to upload them (missed otherwise), and leaving a station with one on duty needs their clearance")]
        public bool ControllerRules = true;

        [GameParameters.CustomIntParameterUI("Upload burns (minutes before)", minValue = 1, maxValue = 60, toolTip = "When a controller uploads a probe's burn")]
        public int UploadLeadMinutes = 10;

        [GameParameters.CustomParameterUI("Controllers need a CommNet link", toolTip = "A controller needs a CommNet link to a probe to upload its burn and check it after, and to another controller to hand a flight over (only with CommNet on)")]
        public bool RequireLink = true;

        [GameParameters.CustomParameterUI("Ask before an uncontrolled departure", toolTip = "Before a probe leaves, every burn is checked for a controller; if one won't have one, you're asked whether to fly it uncontrolled")]
        public bool DepartureApproval = true;

        [GameParameters.CustomIntParameterUI("Slots, new controller", minValue = 1, maxValue = 20)]
        public int CapacityNew = 2;

        [GameParameters.CustomIntParameterUI("Slots, experienced controller", minValue = 1, maxValue = 30)]
        public int CapacityMid = 4;

        [GameParameters.CustomIntParameterUI("Slots, veteran controller", minValue = 1, maxValue = 50)]
        public int CapacityVeteran = 8;

        [GameParameters.CustomIntParameterUI("Handled to be experienced", minValue = 1, maxValue = 200, toolTip = "Burns, hand-offs and clearances on the controller's log")]
        public int MidFrom = 10;

        [GameParameters.CustomIntParameterUI("Handled to be a veteran", minValue = 1, maxValue = 500)]
        public int VeteranFrom = 20;

        [GameParameters.CustomIntParameterUI("Seconds a burn holds a slot after it", minValue = 0, maxValue = 600, toolTip = "On top of the burn's own length: checking it and planning the next")]
        public int SlotExtraSeconds = 60;

        [GameParameters.CustomIntParameterUI("Priority: course correction", minValue = 2, maxValue = 10, toolTip = "1 is the most urgent (burns it can't miss); 10 can wait hours")]
        public int PriorityCorrection = 3;

        [GameParameters.CustomIntParameterUI("Priority: phasing and orbit changes", minValue = 2, maxValue = 10)]
        public int PriorityPhasing = 5;

        [GameParameters.CustomIntParameterUI("Priority: transfer", minValue = 2, maxValue = 10)]
        public int PriorityTransfer = 7;

        [GameParameters.CustomIntParameterUI("Priority: departure", minValue = 2, maxValue = 10)]
        public int PriorityDeparture = 10;

        /// <summary>This save's settings (defaults when there's no game).</summary>
        internal static TrafficSettings Current =>
            HighLogic.CurrentGame?.Parameters?.CustomParams<TrafficSettings>() ?? new TrafficSettings();
    }
}
