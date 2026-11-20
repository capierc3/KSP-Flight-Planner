using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KspFlightPlanner.Traffic;

namespace KspFlightPlanner.Refueler
{
    /// <summary>One tank's amount of one resource: on a loaded part, or in an unloaded stack's saved part.</summary>
    internal sealed class FuelTank
    {
        internal PartResource Live;
        internal ProtoPartResourceSnapshot Saved;

        internal string Name => Live != null ? Live.resourceName : Saved.resourceName;
        internal bool Flow => Live != null ? Live.flowState : Saved.flowState;
        internal double Max => Live != null ? Live.maxAmount : Saved.maxAmount;

        internal double Amount
        {
            get => Live != null ? Live.amount : Saved.amount;
            set
            {
                if (Live != null) Live.amount = value;
                else Saved.amount = value;
            }
        }
    }

    /// <summary>
    /// One vessel that docked into a stack: the parts it came with (docked craft hanging off it are pieces of their
    /// own), its name and type from its docking port, and whether it's one of the station's tanks or a craft of its own.
    /// Read from a loaded stack's parts or an unloaded one's saved parts; one or the other list is filled.
    /// </summary>
    internal sealed class StackPiece
    {
        internal string Name;
        internal VesselType Type;
        internal bool IsRoot;

        /// <summary>A loaded stack's parts of this piece.</summary>
        internal readonly List<Part> Parts = new List<Part>();

        /// <summary>An unloaded stack's saved parts of this piece.</summary>
        internal readonly List<ProtoPartSnapshot> Saved = new List<ProtoPartSnapshot>();

        /// <summary>The docking port this piece joined the stack by (its own side): its node type and part title. Null for the root piece.</summary>
        internal string PortType, PortTitle;

        internal bool HasCommand, HasEngine;

        /// <summary>The planner's rule (DockedPiece.IsCraftByDefault): a ship, lander, rover or plane with a command part and an engine.</summary>
        internal bool IsCraftByDefault =>
            (Type == VesselType.Ship || Type == VesselType.Lander || Type == VesselType.Rover || Type == VesselType.Plane) && HasCommand && HasEngine;

        /// <summary>Part of the station (its fuel is the station's), by the player's mark when there is one, else the rule.</summary>
        internal bool IsStation => FuelTransfer.StationMark(Name) ?? !IsCraftByDefault;

        /// <summary>Its unlocked tanks, every resource. Looked up again each time: KSP rebuilds saved parts when a stack loads or unloads.</summary>
        internal List<FuelTank> Tanks()
        {
            var tanks = new List<FuelTank>();
            foreach (var p in Parts)
                foreach (var r in p.Resources)
                    if (r.flowState) tanks.Add(new FuelTank { Live = r });
            foreach (var pp in Saved)
                foreach (var r in pp.resources)
                    if (r.flowState) tanks.Add(new FuelTank { Saved = r });
            return tanks;
        }

        /// <summary>The Δv it can still make on its own, from its own parts (Ships.DeltaVLeft, docs/MATH.md §10.2).</summary>
        internal double DeltaV() => Parts.Count > 0 ? Ships.DeltaVLeft(Parts, out _) : Ships.DeltaVLeft(Saved, out _);
    }

    /// <summary>
    /// Moving fuel between a docked craft and the station it's docked at, either way: the Refueler. The station's fuel is
    /// the pieces of the stack marked as station parts (its tanks, its core), never the other craft docked there. Liquid
    /// fuel, oxidizer and monopropellant, taken from every unlocked tank in proportion to what each holds and put into
    /// every unlocked tank in proportion to its free room. A real transfer: nothing is made or lost. Works on a loaded
    /// stack's parts and on an unloaded one's saved parts alike. docs/TRAFFIC-CONTROLLER.md, "Fuel transfer and RCS balance".
    /// </summary>
    internal static class FuelTransfer
    {
        internal static readonly string[] Resources = { "LiquidFuel", "Oxidizer", "MonoPropellant" };

        /// <summary>Marks kept when there's no scenario (outside a save's scenes); normally the scenario keeps them in the save.</summary>
        private static readonly Dictionary<string, bool> Fallback = new Dictionary<string, bool>();

        private static Dictionary<string, bool> Marks => RefuelerScenario.Instance != null ? RefuelerScenario.Instance.StationMarks : Fallback;

        /// <summary>The player's mark for a docked vessel, by name: true for a station part, false for a craft; null for the rule.</summary>
        internal static bool? StationMark(string name) => name != null && Marks.TryGetValue(name, out var station) ? station : (bool?)null;

        /// <summary>Marks a docked vessel as a station part or a craft; a mark that agrees with the rule is dropped.</summary>
        internal static void SetStationMark(StackPiece piece, bool station)
        {
            if (station == !piece.IsCraftByDefault) Marks.Remove(piece.Name);
            else Marks[piece.Name] = station;
        }

        // ---------------- Finding a stack and its pieces ----------------

        /// <summary>
        /// A stack's pieces by its persistent id, loaded or not: the game's vessels in flight and the tracking station, the
        /// save's records at the space centre. Empty when there's no such stack.
        /// </summary>
        internal static List<StackPiece> Pieces(uint stackId, out string stackName)
        {
            stackName = null;
            var v = FlightGlobals.Vessels?.FirstOrDefault(x => x != null && x.persistentId == stackId);
            if (v != null)
            {
                stackName = v.vesselName;
                return Pieces(v);
            }
            var pv = HighLogic.CurrentGame?.flightState?.protoVessels?.FirstOrDefault(p => p != null && p.persistentId == stackId);
            if (pv == null) return new List<StackPiece>();
            stackName = pv.vesselName;
            return Pieces(pv);
        }

        /// <summary>The stack's pieces, from its parts when it's loaded and its saved parts when it isn't.</summary>
        internal static List<StackPiece> Pieces(Vessel stack)
        {
            if (stack == null) return new List<StackPiece>();
            if (stack.loaded) return LoadedPieces(stack);
            return stack.protoVessel != null ? Pieces(stack.protoVessel) : new List<StackPiece>();
        }

        /// <summary>
        /// A loaded stack split back into the vessels that docked to make it, the way the planner does
        /// (DockedCraft.Pieces): the part tree cut at every docked pair of ports. The piece with the root part comes
        /// first and carries the stack's name; the others are named by their own port.
        /// </summary>
        private static List<StackPiece> LoadedPieces(Vessel stack)
        {
            var pieces = new List<StackPiece>();
            if (stack.rootPart == null) return pieces;
            var root = new StackPiece { Name = stack.vesselName, Type = stack.vesselType, IsRoot = true };
            pieces.Add(root);
            var todo = new Stack<KeyValuePair<Part, StackPiece>>();
            todo.Push(new KeyValuePair<Part, StackPiece>(stack.rootPart, root));
            while (todo.Count > 0)
            {
                var next = todo.Pop();
                var part = next.Key;
                var piece = next.Value;
                piece.Parts.Add(part);
                if (part.FindModuleImplementing<ModuleCommand>() != null) piece.HasCommand = true;
                if (part.FindModuleImplementing<ModuleEngines>() != null) piece.HasEngine = true;
                foreach (var child in part.children)
                {
                    var into = piece;
                    if (DockedJoin(child, part) is ModuleDockingNode port)
                    {
                        var info = port.vesselInfo;
                        into = new StackPiece
                        {
                            Name = info != null && !string.IsNullOrEmpty(info.name) ? info.name : "Docked part",
                            Type = info != null ? info.vesselType : VesselType.Unknown,
                            PortType = port.nodeType, PortTitle = port.part?.partInfo?.title,
                        };
                        pieces.Add(into);
                    }
                    todo.Push(new KeyValuePair<Part, StackPiece>(child, into));
                }
            }
            return pieces;
        }

        /// <summary>
        /// When a part joins its parent by docking, the child side's port (its record is the vessel it came with); null
        /// for an ordinary join, a port fitted in the editor included.
        /// </summary>
        private static ModuleDockingNode DockedJoin(Part child, Part parent)
        {
            foreach (var node in child.FindModulesImplementing<ModuleDockingNode>())
                if (node.otherNode != null && node.otherNode.part == parent && Docked(node.state)) return node;
            foreach (var node in parent.FindModulesImplementing<ModuleDockingNode>())
                if (node.otherNode != null && node.otherNode.part == child && Docked(node.state))
                    return node.otherNode;
            return null;
        }

        private static bool Docked(string state) => state != null && state.StartsWith("Docked", StringComparison.Ordinal);

        /// <summary>
        /// An unloaded stack split the same way, from its saved parts: a part joins its parent by docking when its own
        /// port is docked and either port's record names the other part (dockUId). The child side's record
        /// (DOCKEDVESSEL) names the vessel it came with.
        /// </summary>
        internal static List<StackPiece> Pieces(ProtoVessel pv)
        {
            var pieces = new List<StackPiece>();
            var snaps = pv?.protoPartSnapshots;
            if (snaps == null || snaps.Count == 0) return pieces;
            var children = new List<int>[snaps.Count];
            var rootIndex = -1;
            for (var i = 0; i < snaps.Count; i++)
            {
                var parent = snaps[i].parentIdx;
                if (parent < 0 || parent >= snaps.Count || parent == i)
                {
                    if (rootIndex < 0) rootIndex = i;
                    continue;
                }
                (children[parent] ?? (children[parent] = new List<int>())).Add(i);
            }
            if (pv.rootIndex >= 0 && pv.rootIndex < snaps.Count) rootIndex = pv.rootIndex;
            if (rootIndex < 0) return pieces;
            var root = new StackPiece { Name = pv.vesselName, Type = pv.vesselType, IsRoot = true };
            pieces.Add(root);
            var todo = new Stack<KeyValuePair<int, StackPiece>>();
            todo.Push(new KeyValuePair<int, StackPiece>(rootIndex, root));
            var seen = new HashSet<int>();
            while (todo.Count > 0)
            {
                var next = todo.Pop();
                if (!seen.Add(next.Key)) continue;
                var pp = snaps[next.Key];
                var piece = next.Value;
                piece.Saved.Add(pp);
                if (pp.modules.Any(m => m.moduleName == "ModuleCommand")) piece.HasCommand = true;
                if (pp.partInfo?.partPrefab != null && pp.partInfo.partPrefab.FindModuleImplementing<ModuleEngines>() != null) piece.HasEngine = true;
                if (children[next.Key] == null) continue;
                foreach (var c in children[next.Key])
                {
                    var into = piece;
                    var child = snaps[c];
                    var dock = DockModule(child);
                    if (dock != null && Docked(dock.GetValue("state")) && (DockUid(dock) == pp.flightID || DockUid(DockModule(pp)) == child.flightID))
                    {
                        var info = dock.GetNode("DOCKEDVESSEL");
                        var name = info?.GetValue("vesselName");
                        var type = VesselType.Unknown;
                        if (int.TryParse(info?.GetValue("vesselType"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)) type = (VesselType)t;
                        else if (info?.GetValue("vesselType") is string typeName) try { type = (VesselType)Enum.Parse(typeof(VesselType), typeName); } catch (ArgumentException) { }
                        into = new StackPiece
                        {
                            Name = string.IsNullOrEmpty(name) ? "Docked part" : name, Type = type,
                            PortType = child.partInfo?.partPrefab?.FindModuleImplementing<ModuleDockingNode>()?.nodeType,
                            PortTitle = child.partInfo?.title,
                        };
                        pieces.Add(into);
                    }
                    todo.Push(new KeyValuePair<int, StackPiece>(c, into));
                }
            }
            return pieces;
        }

        private static ConfigNode DockModule(ProtoPartSnapshot pp) => pp?.modules.FirstOrDefault(m => m.moduleName == "ModuleDockingNode")?.moduleValues;

        private static uint DockUid(ConfigNode dock) =>
            dock != null && uint.TryParse(dock.GetValue("dockUId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid) ? uid : 0;

        // ---------------- Fuel ----------------

        /// <summary>The station's tanks: those of every piece marked as the station's.</summary>
        internal static List<FuelTank> StationTanks(IEnumerable<StackPiece> pieces) => pieces.Where(p => p.IsStation).SelectMany(p => p.Tanks()).ToList();

        /// <summary>How much of a resource these tanks hold, and their room for it, in units.</summary>
        internal static void Totals(IEnumerable<FuelTank> tanks, string resource, out double amount, out double max)
        {
            amount = max = 0;
            foreach (var t in tanks)
                if (t.Name == resource)
                {
                    amount += t.Amount;
                    max += t.Max;
                }
        }

        /// <summary>
        /// The pump's flow through the docking port a craft is joined by (kg/s), and a name for that port. Set by the port's
        /// size, from its node type: Junior (size0), standard (size1, and anything not otherwise known) or Senior (size2
        /// and up). The rates are the Refueler's settings. docs/TRAFFIC-CONTROLLER.md, "Fuel transfer and RCS balance".
        /// </summary>
        internal static double PumpKgS(StackPiece piece, out string through)
        {
            var s = RefuelerSettings.Current;
            var type = (piece?.PortType ?? "").ToLowerInvariant();
            var title = piece?.PortTitle ?? "its docking port";
            if (type.Contains("size0"))
            {
                through = title + " (Junior)";
                return s.JuniorKgS;
            }
            if (type.Contains("size2") || type.Contains("size3") || type.Contains("size4"))
            {
                through = title + " (Senior)";
                return s.SeniorKgS;
            }
            through = title + " (standard)";
            return s.StandardKgS;
        }

        /// <summary>
        /// How much of each fuel could still move (units), filling the craft (<paramref name="unload"/> false) or unloading
        /// it: the smaller of what one side holds and what the other has room for. Also the mass of it all (t).
        /// </summary>
        internal static double[] Movable(IList<FuelTank> craft, IList<FuelTank> station, bool unload, out double tons)
        {
            var units = new double[Resources.Length];
            tons = 0;
            for (var i = 0; i < Resources.Length; i++)
            {
                Totals(craft, Resources[i], out var a, out var am);
                Totals(station, Resources[i], out var b, out var bm);
                units[i] = Math.Max(0, unload ? Math.Min(a, bm - b) : Math.Min(b, am - a));
                tons += units[i] * Density(Resources[i]);
            }
            return units;
        }

        /// <summary>A resource's density (t per unit): 0.005 for liquid fuel and oxidizer, 0.004 for monopropellant.</summary>
        internal static double Density(string resource) => PartResourceLibrary.Instance?.GetDefinition(resource)?.density ?? 0.005;

        /// <summary>
        /// Pumps <paramref name="tons"/> of fuel between the craft and the station, shared among the fuels by how much of each
        /// could still move, so they all finish together. Returns the mass moved (t).
        /// </summary>
        internal static double Pump(IList<FuelTank> craft, IList<FuelTank> station, bool unload, double tons)
        {
            var units = Movable(craft, station, unload, out var total);
            if (total <= 1e-9 || tons <= 0) return 0;
            var share = Math.Min(1, tons / total);
            double moved = 0;
            for (var i = 0; i < Resources.Length; i++)
            {
                if (units[i] <= 0) continue;
                var u = unload ? Move(craft, station, Resources[i], units[i] * share) : Move(station, craft, Resources[i], units[i] * share);
                moved += u * Density(Resources[i]);
            }
            return moved;
        }

        /// <summary>
        /// Moves up to <paramref name="units"/> of a resource from one set of tanks to the other: no more than the source
        /// has or the destination has room for. Returns what was moved.
        /// </summary>
        internal static double Move(IList<FuelTank> from, IList<FuelTank> to, string resource, double units)
        {
            Totals(from, resource, out var have, out _);
            Totals(to, resource, out var held, out var room);
            var move = Math.Min(units, Math.Min(have, room - held));
            if (move <= 1e-6) return 0;
            foreach (var t in from)
                if (t.Name == resource && have > 0) t.Amount = Math.Max(0, t.Amount - move * t.Amount / have);
            var free = room - held;
            foreach (var t in to)
                if (t.Name == resource && free > 0) t.Amount = Math.Min(t.Max, t.Amount + move * (t.Max - t.Amount) / free);
            return move;
        }
    }
}
