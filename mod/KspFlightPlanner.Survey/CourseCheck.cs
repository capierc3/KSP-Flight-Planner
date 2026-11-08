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
        /// <param name="expected">The ship was moved on purpose (backing off a station): planning it again is routine, so it's said as that, not as a fault.</param>
        internal static bool Check(HaulerRoute route, Vessel ship, double fromUt, string when, bool expected = false)
        {
            // A trip to an orbit with its last burn made: there (how close is said when it arrives). Its place along the
            // orbit is no longer worth correcting, only its shape was the point.
            if (route.EndsInOrbit && route.Burns.All(b => b.Done)) return true;
            var to = GoalOf(route);
            if (to == null)
            {
                HaulerScenario.Stop(route, (route.EndsInOrbit ? route.GoalBody + " isn't in this game." : route.TargetName + " is gone."));
                return false;
            }
            var meeting = HaulerShip.PredictMeeting(ship, to, route.Burns.Where(b => !b.Done), fromUt, route.MeetUt);
            if (meeting.Problem == null && meeting.GapM <= Tolerance(route.MeetUt - fromUt) && meeting.SpeedMs <= OffCourseMs)
            {
                HaulerScenario.Note(route, when + ": on course, " + Meets(route) + " within " + meeting + ".", false);
                return true;
            }
            // A small miss: a course correction puts it back on the plan, keeping its schedule, rather than a whole new plan.
            if (meeting.Problem == null && meeting.GapM < CorrectUnderM && Correct(route, ship, to, fromUt, when + ": would " + (route.EndsInOrbit ? "reach its orbit " : "meet ") + meeting + " off"))
                return true;
            GoOffCourse(route, ship, when + ": " + (meeting.Problem ?? "would " + Meets(route) + " " + meeting + " off"), expected);
            return false;
        }

        /// <summary>
        /// What the route aims at, as an orbit: the target craft's, or for a trip to an orbit the planned orbit itself
        /// (docs/MATH.md §11.16). Null when the craft is gone or the body isn't in the game.
        /// </summary>
        internal static Orbit GoalOf(HaulerRoute route) =>
            route.EndsInOrbit ? route.GoalOrbit() : HaulerShip.Find(route.TargetId)?.orbitDriver.orbit;

        /// <summary>"meets Mun Station", or "reaches the planned Mun orbit", for the log.</summary>
        internal static string Meets(HaulerRoute route) => route.EndsInOrbit ? "reaches the planned " + route.GoalBody + " orbit" : "meets " + route.TargetName;

        /// <summary>
        /// How far off the meeting may be predicted before it counts as off course (m): 1 km within an hour of it, a km more
        /// for each hour further, 5 km at most. Predicting hours ahead through KSP's orbits isn't steady to better than about
        /// a kilometre (the same moment came out 29 m and 1,092 m apart, 2026-11-06), and braking in fixes the last of it.
        /// </summary>
        internal static double Tolerance(double toMeetS) => Math.Max(OffCourseM, Math.Min(5000, OffCourseM * (1 + Math.Max(0, toMeetS) / 3600)));

        /// <summary>A miss under this (m) is put right with a course correction instead of a new plan.</summary>
        internal const double CorrectUnderM = 100000;

        /// <summary>A correction is placed this long from now (game s), or the next burn is the one corrected when it's that soon.</summary>
        private const double CorrectLeadS = 120;

        /// <summary>A correction smaller than this (m/s) isn't added as a burn.</summary>
        private const double MinCorrectionMs = 0.2;

        /// <summary>The most a correction may change a burn by (m/s); more than that is a new plan's job.</summary>
        private const double CorrectMaxMs = 60;

        /// <summary>
        /// A course correction (docs/MATH.md §11.3): a burn two minutes from now (or the next burn, if it's sooner than
        /// that), solved so that, flown as planned from there, the ship is at the target's position at the meeting time:
        /// three unknowns (its prograde, normal and radial Δv) for the three of the miss, by Newton's method with the
        /// slopes measured by nudging each by 0.1 m/s through KSP's own prediction. Then the match at the meeting is set to
        /// the speed difference it really arrives with. Nothing is planned: the burns and meeting time stay as they were.
        /// False (and nothing changed) when it doesn't converge, needs more than 60 m/s, or the fuel isn't there.
        /// </summary>
        private static bool Correct(HaulerRoute route, Vessel ship, Orbit to, double now, string why, bool intoNextBurn = false)
        {
            var left = route.Burns.Where(b => !b.Done).OrderBy(b => b.Ut).ToList();
            var match = left.Count > 0 && left[left.Count - 1].Ut >= route.MeetUt - 1 ? left[left.Count - 1] : null;
            var middle = match != null ? left.Take(left.Count - 1).ToList() : left;
            var next = middle.FirstOrDefault();
            var insert = !intoNextBurn && (next == null || next.Ut > now + 2 * CorrectLeadS);
            if (intoNextBurn && next == null) return false;
            var fix = insert ? new HaulerBurn { Ut = now + CorrectLeadS, Title = "Course correction" } : next;
            if (fix.Ut >= route.MeetUt - 60) return false;
            var start = new Vector3d(fix.Prograde, fix.Normal, fix.Radial);
            var burns = insert ? middle.Concat(new[] { fix }).ToList() : middle;

            Vector3d? Miss(Vector3d dv)
            {
                fix.Prograde = dv.x;
                fix.Normal = dv.y;
                fix.Radial = dv.z;
                var o = HaulerShip.PathAt(ship, burns, now, route.MeetUt);
                if (o.referenceBody != to.referenceBody) return null;
                var gap = o.getRelativePositionAtUT(route.MeetUt) - to.getRelativePositionAtUT(route.MeetUt);
                return double.IsNaN(gap.x) ? (Vector3d?)null : gap;
            }

            var x = start;
            var ok = false;
            for (var i = 0; i < 10; i++)
            {
                var g = Miss(x);
                if (g == null) break;
                if (g.Value.magnitude < 100)
                {
                    ok = true;
                    break;
                }
                const double h = 0.1;
                var cx = (Miss(x + new Vector3d(h, 0, 0)) - g) / h;
                var cy = (Miss(x + new Vector3d(0, h, 0)) - g) / h;
                var cz = (Miss(x + new Vector3d(0, 0, h)) - g) / h;
                if (cx == null || cy == null || cz == null) break;
                if (!Solve(cx.Value, cy.Value, cz.Value, -g.Value, out var step)) break;
                if (step.magnitude > CorrectMaxMs) step = step.normalized * CorrectMaxMs; // a damped step far from the answer
                x += step;
            }
            if (!ok || (x - start).magnitude > CorrectMaxMs)
            {
                Miss(start);
                return false;
            }
            // Too small to fly as a burn of its own (a burn stops when under 0.05 m/s is left, so a few hundredths never
            // happen, and on an escape path a few hundredths is tens of km at the far end, 2026-11-06): folded into the next
            // planned burn instead, which is big enough to fly exactly. With no burn to fold it into, it's left: on course.
            if (insert && (x - start).magnitude < MinCorrectionMs)
            {
                Miss(start);
                if (next != null && Correct(route, ship, to, now, why, intoNextBurn: true)) return true;
                HaulerScenario.Note(route, why + "; a correction would be under " + MinCorrectionMs.ToString("0.0") + " m/s, and no burn left to fold it into: on course.", false);
                return true;
            }

            // The match at the meeting: the speed it really arrives with, in that moment's node axes.
            var oldMatch = match != null ? new Vector3d(match.Prograde, match.Normal, match.Radial) : Vector3d.zero;
            if (match != null)
            {
                var o = HaulerShip.PathAt(ship, burns, now, route.MeetUt);
                var pos = o.getRelativePositionAtUT(route.MeetUt);
                var vel = o.getOrbitalVelocityAtUT(route.MeetUt);
                HaulerShip.Axes(pos, vel, out var prograde, out var normal, out var radial);
                var dv = to.getOrbitalVelocityAtUT(route.MeetUt) - vel;
                match.Prograde = Vector3d.Dot(dv, prograde);
                match.Normal = Vector3d.Dot(dv, normal);
                match.Radial = Vector3d.Dot(dv, radial);
            }

            // Fuel for the rest, with the margin, or it's not worth fixing this way.
            var need = (insert ? middle.Concat(new[] { fix }) : middle).Sum(b => b.DeltaV) + (match?.DeltaV ?? 0);
            if (HaulerShip.DeltaVLeft(ship, out _) < need * 1.05 + 30)
            {
                Miss(start);
                if (match != null)
                {
                    match.Prograde = oldMatch.x;
                    match.Normal = oldMatch.y;
                    match.Radial = oldMatch.z;
                }
                return false;
            }

            if (insert)
            {
                route.Burns.Add(fix);
                route.Burns.Sort((a, b) => a.Ut.CompareTo(b.Ut));
            }
            var changed = (x - start).magnitude;
            HaulerScenario.Note(route, (insert ? "Course correction added: " + changed.ToString("0.0") + " m/s " : "Burn \"" + fix.Title + "\" corrected by " + changed.ToString("0.0") + " m/s ") +
                                       "at " + KSPUtil.dateTimeFormatter.PrintDateCompact(fix.Ut, true, true) + " (" + why + "); the meeting stays as planned.", true);
            return true;
        }

        /// <summary>Solves [a b c]·x = r for x (Cramer's rule); false when the columns are (nearly) dependent.</summary>
        private static bool Solve(Vector3d a, Vector3d b, Vector3d c, Vector3d r, out Vector3d x)
        {
            var det = Vector3d.Dot(a, Vector3d.Cross(b, c));
            x = Vector3d.zero;
            if (Math.Abs(det) < 1e-9) return false;
            x = new Vector3d(Vector3d.Dot(r, Vector3d.Cross(b, c)), Vector3d.Dot(a, Vector3d.Cross(r, c)), Vector3d.Dot(a, Vector3d.Cross(b, r))) / det;
            return true;
        }

        /// <summary>Physics is running on it (loaded and unpacked): the player is flying it, or something near it.</summary>
        internal static bool OffRails(Vessel v) => v != null && v.loaded && !v.packed;

        /// <summary>A disturbed route whose ship and target are both back on rails (or unloaded): check it once, now.</summary>
        internal static void CheckIfResting(HaulerRoute route, double now)
        {
            var ship = HaulerShip.Find(route.VesselId);
            var target = route.EndsInOrbit ? null : HaulerShip.Find(route.TargetId);
            if (ship == null || !route.EndsInOrbit && target == null || OffRails(ship) || OffRails(target)) return;
            // Packed mid-burn: the rest of the burn is made on rails, with no check first.
            if (PhysicsFlyer.HandBackCutBurn(route)) return;
            route.Disturbed = false;
            try { Check(route, ship, Math.Min(now, route.NextUt), "Back on rails"); }
            catch (Exception e)
            {
                Debug.LogException(e);
                HaulerScenario.Stop(route, e.Message);
            }
        }

        /// <summary>Pauses the route (no more burns, no parking) and stops time warp, so the planner can plan it again in time.</summary>
        private static void GoOffCourse(HaulerRoute route, Vessel ship, string why, bool expected = false)
        {
            // A trip to an orbit isn't planned again by the planner (yet): it stops where it is, to be planned from there.
            if (route.EndsInOrbit)
            {
                StopWarp();
                HaulerScenario.Stop(route, "off course (" + why + "): plan the trip again from where it is (From here › Go to) and send it again.");
                return;
            }
            route.State = RouteState.OffCourse;
            route.ReplanNote = null;
            route.OffCourseSince = Time.realtimeSinceStartup;
            route.WarnedNoPlanner = false;
            route.OffCourseBody = ship.orbitDriver.orbit.referenceBody.bodyName;
            route.Log.Add("Off course: " + why);
            Debug.LogWarning("[KspFlightPlanner] " + route.VesselName + ": off course: " + why);
            ScreenMessages.PostScreenMessage(expected
                    ? route.VesselName + " is clear: the planner plans its trip again from here."
                    : route.VesselName + " is off course (" + why + "): the planner plans it again from where it is.", 10f,
                ScreenMessageStyle.UPPER_CENTER);
            StopWarp();
        }

        /// <summary>An off-course ship that leaves the body it was round can be planned now: stop time warp for it.</summary>
        /// <summary>How long warp is held for a replan (real s) before giving up on the planner answering.</summary>
        private const float HoldWarpS = 30;

        internal static void WatchOffCourse(HaulerRoute route)
        {
            var ship = HaulerShip.Find(route.VesselId);
            if (ship == null || route.OffCourseBody == null) return;
            // No time warp while the planner plans it again (a few seconds), unless it's waiting for something warp
            // brings (leaving a moon's pull), or the planner hasn't answered in 30 s (not running?).
            if (route.ReplanNote == null && TimeWarp.fetch != null && TimeWarp.CurrentRateIndex > 0)
            {
                if (route.OffCourseSince < 0) route.OffCourseSince = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - route.OffCourseSince < HoldWarpS)
                {
                    StopWarp();
                    ScreenMessages.PostScreenMessage("No time warp while the planner plans " + route.VesselName + " again", 2f, ScreenMessageStyle.UPPER_CENTER);
                }
                else if (!route.WarnedNoPlanner)
                {
                    route.WarnedNoPlanner = true;
                    HaulerScenario.Note(route, "The planner hasn't planned it again: is it running and connected to KSP?", true);
                }
            }
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
                if (TimeWarp.fetch == null) return;
                // KSP's own "Warp to" keeps raising the rate towards its goal: cancel it, or the warp carries on.
                TimeWarp.fetch.CancelAutoWarp();
                if (TimeWarp.CurrentRateIndex > 0) TimeWarp.SetRate(0, true);
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
