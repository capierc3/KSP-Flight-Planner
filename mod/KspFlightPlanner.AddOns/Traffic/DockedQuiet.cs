using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Traffic
{
    /// <summary>
    /// What was switched off on one docked craft: its docking port (part persistent id, the craft's own side) and the parts
    /// whose engines, RCS blocks and reaction wheels were turned off. Only those are turned back on when it undocks.
    /// </summary>
    internal sealed class QuietCraft
    {
        public uint PortPartId;
        public string Craft = "";
        public readonly HashSet<uint> Engines = new HashSet<uint>();
        public readonly HashSet<uint> Rcs = new HashSet<uint>();
        public readonly HashSet<uint> Wheels = new HashSet<uint>();

        internal void Save(ConfigNode n)
        {
            n.AddValue("port", PortPartId);
            n.AddValue("craft", Craft);
            foreach (var id in Engines) n.AddValue("engine", id);
            foreach (var id in Rcs) n.AddValue("rcs", id);
            foreach (var id in Wheels) n.AddValue("wheel", id);
        }

        internal static QuietCraft Load(ConfigNode n)
        {
            uint Id(string s) => uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            var q = new QuietCraft { PortPartId = Id(n.GetValue("port")), Craft = n.GetValue("craft") ?? "" };
            foreach (var s in n.GetValues("engine")) q.Engines.Add(Id(s));
            foreach (var s in n.GetValues("rcs")) q.Rcs.Add(Id(s));
            foreach (var s in n.GetValues("wheel")) q.Wheels.Add(Id(s));
            return q;
        }
    }

    /// <summary>
    /// A docked craft is made quiet: its engines shut down, its RCS blocks and reaction wheels switched off, so nothing on it
    /// fights the station. Reaction wheels on docked ships pulling against the station's SAS are a known way to shake a big
    /// stack apart. It happens on every docking, by the autopilot or the player, docked on rails or in physics (the lighter
    /// of the two craft is the one docking). Whatever was switched off is kept in the save, and turned back on when the craft
    /// undocks, whoever undocks it. docs/TRAFFIC-CONTROLLER.md "Docked craft".
    /// </summary>
    internal static class DockedQuiet
    {
        internal static readonly List<QuietCraft> Crafts = new List<QuietCraft>();

        /// <summary>
        /// Makes the craft on this port (its own side, docked) quiet, once. Returns what it did, for the log ("" when there
        /// was nothing on to switch off, or it's quiet already).
        /// </summary>
        internal static string Quiet(ModuleDockingNode craftPort)
        {
            if (craftPort == null || craftPort.otherNode == null) return "";
            var id = craftPort.part.persistentId;
            if (Crafts.Any(c => c.PortPartId == id)) return "";
            var q = new QuietCraft { PortPartId = id, Craft = craftPort.vesselInfo?.name ?? craftPort.part.vessel?.vesselName ?? "" };
            foreach (var part in Ships.DockedCraftParts(craftPort))
            {
                foreach (var e in part.FindModulesImplementing<ModuleEngines>())
                    if (e.EngineIgnited)
                    {
                        e.Shutdown();
                        q.Engines.Add(part.persistentId);
                    }
                foreach (var rcs in part.FindModulesImplementing<ModuleRCS>())
                    if (rcs.rcsEnabled)
                    {
                        rcs.rcsEnabled = false;
                        q.Rcs.Add(part.persistentId);
                    }
                foreach (var w in part.FindModulesImplementing<ModuleReactionWheel>())
                    if (w.State == ModuleReactionWheel.WheelState.Active)
                    {
                        w.State = ModuleReactionWheel.WheelState.Disabled;
                        q.Wheels.Add(part.persistentId);
                    }
            }
            if (q.Engines.Count + q.Rcs.Count + q.Wheels.Count == 0) return "";
            Crafts.Add(q);
            var line = "Shut down " + Count(q.Engines.Count, "engine") + ", switched off " + Count(q.Rcs.Count, "RCS block") + " and " +
                       Count(q.Wheels.Count, "reaction wheel") + ".";
            Debug.Log("[KspFlightPlanner] " + q.Craft + " docked: " + line);
            return " " + line;
        }

        /// <summary>
        /// Docked on rails: the same for the craft's saved parts (<paramref name="parts"/>, PART nodes) in the merged record,
        /// its port being the part with <paramref name="portPartId"/>.
        /// </summary>
        internal static void QuietRecord(IEnumerable<ConfigNode> parts, uint portPartId, string craft)
        {
            if (Crafts.Any(c => c.PortPartId == portPartId)) return;
            var q = new QuietCraft { PortPartId = portPartId, Craft = craft ?? "" };
            foreach (var p in parts)
            {
                if (!uint.TryParse(p.GetValue("persistentId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) continue;
                foreach (var m in p.GetNodes("MODULE"))
                {
                    var kind = m.GetValue("name") ?? "";
                    if (kind.StartsWith("ModuleEngines", StringComparison.Ordinal) && m.GetValue("EngineIgnited") == "True")
                    {
                        m.SetValue("EngineIgnited", "False");
                        q.Engines.Add(pid);
                    }
                    if (kind.StartsWith("ModuleRCS", StringComparison.Ordinal) && m.GetValue("rcsEnabled") == "True")
                    {
                        m.SetValue("rcsEnabled", "False");
                        q.Rcs.Add(pid);
                    }
                    if (kind == "ModuleReactionWheel" && m.GetValue("WheelState") == "Active")
                    {
                        m.SetValue("WheelState", "Disabled");
                        m.SetValue("stateString", "Disabled", true);
                        q.Wheels.Add(pid);
                    }
                }
            }
            if (q.Engines.Count + q.Rcs.Count + q.Wheels.Count > 0) Crafts.Add(q);
        }

        /// <summary>Undocked on rails: the craft's record was readied to fly whole, so its quiet entry (by any of its parts) goes.</summary>
        internal static void ForgetParts(IEnumerable<uint> partIds)
        {
            var ids = new HashSet<uint>(partIds);
            Crafts.RemoveAll(c => ids.Contains(c.PortPartId));
        }

        /// <summary>
        /// After any undocking (a frame later, once KSP has split the craft): every quiet craft whose port is no longer docked
        /// gets back what was switched off on it: engines lit at zero throttle, RCS blocks and reaction wheels on. Returns
        /// the vessels woken.
        /// </summary>
        internal static HashSet<uint> WakeUndocked()
        {
            var woken = new HashSet<uint>();
            foreach (var q in Crafts.ToList())
            {
                var vessel = Ships.VesselOfPart(q.PortPartId);
                if (vessel == null || !vessel.loaded) continue;
                var port = Ships.FindPort(vessel, q.PortPartId);
                if (port != null && port.otherNode != null) continue; // still docked
                Crafts.Remove(q);
                woken.Add(vessel.persistentId);
                int engines = 0, thrusters = 0, wheels = 0;
                foreach (var part in vessel.parts)
                {
                    if (q.Engines.Contains(part.persistentId))
                        foreach (var e in part.FindModulesImplementing<ModuleEngines>())
                            if (!e.EngineIgnited)
                            {
                                e.Activate();
                                engines++;
                            }
                    if (q.Rcs.Contains(part.persistentId))
                        foreach (var rcs in part.FindModulesImplementing<ModuleRCS>())
                            if (!rcs.rcsEnabled)
                            {
                                rcs.rcsEnabled = true;
                                thrusters++;
                            }
                    if (q.Wheels.Contains(part.persistentId))
                        foreach (var w in part.FindModulesImplementing<ModuleReactionWheel>())
                            if (w.State == ModuleReactionWheel.WheelState.Disabled)
                            {
                                w.State = ModuleReactionWheel.WheelState.Active;
                                wheels++;
                            }
                }
                if (vessel.ctrlState != null && engines > 0) vessel.ctrlState.mainThrottle = 0;
                var line = vessel.vesselName + " undocked: lit " + Count(engines, "engine") + ", switched on " + Count(thrusters, "RCS block") + " and " + Count(wheels, "reaction wheel") + ".";
                Debug.Log("[KspFlightPlanner] " + line);
                ScreenMessages.PostScreenMessage(line, 5f, ScreenMessageStyle.UPPER_CENTER);
            }
            return woken;
        }

        private static string Count(int n, string what) => n + " " + what + (n == 1 ? "" : "s");

        internal static void Save(ConfigNode node)
        {
            foreach (var q in Crafts) q.Save(node.AddNode("QUIET"));
        }

        internal static void Load(ConfigNode node)
        {
            Crafts.Clear();
            foreach (var n in node.GetNodes("QUIET")) Crafts.Add(QuietCraft.Load(n));
        }
    }
}
