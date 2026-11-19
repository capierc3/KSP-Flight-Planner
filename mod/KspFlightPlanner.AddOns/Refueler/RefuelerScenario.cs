using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Refueler
{
    /// <summary>A fill or unload under way at one stack: which craft, which way, the Δv to keep, and the game time it last pumped to.</summary>
    internal sealed class PumpJob
    {
        internal uint StackId;
        internal string StackName = "";
        internal string Craft = "";
        internal bool Unload;
        internal double KeepDv;
        internal double LastUt;

        /// <summary>The pump switched the stack's SAS off (to be put back on when it ends).</summary>
        internal bool SasOff;
    }

    /// <summary>
    /// The Refueler's own part of the save: the player's marks on docked vessels, by name (true for a station tank, false
    /// for a craft of its own; vessels without a mark follow the rule), and the pump jobs under way. The pump runs here,
    /// not in a window, so it keeps going whatever you fly, in the tracking station and at the space centre, loaded or on
    /// rails, through time warp. docs/TRAFFIC-CONTROLLER.md, "Fuel transfer and RCS balance".
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.TRACKSTATION, GameScenes.SPACECENTER)]
    public sealed class RefuelerScenario : ScenarioModule
    {
        internal static RefuelerScenario Instance { get; private set; }

        internal readonly Dictionary<string, bool> StationMarks = new Dictionary<string, bool>();

        internal readonly List<PumpJob> Jobs = new List<PumpJob>();

        /// <summary>Stacks whose SAS the pump switched off and that weren't loaded when it ended: SAS goes back on when they next are.</summary>
        private readonly HashSet<uint> _sasToRestore = new HashSet<uint>();

        /// <summary>How each stack's last pumping ended ("Done: …", "Stopped: …"), for the windows. Not saved.</summary>
        private readonly Dictionary<uint, string> _notes = new Dictionary<uint, string>();

        /// <summary>The pump runs this often (real seconds); it moves fuel by game time, so how often doesn't change how much.</summary>
        private const float StepS = 0.25f;

        /// <summary>Unloading moves at most this much game time per check of the Δv to keep, so it can't run far past it.</summary>
        private const double UnloadSliceS = 60;

        private float _nextStep;

        public override void OnAwake() => Instance = this;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ---------------- Jobs ----------------

        internal PumpJob JobAt(uint stackId) => Jobs.FirstOrDefault(j => j.StackId == stackId);

        internal string NoteAt(uint stackId) => _notes.TryGetValue(stackId, out var note) ? note : null;

        /// <summary>Starts filling or unloading a craft docked at a stack (one job per stack: a running one is replaced).</summary>
        internal void Start(uint stackId, string stackName, string craft, bool unload, double keepDv)
        {
            Jobs.RemoveAll(j => j.StackId == stackId);
            _notes.Remove(stackId);
            Jobs.Add(new PumpJob { StackId = stackId, StackName = stackName ?? "", Craft = craft, Unload = unload, KeepDv = keepDv, LastUt = Planetarium.GetUniversalTime() });
            _nextStep = 0;
        }

        internal void Stop(uint stackId, string note)
        {
            var job = JobAt(stackId);
            if (job != null) End(job, note, false);
        }

        private void End(PumpJob job, string note, bool tell)
        {
            Jobs.Remove(job);
            _notes[job.StackId] = note;
            if (job.SasOff) RestoreSas(job.StackId);
            if (tell) ScreenMessages.PostScreenMessage("Refueler, " + job.StackName + ": " + note, 6f, ScreenMessageStyle.UPPER_CENTER);
        }

        // ---------------- The pump ----------------

        private void Update()
        {
            if (Time.realtimeSinceStartup < _nextStep) return;
            _nextStep = Time.realtimeSinceStartup + StepS;
            foreach (var job in Jobs.ToList())
            {
                try { Step(job); }
                catch (Exception e)
                {
                    Debug.LogWarning("[KspFlightPlanner] Refueler: " + e);
                    End(job, "Stopped: something went wrong (see the log).", true);
                }
            }
            if (_sasToRestore.Count > 0)
                foreach (var id in _sasToRestore.ToList())
                {
                    var v = Loaded(id);
                    if (v == null) continue;
                    _sasToRestore.Remove(id);
                    SetSas(v, true);
                }
        }

        /// <summary>
        /// One step of a job: the port's flow (kg/s) times the game time since the last step, between the craft's tanks and
        /// the station's, live parts when the stack is loaded and saved parts when it isn't. Ends the job when it's done,
        /// when unloading reaches the Δv to keep, or when the craft or stack is gone.
        /// </summary>
        private void Step(PumpJob job)
        {
            var pieces = FuelTransfer.Pieces(job.StackId, out var stackName);
            if (pieces.Count == 0)
            {
                End(job, "Stopped: the stack is gone.", true);
                return;
            }
            job.StackName = stackName ?? job.StackName;
            var piece = pieces.FirstOrDefault(p => p.Name == job.Craft && !p.IsStation);
            if (piece == null)
            {
                End(job, "Stopped: " + job.Craft + " isn't docked there any more.", true);
                return;
            }
            var station = FuelTransfer.StationTanks(pieces);
            if (station.Count == 0)
            {
                End(job, "Stopped: no station tanks are ticked.", true);
                return;
            }
            // SAS on while fuel moves shakes a big stack: off whenever the stack is loaded, back on when the pumping ends.
            if (Loaded(job.StackId) is Vessel live && live.ActionGroups[KSPActionGroup.SAS])
            {
                SetSas(live, false);
                job.SasOff = true;
            }

            var now = Planetarium.GetUniversalTime();
            var dt = now - job.LastUt;
            if (dt <= 0)
            {
                job.LastUt = now;
                return;
            }
            var kgs = FuelTransfer.PumpKgS(piece, out _);
            var craft = piece.Tanks();
            if (!job.Unload)
            {
                FuelTransfer.Pump(craft, station, false, kgs / 1000 * dt);
                job.LastUt = now;
            }
            else
            {
                // A slice at a time, the Δv checked before each; in a big time-warp step it catches up over the next steps.
                for (var i = 0; i < 50 && job.LastUt < now; i++)
                {
                    if (piece.DeltaV() <= job.KeepDv)
                    {
                        End(job, "Stopped: " + job.Craft + " keeps " + job.KeepDv.ToString("0") + " m/s.", true);
                        return;
                    }
                    var slice = Math.Min(UnloadSliceS, now - job.LastUt);
                    FuelTransfer.Pump(craft, station, true, kgs / 1000 * slice);
                    job.LastUt += slice;
                    FuelTransfer.Movable(craft, station, true, out var still);
                    if (still <= 1e-6) break;
                }
            }
            FuelTransfer.Movable(craft, station, job.Unload, out var left);
            if (left <= 1e-6)
                End(job, job.Unload ? "Done: " + job.Craft + " is empty or the station's tanks are full." : "Done: " + job.Craft + " is full or the station's tanks are empty.", true);
        }

        private static Vessel Loaded(uint id) => FlightGlobals.Vessels?.FirstOrDefault(v => v != null && v.persistentId == id && v.loaded);

        private void RestoreSas(uint stackId)
        {
            var v = Loaded(stackId);
            if (v != null) SetSas(v, true);
            else _sasToRestore.Add(stackId);
        }

        private static void SetSas(Vessel v, bool on)
        {
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, on);
            if (v == FlightGlobals.ActiveVessel)
                ScreenMessages.PostScreenMessage(on ? "Refueler: SAS back on" : "Refueler: SAS off while fuel is pumped", 4f, ScreenMessageStyle.UPPER_CENTER);
        }

        // ---------------- The save ----------------

        public override void OnSave(ConfigNode node)
        {
            foreach (var mark in StationMarks)
            {
                var n = node.AddNode("STATIONMARK");
                n.AddValue("vessel", mark.Key);
                n.AddValue("station", mark.Value);
            }
            foreach (var job in Jobs)
            {
                var n = node.AddNode("PUMP");
                n.AddValue("stack", job.StackId);
                n.AddValue("stackName", job.StackName);
                n.AddValue("craft", job.Craft);
                n.AddValue("unload", job.Unload);
                n.AddValue("keep", job.KeepDv.ToString("R", CultureInfo.InvariantCulture));
                n.AddValue("lastUt", job.LastUt.ToString("R", CultureInfo.InvariantCulture));
                n.AddValue("sasOff", job.SasOff);
            }
            foreach (var id in _sasToRestore) node.AddNode("SASRESTORE").AddValue("stack", id);
        }

        public override void OnLoad(ConfigNode node)
        {
            StationMarks.Clear();
            foreach (var n in node.GetNodes("STATIONMARK"))
                if (n.GetValue("vessel") is string name && bool.TryParse(n.GetValue("station"), out var station)) StationMarks[name] = station;
            Jobs.Clear();
            foreach (var n in node.GetNodes("PUMP"))
            {
                if (!uint.TryParse(n.GetValue("stack"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || string.IsNullOrEmpty(n.GetValue("craft"))) continue;
                bool.TryParse(n.GetValue("unload"), out var unload);
                bool.TryParse(n.GetValue("sasOff"), out var sasOff);
                double.TryParse(n.GetValue("keep"), NumberStyles.Float, CultureInfo.InvariantCulture, out var keep);
                if (!double.TryParse(n.GetValue("lastUt"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lastUt)) lastUt = Planetarium.GetUniversalTime();
                Jobs.Add(new PumpJob { StackId = id, StackName = n.GetValue("stackName") ?? "", Craft = n.GetValue("craft"), Unload = unload, KeepDv = keep, LastUt = lastUt, SasOff = sasOff });
            }
            _sasToRestore.Clear();
            foreach (var n in node.GetNodes("SASRESTORE"))
                if (uint.TryParse(n.GetValue("stack"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) _sasToRestore.Add(id);
        }
    }
}
