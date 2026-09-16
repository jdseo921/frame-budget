using System;
using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FrameBudget
{
    /// <summary>
    /// The scene's single behaviour. Owns the world, the presenter and the instrument, and runs the
    /// simulation on a fixed timestep accumulated from frame time. Order of work inside a frame:
    /// sample the frame that just finished, apply any pending respawn, step the simulation zero or
    /// more times, present.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class FrameBudgetDriver : MonoBehaviour
    {
        public const int MaxAgents = 200000;

        [SerializeField] private SimConfig config;
        [SerializeField] private Material agentMaterial;

        private static readonly ProfilerMarker SimulationStepMarker = new ProfilerMarker("FrameBudget.SimulationStep");
        private static readonly ProfilerMarker PresentMarker = new ProfilerMarker("FrameBudget.Present");
        private readonly Stopwatch stopwatch = new Stopwatch();

        private AgentWorld world;
        private NaiveAgentPresenter presenter;
        private FrameMetrics metrics;

        private double accumulator;
        private int pendingAgentCount = -1;

        public SimConfig Config => config;
        public AgentWorld World => world;
        public FrameMetrics Metrics => metrics;
        public int AgentCount => world != null ? world.Count : 0;

        public int StepsLastFrame { get; private set; }
        public double SimMsLastFrame { get; private set; }
        public double PresentMsLastFrame { get; private set; }
        public bool StepCapHitLastFrame { get; private set; }

        /// <summary>Frames since the last spawn in which the step cap stopped the simulation from catching up with real time.</summary>
        public int StepCapHitFrames { get; private set; }

        /// <summary>Simulated seconds dropped by the step cap since the last spawn. Non-zero means the simulation is running slower than real time.</summary>
        public double DroppedSimulationSeconds { get; private set; }

        private void Awake()
        {
            if (config == null)
            {
                Fatal("No SimConfig assigned to the FrameBudgetDriver.");
                return;
            }
            string problem = config.Validate();
            if (problem != null)
            {
                Fatal("SimConfig '" + config.name + "' is invalid: " + problem);
                return;
            }
            if (agentMaterial == null)
            {
                Fatal("No agent material assigned to the FrameBudgetDriver.");
                return;
            }

            // Frame time must mean "what this frame cost", so nothing is allowed to pace the frame.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Application.runInBackground = true;

            Debug.Log("[FrameBudget] Unity " + Application.unityVersion + " | " + (Application.isEditor ? "Editor" : "Player")
                      + " | batchmode=" + Application.isBatchMode + " | vSyncCount=" + QualitySettings.vSyncCount
                      + " targetFrameRate=" + Application.targetFrameRate + " | " + Screen.width + "x" + Screen.height
                      + " | GPU: " + SystemInfo.graphicsDeviceName + " | CPU: " + SystemInfo.processorType);
            if (agentMaterial.enableInstancing)
            {
                Debug.LogWarning("[FrameBudget] The agent material has GPU instancing enabled. The naive baseline expects it off; gpuInstancing is a later, measured technique.");
            }

            world = new AgentWorld();
            presenter = new NaiveAgentPresenter(agentMaterial);
            metrics = new FrameMetrics();

            LogConfig();
            Spawn(config.agentCount);
        }

        private void Update()
        {
            // 1. Sample the frame that just finished.
            metrics.BeginFrame(SimMsLastFrame, StepsLastFrame, PresentMsLastFrame, StepCapHitLastFrame);

            // 2. The operator may have asked for a respawn.
            HandleInput();
            if (pendingAgentCount >= 0)
            {
                Spawn(pendingAgentCount);
                pendingAgentCount = -1;
            }

            // 3. Fixed-timestep simulation. Frame time only decides HOW MANY steps run; it never
            //    sizes a step, so the work done per step is the same at 30 fps and at 300 fps.
            //    Unscaled so timeScale cannot slow the workload down. Steps per frame are capped:
            //    the simulated time the cap leaves behind is counted and shown, not hidden.
            float dt = config.fixedTimestep;
            accumulator += Time.unscaledDeltaTime;
            int steps = 0;
            double simMs = 0.0;
            while (accumulator >= dt && steps < config.maxStepsPerFrame)
            {
                stopwatch.Restart();
                using (SimulationStepMarker.Auto())
                {
                    NaiveSimulationStep.Step(world, config, dt);
                }
                stopwatch.Stop();

                double stepMs = stopwatch.Elapsed.TotalMilliseconds;
                metrics.RecordStep(stepMs);
                simMs += stepMs;
                accumulator -= dt;
                steps++;
            }
            bool capHit = false;
            if (accumulator >= dt)
            {
                capHit = true;
                StepCapHitFrames++;
                DroppedSimulationSeconds += accumulator - dt;
                accumulator = dt;   // keep exactly one step of debt so the next frame steps immediately
            }

            // 4. Present (naive GameObject path; timed separately from the simulation).
            stopwatch.Restart();
            using (PresentMarker.Auto())
            {
                presenter.Present(world);
            }
            stopwatch.Stop();

            StepsLastFrame = steps;
            SimMsLastFrame = simMs;
            PresentMsLastFrame = stopwatch.Elapsed.TotalMilliseconds;
            StepCapHitLastFrame = capHit;
        }

        private void OnDestroy()
        {
            presenter?.Dispose();
            metrics?.Dispose();
        }

        /// <summary>Respawns with a new agent count at the start of the next frame.</summary>
        public void RequestAgentCount(int count)
        {
            pendingAgentCount = Mathf.Clamp(count, 0, MaxAgents);
        }

        public void RequestRespawn()
        {
            pendingAgentCount = AgentCount;
        }

        private void Spawn(int count)
        {
            world.Respawn(config, count);
            presenter.Rebuild(count);
            accumulator = 0.0;
            StepsLastFrame = 0;
            SimMsLastFrame = 0.0;
            PresentMsLastFrame = 0.0;
            StepCapHitLastFrame = false;
            StepCapHitFrames = 0;
            DroppedSimulationSeconds = 0.0;
            metrics.ClearWindows();
            Debug.Log("[FrameBudget] Spawned " + count + " agents from seed " + config.seed + " (initial state hash " + world.StateHash().ToString("X16") + ").");
        }

        private void HandleInput()
        {
            if (Input.GetKeyDown(KeyCode.UpArrow)) RequestAgentCount(AgentCount + 100);
            if (Input.GetKeyDown(KeyCode.DownArrow)) RequestAgentCount(AgentCount - 100);
            if (Input.GetKeyDown(KeyCode.PageUp)) RequestAgentCount(AgentCount + 1000);
            if (Input.GetKeyDown(KeyCode.PageDown)) RequestAgentCount(AgentCount - 1000);
            if (Input.GetKeyDown(KeyCode.R)) RequestRespawn();
        }

        private void LogConfig()
        {
            Debug.Log("[FrameBudget] Config '" + config.name + "': techniques=" + config.TechniqueLabel + " agents=" + config.agentCount
                      + " seed=" + config.seed + " dt=" + config.fixedTimestep + "s maxSteps/frame=" + config.maxStepsPerFrame
                      + " world=+-" + config.worldHalfExtent + " neighbourRadius=" + config.neighbourRadius);
        }

        private void Fatal(string message)
        {
            Debug.LogError("[FrameBudget] " + message + " The driver is disabled.");
            enabled = false;
        }
    }
}
