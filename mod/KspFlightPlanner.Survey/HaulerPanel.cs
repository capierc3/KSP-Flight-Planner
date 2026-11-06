using System;
using KSP.UI.Screens;
using UnityEngine;

namespace KspFlightPlanner.Haulers
{
    /// <summary>
    /// The haulers' panel in flight: a button on KSP's toolbar (right side) opens a small window listing every route, what
    /// it's doing, and for a ship near you, what the autopilot is doing with it and a switch to turn the autopilot off
    /// (to fly it yourself) or back on. Touching the controls of a hauler you're flying switches it off too.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class HaulerPanel : MonoBehaviour
    {
        private ApplicationLauncherButton _button;
        private Texture2D _icon;
        private bool _open;
        private Rect _window = new Rect(Screen.width - 460, 80, 420, 10);
        private Vector2 _scroll;

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
                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW, _icon);
        }

        private void OnGUI()
        {
            if (!_open) return;
            GUI.skin = HighLogic.Skin;
            _window = GUILayout.Window(GetInstanceID(), _window, DrawWindow, "Haulers", GUILayout.Width(420));
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
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(Math.Min(400, 90 * scenario.Routes.Count + 10)));
                foreach (var route in scenario.Routes)
                {
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.Label(route.Summary());
                    var flying = PhysicsFlyer.Status(route);
                    if (route.State == RouteState.Flying || route.State == RouteState.Approaching || route.State == RouteState.Arrived)
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
            if (GUILayout.Button("Close")) _button?.SetFalse();
            GUI.DragWindow();
        }

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
}
