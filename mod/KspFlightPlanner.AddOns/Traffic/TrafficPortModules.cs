using System.Collections.Generic;
using System.Linq;

namespace KspFlightPlanner.Traffic
{
    /// <summary>
    /// Which route craft may dock at this port: a right-click setting on every docking port (added by the add-on's
    /// ModuleManager patch, TrafficPorts.cfg), saved with the part. Route craft pick only ports open to their role
    /// (docs/MATH.md §11.15). The player can always dock by hand.
    /// </summary>
    public sealed class ModuleTrafficPort : PartModule
    {
        internal const string Any = "Any", CrewSupplies = "CrewSupplies", Tankers = "Tankers", Closed = "Closed";

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Traffic")]
        [UI_ChooseOption(options = new[] { Any, CrewSupplies, Tankers, Closed },
            display = new[] { "Any craft", "Crew & supplies only", "Tankers only", "No route craft" }, scene = UI_Scene.All)]
        public string access = Any;
    }

    /// <summary>
    /// What a route craft carries, which decides the ports it may use: a right-click setting on every command pod and probe
    /// core (added by the same patch). A ship is a tanker if any of its command parts says so.
    /// </summary>
    public sealed class ModuleTrafficRole : PartModule
    {
        internal const string CrewSupplies = "CrewSupplies", Tanker = "Tanker";

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Traffic role")]
        [UI_ChooseOption(options = new[] { CrewSupplies, Tanker }, display = new[] { "Crew & supplies", "Tanker" }, scene = UI_Scene.All)]
        public string role = CrewSupplies;
    }

    /// <summary>Reading the port and role settings, from loaded parts or saved ones (docs/MATH.md §11.15).</summary>
    internal static class PortAccess
    {
        /// <summary>A port with this setting is open to a route craft with this role.</summary>
        internal static bool Allows(string access, string role)
        {
            switch (access)
            {
                case ModuleTrafficPort.Closed: return false;
                case ModuleTrafficPort.Tankers: return role == ModuleTrafficRole.Tanker;
                case ModuleTrafficPort.CrewSupplies: return role != ModuleTrafficRole.Tanker;
                default: return true;
            }
        }

        /// <summary>How the role reads in messages: "tankers", "crew & supplies craft".</summary>
        internal static string RolePlural(string role) => role == ModuleTrafficRole.Tanker ? "tankers" : "crew & supplies craft";

        /// <summary>
        /// The station's docking ports a route craft with this role may not use (part persistent ids), loaded or not. Added to
        /// the ports other route craft have claimed wherever a port is picked.
        /// </summary>
        internal static HashSet<uint> NotOpenTo(Vessel station, string role)
        {
            var shut = new HashSet<uint>();
            if (station == null) return shut;
            if (station.loaded)
            {
                foreach (var m in station.FindPartModulesImplementing<ModuleTrafficPort>())
                    if (!Allows(m.access, role)) shut.Add(m.part.persistentId);
            }
            else if (station.protoVessel != null)
            {
                foreach (var pp in station.protoVessel.protoPartSnapshots)
                foreach (var m in pp.modules)
                    if (m.moduleName == nameof(ModuleTrafficPort) && m.moduleValues != null && !Allows(m.moduleValues.GetValue("access") ?? ModuleTrafficPort.Any, role))
                        shut.Add(pp.persistentId);
            }
            return shut;
        }

        /// <summary>A ship's role, from its command parts, loaded or not: a tanker if any says so.</summary>
        internal static string RoleOf(Vessel ship)
        {
            if (ship == null) return ModuleTrafficRole.CrewSupplies;
            if (ship.loaded) return RoleOf(ship.FindPartModulesImplementing<ModuleTrafficRole>().Select(m => m.role));
            return ship.protoVessel == null ? ModuleTrafficRole.CrewSupplies : RoleOf(Snapshots(ship.protoVessel.protoPartSnapshots));
        }

        /// <summary>The role of these parts (a docked craft's own, say): a tanker if any of their command parts says so.</summary>
        internal static string RoleOf(IEnumerable<Part> parts) =>
            RoleOf(parts.Select(p => p.FindModuleImplementing<ModuleTrafficRole>()).Where(m => m != null).Select(m => m.role));

        /// <summary>The role of these saved parts.</summary>
        internal static IEnumerable<string> Snapshots(IEnumerable<ProtoPartSnapshot> parts) =>
            parts.SelectMany(pp => pp.modules).Where(m => m.moduleName == nameof(ModuleTrafficRole) && m.moduleValues != null)
                .Select(m => m.moduleValues.GetValue("role"));

        private static string RoleOf(IEnumerable<string> roles) =>
            roles.Any(r => r == ModuleTrafficRole.Tanker) ? ModuleTrafficRole.Tanker : ModuleTrafficRole.CrewSupplies;
    }
}
