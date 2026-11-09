using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace KspFlightPlanner.KerbinResources
{
    /// <summary>What Kerbin Resources keeps on a kerbal beyond KSP's own roster: made up once, when it first sees them.</summary>
    internal sealed class KerbalRecord
    {
        public string Name = "";
        public string LastName = "";

        /// <summary>Born (game time; before 0 is before the company's founding).</summary>
        public double BirthUt;

        /// <summary>Joined the company (game time); NaN while still an applicant.</summary>
        public double HiredUt = double.NaN;

        /// <summary>"City, Nation", from KerbinResources.cfg.</summary>
        public string Birthplace = "";
        public string CallSign = "";

        /// <summary>Has had their name set (a new last name, or left as it was): names don't change after this.</summary>
        public bool Named;

        internal void Save(ConfigNode n)
        {
            n.AddValue("name", Name);
            n.AddValue("lastName", LastName);
            n.AddValue("birth", BirthUt.ToString("R", CultureInfo.InvariantCulture));
            n.AddValue("hired", HiredUt.ToString("R", CultureInfo.InvariantCulture));
            n.AddValue("birthplace", Birthplace);
            n.AddValue("callSign", CallSign);
            n.AddValue("named", Named);
        }

        internal static KerbalRecord Load(ConfigNode n) => new KerbalRecord
        {
            Name = n.GetValue("name") ?? "",
            LastName = n.GetValue("lastName") ?? "",
            BirthUt = Flight.Num(n, "birth"),
            HiredUt = double.TryParse(n.GetValue("hired"), NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : double.NaN,
            Birthplace = n.GetValue("birthplace") ?? "",
            CallSign = n.GetValue("callSign") ?? "",
            Named = n.GetValue("named") == "True",
        };
    }

    /// <summary>
    /// Kerbin Resources' personnel files: a record per kerbal (birthday, hire date, birthplace, call sign) and their last
    /// names. A kerbal is named once, when created, and keeps that name: their KSP first name, and a last name from
    /// KerbinResources.cfg in place of Kerman, sometimes one an existing kerbal already has (family). The stock four keep
    /// their first names too. Tourists and kerbals waiting to be rescued are left as KSP made them, since contracts know
    /// them by name. docs/KERBIN-RESOURCES.md.
    /// </summary>
    internal sealed class Personnel
    {
        private static readonly System.Random Rng = new System.Random();
        private readonly Dictionary<string, KerbalRecord> _records = new Dictionary<string, KerbalRecord>();

        internal KerbalRecord Of(string kerbal) => kerbal != null && _records.TryGetValue(kerbal, out var r) ? r : null;

        internal IEnumerable<KerbalRecord> Records => _records.Values;

        private static KerbinResourcesSettings Settings => KerbinResourcesSettings.Current;
        private static KerbinResourcesConfig Config => KerbinResourcesConfig.Current;

        /// <summary>Kerbals KSP creates as company crew (hired or applying): the ones given a new last name.</summary>
        private static bool Renamable(ProtoCrewMember k) =>
            k.type == ProtoCrewMember.KerbalType.Crew || k.type == ProtoCrewMember.KerbalType.Applicant;

        /// <summary>
        /// A record for every kerbal on the roster that hasn't got one, and a last name for any crew not named yet (only
        /// when <paramref name="mayRename"/>: not in flight, where a kerbal's name shows in many places at once).
        /// Kerbals already hired when Kerbin Resources first sees them get a made-up hire date since the founding; the
        /// stock four (veterans) were there from day one.
        /// </summary>
        internal void LookOver(double now, bool mayRename)
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return;
            foreach (var k in roster.Crew.Concat(roster.Applicants).Concat(roster.Tourist).ToList())
            {
                if (k == null) continue;
                var r = Of(k.name);
                if (r == null)
                {
                    r = NewRecord(k, now);
                    if (k.type == ProtoCrewMember.KerbalType.Crew) r.HiredUt = k.veteran ? 0 : Math.Floor(Rng.NextDouble() * Math.Max(0, now) / Calendar.Day) * Calendar.Day;
                }
                if (!r.Named && mayRename) Name(k, r);
            }
        }

        /// <summary>A kerbal KSP has just created (an applicant, a rescued kerbal, a tourist): named and given a record.</summary>
        internal void Created(ProtoCrewMember k, double now)
        {
            if (k == null || Of(k.name) != null) return;
            var r = NewRecord(k, now);
            if (k.type == ProtoCrewMember.KerbalType.Crew) r.HiredUt = now;
            Name(k, r);
        }

        /// <summary>An applicant was hired (or a rescued kerbal joined): hire date today, and a call sign if a pilot.</summary>
        internal void TypeChanged(ProtoCrewMember k, ProtoCrewMember.KerbalType from, ProtoCrewMember.KerbalType to, double now)
        {
            if (k == null || to != ProtoCrewMember.KerbalType.Crew || from == ProtoCrewMember.KerbalType.Crew) return;
            var r = Of(k.name) ?? NewRecord(k, now);
            if (double.IsNaN(r.HiredUt)) r.HiredUt = now;
            GiveCallSign(k, r);
        }

        /// <summary>KSP renamed a kerbal (ours, or the player in the Astronaut Complex): the record follows.</summary>
        internal void Renamed(string from, string to)
        {
            if (from == to || !_records.TryGetValue(from, out var r)) return;
            _records.Remove(from);
            r.Name = to;
            _records[to] = r;
        }

        private KerbalRecord NewRecord(ProtoCrewMember k, double now)
        {
            var s = Settings;
            // The stock four (veterans) founded the company: older than the kerbals hired since.
            var low = k.veteran ? s.FounderMinAge : s.MinAge;
            var high = k.veteran ? s.FounderMaxAge : s.MaxAge;
            var youngest = Math.Min(low, high);
            var oldest = Math.Max(low, high);
            var age = youngest + Rng.Next(oldest - youngest + 1);
            var birth = now - (age + Rng.NextDouble()) * Calendar.Year;
            var r = new KerbalRecord
            {
                Name = k.name,
                LastName = LastNameOf(k.name),
                BirthUt = Math.Floor(birth / Calendar.Day) * Calendar.Day,
                Birthplace = PickBirthplace(),
            };
            _records[k.name] = r;
            GiveCallSign(k, r);
            return r;
        }

        /// <summary>Sets a kerbal's name for good: their first name, and a new last name (or a family's) if they're company crew.</summary>
        private void Name(ProtoCrewMember k, KerbalRecord r)
        {
            r.Named = true;
            if (!Settings.NewLastNames || !Renamable(k) || LastNameOf(k.name) != "Kerman") return;
            if (!RenameInPlace.Works) return;
            var first = FirstNameOf(k.name);
            var roster = HighLogic.CurrentGame.CrewRoster;
            foreach (var last in LastNameChoices())
            {
                var name = first + " " + last;
                if (roster.Exists(name)) continue;
                var old = k.name;
                if (!RenameInPlace.Rename(k, name)) return;
                // The rename event moves the record and the flights; done here too in case the event didn't come.
                Renamed(old, name);
                KerbinResourcesScenario.Instance?.Log.RenameKerbal(old, name);
                KerbinResourcesScenario.Instance?.Duty.RenameKerbal(old, name);
                KerbinResourcesScenario.Instance?.Control.RenameKerbal(old, name);
                r.LastName = last;
                Debug.Log("[KspFlightPlanner] Kerbin Resources: " + old + " is now " + name + ".");
                return;
            }
        }

        /// <summary>
        /// Last names to try, best first: by the family chance, an existing family's (one with room); otherwise the
        /// config's names, unused ones first, in a random order.
        /// </summary>
        private IEnumerable<string> LastNameChoices()
        {
            var s = Settings;
            var families = _records.Values.Where(r => r.Named && r.LastName.Length > 0 && r.LastName != "Kerman")
                                   .GroupBy(r => r.LastName).Where(g => g.Count() < s.MaxFamily).Select(g => g.Key).ToList();
            if (families.Count > 0 && Rng.Next(100) < s.FamilyChance) yield return families[Rng.Next(families.Count)];
            var used = new HashSet<string>(_records.Values.Select(r => r.LastName));
            foreach (var last in Config.LastNames.OrderBy(n => used.Contains(n) ? 1 : 0).ThenBy(_ => Rng.Next())) yield return last;
        }

        private void GiveCallSign(ProtoCrewMember k, KerbalRecord r)
        {
            if (!Settings.CallSigns || r.CallSign.Length > 0 || k.trait != "Pilot" || k.type != ProtoCrewMember.KerbalType.Crew && k.type != ProtoCrewMember.KerbalType.Applicant) return;
            var taken = new HashSet<string>(_records.Values.Select(x => x.CallSign));
            var free = Config.CallSigns.Where(c => !taken.Contains(c)).ToList();
            if (free.Count > 0) r.CallSign = free[Rng.Next(free.Count)];
        }

        /// <summary>A random birthplace from the config's list ("" with none).</summary>
        private static string PickBirthplace()
        {
            var places = Config.Birthplaces;
            return places.Count == 0 ? "" : places[Rng.Next(places.Count)];
        }

        /// <summary>The kerbals sharing this one's last name (their family), not counting them.</summary>
        internal IEnumerable<KerbalRecord> FamilyOf(KerbalRecord r) =>
            r == null || r.LastName.Length == 0 || r.LastName == "Kerman" ? Enumerable.Empty<KerbalRecord>() : _records.Values.Where(x => x != r && x.LastName == r.LastName);

        internal static string LastNameOf(string name)
        {
            var space = name.LastIndexOf(' ');
            return space < 0 ? "" : name.Substring(space + 1);
        }

        internal static string FirstNameOf(string name)
        {
            var space = name.LastIndexOf(' ');
            return space < 0 ? name : name.Substring(0, space);
        }

        internal void Save(ConfigNode node)
        {
            foreach (var r in _records.Values) r.Save(node.AddNode("KERBAL"));
        }

        internal void Load(ConfigNode node)
        {
            _records.Clear();
            if (node == null) return;
            foreach (var n in node.GetNodes("KERBAL"))
            {
                var r = KerbalRecord.Load(n);
                if (r.Name.Length > 0) _records[r.Name] = r;
            }
        }
    }

    /// <summary>
    /// Renames a kerbal everywhere KSP keeps the name. A craft's seats remember their crew by name
    /// (<c>ProtoPartSnapshot.protoCrewNames</c>), and KSP's own rename (<c>ProtoCrewMember.ChangeName</c>) doesn't touch
    /// them. When KSP next checks the roster (<c>KerbalRoster.ValidateAssignments</c>, on every save) it can't find the old
    /// name, takes the seat as empty and deletes the kerbal: crew vanish from their stations and the save won't load (seen
    /// twice on 2026-11-09). So this renames the seats in every craft too. It also swaps the name where the kerbal sits in
    /// the roster's list, rather than KSP's remove-and-add-at-the-end, so the roster's order doesn't change, and sends
    /// KSP's rename events as its rename does. A kerbal not in the roster yet (KSP announces a new one just before adding
    /// it) only has its name set: KSP adds it under that name, and it isn't in any seat yet.
    /// </summary>
    internal static class RenameInPlace
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo NameField = typeof(ProtoCrewMember).GetField("_name", Any);
        private static readonly FieldInfo KerbalsField = typeof(KerbalRoster).GetField("kerbals", Any);
        private static bool _warned;

        /// <summary>The fields this needs are there (this KSP version's roster is laid out as expected).</summary>
        internal static bool Works
        {
            get
            {
                var ok = NameField != null && KerbalsField != null;
                if (!ok && !_warned)
                {
                    _warned = true;
                    Debug.LogWarning("[KspFlightPlanner] Kerbin Resources: can't rename kerbals safely in this KSP version; names left as they are.");
                }
                return ok;
            }
        }

        /// <summary>Renames the kerbal; false (nothing changed) if the roster isn't as expected or the name is taken.</summary>
        internal static bool Rename(ProtoCrewMember k, string newName)
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (k == null || roster == null || !Works || string.IsNullOrEmpty(newName) || roster.Exists(newName)) return false;
            var kerbals = KerbalsField.GetValue(roster);
            var type = kerbals?.GetType();
            var dict = type?.GetField("dict", Any)?.GetValue(kerbals) as IDictionary;
            var keys = type?.GetField("listKeys", Any)?.GetValue(kerbals) as IList;
            if (dict == null || keys == null)
            {
                if (!_warned) Debug.LogWarning("[KspFlightPlanner] Kerbin Resources: KSP's roster isn't laid out as expected; names left as they are.");
                _warned = true;
                return false;
            }
            var old = k.name;
            if (!dict.Contains(old))
            {
                // Not in the roster yet: KSP is about to add it, by the name it has then.
                NameField.SetValue(k, newName);
                return true;
            }
            var at = keys.IndexOf(old);
            if (at < 0 || !ReferenceEquals(dict[old], k)) return false;
            GameEvents.onKerbalNameChange.Fire(k, old, newName);
            dict.Remove(old);
            dict.Add(newName, k);
            keys[at] = newName;
            NameField.SetValue(k, newName);
            RenameSeats(old, newName);
            GameEvents.onKerbalNameChanged.Fire(k, old, newName);
            return true;
        }

        /// <summary>Every craft's seats with the old name get the new one: the save's craft, and in flight the loaded ones too.</summary>
        private static void RenameSeats(string old, string newName)
        {
            var seen = new HashSet<ProtoVessel>();
            var vessels = HighLogic.CurrentGame?.flightState?.protoVessels;
            if (vessels != null)
                foreach (var pv in vessels) RenameSeats(pv, old, newName, seen);
            if (FlightGlobals.Vessels != null)
                foreach (var v in FlightGlobals.Vessels)
                    if (v != null) RenameSeats(v.protoVessel, old, newName, seen);
        }

        private static void RenameSeats(ProtoVessel pv, string old, string newName, HashSet<ProtoVessel> seen)
        {
            if (pv?.protoPartSnapshots == null || !seen.Add(pv)) return;
            foreach (var part in pv.protoPartSnapshots)
            {
                var names = part?.protoCrewNames;
                if (names == null) continue;
                for (var i = 0; i < names.Count; i++)
                    if (names[i] == old)
                    {
                        names[i] = newName;
                        Debug.Log("[KspFlightPlanner] Kerbin Resources: seat in " + part.partName + " on " + pv.vesselName + " now " + newName + ".");
                    }
            }
        }
    }
}
