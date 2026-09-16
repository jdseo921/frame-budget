using System;
using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FrameBudget
{
    /// <summary>
    /// The scene's single behaviour. Owns the world, the presenter, the instrument, the HUD and the
    /// benchmark, and runs the simulation on a fixed timestep accumulated from frame time. Order of
    /// work inside a frame: sample the frame that just finished, let the benchmark act on it, apply
    /// any pending respawn, step the simulation zero or more times, present, rebuild the HUD text.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class FrameBudgetDriver : MonoBehaviour
    {
        public const int MaxAgents = 200000;

        [SerializeField] private SimConfig config;

        [Tooltip("Configs selectable by name from the command line with -frameBudgetConfig <asset name>.")]
        [SerializeField] private SimConfig[] benchmarkConfigs = Array.Empty<SimConfig>();

        [SerializeField] private Material agentMaterial;

        private static readonly ProfilerMarker SimulationStepMarker = new ProfilerMarker("FrameBudget.SimulationStep");
        private static readonly ProfilerMarker PresentMarker = new ProfilerMarker("FrameBudget.Present");
        private readonly Stopwatch stopwatch = new Stopwatch();

        private AgentWorld world;
        private NaiveAgentPresenter presenter;
        private FrameMetrics metrics;
        private FrameBudgetHud hud;
        private BenchmarkRunner benchmark;
        private Camera worldCamera;
        private readonly WorldViewport worldViewport = new WorldViewport();

        private double accumulator;
        private int pendingAgentCount = -1;

        public SimConfig Config => config;
        public AgentWorld World => world;
        public FrameMetrics Metrics => metrics;
        public BenchmarkRunner Benchmark => benchmark;
        public int AgentCount => world != null ? world.Count : 0;
        public bool HudVisible { get; set; } = true;

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
            hud = new FrameBudgetHud();
            benchmark = new BenchmarkRunner();

            worldCamera = Camera.main;
            if (worldCamera == null)
            {
                Debug.LogWarning("[FrameBudget] No camera tagged MainCamera in the scene; the world view cannot be kept beside the HUD.");
            }

            LogConfig();
            Spawn(config.agentCount);
        }

        private void Start()
        {
            if (!enabled) return;
            if (BenchmarkLaunch.TryGetRequest(out string configName))
            {
                SimConfig cfg = ResolveConfig(configName);
                if (cfg == null) return;
                Debug.Log("[FrameBudget] Benchmark requested from the command line with config '" + cfg.name + "'.");
                StartBenchmark(cfg);
            }
        }

        private void Update()
        {
            // 1. Sample the frame that just finished.
            FrameSample sample = metrics.BeginFrame(SimMsLastFrame, StepsLastFrame, PresentMsLastFrame, StepCapHitLastFrame);

            // 2. The benchmark may close a point and ask for a respawn; so may the operator.
            benchmark.Tick(in sample);
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
                benchmark.RecordStep(stepMs);
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

            // 5. HUD text, rebuilt every frame (control condition; see FrameBudgetHud.BuildText).
            hud.BuildText(this);

            // 6. Keep the world beside the HUD column, never under it (the column width comes from the last OnGUI).
            worldViewport.Fit(worldCamera, HudVisible && !Application.isBatchMode ? hud.ColumnWidth : 0f, config.worldHalfExtent);
        }

        private void OnGUI()
        {
            if (HudVisible && !Application.isBatchMode) hud.Draw(this);
            else hud.NotifyHidden();
        }

        private void OnDestroy()
        {
            presenter?.Dispose();
            metrics?.Dispose();
            hud?.Dispose();
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

        public void StartBenchmark(SimConfig cfg = null)
        {
            benchmark.Start(cfg != null ? cfg : config, this);
        }

        /// <summary>Switches the active config (timestep, steering, seed, techniques) and respawns. Used by the benchmark.</summary>
        public void UseConfig(SimConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            string problem = cfg.Validate();
            if (problem != null) throw new InvalidOperationException("SimConfig '" + cfg.name + "' is invalid: " + problem);
            config = cfg;
            LogConfig();
            Spawn(cfg.agentCount);
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
            hud.NotifyAgentCount(count);
            Debug.Log("[FrameBudget] Spawned " + count + " agents from seed " + config.seed + " (initial state hash " + world.StateHash().ToString("X16") + ").");
        }

        private void HandleInput()
        {
            if (Input.GetKeyDown(KeyCode.H)) HudVisible = !HudVisible;
            if (benchmark.IsRunning) return;
            if (Input.GetKeyDown(KeyCode.UpArrow)) RequestAgentCount(AgentCount + 100);
            if (Input.GetKeyDown(KeyCode.DownArrow)) RequestAgentCount(AgentCount - 100);
            if (Input.GetKeyDown(KeyCode.PageUp)) RequestAgentCount(AgentCount + 1000);
            if (Input.GetKeyDown(KeyCode.PageDown)) RequestAgentCount(AgentCount - 1000);
            if (Input.GetKeyDown(KeyCode.R)) RequestRespawn();
            if (Input.GetKeyDown(KeyCode.B)) StartBenchmark();
        }

        private SimConfig ResolveConfig(string name)
        {
            if (string.IsNullOrEmpty(name)) return config;
            if (config != null && string.Equals(config.name, name, StringComparison.OrdinalIgnoreCase)) return config;
            foreach (SimConfig candidate in benchmarkConfigs)
            {
                if (candidate != null && string.Equals(candidate.name, name, StringComparison.OrdinalIgnoreCase)) return candidate;
            }

            var available = new System.Text.StringBuilder();
            if (config != null) available.Append(config.name);
            foreach (SimConfig candidate in benchmarkConfigs)
            {
                if (candidate != null) available.Append(", ").Append(candidate.name);
            }
            Fatal("Unknown benchmark config '" + name + "'. Configs listed on the FrameBudgetDriver: " + available + ". Refusing to run a different config than the one asked for.");
            return null;
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
            BenchmarkLaunch.ExitIfUnattended(2);
        }
    }
}
