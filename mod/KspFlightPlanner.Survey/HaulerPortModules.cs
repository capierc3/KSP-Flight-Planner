using System.Collections.Generic;
using System.Linq;

namespace KspFlightPlanner.Haulers
{
    /// <summary>
    /// Which haulers may dock at this port: a right-click setting on every docking port (added by the add-on's
    /// ModuleManager patch, HaulerPorts.cfg), saved with the part. Haulers pick only ports open to their role
    /// (docs/MATH.md §11.15). The player can always dock by hand.
    /// </summary>
    public sealed class ModuleHaulerPort : PartModule
    {
        internal const string Any = "Any", CrewSupplies = "CrewSupplies", Tankers = "Tankers", Closed = "Closed";

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Haulers")]
        [UI_ChooseOption(options = new[] { Any, CrewSupplies, Tankers, Closed },
            display = new[] { "Any hauler", "Crew & supplies only", "Tankers only", "No haulers" }, scene = UI_Scene.All)]
        public string access = Any;
    }

    /// <summary>
    /// What a hauler carries, which decides the ports it may use: a right-click setting on every command pod and probe
    /// core (added by the same patch). A ship is a tanker if any of its command parts says so.
    /// </summary>
    public sealed class ModuleHaulerRole : PartModule
    {
        internal const string CrewSupplies = "CrewSupplies", Tanker = "Tanker";

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Hauler role")]
        [UI_ChooseOption(options = new[] { CrewSupplies, Tanker }, display = new[] { "Crew & supplies", "Tanker" }, scene = UI_Scene.All)]
        public string role = CrewSupplies;
    }

    /// <summary>Reading the port and role settings, from loaded parts or saved ones (docs/MATH.md §11.15).</summary>
    internal static class HaulerPorts
    {
        /// <summary>A port with this setting is open to a hauler with this role.</summary>
        internal static bool Allows(string access, string role)
        {
            switch (access)
            {
                case ModuleHaulerPort.Closed: return false;
                case ModuleHaulerPort.Tankers: return role == ModuleHaulerRole.Tanker;
                case ModuleHaulerPort.CrewSupplies: return role != ModuleHaulerRole.Tanker;
                default: return true;
            }
        }

        /// <summary>How the role reads in messages: "tankers", "crew & supplies haulers".</summary>
        internal static string RolePlural(string role) => role == ModuleHaulerRole.Tanker ? "tankers" : "crew & supplies haulers";

        /// <summary>
        /// The station's docking ports a hauler with this role may not use (part persistent ids), loaded or not. Added to
        /// the ports other haulers have claimed wherever a port is picked.
        /// </summary>
        internal static HashSet<uint> NotOpenTo(Vessel station, string role)
        {
            var shut = new HashSet<uint>();
            if (station == null) return shut;
            if (station.loaded)
            {
                foreach (var m in station.FindPartModulesImplementing<ModuleHaulerPort>())
                    if (!Allows(m.access, role)) shut.Add(m.part.persistentId);
            }
            else if (station.protoVessel != null)
            {
                foreach (var pp in station.protoVessel.protoPartSnapshots)
                foreach (var m in pp.modules)
                    if (m.moduleName == nameof(ModuleHaulerPort) && m.moduleValues != null && !Allows(m.moduleValues.GetValue("access") ?? ModuleHaulerPort.Any, role))
                        shut.Add(pp.persistentId);
            }
            return shut;
        }

        /// <summary>A ship's role, from its command parts, loaded or not: a tanker if any says so.</summary>
        internal static string RoleOf(Vessel ship)
        {
            if (ship == null) return ModuleHaulerRole.CrewSupplies;
            if (ship.loaded) return RoleOf(ship.FindPartModulesImplementing<ModuleHaulerRole>().Select(m => m.role));
            return ship.protoVessel == null ? ModuleHaulerRole.CrewSupplies : RoleOf(Snapshots(ship.protoVessel.protoPartSnapshots));
        }

        /// <summary>The role of these parts (a docked craft's own, say): a tanker if any of their command parts says so.</summary>
        internal static string RoleOf(IEnumerable<Part> parts) =>
            RoleOf(parts.Select(p => p.FindModuleImplementing<ModuleHaulerRole>()).Where(m => m != null).Select(m => m.role));

        /// <summary>The role of these saved parts.</summary>
        internal static IEnumerable<string> Snapshots(IEnumerable<ProtoPartSnapshot> parts) =>
            parts.SelectMany(pp => pp.modules).Where(m => m.moduleName == nameof(ModuleHaulerRole) && m.moduleValues != null)
                .Select(m => m.moduleValues.GetValue("role"));

        private static string RoleOf(IEnumerable<string> roles) =>
            roles.Any(r => r == ModuleHaulerRole.Tanker) ? ModuleHaulerRole.Tanker : ModuleHaulerRole.CrewSupplies;
    }
}
