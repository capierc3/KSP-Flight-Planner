using System;
using System.Collections.Generic;
using System.Linq;
using KspFlightPlanner.KerbinResources;
using KspFlightPlanner.PowerHeat;

namespace KspFlightPlanner.Traffic
{
    /// <summary>One part of a station's stack, as the Station window shows it.</summary>
    internal sealed class StationPart
    {
        public uint Id;
        public uint ParentId;
        public string Title = "";

        /// <summary>The craft of the stack it came from (the station's own name for its own parts).</summary>
        public string Craft = "";

        public int Seats;
        public List<string> Crew = new List<string>();
        public bool Command;

        /// <summary>A research lab (ModuleScienceLab): where Science Officers and Survey Engineers sit.</summary>
        public bool Lab;

        public bool Port;
        public bool PortFree;
        public bool PortDocked;
        public string Access = ModuleTrafficPort.Any;

        public int FreeSeats => Math.Max(0, Seats - Crew.Count);
    }

    /// <summary>
    /// A station (or any craft) read for the Station window, loaded or not: its parts by craft, crew and seats, docking
    /// ports, and the main craft's supplies. Read every second or so, not every frame.
    /// </summary>
    internal sealed class StationView
    {
        public uint Id;
        public string Name = "";
        public string Body = "";
        public double ApA, PeA;
        public bool Landed;
        public bool Loaded;
        public bool IsStation;
        public VesselType Type;
        public string MainCraft = "";
        public readonly List<StationPart> Parts = new List<StationPart>();

        /// <summary>The main craft's resources: name → (amount, capacity).</summary>
        public readonly SortedDictionary<string, double[]> Supplies = new SortedDictionary<string, double[]>();

        /// <summary>The whole stack's power and heat parts (docked craft share one electrical system), or null if they couldn't be read.</summary>
        public PowerParts Power;

        /// <summary>Loaded craft in flight: the craft itself, for the Sun now and the measured flow.</summary>
        public Vessel Live;

        /// <summary>The crafts of the stack, the station's own first.</summary>
        internal IEnumerable<string> Crafts => Parts.Select(p => p.Craft).Distinct().OrderBy(c => c == MainCraft ? 0 : 1);

        internal StationPart PartOf(string kerbal) => Parts.FirstOrDefault(p => p.Crew.Contains(kerbal));

        internal bool HasPart(uint id) => Parts.Any(p => p.Id == id);

        /// <summary>The port of another craft docked to this port (its parent or child in the part tree), or null.</summary>
        internal StationPart OtherSide(StationPart port) =>
            Parts.FirstOrDefault(p => p.Port && p.Craft != port.Craft && (p.Id == port.ParentId || p.ParentId == port.Id));

        /// <summary>A craft found by persistent id, loaded or not: the game's craft list in flight and the tracking station, the save's records at the space centre.</summary>
        internal static StationView Find(uint id)
        {
            var live = Ships.Find(id);
            if (live != null) return Of(live);
            var pv = HighLogic.CurrentGame?.flightState?.protoVessels?.FirstOrDefault(p => p != null && p.persistentId == id);
            return pv == null ? null : Of(pv);
        }

        /// <summary>
        /// Every station: KSP's vessel type Station, a craft routes fly to, or a post someone is on duty at. Id and name,
        /// by name.
        /// </summary>
        internal static List<KeyValuePair<uint, string>> All()
        {
            var routes = TrafficScenario.Instance?.Routes ?? new List<Route>();
            var posts = KerbinResourcesScenario.Instance?.Duty.OnDuty.Where(s => !s.AtKsc).Select(s => s.PostId).ToList() ?? new List<uint>();
            bool Counts(uint id, string name, VesselType type) =>
                type == VesselType.Station || posts.Contains(id) || routes.Any(r => r.State != RouteState.Stopped && (r.TargetId == id || r.TargetName == name));
            var list = new List<KeyValuePair<uint, string>>();
            if (FlightGlobals.Vessels != null && FlightGlobals.Vessels.Count > 0)
            {
                foreach (var v in FlightGlobals.Vessels)
                    if (v != null && v.vesselType != VesselType.Flag && v.vesselType != VesselType.SpaceObject && !TrafficScenario.FliesRoute(v) && Counts(v.persistentId, v.vesselName, v.vesselType))
                        list.Add(new KeyValuePair<uint, string>(v.persistentId, v.vesselName));
            }
            else if (HighLogic.CurrentGame?.flightState?.protoVessels is List<ProtoVessel> saved)
            {
                foreach (var pv in saved)
                    if (pv != null && pv.vesselType != VesselType.Flag && pv.vesselType != VesselType.SpaceObject && Counts(pv.persistentId, pv.vesselName, pv.vesselType))
                        list.Add(new KeyValuePair<uint, string>(pv.persistentId, pv.vesselName));
            }
            return list.OrderBy(p => p.Value).ToList();
        }

        internal static StationView Of(Vessel v)
        {
            if (!v.loaded && v.protoVessel != null)
            {
                var saved = Of(v.protoVessel);
                saved.Body = v.mainBody?.bodyName ?? saved.Body;
                return saved;
            }
            var view = new StationView
            {
                Id = v.persistentId, Name = v.vesselName, Body = v.mainBody?.bodyName ?? "", Loaded = true, Type = v.vesselType,
                Landed = v.LandedOrSplashed, ApA = v.orbit?.ApA ?? 0, PeA = v.orbit?.PeA ?? 0,
            };
            var map = CraftMap.Of(v);
            view.MainCraft = map.Main;
            foreach (var p in v.parts)
            {
                var dock = p.FindModuleImplementing<ModuleDockingNode>();
                var state = dock == null ? "" : dock.fsm != null ? dock.fsm.currentStateName ?? "" : dock.state ?? "";
                var part = new StationPart
                {
                    Id = p.persistentId, ParentId = p.parent != null ? p.parent.persistentId : 0, Title = p.partInfo?.title ?? p.name,
                    Craft = map.CraftOfPart(p.persistentId) ?? map.Main, Seats = p.CrewCapacity,
                    Crew = p.protoModuleCrew.Select(c => c.name).ToList(), Command = p.FindModuleImplementing<ModuleCommand>() != null,
                    Lab = p.Modules.Cast<PartModule>().Any(m => m.moduleName == "ModuleScienceLab"),
                    Port = dock != null, PortFree = Ships.IsFree(dock),
                    PortDocked = dock != null && (dock.otherNode != null || state.StartsWith("Docked", StringComparison.Ordinal) || state.StartsWith("PreAttached", StringComparison.Ordinal)),
                    Access = p.FindModuleImplementing<ModuleTrafficPort>()?.access ?? ModuleTrafficPort.Any,
                };
                view.Parts.Add(part);
                if (part.Craft == view.MainCraft)
                    foreach (var r in p.Resources) view.Add(r.resourceName, r.amount, r.maxAmount);
            }
            view.Power = Safe(() => PowerReader.Of(v.parts, true));
            view.Live = v;
            view.Finish();
            return view;
        }

        private static PowerParts Safe(Func<PowerParts> read)
        {
            try { return read(); }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[KspFlightPlanner] Station window power: " + e);
                return null;
            }
        }

        internal static StationView Of(ProtoVessel pv)
        {
            var bodies = FlightGlobals.Bodies;
            var bodyIndex = pv.orbitSnapShot?.ReferenceBodyIndex ?? -1;
            var view = new StationView
            {
                Id = pv.persistentId, Name = pv.vesselName, Type = pv.vesselType, Landed = pv.landed || pv.splashed,
                Body = bodies != null && bodyIndex >= 0 && bodyIndex < bodies.Count ? bodies[bodyIndex].bodyName : "",
            };
            try
            {
                var orbit = pv.orbitSnapShot?.Load();
                if (orbit != null)
                {
                    view.ApA = orbit.ApA;
                    view.PeA = orbit.PeA;
                }
            }
            catch (Exception) { }
            var map = CraftMap.Of(pv);
            view.MainCraft = map.Main;
            var snaps = pv.protoPartSnapshots;
            for (var i = 0; i < snaps.Count; i++)
            {
                var pp = snaps[i];
                var dock = pp.modules.FirstOrDefault(m => m.moduleName == "ModuleDockingNode")?.moduleValues;
                var state = dock?.GetValue("state") ?? "";
                var access = pp.modules.FirstOrDefault(m => m.moduleName == nameof(ModuleTrafficPort))?.moduleValues?.GetValue("access");
                var part = new StationPart
                {
                    Id = pp.persistentId,
                    ParentId = pp.parentIdx >= 0 && pp.parentIdx < snaps.Count && pp.parentIdx != i ? snaps[pp.parentIdx].persistentId : 0,
                    Title = CrewMove.Title(pp), Craft = map.CraftOfPart(pp.persistentId) ?? map.Main,
                    Seats = pp.partInfo?.partPrefab != null ? pp.partInfo.partPrefab.CrewCapacity : 0,
                    Crew = pp.protoModuleCrew.Select(c => c.name).ToList(), Command = pp.modules.Any(m => m.moduleName == "ModuleCommand"),
                    Lab = pp.modules.Any(m => m.moduleName == "ModuleScienceLab"),
                    Port = dock != null, PortFree = state == "Ready",
                    PortDocked = state.StartsWith("Docked", StringComparison.Ordinal) || state.StartsWith("PreAttached", StringComparison.Ordinal),
                    Access = string.IsNullOrEmpty(access) ? ModuleTrafficPort.Any : access,
                };
                view.Parts.Add(part);
                if (part.Craft == view.MainCraft)
                    foreach (var r in pp.resources) view.Add(r.resourceName, r.amount, r.maxAmount);
            }
            view.Power = Safe(() => PowerReader.Of(pv));
            view.Finish();
            return view;
        }

        private void Add(string resource, double amount, double max)
        {
            if (max <= 0) return;
            if (!Supplies.TryGetValue(resource, out var had)) Supplies[resource] = had = new double[2];
            had[0] += amount;
            had[1] += max;
        }

        private void Finish()
        {
            var routes = TrafficScenario.Instance?.Routes;
            IsStation = Type == VesselType.Station || routes != null && routes.Any(r => r.State != RouteState.Stopped && (r.TargetId == Id || r.TargetName == Name));
        }

        /// <summary>
        /// Sets a station port's route-craft access (the Traffic setting on the port's right-click menu), on the loaded part
        /// or the saved one. Saved at the space centre, where the save file is what the next scene loads from.
        /// </summary>
        internal static bool SetAccess(uint vesselId, uint partId, string access)
        {
            var live = Ships.Find(vesselId);
            if (live != null && live.loaded)
            {
                var m = live.parts.FirstOrDefault(p => p.persistentId == partId)?.FindModuleImplementing<ModuleTrafficPort>();
                if (m == null) return false;
                m.access = access;
                return true;
            }
            var pv = live?.protoVessel ?? HighLogic.CurrentGame?.flightState?.protoVessels?.FirstOrDefault(p => p != null && p.persistentId == vesselId);
            var values = pv?.protoPartSnapshots.FirstOrDefault(pp => pp.persistentId == partId)?.modules
                .FirstOrDefault(m => m.moduleName == nameof(ModuleTrafficPort))?.moduleValues;
            if (values == null) return false;
            values.SetValue("access", access, true);
            if (!HighLogic.LoadedSceneIsFlight)
            {
                try { GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE); }
                catch (Exception) { }
            }
            return true;
        }
    }
}
