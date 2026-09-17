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

        /// <summary>Transforms cached at build time, used by the zeroAlloc path instead of a per-agent GetComponent.</summary>
        private Transform[] transforms = Array.Empty<Transform>();

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
            transforms = new Transform[count];
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
                transforms[i] = go.transform;
            }
        }

        /// <summary>Copies simulation positions into the per-agent Transforms.</summary>
        public void Present(AgentWorld world, bool cachedLookup)
        {
            Vector3[] positions = world.Positions;
            int n = Math.Min(agents.Length, world.Count);

            // CONTROL: one GameObject per agent and one Transform write per agent per frame, either
            //          way. Replaced by the gpuInstancing technique, which draws straight from the
            //          position array and never touches a Transform.
            if (cachedLookup)
            {
                Transform[] cached = transforms;
                for (int i = 0; i < n; i++) cached[i].position = positions[i];
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    // CONTROL: GetComponent inside the per-agent loop instead of a cached reference.
                    //          Replaced by the zeroAlloc technique.
                    agents[i].GetComponent<Transform>().position = positions[i];
                }
            }
        }

        public void Dispose()
        {
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            agents = Array.Empty<GameObject>();
            transforms = Array.Empty<Transform>();
        }

        /// <summary>Hides or shows the agent objects, so the instanced path can render without the naive one drawing the same agents twice.</summary>
        public void SetVisible(bool visible)
        {
            if (root != null && root.activeSelf != visible) root.SetActive(visible);
        }
    }
}
