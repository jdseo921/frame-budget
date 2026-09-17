using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace FrameBudget.Tests
{
    /// <summary>
    /// The test that decides whether the spatial hash is an optimisation or a bug.
    ///
    /// A grid that misses neighbours is fast and wrong, and it fails in the most flattering possible
    /// way: the fewer neighbours it returns, the better it scores. Timing alone cannot catch that.
    /// So every case here asserts that UniformGridIndex returns the identical neighbour list to
    /// BruteForceIndex - identical contents and identical order, since the caller sums a float force
    /// over them and a reordering is a different simulation.
    ///
    /// The cases are chosen for the geometry that breaks naive grid implementations, not for
    /// coverage: a radius smaller than a cell, a radius spanning several cells, agents sitting
    /// exactly on cell boundaries, and agents pinned to the world edge where the ring of searched
    /// cells is clipped.
    /// </summary>
    public class SpatialIndexEquivalenceTests
    {
        private static SimConfig MakeConfig(float worldHalfExtent, float neighbourRadius, float cellSize)
        {
            SimConfig config = ScriptableObject.CreateInstance<SimConfig>();
            config.worldHalfExtent = worldHalfExtent;
            config.neighbourRadius = neighbourRadius;
            config.spatialHashCellSize = cellSize;
            config.seed = 4242;
            config.tickBucketCount = 4;
            config.maxSpeed = 6f;
            return config;
        }

        /// <summary>Compares the two indexes over every agent and fails with the first disagreement, described.</summary>
        private static void AssertIndexesAgree(AgentWorld world, SimConfig config, string because)
        {
            var brute = new BruteForceIndex();
            var grid = new UniformGridIndex();
            brute.Rebuild(world, config);
            grid.Rebuild(world, config);

            for (int i = 0; i < world.Count; i++)
            {
                List<int> expected = brute.Query(world, i, config.neighbourRadius);
                List<int> actual = grid.Query(world, i, config.neighbourRadius);

                Assert.That(actual, Is.EqualTo(expected),
                    because + ": agent " + i + " at " + world.Positions[i]
                    + " (cells " + grid.Dimension + "x" + grid.Dimension + " of size " + grid.CellSize
                    + ", radius " + config.neighbourRadius + ")");
            }
        }

        [Test]
        public void GridMatchesBruteForce_AcrossAgentCountsAndRadii(
            [Values(1, 2, 64, 500)] int agentCount,
            [Values(0.5f, 4f, 25f)] float neighbourRadius)
        {
            SimConfig config = MakeConfig(50f, neighbourRadius, 0f);   // cell size 0 = use the radius
            try
            {
                var world = new AgentWorld();
                world.Respawn(config, agentCount);
                AssertIndexesAgree(world, config, "count " + agentCount + ", radius " + neighbourRadius);
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void GridMatchesBruteForce_WhenRadiusIsSmallerThanACell_AndWhenItSpansSeveralCells(
            [Values(0.25f, 1f, 3f, 12f)] float cellSize)
        {
            // Radius fixed at 4: smaller than a 12-unit cell, larger than a 0.25-unit cell by 16x.
            // The grid must widen its searched ring rather than assuming a single ring of neighbours.
            SimConfig config = MakeConfig(40f, 4f, cellSize);
            try
            {
                var world = new AgentWorld();
                world.Respawn(config, 300);
                AssertIndexesAgree(world, config, "cell size " + cellSize + " against radius 4");
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void GridMatchesBruteForce_ForAgentsExactlyOnCellBoundariesAndWorldEdges()
        {
            // Hand-placed rather than seeded: exact boundary coordinates are the cases floating-point
            // cell arithmetic gets wrong, and a random spawn will essentially never produce them.
            const float half = 10f;
            const float cell = 2.5f;
            SimConfig config = MakeConfig(half, 2.5f, cell);
            try
            {
                var positions = new List<Vector3>();
                for (float x = -half; x <= half; x += cell)
                {
                    for (float z = -half; z <= half; z += cell)
                    {
                        positions.Add(new Vector3(x, 0f, z));                       // exactly on a boundary
                        positions.Add(new Vector3(x + 1e-4f, 0f, z - 1e-4f));       // just inside it
                    }
                }
                positions.Add(new Vector3(half, 0f, half));      // the far corner
                positions.Add(new Vector3(-half, 0f, -half));    // the near corner
                positions.Add(new Vector3(half, 0f, -half));
                positions.Add(new Vector3(-half, 0f, half));

                var world = new AgentWorld();
                world.Respawn(config, positions.Count);
                for (int i = 0; i < positions.Count; i++) world.Positions[i] = positions[i];

                AssertIndexesAgree(world, config, "agents on cell boundaries and world edges");
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        /// <summary>
        /// The zeroAlloc path writes neighbours into one buffer reused across every agent and every
        /// step, so a query that returns fewer neighbours than the previous one leaves stale indices
        /// behind it. Reading past the returned count, or failing to respect it, is the classic way
        /// to break a buffer-reuse optimisation - and it corrupts the simulation rather than
        /// crashing, which is worse. This deliberately reuses a single buffer across all agents, in
        /// descending order of expected neighbour count, so that any leakage past the count shows up.
        /// </summary>
        [Test]
        public void QueryIntoMatchesQuery_WithOneBufferReusedAcrossEveryAgent(
            [Values(64, 400)] int agentCount,
            [Values(2f, 8f)] float neighbourRadius)
        {
            SimConfig config = MakeConfig(40f, neighbourRadius, 0f);
            try
            {
                var world = new AgentWorld();
                world.Respawn(config, agentCount);

                foreach (ISpatialIndex index in new ISpatialIndex[] { new BruteForceIndex(), new UniformGridIndex() })
                {
                    index.Rebuild(world, config);

                    // Visit the agents with the most neighbours first, so later shorter queries are
                    // the ones that would expose stale tail entries.
                    var order = new List<int>();
                    for (int i = 0; i < agentCount; i++) order.Add(i);
                    order.Sort((a, b) => index.Query(world, b, neighbourRadius).Count
                                        .CompareTo(index.Query(world, a, neighbourRadius).Count));

                    var buffer = new int[agentCount];
                    foreach (int i in order)
                    {
                        List<int> expected = index.Query(world, i, neighbourRadius);
                        int count = index.QueryInto(world, i, neighbourRadius, buffer);

                        Assert.That(count, Is.EqualTo(expected.Count),
                            index.Name + ": agent " + i + " returned a different neighbour count");
                        for (int k = 0; k < count; k++)
                        {
                            Assert.That(buffer[k], Is.EqualTo(expected[k]),
                                index.Name + ": agent " + i + " differs at position " + k
                                + " (stale buffer contents leaking past the count?)");
                        }
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void GridQueryIsInAscendingIndexOrder()
        {
            // Order is not a stylistic preference: the caller accumulates a float sum over this list,
            // float addition is not associative, so a reordered list is a different simulation.
            SimConfig config = MakeConfig(30f, 6f, 0f);
            try
            {
                var world = new AgentWorld();
                world.Respawn(config, 400);
                var grid = new UniformGridIndex();
                grid.Rebuild(world, config);

                bool sawANonTrivialList = false;
                for (int i = 0; i < world.Count; i++)
                {
                    List<int> neighbours = grid.Query(world, i, config.neighbourRadius);
                    if (neighbours.Count > 1) sawANonTrivialList = true;
                    for (int k = 1; k < neighbours.Count; k++)
                    {
                        Assert.That(neighbours[k], Is.GreaterThan(neighbours[k - 1]),
                            "agent " + i + " neighbour list is not ascending at position " + k);
                    }
                }

                Assert.That(sawANonTrivialList, Is.True,
                    "no agent had more than one neighbour, so this test proved nothing about ordering");
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }
    }
}
