using System;
using System.Collections.Generic;
using System.Linq;
using KSP.UI.Screens;
using KspFlightPlanner.Traffic;
using UnityEngine;

namespace KspFlightPlanner.Refueler
{
    /// <summary>
    /// The Refueler window, in flight: its own button on KSP's toolbar, apart from the Traffic Controller window. For the stack
    /// you're flying it lists each docked vessel with a tick for "station tank" (whose fuel is the station's), and moves
    /// fuel between one docked craft and those station tanks, either way, started and stopped by hand (<see cref="RefuelerView"/>).
    /// Other craft docked at the station are left alone. Unloading stops by itself when the craft is down to the Δv you said
    /// to keep. The pump runs in <see cref="RefuelerScenario"/>, so it carries on when you fly something else or leave the
    /// scene; the Station window has the same controls in the tracking station and at the space centre.
    /// docs/TRAFFIC-CONTROLLER.md, "Fuel transfer and RCS balance".
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class RefuelerPanel : MonoBehaviour
    {
        private ApplicationLauncherButton _button;
        private Texture2D _icon;
        private bool _open;
        private Rect _window = new Rect(Screen.width - 940, 80, 440, 380);
        private Vector2 _scroll;
        private bool _resizing;

        // The controls, shared with the Station window; the pumping itself runs in RefuelerScenario.
        private readonly RefuelerView _view = new RefuelerView();

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
            _view.Dispose();
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
            GUI.skin = null;
            PanelWindow.FollowResize(ref _window, ref _resizing);
            _window = GUILayout.Window(GetInstanceID(), _window, DrawWindow, "Refueler", GUILayout.Width(_window.width), GUILayout.Height(_window.height));
        }

        private void DrawWindow(int id)
        {
            var stack = FlightGlobals.ActiveVessel;
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (stack == null) GUILayout.Label("Not flying a vessel.");
            else _view.Draw(stack.persistentId);
            GUILayout.EndScrollView();
            if (GUILayout.Button("Close")) _button?.SetFalse();
            PanelWindow.Grip(_window, ref _resizing);
            GUI.DragWindow();
        }

        /// <summary>A plain toolbar icon drawn in code: a fuel drop, in KSP's green.</summary>
        private static Texture2D MakeIcon()
        {
            const int size = 38;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var clear = new Color(0, 0, 0, 0);
            var green = new Color(0.55f, 0.9f, 0.35f, 1);
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                // A round bottom (circle at y = 14) with a point on top (y = 32); texture rows count up from the bottom.
                var dx = x - 19f;
                var round = dx * dx + (y - 14f) * (y - 14f) <= 81f;
                var point = y >= 14 && y <= 32 && Math.Abs(dx) <= 9f * (32 - y) / 18f;
                tex.SetPixel(x, y, round || point ? green : clear);
            }
            tex.Apply();
            return tex;
        }
    }
}
