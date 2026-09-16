using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FrameBudget
{
    /// <summary>
    /// One fixed-timestep step of seek-to-goal + separation steering, written the naive way on purpose.
    /// Every anti-pattern below is a control condition: it stays until the technique named in its
    /// comment removes it, and the removal is measured. Do not "tidy" these.
    /// </summary>
    public static class NaiveSimulationStep
    {
        public static void Step(AgentWorld world, SimConfig config, float dt)
        {
            int n = world.Count;
            Vector3[] positions = world.Positions;
            Vector3[] velocities = world.Velocities;
            Vector3[] goals = world.Goals;
            Vector3[] next = world.NextVelocities;
            uint[] rngStates = world.RngStates;

            float radiusSq = config.neighbourRadius * config.neighbourRadius;
            float maxSpeed = config.maxSpeed;
            float maxVelocityDelta = config.maxAcceleration * dt;
            float separationWeight = config.separationWeight;
            float goalRadiusSq = config.goalReachedRadius * config.goalReachedRadius;
            float half = world.HalfExtent;

            // Phase 1 - steering. Reads only the previous step's state and writes NextVelocities, so
            // the result does not depend on agent order. That keeps later parallel versions
            // comparable with this one instead of "different but probably fine".
            for (int i = 0; i < n; i++)
            {
                Vector3 pos = positions[i];

                // CONTROL: O(n^2) neighbour query - every agent tests every other agent.
                //          Replaced by the spatialHash technique.
                // CONTROL: LINQ with a capturing lambda in the hot path - allocates a closure, an
                //          iterator chain and a List per agent per step, so the GC runs on a timer set
                //          by the agent count. Replaced by the zeroAlloc technique.
                List<int> neighbours = Enumerable.Range(0, n)
                    .Where(j => j != i && (positions[j] - pos).sqrMagnitude < radiusSq)
                    .ToList();

                Vector3 separation = Vector3.zero;
                foreach (int j in neighbours)
                {
                    Vector3 away = pos - positions[j];
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
            // CONTROL: every agent is integrated every step regardless of its tick bucket.
            //          Replaced by the tickBudget technique (one bucket per step).
            // CONTROL: single-threaded managed loops over managed arrays throughout this file.
            //          Replaced by the burstJobs technique (IJobParallelFor over NativeArrays).
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
