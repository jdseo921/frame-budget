using System.Collections.Generic;

namespace FrameBudget
{
    /// <summary>
    /// How an agent finds the agents near it. The whole point of putting this behind an interface is
    /// that the two implementations can be swapped at runtime from a config flag, so the brute-force
    /// and spatial-hash configurations differ in exactly one thing and are measured by the same
    /// harness in the same run.
    ///
    /// The contract is deliberately strict about two properties, because a faster neighbour query
    /// that returns a different answer is not an optimisation, it is a different simulation:
    ///
    /// 1. <b>Same set.</b> A query must return exactly the agents j != i whose squared distance from
    ///    agent i is strictly less than radius squared. Not approximately; exactly. An index that
    ///    misses distant-but-in-range neighbours is fast and wrong, and the equivalence test in
    ///    Assets/Tests exists to catch precisely that.
    /// 2. <b>Same order.</b> The results must be in ascending index order. The caller sums a
    ///    separation force over them, floating-point addition is not associative, and so a different
    ///    visiting order produces a different sum, a different velocity, and after a few hundred
    ///    steps a visibly different simulation. Ascending order in both implementations is what lets
    ///    the project claim the optimisation changed the cost and not the result - a claim that
    ///    state_hash then verifies bit-for-bit.
    /// </summary>
    public interface ISpatialIndex
    {
        /// <summary>Short name for logs and CSV columns.</summary>
        string Name { get; }

        /// <summary>
        /// Prepares the index for the current agent positions. Called once per simulation step,
        /// before any query, because the agents have moved since the last one. Any per-step build
        /// cost belongs to the technique and is measured inside the step, not hidden outside it.
        /// </summary>
        void Rebuild(AgentWorld world, SimConfig config);

        /// <summary>
        /// Agents within <paramref name="radius"/> of agent <paramref name="agentIndex"/>, excluding
        /// itself, in ascending index order.
        /// </summary>
        List<int> Query(AgentWorld world, int agentIndex, float radius);
    }
}
