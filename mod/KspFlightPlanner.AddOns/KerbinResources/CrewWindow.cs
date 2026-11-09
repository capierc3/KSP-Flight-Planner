using System;
using System.Collections.Generic;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace KspFlightPlanner.KerbinResources
{
    /// <summary>
    /// The Kerbin Resources window: every kerbal on the roster with their trait, where they are, and their flights from
    /// the flight log. Pick one to see their trips (from → to, how often, when last, by hand or autopilot) and each flight
    /// with who else was aboard. It has its own toolbar button in flight, the map, the tracking station and the space
    /// centre, and opens by itself beside the Astronaut Complex.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public sealed class CrewWindow : MonoBehaviour
    {
        private static readonly string[] Traits = { "All", "Pilot", "Engineer", "Scientist", "Tourist" };

        private ApplicationLauncherButton _button;
        private Texture2D _icon;
        private bool _open;         // the toolbar button is on
        private bool _complexOpen;  // the Astronaut Complex is showing
        private Rect _window = new Rect(Screen.width - 560, 80, 500, 560);
        private bool _resizing;
        private Vector2 _listScroll, _detailScroll;
        private int _trait;
        private bool _showGone;
        private string _picked;

        // Where each kerbal is (vessel name by kerbal name), looked up every couple of seconds, not every frame.
        private Dictionary<string, string> _whereIs = new Dictionary<string, string>();
        private float _nextLook;

        private void Start()
        {
            if (!(HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.SPACECENTER || HighLogic.LoadedScene == GameScenes.TRACKSTATION))
            {
                Destroy(this);
                return;
            }
            _icon = MakeIcon();
            if (ApplicationLauncher.Ready) AddButton();
            else GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            GameEvents.onGUIAstronautComplexSpawn.Add(OnComplexOpen);
            GameEvents.onGUIAstronautComplexDespawn.Add(OnComplexClose);
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            GameEvents.onGUIAstronautComplexSpawn.Remove(OnComplexOpen);
            GameEvents.onGUIAstronautComplexDespawn.Remove(OnComplexClose);
            if (_button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(_button);
            if (_icon != null) Destroy(_icon);
        }

        private void AddButton()
        {
            if (_button != null || ApplicationLauncher.Instance == null) return;
            _button = ApplicationLauncher.Instance.AddModApplication(() => _open = true, () => _open = false, null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW | ApplicationLauncher.AppScenes.SPACECENTER |
                ApplicationLauncher.AppScenes.TRACKSTATION, _icon);
        }

        /// <summary>The Astronaut Complex opened: the window shows on its right, where the complex leaves room.</summary>
        private void OnComplexOpen()
        {
            _complexOpen = true;
            _window.x = Screen.width - _window.width - 20;
            _window.y = 60;
            _nextLook = 0;
        }

        private void OnComplexClose() => _complexOpen = false;

        private void OnGUI()
        {
            if (!_open && !_complexOpen) return;
            if (KerbinResourcesScenario.Instance == null || HighLogic.CurrentGame?.CrewRoster == null) return;
            if (Time.realtimeSinceStartup >= _nextLook)
            {
                _whereIs = WhereIs();
                _nextLook = Time.realtimeSinceStartup + 2;
            }
            GUI.skin = null;
            PanelWindow.FollowResize(ref _window, ref _resizing);
            _window = GUILayout.Window(GetInstanceID(), _window, DrawWindow, "Kerbin Resources", GUILayout.Width(_window.width), GUILayout.Height(_window.height));
        }

        private void DrawWindow(int id)
        {
            var log = KerbinResourcesScenario.Instance.Log;
            var crew = Roster().ToList();

            GUILayout.BeginHorizontal();
            _trait = GUILayout.Toolbar(_trait, Traits);
            _showGone = GUILayout.Toggle(_showGone, "Dead/missing", GUILayout.Width(110));
            GUILayout.EndHorizontal();

            // The list: one row per kerbal.
            _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.Height(_window.height * 0.4f));
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>Kerbal</b>", Rich(), GUILayout.Width(170));
            GUILayout.Label("<b>Trait</b>", Rich(), GUILayout.Width(70));
            GUILayout.Label("<b>Age</b>", Rich(), GUILayout.Width(35));
            GUILayout.Label("<b>Where</b>", Rich(), GUILayout.ExpandWidth(true));
            GUILayout.Label("<b>Flights</b>", Rich(), GUILayout.Width(50));
            GUILayout.EndHorizontal();
            foreach (var k in crew)
            {
                var flights = log.FlightsOf(k.name).Count();
                GUILayout.BeginHorizontal();
                var label = k.name == _picked ? "▶ " + k.name : k.name;
                if (GUILayout.Button(label, GUI.skin.label, GUILayout.Width(170))) _picked = k.name == _picked ? null : k.name;
                GUILayout.Label(k.trait, GUILayout.Width(70));
                var rec = KerbinResourcesScenario.Instance.Personnel.Of(k.name);
                GUILayout.Label(rec != null ? Calendar.YearsBetween(rec.BirthUt, Planetarium.GetUniversalTime()).ToString() : "", GUILayout.Width(35));
                var duty = KerbinResourcesScenario.Instance.Duty.ShiftOf(k.name);
                GUILayout.Label(Where(k) + (duty == null ? "" : "  · " + duty.JobTitle), GUILayout.ExpandWidth(true));
                GUILayout.Label(flights.ToString(), GUILayout.Width(50));
                GUILayout.EndHorizontal();
            }
            if (crew.Count == 0) GUILayout.Label("No kerbals to show.");
            GUILayout.EndScrollView();

            // The picked kerbal's trips and flights.
            _detailScroll = GUILayout.BeginScrollView(_detailScroll, GUILayout.ExpandHeight(true));
            if (_picked == null) GUILayout.Label("Pick a kerbal to see their flights.");
            else DrawKerbal(log, _picked);
            GUILayout.EndScrollView();

            if (_open && GUILayout.Button("Close")) _button?.SetFalse();
            PanelWindow.Grip(_window, ref _resizing);
            GUI.DragWindow();
        }

        private void DrawKerbal(FlightLog log, string kerbal)
        {
            var flights = log.FlightsOf(kerbal).ToList();
            var flown = flights.Sum(f => Math.Max(0, f.ArrivedUt - f.DepartedUt));
            var personnel = KerbinResourcesScenario.Instance.Personnel;
            var rec = personnel.Of(kerbal);
            var pcm = HighLogic.CurrentGame.CrewRoster[kerbal];
            var now = Planetarium.GetUniversalTime();

            // The data card.
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<size=15><b>" + kerbal + "</b></size>" + (rec != null && rec.CallSign.Length > 0 ? "   <i>\"" + rec.CallSign + "\"</i>" : ""), Rich());
            if (pcm != null) GUILayout.Label(pcm.trait + (pcm.veteran ? ", founding member" : "") + "   ·   " + Where(pcm));
            GUILayout.Space(4);
            if (rec != null)
            {
                var age = Calendar.YearsBetween(rec.BirthUt, now);
                var config = KerbinResourcesConfig.Current;
                var stage = config.LifeStage(age);
                var toRetire = config.RetirementAge - age;
                Row("Age", age + (stage.Length > 0 ? "   ·   " + stage : "") +
                           (config.RetirementAge > 0 ? "   ·   " + (toRetire > 0 ? "retires in " + toRetire + " year" + (toRetire == 1 ? "" : "s") : "past retirement age") : ""));
                Row("Born", Calendar.PrintLong(rec.BirthUt));
                if (rec.Birthplace.Length > 0) Row("Birthplace", rec.Birthplace);
                Row("Hired", double.IsNaN(rec.HiredUt) ? "Not yet (applicant)" :
                             Calendar.Print(rec.HiredUt) + "   ·   " + Calendar.Between(rec.HiredUt, now) + " with the company");
                var family = personnel.FamilyOf(rec).ToList();
                if (family.Count > 0) Row("Family", string.Join(", ", family.Select(f => f.Name + Relation(f.Name))));
            }
            if (pcm != null)
                Row("Nature", "Courage " + (pcm.courage * 100).ToString("0") + "%   ·   Stupidity " + (pcm.stupidity * 100).ToString("0") + "%" + (pcm.isBadass ? "   ·   Badass" : ""));
            var visited = Visited(pcm);
            Row("Been to", visited.Length > 0 ? visited : "Nowhere yet");
            DrawDuty(kerbal, pcm, now);
            if (KerbinResourcesScenario.CardRows != null)
                foreach (var extra in KerbinResourcesScenario.CardRows(kerbal)) Row(extra.Key, extra.Value);
            Row("Flights", flights.Count + " logged" + (flights.Count > 0 ? "   ·   " + Span(flown) + " flying   ·   longest " + Span(flights.Max(f => f.ArrivedUt - f.DepartedUt)) : ""));

            // Open trip: left somewhere and not arrived yet.
            var open = log.OpenTrips.FirstOrDefault(t => _whereIs.TryGetValue(kerbal, out var v) && v == t.VesselName);
            if (open != null) Row("Flying now", open.VesselName + ", left " + open.FromName + " " + Span(now - open.DepartedUt) + " ago");
            GUILayout.EndVertical();
            DrawControlHistory(kerbal, now);
            if (flights.Count == 0) return;

            // Trips: each from → to, most flown first.
            GUILayout.Space(6);
            GUILayout.Label("<b>Trips</b>", Rich());
            foreach (var trip in flights.GroupBy(f => (f.FromId != 0 ? f.FromId.ToString() : f.FromName) + "→" + (f.ToId != 0 ? f.ToId.ToString() : f.ToName))
                                        .OrderByDescending(g => g.Count()))
            {
                var last = trip.OrderBy(f => f.ArrivedUt).Last();
                var byHand = trip.Count(f => !f.Autopilot);
                GUILayout.Label(last.FromName + " → " + last.ToName + ": " + trip.Count() + "×" +
                                (byHand > 0 ? " (" + byHand + " by hand)" : "") + ", last " + Date(last.ArrivedUt));
            }

            // Flights, newest first, with who else was aboard.
            GUILayout.Space(6);
            GUILayout.Label("<b>Flights</b>", Rich());
            foreach (var f in flights.OrderByDescending(f => f.ArrivedUt))
            {
                var others = f.Crew.Where(c => c.Name != kerbal).Select(c => c.Name.Replace(" Kerman", "") + (c.Trait.Length > 0 ? " (" + c.Trait + ")" : "")).ToList();
                var me = f.Crew.FirstOrDefault(c => c.Name == kerbal);
                GUILayout.Label(Date(f.ArrivedUt) + "  " + f.FromName + " → " + f.ToName + " in " + f.Vessel + ", " + Span(f.ArrivedUt - f.DepartedUt) +
                                (f.Autopilot ? ", autopilot" : ", by hand") + (me != null && me.Trait.Length > 0 ? ", as " + me.Trait : "") +
                                (others.Count > 0 ? "; with " + string.Join(", ", others) : ""));
            }
        }

        private string _dutyWhy = "";
        private string _dutyWhyFor = "";

        /// <summary>
        /// The data card's duty lines: on duty where and since when (with a button to take them off), or off duty (with a
        /// button to put them on where they are); time on duty over all their shifts; the burns and hand-offs they controlled.
        /// </summary>
        private void DrawDuty(string kerbal, ProtoCrewMember pcm, double now)
        {
            var scenario = KerbinResourcesScenario.Instance;
            var duty = scenario.Duty;
            var shift = duty.ShiftOf(kerbal);
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>Duty</b>", Rich(), GUILayout.Width(80));
            if (shift != null)
            {
                GUILayout.Label("<b>" + shift.JobTitle + "</b> at " + shift.PostName + (shift.Body.Length > 0 && !shift.AtKsc ? " (" + shift.Body + ")" : "") +
                                (shift.InDockedShip ? ", in " + shift.Seat + " (docked)" : "") +
                                ", on duty for " + Span(now - shift.StartUt), RichWrap());
                if (GUILayout.Button("Off duty", GUILayout.Width(80))) duty.TakeOff(kerbal, now);
            }
            else if (pcm != null && pcm.type == ProtoCrewMember.KerbalType.Crew)
            {
                GUILayout.Label("Off duty", Wrap());
                if (GUILayout.Button("On duty", GUILayout.Width(80)))
                {
                    _dutyWhyFor = kerbal;
                    _dutyWhy = duty.PutOn(kerbal, now, out var why) == null ? why : "";
                }
            }
            else GUILayout.Label("Not company crew", Wrap());
            GUILayout.EndHorizontal();
            if (_dutyWhyFor == kerbal && _dutyWhy.Length > 0) Row("", "Can't: " + _dutyWhy);

            var shifts = duty.ShiftsOf(kerbal).Count();
            if (shifts > 0) Row("Time on duty", Span(duty.TimeOnDuty(kerbal, now)) + " over " + shifts + " shift" + (shifts == 1 ? "" : "s"));
            var burns = scenario.Control.BurnsOf(kerbal);
            var handOffs = scenario.Control.HandOffsOf(kerbal);
            if (burns + handOffs > 0)
            {
                var bodies = scenario.Control.Of(kerbal).Select(e => e.Body).Where(b => b.Length > 0).Distinct();
                Row("Controlled", burns + " burn" + (burns == 1 ? "" : "s") + "   ·   " + handOffs + " hand-off" + (handOffs == 1 ? "" : "s") +
                                  "   ·   " + string.Join(", ", bodies));
            }
        }

        /// <summary>Below the card: the kerbal's last shifts and the last burns and hand-offs they controlled, newest first.</summary>
        private void DrawControlHistory(string kerbal, double now)
        {
            var scenario = KerbinResourcesScenario.Instance;
            var control = scenario.Control.Of(kerbal).OrderByDescending(e => e.Ut).Take(20).ToList();
            if (control.Count > 0)
            {
                GUILayout.Space(6);
                GUILayout.Label("<b>Controlled</b>", Rich());
                foreach (var e in control)
                    GUILayout.Label(Date(e.Ut) + "  " + (e.Kind == ControlEntry.Burn
                        ? e.Vessel + ": " + (e.BurnKind.Length > 0 ? e.BurnKind : "burn") + " (priority " + e.Priority + ", " + Span(e.BurnSeconds) + ")" + " from " + e.PostName
                        : e.Vessel + (e.Gave ? " handed to " : " taken from ") + e.Other + (e.OtherBody.Length > 0 ? " (" + e.OtherBody + ")" : "")));
            }
            var shifts = scenario.Duty.ShiftsOf(kerbal).OrderByDescending(s => s.StartUt).Take(10).ToList();
            if (shifts.Count == 0) return;
            GUILayout.Space(6);
            GUILayout.Label("<b>Shifts</b>", Rich());
            foreach (var s in shifts)
            {
                var jobs = s.Jobs.Select(j => string.IsNullOrEmpty(j.Value) ? DutyShift.AwaitingOrders : j.Value).Distinct().ToList();
                GUILayout.Label(Date(s.StartUt) + "  " + s.PostName + (s.AtKsc ? "" : " (" + s.Body + ")") + ", " + Span(s.Length(now)) +
                                (jobs.Count > 0 ? ", " + string.Join(" then ", jobs) : "") +
                                (s.OnNow ? ", on now" : s.EndReason.Length > 0 ? ", " + s.EndReason : ""));
            }
        }

        /// <summary>The roster's kerbals (applicants left out), filtered by the trait tab, the dead and missing only when ticked.</summary>
        private IEnumerable<ProtoCrewMember> Roster()
        {
            var roster = HighLogic.CurrentGame.CrewRoster;
            return roster.Crew.Concat(roster.Tourist)
                         .Where(k => _trait == 0 || k.trait == Traits[_trait])
                         .Where(k => _showGone || (k.rosterStatus != ProtoCrewMember.RosterStatus.Dead && k.rosterStatus != ProtoCrewMember.RosterStatus.Missing))
                         .OrderBy(k => k.trait).ThenBy(k => k.name);
        }

        private string Where(ProtoCrewMember k)
        {
            switch (k.rosterStatus)
            {
                case ProtoCrewMember.RosterStatus.Available: return "KSC";
                case ProtoCrewMember.RosterStatus.Dead: return "Dead";
                case ProtoCrewMember.RosterStatus.Missing: return "Missing";
                default: return _whereIs.TryGetValue(k.name, out var v) ? v : "Assigned";
            }
        }

        /// <summary>Which craft each kerbal is aboard, from the save's craft (works in every scene, loaded or not).</summary>
        private static Dictionary<string, string> WhereIs()
        {
            var map = new Dictionary<string, string>();
            var vessels = HighLogic.CurrentGame?.flightState?.protoVessels;
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.Vessels != null)
            {
                foreach (var v in FlightGlobals.Vessels)
                    if (v != null)
                        foreach (var c in v.GetVesselCrew())
                            map[c.name] = v.vesselName;
            }
            else if (vessels != null)
            {
                foreach (var pv in vessels)
                    foreach (var c in pv.GetVesselCrew())
                        map[c.name] = pv.vesselName;
            }
            return map;
        }

        private static string Date(double ut) => Calendar.Print(ut);

        /// <summary>One line of the data card: a label column, then its value.</summary>
        private static void Row(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>" + label + "</b>", Rich(), GUILayout.Width(80));
            GUILayout.Label(value, Wrap());
            GUILayout.EndHorizontal();
        }

        private static GUIStyle _wrap;

        private static GUIStyle Wrap() => _wrap ?? (_wrap = new GUIStyle(GUI.skin.label) { wordWrap = true });

        private static GUIStyle _richWrap;

        private static GUIStyle RichWrap() => _richWrap ?? (_richWrap = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true });

        /// <summary>" (sister)" / " (brother)": everyone sharing a last name is taken as a sibling.</summary>
        private static string Relation(string kerbal)
        {
            var k = HighLogic.CurrentGame.CrewRoster[kerbal];
            return k == null ? "" : k.gender == ProtoCrewMember.Gender.Female ? " (sister)" : " (brother)";
        }

        /// <summary>
        /// Where KSP's own career log says a kerbal has been, the furthest thing they did at each body: "Mun (landed),
        /// Minmus (orbit)". KSP adds a flight to the career log when the kerbal is recovered; the flight they're on now
        /// counts too.
        /// </summary>
        private static string Visited(ProtoCrewMember k)
        {
            if (k == null) return "";
            var entries = k.careerLog.Entries.Concat(k.flightLog.Entries);
            var best = new Dictionary<string, int>();
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.target)) continue;
                var rank = e.type == "Land" || e.type == "PlantFlag" ? 4 : e.type == "Orbit" ? 3 : e.type == "Flyby" || e.type == "Escape" ? 2 : e.type == "Suborbit" ? 1 : 0;
                if (rank == 0) continue;
                best[e.target] = best.TryGetValue(e.target, out var had) ? Math.Max(had, rank) : rank;
            }
            string Word(int r) => r == 4 ? "landed" : r == 3 ? "orbit" : r == 2 ? "flyby" : "suborbital";
            return string.Join(", ", best.OrderByDescending(p => p.Value).Select(p => p.Key + " (" + Word(p.Value) + ")"));
        }

        /// <summary>A length of time as days, hours and minutes, in the game's own day (6 h on Kerbin time, 24 h on Earth time).</summary>
        private static string Span(double seconds)
        {
            seconds = Math.Max(0, seconds);
            var day = KSPUtil.dateTimeFormatter.Day;
            var days = (int)(seconds / day);
            var hours = (int)(seconds % day / 3600);
            var minutes = (int)(seconds % 3600 / 60);
            return days > 0 ? days + "d " + hours + "h" : hours > 0 ? hours + "h " + minutes + "m" : minutes + "m";
        }

        private static GUIStyle _rich;

        private static GUIStyle Rich() => _rich ?? (_rich = new GUIStyle(GUI.skin.label) { richText = true });

        /// <summary>A plain toolbar icon: a kerbal's head and shoulders, in orange.</summary>
        private static Texture2D MakeIcon()
        {
            const int size = 38;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var clear = new Color(0, 0, 0, 0);
            var orange = new Color(1f, 0.6f, 0.2f, 1);
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                float dx = x - 19, hy = y - 25, sy = y - 2;
                var head = dx * dx + hy * hy <= 64;               // head: radius 8, centre (19, 25)
                var shoulders = sy >= 0 && sy <= 12 && dx * dx / 196f + (sy - 12) * (sy - 12) / 144f <= 1; // half ellipse at the bottom
                tex.SetPixel(x, y, head || shoulders ? orange : clear);
            }
            tex.Apply();
            return tex;
        }
    }
}
