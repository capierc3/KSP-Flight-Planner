using System;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.KerbinResources
{
    /// <summary>
    /// Moving a kerbal to another part of the craft they're aboard, a docked stack counting as one craft: a pilot from a
    /// hitchhiker can to the cupola, say, so they can go on duty as the station's Traffic Controller. Works on a loaded
    /// craft (the way KSP's own crew transfer does it) and on one that isn't (its saved record, saved to the save file at the
    /// space centre). Shift changes will use it to swap who sits where (docs/KERBIN-RESOURCES.md "Moving crew").
    /// </summary>
    internal static class CrewMove
    {
        /// <summary>Free seats in a loaded part.</summary>
        internal static int FreeSeats(Part p) => p == null ? 0 : Math.Max(0, p.CrewCapacity - p.protoModuleCrew.Count);

        /// <summary>Free seats in a saved part.</summary>
        internal static int FreeSeats(ProtoPartSnapshot pp) =>
            pp == null ? 0 : Math.Max(0, (pp.partInfo?.partPrefab != null ? pp.partInfo.partPrefab.CrewCapacity : 0) - pp.protoModuleCrew.Count);

        /// <summary>
        /// Moves <paramref name="kerbal"/> into the part with persistent id <paramref name="toPartId"/> of the craft with
        /// persistent id <paramref name="vesselId"/>. <paramref name="host"/> runs the frame-later step for the craft you're
        /// flying (its kerbals' portraits and IVA are put back once the move is done). False with the reason when it can't.
        /// </summary>
        internal static bool Move(uint vesselId, string kerbal, uint toPartId, MonoBehaviour host, out string why)
        {
            why = "";
            var pcm = HighLogic.CurrentGame?.CrewRoster?[kerbal];
            if (pcm == null) { why = kerbal + " isn't on the roster"; return false; }
            Vessel live = null;
            if (FlightGlobals.Vessels != null)
                foreach (var v in FlightGlobals.Vessels)
                    if (v != null && v.persistentId == vesselId) { live = v; break; }
            if (live != null && live.loaded) return MoveLoaded(live, pcm, toPartId, host, out why);
            var pv = live?.protoVessel ?? HighLogic.CurrentGame?.flightState?.protoVessels?.FirstOrDefault(p => p != null && p.persistentId == vesselId);
            if (pv == null) { why = "couldn't find the craft"; return false; }
            if (!MoveSaved(pv, pcm, toPartId, out why)) return false;
            // Outside flight the save file is what the next scene loads from; in flight the craft's record is saved with the rest.
            if (!HighLogic.LoadedSceneIsFlight)
            {
                try { GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE); }
                catch (Exception e) { Debug.LogWarning("[KspFlightPlanner] Kerbin Resources: saving after moving " + kerbal + " failed: " + e.Message); }
            }
            return true;
        }

        /// <summary>A loaded craft: KSP's own crew transfer steps (out of one part, into the other, tell the game, respawn the IVA).</summary>
        private static bool MoveLoaded(Vessel v, ProtoCrewMember pcm, uint toPartId, MonoBehaviour host, out string why)
        {
            why = "";
            var from = v.parts.FirstOrDefault(p => p.protoModuleCrew.Contains(pcm));
            var to = v.parts.FirstOrDefault(p => p.persistentId == toPartId);
            if (from == null) { why = pcm.name + " isn't aboard " + v.vesselName; return false; }
            if (to == null) { why = "that part isn't part of " + v.vesselName + " any more"; return false; }
            if (to == from) { why = pcm.name + " is already there"; return false; }
            if (FreeSeats(to) == 0) { why = to.partInfo.title + " is full"; return false; }
            from.RemoveCrewmember(pcm);
            to.AddCrewmember(pcm);
            GameEvents.onCrewTransferred.Fire(new GameEvents.HostedFromToAction<ProtoCrewMember, Part>(pcm, from, to));
            Vessel.CrewWasModified(v);
            if (v == FlightGlobals.ActiveVessel)
            {
                v.DespawnCrew();
                if (host != null) host.StartCoroutine(Respawn(v));
                else v.SpawnCrew();
            }
            Log(pcm.name, from.partInfo.title, to.partInfo.title, v.vesselName);
            return true;
        }

        private static IEnumerator Respawn(Vessel v)
        {
            yield return null;
            yield return null;
            if (v != null && v.loaded) v.SpawnCrew();
        }

        /// <summary>
        /// A craft not loaded: moved between its saved parts, given the first seat nobody in that part has, and the craft's
        /// crew counts rebuilt.
        /// </summary>
        private static bool MoveSaved(ProtoVessel pv, ProtoCrewMember pcm, uint toPartId, out string why)
        {
            why = "";
            var from = pv.protoPartSnapshots.FirstOrDefault(pp => pp.protoModuleCrew.Any(c => c.name == pcm.name));
            var to = pv.protoPartSnapshots.FirstOrDefault(pp => pp.persistentId == toPartId);
            if (from == null) { why = pcm.name + " isn't aboard " + pv.vesselName; return false; }
            if (to == null) { why = "that part isn't part of " + pv.vesselName + " any more"; return false; }
            if (to == from) { why = pcm.name + " is already there"; return false; }
            if (FreeSeats(to) == 0) { why = Title(to) + " is full"; return false; }
            from.RemoveCrew(pcm.name);
            var taken = to.protoModuleCrew.Select(c => c.seatIdx).ToList();
            var seat = 0;
            while (taken.Contains(seat)) seat++;
            pcm.seat = null;
            pcm.seatIdx = seat;
            to.protoModuleCrew.Add(pcm);
            to.protoCrewNames.Add(pcm.name);
            pv.RebuildCrewCounts();
            Log(pcm.name, Title(from), Title(to), pv.vesselName);
            return true;
        }

        internal static string Title(ProtoPartSnapshot pp) => pp.partInfo != null ? pp.partInfo.title : pp.partName;

        private static void Log(string kerbal, string from, string to, string craft) =>
            Debug.Log("[KspFlightPlanner] Kerbin Resources: moved " + kerbal + " from " + from + " to " + to + " aboard " + craft + ".");
    }
}
