using System;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace KspFlightPlanner.Haulers
{
    /// <summary>
    /// The haulers' panel, in flight and the tracking station: a button on KSP's toolbar (right side) opens a small window
    /// listing every route: what it's doing, its next burn (or the meeting) as a game date with a countdown and a button
    /// to warp to just before it, and for a ship near you, what the autopilot is doing with it and a switch to turn the
    /// autopilot off (to fly it yourself) or back on. Touching the controls of a hauler you're flying switches it off too.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class HaulerPanel : MonoBehaviour
    {
        /// <summary>Warp stops this long before a burn (or the meeting) on rails (game s), to see it happen.</summary>
        private const double WarpLeadS = 60;

        private ApplicationLauncherButton _button;
        private Texture2D _icon;
        private bool _open;
        private Rect _window = new Rect(Screen.width - 480, 80, 440, 420);
        private Vector2 _scroll;
        private bool _resizing;

        // The Fuel section: which docked craft, which way, the Δv to keep when unloading, and whether it's running.
        private int _craft;
        private bool _unload;
        private string _keepText = "300";
        private bool _moving;
        private string _fuelNote;
        private string _rcsNote;

        private void Start()
        {
            _icon = MakeIcon();
            if (ApplicationLauncher.Ready) AddButton();
            else GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            if (_button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(_button);
            if (_icon != null) Destroy(_icon);
        }

        private void AddButton()
        {
            if (_button != null || ApplicationLauncher.Instance == null) return;
            _button = ApplicationLauncher.Instance.AddModApplication(() => _open = true, () => _open = false, null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW | ApplicationLauncher.AppScenes.TRACKSTATION, _icon);
        }

        private void OnGUI()
        {
            if (!_open) return;
            GUI.skin = null; // Unity's own look, as kRPC, EVE and Firefly's windows use (KSP's old green skin looked out of place)
            // Resizing from the corner grip: the window follows the mouse until the button comes up.
            if (_resizing)
            {
                var e = Event.current;
                if (e.type == EventType.MouseUp) _resizing = false;
                else if (e.type == EventType.MouseDrag)
                {
                    _window.width = Mathf.Clamp(e.mousePosition.x - _window.x + 8, 320, Screen.width - _window.x);
                    _window.height = Mathf.Clamp(e.mousePosition.y - _window.y + 8, 200, Screen.height - _window.y);
                    e.Use();
                }
            }
            _window = GUILayout.Window(GetInstanceID(), _window, DrawWindow, "Haulers", GUILayout.Width(_window.width), GUILayout.Height(_window.height));
        }

        /// <summary>The Fuel section's transfer runs here, a little every frame, until it's done or stopped.</summary>
        private void Update()
        {
            if (!_moving) return;
            if (!HighLogic.LoadedSceneIsFlight || !(FlightGlobals.ActiveVessel is Vessel stack))
            {
                _moving = false;
                return;
            }
            var crafts = FuelTransfer.DockedCrafts(stack);
            if (_craft >= crafts.Count)
            {
                _moving = false;
                _fuelNote = "The craft isn't docked any more.";
                return;
            }
            var craft = HaulerShip.DockedCraftParts(crafts[_craft].Value);
            var station = FuelTransfer.Others(stack, craft);
            if (_unload && double.TryParse(_keepText, out var keep) && HaulerShip.DeltaVLeft(craft, out _) <= keep)
            {
                _moving = false;
                _fuelNote = "Stopped: " + crafts[_craft].Key + " keeps " + keep.ToString("0") + " m/s.";
                return;
            }
            double moved = 0;
            foreach (var res in FuelTransfer.Resources)
            {
                // At this rate a craft's tanks fill or empty in about 30 s.
                FuelTransfer.Totals(craft, res, out _, out var room);
                var units = Math.Max(1, room / 30) * Time.deltaTime;
                moved += _unload ? FuelTransfer.Move(craft, station, res, units) : FuelTransfer.Move(station, craft, res, units);
            }
            if (moved <= 1e-6)
            {
                _moving = false;
                _fuelNote = _unload ? "Done: " + crafts[_craft].Key + " is empty or the station is full." : "Done: " + crafts[_craft].Key + " is full or the station is empty.";
            }
        }

        private void DrawWindow(int id)
        {
            var scenario = HaulerScenario.Instance;
            if (scenario == null || scenario.Routes.Count == 0)
            {
                GUILayout.Label("No hauler routes in this save. Plan one in the planner: From here › Go to… › Fly it in the background.");
            }
            else
            {
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
                foreach (var route in scenario.Routes)
                {
                    GUILayout.BeginVertical(GUI.skin.box);
                    if (route.State == RouteState.Flying)
                    {
                        GUILayout.Label(route.VesselName + " → " + route.TargetName + ": " + route.Burns.Count(b => b.Done) + " of " + route.Burns.Count + " burns done");
                        DrawNext(route);
                    }
                    else if (route.State == RouteState.OffCourse)
                    {
                        GUILayout.Label(route.VesselName + " → " + route.TargetName + ": off course, paused");
                        DrawOffCourse(route);
                    }
                    else GUILayout.Label(route.Summary());
                    if (route.State == RouteState.Approaching && !double.IsNaN(route.ApproachNextCheckUt) && TimeWarp.fetch != null)
                    {
                        var warpTo = route.ApproachNextCheckUt - RailsFlyer.WarpStopS;
                        var now = Planetarium.GetUniversalTime();
                        if (warpTo > now + 5 && GUILayout.Button("Warp to next check (" + Span(warpTo - now) + ")")) TimeWarp.fetch.WarpTo(warpTo);
                    }
                    var flying = PhysicsFlyer.Status(route);
                    if (HighLogic.LoadedSceneIsFlight && (route.State == RouteState.Flying || route.State == RouteState.Approaching || route.State == RouteState.Arrived))
                    {
                        var near = CourseCheck.OffRails(HaulerShip.Find(route.VesselId));
                        var on = GUILayout.Toggle(route.Autopilot, near ? " Autopilot" : " Autopilot (when it's near you)");
                        if (on != route.Autopilot)
                        {
                            route.Autopilot = on;
                            HaulerScenario.Note(route, on ? "Autopilot on." : "Autopilot off: fly it yourself.", false);
                        }
                        if (flying != null) GUILayout.Label("Autopilot: " + flying);
                        else if (near && !route.Autopilot)
                            GUILayout.Label("You're flying it. Once you leave, it's checked (planned again if you moved it) and the autopilot comes back on.");
                    }
                    GUILayout.EndVertical();
                }
                GUILayout.EndScrollView();
            }
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel is Vessel active)
            {
                DrawFuel(active);
                DrawRcs(active);
            }
            if (GUILayout.Button("Close")) _button?.SetFalse();
            // The resize grip, bottom right.
            var grip = new Rect(_window.width - 20, _window.height - 20, 18, 18);
            GUI.Label(grip, "◢");
            if (Event.current.type == EventType.MouseDown && grip.Contains(Event.current.mousePosition))
            {
                _resizing = true;
                Event.current.Use();
            }
            GUI.DragWindow();
        }

        /// <summary>
        /// Fuel: for a craft docked in the stack you're flying, its tanks and the station's, its Δv, and a transfer either
        /// way, started and stopped by hand; unloading stops by itself when the craft is down to the Δv you said to keep
        /// (enough to deorbit, say).
        /// </summary>
        private void DrawFuel(Vessel stack)
        {
            var crafts = FuelTransfer.DockedCrafts(stack);
            if (crafts.Count == 0) return;
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("Fuel: " + stack.vesselName);
            if (_craft >= crafts.Count) _craft = 0;
            if (crafts.Count > 1 && !_moving)
            {
                GUILayout.BeginHorizontal();
                for (var i = 0; i < crafts.Count; i++)
                    if (GUILayout.Toggle(_craft == i, crafts[i].Key, GUI.skin.button)) _craft = i;
                GUILayout.EndHorizontal();
            }
            var name = crafts[_craft].Key;
            var craft = HaulerShip.DockedCraftParts(crafts[_craft].Value);
            var station = FuelTransfer.Others(stack, craft);
            foreach (var res in FuelTransfer.Resources)
            {
                FuelTransfer.Totals(craft, res, out var a, out var am);
                FuelTransfer.Totals(station, res, out var b, out var bm);
                if (am <= 0 && bm <= 0) continue;
                GUILayout.Label(res + ":  " + name + " " + a.ToString("N0") + " / " + am.ToString("N0") + "    station " + b.ToString("N0") + " / " + bm.ToString("N0"));
            }
            GUILayout.Label(name + " Δv: " + HaulerShip.DeltaVLeft(craft, out _).ToString("N0") + " m/s");
            if (!_moving)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Toggle(!_unload, "Fill " + name, GUI.skin.button)) _unload = false;
                if (GUILayout.Toggle(_unload, "Unload to the station", GUI.skin.button)) _unload = true;
                GUILayout.EndHorizontal();
                if (_unload)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Keep at least", GUILayout.ExpandWidth(false));
                    _keepText = GUILayout.TextField(_keepText, GUILayout.Width(60));
                    GUILayout.Label("m/s in " + name, GUILayout.ExpandWidth(false));
                    GUILayout.EndHorizontal();
                }
                if (GUILayout.Button("Start"))
                {
                    _moving = true;
                    _fuelNote = null;
                }
            }
            else if (GUILayout.Button("Stop"))
            {
                _moving = false;
                _fuelNote = "Stopped.";
            }
            if (_fuelNote != null) GUILayout.Label(_fuelNote);
            GUILayout.EndVertical();
        }

        /// <summary>Balance RCS: thrust limiters on the ship's RCS blocks so a push moves it without turning it.</summary>
        private void DrawRcs(Vessel ship)
        {
            if (ship.FindPartModulesImplementing<ModuleRCS>().Count == 0) return;
            GUILayout.BeginVertical(GUI.skin.box);
            if (GUILayout.Button("Balance RCS on " + ship.vesselName))
                _rcsNote = RcsBalance.Balance(ship, out var before, out var after)
                    ? after < before
                        ? "Turning per push: " + (100 * before).ToString("0.#") + "% → " + (100 * after).ToString("0.#") + "% (0% is perfect)."
                        : "Already balanced as well as it can be (" + (100 * before).ToString("0.#") + "%)."
                    : "No RCS.";
            if (_rcsNote != null) GUILayout.Label(_rcsNote);
            GUILayout.EndVertical();
        }

        /// <summary>
        /// The next thing on a flying route: the burn (or the meeting), when as a game date and how long till then, and a
        /// button to warp to just before it. Near you with the autopilot on, the burn starts early (half its length) and
        /// the autopilot needs time to turn the ship, so it warps to 40 s before the start; otherwise to a minute
        /// before the burn, so you see the orbit change.
        /// </summary>
        private static void DrawNext(HaulerRoute route)
        {
            var now = Planetarium.GetUniversalTime();
            var burn = route.Burns.FirstOrDefault(b => !b.Done);
            var ut = burn != null ? burn.Ut : route.MeetUt;
            var number = burn != null ? route.Burns.IndexOf(burn) + 1 : 0;
            GUILayout.Label(burn != null
                ? "Next: burn " + number + " of " + route.Burns.Count + ", " + (burn.Title.Length > 0 ? burn.Title : "burn") + ", " + burn.DeltaV.ToString("0") + " m/s"
                : "Next: meets " + route.TargetName);
            GUILayout.Label(Date(ut) + "  (" + (ut > now ? "in " + Span(ut - now) : "due now") + ")");

            var ship = HaulerShip.Find(route.VesselId);
            var start = ut;
            var lead = WarpLeadS;
            if (burn != null && ship != null && ship.loaded && route.Autopilot && HighLogic.LoadedSceneIsFlight)
            {
                start = ut - PhysicsFlyer.StartLead(ship, burn.DeltaV);
                lead = PhysicsFlyer.TurnLeadS + 10;
            }
            var warpTo = start - lead;
            if (warpTo > now + 5 && TimeWarp.fetch != null && GUILayout.Button("Warp to " + Span(ut - warpTo) + " before it"))
                TimeWarp.fetch.WarpTo(warpTo);
        }

        /// <summary>
        /// An off-course route: why, and what it's waiting for. The planner plans it again from where it is (while it's
        /// connected); when it can't yet because the ship is still leaving a moon's pull, a button warps to the moment it's
        /// out, when the planner can.
        /// </summary>
        private static void DrawOffCourse(HaulerRoute route)
        {
            var why = route.Log.LastOrDefault(l => l.StartsWith("Off course: ", StringComparison.Ordinal));
            if (why != null) GUILayout.Label(why.Substring("Off course: ".Length));
            GUILayout.Label(route.ReplanNote != null ? "Waiting: " + route.ReplanNote : "The planner plans it again from where it is (keep it running and connected).");
            var ship = HaulerShip.Find(route.VesselId);
            if (ship == null || TimeWarp.fetch == null) return;
            var leaves = HaulerShip.LeavesSphereUt(ship);
            var now = Planetarium.GetUniversalTime();
            if (leaves > now + 10 && GUILayout.Button("Warp to leaving " + ship.orbit.referenceBody.bodyName + "'s pull (" + Span(leaves - now) + ")"))
                TimeWarp.fetch.WarpTo(leaves + 5);
        }

        /// <summary>A game time as KSP shows it on its clock ("Y1, D197, 00:53:54"), on the save's calendar (Kerbin or Earth days).</summary>
        private static string Date(double ut) => KSPUtil.dateTimeFormatter.PrintDateCompact(ut, true, true);

        /// <summary>A length of game time, its two biggest units ("1h, 12m").</summary>
        private static string Span(double seconds) => KSPUtil.dateTimeFormatter.PrintTime(seconds, 2, false);

        /// <summary>A plain toolbar icon drawn in code (no image files to ship): a box with an arrow, in KSP's green.</summary>
        private static Texture2D MakeIcon()
        {
            const int size = 38;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var clear = new Color(0, 0, 0, 0);
            var green = new Color(0.55f, 0.9f, 0.35f, 1);
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var box = x >= 6 && x <= 20 && y >= 12 && y <= 26 && (x == 6 || x == 20 || y == 12 || y == 26 || (x + y) % 4 == 0);
                var shaft = y >= 18 && y <= 20 && x >= 22 && x <= 30;
                var head = x >= 28 && x <= 34 && Math.Abs(y - 19) <= 34 - x;
                tex.SetPixel(x, y, box || shaft || head ? green : clear);
            }
            tex.Apply();
            return tex;
        }
    }

    /// <summary>The same panel in the tracking station, where you usually watch a trip from.</summary>
    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    public sealed class HaulerPanelTracking : HaulerPanel
    {
    }
}
