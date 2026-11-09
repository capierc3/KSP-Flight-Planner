using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KRPC.Service;
using KRPC.Service.Attributes;

namespace KspFlightPlanner.KerbinResources
{
    /// <summary>kRPC service "PlannerCrew": Kerbin Resources' flight log, duty roster and controller log, for the planner.</summary>
    [KRPCService(Name = "PlannerCrew", GameScene = GameScene.All)]
    public static class KerbinResourcesService
    {
        private const string Tab = "\t";

        private static string R(double d) => d.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>
        /// Every logged flight, oldest first, one line each, tab-separated: game time departed, game time arrived, from,
        /// to, craft, "autopilot" or "hand", then the crew as "name:trait" joined by "; ".
        /// </summary>
        [KRPCProcedure]
        public static IList<string> Flights()
        {
            var log = KerbinResourcesScenario.Instance?.Log;
            if (log == null) return new List<string>();
            return log.Flights.Select(f => string.Join(Tab, new[]
            {
                R(f.DepartedUt), R(f.ArrivedUt), f.FromName, f.ToName, f.Vessel, f.Autopilot ? "autopilot" : "hand",
                string.Join("; ", f.Crew.Select(c => c.Name + ":" + c.Trait)),
            })).ToList();
        }

        /// <summary>Crewed craft that have left somewhere and not arrived yet, tab-separated: craft, from, game time departed.</summary>
        [KRPCProcedure]
        public static IList<string> OpenTrips()
        {
            var log = KerbinResourcesScenario.Instance?.Log;
            if (log == null) return new List<string>();
            return log.OpenTrips.Select(t => string.Join(Tab, new[] { t.VesselName, t.FromName, R(t.DepartedUt) })).ToList();
        }

        /// <summary>
        /// Every duty shift, oldest first, tab-separated: kerbal, post ("KSC" or a station's name), body, game time started,
        /// game time ended ("" while still on), why it ended, the craft they sit in, and "docked" if that's a ship docked
        /// to the post rather than the station's own parts, then the job now ("Awaiting orders" when none).
        /// </summary>
        [KRPCProcedure]
        public static IList<string> Shifts()
        {
            var duty = KerbinResourcesScenario.Instance?.Duty;
            if (duty == null) return new List<string>();
            return duty.Shifts.Select(s => string.Join(Tab, new[]
            {
                s.Kerbal, s.PostName, s.Body, R(s.StartUt), s.OnNow ? "" : R(s.EndUt), s.EndReason, s.Seat, s.InDockedShip ? "docked" : "", s.JobTitle,
            })).ToList();
        }

        /// <summary>
        /// Every burn and hand-off a controller oversaw, oldest first, tab-separated: game time, kerbal, "burn" or "handoff",
        /// body, post, probe, then for a burn its kind, priority and length (s), for a hand-off the other controller, their
        /// body and "gave" or "took".
        /// </summary>
        [KRPCProcedure]
        public static IList<string> Controllers()
        {
            var control = KerbinResourcesScenario.Instance?.Control;
            if (control == null) return new List<string>();
            return control.Entries.Select(e => string.Join(Tab, e.Kind == ControlEntry.Burn
                ? new[] { R(e.Ut), e.Kerbal, e.Kind, e.Body, e.PostName, e.Vessel, e.BurnKind, e.Priority.ToString(CultureInfo.InvariantCulture), R(e.BurnSeconds) }
                : new[] { R(e.Ut), e.Kerbal, e.Kind, e.Body, e.PostName, e.Vessel, e.Other, e.OtherBody, e.Gave ? "gave" : "took" })).ToList();
        }

        /// <summary>Puts a kerbal on duty where they are now (KSC or their craft). Returns "" when done, otherwise why not.</summary>
        [KRPCProcedure]
        public static string PutOnDuty(string kerbal)
        {
            var duty = KerbinResourcesScenario.Instance?.Duty;
            if (duty == null) return "Kerbin Resources isn't running";
            return duty.PutOn(kerbal, Planetarium.GetUniversalTime(), out var why) == null ? why : "";
        }

        /// <summary>Takes a kerbal off duty.</summary>
        [KRPCProcedure]
        public static void TakeOffDuty(string kerbal) => KerbinResourcesScenario.Instance?.Duty.TakeOff(kerbal, Planetarium.GetUniversalTime());
    }
}
