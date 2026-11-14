using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KspFlightPlanner.PowerHeat
{
    /// <summary>What a power item is, which decides when it counts (docs/POWER-AND-HEAT.md "What counts").</summary>
    internal enum PowerKind
    {
        /// <summary>Solar panels: only in sunlight, scaled by distance from the Sun.</summary>
        Solar,

        /// <summary>RTGs and other generators: day and night.</summary>
        Generator,

        /// <summary>Fuel cells: day and night, but only run to cover what's missing (they burn fuel).</summary>
        FuelCell,

        /// <summary>Everything that uses charge: probe cores, lights, the lab, drills, ISRU, radiators.</summary>
        Load,
    }

    /// <summary>One part module that makes or uses electric charge.</summary>
    internal sealed class PowerItem
    {
        /// <summary>Part id + module index: the key the editor's what-if switches are kept by.</summary>
        public string Key = "";

        public uint PartId;
        public string Title = "";
        public PowerKind Kind;

        /// <summary>
        /// Converters on one part share a group (an ISRU's Lf+Ox, Monoprop… recipes): the editor shows them as one switch
        /// that picks which recipe runs. Empty for anything else.
        /// </summary>
        public string Group = "";

        /// <summary>The part's title without the recipe, and the recipe's name, for grouped converters.</summary>
        public string PartTitle = "", Option = "";

        /// <summary>EC/s when running: made for sources, used for loads (always ≥ 0). Solar: at Kerbin's distance, facing the Sun.</summary>
        public double Rate;

        /// <summary>Switched on (or, for a panel, deployed and not broken).</summary>
        public bool On;

        /// <summary>Solar only: a panel that turns to follow the Sun. Fixed panels make their rating only when the craft faces it.</summary>
        public bool Tracking = true;

        /// <summary>Solar only: the panel's own output curve against distance from the Sun (m → share of its rating).</summary>
        public FloatCurve PowerCurve;

        /// <summary>Solar only, loaded craft: what it makes this moment (EC/s), angle and shadow included.</summary>
        public double Now = -1;
    }

    /// <summary>One part whose converters or drills make core heat (ModuleCoreHeat).</summary>
    internal sealed class HeatCore
    {
        public uint PartId;
        public string Title = "";

        /// <summary>Cooling it asks for at its goal temperature with what's switched on (kW), as KSP's part info shows it.</summary>
        public double NeedKw;

        /// <summary>The most radiator cooling it can draw (kW, MaxCoolant).</summary>
        public double MaxKw;

        public double GoalK, ShutdownK;

        /// <summary>Loaded craft: the core's temperature now (K), else −1.</summary>
        public double CoreK = -1;

        /// <summary>Loaded craft: the running converters' worst thermal efficiency at that temperature (0–1), else −1.</summary>
        public double Efficiency = -1;

        /// <summary>The converters that make heat, with whether each is on (key part id + module index, for the editor's switches).</summary>
        public readonly List<KeyValuePair<string, bool>> Converters = new List<KeyValuePair<string, bool>>();

        /// <summary>Heat each converter makes at the goal (kW), same order as Converters.</summary>
        public readonly List<double> ConverterKw = new List<double>();
    }

    /// <summary>A radiator that takes core heat (ModuleActiveRadiator).</summary>
    internal sealed class Radiator
    {
        public string Key = "";
        public uint PartId;
        public string Title = "";

        /// <summary>Core heat it can move (kW): maxEnergyTransfer / 50.</summary>
        public double Kw;

        /// <summary>Cooling and, if it folds, extended.</summary>
        public bool On;

        /// <summary>Panels that only cool the part they're mounted on (and that part's other children).</summary>
        public bool ParentOnly;

        /// <summary>Parts it can cool when ParentOnly: its parent and siblings.</summary>
        public readonly List<uint> Reaches = new List<uint>();
    }

    /// <summary>Everything that makes, stores or uses charge on a craft, and its core heat and radiators.</summary>
    internal sealed class PowerParts
    {
        public readonly List<PowerItem> Items = new List<PowerItem>();
        public readonly List<HeatCore> Cores = new List<HeatCore>();
        public readonly List<Radiator> Radiators = new List<Radiator>();

        /// <summary>Electric charge stored now and the batteries' size (EC).</summary>
        public double Stored, Capacity;

        public bool Loaded;
    }

    /// <summary>
    /// Reads a craft's power and heat parts: live parts (in the editor or a loaded craft in flight), or the saved snapshot of
    /// an unloaded one, where the rates come from the part's prefab and on/off from the saved module values.
    /// </summary>
    internal static class PowerReader
    {
        internal const string EC = "ElectricCharge";

        /// <summary>KSP's core-heat and radiator figures are in energy per 1/50 s: dividing by 50 gives kW (what part info shows).</summary>
        internal const double HeatUnitsPerKw = 50;

        /// <summary>Live parts: the editor's ship or a loaded vessel.</summary>
        internal static PowerParts Of(IEnumerable<Part> parts, bool loaded)
        {
            var result = new PowerParts { Loaded = loaded };
            var list = parts.Where(p => p != null).ToList();
            foreach (var p in list)
            {
                var siblings = Reach(p);
                Read(p, p.persistentId, p.partInfo?.title ?? p.name, null, siblings, result, loaded);
                foreach (var r in p.Resources)
                    if (r.resourceName == EC)
                    {
                        result.Stored += r.amount;
                        result.Capacity += r.maxAmount;
                    }
            }
            return result;
        }

        /// <summary>An unloaded craft's saved snapshot.</summary>
        internal static PowerParts Of(ProtoVessel pv)
        {
            var result = new PowerParts();
            var snaps = pv.protoPartSnapshots;
            for (var i = 0; i < snaps.Count; i++)
            {
                var pp = snaps[i];
                var prefab = pp.partInfo?.partPrefab;
                if (prefab == null) continue;
                var siblings = new List<uint>();
                if (pp.parentIdx >= 0 && pp.parentIdx < snaps.Count && pp.parentIdx != i)
                {
                    siblings.Add(snaps[pp.parentIdx].persistentId);
                    for (var j = 0; j < snaps.Count; j++)
                        if (j != i && snaps[j].parentIdx == pp.parentIdx) siblings.Add(snaps[j].persistentId);
                }
                Read(prefab, pp.persistentId, pp.partInfo.title ?? pp.partName, pp.modules, siblings, result, false);
                foreach (var r in pp.resources)
                    if (r.resourceName == EC)
                    {
                        result.Stored += r.amount;
                        result.Capacity += r.maxAmount;
                    }
            }
            return result;
        }

        /// <summary>The parts a parent-only radiator on this part can cool: its parent and the parent's other children.</summary>
        private static List<uint> Reach(Part p)
        {
            var list = new List<uint>();
            if (p.parent == null) return list;
            list.Add(p.parent.persistentId);
            foreach (var c in p.parent.children)
                if (c != p) list.Add(c.persistentId);
            return list;
        }

        /// <summary>
        /// One part's modules. With <paramref name="saved"/> null the part is live and its modules' own fields say what's on;
        /// otherwise the part is the prefab and the saved module values (matched by name, in order) say it.
        /// </summary>
        private static void Read(Part part, uint id, string title, List<ProtoPartModuleSnapshot> saved, List<uint> siblings, PowerParts into, bool loaded)
        {
            var used = new HashSet<int>();
            HeatCore core = null;
            var coreModule = part.FindModuleImplementing<ModuleCoreHeat>();
            if (coreModule != null)
            {
                core = new HeatCore
                {
                    PartId = id, Title = title, MaxKw = coreModule.MaxCoolant, GoalK = coreModule.CoreTempGoal, ShutdownK = coreModule.CoreShutdownTemp,
                    CoreK = loaded && saved == null ? coreModule.CoreTemperature : -1,
                };
            }
            for (var i = 0; i < part.Modules.Count; i++)
            {
                var m = part.Modules[i];
                var values = SavedValues(saved, m.moduleName, used);
                var key = id + "/" + i;
                switch (m)
                {
                    case ModuleDeployableSolarPanel solar:
                    {
                        if (solar.resourceName != EC) break;
                        var state = saved == null ? solar.deployState : ParseEnum(values?.GetValue("deployState"), solar.deployState);
                        into.Items.Add(new PowerItem
                        {
                            Key = key, PartId = id, Title = title, Kind = PowerKind.Solar, Rate = solar.chargeRate * solar.efficiencyMult,
                            On = state != ModuleDeployablePart.DeployState.BROKEN && (state == ModuleDeployablePart.DeployState.EXTENDED || !solar.useAnimation),
                            Tracking = solar.isTracking, PowerCurve = solar.powerCurve, Now = loaded && saved == null ? solar.flowRate : -1,
                        });
                        break;
                    }
                    case ModuleGenerator gen:
                    {
                        var made = gen.resHandler.outputResources.Where(r => r.name == EC).Sum(r => r.rate);
                        var used2 = gen.resHandler.inputResources.Where(r => r.name == EC).Sum(r => r.rate);
                        var on = gen.isAlwaysActive || (saved == null ? gen.generatorIsActive : Bool(values, "generatorIsActive", gen.generatorIsActive));
                        if (made > 0) into.Items.Add(new PowerItem { Key = key, PartId = id, Title = title, Kind = PowerKind.Generator, Rate = made, On = on });
                        else if (used2 > 0) into.Items.Add(new PowerItem { Key = key, PartId = id, Title = title, Kind = PowerKind.Load, Rate = used2, On = on });
                        break;
                    }
                    case ModuleScienceConverter lab:
                        into.Items.Add(new PowerItem
                        {
                            Key = key, PartId = id, Title = title + " (research)", Kind = PowerKind.Load, Rate = lab.powerRequirement,
                            On = saved == null ? lab.IsActivated : Bool(values, "IsActivated", lab.IsActivated),
                        });
                        break;
                    case BaseConverter conv:
                    {
                        var on = conv.AlwaysActive || (saved == null ? conv.IsActivated : Bool(values, "IsActivated", conv.IsActivated));
                        var option = string.IsNullOrEmpty(conv.ConverterName) ? "" : Localize(conv.ConverterName);
                        var name = title + (option.Length == 0 ? "" : " (" + option + ")");
                        var group = id + "/converters";
                        var inEc = conv.inputList.Where(r => r.ResourceName == EC).Sum(r => r.Ratio);
                        var outEc = conv.outputList.Where(r => r.ResourceName == EC).Sum(r => r.Ratio);
                        if (outEc > 0) into.Items.Add(new PowerItem { Key = key, PartId = id, Title = name, Kind = PowerKind.FuelCell, Rate = outEc, On = on, Group = group, PartTitle = title, Option = option });
                        else if (inEc > 0) into.Items.Add(new PowerItem { Key = key, PartId = id, Title = name, Kind = PowerKind.Load, Rate = inEc, On = on, Group = group, PartTitle = title, Option = option });
                        if (core != null && conv.GeneratesHeat && conv.TemperatureModifier != null)
                        {
                            core.Converters.Add(new KeyValuePair<string, bool>(key, on));
                            core.ConverterKw.Add(conv.TemperatureModifier.Evaluate((float)core.GoalK) / HeatUnitsPerKw * coreModule.radiatorCoolingFactor);
                            if (on && core.CoreK >= 0 && conv.ThermalEfficiency != null)
                            {
                                var eff = conv.ThermalEfficiency.Evaluate((float)core.CoreK);
                                core.Efficiency = core.Efficiency < 0 ? eff : Math.Min(core.Efficiency, eff);
                            }
                        }
                        break;
                    }
                    case ModuleCommand cmd:
                    {
                        var rate = cmd.resHandler.inputResources.Where(r => r.name == EC).Sum(r => r.rate);
                        var hibernating = saved == null ? cmd.hibernation : Bool(values, "hibernation", false);
                        if (hibernating) rate *= cmd.hibernationMultiplier;
                        if (rate > 0) into.Items.Add(new PowerItem { Key = key, PartId = id, Title = title + (hibernating ? " (hibernating)" : ""), Kind = PowerKind.Load, Rate = rate, On = true });
                        break;
                    }
                    case ModuleLight light:
                        if (light.useResources && light.resourceAmount > 0)
                            into.Items.Add(new PowerItem
                            {
                                Key = key, PartId = id, Title = title + " (light)", Kind = PowerKind.Load, Rate = light.resourceAmount,
                                On = saved == null ? light.isOn : Bool(values, "isOn", light.isOn),
                            });
                        break;
                    case ModuleActiveRadiator rad:
                    {
                        var deploy = part.FindModuleImplementing<ModuleDeployableRadiator>();
                        var cooling = saved == null ? rad.IsCooling : Bool(values, "IsCooling", rad.IsCooling);
                        var extended = deploy == null || (saved == null ? deploy.deployState : ParseEnum(SavedValues(saved, deploy.moduleName, new HashSet<int>())?.GetValue("deployState"), deploy.deployState))
                            == ModuleDeployablePart.DeployState.EXTENDED;
                        var radiator = new Radiator
                        {
                            Key = key, PartId = id, Title = title, Kw = rad.maxEnergyTransfer / HeatUnitsPerKw, On = cooling && extended, ParentOnly = rad.parentCoolingOnly,
                        };
                        radiator.Reaches.AddRange(siblings);
                        into.Radiators.Add(radiator);
                        var ec = rad.resHandler.inputResources.Where(r => r.name == EC).Sum(r => r.rate);
                        if (ec > 0) into.Items.Add(new PowerItem { Key = key, PartId = id, Title = title + " (cooling)", Kind = PowerKind.Load, Rate = ec, On = radiator.On });
                        break;
                    }
                }
            }
            if (core != null && core.Converters.Count > 0)
            {
                core.NeedKw = NeedOf(core, k => core.Converters.First(c => c.Key == k).Value);
                into.Cores.Add(core);
            }
        }

        /// <summary>
        /// Cooling a core asks for at its goal temperature: every running converter adds its heat to the core
        /// (ModuleCoreHeat.ResolveConverterEnergy), so the running converters' heat added up. KSP's part info shows only the
        /// largest, one converter's worth.
        /// </summary>
        internal static double NeedOf(HeatCore core, Func<string, bool> on)
        {
            var need = 0.0;
            for (var i = 0; i < core.Converters.Count; i++)
                if (on(core.Converters[i].Key)) need += core.ConverterKw[i];
            return need;
        }

        private static ConfigNode SavedValues(List<ProtoPartModuleSnapshot> saved, string name, HashSet<int> used)
        {
            if (saved == null) return null;
            for (var i = 0; i < saved.Count; i++)
            {
                if (used.Contains(i) || saved[i].moduleName != name) continue;
                used.Add(i);
                return saved[i].moduleValues;
            }
            return null;
        }

        private static bool Bool(ConfigNode values, string key, bool fallback)
        {
            var s = values?.GetValue(key);
            return s != null && bool.TryParse(s, out var b) ? b : fallback;
        }

        private static T ParseEnum<T>(string s, T fallback) where T : struct =>
            s != null && Enum.TryParse(s, true, out T v) ? v : fallback;

        private static string Localize(string s)
        {
            try { return KSP.Localization.Localizer.Format(s); }
            catch (Exception) { return s; }
        }

        internal static string Num(double x, string format = "0.##") => x.ToString(format, CultureInfo.InvariantCulture);
    }
}
