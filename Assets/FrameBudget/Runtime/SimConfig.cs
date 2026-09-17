using System;
using System.Text;
using UnityEngine;

namespace FrameBudget
{
    /// <summary>
    /// One point on the technique axis. Kept as a serialisable struct rather than reusing the
    /// config's own flags so that a single sweep can measure several combinations against each
    /// other, interleaved, without mutating the asset it was launched from.
    /// </summary>
    [Serializable]
    public struct TechniqueCombination
    {
        public bool spatialHash;
        public bool zeroAlloc;
        public bool tickBudget;
        public bool gpuInstancing;
        public bool burstJobs;

        /// <summary>"baseline", or the '+'-joined names of the enabled techniques.</summary>
        public string Label
        {
            get
            {
                var sb = new StringBuilder();
                if (spatialHash) sb.Append("spatialHash+");
                if (zeroAlloc) sb.Append("zeroAlloc+");
                if (tickBudget) sb.Append("tickBudget+");
                if (gpuInstancing) sb.Append("gpuInstancing+");
                if (burstJobs) sb.Append("burstJobs+");
                if (sb.Length == 0) return "baseline";
                sb.Length -= 1;
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// Everything a run needs in order to be reproduced. One asset describes one run:
    /// same asset, same simulation, same workload. Technique flags all default to false;
    /// each one is a control condition today and a measured optimisation later in the week.
    /// </summary>
    [CreateAssetMenu(fileName = "SimConfig", menuName = "Frame Budget/Sim Config")]
    public sealed class SimConfig : ScriptableObject
    {
        [Header("Simulation")]
        [Min(0)] public int agentCount = 1000;

        [Tooltip("The world is a square on the XZ plane centred on the origin, extending this far in +/-X and +/-Z.")]
        [Min(1f)] public float worldHalfExtent = 100f;

        [Tooltip("Seed for every random decision: spawn positions, initial velocities, goals. Same seed + same config = same simulation.")]
        public int seed = 12345;

        [Tooltip("Fixed simulation timestep in seconds. Steps are accumulated from frame time and never sized by it, so the work per step does not depend on frame rate.")]
        [Min(0.0001f)] public float fixedTimestep = 1f / 60f;

        [Tooltip("Upper bound on simulation steps per rendered frame. When the simulation cannot keep up with real time the frame stops here, and the simulated time it fell behind by is counted and reported instead of spiralling into ever-longer frames.")]
        [Range(1, 8)] public int maxStepsPerFrame = 1;

        [Header("Steering")]
        [Min(0.01f)] public float neighbourRadius = 4f;
        [Min(0.01f)] public float maxSpeed = 6f;
        [Min(0.01f)] public float maxAcceleration = 12f;
        [Min(0f)] public float separationWeight = 4f;
        [Min(0.01f)] public float goalReachedRadius = 1f;

        [Tooltip("Agents are labelled with a tick bucket at spawn. The naive baseline updates every agent every step regardless; the tickBudget technique will update one bucket per step.")]
        [Min(1)] public int tickBucketCount = 4;

        [Tooltip("Side length of a uniform-grid cell in world units, used when the spatialHash technique is on. Zero means use the neighbour radius, which makes the searched neighbourhood exactly one ring of cells. Smaller cells mean more cells to visit but fewer agents rejected per cell; the query widens its ring automatically so a smaller cell size stays correct.")]
        [Min(0f)] public float spatialHashCellSize;

        [Header("Benchmark")]
        [Tooltip("Frames discarded at the start of every measured point (absorbs spawn cost, JIT and cache warm-up). At least one frame is always discarded so the spawn frame itself is never measured.")]
        [Min(1)] public int warmupFrameCount = 60;

        [Tooltip("Frames recorded per point after warm-up. Median and 95th percentile are computed over exactly these frames.")]
        [Min(1)] public int measuredFrameCount = 300;

        [Tooltip("Agent counts the benchmark sweeps through, in order. Empty = just agentCount.")]
        public int[] sweepAgentCounts = { 100, 250, 500, 1000, 2000 };

        [Tooltip("How many times each agent count is measured. Runs are identical simulations (same seed); differences between them are machine noise, which is what repeated runs are for.")]
        [Min(1)] public int runsPerAgentCount = 3;

        [Header("Techniques (all false = naive baseline)")]
        public bool spatialHash;
        public bool zeroAlloc;
        public bool tickBudget;
        public bool gpuInstancing;
        public bool burstJobs;

        [Tooltip("Technique combinations the benchmark sweeps through. Leave empty to measure only the flags above. " +
                 "Listing combinations here is what lets a technique be measured against its own control inside one run, " +
                 "interleaved with it, instead of as a separate sweep whose thermal drift would land on the technique axis.")]
        public TechniqueCombination[] techniqueSweep = Array.Empty<TechniqueCombination>();

        /// <summary>The combinations to measure: the explicit sweep list, or this config's own flags when the list is empty.</summary>
        public TechniqueCombination[] ResolveTechniqueSweep()
        {
            if (techniqueSweep != null && techniqueSweep.Length > 0) return (TechniqueCombination[])techniqueSweep.Clone();
            return new[] { CurrentTechniques };
        }

        public TechniqueCombination CurrentTechniques => new TechniqueCombination
        {
            spatialHash = spatialHash,
            zeroAlloc = zeroAlloc,
            tickBudget = tickBudget,
            gpuInstancing = gpuInstancing,
            burstJobs = burstJobs,
        };

        /// <summary>"baseline" or the '+'-joined names of the enabled techniques. Used in the HUD and the CSV.</summary>
        public string TechniqueLabel
        {
            get
            {
                var sb = new StringBuilder();
                if (spatialHash) sb.Append("spatialHash+");
                if (zeroAlloc) sb.Append("zeroAlloc+");
                if (tickBudget) sb.Append("tickBudget+");
                if (gpuInstancing) sb.Append("gpuInstancing+");
                if (burstJobs) sb.Append("burstJobs+");
                if (sb.Length == 0) return "baseline";
                sb.Length -= 1;
                return sb.ToString();
            }
        }

        /// <summary>Returns null when the config is usable, otherwise a message listing every problem. Fail early, fail loudly.</summary>
        public string Validate()
        {
            var sb = new StringBuilder();
            if (agentCount < 0) sb.Append("agentCount must be >= 0. ");
            if (worldHalfExtent <= 0f) sb.Append("worldHalfExtent must be > 0. ");
            if (fixedTimestep <= 0f) sb.Append("fixedTimestep must be > 0. ");
            if (maxStepsPerFrame < 1) sb.Append("maxStepsPerFrame must be >= 1. ");
            if (neighbourRadius <= 0f) sb.Append("neighbourRadius must be > 0. ");
            if (maxSpeed <= 0f) sb.Append("maxSpeed must be > 0. ");
            if (maxAcceleration <= 0f) sb.Append("maxAcceleration must be > 0. ");
            if (goalReachedRadius <= 0f) sb.Append("goalReachedRadius must be > 0. ");
            if (tickBucketCount < 1) sb.Append("tickBucketCount must be >= 1. ");
            if (warmupFrameCount < 1) sb.Append("warmupFrameCount must be >= 1 (the spawn frame must never be measured). ");
            if (measuredFrameCount < 1) sb.Append("measuredFrameCount must be >= 1. ");
            if (runsPerAgentCount < 1) sb.Append("runsPerAgentCount must be >= 1. ");
            if (sweepAgentCounts != null)
            {
                for (int i = 0; i < sweepAgentCounts.Length; i++)
                {
                    if (sweepAgentCounts[i] < 0) sb.Append("sweepAgentCounts[").Append(i).Append("] must be >= 0. ");
                }
            }
            return sb.Length == 0 ? null : sb.ToString().TrimEnd();
        }
    }
}
