namespace KspFlightPlanner.Haulers
{
    /// <summary>
    /// The game's crew rules for haulers, in one place: who may fly a route, and which stations direct their traffic.
    /// The flying code only asks; the rules live here. For now everything is allowed: the crew rules (a pilot who has
    /// flown the route, a probe flown from a crewed station, a pilot aboard a station for traffic control) come later.
    /// </summary>
    internal static class HaulerRules
    {
        /// <summary>
        /// May this ship fly this route? Asked when the route starts (refused with the reason) and when the physics
        /// flyer would take the ship over near the player (left alone with the reason on its log).
        /// </summary>
        internal static bool CanFly(HaulerRoute route, Vessel ship, out string why)
        {
            why = "";
            return true;
        }

        /// <summary>
        /// Does this station direct its traffic (clearance into its keep-out zone, the arrival and departure lanes)? It
        /// does with a pilot aboard (one in a ship docked to it counts: it's aboard the station as KSP sees it). Without
        /// one, haulers still come and dock there on their own, kept apart only by their pilots' safety stops.
        /// </summary>
        internal static bool HasTrafficControl(Vessel station, out string why)
        {
            why = "";
            if (station == null) return false;
            foreach (var kerbal in station.GetVesselCrew())
                if (kerbal.trait == "Pilot") return true;
            why = "no pilot aboard " + station.vesselName;
            return false;
        }
    }
}
