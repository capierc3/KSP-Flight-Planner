using System;
using System.Collections.Generic;
using System.Linq;

namespace KspFlightPlanner.Refueler
{
    /// <summary>
    /// One vessel that docked into a stack: the parts it came with (docked craft hanging off it are pieces of their
    /// own), its name and type from its docking port, and whether it's one of the station's tanks or a craft of its own.
    /// </summary>
    internal sealed class StackPiece
    {
        internal string Name;
        internal VesselType Type;
        internal readonly List<Part> Parts = new List<Part>();
        internal bool IsRoot;

        /// <summary>The planner's rule (DockedPiece.IsCraftByDefault): a ship, lander, rover or plane with a command part and an engine.</summary>
        internal bool IsCraftByDefault =>
            (Type == VesselType.Ship || Type == VesselType.Lander || Type == VesselType.Rover || Type == VesselType.Plane)
            && Parts.Any(p => p.FindModuleImplementing<ModuleCommand>() != null)
            && Parts.Any(p => p.FindModuleImplementing<ModuleEngines>() != null);

        /// <summary>Part of the station (its fuel is the station's), by the player's mark when there is one, else the rule.</summary>
        internal bool IsStation => FuelTransfer.StationMark(Name) ?? !IsCraftByDefault;
    }

    /// <summary>
    /// Moving fuel between a docked craft and the station it's docked at, either way: the Station fuel window. The
    /// station's fuel is the pieces of the stack marked as station parts (its tanks, its core), never the other craft
    /// docked there. Liquid fuel, oxidizer and monopropellant, taken from every unlocked tank in proportion to what each
    /// holds and put into every unlocked tank in proportion to its free room. A real transfer: nothing is made or lost.
    /// docs/MATH.md §11.9.
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

        /// <summary>
        /// A loaded stack split back into the vessels that docked to make it, the way the planner does
        /// (DockedCraft.Pieces): the part tree cut at every docked pair of ports. The piece with the root part comes
        /// first and carries the stack's name; the others are named by their own port.
        /// </summary>
        internal static List<StackPiece> Pieces(Vessel stack)
        {
            var pieces = new List<StackPiece>();
            if (stack == null || !stack.loaded || stack.rootPart == null) return pieces;
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
                foreach (var child in part.children)
                {
                    var into = piece;
                    if (DockedJoin(child, part) is ModuleDockingNode port)
                    {
                        var info = port.vesselInfo;
                        into = new StackPiece { Name = info != null && !string.IsNullOrEmpty(info.name) ? info.name : "Docked part", Type = info != null ? info.vesselType : VesselType.Unknown };
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
                if (node.otherNode != null && node.otherNode.part == parent && Docked(node)) return node;
            foreach (var node in parent.FindModulesImplementing<ModuleDockingNode>())
                if (node.otherNode != null && node.otherNode.part == child && Docked(node))
                    return node.otherNode;
            return null;
        }

        private static bool Docked(ModuleDockingNode node) => node.state != null && node.state.StartsWith("Docked", StringComparison.Ordinal);

        /// <summary>The station's tanks: the parts of every piece marked as the station's.</summary>
        internal static List<Part> StationParts(IEnumerable<StackPiece> pieces) => pieces.Where(p => p.IsStation).SelectMany(p => p.Parts).ToList();

        /// <summary>How much of a resource these parts hold, and their room for it, in units (unlocked tanks only).</summary>
        internal static void Totals(IEnumerable<Part> parts, string resource, out double amount, out double max)
        {
            amount = max = 0;
            foreach (var p in parts)
                foreach (var r in p.Resources)
                    if (r.resourceName == resource && r.flowState)
                    {
                        amount += r.amount;
                        max += r.maxAmount;
                    }
        }

        /// <summary>
        /// Moves up to <paramref name="units"/> of a resource from one set of parts to the other: no more than the source
        /// has or the destination has room for. Returns what was moved.
        /// </summary>
        internal static double Move(IList<Part> from, IList<Part> to, string resource, double units)
        {
            Totals(from, resource, out var have, out _);
            Totals(to, resource, out var held, out var room);
            var move = Math.Min(units, Math.Min(have, room - held));
            if (move <= 1e-6) return 0;
            foreach (var p in from)
                foreach (var r in p.Resources)
                    if (r.resourceName == resource && r.flowState && have > 0) r.amount = Math.Max(0, r.amount - move * r.amount / have);
            var free = room - held;
            foreach (var p in to)
                foreach (var r in p.Resources)
                    if (r.resourceName == resource && r.flowState && free > 0) r.amount = Math.Min(r.maxAmount, r.amount + move * (r.maxAmount - r.amount) / free);
            return move;
        }
    }
}
