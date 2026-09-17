using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FrameBudget
{
    /// <summary>
    /// The all-pairs neighbour query, extracted unchanged from the original step so that it remains
    /// a genuine control rather than a rewritten approximation of one. Every agent tests every other
    /// agent: O(n^2) comparisons per step.
    ///
    /// CONTROL: the O(n^2) scan is what the spatialHash technique replaces.
    /// CONTROL: the LINQ chain with a capturing lambda allocates a closure, an iterator and a List
    ///          for every agent on every step. That is a separate control, removed by the zeroAlloc
    ///          technique on a later day, and it is deliberately left in place here so that the
    ///          spatial-hash measurement is not quietly measuring an allocation fix as well.
    ///
    /// Enumerable.Range walks indices in ascending order and Where preserves it, so the ascending
    /// order the interface requires holds by construction. No sort is applied, because sorting an
    /// already-ordered list would add cost to the baseline and flatter the comparison.
    /// </summary>
    public sealed class BruteForceIndex : ISpatialIndex
    {
        public string Name => "brute-force";

        public void Rebuild(AgentWorld world, SimConfig config)
        {
            // Nothing to build: the query scans the position array directly.
        }

        public List<int> Query(AgentWorld world, int agentIndex, float radius)
        {
            Vector3[] positions = world.Positions;
            Vector3 pos = positions[agentIndex];
            float radiusSq = radius * radius;
            int n = world.Count;

            return Enumerable.Range(0, n)
                .Where(j => j != agentIndex && (positions[j] - pos).sqrMagnitude < radiusSq)
                .ToList();
        }
    }
}
