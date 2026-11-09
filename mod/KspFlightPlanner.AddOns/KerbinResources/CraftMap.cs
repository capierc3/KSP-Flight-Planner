using System;
using System.Collections.Generic;
using System.Linq;

namespace KspFlightPlanner.KerbinResources
{
    /// <summary>
    /// Which craft each part of a docked stack came from: the station's own parts, or a ship docked to it. Every docked
    /// port remembers the craft it belonged to before docking (KSP's DOCKEDVESSEL), so walking up the part tree from a
    /// part, the first docked port met names its craft. Parts that meet none belong to the craft at the tree's root. The
    /// stack's main craft is the one KSP named it after (the station, as a rule), or the root's if none matches. Works
    /// on a loaded vessel's parts and on a saved one's records alike.
    /// </summary>
    internal sealed class CraftMap
    {
        private sealed class Node
        {
            public uint Id;
            public int Parent = -1;
            public string DockedName; // the craft this docked port came from; null if it isn't a docked port
            public List<string> Crew = new List<string>();
        }

        private readonly List<Node> _parts = new List<Node>();
        private readonly string _vesselName;
        private string[] _craft;

        /// <summary>The craft the stack is named after (the station), or the root's craft when none is.</summary>
        internal string Main { get; private set; } = "";

        private CraftMap(string vesselName) => _vesselName = vesselName ?? "";

        internal static CraftMap Of(Vessel v)
        {
            var map = new CraftMap(v.vesselName);
            if (v.loaded)
            {
                var index = new Dictionary<Part, int>();
                for (var i = 0; i < v.parts.Count; i++) index[v.parts[i]] = i;
                foreach (var p in v.parts)
                {
                    var dock = p.FindModuleImplementing<ModuleDockingNode>();
                    map._parts.Add(new Node
                    {
                        Id = p.persistentId,
                        Parent = p.parent != null && index.TryGetValue(p.parent, out var at) ? at : -1,
                        DockedName = dock != null && (dock.state ?? "").StartsWith("Docked", StringComparison.Ordinal) ? dock.vesselInfo?.name : null,
                        Crew = p.protoModuleCrew.Select(c => c.name).ToList(),
                    });
                }
            }
            else if (v.protoVessel != null) map.Read(v.protoVessel);
            map.Finish();
            return map;
        }

        internal static CraftMap Of(ProtoVessel pv)
        {
            var map = new CraftMap(pv.vesselName);
            map.Read(pv);
            map.Finish();
            return map;
        }

        private void Read(ProtoVessel pv)
        {
            foreach (var pp in pv.protoPartSnapshots)
            {
                var dock = pp.modules.FirstOrDefault(m => m.moduleName == "ModuleDockingNode")?.moduleValues;
                var docked = dock != null && (dock.GetValue("state") ?? "").StartsWith("Docked", StringComparison.Ordinal);
                _parts.Add(new Node
                {
                    Id = pp.persistentId,
                    Parent = pp.parentIdx >= 0 && pp.parentIdx < pv.protoPartSnapshots.Count && pp != pv.protoPartSnapshots[pp.parentIdx] ? pp.parentIdx : -1,
                    DockedName = docked ? dock.GetNode("DOCKEDVESSEL")?.GetValue("vesselName") : null,
                    Crew = pp.protoModuleCrew.Select(c => c.name).ToList(),
                });
            }
        }

        private void Finish()
        {
            _craft = new string[_parts.Count];
            // The root's craft: named by a docked port whose way up meets no other docked port (the root side of a docking).
            string rootCraft = null;
            for (var i = 0; i < _parts.Count && rootCraft == null; i++)
                if (_parts[i].DockedName != null && FirstDockedAbove(_parts[i].Parent) < 0 && !IsLowerSide(i)) rootCraft = _parts[i].DockedName;
            rootCraft = rootCraft ?? _vesselName;
            for (var i = 0; i < _parts.Count; i++)
            {
                var d = FirstDockedAbove(i);
                _craft[i] = d >= 0 && !string.IsNullOrEmpty(_parts[d].DockedName) ? _parts[d].DockedName : rootCraft;
            }
            Main = _craft.Contains(_vesselName) ? _vesselName : rootCraft;
        }

        /// <summary>The first docked port from this part up to the root, the part itself included (-1 if none).</summary>
        private int FirstDockedAbove(int i)
        {
            for (var guard = 0; i >= 0 && guard <= _parts.Count; guard++, i = _parts[i].Parent)
                if (_parts[i].DockedName != null) return i;
            return -1;
        }

        /// <summary>A docked port hanging under the other port of its docking (its parent is a docked port too).</summary>
        private bool IsLowerSide(int i) => _parts[i].Parent >= 0 && _parts[_parts[i].Parent].DockedName != null;

        /// <summary>The craft a kerbal is aboard in this stack (null if they aren't), and the part they're in.</summary>
        internal string CraftOfKerbal(string kerbal, out uint partId)
        {
            for (var i = 0; i < _parts.Count; i++)
                if (_parts[i].Crew.Contains(kerbal))
                {
                    partId = _parts[i].Id;
                    return _craft[i];
                }
            partId = 0;
            return null;
        }

        /// <summary>A part of the main craft (the first in the tree), to follow the station by through dockings; 0 if none.</summary>
        internal uint MainAnchor()
        {
            for (var i = 0; i < _parts.Count; i++)
                if (_craft[i] == Main) return _parts[i].Id;
            return 0;
        }

        internal bool HasPart(uint id) => _parts.Any(p => p.Id == id);
    }
}
