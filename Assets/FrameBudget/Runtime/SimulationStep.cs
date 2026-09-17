using System.Collections.Generic;
using UnityEngine;

namespace FrameBudget
{
    /// <summary>
    /// One fixed-timestep step of seek-to-goal plus separation steering.
    ///
    /// The neighbour query is the only part that varies: it comes in through <see cref="ISpatialIndex"/>
    /// so that the brute-force control and the spatial hash can be swapped from a config flag and
    /// measured by the same harness, in the same sweep, with everything else held identical. The
    /// index's per-step rebuild happens inside the timed region, because a technique's setup cost is
    /// part of its cost.
    ///
    /// Everything else here is still deliberately naive, and each remaining control names the
    /// technique that will remove it:
    ///
    /// CONTROL: a List&lt;int&gt; is allocated per agent per step by the neighbour query.
    ///          Replaced by the zeroAlloc technique.
    /// CONTROL: every agent is integrated every step regardless of its tick bucket.
    ///          Replaced by the tickBudget technique.
    /// CONTROL: single-threaded managed loops over managed arrays throughout.
    ///          Replaced by the burstJobs technique.
    /// </summary>
    public static class SimulationStep
    {
        public static void Step(AgentWorld world, SimConfig config, float dt, ISpatialIndex index)
        {
            int n = world.Count;
            Vector3[] positions = world.Positions;
            Vector3[] velocities = world.Velocities;
            Vector3[] goals = world.Goals;
            Vector3[] next = world.NextVelocities;
            uint[] rngStates = world.RngStates;

            float radius = config.neighbourRadius;
            float maxSpeed = config.maxSpeed;
            float maxVelocityDelta = config.maxAcceleration * dt;
            float separationWeight = config.separationWeight;
            float goalRadiusSq = config.goalReachedRadius * config.goalReachedRadius;
            float half = world.HalfExtent;

            // The index sees the positions as they are at the start of the step, which is the same
            // state phase 1 reads. Rebuilding here rather than after integration is what keeps the
            // two implementations answering the same question.
            index.Rebuild(world, config);

            // Phase 1 - steering. Reads only the previous step's state and writes NextVelocities, so
            // the result does not depend on the order agents are visited in. That keeps the later
            // parallel versions comparable with this one instead of "different but probably fine".
            for (int i = 0; i < n; i++)
            {
                Vector3 pos = positions[i];

                // Ascending index order is part of the ISpatialIndex contract: the sum below is a
                // float accumulation, and float addition is not associative, so a different order is
                // a different simulation rather than a different implementation of the same one.
                List<int> neighbours = index.Query(world, i, radius);

                Vector3 separation = Vector3.zero;
                for (int k = 0; k < neighbours.Count; k++)
                {
                    Vector3 away = pos - positions[neighbours[k]];
                    float distSq = Mathf.Max(away.sqrMagnitude, 1e-4f);
                    separation += away / distSq;
                }

                Vector3 toGoal = goals[i] - pos;
                Vector3 desired = toGoal.sqrMagnitude > 1e-6f ? toGoal.normalized * maxSpeed : Vector3.zero;
                desired += separation * separationWeight;
                desired = Vector3.ClampMagnitude(desired, maxSpeed);

                next[i] = Vector3.MoveTowards(velocities[i], desired, maxVelocityDelta);
            }

            // Phase 2 - integrate and re-roll reached goals.
            for (int i = 0; i < n; i++)
            {
                Vector3 v = next[i];
                velocities[i] = v;

                Vector3 p = positions[i] + v * dt;
                p.x = Mathf.Clamp(p.x, -half, half);
                p.y = 0f;
                p.z = Mathf.Clamp(p.z, -half, half);
                positions[i] = p;

                if ((goals[i] - p).sqrMagnitude < goalRadiusSq)
                {
                    goals[i] = AgentWorld.RandomPoint(ref rngStates[i], half);
                }
            }

            world.StepCount++;
        }
    }
}
