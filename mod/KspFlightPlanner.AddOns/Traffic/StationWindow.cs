using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KSP.UI.Screens;
using KspFlightPlanner.KerbinResources;
using KspFlightPlanner.PowerHeat;
using UnityEngine;
using CrewLog = KspFlightPlanner.KerbinResources.FlightLog;

namespace KspFlightPlanner.Traffic
{
    /// <summary>
    /// The Station window (Traffic Controller stage E, docs/TRAFFIC-CONTROLLER.md "Station pane"). In flight a toolbar
    /// button shows the craft you're flying: whether its traffic is controlled and the controllers' load, its ports, the
    /// craft docked to it, the next day's traffic (arrivals, held departures, burns its SOI's controllers upload), who's
    /// aboard with their duty and a button to move each to another part, and its supplies. At the space centre it takes
    /// over the Administration building (closed in science mode): clicking it opens the window with every station listed.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class StationWindow : MonoBehaviour
    {
        /// <summary>The traffic list looks this far ahead (game s).</summary>
        private const double AheadS = 24 * 3600;

        private static readonly string[] AccessOrder = { ModuleTrafficPort.Any, ModuleTrafficPort.CrewSupplies, ModuleTrafficPort.Tankers, ModuleTrafficPort.Closed };

        private ApplicationLauncherButton _button;
        private Texture2D _icon;
        private bool _open;
        private Rect _window = new Rect(Screen.width / 2f - 360, 70, 720, 640);
        private bool _resizing;
        private Vector2 _scroll, _listScroll;

        private uint _picked;
        private StationView _view;
        private float _nextLook;
        private string _moving;
        private string _note = "";
        private readonly Dictionary<string, bool> _shut = new Dictionary<string, bool>();
        private PowerBudget _budget;
        private LivePower _livePower;
        private string _where = "";
        private bool _showPowerParts;

        private bool AtKsc => HighLogic.LoadedScene == GameScenes.SPACECENTER;

        private void Start()
        {
            if (AtKsc) return; // opened by the Administration building (WatchAdmin)
            _icon = MakeIcon();
            if (ApplicationLauncher.Ready) AddButton();
            else GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            if (_button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(_button);
            if (_icon != null) Destroy(_icon);
            InputLockManager.RemoveControlLock(LockId);
        }

        private void AddButton()
        {
            if (_button != null || ApplicationLauncher.Instance == null) return;
            _button = ApplicationLauncher.Instance.AddModApplication(() => Open(), () => _open = false, null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW, _icon);
        }

        private void Open()
        {
            _open = true;
            _nextLook = 0;
        }

        private void Close()
        {
            if (_button != null) _button.SetFalse();
            _open = false;
            InputLockManager.RemoveControlLock(LockId);
        }

        // ---------------- The Administration building ----------------

        private const string LockId = "KspFlightPlannerStationWindow";
        private SpaceCenterBuilding _admin;
        private float _nextFind;
        private bool _mouseOver;

        private static readonly FieldInfo ClosedDialog = typeof(SpaceCenterBuilding).GetField("facilityClosedDialog", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo LockedDialog = typeof(SpaceCenterBuilding).GetField("facilityLockedDialog", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private void Update()
        {
            if (!AtKsc) return;
            WatchAdmin();
            // Clicks on the window don't reach the buildings behind it.
            if (_open && _mouseOver) InputLockManager.SetControlLock(ControlTypes.KSC_ALL, LockId);
            else InputLockManager.RemoveControlLock(LockId);
        }

        /// <summary>
        /// With no strategies (science mode, sandbox) KSP answers a click on the Administration building with a "facility
        /// closed" box. When that box comes up, it's closed and the Station window opens instead.
        /// </summary>
        private void WatchAdmin()
        {
            if (_admin == null)
            {
                if (Time.realtimeSinceStartup < _nextFind) return;
                _nextFind = Time.realtimeSinceStartup + 1;
                _admin = FindObjectOfType<AdministrationFacility>();
                if (_admin == null) return;
            }
            foreach (var field in new[] { ClosedDialog, LockedDialog })
            {
                if (field == null || !(field.GetValue(_admin) is PopupDialog box) || box == null) continue;
                box.Dismiss();
                field.SetValue(_admin, null);
                InputLockManager.RemoveControlLock("administrationFacility");
                Open();
            }
        }

        // ---------------- Drawing ----------------

        private void OnGUI()
        {
            if (!_open) return;
            if (Event.current.type == EventType.Repaint) _mouseOver = _window.Contains(Event.current.mousePosition);
            if (Time.realtimeSinceStartup >= _nextLook) Look();
            GUI.skin = null;
            PanelWindow.FollowResize(ref _window, ref _resizing);
            _window = GUILayout.Window(GetInstanceID(), _window, DrawWindow, AtKsc ? "Administration: Stations" : "Station", PanelWindow.Dark(),
                GUILayout.Width(_window.width), GUILayout.Height(_window.height));
        }

        /// <summary>Reads the station again (every second): the craft you're flying in flight, the one picked from the list otherwise.</summary>
        private void Look()
        {
            _nextLook = Time.realtimeSinceStartup + 1;
            try
            {
                if (HighLogic.LoadedSceneIsFlight) _view = FlightGlobals.ActiveVessel != null ? StationView.Of(FlightGlobals.ActiveVessel) : null;
                else
                {
                    if (_picked == 0) _picked = StationView.All().Select(p => p.Key).FirstOrDefault();
                    _view = _picked != 0 ? StationView.Find(_picked) : null;
                }
                ReadPower();
            }
            catch (Exception e)
            {
                _view = null;
                Debug.LogWarning("[KspFlightPlanner] Station window: " + e);
            }
        }

        /// <summary>
        /// The station's power through its night and its core heat, where it is now: a loaded craft's own Sun and measured
        /// flow, an unloaded one's longest night for its orbit or the surface (docs/POWER-AND-HEAT.md).
        /// </summary>
        private void ReadPower()
        {
            _budget = null;
            _livePower = null;
            var v = _view;
            if (v?.Power == null) return;
            try
            {
                var body = FlightGlobals.Bodies?.FirstOrDefault(b => b.bodyName == v.Body);
                if (body == null) return;
                double night, day, distance = Sunlight.SunDistance(body);
                var live = v.Live != null && v.Live.loaded ? v.Live : null;
                if (live != null)
                {
                    _livePower = new LivePower
                    {
                        Sun = live.LandedOrSplashed ? Sunlight.Landed(live) : Sunlight.Orbiting(live),
                        Measured = PowerMeter.Sample(live.persistentId, Planetarium.GetUniversalTime(), v.Power.Stored, v.Power.Capacity),
                    };
                    if (Sunlight.Sun != null) distance = (Sunlight.Sun.position - live.GetWorldPos3D()).magnitude;
                }
                if (v.Landed)
                {
                    var here = _livePower?.Sun;
                    night = here != null ? here.NightLength : Sunlight.SurfaceNight(body);
                    day = Math.Max(0, body.solarDayLength - night);
                    _where = "on " + body.bodyName + (here != null ? " here" : " (at the equator)");
                }
                else
                {
                    night = Sunlight.OrbitNight(body, (v.ApA + v.PeA) / 2, out var period);
                    day = Math.Max(0, period - night);
                    _where = "at its longest, round " + body.bodyName;
                }
                _budget = PowerBudget.Of(v.Power, distance, night, day);
            }
            catch (Exception e)
            {
                _budget = null;
                Debug.LogWarning("[KspFlightPlanner] Station window power: " + e);
            }
        }

        private void DrawWindow(int id)
        {
            if (AtKsc)
            {
                GUILayout.BeginHorizontal();
                DrawList();
                GUILayout.BeginVertical();
                DrawDetails();
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }
            else DrawDetails();
            if (GUILayout.Button("Close")) Close();
            PanelWindow.Grip(_window, ref _resizing);
            GUI.DragWindow();
        }

        /// <summary>At the space centre: every station, to pick one.</summary>
        private void DrawList()
        {
            _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.Width(190), GUILayout.ExpandHeight(true));
            var all = StationView.All();
            if (all.Count == 0) GUILayout.Label("No stations yet. A craft counts once its type is Station, routes fly to it, or someone's on duty aboard.", Wrap());
            foreach (var s in all)
                if (GUILayout.Toggle(s.Key == _picked, " " + s.Value, GUI.skin.button) && s.Key != _picked)
                {
                    _picked = s.Key;
                    _moving = null;
                    _note = "";
                    _nextLook = 0;
                }
            GUILayout.EndScrollView();
        }

        private void DrawDetails()
        {
            var v = _view;
            if (v == null)
            {
                GUILayout.Label(HighLogic.LoadedSceneIsFlight ? "No craft." : "Pick a station.");
                GUILayout.FlexibleSpace();
                return;
            }
            var now = Planetarium.GetUniversalTime();
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            DrawHeader(v, now);
            if (Section("Ports", v)) DrawPorts(v, now);
            if (v.Crafts.Count() > 1 && Section("Docked craft", v)) DrawDocked(v);
            if (Section("Traffic, next 24 h", v)) DrawTraffic(v, now);
            if (Section("Crew", v)) DrawCrew(v, now);
            if (_budget != null && Section("Power", v))
                PowerView.DrawPower(_budget, v.Power, _where, _livePower, i => i.On, null, ref _showPowerParts);
            if (_budget != null && (v.Power.Cores.Count > 0 || v.Power.Radiators.Count > 0) && Section("Heat", v))
                PowerView.DrawHeat(_budget, v.Power, null, r => r.On, null);
            if (v.Supplies.Count > 0 && Section("Supplies", v)) DrawSupplies(v);
            GUILayout.EndScrollView();
            if (_note.Length > 0) GUILayout.Label(_note, Wrap());
        }

        /// <summary>A section heading that folds its section away when clicked.</summary>
        private bool Section(string title, StationView v)
        {
            _shut.TryGetValue(title, out var shut);
            if (GUILayout.Button((shut ? "▶ " : "▼ ") + "<b>" + title + "</b>", Heading())) _shut[title] = !shut;
            return !shut;
        }

        /// <summary>Name, where it is, and whether its traffic is controlled, with each controller's load.</summary>
        private void DrawHeader(StationView v, double now)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<size=15><b>" + v.Name + "</b></size>" + (v.IsStation ? "" : "   <i>(not a station: its crew, ports and supplies)</i>"), Rich());
            GUILayout.Label(v.Landed ? "Landed on " + v.Body : "Round " + v.Body + ", " + Km(v.PeA) + " × " + Km(v.ApA));
            var controllers = ControllersAt(v);
            var rules = TrafficSettings.Current.ControllerRules;
            if (!rules)
            {
                var pilot = v.Parts.SelectMany(p => p.Crew).Any(k => CrewLog.TraitOf(k) == "Pilot");
                GUILayout.Label(pilot ? "Traffic: controlled (a pilot aboard; controller rules are off)" : "Traffic: uncontrolled airspace (no pilot aboard)");
            }
            else if (controllers.Count == 0)
                GUILayout.Label("Traffic: <b>uncontrolled airspace</b>. No Traffic Controller on duty in " + v.MainCraft + "'s own parts: route craft come and go on their own, and probes can't be flown in to dock.", RichWrap());
            else
            {
                GUILayout.Label("Traffic: <b>controlled</b>", Rich());
                foreach (var c in controllers)
                {
                    var cap = CrewRules.Capacity(c.Kerbal);
                    var load = CrewRules.Load(c.Kerbal, now, now + 1, now);
                    var peak = CrewRules.PeakLoad(c.Kerbal, now, now + 6 * 3600);
                    var zone = Routes.Where(r => r.ZoneController == c.Kerbal && r.ZoneStationId == v.Id && CrewRules.Occupies(r)).Select(r => r.VesselName).ToList();
                    GUILayout.Label("   " + c.Kerbal + ": " + load + " of " + cap + " slots now, busiest next 6 h " + peak + " of " + cap +
                                    (peak >= cap ? " (full)" : peak >= cap - 1 ? " (nearly full)" : "") +
                                    (zone.Count > 0 ? "; handling " + string.Join(", ", zone) + " in the zone" : "") +
                                    "; handled " + CrewRules.Handled(c.Kerbal), Wrap());
                }
            }
            GUILayout.EndVertical();
        }

        /// <summary>The station's own ports: free, booked, claimed or occupied, each with its route-craft access to change.</summary>
        private void DrawPorts(StationView v, double now)
        {
            var ports = v.Parts.Where(p => p.Port && p.Craft == v.MainCraft).ToList();
            if (ports.Count == 0) GUILayout.Label("   No docking ports.");
            var n = 0;
            foreach (var p in ports)
            {
                n++;
                GUILayout.BeginHorizontal();
                GUILayout.Label("   " + n + ". " + p.Title + ": " + PortState(v, p, now), Wrap());
                if (GUILayout.Button(AccessName(p.Access), GUILayout.Width(170)))
                {
                    var next = AccessOrder[(Array.IndexOf(AccessOrder, p.Access) + 1) % AccessOrder.Length];
                    _note = StationView.SetAccess(v.Id, p.Id, next) ? "Port " + n + " now takes " + AccessName(next).ToLower() + "." : "Couldn't change port " + n + ".";
                    _nextLook = 0;
                }
                GUILayout.EndHorizontal();
            }
        }

        private static string PortState(StationView v, StationPart p, double now)
        {
            if (p.PortDocked)
            {
                var other = v.OtherSide(p);
                return "occupied by " + (other != null ? other.Craft : "a docked craft");
            }
            foreach (var r in Routes)
            {
                if (r.State == RouteState.Stopped || r.Finished) continue;
                if ((r.State == RouteState.Approaching || r.State == RouteState.Arrived) && r.ApproachPortId == p.Id) return r.VesselName + " coming in";
                if (r.BackingOff && r.BackOffPortId == p.Id) return r.VesselName + " just left, backing off";
                if (r.BookedPortId == p.Id) return "booked by " + r.VesselName + (r.MeetUt > now ? ", arriving in " + Span(r.MeetUt - now) : "");
            }
            return p.PortFree ? "free" : "shut (shielded or busy)";
        }

        private static string AccessName(string access)
        {
            switch (access)
            {
                case ModuleTrafficPort.CrewSupplies: return "Crew & supplies only";
                case ModuleTrafficPort.Tankers: return "Tankers only";
                case ModuleTrafficPort.Closed: return "No route craft";
                default: return "Any craft";
            }
        }

        /// <summary>Each craft docked to the station: its route (what it's doing), whether it's switched off while docked, and a button to cancel a held departure.</summary>
        private void DrawDocked(StationView v)
        {
            foreach (var craft in v.Crafts.Where(c => c != v.MainCraft))
            {
                var parts = v.Parts.Where(p => p.Craft == craft).Select(p => p.Id).ToList();
                var quiet = DockedQuiet.Crafts.Any(q => parts.Contains(q.PortPartId));
                var crew = v.Parts.Where(p => p.Craft == craft).Sum(p => p.Crew.Count);
                var route = Routes.LastOrDefault(r => r.VesselName == craft && r.State != RouteState.Stopped);
                GUILayout.BeginHorizontal();
                GUILayout.Label("   <b>" + craft + "</b>" + (crew > 0 ? ", " + crew + " aboard" : ", no crew") + (quiet ? ", engines, RCS and wheels off while docked" : "") +
                                (route != null ? "\n      " + RouteLine(route) : ""), RichWrap());
                if (route != null && route.State == RouteState.Held && GUILayout.Button("Cancel trip", GUILayout.Width(100)))
                {
                    TrafficScenario.Stop(route, "cancelled from the Station window");
                    _note = route.VesselName + "'s trip is cancelled; it stays docked.";
                }
                GUILayout.EndHorizontal();
            }
        }

        private static string RouteLine(Route r)
        {
            var s = r.Summary();
            var head = r.VesselName + " → ";
            return s.StartsWith(head, StringComparison.Ordinal) ? "→ " + s.Substring(head.Length) : s;
        }

        /// <summary>
        /// The next day: craft arriving here (or waiting for a port), departures held here, and burns in this SOI, which its
        /// controllers upload (marked when the craft is only passing through). A burn its SOI's controllers can't all cover
        /// (more burns as urgent or more overlapping it than they have slots) is flagged.
        /// </summary>
        private void DrawTraffic(StationView v, double now)
        {
            var items = new List<KeyValuePair<double, string>>();
            foreach (var r in Routes)
            {
                if (r.State == RouteState.Stopped) continue;
                var here = r.TargetId == v.Id || r.TargetName == v.Name;
                var from = r.FromId == v.Id || r.FromName == v.Name;
                if (here && !r.Finished)
                {
                    var port = r.BookedPortId != 0 ? v.Parts.FirstOrDefault(p => p.Id == r.BookedPortId) : null;
                    var portText = port != null ? "port " + (v.Parts.Where(p => p.Port && p.Craft == v.MainCraft).ToList().IndexOf(port) + 1) : "no port booked yet";
                    switch (r.State)
                    {
                        case RouteState.Waiting:
                            items.Add(new KeyValuePair<double, string>(now, "Waiting: " + r.VesselName + ", 3 km behind for " + (r.ArrivalHeldNote ?? "a free port")));
                            break;
                        case RouteState.Approaching:
                        case RouteState.Arrived:
                            items.Add(new KeyValuePair<double, string>(now, "Arriving: " + r.VesselName + ", coming in to " + portText));
                            break;
                        case RouteState.Held:
                            break;
                        default:
                            if (r.MeetUt < now + AheadS) items.Add(new KeyValuePair<double, string>(r.MeetUt, "Arrives: " + r.VesselName + " from " + r.FromName + ", " + portText));
                            break;
                    }
                }
                if (from && r.State == RouteState.Held)
                    items.Add(new KeyValuePair<double, string>(now, "Leaving: " + r.VesselName + " for " + r.TargetName + ", " +
                                                                   (r.AwaitingApproval ? "waiting for your approval (some burns won't have a controller)" : "held for clearance" + (r.ReplanNote != null ? ": " + r.ReplanNote : ""))));
                if (r.Finished) continue;
                foreach (var b in r.Burns)
                {
                    if (b.Done || b.Ut < now || b.Ut > now + AheadS || b.PlannedBody != v.Body) continue;
                    var text = "Burn: " + r.VesselName + ", " + (b.Title.Length > 0 ? b.Title : "burn") + ", priority " + b.Priority + (b.Critical ? " (critical)" : "") + ", " +
                               (b.Uploaded ? "uploaded by " + b.Controller : "not uploaded yet") + (here ? "" : " (passing through)");
                    var warn = BurnWarning(r, b, v.Body);
                    items.Add(new KeyValuePair<double, string>(b.Ut, text + (warn.Length > 0 ? "\n      ⚠ " + warn : "")));
                }
            }
            if (items.Count == 0) GUILayout.Label("   Nothing due here.");
            foreach (var it in items.OrderBy(i => i.Key))
                GUILayout.Label("   " + (it.Key <= now + 1 ? "now" : KSPUtil.dateTimeFormatter.PrintDateCompact(it.Key, true, false) + " (in " + Span(it.Key - now) + ")") + "  " + it.Value, Wrap());
        }

        /// <summary>Why a burn in this SOI might not get a controller ("" when it should): none covering the SOI, or too many burns at once.</summary>
        private static string BurnWarning(Route route, RouteBurn burn, string body)
        {
            if (!TrafficSettings.Current.ControllerRules || !CrewRules.NeedsController(route)) return "";
            var controllers = CrewRules.ControllersFor(body).ToList();
            if (controllers.Count == 0) return "no Traffic Controller on duty covers " + body;
            var supply = controllers.Sum(c => CrewRules.Capacity(c.Kerbal));
            double from = CrewRules.SlotStart(burn), to = CrewRules.SlotEnd(burn);
            var demand = Routes.Where(r => r.State != RouteState.Stopped && !r.Finished && CrewRules.NeedsController(r))
                .SelectMany(r => r.Burns).Count(b => !b.Done && b.PlannedBody == body && b.Priority <= burn.Priority && CrewRules.SlotStart(b) < to && CrewRules.SlotEnd(b) > from);
            return demand > supply ? demand + " burns as urgent or more at once, " + supply + " slots: one may be bumped" : "";
        }

        /// <summary>
        /// Who's aboard, part by part: trait, duty and job, a button to put them on or off duty, and one to move them to
        /// another part with a free seat (a pilot needs a part with controls to be the station's Traffic Controller).
        /// </summary>
        private void DrawCrew(StationView v, double now)
        {
            var duty = KerbinResourcesScenario.Instance?.Duty;
            var aboard = v.Parts.Where(p => p.Crew.Count > 0).OrderBy(p => p.Craft == v.MainCraft ? 0 : 1).ToList();
            if (aboard.Count == 0) GUILayout.Label("   Nobody aboard.");
            foreach (var part in aboard)
            foreach (var kerbal in part.Crew)
            {
                var trait = CrewLog.TraitOf(kerbal);
                var shift = duty?.ShiftOf(kerbal);
                GUILayout.BeginHorizontal();
                var where = "in " + part.Title + (part.Craft != v.MainCraft ? " (docked " + part.Craft + ")" : "");
                var dutyText = shift != null ? "<b>" + shift.JobTitle + "</b>" : "off duty";
                var hint = trait == "Pilot" && part.Craft == v.MainCraft && !part.Command && v.IsStation ? "  <i>no controls here: move to a cupola or pod to control traffic</i>" : "";
                GUILayout.Label("   <b>" + kerbal + "</b> (" + trait + "), " + where + ": " + dutyText + hint, RichWrap());
                if (duty != null && trait.Length > 0 && trait != "Tourist")
                {
                    if (shift != null && GUILayout.Button("Off duty", GUILayout.Width(75))) duty.TakeOff(kerbal, now);
                    else if (shift == null && GUILayout.Button("On duty", GUILayout.Width(75)))
                        _note = duty.PutOn(kerbal, now, out var why) == null ? "Can't put " + kerbal + " on duty: " + why : "";
                }
                if (GUILayout.Button(_moving == kerbal ? "Cancel" : "Move…", GUILayout.Width(65))) _moving = _moving == kerbal ? null : kerbal;
                GUILayout.EndHorizontal();
                if (_moving == kerbal) DrawMoveTargets(v, kerbal, part, now);
            }
            if (v.IsStation && duty != null && !v.Parts.Any(p => p.Craft == v.MainCraft && p.Command && p.Crew.Any(k => CrewLog.TraitOf(k) == "Pilot")))
                GUILayout.Label("   No pilot sits in a part with controls of " + v.MainCraft + "'s own: move one into its cupola or pod to be its Traffic Controller.", Wrap());
        }

        private void DrawMoveTargets(StationView v, string kerbal, StationPart from, double now)
        {
            var targets = v.Parts.Where(p => p.Id != from.Id && p.FreeSeats > 0).OrderBy(p => p.Craft == v.MainCraft ? 0 : 1).ThenBy(p => p.Command ? 0 : 1).ToList();
            if (targets.Count == 0) GUILayout.Label("         No free seats anywhere else aboard.");
            foreach (var t in targets)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(40);
                if (GUILayout.Button("→ " + t.Title + (t.Craft != v.MainCraft ? " (docked " + t.Craft + ")" : "") + ", " + t.FreeSeats + " free" + (t.Command ? ", has controls" : ""), Left()))
                {
                    if (CrewMove.Move(v.Id, kerbal, t.Id, this, out var why))
                    {
                        KerbinResourcesScenario.Instance?.Duty.LookOver(now);
                        _note = kerbal + " moved to " + t.Title + ".";
                        _moving = null;
                    }
                    else _note = "Can't move " + kerbal + ": " + why + ".";
                    _nextLook = 0;
                }
                GUILayout.EndHorizontal();
            }
        }

        private static void DrawSupplies(StationView v)
        {
            foreach (var s in v.Supplies)
            {
                var name = PartResourceLibrary.Instance?.GetDefinition(s.Key)?.displayName;
                GUILayout.Label("   " + (string.IsNullOrEmpty(name) ? s.Key : name) + ": " + s.Value[0].ToString("N0") + " of " + s.Value[1].ToString("N0") +
                                " (" + (100 * s.Value[0] / s.Value[1]).ToString("0") + "%)");
            }
        }

        // ---------------- Helpers ----------------

        private static IEnumerable<Route> Routes => TrafficScenario.Instance?.Routes ?? Enumerable.Empty<Route>();

        /// <summary>Traffic Controllers on duty in the station's own parts (works loaded or not: their post's part is in the stack).</summary>
        private static List<DutyShift> ControllersAt(StationView v)
        {
            var duty = KerbinResourcesScenario.Instance?.Duty;
            if (duty == null) return new List<DutyShift>();
            return duty.OnDuty.Where(s => CrewRules.IsTrafficController(s) && !s.AtKsc && !s.InDockedShip && (s.PostId == v.Id || v.HasPart(s.PostPartId))).ToList();
        }

        private static string Km(double m) => (m / 1000).ToString("N0") + " km";

        private static string Span(double seconds) => KSPUtil.dateTimeFormatter.PrintTime(Math.Max(0, seconds), 2, false);

        private static GUIStyle _wrap, _rich, _richWrap, _heading, _left;

        private static GUIStyle Wrap() => _wrap ?? (_wrap = new GUIStyle(GUI.skin.label) { wordWrap = true });

        private static GUIStyle Rich() => _rich ?? (_rich = new GUIStyle(GUI.skin.label) { richText = true });

        private static GUIStyle RichWrap() => _richWrap ?? (_richWrap = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });

        private static GUIStyle Heading() => _heading ?? (_heading = new GUIStyle(GUI.skin.label) { richText = true, margin = new RectOffset(4, 4, 8, 2) });

        private static GUIStyle Left() => _left ?? (_left = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft });

        /// <summary>A plain toolbar icon: a station (a hub with two panels), in KSP's blue.</summary>
        private static Texture2D MakeIcon()
        {
            const int size = 38;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var clear = new Color(0, 0, 0, 0);
            var blue = new Color(0.45f, 0.75f, 1f, 1);
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var hub = Math.Abs(x - 19) <= 4 && Math.Abs(y - 19) <= 7;
                var truss = Math.Abs(y - 19) <= 1 && x >= 4 && x <= 34;
                var panel = (x >= 3 && x <= 11 || x >= 27 && x <= 35) && Math.Abs(y - 19) <= 9 && (y % 3 != 0);
                tex.SetPixel(x, y, hub || truss || panel ? blue : clear);
            }
            tex.Apply();
            return tex;
        }
    }

    /// <summary>The same window at the space centre, opened by the Administration building, with every station listed.</summary>
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public sealed class StationWindowKsc : StationWindow
    {
    }
}
