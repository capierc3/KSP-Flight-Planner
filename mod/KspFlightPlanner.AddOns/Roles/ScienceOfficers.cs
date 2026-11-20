using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KspFlightPlanner.KerbinResources;
using KspFlightPlanner.Science;
using UnityEngine;

namespace KspFlightPlanner.Roles
{
    /// <summary>An experiment a probe can run again from a distance: its id and the share of its value it earns sent home.</summary>
    internal struct ProbeExperiment
    {
        public string Id;
        public double Transmit;
    }

    /// <summary>Where a probe is, for science: its body, situation and biome (biome "" where the body has none).</summary>
    internal struct SciencePlace
    {
        public CelestialBody Body;
        public ExperimentSituations Situation;
        public string Biome;
    }

    /// <summary>One experiment worked out at a place: its subject, the data a run makes and what sending it home earns.</summary>
    internal struct ScienceRun
    {
        public ProbeExperiment Experiment;
        public ScienceExperiment Definition;
        public string SubjectId;
        public SciencePlace Place;
        public double Data;
        public double Sent;
    }

    /// <summary>A Science Officer working one probe: signing in, running and sending each experiment in turn, signing out.</summary>
    internal sealed class OfficerSession
    {
        internal enum Step { SignIn, Run, Send, SignOut }

        public string Officer = "";
        public uint ProbeId;
        public string ProbeName = "";
        public Step Stage;
        public double Started;
        public double Until;
        public readonly List<ProbeExperiment> Queue = new List<ProbeExperiment>();
        public ProbeExperiment Current;
        public ScienceRun Captured;

        /// <summary>The player took the probe over: finish the step under way, then sign out.</summary>
        public bool StopAfterStep;

        /// <summary>The lab was switched off for this session (the officer was its only scientist, on a loaded station).</summary>
        public bool StoppedLab;
    }

    /// <summary>
    /// Science Officers at work (docs/KERBIN-RESOURCES.md "Science Officer at work" and "The officer's work limit"): a
    /// scientist on duty in a station's lab, or at KSC for Kerbin, runs and sends the experiments of linked probes the
    /// player isn't flying. One probe at a time, one experiment at a time, each step taking game time. Only what can be sent
    /// home counts. Runs in flight and the tracking station, where CommNet and the game's craft are.
    /// </summary>
    internal sealed class ScienceOfficers
    {
        private readonly Dictionary<string, OfficerSession> _sessions = new Dictionary<string, OfficerSession>();

        /// <summary>Each probe's last subject per experiment: an experiment runs again only once its subject has changed (a new place).</summary>
        internal readonly Dictionary<string, Dictionary<string, string>> LastSubjects = new Dictionary<string, Dictionary<string, string>>();

        /// <summary>When each probe was first seen waiting with something to send, for first come, first served.</summary>
        private readonly Dictionary<uint, double> _waiting = new Dictionary<uint, double>();

        internal IEnumerable<OfficerSession> Sessions => _sessions.Values;

        internal OfficerSession SessionOf(string officer) => _sessions.TryGetValue(officer, out var s) ? s : null;

        internal void Tick(double now)
        {
            var duty = KerbinResourcesScenario.Instance?.Duty;
            if (duty == null || ResearchAndDevelopment.Instance == null) return;
            var settings = RolesSettings.Current;
            var officers = duty.OnDuty.Where(SurveyRoles.IsScienceOfficer).ToList();

            foreach (var name in _sessions.Keys.ToList())
                if (!settings.ScienceOfficers || officers.All(o => o.Kerbal != name)) End(_sessions[name], now, "came off duty");

            foreach (var officer in officers)
            {
                if (_sessions.TryGetValue(officer.Kerbal, out var session))
                {
                    if (FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.persistentId == session.ProbeId) session.StopAfterStep = true;
                    Advance(officer, session, now);
                }
                if (!_sessions.ContainsKey(officer.Kerbal)) Start(officer, now);
            }
        }

        /// <summary>An idle officer takes the probe that has waited longest with something worth sending.</summary>
        private void Start(DutyShift officer, double now)
        {
            var threshold = ScienceSettings.Current.PaysAtLeast;
            var busy = new HashSet<uint>(_sessions.Values.Select(s => s.ProbeId));
            Vessel pick = null;
            var pickSince = double.MaxValue;
            foreach (var probe in SurveyRoles.LinkedProbes(officer))
            {
                if (busy.Contains(probe.persistentId) || probe == FlightGlobals.ActiveVessel || !SurveyRoles.LinkedHome(probe)) continue;
                if (!Runs(probe, now).Any(r => r.Sent >= threshold)) { _waiting.Remove(probe.persistentId); continue; }
                if (!_waiting.TryGetValue(probe.persistentId, out var since)) _waiting[probe.persistentId] = since = now;
                if (since < pickSince) { pick = probe; pickSince = since; }
            }
            if (pick == null) return;
            _waiting.Remove(pick.persistentId);
            var s = new OfficerSession
            {
                Officer = officer.Kerbal, ProbeId = pick.persistentId, ProbeName = pick.vesselName,
                Stage = OfficerSession.Step.SignIn, Started = now, Until = now + RolesSettings.Current.SignInSeconds,
            };
            s.StoppedLab = PauseLab(officer, true);
            _sessions[officer.Kerbal] = s;
        }

        /// <summary>Works through every step whose time has come (several at once under time warp).</summary>
        private void Advance(DutyShift officer, OfficerSession s, double now)
        {
            var settings = RolesSettings.Current;
            var threshold = ScienceSettings.Current.PaysAtLeast;
            for (var guard = 0; guard < 50 && _sessions.ContainsKey(s.Officer) && s.Until <= now; guard++)
            {
                var t = s.Until;
                var probe = SurveyRoles.Find(s.ProbeId);
                if (probe == null) { End(s, t, "the probe is gone"); return; }
                switch (s.Stage)
                {
                    case OfficerSession.Step.SignIn:
                        s.Queue.Clear();
                        s.Queue.AddRange(Runs(probe, t).Where(r => r.Sent >= threshold).OrderByDescending(r => r.Sent).Select(r => r.Experiment));
                        Next(s, t, settings);
                        break;
                    case OfficerSession.Step.Run:
                        var run = Runs(probe, t, s.Current.Id).FirstOrDefault();
                        if (run.Definition != null && run.Sent >= threshold)
                        {
                            s.Captured = run;
                            s.Stage = OfficerSession.Step.Send;
                            s.Until = t + TransferSeconds(probe, run.Data);
                        }
                        else Next(s, t, settings);
                        break;
                    case OfficerSession.Step.Send:
                        if (SurveyRoles.Linked(officer, probe) && SurveyRoles.LinkedHome(probe)) Submit(officer, s, probe, t);
                        else
                        {
                            Say(officer, s.ProbeName + ": lost the link sending " + s.Captured.Definition.experimentTitle + ", nothing earned");
                            s.Queue.Clear();
                        }
                        Next(s, t, settings);
                        break;
                    case OfficerSession.Step.SignOut:
                        End(s, t, null);
                        return;
                }
            }
        }

        private static void Next(OfficerSession s, double t, RolesSettings settings)
        {
            if (s.StopAfterStep || s.Queue.Count == 0)
            {
                s.Stage = OfficerSession.Step.SignOut;
                s.Until = t + settings.SignOutSeconds;
                return;
            }
            s.Current = s.Queue[0];
            s.Queue.RemoveAt(0);
            s.Stage = OfficerSession.Step.Run;
            s.Until = t + settings.RunSeconds;
        }

        private void End(OfficerSession s, double t, string why)
        {
            _sessions.Remove(s.Officer);
            var duty = KerbinResourcesScenario.Instance?.Duty;
            var officer = duty?.ShiftOf(s.Officer);
            if (officer != null) ResumeLab(officer, s, t);
            if (why != null) Debug.Log("[KspFlightPlanner] Roles: " + s.Officer + " stopped working " + s.ProbeName + ": " + why + ".");
        }

        /// <summary>Sends the captured run home: the game credits the sent value, as pressing Transmit does.</summary>
        private void Submit(DutyShift officer, OfficerSession s, Vessel probe, double t)
        {
            var run = s.Captured;
            var place = run.Place;
            var biome = run.Definition.BiomeIsRelevantWhile(place.Situation) ? place.Biome ?? "" : "";
            var display = string.IsNullOrEmpty(biome) ? "" : ScienceUtil.GetBiomedisplayName(place.Body, biome);
            var subject = ResearchAndDevelopment.GetExperimentSubject(run.Definition, place.Situation, place.Body, biome, display);
            if (subject == null) return;
            var earned = ResearchAndDevelopment.Instance.SubmitScienceData((float)run.Data, subject, (float)run.Experiment.Transmit, probe.protoVessel, false);
            var pid = SurveyRoles.Pid(probe);
            if (!LastSubjects.TryGetValue(pid, out var last)) LastSubjects[pid] = last = new Dictionary<string, string>();
            last[run.Experiment.Id] = run.SubjectId;
            var where = (string.IsNullOrEmpty(display) ? "" : display + " ") + "(" + ScanNow.Label(place.Situation) + ")";
            KerbinResourcesScenario.Instance?.Work.Add(new WorkEntry
            {
                Ut = t, Kerbal = officer.Kerbal, Kind = WorkEntry.Science, Body = place.Body.bodyName,
                PostName = officer.AtKsc ? DutyShift.Ksc : officer.PostName, Vessel = s.ProbeName,
                Detail = run.Definition.experimentTitle + " from " + where, Value = earned,
            });
            Say(officer, s.ProbeName + " sent " + run.Definition.experimentTitle + " from " + where + ": " + ScanNow.Points(earned));
        }

        private static void Say(DutyShift officer, string text)
        {
            var line = (officer.AtKsc ? DutyShift.Ksc : officer.PostName) + " Science Officer (" + officer.Kerbal + "): " + text;
            Debug.Log("[KspFlightPlanner] Roles: " + line);
            ScreenMessages.PostScreenMessage(line, 8f, ScreenMessageStyle.UPPER_LEFT);
        }

        // ---------------- What a probe could send ----------------

        /// <summary>
        /// The probe's experiments worked out where it is at <paramref name="ut"/>: only ones it can run again, whose subject
        /// there differs from the last one it sent (a new place for that experiment). <paramref name="only"/> picks one.
        /// </summary>
        internal IEnumerable<ScienceRun> Runs(Vessel probe, double ut, string only = null)
        {
            var place = PlaceOf(probe, ut);
            if (place.Body == null) yield break;
            LastSubjects.TryGetValue(SurveyRoles.Pid(probe), out var last);
            foreach (var x in Experiments(probe))
            {
                if (only != null && x.Id != only) continue;
                var e = ResearchAndDevelopment.GetExperiment(x.Id);
                if (e == null || !e.IsAvailableWhile(place.Situation, place.Body)) continue;
                var relevant = e.BiomeIsRelevantWhile(place.Situation);
                var subjectId = e.id + "@" + place.Body.name + place.Situation + (relevant ? (place.Biome ?? "").Replace(" ", "") : "");
                if (last != null && last.TryGetValue(x.Id, out var was) && was == subjectId) continue;
                var subject = ResearchAndDevelopment.GetSubjectByID(subjectId);
                double multiplier = subject != null ? subject.subjectValue : ScanNow.Multiplier(place.Body, place.Situation);
                double cap = subject != null ? subject.scienceCap : e.scienceCap * multiplier;
                double earned = subject != null ? subject.science : 0;
                var data = e.baseValue * e.dataScale;
                yield return new ScienceRun
                {
                    Experiment = x, Definition = e, SubjectId = subjectId, Place = place, Data = data,
                    Sent = ScanNow.Value(data, e.dataScale, multiplier, earned, cap) * x.Transmit,
                };
            }
        }

        /// <summary>The probe's body, situation and biome at a time: from its orbit in flight, or where it stands if landed.</summary>
        internal static SciencePlace PlaceOf(Vessel v, double ut)
        {
            var body = v.mainBody;
            if (body == null) return default;
            if (v.LandedOrSplashed)
                return new SciencePlace
                {
                    Body = body, Situation = v.Landed ? ExperimentSituations.SrfLanded : ExperimentSituations.SrfSplashed,
                    Biome = !string.IsNullOrEmpty(v.landedAt) ? Vessel.GetLandedAtString(v.landedAt) : ScienceUtil.GetExperimentBiome(body, v.latitude, v.longitude),
                };
            if (v.orbit == null) return default;
            var pos = v.orbit.getPositionAtUT(ut);
            var alt = body.GetAltitude(pos);
            var lat = body.GetLatitude(pos);
            var lon = body.GetLongitude(pos);
            var values = body.scienceValues;
            ExperimentSituations situation;
            if (body.atmosphere && alt < body.atmosphereDepth)
                situation = alt < values.flyingAltitudeThreshold ? ExperimentSituations.FlyingLow : ExperimentSituations.FlyingHigh;
            else
                situation = alt < values.spaceAltitudeThreshold ? ExperimentSituations.InSpaceLow : ExperimentSituations.InSpaceHigh;
            return new SciencePlace { Body = body, Situation = situation, Biome = ScienceUtil.GetExperimentBiome(body, lat, lon) };
        }

        /// <summary>Working experiment parts that can run again, each with its transmit share (the best of its kind aboard).</summary>
        internal static List<ProbeExperiment> Experiments(Vessel v)
        {
            var best = new Dictionary<string, double>();
            void Add(ModuleScienceExperiment prefab, bool inoperable)
            {
                if (prefab == null || inoperable || !prefab.rerunnable || string.IsNullOrEmpty(prefab.experimentID)) return;
                best[prefab.experimentID] = Math.Max(best.TryGetValue(prefab.experimentID, out var x) ? x : 0, prefab.xmitDataScalar);
            }
            if (v.loaded)
                foreach (var m in v.FindPartModulesImplementing<ModuleScienceExperiment>())
                    Add(m, m.Inoperable);
            else if (v.protoVessel != null)
                foreach (var pp in v.protoVessel.protoPartSnapshots)
                {
                    var prefabs = pp.partInfo?.partPrefab?.Modules.OfType<ModuleScienceExperiment>().Where(m => m.GetType() == typeof(ModuleScienceExperiment)).ToList();
                    if (prefabs == null || prefabs.Count == 0) continue;
                    var saved = pp.modules.Where(m => m.moduleName == "ModuleScienceExperiment").ToList();
                    for (var i = 0; i < prefabs.Count; i++)
                        Add(prefabs[i], i < saved.Count && saved[i].moduleValues?.GetValue("Inoperable") == "True");
                }
            return best.Select(kv => new ProbeExperiment { Id = kv.Key, Transmit = kv.Value }).ToList();
        }

        /// <summary>
        /// How long sending takes: data goes in packets of packetSize Mits, one every packetInterval seconds, from the
        /// probe's fastest antenna: ceil(data ÷ packetSize) × packetInterval.
        /// </summary>
        internal static double TransferSeconds(Vessel v, double data)
        {
            IEnumerable<ModuleDataTransmitter> antennas = v.loaded
                ? v.FindPartModulesImplementing<ModuleDataTransmitter>()
                : (v.protoVessel?.protoPartSnapshots ?? new List<ProtoPartSnapshot>())
                    .SelectMany(pp => pp.partInfo?.partPrefab?.Modules.OfType<ModuleDataTransmitter>() ?? Enumerable.Empty<ModuleDataTransmitter>());
            var fastest = antennas.Where(a => a.packetSize > 0 && a.packetInterval > 0).OrderByDescending(a => a.packetSize / a.packetInterval).FirstOrDefault();
            double size = fastest != null ? fastest.packetSize : 2, interval = fastest != null ? fastest.packetInterval : 1;
            return Math.Ceiling(data / size) * interval;
        }

        // ---------------- The lab while the officer is busy ----------------

        /// <summary>Scientists on duty as Science Officers at the same station (they're the lab's researchers).</summary>
        private static int Researchers(DutyShift officer) =>
            KerbinResourcesScenario.Instance?.Duty.OnDuty.Count(s => SurveyRoles.IsScienceOfficer(s) && !s.AtKsc && s.PostId == officer.PostId) ?? 1;

        /// <summary>
        /// The officer starts a session. On a loaded station where they're the lab's only researcher, the lab stops at once
        /// (true). Craft that aren't loaded are put right when the session ends (<see cref="ResumeLab"/>).
        /// </summary>
        private static bool PauseLab(DutyShift officer, bool start)
        {
            if (officer.AtKsc) return false;
            var station = SurveyRoles.Find(officer.PostId);
            if (station == null || !station.loaded || Researchers(officer) > 1) return false;
            var stopped = false;
            foreach (var lab in station.FindPartModulesImplementing<ModuleScienceConverter>())
                if (lab.IsActivated) { lab.StopResourceConverter(); stopped = true; }
            return stopped;
        }

        /// <summary>
        /// The session ended. A lab switched off for it starts again. On a station that isn't loaded, the lab's research
        /// clock (lastUpdateTime, from which KSP catches its research up when the craft loads) moves on by the officer's
        /// share of the session: all of it when they were the only researcher, half with two, and so on.
        /// </summary>
        private static void ResumeLab(DutyShift officer, OfficerSession s, double t)
        {
            if (officer.AtKsc) return;
            var station = SurveyRoles.Find(officer.PostId);
            if (station == null) return;
            if (station.loaded)
            {
                if (s.StoppedLab)
                    foreach (var lab in station.FindPartModulesImplementing<ModuleScienceConverter>())
                        lab.StartResourceConverter();
                return;
            }
            var share = 1.0 / Math.Max(1, Researchers(officer));
            var shift = (t - s.Started) * share;
            if (station.protoVessel == null || shift <= 0) return;
            foreach (var pp in station.protoVessel.protoPartSnapshots)
                foreach (var m in pp.modules.Where(m => m.moduleName == "ModuleScienceConverter"))
                {
                    var values = m.moduleValues;
                    if (values == null || values.GetValue("IsActivated") != "True") continue;
                    if (!double.TryParse(values.GetValue("lastUpdateTime"), NumberStyles.Float, CultureInfo.InvariantCulture, out var last)) continue;
                    values.SetValue("lastUpdateTime", Math.Min(t, last + shift).ToString("R", CultureInfo.InvariantCulture));
                }
        }

        // ---------------- Save ----------------

        internal void Save(ConfigNode node)
        {
            foreach (var probe in LastSubjects)
            {
                var pn = node.AddNode("PROBE");
                pn.AddValue("pid", probe.Key);
                foreach (var e in probe.Value)
                {
                    var en = pn.AddNode("EXP");
                    en.AddValue("id", e.Key);
                    en.AddValue("subject", e.Value);
                }
            }
        }

        internal void Load(ConfigNode node)
        {
            LastSubjects.Clear();
            _sessions.Clear();
            _waiting.Clear();
            if (node == null) return;
            foreach (var pn in node.GetNodes("PROBE"))
            {
                var map = new Dictionary<string, string>();
                foreach (var en in pn.GetNodes("EXP")) map[en.GetValue("id") ?? ""] = en.GetValue("subject") ?? "";
                LastSubjects[pn.GetValue("pid") ?? ""] = map;
            }
        }
    }
}
