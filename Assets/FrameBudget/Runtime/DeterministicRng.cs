namespace FrameBudget
{
    /// <summary>
    /// A tiny xorshift32 generator operating on a caller-owned uint state, so every agent can carry
    /// its own stream in a plain array. Deliberately not System.Random: this has no hidden state,
    /// no platform-dependent algorithm, and will run unchanged inside a Burst job later.
    /// </summary>
    public static class DeterministicRng
    {
        /// <summary>Derives an independent, non-zero starting state from a seed and a stream index (e.g. agent index).</summary>
        public static uint Seed(int seed, int stream)
        {
            uint s = Mix(unchecked((uint)seed * 0x9E3779B9u + (uint)stream * 0x85EBCA6Bu + 0x27D4EB2Fu));
            return s == 0u ? 0x6D2B79F5u : s;
        }

        /// <summary>lowbias32 integer hash: spreads nearby seeds far apart.</summary>
        public static uint Mix(uint x)
        {
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }

        public static uint NextUInt(ref uint state)
        {
            uint x = state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            state = x;
            return x;
        }

        /// <summary>Uniform float in [0, 1).</summary>
        public static float NextFloat01(ref uint state)
        {
            return (NextUInt(ref state) >> 8) * (1f / 16777216f);
        }

        /// <summary>Uniform float in [min, max).</summary>
        public static float NextRange(ref uint state, float min, float max)
        {
            return min + (max - min) * NextFloat01(ref state);
        }
    }
}
