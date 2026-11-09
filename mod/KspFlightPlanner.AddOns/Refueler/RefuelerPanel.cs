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
    /// fuel between one docked craft and those station tanks, either way, started and stopped by hand. Other craft docked
    /// at the station are left alone. Unloading stops by itself when the craft is down to the Δv you said to keep.
    /// docs/MATH.md §11.9.
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

        // Which craft (by name), which way, the Δv to keep when unloading, and whether it's running.
        private string _craft;
        private bool _unload;
        private string _keepText = "300";
        private bool _moving;
        private string _note;

        // The gauges' plain white texture (tinted when drawn) and their centred text.
        private Texture2D _white;
        private GUIStyle _gaugeText;

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
            if (_white != null) Destroy(_white);
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

        /// <summary>The transfer runs here, a little every frame, until it's done or stopped.</summary>
        private void Update()
        {
            if (!_moving) return;
            if (!(FlightGlobals.ActiveVessel is Vessel stack))
            {
                _moving = false;
                return;
            }
            var pieces = FuelTransfer.Pieces(stack);
            var piece = pieces.FirstOrDefault(p => p.Name == _craft && !p.IsStation);
            if (piece == null)
            {
                _moving = false;
                _note = "The craft isn't docked any more.";
                return;
            }
            var craft = piece.Parts;
            var station = FuelTransfer.StationParts(pieces);
            if (_unload && double.TryParse(_keepText, out var keep) && Ships.DeltaVLeft(craft, out _) <= keep)
            {
                _moving = false;
                _note = "Stopped: " + _craft + " keeps " + keep.ToString("0") + " m/s.";
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
                _note = _unload ? "Done: " + _craft + " is empty or the station's tanks are full." : "Done: " + _craft + " is full or the station's tanks are empty.";
            }
        }

        private void DrawWindow(int id)
        {
            var stack = FlightGlobals.ActiveVessel;
            var pieces = FuelTransfer.Pieces(stack);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (pieces.Count < 2) GUILayout.Label(stack == null ? "Not flying a vessel." : "Nothing is docked to " + stack.vesselName + ".");
            else
            {
                DrawPieces(pieces);
                DrawTransfer(pieces);
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Close")) _button?.SetFalse();
            PanelWindow.Grip(_window, ref _resizing);
            GUI.DragWindow();
        }

        /// <summary>
        /// Every vessel in the stack, with a tick for "station tank": fuel moves to and from the ticked ones only. Unticked
        /// ones are craft of their own, to fill or unload.
        /// </summary>
        private void DrawPieces(List<StackPiece> pieces)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("Station tanks (tick the parts whose fuel is the station's)");
            foreach (var piece in pieces)
            {
                GUI.enabled = !_moving;
                var station = piece.IsStation;
                var on = GUILayout.Toggle(station, " " + piece.Name + "  (" + piece.Type.ToString().ToLowerInvariant() + ")" + FuelSummary(piece.Parts));
                if (on != station) FuelTransfer.SetStationMark(piece, on);
                GUI.enabled = true;
            }
            GUILayout.EndVertical();
        }

        /// <summary>Fill or unload: one craft, its tanks against the station's, its Δv, and the transfer.</summary>
        private void DrawTransfer(List<StackPiece> pieces)
        {
            var crafts = pieces.Where(p => !p.IsStation).ToList();
            GUILayout.BeginVertical(GUI.skin.box);
            if (crafts.Count == 0)
            {
                GUILayout.Label("No craft to fill: untick a vessel above to treat it as a craft of its own.");
                GUILayout.EndVertical();
                return;
            }
            if (!_moving && crafts.All(c => c.Name != _craft)) _craft = crafts[0].Name;
            var piece = crafts.FirstOrDefault(c => c.Name == _craft) ?? crafts[0];
            if (crafts.Count > 1 && !_moving)
            {
                GUILayout.Label("Craft:");
                foreach (var c in crafts)
                    if (GUILayout.Toggle(_craft == c.Name, c.Name, GUI.skin.button)) _craft = c.Name;
            }
            var name = piece.Name;
            var craft = piece.Parts;
            var station = FuelTransfer.StationParts(pieces);
            foreach (var res in FuelTransfer.Resources)
            {
                FuelTransfer.Totals(craft, res, out var a, out var am);
                FuelTransfer.Totals(station, res, out var b, out var bm);
                if (am <= 0 && bm <= 0) continue;
                // Each fuel in its own box under a bold heading, so a heading can't be read as part of the gauges above it.
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label("<b>" + Long(res) + "</b>", Heading());
                Gauge(name, a, am);
                Gauge("Station", b, bm);
                GUILayout.EndVertical();
                GUILayout.Space(4);
            }
            GUILayout.Label("<b>" + name + " Δv:</b> " + Ships.DeltaVLeft(craft, out _).ToString("N0") + " m/s", Heading());
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
                if (station.Count == 0) GUILayout.Label("Tick at least one station tank above.");
                else if (GUILayout.Button("Start"))
                {
                    _moving = true;
                    _note = null;
                }
            }
            else if (GUILayout.Button("Stop"))
            {
                _moving = false;
                _note = "Stopped.";
            }
            if (_note != null) GUILayout.Label(_note);
            GUILayout.EndVertical();
        }

        /// <summary>A short line of what these parts hold: "  · LF 1,200/2,880 · Mono 40/750"; empty when they hold none.</summary>
        private static string FuelSummary(IList<Part> parts)
        {
            var bits = new List<string>();
            foreach (var res in FuelTransfer.Resources)
            {
                FuelTransfer.Totals(parts, res, out var a, out var am);
                if (am > 0) bits.Add(Short(res) + " " + a.ToString("N0") + "/" + am.ToString("N0"));
            }
            return bits.Count == 0 ? "" : "  · " + string.Join(" · ", bits.ToArray());
        }

        /// <summary>
        /// One fuel gauge: a name on the left, then a bar filled green to the level, with "held / room  percent" written
        /// across it. A craft or station with no room for this fuel gets a grey "none" bar.
        /// </summary>
        private void Gauge(string label, double amount, double max)
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            if (_gaugeText == null)
                _gaugeText = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120));
            var bar = GUILayoutUtility.GetRect(100, 20, GUILayout.ExpandWidth(true), GUILayout.Height(20));
            var share = max > 0 ? Mathf.Clamp01((float)(amount / max)) : 0f;
            var was = GUI.color;
            GUI.color = new Color(0.12f, 0.14f, 0.12f, 1);
            GUI.DrawTexture(bar, _white);
            GUI.color = share < 0.15f ? new Color(0.85f, 0.55f, 0.15f, 1) : new Color(0.25f, 0.65f, 0.2f, 1);
            if (share > 0) GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * share, bar.height), _white);
            GUI.color = new Color(0.55f, 0.9f, 0.35f, 1);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width, 1), _white);
            GUI.DrawTexture(new Rect(bar.x, bar.yMax - 1, bar.width, 1), _white);
            GUI.DrawTexture(new Rect(bar.x, bar.y, 1, bar.height), _white);
            GUI.DrawTexture(new Rect(bar.xMax - 1, bar.y, 1, bar.height), _white);
            GUI.color = was;
            GUI.Label(bar, max > 0 ? amount.ToString("N0") + " / " + max.ToString("N0") + "   " + (100 * share).ToString("0") + "%" : "none", _gaugeText);
            GUILayout.EndHorizontal();
        }

        private static GUIStyle _heading;

        private static GUIStyle Heading() => _heading ?? (_heading = new GUIStyle(GUI.skin.label) { richText = true });

        private static string Long(string res) => res == "LiquidFuel" ? "Liquid fuel" : res == "MonoPropellant" ? "Monopropellant" : res;

        private static string Short(string res) => res == "LiquidFuel" ? "LF" : res == "Oxidizer" ? "Ox" : res == "MonoPropellant" ? "Mono" : res;

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
