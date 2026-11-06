using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Haulers
{
    /// <summary>
    /// Balancing a ship's RCS so a push sideways moves it without turning it: one thrust limiter per RCS block, chosen so
    /// that for each of the six pushes (left, right, up, down, forward, back) the torque of the thrusters that fire comes
    /// as near zero about the centre of mass as it can, while keeping as much thrust as it can. A block has one limiter
    /// for every direction, so it's a best fit over the six. docs/MATH.md §11.9.
    /// </summary>
    internal static class RcsBalance
    {
        /// <summary>No block goes below this share of its thrust.</summary>
        private const float MinShare = 0.05f;

        /// <summary>How much keeping thrust weighs against killing torque (per kN², in the sum the fit makes small).</summary>
        private const float KeepThrust = 0.02f;

        /// <summary>
        /// Sets the limiters of every RCS block on the ship; returns the leftover turning as a share of the push, before and
        /// after (the worst of the six directions: torque over thrust times the ship's size, so 0 is perfect and 1 means a
        /// push turns it as hard as a thruster on its far end would). False when it has no RCS.
        /// </summary>
        internal static bool Balance(Vessel v, out float before, out float after)
        {
            before = after = 0;
            var blocks = v.FindPartModulesImplementing<ModuleRCS>();
            if (blocks.Count == 0) return false;
            var com = v.CoM;
            var size = Math.Max(1f, (float)HaulerShip.Size(v));
            var axes = new[] { v.ReferenceTransform.right, v.ReferenceTransform.up, v.ReferenceTransform.forward };

            // For each block and each push: the force and torque of its thrusters that would fire for it. The sign of a
            // thruster's direction doesn't matter: each push and its opposite are both counted.
            var force = new Vector3[blocks.Count, 6];
            var torque = new Vector3[blocks.Count, 6];
            for (var m = 0; m < blocks.Count; m++)
            {
                var rcs = blocks[m];
                foreach (var t in rcs.thrusterTransforms)
                {
                    if (t == null) continue;
                    var f = (rcs.useZaxis ? t.forward : t.up) * rcs.thrusterPower;
                    var arm = t.position - com;
                    for (var d = 0; d < 6; d++)
                    {
                        var e = d < 3 ? axes[d] : -axes[d - 3];
                        if (Vector3.Dot(f.normalized, e) < 0.3f) continue;
                        force[m, d] += f;
                        torque[m, d] += Vector3.Cross(arm, f);
                    }
                }
            }

            var share = blocks.Select(b => 1f).ToArray();
            before = Imbalance(share, force, torque, size);
            // Projected gradient descent on Σ_d |Σ_m s_m τ_md|² + k Σ_m (1 − s_m)², each s_m kept in [MinShare, 1].
            var step = 0.5f / Math.Max(1e-3f, Enumerable.Range(0, 6).Sum(d => Enumerable.Range(0, blocks.Count).Sum(m => torque[m, d].sqrMagnitude)));
            for (var i = 0; i < 2000; i++)
            {
                for (var d = 0; d < 6; d++)
                {
                    var total = Vector3.zero;
                    for (var m = 0; m < blocks.Count; m++) total += share[m] * torque[m, d];
                    for (var m = 0; m < blocks.Count; m++)
                        share[m] -= step * 2 * Vector3.Dot(total, torque[m, d]);
                }
                for (var m = 0; m < blocks.Count; m++)
                    share[m] = Mathf.Clamp(share[m] + step * 2 * KeepThrust * (1 - share[m]) * 100, MinShare, 1);
            }
            var top = share.Max();
            for (var m = 0; m < blocks.Count; m++) share[m] = Mathf.Clamp(share[m] / top, MinShare, 1);
            after = Imbalance(share, force, torque, size);
            if (after >= before) return true; // already as good as it gets: leave the limiters alone
            for (var m = 0; m < blocks.Count; m++) blocks[m].thrustPercentage = 100 * share[m];
            return true;
        }

        private static float Imbalance(float[] share, Vector3[,] force, Vector3[,] torque, float size)
        {
            var worst = 0f;
            for (var d = 0; d < 6; d++)
            {
                Vector3 f = Vector3.zero, t = Vector3.zero;
                for (var m = 0; m < share.Length; m++)
                {
                    f += share[m] * force[m, d];
                    t += share[m] * torque[m, d];
                }
                if (f.magnitude > 1e-4f) worst = Math.Max(worst, t.magnitude / (f.magnitude * size));
            }
            return worst;
        }
    }
}
