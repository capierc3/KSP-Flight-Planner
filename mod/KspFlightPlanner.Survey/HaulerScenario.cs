using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Haulers
{
    /// <summary>
    /// Runs the haulers' routes inside the game, every frame, in flight, the tracking station and the space centre. The
    /// routes are kept in the save (this is a scenario module), so they carry on after a restart and rewind with a
    /// quickload or revert.
    ///
    /// It decides who flies each route: <see cref="RailsFlyer"/> while the ship is on rails (the only flyer so far; the
    /// physics flyer, for when the ship is loaded near the player, comes next), and <see cref="CourseCheck"/> in between,
    /// after the ship or its target has been off rails.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.TRACKSTATION, GameScenes.SPACECENTER)]
    public sealed class HaulerScenario : ScenarioModule
    {
        internal static HaulerScenario Instance { get; private set; }

        internal readonly List<HaulerRoute> Routes = new List<HaulerRoute>();

        public override void OnAwake()
        {
            Instance = this;
            GameEvents.onVesselGoOffRails.Add(OnVesselGoOffRails);
        }

        private void OnDestroy()
        {
            GameEvents.onVesselGoOffRails.Remove(OnVesselGoOffRails);
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// A ship's physics came on (the player is flying it or near it): if it's a flying hauler or its target, something
        /// may move it now, so its plan is checked again once it's back on rails. Nothing is checked until then.
        /// </summary>
        private void OnVesselGoOffRails(Vessel v)
        {
            if (v == null) return;
            foreach (var route in Routes)
            {
                if (route.State != RouteState.Flying || route.Disturbed || (v.persistentId != route.VesselId && v.persistentId != route.TargetId)) continue;
                route.Disturbed = true;
                Note(route, v.vesselName + " is off rails: the plan is checked again once it's back on them.", false);
            }
        }

        public override void OnSave(ConfigNode node)
        {
            foreach (var r in Routes) r.Save(node.AddNode("ROUTE"));
        }

        public override void OnLoad(ConfigNode node)
        {
            Routes.Clear();
            foreach (var n in node.GetNodes("ROUTE"))
            {
                try { Routes.Add(HaulerRoute.Load(n)); }
                catch (Exception e) { Debug.LogError("[KspFlightPlanner] Couldn't read a hauler route: " + e.Message); }
            }
        }

        private void Update()
        {
            // Give the scene a moment to set up its ships' orbits before moving any.
            if (Time.timeSinceLevelLoad < 1f || FlightGlobals.fetch == null || FlightGlobals.Vessels == null) return;
            var now = Planetarium.GetUniversalTime();
            foreach (var route in Routes)
            {
                // Back on rails (or unloaded) after being disturbed: still on course?
                if (route.State == RouteState.Flying && route.Disturbed) CourseCheck.CheckIfResting(route, now);
                // A few events in one frame at most; whatever is left goes next frame.
                for (var i = 0; i < 4 && route.State == RouteState.Flying && route.NextUt <= now; i++)
                    RailsFlyer.Step(route, now);
                if (route.State == RouteState.Arrived && HighLogic.LoadedSceneIsFlight) RailsFlyer.Dock(route);
                if (route.State == RouteState.OffCourse) CourseCheck.WatchOffCourse(route);
            }
        }

        internal static void Note(HaulerRoute route, string line, bool onScreen)
        {
            route.Log.Add(line);
            Debug.Log("[KspFlightPlanner] " + route.VesselName + ": " + line);
            if (onScreen) ScreenMessages.PostScreenMessage(route.VesselName + ": " + line, 8f, ScreenMessageStyle.UPPER_CENTER);
        }

        internal static void Stop(HaulerRoute route, string why)
        {
            route.State = RouteState.Stopped;
            route.Log.Add("Stopped: " + why);
            Debug.LogWarning("[KspFlightPlanner] " + route.VesselName + ": stopped: " + why);
            ScreenMessages.PostScreenMessage(route.VesselName + " stopped its route: " + why, 10f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
