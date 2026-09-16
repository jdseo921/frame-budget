using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FrameBudget
{
    /// <summary>
    /// Shows the agents the naive way: one GameObject with a MeshFilter + MeshRenderer per agent, and
    /// the simulation's position copied into each Transform every frame. Presentation is kept apart
    /// from simulation so that <see cref="AgentWorld"/> never learns that GameObjects exist.
    /// Shadows and probes are off on every renderer so the draw-call count measures the agents, not
    /// a shadow pass. Physics is never involved: no colliders are created.
    /// </summary>
    public sealed class NaiveAgentPresenter : IDisposable
    {
        private readonly Material material;
        private readonly Mesh mesh;
        private GameObject root;
        private GameObject[] agents = Array.Empty<GameObject>();

        public int Count => agents.Length;

        public NaiveAgentPresenter(Material material)
        {
            this.material = material ?? throw new ArgumentNullException(nameof(material));
            mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (mesh == null) throw new InvalidOperationException("Built-in Cube.fbx mesh not found.");
        }

        /// <summary>Destroys the current agent objects and creates <paramref name="count"/> new ones.</summary>
        public void Rebuild(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

            if (root != null) UnityEngine.Object.Destroy(root);
            root = new GameObject("Agents (naive: one GameObject per agent)");
            Transform parent = root.transform;

            agents = new GameObject[count];
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("Agent " + i);
                go.transform.SetParent(parent, false);

                var filter = go.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;

                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

                agents[i] = go;
            }
        }

        /// <summary>Copies simulation positions into the per-agent Transforms.</summary>
        public void Present(AgentWorld world)
        {
            Vector3[] positions = world.Positions;
            int n = Math.Min(agents.Length, world.Count);
            for (int i = 0; i < n; i++)
            {
                // CONTROL: GetComponent inside the per-agent loop instead of a cached reference.
                //          Replaced by the zeroAlloc technique (cached, lookup-free hot path).
                // CONTROL: one GameObject per agent and one Transform write per agent per frame.
                //          Replaced by the gpuInstancing technique (instanced draw straight from the position array).
                agents[i].GetComponent<Transform>().position = positions[i];
            }
        }

        public void Dispose()
        {
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            agents = Array.Empty<GameObject>();
        }
    }
}
