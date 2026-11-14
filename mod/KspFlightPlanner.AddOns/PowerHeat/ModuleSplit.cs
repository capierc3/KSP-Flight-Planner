using System.Collections.Generic;
using System.Linq;

namespace KspFlightPlanner.PowerHeat
{
    /// <summary>One module of a station built as one craft: the parts between its docking ports.</summary>
    internal sealed class StationModule
    {
        public string Name = "";
        public string Role = "";
        public readonly List<Part> Parts = new List<Part>();

        /// <summary>Titles of the module's own docking ports at its joins.</summary>
        public readonly List<string> Joins = new List<string>();
    }

    /// <summary>
    /// Splits a station built whole in the editor into the modules it will be launched as (docs/POWER-AND-HEAT.md
    /// "Modules"). The cut goes only between two docking ports attached port to port: each port stays with the parts on
    /// its side. A single port between ordinary parts doesn't split. Each module is then named by what it does.
    /// </summary>
    internal static class ModuleSplit
    {
        internal static bool IsPort(Part p) => p.FindModuleImplementing<ModuleDockingNode>() != null;

        internal static List<StationModule> Split(IList<Part> parts)
        {
            var index = new Dictionary<Part, int>();
            for (var i = 0; i < parts.Count; i++) index[parts[i]] = i;
            var root = Enumerable.Range(0, parts.Count).ToArray();
            int Find(int i)
            {
                while (root[i] != i) i = root[i] = root[root[i]];
                return i;
            }
            // Cut only between two docking ports attached port to port: each port stays with the parts on its side.
            var joins = new List<Part>();
            for (var i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.parent == null || !index.TryGetValue(p.parent, out var j)) continue;
                if (IsPort(p) && IsPort(p.parent))
                {
                    joins.Add(p);
                    joins.Add(p.parent);
                    continue;
                }
                root[Find(i)] = Find(j);
            }
            // Modules in the order their first part comes in the craft (the root's module first).
            var modules = new List<StationModule>();
            var byRoot = new Dictionary<int, StationModule>();
            for (var i = 0; i < parts.Count; i++)
            {
                var r = Find(i);
                if (!byRoot.TryGetValue(r, out var m))
                {
                    byRoot[r] = m = new StationModule();
                    modules.Add(m);
                }
                m.Parts.Add(parts[i]);
                if (joins.Contains(parts[i])) m.Joins.Add(parts[i].partInfo?.title ?? parts[i].name);
            }
            var count = new Dictionary<string, int>();
            foreach (var m in modules)
            {
                m.Role = RoleOf(m.Parts);
                count.TryGetValue(m.Role, out var n);
                count[m.Role] = n + 1;
            }
            var seen = new Dictionary<string, int>();
            foreach (var m in modules)
            {
                seen.TryGetValue(m.Role, out var n);
                seen[m.Role] = ++n;
                m.Name = m.Role + " module" + (count[m.Role] > 1 ? " " + n : "");
            }
            return modules;
        }

        /// <summary>
        /// What a module is for, by the first of these it has: drills or ISRU (Drilling), a science lab (Science), a crewed
        /// part with controls (Command), seats (Habitat), engines (Ship: the launcher or a tug), parts that make charge or
        /// lots of battery (Electric), tanks (Fuel). Anything else is just a Module.
        /// </summary>
        internal static string RoleOf(IEnumerable<Part> parts)
        {
            var list = parts.ToList();
            bool Any<T>() where T : class => list.Any(p => p.FindModuleImplementing<T>() != null);
            if (Any<ModuleResourceHarvester>()) return "Drilling";
            if (list.Any(p => p.Modules.OfType<ModuleResourceConverter>().Any(c => c.inputList.Any(r => r.ResourceName == "Ore")))) return "ISRU";
            if (Any<ModuleScienceConverter>()) return "Science";
            if (list.Any(p => p.CrewCapacity > 0 && p.FindModuleImplementing<ModuleCommand>() != null)) return "Command";
            if (list.Any(p => p.CrewCapacity > 0)) return "Habitat";
            if (Any<ModuleEngines>()) return "Ship";
            var battery = list.Sum(p => p.Resources.Where(r => r.resourceName == PowerReader.EC).Sum(r => r.maxAmount));
            if (Any<ModuleDeployableSolarPanel>() || Any<ModuleGenerator>() || battery >= 1000) return "Electric";
            if (list.Any(p => p.Resources.Any(r => r.resourceName != PowerReader.EC && r.maxAmount > 0))) return "Fuel";
            return list.Any(p => p.FindModuleImplementing<ModuleCommand>() != null) ? "Probe" : "Module";
        }
    }
}
