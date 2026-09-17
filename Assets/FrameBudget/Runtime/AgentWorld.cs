using System;
using UnityEngine;

namespace FrameBudget
{
    /// <summary>
    /// Owns all agent state as parallel arrays of structs (structure-of-arrays), indexed by agent.
    /// There is deliberately no per-agent object and no MonoBehaviour: every technique planned for
    /// the week (spatial hash, allocation-free paths, tick budgeting, GPU instancing straight from
    /// the position array, Burst jobs over NativeArrays) is possible only because the state is laid
    /// out this way. Spawning is driven by the seeded RNG, so two runs of the same config produce
    /// the same simulation; <see cref="StateHash"/> lets that be checked.
    /// </summary>
    public sealed class AgentWorld
    {
        public int Count { get; private set; }
        public float HalfExtent { get; private set; }
        public int Seed { get; private set; }

        /// <summary>Number of fixed-timestep steps applied since the last (re)spawn.</summary>
        public long StepCount { get; internal set; }

        public Vector3[] Positions = Array.Empty<Vector3>();
        public Vector3[] Velocities = Array.Empty<Vector3>();
        public Vector3[] Goals = Array.Empty<Vector3>();
        public int[] TickBuckets = Array.Empty<int>();

        /// <summary>Per-agent RNG stream, used when an agent picks its next goal. Keeps goal selection deterministic and order-independent.</summary>
        public uint[] RngStates = Array.Empty<uint>();

        /// <summary>Scratch output of the steering phase, so a step reads only the previous step's state.</summary>
        public Vector3[] NextVelocities = Array.Empty<Vector3>();

        /// <summary>
        /// Reused neighbour scratch for the zeroAlloc path, sized for the worst case of every other
        /// agent being in range. Allocated once per respawn rather than once per query, which is the
        /// whole point of the technique. Only the count a query returns is meaningful.
        /// </summary>
        public int[] NeighbourBuffer = Array.Empty<int>();

        /// <summary>(Re)creates every agent from the config's seed. Same config and count => identical arrays.</summary>
        public void Respawn(SimConfig config, int count)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "Agent count must be >= 0.");

            Count = count;
            HalfExtent = config.worldHalfExtent;
            Seed = config.seed;
            StepCount = 0;

            Positions = new Vector3[count];
            Velocities = new Vector3[count];
            Goals = new Vector3[count];
            TickBuckets = new int[count];
            RngStates = new uint[count];
            NextVelocities = new Vector3[count];
            NeighbourBuffer = new int[Mathf.Max(count, 1)];

            uint spawnRng = DeterministicRng.Seed(config.seed, 0);
            float initialSpeedCap = config.maxSpeed * 0.5f;
            for (int i = 0; i < count; i++)
            {
                Positions[i] = RandomPoint(ref spawnRng, HalfExtent);
                float heading = DeterministicRng.NextRange(ref spawnRng, 0f, 2f * Mathf.PI);
                float speed = DeterministicRng.NextRange(ref spawnRng, 0f, initialSpeedCap);
                Velocities[i] = new Vector3(Mathf.Cos(heading) * speed, 0f, Mathf.Sin(heading) * speed);
                Goals[i] = RandomPoint(ref spawnRng, HalfExtent);
                TickBuckets[i] = i % config.tickBucketCount;
                RngStates[i] = DeterministicRng.Seed(config.seed, i + 1);
            }
        }

        /// <summary>Uniform point on the XZ plane inside the world square.</summary>
        public static Vector3 RandomPoint(ref uint rng, float halfExtent)
        {
            float x = DeterministicRng.NextRange(ref rng, -halfExtent, halfExtent);
            float z = DeterministicRng.NextRange(ref rng, -halfExtent, halfExtent);
            return new Vector3(x, 0f, z);
        }

        /// <summary>FNV-1a over the exact float bits of every position and velocity. Equal hashes => equal state.</summary>
        public ulong StateHash()
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong h = offset;
            h = (h ^ (uint)Count) * prime;
            h = (h ^ (ulong)StepCount) * prime;
            for (int i = 0; i < Count; i++)
            {
                h = HashVector(h, Positions[i], prime);
                h = HashVector(h, Velocities[i], prime);
            }
            return h;
        }

        private static ulong HashVector(ulong h, Vector3 v, ulong prime)
        {
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(v.x)) * prime;
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(v.y)) * prime;
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(v.z)) * prime;
            return h;
        }
    }
}
