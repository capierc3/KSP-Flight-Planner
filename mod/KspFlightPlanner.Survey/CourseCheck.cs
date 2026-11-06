using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Haulers
{
    /// <summary>
    /// Keeps a route honest between the flyers: checks once when it starts, and again after the ship or its target has been
    /// off rails (physics running, so something may have moved it). A route that would miss goes off course: it pauses,
    /// time warp stops, and the planner plans it again from where the ship is. docs/MATH.md §11.3.
    /// </summary>
    internal static class CourseCheck
    {
        /// <summary>A check that finds the ship would meet the target further off than this, or faster, calls it off course.</summary>
        internal const double OffCourseM = 1000, OffCourseMs = 5;

        /// <summary>
        /// Checks the rest of the plan from where the ship really is (from <paramref name="fromUt"/>): flown as planned,
        /// does it still meet the target? If not, the route goes off course. True when it's on course.
        /// </summary>
        internal static bool Check(HaulerRoute route, Vessel ship, double fromUt, string when)
        {
            var target = HaulerShip.Find(route.TargetId);
            if (target == null)
            {
                HaulerScenario.Stop(route, route.TargetName + " is gone.");
                return false;
            }
            var meeting = HaulerShip.PredictMeeting(ship, target, route.Burns.Where(b => !b.Done), fromUt, route.MeetUt);
            if (meeting.Problem == null && meeting.GapM <= OffCourseM && meeting.SpeedMs <= OffCourseMs)
            {
                HaulerScenario.Note(route, when + ": on course, meets " + route.TargetName + " within " + meeting + ".", false);
                return true;
            }
            GoOffCourse(route, ship, when + ": " + (meeting.Problem ?? "would meet " + route.TargetName + " " + meeting + " off"));
            return false;
        }

        /// <summary>Physics is running on it (loaded and unpacked): the player is flying it, or something near it.</summary>
        internal static bool OffRails(Vessel v) => v != null && v.loaded && !v.packed;

        /// <summary>A disturbed route whose ship and target are both back on rails (or unloaded): check it once, now.</summary>
        internal static void CheckIfResting(HaulerRoute route, double now)
        {
            var ship = HaulerShip.Find(route.VesselId);
            var target = HaulerShip.Find(route.TargetId);
            if (ship == null || target == null || OffRails(ship) || OffRails(target)) return;
            route.Disturbed = false;
            try { Check(route, ship, Math.Min(now, route.NextUt), "Back on rails"); }
            catch (Exception e)
            {
                Debug.LogException(e);
                HaulerScenario.Stop(route, e.Message);
            }
        }

        /// <summary>Pauses the route (no more burns, no parking) and stops time warp, so the planner can plan it again in time.</summary>
        private static void GoOffCourse(HaulerRoute route, Vessel ship, string why)
        {
            route.State = RouteState.OffCourse;
            route.ReplanNote = null;
            route.OffCourseBody = ship.orbitDriver.orbit.referenceBody.bodyName;
            route.Log.Add("Off course: " + why);
            Debug.LogWarning("[KspFlightPlanner] " + route.VesselName + ": off course: " + why);
            ScreenMessages.PostScreenMessage(route.VesselName + " is off course (" + why + "): the planner plans it again from where it is.", 10f,
                ScreenMessageStyle.UPPER_CENTER);
            StopWarp();
        }

        /// <summary>An off-course ship that leaves the body it was round can be planned now: stop time warp for it.</summary>
        internal static void WatchOffCourse(HaulerRoute route)
        {
            var ship = HaulerShip.Find(route.VesselId);
            if (ship == null || route.OffCourseBody == null) return;
            var body = ship.orbitDriver.orbit.referenceBody.bodyName;
            if (body == route.OffCourseBody) return;
            HaulerScenario.Note(route, "Now round " + body + ": the planner can plan it again.", true);
            route.OffCourseBody = body;
            StopWarp();
        }

        internal static void StopWarp()
        {
            try
            {
                if (TimeWarp.fetch != null && TimeWarp.CurrentRateIndex > 0) TimeWarp.SetRate(0, true);
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>The planner couldn't plan an off-course route again: said once per reason.</summary>
        internal static void ReplanProblem(HaulerRoute route, string why)
        {
            if (route.ReplanNote == why) return;
            route.ReplanNote = why;
            HaulerScenario.Note(route, "Can't plan it again yet: " + why, true);
        }
    }
}
