using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace KspFlightPlanner.PowerHeat
{
    /// <summary>
    /// The Power &amp; Heat window in the VAB and SPH (docs/POWER-AND-HEAT.md): the craft being built, set down on a body's
    /// surface or in orbit round it. It shows what the panels, generators and fuel cells make there, what the loads use,
    /// whether the batteries last the night and the day refills them, and whether the radiators keep up with the drills'
    /// and converters' core heat. Every part can be switched off for a what-if (drills off at night, lab only…).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public sealed class PowerHeatWindow : MonoBehaviour
    {
        private const string LockId = "KspFlightPlannerPowerHeat";

        // Kept for the session, so the place stays picked between editor visits.
        private static string _bodyName = "Minmus";
        private static bool _orbit;
        private static string _altitudeKm = "30";

        private ApplicationLauncherButton _button;
        private Texture2D _icon;
        private bool _open;
        private Rect _window = new Rect(Screen.width - 560, 90, 520, 600);
        private bool _resizing;
        private Vector2 _scroll;
        private float _nextLook;
        private bool _showParts = true;
        private bool _mouseOver;

        private PowerParts _parts;
        private PowerBudget _budget;

        /// <summary>The station split at its docking-port pairs, each with its own sums; empty for a craft of one module.</summary>
        private readonly List<KeyValuePair<StationModule, PowerParts>> _modules = new List<KeyValuePair<StationModule, PowerParts>>();
        private readonly List<PowerBudget> _moduleBudgets = new List<PowerBudget>();
        private PowerBudget _wholeBudget;
        private PowerParts _wholeParts;
        private int _ports;

        /// <summary>The module shown in detail (its first part, so the pick survives edits), or null for the whole station.</summary>
        private Part _picked;

        /// <summary>The what-if switches: item (or radiator) key → on. Missing = on.</summary>
        private readonly Dictionary<string, bool> _off = new Dictionary<string, bool>();

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
            InputLockManager.RemoveControlLock(LockId);
        }

        private void AddButton()
        {
            if (_button != null || ApplicationLauncher.Instance == null) return;
            _button = ApplicationLauncher.Instance.AddModApplication(() => { _open = true; _nextLook = 0; }, Close, null, null, null, null,
                ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.SPH, _icon);
        }

        private void Close()
        {
            _open = false;
            InputLockManager.RemoveControlLock(LockId);
        }

        private void Update()
        {
            // Clicks on the window don't pick up parts behind it.
            if (_open && _mouseOver)
                InputLockManager.SetControlLock(ControlTypes.EDITOR_LOCK, LockId);
            else InputLockManager.RemoveControlLock(LockId);
        }

        private CelestialBody Body => FlightGlobals.Bodies?.FirstOrDefault(b => b.bodyName == _bodyName) ?? FlightGlobals.GetHomeBody();

        private bool On(PowerItem i) => !_off.TryGetValue(i.Key, out var off) || !off;

        private bool On(string key) => !_off.TryGetValue(key, out var off) || !off;

        private bool On(Radiator r) => On(r.Key);

        private void Toggle(string key) => _off[key] = On(key);

        /// <summary>Parts with several recipes already given their one-recipe default (so a later "Off" sticks).</summary>
        private readonly HashSet<string> _grouped = new HashSet<string>();

        /// <summary>
        /// A part with several recipes runs one at a time in the plan: the first is on when the part first shows up, the
        /// rest off. (KSP itself lets them all run at once; picking that stays possible by building the plan that way.)
        /// </summary>
        private void DefaultRecipes(PowerParts parts)
        {
            foreach (var g in parts.Items.Where(i => i.Group.Length > 0).GroupBy(i => i.Group))
            {
                if (!_grouped.Add(g.Key)) continue;
                var first = true;
                foreach (var i in g)
                {
                    if (!first) _off[i.Key] = true;
                    first = false;
                }
            }
        }

        /// <summary>The recipe switch: the next recipe, Off after the last, then the first again.</summary>
        private void Cycle(IList<PowerItem> items)
        {
            var running = -1;
            for (var i = 0; i < items.Count; i++)
                if (On(items[i])) { running = i; break; }
            var next = running + 1; // −1 (off) → 0; last → items.Count (off)
            for (var i = 0; i < items.Count; i++) _off[items[i].Key] = i != next;
            _nextLook = 0;
        }

        /// <summary>Reads the craft again (twice a second) and adds it up for the picked place.</summary>
        private void Look()
        {
            _nextLook = Time.realtimeSinceStartup + 0.5f;
            try
            {
                var ship = EditorLogic.fetch?.ship;
                var body = Body;
                _modules.Clear();
                _moduleBudgets.Clear();
                if (ship == null || ship.parts.Count == 0 || body == null)
                {
                    _parts = null;
                    _budget = null;
                    return;
                }
                var whole = _wholeParts = PowerReader.Of(ship.parts, false);
                DefaultRecipes(whole);
                var split = ModuleSplit.Split(ship.parts);
                _ports = ship.parts.Count(ModuleSplit.IsPort);
                if (split.Count > 1)
                    foreach (var m in split) _modules.Add(new KeyValuePair<StationModule, PowerParts>(m, PowerReader.Of(m.Parts, false)));
                double night, day;
                if (_orbit)
                {
                    var km = double.TryParse(_altitudeKm, NumberStyles.Float, CultureInfo.InvariantCulture, out var k) ? k : 0;
                    night = Sunlight.OrbitNight(body, km * 1000, out var period);
                    day = period - night;
                }
                else
                {
                    night = Sunlight.SurfaceNight(body);
                    day = Sunlight.SurfaceDay(body);
                }
                var distance = Sunlight.SunDistance(body);
                _wholeBudget = PowerBudget.Of(whole, distance, night, day, On, On, On);
                foreach (var m in _modules) _moduleBudgets.Add(PowerBudget.Of(m.Value, distance, night, day, On, On, On));
                var picked = _modules.FindIndex(m => _picked != null && m.Key.Parts.Contains(_picked));
                if (picked < 0) _picked = null;
                _parts = picked >= 0 ? _modules[picked].Value : whole;
                _budget = picked >= 0 ? _moduleBudgets[picked] : _wholeBudget;
            }
            catch (Exception e)
            {
                _budget = null;
                Debug.LogWarning("[KspFlightPlanner] Power & Heat: " + e);
            }
        }

        private void OnGUI()
        {
            if (!_open) return;
            if (Event.current.type == EventType.Repaint) _mouseOver = _window.Contains(Event.current.mousePosition);
            if (Time.realtimeSinceStartup >= _nextLook) Look();
            GUI.skin = null;
            PanelWindow.FollowResize(ref _window, ref _resizing);
            _window = GUILayout.Window(GetInstanceID(), _window, Draw, "Power & Heat", PanelWindow.Dark(), GUILayout.Width(_window.width), GUILayout.Height(_window.height));
        }

        private void Draw(int id)
        {
            DrawPlace();
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (_parts == null || _budget == null) GUILayout.Label("No craft yet.");
            else
            {
                DrawModules();
                var shown = _picked == null ? (_modules.Count > 0 ? "Whole station" : "") : _modules.First(m => m.Key.Parts.Contains(_picked)).Key.Name;
                GUILayout.Label("<b>Power</b>" + (shown.Length > 0 ? ": " + shown : ""), PowerView.RichWrap());
                var where = (_orbit ? "in a " + _altitudeKm + " km orbit round " : "on ") + Body.bodyName + (_orbit ? " (its longest)" : " (at the equator)");
                PowerView.DrawPower(_budget, _parts, where, null, On, i => { Toggle(i.Key); _nextLook = 0; }, ref _showParts, Cycle);
                GUILayout.Space(6);
                GUILayout.Label("<b>Heat</b>" + (shown.Length > 0 ? ": " + shown : ""), PowerView.RichWrap());
                PowerView.DrawHeat(_budget, _parts, On, On, r => { Toggle(r.Key); _nextLook = 0; });
                GUILayout.Space(6);
                GUILayout.Label("Panels are taken as deployed and every part as switched on, with one recipe per ISRU: click a part's switch to try it another way. Antennas (only while sending) and reaction wheels (only while turning) aren't counted.", PowerView.Wrap());
            }
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (_off.Any(o => o.Value) && GUILayout.Button("Reset switches"))
            {
                _off.Clear();
                _grouped.Clear();
                _nextLook = 0;
            }
            if (GUILayout.Button("Close"))
            {
                if (_button != null) _button.SetFalse();
                Close();
            }
            GUILayout.EndHorizontal();
            PanelWindow.Grip(_window, ref _resizing);
            GUI.DragWindow();
        }

        /// <summary>
        /// The modules the station will be launched as, split at its docking-port pairs: a line each with whether it lasts
        /// the night on its own (before it's docked), and a button to show it in detail below.
        /// </summary>
        private void DrawModules()
        {
            if (_modules.Count == 0)
            {
                GUILayout.Label("<b>Modules</b>: just one. " + (_ports == 0
                    ? "There are no docking ports on this craft. Join modules with a pair of ports, port to port, and the station splits between them."
                    : "It has " + _ports + " docking port" + (_ports == 1 ? "" : "s") + ", but no two attached port to port. Join modules with a pair of ports and the station splits between them."), PowerView.RichWrap());
                GUILayout.Space(6);
                return;
            }
            GUILayout.Label("<b>Modules</b> (split where two docking ports meet)", PowerView.RichWrap());
            Row("Whole station", _picked == null, _wholeBudget, null, () => _picked = null);
            for (var i = 0; i < _modules.Count; i++)
            {
                var m = _modules[i];
                var first = m.Key.Parts[0];
                Row(m.Key.Name, _picked == first, _moduleBudgets[i], m, () => _picked = first);
            }
            GUILayout.Space(6);
        }

        private void Row(string name, bool picked, PowerBudget b, KeyValuePair<StationModule, PowerParts>? m, Action pick)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(picked, " " + name, GUI.skin.button, GUILayout.Width(150)) && !picked)
            {
                pick();
                _nextLook = 0;
            }
            var text = m.HasValue ? m.Value.Key.Parts.Count + " parts; " + PowerView.Summary(b, m.Value.Value) : PowerView.Summary(b, _wholeParts);
            GUILayout.Label(text, PowerView.RichWrap());
            GUILayout.EndHorizontal();
        }

        /// <summary>Where the craft is going: a body (◀ ▶ to step through them), and its surface or an orbit at some altitude.</summary>
        private void DrawPlace()
        {
            var bodies = FlightGlobals.Bodies?.Where(b => b != Sunlight.Sun).ToList() ?? new List<CelestialBody>();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("◀", GUILayout.Width(28)) && bodies.Count > 0) Step(bodies, -1);
            GUILayout.Label("<b>" + (Body?.bodyName ?? "?") + "</b>", new GUIStyle(GUI.skin.label) { richText = true, alignment = TextAnchor.MiddleCenter }, GUILayout.Width(90));
            if (GUILayout.Button("▶", GUILayout.Width(28)) && bodies.Count > 0) Step(bodies, 1);
            GUILayout.Space(10);
            if (GUILayout.Toggle(!_orbit, " Surface", GUI.skin.button, GUILayout.Width(80)) && _orbit) { _orbit = false; _nextLook = 0; }
            if (GUILayout.Toggle(_orbit, " Orbit", GUI.skin.button, GUILayout.Width(70)) && !_orbit) { _orbit = true; _nextLook = 0; }
            if (_orbit)
            {
                var km = GUILayout.TextField(_altitudeKm, GUILayout.Width(60));
                if (km != _altitudeKm) { _altitudeKm = km; _nextLook = 0; }
                GUILayout.Label("km");
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void Step(List<CelestialBody> bodies, int by)
        {
            var i = bodies.FindIndex(b => b.bodyName == _bodyName);
            _bodyName = bodies[((i < 0 ? 0 : i) + by + bodies.Count) % bodies.Count].bodyName;
            _nextLook = 0;
        }

        /// <summary>A plain toolbar icon: a lightning bolt, in KSP's blue.</summary>
        private static Texture2D MakeIcon()
        {
            const int size = 38;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var clear = new Color(0, 0, 0, 0);
            var blue = new Color(0.45f, 0.75f, 1f, 1);
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                // Upper half leans right, lower half leans left: a zigzag bolt about 6 px wide.
                var top = y >= 19 && y <= 34 && Math.Abs(x - (14 + (y - 19) * 0.5)) <= 3;
                var bottom = y >= 4 && y <= 19 && Math.Abs(x - (16 + (y - 4) * 0.5)) <= 3;
                var bar = Math.Abs(y - 19) <= 2 && x >= 12 && x <= 26;
                tex.SetPixel(x, y, top || bottom || bar ? blue : clear);
            }
            tex.Apply();
            return tex;
        }
    }
}
