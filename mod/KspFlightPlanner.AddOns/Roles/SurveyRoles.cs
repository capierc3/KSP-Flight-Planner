using System;
using System.Collections.Generic;
using System.Linq;
using CommNet;
using KspFlightPlanner.KerbinResources;
using CrewLog = KspFlightPlanner.KerbinResources.FlightLog;

namespace KspFlightPlanner.Roles
{
    /// <summary>A craft a Survey Engineer can watch: its save id (pid) and name.</summary>
    internal struct WatchCraft
    {
        public string Pid;
        public string Name;
    }

    /// <summary>
    /// A Survey Engineer at work, as last looked at: where they're posted and which craft's scanners they can watch (the
    /// station's own, if it has any, and every linked probe that has some). The planner works out what they mapped from
    /// these (docs/KERBIN-RESOURCES.md "Survey Engineer at work").
    /// </summary>
    internal sealed class SurveyWorker
    {
        public string Kerbal = "";
        public string PostName = "";
        public string PostPid = "";
        public string Body = "";
        public bool AtKsc;
        public readonly List<WatchCraft> Crafts = new List<WatchCraft>();

        internal void Save(ConfigNode n)
        {
            n.AddValue("kerbal", Kerbal);
            n.AddValue("post", PostName);
            n.AddValue("postPid", PostPid);
            n.AddValue("body", Body);
            n.AddValue("ksc", AtKsc);
            foreach (var c in Crafts)
            {
                var cn = n.AddNode("CRAFT");
                cn.AddValue("pid", c.Pid);
                cn.AddValue("name", c.Name);
            }
        }

        internal static SurveyWorker Load(ConfigNode n)
        {
            var w = new SurveyWorker
            {
                Kerbal = n.GetValue("kerbal") ?? "",
                PostName = n.GetValue("post") ?? "",
                PostPid = n.GetValue("postPid") ?? "",
                Body = n.GetValue("body") ?? "",
                AtKsc = n.GetValue("ksc") == "True",
            };
            foreach (var cn in n.GetNodes("CRAFT")) w.Crafts.Add(new WatchCraft { Pid = cn.GetValue("pid") ?? "", Name = cn.GetValue("name") ?? "" });
            return w;
        }

        /// <summary>A line for the planner: kerbal, post, post pid, body, at KSC (1/0), then craft pid|name pairs joined by commas.</summary>
        internal string Line() =>
            string.Join("\t", new[]
            {
                Kerbal, PostName, PostPid, Body, AtKsc ? "1" : "0",
                string.Join(",", Crafts.Select(c => c.Pid + "|" + (c.Name ?? "").Replace(",", " ").Replace("|", " ")).ToArray()),
            });
    }

    /// <summary>
    /// The survey and science jobs: who gets which title, where they have to sit, and which probes are linked to them
    /// (docs/KERBIN-RESOURCES.md "Survey and science roles").
    /// </summary>
    internal static class SurveyRoles
    {
        internal const string SurveyEngineerJob = "Survey Engineer", ScienceOfficerJob = "Science Officer";

        internal static string HomeBody => FlightGlobals.GetHomeBody()?.bodyName ?? "Kerbin";

        /// <summary>The body a post covers: Kerbin for KSC, otherwise the SOI the station is in.</summary>
        internal static string BodyOf(DutyShift s) => s.AtKsc ? HomeBody : s.Body;

        internal static bool IsSurveyEngineer(DutyShift s) => s?.Job != null && s.Job.EndsWith(SurveyEngineerJob);
        internal static bool IsScienceOfficer(DutyShift s) => s?.Job != null && s.Job.EndsWith(ScienceOfficerJob);

        /// <summary>"Writing the Minmus survey report".</summary>
        internal static string WritingJob(string body) => "Writing the " + body + " survey report";

        /// <summary>
        /// The job title for an engineer or scientist on duty (Kerbin Resources asks; null leaves them awaiting orders):
        /// <list type="bullet">
        /// <item>An engineer writing a finished body's report: "Writing the Minmus survey report", at KSC or in a lab seat.</item>
        /// <item>An engineer at KSC: "KSC Survey Engineer", until Kerbin's survey is done.</item>
        /// <item>An engineer in a research lab of the station's own parts, the station or a linked probe having a scanner and
        /// the body not done: "Min Station Survey Engineer".</item>
        /// <item>A scientist at KSC: "KSC Science Officer"; in a research lab of the station's own parts: "Min Station Science Officer".</item>
        /// </list>
        /// Anyone else (a pod, a hitchhiker can, a ship docked to the station): no job.
        /// </summary>
        internal static string JobOf(DutyShift shift)
        {
            if (shift == null) return null;
            var trait = CrewLog.TraitOf(shift.Kerbal);
            if (trait != "Engineer" && trait != "Scientist") return null;
            var settings = RolesSettings.Current;
            var post = shift.AtKsc ? null : Find(shift.PostId);
            var saved = shift.AtKsc ? null : post?.protoVessel ?? FindSaved(shift.PostId);
            var lab = shift.AtKsc || !shift.InDockedShip && (post != null && post.loaded ? HasModule(post, shift.SeatPartId, "ModuleScienceLab") : HasModule(saved, shift.SeatPartId, "ModuleScienceLab"));
            if (!lab) return null;
            var prefix = shift.AtKsc ? DutyShift.Ksc : shift.PostName;
            var scenario = RolesScenario.Instance;
            if (trait == "Scientist")
                return settings.ScienceOfficers ? prefix + " " + ScienceOfficerJob : null;

            if (scenario != null)
            {
                var writing = scenario.Reports.Values.FirstOrDefault(r => !r.Filed && r.Writers.Contains(shift.Kerbal));
                if (writing != null) return WritingJob(writing.Body);
            }
            if (!settings.SurveyEngineers) return null;
            var body = BodyOf(shift);
            if (scenario != null && scenario.Reports.ContainsKey(body)) return null; // done: nothing left to survey here
            if (shift.AtKsc) return prefix + " " + SurveyEngineerJob;
            var hasScanner = post != null ? HasScanner(post) : HasScanner(saved);
            if (!hasScanner && scenario != null)
                hasScanner = scenario.Workers.Any(w => w.Kerbal == shift.Kerbal && w.Crafts.Count > 0);
            if (!hasScanner && Scene.CanCheckLinks) hasScanner = LinkedProbes(shift).Any(HasScanner);
            return hasScanner ? prefix + " " + SurveyEngineerJob : null;
        }

        // ---------------- Parts ----------------

        /// <summary>A loaded craft's part (by persistent id) has a module of this name.</summary>
        internal static bool HasModule(Vessel v, uint partId, string module)
        {
            var part = v == null || partId == 0 ? null : v.parts.FirstOrDefault(p => p.persistentId == partId);
            return part != null && part.Modules.Cast<PartModule>().Any(m => m.moduleName == module);
        }

        /// <summary>The same from a saved craft record.</summary>
        internal static bool HasModule(ProtoVessel pv, uint partId, string module)
        {
            var pp = pv == null || partId == 0 ? null : pv.protoPartSnapshots.FirstOrDefault(p => p.persistentId == partId);
            return pp != null && pp.modules.Any(m => m.moduleName == module);
        }

        /// <summary>A survey scanner (M700), or a narrow-band (a resource scanner with KerbNet) on one part.</summary>
        private static bool IsScanner(IList<string> modules) =>
            modules.Contains("ModuleOrbitalSurveyor") || modules.Contains("ModuleResourceScanner") && modules.Contains("ModuleKerbNetAccess");

        internal static bool HasScanner(Vessel v)
        {
            if (v == null) return false;
            if (!v.loaded) return HasScanner(v.protoVessel);
            return v.parts.Any(p => IsScanner(p.Modules.Cast<PartModule>().Select(m => m.moduleName).ToList()));
        }

        internal static bool HasScanner(ProtoVessel pv) =>
            pv != null && pv.protoPartSnapshots.Any(p => IsScanner(p.modules.Select(m => m.moduleName).ToList()));

        // ---------------- Craft ----------------

        internal static Vessel Find(uint persistentId)
        {
            if (persistentId == 0 || FlightGlobals.Vessels == null) return null;
            foreach (var v in FlightGlobals.Vessels)
                if (v != null && v.persistentId == persistentId) return v;
            return null;
        }

        internal static ProtoVessel FindSaved(uint persistentId)
        {
            var vessels = HighLogic.CurrentGame?.flightState?.protoVessels;
            return persistentId == 0 || vessels == null ? null : vessels.FirstOrDefault(pv => pv != null && pv.persistentId == persistentId);
        }

        /// <summary>The craft's id as the save writes it (pid): 32 hex digits, lower case.</summary>
        internal static string Pid(Vessel v) => v.id.ToString("N");

        /// <summary>A probe: an uncrewed craft that isn't debris, a flag, a space object or a kerbal on EVA.</summary>
        internal static bool IsProbe(Vessel v) =>
            v != null && v.GetCrewCount() == 0 && !v.isEVA &&
            v.vesselType != VesselType.Debris && v.vesselType != VesselType.Flag && v.vesselType != VesselType.SpaceObject &&
            v.vesselType != VesselType.Unknown && v.vesselType != VesselType.DeployedScienceController && v.vesselType != VesselType.DeployedSciencePart;

        // ---------------- Links ----------------

        /// <summary>CommNet is on in this save; without it every link counts as there.</summary>
        private static bool CommNetOn() => CommNetScenario.CommNetEnabled && CommNetNetwork.Instance != null && CommNetNetwork.Instance.CommNet != null;

        /// <summary>Can the post reach the probe: the same SOI, and a CommNet path (from KSC: the probe's link home).</summary>
        internal static bool Linked(DutyShift post, Vessel probe)
        {
            if (post == null || probe == null || probe.mainBody == null) return false;
            if (probe.mainBody.bodyName != BodyOf(post)) return false;
            if (!CommNetOn()) return true;
            if (post.AtKsc) return probe.connection != null && probe.connection.IsConnectedHome;
            var station = Find(post.PostId);
            if (station == null) return false;
            var a = station.connection?.Comm;
            var b = probe.connection?.Comm;
            return a != null && b != null && CommNetNetwork.Instance.CommNet.FindPath(a, new CommPath(), b);
        }

        /// <summary>The probe can send data home (needed to transmit).</summary>
        internal static bool LinkedHome(Vessel probe) =>
            !CommNetOn() || probe?.connection != null && probe.connection.IsConnectedHome;

        /// <summary>Probes linked to a post now, orbiting or landed (never the craft the player is flying).</summary>
        internal static IEnumerable<Vessel> LinkedProbes(DutyShift post)
        {
            if (FlightGlobals.Vessels == null) yield break;
            foreach (var v in FlightGlobals.Vessels.ToList())
                if (IsProbe(v) && (post.AtKsc || v.persistentId != post.PostId) && Linked(post, v))
                    yield return v;
        }

        /// <summary>
        /// Every Survey Engineer on duty now, with the craft they can watch: the station's own scanners first, then each
        /// linked orbiting probe with a scanner.
        /// </summary>
        internal static List<SurveyWorker> Workers()
        {
            var list = new List<SurveyWorker>();
            var duty = KerbinResourcesScenario.Instance?.Duty;
            if (duty == null) return list;
            foreach (var s in duty.OnDuty.Where(IsSurveyEngineer).ToList())
            {
                var w = new SurveyWorker { Kerbal = s.Kerbal, PostName = s.AtKsc ? DutyShift.Ksc : s.PostName, Body = BodyOf(s), AtKsc = s.AtKsc };
                var station = s.AtKsc ? null : Find(s.PostId);
                if (station != null)
                {
                    w.PostPid = Pid(station);
                    if (HasScanner(station)) w.Crafts.Add(new WatchCraft { Pid = w.PostPid, Name = station.vesselName });
                }
                foreach (var probe in LinkedProbes(s))
                    if (probe.situation == Vessel.Situations.ORBITING && HasScanner(probe))
                        w.Crafts.Add(new WatchCraft { Pid = Pid(probe), Name = probe.vesselName });
                list.Add(w);
            }
            return list;
        }
    }

    /// <summary>What the scene allows.</summary>
    internal static class Scene
    {
        /// <summary>CommNet and the game's craft run here (flight, the tracking station); not at the space centre.</summary>
        internal static bool CanCheckLinks => (HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.TRACKSTATION) && FlightGlobals.Vessels != null;
    }
}
