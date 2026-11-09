using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.KerbinResources
{
    /// <summary>
    /// Kerbin Resources, the kerbals' HR department: keeps every crewed flight in the save (<see cref="FlightLog"/>), the
    /// personnel files, the duty roster (<see cref="DutyRoster"/>) and the controller log (<see cref="ControlLog"/>).
    ///
    /// It watches flights the player flies. A crewed craft undocking (or launching) starts a trip; docking somewhere else
    /// ends it, and the flight is logged with whoever is aboard. Of the two halves at an undocking or a docking, the
    /// lighter one is taken as the craft travelling and the heavier as the place (a shuttle leaving a station, not the
    /// station leaving the shuttle). Traffic Controller's route craft are left to Traffic Controller while a route flies
    /// them: it logs their flights itself, docked on rails or not (<see cref="LoggedElsewhere"/>). docs/MATH.md §11.12.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.TRACKSTATION, GameScenes.SPACECENTER)]
    public sealed class KerbinResourcesScenario : ScenarioModule
    {
        internal static KerbinResourcesScenario Instance { get; private set; }

        internal readonly FlightLog Log = new FlightLog();

        /// <summary>The personnel files: birthdays, birthplaces, hire dates, call signs, last names (docs/KERBIN-RESOURCES.md).</summary>
        internal readonly Personnel Personnel = new Personnel();

        /// <summary>Who's on duty and where, with every shift kept (docs/KERBIN-RESOURCES.md "Duty roster").</summary>
        internal readonly DutyRoster Duty = new DutyRoster();

        /// <summary>The burns and hand-offs each controller oversaw, written by Traffic Controller (docs/KERBIN-RESOURCES.md "Controller log").</summary>
        internal readonly ControlLog Control = new ControlLog();

        // When the roster is next looked over for kerbals without a record (real time).
        private float _nextLookOver = 1;

        /// <summary>
        /// A craft whose flight another add-on logs (set by Traffic Controller: a craft flying a route). Its undockings and
        /// dockings are left alone here.
        /// </summary>
        internal static Func<Vessel, bool> LoggedElsewhere;

        /// <summary>
        /// Extra lines for a kerbal's data card from another add-on (set by Traffic Controller: the routes a pilot is cleared
        /// for and training on), as label and value.
        /// </summary>
        internal static Func<string, IEnumerable<KeyValuePair<string, string>>> CardRows;

        public override void OnAwake()
        {
            Instance = this;
            GameEvents.onVesselsUndocking.Add(OnUndocking);
            GameEvents.onPartCouple.Add(OnCouple);
            GameEvents.onLaunch.Add(OnLaunch);
            GameEvents.onVesselPersistentIdChanged.Add(OnIdChanged);
            GameEvents.onVesselRecovered.Add(OnRecovered);
            GameEvents.onVesselTerminated.Add(OnTerminated);
            GameEvents.onKerbalAdded.Add(OnKerbalAdded);
            GameEvents.onKerbalTypeChange.Add(OnKerbalTypeChange);
            GameEvents.onKerbalNameChanged.Add(OnKerbalNameChanged);
        }

        private void OnDestroy()
        {
            GameEvents.onVesselsUndocking.Remove(OnUndocking);
            GameEvents.onPartCouple.Remove(OnCouple);
            GameEvents.onLaunch.Remove(OnLaunch);
            GameEvents.onVesselPersistentIdChanged.Remove(OnIdChanged);
            GameEvents.onVesselRecovered.Remove(OnRecovered);
            GameEvents.onVesselTerminated.Remove(OnTerminated);
            GameEvents.onKerbalAdded.Remove(OnKerbalAdded);
            GameEvents.onKerbalTypeChange.Remove(OnKerbalTypeChange);
            GameEvents.onKerbalNameChanged.Remove(OnKerbalNameChanged);
            if (Instance == this) Instance = null;
        }

        private static double Now => Planetarium.GetUniversalTime();

        /// <summary>
        /// Every few seconds: any kerbal without a record gets one (and a last name, outside flight). Catches the roster of
        /// a save seen for the first time, and kerbals made while this wasn't listening.
        /// </summary>
        private void Update()
        {
            if (Time.timeSinceLevelLoad < _nextLookOver) return;
            _nextLookOver = Time.timeSinceLevelLoad + 10;
            Personnel.LookOver(Now, mayRename: !HighLogic.LoadedSceneIsFlight);
            Duty.LookOver(Now);
        }

        private void OnKerbalAdded(ProtoCrewMember k) => Personnel.Created(k, Now);

        private void OnKerbalTypeChange(ProtoCrewMember k, ProtoCrewMember.KerbalType from, ProtoCrewMember.KerbalType to) => Personnel.TypeChanged(k, from, to, Now);

        /// <summary>A kerbal renamed (by us when they're created, or by the player): records and flights follow.</summary>
        private void OnKerbalNameChanged(ProtoCrewMember k, string from, string to)
        {
            Personnel.Renamed(from, to);
            Log.RenameKerbal(from, to);
            Duty.RenameKerbal(from, to);
            Control.RenameKerbal(from, to);
            KerbalRenamed?.Invoke(from, to);
        }

        /// <summary>Other add-ons that keep kerbal names (Traffic Controller's routes) follow renames here.</summary>
        internal static event Action<string, string> KerbalRenamed;

        /// <summary>The lighter of two craft travels; the heavier is the place.</summary>
        private static void Split(Vessel a, Vessel b, out Vessel traveller, out Vessel place)
        {
            if (a.GetTotalMass() <= b.GetTotalMass()) { traveller = a; place = b; }
            else { traveller = b; place = a; }
        }

        private void OnUndocking(Vessel oldVessel, Vessel newVessel)
        {
            if (oldVessel == null || newVessel == null || oldVessel == newVessel) return;
            Split(oldVessel, newVessel, out var traveller, out var place);
            if (traveller.GetCrewCount() == 0 || LoggedElsewhere?.Invoke(traveller) == true) return;
            Log.Departed(traveller, place.persistentId, place.vesselName, Now);
            Debug.Log("[KspFlightPlanner] Kerbin Resources: " + traveller.vesselName + " left " + place.vesselName + ".");
        }

        /// <summary>A docking (or a claw grabbing), before KSP joins the two craft into one.</summary>
        private void OnCouple(GameEvents.FromToAction<Part, Part> couple)
        {
            var a = couple.from?.vessel;
            var b = couple.to?.vessel;
            if (a == null || b == null || a == b) return;
            Split(a, b, out var traveller, out var place);
            if (LoggedElsewhere?.Invoke(traveller) == true) return;
            var flight = Log.Arrived(traveller, place.persistentId, place.vesselName, Now);
            if (flight != null) Announce(flight);
        }

        private void OnLaunch(EventReport report)
        {
            var ship = FlightGlobals.ActiveVessel;
            if (ship == null || ship.GetCrewCount() == 0) return;
            Log.Departed(ship, 0, ship.mainBody.bodyName + " (launch)", Now);
        }

        private void OnIdChanged(uint from, uint to)
        {
            Log.Renumber(from, to);
            Duty.Renumber(from, to);
        }

        private void OnRecovered(ProtoVessel pv, bool quick)
        {
            if (pv != null) Log.DropOpen(pv.persistentId);
        }

        private void OnTerminated(ProtoVessel pv)
        {
            if (pv != null) Log.DropOpen(pv.persistentId);
        }

        /// <summary>Says on screen and in the KSP log that a flight was logged, with how often each kerbal aboard has now made it.</summary>
        internal static void Announce(Flight flight)
        {
            var log = Instance?.Log;
            var counts = flight.Crew.Select(c => c.Name.Replace(" Kerman", "") + " " +
                                                 (log?.TimesFlown(c.Name, flight.FromId, flight.FromName, flight.ToId, flight.ToName) ?? 1) + "×");
            var line = "Flight logged: " + flight.FromName + " → " + flight.ToName + " (" + string.Join(", ", counts) + ")";
            Debug.Log("[KspFlightPlanner] Kerbin Resources: " + flight.Vessel + ": " + line + (flight.Autopilot ? ", autopilot" : ", flown by hand"));
            ScreenMessages.PostScreenMessage(line, 6f, ScreenMessageStyle.UPPER_LEFT);
        }

        public override void OnSave(ConfigNode node)
        {
            Log.Save(node);
            Personnel.Save(node.AddNode("PERSONNEL"));
            Duty.Save(node.AddNode("DUTY"));
            Control.Save(node.AddNode("CONTROLLERS"));
        }

        public override void OnLoad(ConfigNode node)
        {
            Log.Load(node);
            Personnel.Load(node.GetNode("PERSONNEL"));
            Duty.Load(node.GetNode("DUTY"));
            Control.Load(node.GetNode("CONTROLLERS"));
        }
    }
}
