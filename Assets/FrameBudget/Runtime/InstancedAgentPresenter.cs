using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FrameBudget
{
    /// <summary>
    /// Draws every agent with instanced draw calls issued straight from the simulation's position
    /// array, with no GameObject, no Transform and no MeshRenderer anywhere.
    ///
    /// This is the gpuInstancing technique. The control it replaces is <see cref="NaiveAgentPresenter"/>,
    /// which keeps one GameObject per agent and writes a position into each Transform every frame;
    /// that presenter stays in the project intact, because a comparison needs both sides.
    ///
    /// Two details are worth stating because they bound what the measurement can claim:
    ///
    /// <b>Batch size.</b> Unity's instanced path takes at most 1023 matrices per call, so an agent
    /// count above that becomes several calls - ten at 10,000 agents. "One draw call" would be the
    /// wrong claim; "ten instead of ten thousand" is the right one.
    ///
    /// <b>The matrix array is rebuilt every frame.</b> Agents move, so their transforms change, and
    /// the array is filled from <see cref="AgentWorld.Positions"/> before the draw. That fill is the
    /// technique's real per-frame cost and it is inside the timed present region, not outside it.
    /// The array itself is allocated once per respawn, so the fill does not allocate.
    /// </summary>
    public sealed class InstancedAgentPresenter : IDisposable
    {
        /// <summary>Unity's per-call instancing limit.</summary>
        public const int MaxInstancesPerCall = 1023;

        private readonly Material material;
        private readonly Mesh mesh;
        private Matrix4x4[] matrices = Array.Empty<Matrix4x4>();
        private RenderParams renderParams;
        private bool renderParamsReady;

        /// <summary>Instanced draw calls the last <see cref="Present"/> issued.</summary>
        public int LastDrawCallCount { get; private set; }

        public InstancedAgentPresenter(Material material)
        {
            this.material = material ?? throw new ArgumentNullException(nameof(material));
            mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (mesh == null) throw new InvalidOperationException("Built-in Cube.fbx mesh not found.");
        }

        /// <summary>Sizes the matrix array for the agent count. Called on respawn, never per frame.</summary>
        public void Rebuild(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (matrices.Length != count) matrices = new Matrix4x4[count];
        }

        public void Present(AgentWorld world)
        {
            int n = Math.Min(matrices.Length, world.Count);
            if (n == 0)
            {
                LastDrawCallCount = 0;
                return;
            }

            if (!renderParamsReady)
            {
                // Matched to the naive presenter's renderers, so the two are drawing the same thing:
                // no shadows, no probes, no motion vectors. Otherwise the comparison would be
                // measuring a different amount of rendering rather than a different way of
                // submitting the same amount.
                renderParams = new RenderParams(material)
                {
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = false,
                    lightProbeUsage = LightProbeUsage.Off,
                    reflectionProbeUsage = ReflectionProbeUsage.Off,
                    worldBounds = new Bounds(Vector3.zero, new Vector3(world.HalfExtent * 4f, 16f, world.HalfExtent * 4f)),
                };
                renderParamsReady = true;
            }

            Vector3[] positions = world.Positions;
            for (int i = 0; i < n; i++)
            {
                // Translation only: the agents are unrotated unit cubes, so building the matrix by
                // hand avoids Matrix4x4.TRS and the quaternion work behind it.
                Matrix4x4 m = Matrix4x4.identity;
                Vector3 p = positions[i];
                m.m03 = p.x;
                m.m13 = p.y;
                m.m23 = p.z;
                matrices[i] = m;
            }

            int calls = 0;
            for (int start = 0; start < n; start += MaxInstancesPerCall)
            {
                int batch = Math.Min(MaxInstancesPerCall, n - start);
                Graphics.RenderMeshInstanced(renderParams, mesh, 0, matrices, batch, start);
                calls++;
            }
            LastDrawCallCount = calls;
        }

        public void Dispose()
        {
            matrices = Array.Empty<Matrix4x4>();
            renderParamsReady = false;
        }
    }
}
