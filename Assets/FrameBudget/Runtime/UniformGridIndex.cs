using System;
using System.Collections.Generic;
using UnityEngine;

namespace FrameBudget
{
    /// <summary>
    /// A uniform grid over the world square. Agents are bucketed into cells once per step, and a
    /// query visits only the cells that can possibly contain a neighbour, instead of all n agents.
    /// Cost per step goes from O(n^2) to roughly O(n * k), where k is the number of agents in the
    /// searched neighbourhood - which, at fixed density and fixed radius, does not grow with n.
    ///
    /// The bucketing is a counting sort rather than a list-per-cell: two integer arrays, a prefix
    /// table of where each cell's agents begin and a flat array of agent indices. That keeps the
    /// per-step build allocation-free after the first sizing and, because the placing pass walks
    /// agents in ascending index order, leaves every cell's contents in ascending order too.
    ///
    /// Two details are load-bearing rather than incidental:
    ///
    /// <b>Ring count.</b> A query searches every cell within ceil(radius / cellSize) cells of the
    /// agent's own, not a fixed 3x3. With the default cell size equal to the neighbour radius the
    /// two are the same thing, but hard-coding 3x3 would silently return an incomplete neighbour set
    /// the moment someone made cells smaller than the radius - fast, wrong, and invisible without a
    /// test.
    ///
    /// <b>The sort.</b> Cells are visited in grid order, which has nothing to do with agent index
    /// order, so the gathered candidates come out shuffled relative to the brute-force scan. Since
    /// the caller sums a force over them and float addition is not associative, the results are
    /// sorted ascending before being returned. Without that the simulation would diverge from the
    /// baseline within a few hundred steps and no bit-identical comparison would be possible.
    /// </summary>
    public sealed class UniformGridIndex : ISpatialIndex
    {
        public string Name => "uniform-grid";

        private int[] cellStart = Array.Empty<int>();    // prefix table, length cellCount + 1
        private int[] cellCursor = Array.Empty<int>();   // scratch write cursor per cell
        private int[] cellItems = Array.Empty<int>();    // agent indices grouped by cell, ascending within a cell

        private int dimension;      // cells per axis
        private float cellSize;
        private float halfExtent;
        private int agentCount;

        /// <summary>Cells per axis in the most recent rebuild; exposed for tests and diagnostics.</summary>
        public int Dimension => dimension;

        /// <summary>Cell size in world units actually used, after falling back to the neighbour radius.</summary>
        public float CellSize => cellSize;

        public void Rebuild(AgentWorld world, SimConfig config)
        {
            agentCount = world.Count;
            halfExtent = world.HalfExtent;

            // A cell size of zero or less means "use the neighbour radius", which makes the searched
            // neighbourhood exactly one ring of cells.
            cellSize = config.spatialHashCellSize > 0f ? config.spatialHashCellSize : config.neighbourRadius;
            if (cellSize <= 0f) cellSize = 1f;

            dimension = Mathf.Max(1, Mathf.CeilToInt(2f * halfExtent / cellSize));
            int cellCount = dimension * dimension;

            if (cellStart.Length != cellCount + 1)
            {
                cellStart = new int[cellCount + 1];
                cellCursor = new int[cellCount];
            }
            else
            {
                Array.Clear(cellStart, 0, cellStart.Length);
            }
            if (cellItems.Length != agentCount) cellItems = new int[agentCount];

            Vector3[] positions = world.Positions;

            // Pass 1: count agents per cell, into cellStart shifted by one so that the prefix sum
            // below lands each cell's begin index in place.
            for (int i = 0; i < agentCount; i++)
            {
                cellStart[CellOf(positions[i]) + 1]++;
            }

            // Pass 2: prefix sum -> begin index of each cell.
            for (int c = 0; c < cellCount; c++)
            {
                cellStart[c + 1] += cellStart[c];
            }
            Array.Copy(cellStart, cellCursor, cellCount);

            // Pass 3: place agents. Ascending i means ascending indices within every cell.
            for (int i = 0; i < agentCount; i++)
            {
                cellItems[cellCursor[CellOf(positions[i])]++] = i;
            }
        }

        public List<int> Query(AgentWorld world, int agentIndex, float radius)
        {
            // Same per-query allocation shape as the brute-force control: one fresh List per query.
            // Pooling it would fold the zeroAlloc technique's win into this one, and neither delta
            // could then be attributed. See docs/METHOD.md and the day-3 notes.
            var result = new List<int>();

            Vector3[] positions = world.Positions;
            Vector3 pos = positions[agentIndex];
            float radiusSq = radius * radius;

            int cx = AxisCell(pos.x);
            int cz = AxisCell(pos.z);
            int ring = Mathf.Max(1, Mathf.CeilToInt(radius / cellSize));

            int xMin = Mathf.Max(0, cx - ring);
            int xMax = Mathf.Min(dimension - 1, cx + ring);
            int zMin = Mathf.Max(0, cz - ring);
            int zMax = Mathf.Min(dimension - 1, cz + ring);

            for (int z = zMin; z <= zMax; z++)
            {
                int rowBase = z * dimension;
                for (int x = xMin; x <= xMax; x++)
                {
                    int cell = rowBase + x;
                    int end = cellStart[cell + 1];
                    for (int k = cellStart[cell]; k < end; k++)
                    {
                        int j = cellItems[k];
                        if (j == agentIndex) continue;
                        if ((positions[j] - pos).sqrMagnitude < radiusSq) result.Add(j);
                    }
                }
            }

            // Ascending order, to match the brute-force scan exactly. See the class comment.
            result.Sort();
            return result;
        }

        private int CellOf(Vector3 position)
        {
            return AxisCell(position.z) * dimension + AxisCell(position.x);
        }

        /// <summary>Cell index along one axis, clamped so that a position exactly on the world edge lands in the last cell rather than out of range.</summary>
        private int AxisCell(float coordinate)
        {
            int cell = Mathf.FloorToInt((coordinate + halfExtent) / cellSize);
            if (cell < 0) return 0;
            if (cell >= dimension) return dimension - 1;
            return cell;
        }
    }
}
