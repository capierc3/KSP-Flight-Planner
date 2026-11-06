using System;
using System.Collections.Generic;
using System.Linq;

namespace KspFlightPlanner.Haulers
{
    /// <summary>
    /// Moving fuel between a docked craft and the station it's docked at (the rest of the stack), either way: the
    /// Haulers panel's Fuel section. Liquid fuel, oxidizer and monopropellant, taken from every unlocked tank in
    /// proportion to what each holds and put into every unlocked tank in proportion to its free room. A real transfer:
    /// nothing is made or lost. docs/MATH.md §11.9.
    /// </summary>
    internal static class FuelTransfer
    {
        internal static readonly string[] Resources = { "LiquidFuel", "Oxidizer", "MonoPropellant" };

        /// <summary>The craft docked in this (loaded) stack, by their own names, with each one's docking port.</summary>
        internal static List<KeyValuePair<string, ModuleDockingNode>> DockedCrafts(Vessel stack)
        {
            var list = new List<KeyValuePair<string, ModuleDockingNode>>();
            if (stack == null || !stack.loaded) return list;
            foreach (var node in stack.FindPartModulesImplementing<ModuleDockingNode>())
            {
                if (node.otherNode == null || node.vesselInfo == null || string.IsNullOrEmpty(node.vesselInfo.name)) continue;
                if (node.vesselInfo.name == stack.vesselName || list.Any(c => c.Key == node.vesselInfo.name)) continue;
                list.Add(new KeyValuePair<string, ModuleDockingNode>(node.vesselInfo.name, node));
            }
            return list;
        }

        /// <summary>The station's side of the stack: every part that isn't the craft's.</summary>
        internal static List<Part> Others(Vessel stack, IList<Part> craft)
        {
            var mine = new HashSet<Part>(craft);
            return stack.parts.Where(p => !mine.Contains(p)).ToList();
        }

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
