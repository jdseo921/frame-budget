using System;
using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FrameBudget
{
    /// <summary>
    /// The scene's single behavior. Owns the world, the presenter, the instrument, the HUD and the
    /// benchmark, and runs the simulation on a fixed timestep accumulated from frame time. Order of
    /// work inside a frame: sample the frame that just finished, let the benchmark act on it, apply
    /// any pending respawn, step the simulation zero or more times, present, rebuild the HUD text.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class FrameBudgetDriver : MonoBehaviour
    {
        public const int MaxAgents = 200000;

        /// <summary>Runs the instrument self-test instead of the benchmark, then exits.</summary>
        public const string SelfTestArg = "-frameBudgetSelfTest";

        /// <summary>Resources path of the instancing-enabled agent material. See Awake for why it is an asset.</summary>
        public const string InstancedMaterialResource = "AgentInstanced";

        [SerializeField] private SimConfig config;

        [Tooltip("Configs selectable by name from the command line with -frameBudgetConfig <asset name>.")]
        [SerializeField] private SimConfig[] benchmarkConfigs = Array.Empty<SimConfig>();

        [SerializeField] private Material agentMaterial;

        private static readonly ProfilerMarker SimulationStepMarker = new ProfilerMarker("FrameBudget.SimulationStep");
        private static readonly ProfilerMarker PresentMarker = new ProfilerMarker("FrameBudget.Present");
        private readonly Stopwatch stopwatch = new Stopwatch();

        private AgentWorld world;
        private BruteForceIndex bruteForceIndex;
        private UniformGridIndex uniformGridIndex;
        private TechniqueCombination activeTechniques;
        private NaiveAgentPresenter presenter;
        private InstancedAgentPresenter instancedPresenter;
        private Material instancedMaterial;
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

        /// <summary>Simulated seconds dropped by the step cap since the last spawn. Non-zero means the simulation is running slower than real time. Always zero under benchmark stepping, which does not pace itself.</summary>
        public double DroppedSimulationSeconds { get; private set; }

        public const string BenchmarkSteppingMode = "fixed-one-step-per-frame";
        public const string InteractiveSteppingMode = "realtime-accumulator-capped";

        /// <summary>Which stepping rule is in force; recorded in every CSV row so rows taken under different rules are never compared.</summary>
        /// <summary>
        /// True while the fixed one-step-per-frame rule is in force: during a benchmark run, and
        /// during a presentation capture, which needs the same rule for the panel to show the figures
        /// the README quotes. False in ordinary interactive play.
        /// </summary>
        public bool BenchmarkStepping => benchmark != null && (benchmark.IsRunning || capturing);

        public string SteppingMode => BenchmarkStepping ? BenchmarkSteppingMode : InteractiveSteppingMode;

        /// <summary>Set once by <see cref="PresentationCaptureBehaviour"/>; false in every normal run.</summary>
        private bool capturing;

        /// <summary>Switches to benchmark stepping for a screenshot, without starting a benchmark or writing a CSV.</summary>
        public void BeginPresentationCapture()
        {
            capturing = true;
            accumulator = 0.0;
        }

        /// <summary>Technique flags currently in force. The benchmark sets these per point; interactive play uses the config's own flags.</summary>
        public TechniqueCombination ActiveTechniques => activeTechniques;

        /// <summary>The neighbor query the next step will use, chosen by the active technique flags.</summary>
        public ISpatialIndex ActiveIndex => activeTechniques.spatialHash ? (ISpatialIndex)uniformGridIndex : bruteForceIndex;

        /// <summary>Name of the active index, recorded in the CSV so a row says which implementation produced it.</summary>
        public string ActiveIndexName => ActiveIndex != null ? ActiveIndex.Name : "";

        /// <summary>Switches the technique flags for the next point. Takes effect on the next respawn.</summary>
        public void SetTechniques(TechniqueCombination techniques)
        {
            activeTechniques = techniques;
        }

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
            // Interactive play only warns; a benchmark run aborts instead (see BenchmarkRunner.Start).
            string clamp = RunGuard.ApplyAndVerify();
            if (clamp != null)
            {
                Debug.LogWarning("[FrameBudget] A frame-rate clamp is in effect, so displayed frame times describe the display, not the code: " + clamp);
            }

            Debug.Log("[FrameBudget] Unity " + Application.unityVersion + " | " + (Application.isEditor ? "Editor" : "Player")
                      + " | batchmode=" + Application.isBatchMode + " | " + RunGuard.Describe()
                      + " | " + Screen.width + "x" + Screen.height
                      + " | GPU: " + SystemInfo.graphicsDeviceName + " | CPU: " + SystemInfo.processorType);
            if (agentMaterial.enableInstancing)
            {
                Debug.LogWarning("[FrameBudget] The agent material has GPU instancing enabled. The naive baseline expects it off; gpuInstancing is a later, measured technique.");
            }

            world = new AgentWorld();
            bruteForceIndex = new BruteForceIndex();
            uniformGridIndex = new UniformGridIndex();
            activeTechniques = config.CurrentTechniques;
            presenter = new NaiveAgentPresenter(agentMaterial);

            // The instanced material is an ASSET under Resources/, not a runtime copy of the naive
            // one. A player build only compiles the shader variants its built-in materials ask for,
            // and a material constructed at run time asks too late: the INSTANCING_ON variant is
            // already stripped, so RenderMeshInstanced submits draws that rasterize nothing. The
            // failure is silent - the draw-call counter still moves - and invisible in the editor,
            // which keeps every variant. Resources/ is always included in a build, so the variant
            // is compiled in. The asset is otherwise identical to AgentNaive: same shader, same
            // color, differing only in the instancing flag.
            instancedMaterial = Resources.Load<Material>(InstancedMaterialResource);
            if (instancedMaterial == null)
            {
                Fatal("Assets/FrameBudget/Resources/" + InstancedMaterialResource + ".mat is missing. gpuInstancing "
                      + "needs a built-in material with instancing enabled; without one the agents are submitted "
                      + "but never rasterized, and any measurement of the technique would be void.");
                return;
            }
            instancedPresenter = new InstancedAgentPresenter(instancedMaterial);
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

            if (CommandLine.HasFlag(SelfTestArg))
            {
                BenchmarkLaunch.MarkUnattended();
                RunInstrumentSelfTest();
                return;
            }

            // Presentation capture. One test at start-up and nothing else: without the flag neither
            // behavior is constructed, so a normal run carries no part of this path.
            if (PresentationCapture.ClipRequested)
            {
                BenchmarkLaunch.MarkUnattended();
                gameObject.AddComponent<ClipCaptureBehaviour>().Bind(this);
                return;
            }

            if (PresentationCapture.Requested)
            {
                BenchmarkLaunch.MarkUnattended();
                gameObject.AddComponent<PresentationCaptureBehaviour>().Bind(this);
                return;
            }

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

            // 3. Simulation. The step itself is always the same fixed dt; only the decision of how
            //    many steps a frame runs differs between the two modes.
            float dt = config.fixedTimestep;
            int steps = 0;
            double simMs = 0.0;
            bool capHit = false;

            if (BenchmarkStepping)
            {
                // BENCHMARK STEPPING: exactly one step per frame, unconditionally, with no reference
                // to wall-clock time. A benchmark wants a fixed amount of work per frame; keeping up
                // with real time is a game concern, and having it in the measured path is what made
                // day 2's results hard to read. Below about 1,500 agents most frames ran no step at
                // all, so frame_ms_median described a frame that did no simulation work and moved
                // with the step ratio rather than the cost; runs of the same configuration executed
                // different numbers of steps, which left state_hash uncomparable exactly where the
                // simulation was fastest; and the heavy configurations discarded simulated time by
                // the minute. One step per frame removes all three at once: every run of
                // measured_frames frames executes exactly measured_frames steps, so frame_ms and
                // step_ms describe the same work and state_hash is comparable everywhere.
                simMs = StepOnce(dt);
                steps = 1;
                accumulator = 0.0;   // pacing debt is meaningless here and must not leak into interactive mode
            }
            else
            {
                // INTERACTIVE: real-time pacing, capped, with the simulated time the cap drops
                // counted rather than hidden. Unscaled so timeScale cannot resize the workload.
                accumulator += Time.unscaledDeltaTime;
                while (accumulator >= dt && steps < config.maxStepsPerFrame)
                {
                    simMs += StepOnce(dt);
                    accumulator -= dt;
                    steps++;
                }
                if (accumulator >= dt)
                {
                    capHit = true;
                    StepCapHitFrames++;
                    DroppedSimulationSeconds += accumulator - dt;
                    accumulator = dt;   // keep exactly one step of debt so the next frame steps immediately
                }
            }

            // 4. Present (naive GameObject path; timed separately from the simulation).
            stopwatch.Restart();
            using (PresentMarker.Auto())
            {
                if (activeTechniques.gpuInstancing)
                {
                    // The naive presenter's GameObjects must stop rendering, or the same agents
                    // would be drawn twice and the draw-call count would measure both paths.
                    presenter.SetVisible(false);
                    instancedPresenter.Present(world);
                }
                else
                {
                    presenter.SetVisible(true);
                    presenter.Present(world, activeTechniques.zeroAlloc);
                }
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
            instancedPresenter?.Dispose();
            if (instancedMaterial != null) Destroy(instancedMaterial);
            metrics?.Dispose();
            hud?.Dispose();
        }

        /// <summary>
        /// Checks the instrument itself rather than the simulation, in whichever build it is run in.
        /// The allocation counter is a runtime API whose behavior under IL2CPP stripping cannot be
        /// assumed, so this allocates several known block sizes and requires the counter to report
        /// each of them. Run with -frameBudgetSelfTest; the process exits with 0 on pass, 1 on fail.
        /// </summary>
        private void RunInstrumentSelfTest()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[FrameBudget] INSTRUMENT SELF-TEST\n");
            sb.Append("  environment:        ").Append(RunEnvironment.Describe()).Append('\n');
            sb.Append("  pacing:             ").Append(RunGuard.Describe()).Append('\n');
            sb.Append("  allocation source:  ").Append(metrics.AllocationSource).Append("  verified=").Append(metrics.GcAllocatedValid).Append('\n');
            sb.Append("  GPU frame time:     ").Append(metrics.GpuFrameTimeValid ? "counter resolved" : "NOT AVAILABLE").Append('\n');
            sb.Append("  draw / SetPass:     ").Append(metrics.DrawCallsValid && metrics.SetPassCallsValid ? "counters resolved" : "NOT AVAILABLE").Append('\n');

            // When no counter verified, survey every candidate this runtime might offer, so the
            // failure report says what IS available rather than only what is not.
            if (!metrics.GcAllocatedValid)
            {
                sb.Append("  --- allocation counter survey (allocating 1 MiB between two samples) ---\n");
                foreach (var candidate in AllocationProbe.SurveyCandidates())
                {
                    sb.Append("  ").Append(candidate).Append('\n');
                }
            }

            bool pass = metrics.GcAllocatedValid;
            if (metrics.GcAllocatedValid)
            {
                // Several sizes, because a counter that reports a fixed number, or reports only
                // large allocations, would pass a single-size check and still be useless.
                foreach (int kilobytes in new[] { 16, 256, 4096 })
                {
                    int bytes = kilobytes * 1024;
                    long before = AllocationProbe.Sample();
                    var block = new byte[bytes];
                    block[0] = 1;
                    block[bytes - 1] = 2;
                    long after = AllocationProbe.Sample();
                    long delta = after - before;
                    bool ok = delta >= bytes && delta <= bytes + 4096;
                    if (!ok) pass = false;
                    sb.Append("  allocated ").Append(bytes).Append(" B -> counter moved ").Append(delta)
                      .Append(" B  ").Append(ok ? "OK" : "MISMATCH")
                      .Append("  (block length ").Append(block.Length).Append(")\n");
                }

                long idleBefore = AllocationProbe.Sample();
                long idleAfter = AllocationProbe.Sample();
                sb.Append("  two samples with nothing between -> ").Append(idleAfter - idleBefore)
                  .Append(" B (a non-zero value here is the counter's own overhead)\n");
            }

            sb.Append(pass ? "  RESULT: PASS" : "  RESULT: FAIL");
            if (pass) Debug.Log(sb.ToString());
            else Debug.LogError(sb.ToString());
            BenchmarkLaunch.ExitIfUnattended(pass ? 0 : 1);
        }

        /// <summary>Runs one simulation step and returns its cost in milliseconds, recording it with both the HUD window and the benchmark.</summary>
        private double StepOnce(float dt)
        {
            stopwatch.Restart();
            using (SimulationStepMarker.Auto())
            {
                SimulationStep.Step(world, config, dt, ActiveIndex, in activeTechniques);
            }
            stopwatch.Stop();

            double stepMs = stopwatch.Elapsed.TotalMilliseconds;
            metrics.RecordStep(stepMs);
            benchmark.RecordStep(stepMs);
            return stepMs;
        }

        /// <summary>Flips one technique flag and respawns, so the before and after simulate the same agents.</summary>
        private void ToggleTechnique(ref bool flag, string name)
        {
            flag = !flag;
            Debug.Log("[FrameBudget] " + name + " " + (flag ? "ON" : "OFF") + " -> " + activeTechniques.Label);
            RequestRespawn();
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
            activeTechniques = cfg.CurrentTechniques;
            LogConfig();
            Spawn(cfg.agentCount);
        }

        private void Spawn(int count)
        {
            world.Respawn(config, count);
            presenter.Rebuild(count);
            instancedPresenter.Rebuild(count);
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

            // Technique toggles, so the difference can be seen rather than only read. Each respawns
            // from the same seed, which keeps the comparison honest on screen for the same reason
            // the benchmark does it: the two configurations then simulate the same agents.
            // Disabled while a sweep is running, because there the sweep owns the flags.
            if (Input.GetKeyDown(KeyCode.Alpha1)) ToggleTechnique(ref activeTechniques.spatialHash, "spatialHash");
            if (Input.GetKeyDown(KeyCode.Alpha2)) ToggleTechnique(ref activeTechniques.zeroAlloc, "zeroAlloc");
            if (Input.GetKeyDown(KeyCode.Alpha3)) ToggleTechnique(ref activeTechniques.gpuInstancing, "gpuInstancing");
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
                      + " world=+-" + config.worldHalfExtent + " neighborRadius=" + config.neighborRadius);
        }

        private void Fatal(string message)
        {
            Debug.LogError("[FrameBudget] " + message + " The driver is disabled.");
            enabled = false;
            BenchmarkLaunch.ExitIfUnattended(2);
        }
    }
}
