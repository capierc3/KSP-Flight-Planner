using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Traffic
{
    /// <summary>
    /// Docking and undocking on rails: for ships that aren't loaded (the player is elsewhere, or in the tracking station),
    /// done on their saved records, as KSP itself would leave them. Undocking cuts the craft's branch of parts out of the
    /// stack's record into a record of its own, re-rooted at its own root part; docking hangs the arriving ship's parts off
    /// a free port on the station, its port turned to face the station's. Both ports are set free, or docked to each
    /// other, as KSP's own docking does. A backup of the game is saved first ("persistent-before-rails-dock").
    /// docs/MATH.md §11.10.
    /// </summary>
    internal static class RailsDocking
    {
        private const string BackupName = "persistent-before-rails-dock";

        /// <summary>Save a backup of the game before each split or merge. Off: each one is a full save (a hitch), and a
        /// fleet docking all day would only overwrite it. A debugging aid.</summary>
        internal static bool KeepBackups = false;

        /// <summary>One PART node of a vessel record, with what docking needs read out of it.</summary>
        private sealed class P
        {
            public ConfigNode Node;
            public int Parent;
            public uint Uid;
            public Vector3 Pos;
            public Quaternion Rot;
            public ConfigNode Dock; // its ModuleDockingNode MODULE, if it has one
            public uint Pid;
            public string Name;
        }

        private static ConfigNode Record(Vessel v)
        {
            var node = new ConfigNode("VESSEL");
            v.protoVessel.Save(node);
            return node;
        }

        private static List<P> Parts(ConfigNode vessel) =>
            vessel.GetNodes("PART").Select(n => new P
            {
                Node = n,
                Name = n.GetValue("name"),
                Parent = int.Parse(n.GetValue("parent"), CultureInfo.InvariantCulture),
                Uid = uint.Parse(n.GetValue("uid"), CultureInfo.InvariantCulture),
                Pid = uint.TryParse(n.GetValue("persistentId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : 0,
                Pos = ConfigNode.ParseVector3(n.GetValue("position")),
                Rot = ConfigNode.ParseQuaternion(n.GetValue("rotation")),
                Dock = n.GetNodes("MODULE").FirstOrDefault(m => m.GetValue("name") == "ModuleDockingNode"),
            }).ToList();

        private static bool Docked(P p) => p.Dock != null && (p.Dock.GetValue("state") ?? "").StartsWith("Docked", StringComparison.Ordinal);
        private static bool IsFree(P p) => p.Dock != null && (p.Dock.GetValue("state") ?? "") == "Ready";
        private static string DockedName(P p) => p.Dock?.GetNode("DOCKEDVESSEL")?.GetValue("vesselName");

        /// <summary>An unloaded vessel's free docking ports, from its saved parts: each part's persistent id and part name.</summary>
        internal static List<KeyValuePair<uint, string>> FreePorts(Vessel v)
        {
            var list = new List<KeyValuePair<uint, string>>();
            if (v?.protoVessel == null) return list;
            foreach (var pp in v.protoVessel.protoPartSnapshots)
                if (pp.modules.Any(m => m.moduleName == "ModuleDockingNode" && m.moduleValues != null && (m.moduleValues.GetValue("state") ?? "") == "Ready"))
                    list.Add(new KeyValuePair<uint, string>(pp.persistentId, pp.partName));
            return list;
        }

        /// <summary>The part name of the port a craft of this name is docked by, in an unloaded stack (null if none).</summary>
        internal static string CraftPortPart(Vessel stack, string craftName)
        {
            if (stack?.protoVessel == null) return null;
            foreach (var pp in stack.protoVessel.protoPartSnapshots)
            foreach (var m in pp.modules)
            {
                if (m.moduleName != "ModuleDockingNode" || m.moduleValues == null) continue;
                var state = m.moduleValues.GetValue("state") ?? "";
                if (state.StartsWith("Docked", StringComparison.Ordinal) && m.moduleValues.GetNode("DOCKEDVESSEL")?.GetValue("vesselName") == craftName) return pp.partName;
            }
            return null;
        }

        /// <summary>The role (<see cref="ModuleTrafficRole"/>) of a craft of this name docked in an unloaded stack, from its own parts.</summary>
        internal static string CraftRole(Vessel stack, string craftName)
        {
            var parts = Parts(Record(stack));
            var h = parts.FindIndex(p => Docked(p) && DockedName(p) == craftName);
            if (h < 0) return ModuleTrafficRole.CrewSupplies;
            var roles = Branch(parts, h).SelectMany(i => parts[i].Node.GetNodes("MODULE")).Where(m => m.GetValue("name") == nameof(ModuleTrafficRole)).Select(m => m.GetValue("role"));
            return roles.Any(r => r == ModuleTrafficRole.Tanker) ? ModuleTrafficRole.Tanker : ModuleTrafficRole.CrewSupplies;
        }

        /// <summary>The kerbals aboard a craft of this name docked in an unloaded stack, from its own parts' records.</summary>
        internal static List<string> CraftCrew(Vessel stack, string craftName)
        {
            var parts = Parts(Record(stack));
            var h = parts.FindIndex(p => Docked(p) && DockedName(p) == craftName);
            return h < 0 ? new List<string>() : Branch(parts, h).SelectMany(i => parts[i].Node.GetValues("crew")).ToList();
        }

        /// <summary>The unloaded stack a craft of this name is docked in (as its port remembers it), or null.</summary>
        internal static Vessel FindDockedIn(string craftName)
        {
            foreach (var v in FlightGlobals.Vessels)
            {
                if (v == null || v.loaded || v.protoVessel == null) continue;
                foreach (var pp in v.protoVessel.protoPartSnapshots)
                foreach (var m in pp.modules)
                {
                    if (m.moduleName != "ModuleDockingNode" || m.moduleValues == null) continue;
                    var state = m.moduleValues.GetValue("state") ?? "";
                    if (state.StartsWith("Docked", StringComparison.Ordinal) && m.moduleValues.GetNode("DOCKEDVESSEL")?.GetValue("vesselName") == craftName) return v;
                }
            }
            return null;
        }

        /// <summary>
        /// Undocks the craft called <paramref name="craftName"/> from an unloaded stack, on rails. The craft is put on the
        /// stack's orbit 60 m ahead of it. Returns the craft's new vessel; null, with <paramref name="problem"/>, when it can't.
        /// </summary>
        internal static Vessel Undock(Vessel stack, string craftName, out string problem)
        {
            problem = null;
            try
            {
                var record = Record(stack);
                var parts = Parts(record);
                var h = parts.FindIndex(p => Docked(p) && DockedName(p) == craftName);
                if (h < 0) { problem = craftName + " isn't docked in " + stack.vesselName + "."; return null; }
                var otherUid = uint.Parse(parts[h].Dock.GetValue("dockUId"), CultureInfo.InvariantCulture);
                var o = parts.FindIndex(p => p.Uid == otherUid);
                if (o < 0) { problem = "the port it's docked to isn't in " + stack.vesselName + "."; return null; }
                if (parts[h].Parent != o) { problem = craftName + " holds " + stack.vesselName + "'s root part: undock it by hand once."; return null; }

                // The craft: the branch hanging off its port, re-rooted at its own root part.
                var branch = Branch(parts, h);
                var rootUid = uint.TryParse(parts[h].Dock.GetNode("DOCKEDVESSEL")?.GetValue("rootUId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ru) ? ru : parts[h].Uid;
                var r = branch.FirstOrDefault(i => parts[i].Uid == rootUid);
                if (!branch.Contains(r)) r = h;
                var parent = branch.ToDictionary(i => i, i => i == h ? h : parts[i].Parent);
                Reroot(parent, r);

                SaveBackup();
                var craftType = parts[h].Dock.GetNode("DOCKEDVESSEL")?.GetValue("vesselType") ?? "Ship";
                SetFree(parts[h]);
                SetFree(parts[o]);
                var craft = Build(record, parts, branch, parent, r, craftName, craftType, newIdentity: true);
                AheadOnOrbit(craft, UndockAheadM);
                ReadyToFly(craft);
                var stays = Enumerable.Range(0, parts.Count).Where(i => !branch.Contains(i)).ToList();
                var stationRoot = int.Parse(record.GetValue("root"), CultureInfo.InvariantCulture);
                var stationParent = stays.ToDictionary(i => i, i => parts[i].Parent);
                var station = Build(record, parts, stays, stationParent, stationRoot, stack.vesselName, record.GetValue("type"), newIdentity: false);

                if ((Check(craft) ?? Check(station)) is { } bad) { problem = "the split came out wrong (" + bad + "), so it was left docked."; return null; }
                Replace(new[] { stack }, new[] { station, craft }, out var made);
                return made.LastOrDefault();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                problem = e.Message;
                return null;
            }
        }

        /// <summary>
        /// Docks an unloaded ship to an unloaded station, on rails: a free port on the ship to a free port on the station that
        /// it fits (not one in <paramref name="claimed"/>, claimed by another route craft), the ship's port turned to face the station's,
        /// its parts hung off the station's port. Returns the merged vessel; null, with <paramref name="problem"/>, when it can't.
        /// </summary>
        internal static Vessel Dock(Vessel ship, Vessel station, out string problem, ICollection<uint> claimed = null, uint prefer = 0)
        {
            problem = null;
            try
            {
                var sRec = Record(station);
                var hRec = Record(ship);
                var sp = Parts(sRec);
                var hp = Parts(hRec);
                int s = -1, h = -1;
                // The booked port first (docs/MATH.md §11.14), then the rest in order.
                foreach (var i in Enumerable.Range(0, sp.Count).Where(i => IsFree(sp[i]) && (claimed == null || !claimed.Contains(sp[i].Pid))).OrderBy(i => prefer != 0 && sp[i].Pid == prefer ? 0 : 1))
                {
                    h = Enumerable.Range(0, hp.Count).Where(j => IsFree(hp[j]) && Fits(sp[i].Name, hp[j].Name)).DefaultIfEmpty(-1).First();
                    if (h >= 0) { s = i; break; }
                }
                if (s < 0) { problem = station.vesselName + " has no free port that a free port on " + ship.vesselName + " fits."; return null; }
                if (!PortPose(sp[s].Name, out var sLocalPos, out var sLocalRot) || !PortPose(hp[h].Name, out var hLocalPos, out var hLocalRot))
                {
                    problem = "couldn't read the ports' shapes."; return null;
                }

                // Where the ports' faces are, each in its own ship's frame (the root part's), and the move that puts the
                // ship's port face to face with the station's: forward against forward, up along up.
                var sPos = sp[s].Pos + sp[s].Rot * sLocalPos;
                var sRot = sp[s].Rot * sLocalRot;
                var hPos = hp[h].Pos + hp[h].Rot * hLocalPos;
                var hRot = hp[h].Rot * hLocalRot;
                var want = Quaternion.LookRotation(-(sRot * Vector3.forward), sRot * Vector3.up);
                var turn = want * Quaternion.Inverse(hRot);
                var shift = sPos - turn * hPos;

                // The ship's tree, re-rooted at its port, which hangs off the station's port.
                var hRoot = int.Parse(hRec.GetValue("root"), CultureInfo.InvariantCulture);
                var hParent = Enumerable.Range(0, hp.Count).ToDictionary(i => i, i => hp[i].Parent);
                hParent[hRoot] = hRoot;
                Reroot(hParent, h);

                SaveBackup();
                // KSP loads parts in order and wants each part's parent loaded first, so the ship's parts go in tree
                // order from its port.
                var order = TreeOrder(hParent, h);
                var map = order.Select((old, idx) => (old, idx)).ToDictionary(x => x.old, x => x.idx + sp.Count);
                foreach (var i in order)
                {
                    var n = hp[i].Node;
                    n.SetValue("position", ConfigNode.WriteVector(shift + turn * hp[i].Pos));
                    n.SetValue("rotation", ConfigNode.WriteQuaternion(turn * hp[i].Rot));
                    n.SetValue("parent", (i == h ? s : map[hParent[i]]).ToString(CultureInfo.InvariantCulture));
                    Renumber(n, j => map.TryGetValue(j, out var to) ? to : -1);
                }
                DockTo(hp[h], sp[s], "Docked (docker)", ship.vesselName, hRec.GetValue("type"), hp[hRoot].Uid);
                DockTo(sp[s], hp[h], "Docked (dockee)", station.vesselName, sRec.GetValue("type"),
                    sp[int.Parse(sRec.GetValue("root"), CultureInfo.InvariantCulture)].Uid);

                // Docked: the ship's engines off and its RCS blocks switched off, as the physics flyer leaves them.
                foreach (var p in hp)
                foreach (var m in p.Node.GetNodes("MODULE"))
                {
                    var kind = m.GetValue("name") ?? "";
                    if (kind.StartsWith("ModuleEngines", StringComparison.Ordinal) && m.HasValue("EngineIgnited")) m.SetValue("EngineIgnited", "False");
                    if (kind.StartsWith("ModuleRCS", StringComparison.Ordinal) && m.HasValue("rcsEnabled")) m.SetValue("rcsEnabled", "False");
                }
                var merged = new ConfigNode("VESSEL");
                sRec.CopyTo(merged);
                foreach (var i in order) merged.AddNode(hp[i].Node);
                if (Check(merged) is { } bad) { problem = "the merge came out wrong (" + bad + "), so it wasn't docked."; return null; }
                Replace(new[] { station, ship }, new[] { merged }, out var made);
                return made.FirstOrDefault();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                problem = e.Message;
                return null;
            }
        }

        // ---------------- the parts tree ----------------

        private static List<int> Branch(List<P> parts, int top)
        {
            var list = new List<int> { top };
            for (var k = 0; k < list.Count; k++)
                for (var i = 0; i < parts.Count; i++)
                    if (i != list[k] && parts[i].Parent == list[k] && !list.Contains(i) && i != top) list.Add(i);
            return list;
        }

        /// <summary>Turns the tree so <paramref name="root"/> is its root: the parent links on the way up from it are reversed.</summary>
        private static void Reroot(Dictionary<int, int> parent, int root)
        {
            var path = new List<int> { root };
            while (parent[path[path.Count - 1]] != path[path.Count - 1] && parent.ContainsKey(parent[path[path.Count - 1]]))
            {
                path.Add(parent[path[path.Count - 1]]);
                if (path.Count > parent.Count) break;
            }
            for (var k = path.Count - 1; k > 0; k--) parent[path[k]] = path[k - 1];
            parent[root] = root;
        }

        /// <summary>
        /// The parts from <paramref name="root"/> down, each after its parent (the order KSP loads them in). Parts not
        /// reached from the root go last, in their old order.
        /// </summary>
        private static List<int> TreeOrder(Dictionary<int, int> parent, int root)
        {
            var order = new List<int> { root };
            var seen = new HashSet<int> { root };
            var children = parent.Keys.OrderBy(i => i).Where(i => i != root && parent[i] != i)
                .GroupBy(i => parent[i]).ToDictionary(g => g.Key, g => g.ToList());
            for (var k = 0; k < order.Count; k++)
                if (children.TryGetValue(order[k], out var kids))
                    foreach (var c in kids)
                        if (seen.Add(c)) order.Add(c);
            order.AddRange(parent.Keys.OrderBy(i => i).Where(i => seen.Add(i)));
            return order;
        }

        /// <summary>
        /// A vessel record of some of the parts: re-numbered from 0 (the root first), positions re-expressed from the root,
        /// attach-node links to parts left behind cut.
        /// </summary>
        private static ConfigNode Build(ConfigNode from, List<P> parts, List<int> keep, Dictionary<int, int> parent, int root, string name, string type, bool newIdentity)
        {
            var order = TreeOrder(keep.ToDictionary(i => i, i => parent[i]), root);
            var map = order.Select((old, idx) => (old, idx)).ToDictionary(x => x.old, x => x.idx);
            var inv = Quaternion.Inverse(parts[root].Rot);
            var rootPos = parts[root].Pos;

            var v = new ConfigNode("VESSEL");
            from.CopyTo(v);
            v.RemoveNodes("PART");
            foreach (var old in order)
            {
                var n = parts[old].Node.CreateCopy();
                n.SetValue("parent", map[parent[old]].ToString(CultureInfo.InvariantCulture));
                n.SetValue("position", ConfigNode.WriteVector(inv * (parts[old].Pos - rootPos)));
                n.SetValue("rotation", ConfigNode.WriteQuaternion(inv * parts[old].Rot));
                Renumber(n, j => map.TryGetValue(j, out var to) ? to : -1);
                v.AddNode(n);
            }
            v.SetValue("root", "0");
            v.SetValue("name", name);
            if (type != null) v.SetValue("type", type);
            if (newIdentity)
            {
                v.SetValue("pid", Guid.NewGuid().ToString("N"));
                v.SetValue("persistentId", FlightGlobals.GetUniquepersistentId().ToString(CultureInfo.InvariantCulture));
                v.SetValue("ref", parts[root].Uid.ToString(CultureInfo.InvariantCulture));
                if (v.HasValue("rot")) v.SetValue("rot", ConfigNode.WriteQuaternion(ConfigNode.ParseQuaternion(from.GetValue("rot")) * parts[root].Rot));
            }
            return v;
        }

        /// <summary>
        /// Just undocked on rails: the record's engines lit at zero throttle and its RCS thrusters and RCS switched on, as
        /// <see cref="Ships.ReadyToFly"/> does for a loaded ship. Thrust limiters are left as they are.
        /// </summary>
        private static void ReadyToFly(ConfigNode vessel)
        {
            foreach (var part in vessel.GetNodes("PART"))
            foreach (var m in part.GetNodes("MODULE"))
            {
                var name = m.GetValue("name") ?? "";
                if (name == "ModuleEngines" || name == "ModuleEnginesFX")
                {
                    m.SetValue("staged", "True", true);
                    m.SetValue("EngineIgnited", "True", true);
                    m.SetValue("engineShutdown", "False", true);
                    m.SetValue("flameout", "False", true);
                    m.SetValue("currentThrottle", "0", true);
                }
                else if (name == "ModuleRCS" || name == "ModuleRCSFX")
                    m.SetValue("rcsEnabled", "True", true);
            }
            vessel.GetNode("CTRLSTATE")?.SetValue("mainThrottle", "0", true);
            var groups = vessel.GetNode("ACTIONGROUPS");
            if (groups != null && groups.GetValue("RCS") is { } rcs)
            {
                var comma = rcs.IndexOf(',');
                groups.SetValue("RCS", "True" + (comma >= 0 ? rcs.Substring(comma) : ""));
            }
        }

        /// <summary>
        /// Is a vessel record one KSP can load? Each part after its parent, links by number to parts that exist, symmetry
        /// partners of the same kind, docked ports' partners on board. Null when fine, else what's wrong. Checked before any
        /// split or merge replaces the game's vessels, so a bad one never reaches the save.
        /// </summary>
        private static string Check(ConfigNode vessel)
        {
            var parts = vessel.GetNodes("PART");
            var n = parts.Length;
            var uids = new HashSet<string>(parts.Select(p => p.GetValue("uid")));
            for (var i = 0; i < n; i++)
            {
                var p = parts[i];
                var name = p.GetValue("name");
                if (!int.TryParse(p.GetValue("parent"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parent) || (i > 0 ? parent >= i || parent < 0 : parent != 0))
                    return "part " + i + " " + name + " comes before its parent " + p.GetValue("parent");
                foreach (var key in new[] { "attN", "srfN" })
                foreach (var value in p.GetValues(key))
                {
                    var bits = value.Split(',');
                    if (bits.Length >= 2 && int.TryParse(bits[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && (idx < -1 || idx >= n))
                        return "part " + i + " " + name + " is attached to part " + idx + " of " + n;
                }
                foreach (var value in p.GetValues("sym"))
                    if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) || s < 0 || s >= n || parts[s].GetValue("name") != name)
                        return "part " + i + " " + name + " has symmetry partner " + value + " that isn't one";
                foreach (var m in p.GetNodes("MODULE"))
                    if (m.GetValue("name") == "ModuleDockingNode" && (m.GetValue("state") ?? "").StartsWith("Docked", StringComparison.Ordinal)
                        && !uids.Contains(m.GetValue("dockUId")))
                        return "part " + i + " " + name + " is docked to a port that isn't aboard";
            }
            return null;
        }

        /// <summary>Re-numbers a part's links to other parts by number: attach nodes ("attN = top, 5", "srfN = srfAttach, 3";
        /// -1, none, stays) and symmetry partners ("sym = 7").</summary>
        private static void Renumber(ConfigNode part, Func<int, int> map)
        {
            foreach (var key in new[] { "attN", "srfN" })
            {
                var values = part.GetValues(key);
                if (values.Length == 0) continue;
                part.RemoveValues(key);
                foreach (var value in values)
                {
                    var bits = value.Split(',');
                    if (bits.Length >= 2 && int.TryParse(bits[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0)
                        part.AddValue(key, bits[0].Trim() + ", " + map(idx).ToString(CultureInfo.InvariantCulture));
                    else part.AddValue(key, value);
                }
            }
            // Symmetry partners ("sym = 132"): part numbers too. A partner left in the other vessel is dropped.
            var syms = part.GetValues("sym");
            if (syms.Length == 0) return;
            part.RemoveValues("sym");
            foreach (var value in syms)
                if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0 && map(idx) is var to && to >= 0)
                    part.AddValue("sym", to.ToString(CultureInfo.InvariantCulture));
        }

        // ---------------- ports ----------------

        private static void SetFree(P p)
        {
            p.Dock.SetValue("state", "Ready");
            p.Dock.SetValue("dockUId", "0");
        }

        private static void DockTo(P mine, P theirs, string state, string vesselName, string vesselType, uint rootUid)
        {
            mine.Dock.SetValue("state", state);
            mine.Dock.SetValue("dockUId", theirs.Uid.ToString(CultureInfo.InvariantCulture));
            var info = mine.Dock.GetNode("DOCKEDVESSEL") ?? mine.Dock.AddNode("DOCKEDVESSEL");
            info.SetValue("vesselName", vesselName, true);
            info.SetValue("vesselType", vesselType ?? "Ship", true);
            info.SetValue("rootUId", rootUid.ToString(CultureInfo.InvariantCulture), true);
        }

        private static ModuleDockingNode Prefab(string partName) =>
            PartLoader.getPartInfoByName(partName)?.partPrefab?.FindModuleImplementing<ModuleDockingNode>();

        /// <summary>The two ports fit (same size), by their part designs.</summary>
        internal static bool Fits(string a, string b)
        {
            var pa = Prefab(a);
            var pb = Prefab(b);
            if (pa == null || pb == null) return false;
            if (pa.nodeTypes != null && pb.nodeTypes != null && pa.nodeTypes.Count > 0) return pa.nodeTypes.Overlaps(pb.nodeTypes);
            return pa.nodeType == pb.nodeType;
        }

        /// <summary>Where a port's face is and which way it faces, relative to its part, from the part design.</summary>
        private static bool PortPose(string partName, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            var module = Prefab(partName);
            if (module == null) return false;
            var part = module.part.transform;
            var face = module.nodeTransform != null ? module.nodeTransform : module.part.FindModelTransform(module.nodeTransformName);
            if (face == null) return false;
            pos = part.InverseTransformPoint(face.position);
            rot = Quaternion.Inverse(part.rotation) * face.rotation;
            return true;
        }

        // ---------------- the game ----------------

        /// <summary>How far ahead of the station (m) a craft undocked on rails is put, on the station's own orbit.</summary>
        internal const double UndockAheadM = 60;

        /// <summary>
        /// How much sooner (s) a craft put <see cref="UndockAheadM"/> ahead on this orbit reaches each point than the
        /// station would: the mean anomaly it was moved on by (n · 60 m / v), over the mean motion n. 0 when it wasn't moved.
        /// </summary>
        internal static double UndockLeadS(Orbit o) =>
            o == null || o.semiMajorAxis <= 0 ? 0 : UndockAheadM / Math.Sqrt(o.referenceBody.gravParameter / o.semiMajorAxis);

        /// <summary>Puts the record's orbit this far ahead along it (same orbit, mean anomaly moved on).</summary>
        private static void AheadOnOrbit(ConfigNode vessel, double metres)
        {
            var o = vessel.GetNode("ORBIT");
            if (o == null) return;
            var sma = double.Parse(o.GetValue("SMA"), CultureInfo.InvariantCulture);
            var body = FlightGlobals.Bodies[int.Parse(o.GetValue("REF"), CultureInfo.InvariantCulture)];
            if (sma <= 0) return;
            var n = Math.Sqrt(body.gravParameter / (sma * sma * sma));
            var speed = Math.Sqrt(body.gravParameter / sma);
            var mna = double.Parse(o.GetValue("MNA"), CultureInfo.InvariantCulture) + n * metres / speed;
            o.SetValue("MNA", mna.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>Saves the game as it is now under the backup name, so a bad split or merge can be undone by loading it.</summary>
        private static void SaveBackup()
        {
            if (!KeepBackups) return;
            try { GamePersistence.SaveGame(BackupName, HighLogic.SaveFolder, SaveMode.OVERWRITE); }
            catch (Exception e) { Debug.LogWarning("[KspFlightPlanner] Couldn't save the backup before rails docking: " + e.Message); }
        }

        /// <summary>
        /// Swaps vessels for new ones built from records: the old ones are taken out of the game without being destroyed
        /// the usual way (that would count their crew as lost), the new ones loaded as KSP loads a save's vessels.
        /// </summary>
        private static void Replace(IEnumerable<Vessel> old, IEnumerable<ConfigNode> records, out List<Vessel> made)
        {
            var game = HighLogic.CurrentGame;
            foreach (var v in old)
            {
                game.flightState.protoVessels.Remove(v.protoVessel);
                GameEvents.onVesselDestroy.Fire(v);
                FlightGlobals.RemoveVessel(v);
                UnityEngine.Object.Destroy(v.gameObject);
            }
            made = new List<Vessel>();
            foreach (var record in records)
            {
                var pv = new ProtoVessel(record, game);
                game.flightState.protoVessels.Add(pv);
                pv.Load(game.flightState);
                if (pv.vesselRef != null)
                {
                    made.Add(pv.vesselRef);
                    GameEvents.onNewVesselCreated.Fire(pv.vesselRef);
                }
            }
        }
    }
}
