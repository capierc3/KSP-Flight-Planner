using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Refueler
{
    /// <summary>
    /// The Refueler's controls for one stack, loaded or not, drawn inside a window: each docked vessel with a tick for
    /// "station tank", then one craft's tanks against the station's, its Δv, the pump, and Start/Stop. The pumping itself
    /// is a job in <see cref="RefuelerScenario"/>, so it carries on when the window closes. Drawn by the Refueler's own
    /// flight window and by the Station window (flight, tracking station, space centre).
    /// docs/TRAFFIC-CONTROLLER.md, "Fuel transfer and RCS balance".
    /// </summary>
    internal sealed class RefuelerView : IDisposable
    {
        // Which craft (by name), which way, and the Δv to keep when unloading, as picked before Start.
        private string _craft;
        private bool _unload;
        private string _keepText = "300";

        // The stack read a few times a second, on layout events only, so a frame's layout and repaint draw the same rows.
        private uint _stackId;
        private string _stackName;
        private List<StackPiece> _pieces = new List<StackPiece>();
        private float _nextRead;

        private Texture2D _white;
        private GUIStyle _gaugeText;

        public void Dispose()
        {
            if (_white != null) UnityEngine.Object.Destroy(_white);
            _white = null;
        }

        /// <summary>The stack's pieces (read fresh at most every quarter second), to tell whether there's anything docked.</summary>
        internal List<StackPiece> Pieces(uint stackId)
        {
            if (Event.current == null || Event.current.type == EventType.Layout)
                if (stackId != _stackId || Time.realtimeSinceStartup >= _nextRead)
                {
                    _stackId = stackId;
                    _nextRead = Time.realtimeSinceStartup + 0.25f;
                    _pieces = stackId == 0 ? new List<StackPiece>() : FuelTransfer.Pieces(stackId, out _stackName);
                }
            return _pieces;
        }

        /// <summary>The controls for a stack.</summary>
        internal void Draw(uint stackId)
        {
            var pieces = Pieces(stackId);
            if (pieces.Count < 2)
            {
                GUILayout.Label(stackId == 0 ? "No vessel." : "Nothing is docked to " + (_stackName ?? "it") + ".");
                return;
            }
            var job = RefuelerScenario.Instance?.JobAt(stackId);
            DrawPieces(pieces, job != null);
            DrawTransfer(stackId, pieces, job);
        }

        /// <summary>
        /// Every vessel in the stack, with a tick for "station tank": fuel moves to and from the ticked ones only. Unticked
        /// ones are craft of their own, to fill or unload.
        /// </summary>
        private static void DrawPieces(List<StackPiece> pieces, bool running)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("Station tanks (tick the parts whose fuel is the station's)");
            foreach (var piece in pieces)
            {
                GUI.enabled = !running;
                var station = piece.IsStation;
                var on = GUILayout.Toggle(station, " " + piece.Name + "  (" + piece.Type.ToString().ToLowerInvariant() + ")" + FuelSummary(piece.Tanks()));
                if (on != station) FuelTransfer.SetStationMark(piece, on);
                GUI.enabled = true;
            }
            GUILayout.EndVertical();
        }

        /// <summary>Fill or unload: one craft, its tanks against the station's, its Δv, and the transfer.</summary>
        private void DrawTransfer(uint stackId, List<StackPiece> pieces, PumpJob job)
        {
            var scenario = RefuelerScenario.Instance;
            var crafts = pieces.Where(p => !p.IsStation).ToList();
            GUILayout.BeginVertical(GUI.skin.box);
            if (crafts.Count == 0)
            {
                GUILayout.Label("No craft to fill: untick a vessel above to treat it as a craft of its own.");
                GUILayout.EndVertical();
                return;
            }
            var moving = job != null;
            if (moving) _craft = job.Craft;
            else if (crafts.All(c => c.Name != _craft)) _craft = crafts[0].Name;
            var piece = crafts.FirstOrDefault(c => c.Name == _craft) ?? crafts[0];
            if (crafts.Count > 1 && !moving)
            {
                GUILayout.Label("Craft:");
                foreach (var c in crafts)
                    if (GUILayout.Toggle(_craft == c.Name, c.Name, GUI.skin.button)) _craft = c.Name;
            }
            var unload = moving ? job.Unload : _unload;
            var name = piece.Name;
            var craft = piece.Tanks();
            var station = FuelTransfer.StationTanks(pieces);
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
            GUILayout.Label("<b>" + name + " Δv:</b> " + piece.DeltaV().ToString("N0") + " m/s", Heading());
            // The pump: its flow through the craft's port, and how long what's left takes at that rate (game time).
            var kgs = FuelTransfer.PumpKgS(piece, out var through);
            FuelTransfer.Movable(craft, station, unload, out var movable);
            GUILayout.Label("<b>Pump:</b> " + kgs.ToString("0.#") + " kg/s through " + through +
                            (movable > 1e-6 && kgs > 0 ? "; " + (movable * 1000).ToString("N0") + " kg to " + (unload ? "unload" : "fill") + ", " +
                                                         KSPUtil.PrintTimeCompact(movable * 1000 / kgs, false) + (moving ? " left" : " at this rate") : ""), RichWrap());
            if (!moving)
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
                else if (scenario == null) GUILayout.Label("The Refueler isn't running in this scene.");
                else if (GUILayout.Button("Start"))
                {
                    double.TryParse(_keepText, out var keep);
                    scenario.Start(stackId, _stackName, name, _unload, _unload ? Math.Max(0, keep) : 0);
                }
            }
            else
            {
                GUILayout.Label(unload ? "Unloading " + name + " to the station, keeping " + job.KeepDv.ToString("0") + " m/s. It carries on with this window closed, on rails too." : "Filling " + name + ". It carries on with this window closed, on rails too.", Wrap());
                if (GUILayout.Button("Stop")) scenario?.Stop(stackId, "Stopped.");
            }
            var note = moving ? null : scenario?.NoteAt(stackId);
            if (note != null) GUILayout.Label(note, Wrap());
            GUILayout.EndVertical();
        }

        /// <summary>A short line of what these tanks hold: "  · LF 1,200/2,880 · Mono 40/750"; empty when they hold none.</summary>
        private static string FuelSummary(IList<FuelTank> tanks)
        {
            var bits = new List<string>();
            foreach (var res in FuelTransfer.Resources)
            {
                FuelTransfer.Totals(tanks, res, out var a, out var am);
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

        private static GUIStyle _heading, _richWrap, _wrap;

        private static GUIStyle Heading() => _heading ?? (_heading = new GUIStyle(GUI.skin.label) { richText = true });

        private static GUIStyle RichWrap() => _richWrap ?? (_richWrap = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });

        private static GUIStyle Wrap() => _wrap ?? (_wrap = new GUIStyle(GUI.skin.label) { wordWrap = true });

        private static string Long(string res) => res == "LiquidFuel" ? "Liquid fuel" : res == "MonoPropellant" ? "Monopropellant" : res;

        private static string Short(string res) => res == "LiquidFuel" ? "LF" : res == "Oxidizer" ? "Ox" : res == "MonoPropellant" ? "Mono" : res;
    }
}
