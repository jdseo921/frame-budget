using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace FrameBudget
{
    /// <summary>
    /// Unattended sweep: for every agent count in the config, for every run, respawn, discard the
    /// warm-up frames, record the measured frames, and emit one CSV row of medians and p95s. A second
    /// CSV holds every measured frame so the summary can be audited. During measurement this class
    /// only writes into preallocated arrays - the CSV text is built between points - so the
    /// instrument does not add allocations to the "GC Allocated In Frame" it is measuring.
    /// Counters that did not resolve are written as empty cells, never as zero.
    /// </summary>
    public sealed class BenchmarkRunner
    {
        private enum Phase { Idle, WarmUp, Measure, Finished }

        public const string SummaryHeader =
            "timestamp_utc," + RunEnvironment.CsvHeader + "," +
            "vsync_count,target_frame_rate,run_in_background,display_refresh_hz," +
            "config,agent_count,run,techniques,spatialHash,zeroAlloc,tickBudget,gpuInstancing,burstJobs," +
            "seed,fixed_timestep_s,stepping_mode,max_steps_per_frame,warmup_frames,measured_frames," +
            "steps_in_window,sim_steps_total,capped_frames,dropped_sim_seconds," +
            "frame_ms_median,frame_ms_p95,main_thread_ms_median,main_thread_ms_p95,sim_ms_per_frame_median,sim_ms_per_frame_p95," +
            "step_ms_median,step_ms_p95,present_ms_median,present_ms_p95,other_ms_median,other_ms_p95," +
            "gc_alloc_bytes_median,gc_alloc_bytes_p95,draw_calls_median,draw_calls_p95,setpass_calls_median,setpass_calls_p95," +
            "state_hash,invalid_counters";

        /// <summary>Directory the CSVs are written to; defaults to persistentDataPath when absent.</summary>
        public const string OutputDirArg = "-frameBudgetOutput";

        public const string FramesHeader =
            "config,agent_count,run,frame,frame_ms,main_thread_ms,sim_ms,steps,step_cap_hit,present_ms,other_ms,gc_alloc_bytes,draw_calls,setpass_calls";

        private Phase phase = Phase.Idle;
        private SimConfig config;
        private FrameBudgetDriver driver;
        private int[] sweep = Array.Empty<int>();
        private int sweepIndex;
        private int run;
        private int frameCounter;
        private int warmupFrames;

        // Preallocated per-point sample storage (no allocation while measuring).
        private double[] frameMs = Array.Empty<double>();
        private double[] mainThreadMs = Array.Empty<double>();
        private double[] simMs = Array.Empty<double>();
        private double[] presentMs = Array.Empty<double>();
        private double[] otherMs = Array.Empty<double>();
        private double[] gcBytes = Array.Empty<double>();
        private double[] drawCalls = Array.Empty<double>();
        private double[] setPassCalls = Array.Empty<double>();
        private int[] stepsPerFrame = Array.Empty<int>();
        private bool[] capHit = Array.Empty<bool>();
        private double[] stepMs = Array.Empty<double>();
        private int measured;
        private int stepCount;
        private double droppedAtStart;



        private readonly StringBuilder summary = new StringBuilder();
        private readonly StringBuilder frames = new StringBuilder();
        private DateTime startedUtc;

        /// <summary>Machine and build columns; constant for the process, so captured once per sweep.</summary>
        private string environmentRow = "";

        public bool IsRunning => phase == Phase.WarmUp || phase == Phase.Measure;
        public bool IsFinished => phase == Phase.Finished;
        public string Status { get; private set; } = "";
        public string SummaryPath { get; private set; }
        public string FramesPath { get; private set; }

        public void Start(SimConfig cfg, FrameBudgetDriver drv)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (drv == null) throw new ArgumentNullException(nameof(drv));
            if (IsRunning)
            {
                Debug.LogWarning("[FrameBudget] Benchmark already running; ignoring second start.");
                return;
            }

            // Nothing may pace a frame. A run that cannot prove this produces no numbers at all.
            string clamp = RunGuard.ApplyAndVerify();
            if (clamp != null)
            {
                Debug.LogError("[FrameBudget] ABORTING the benchmark: a frame-rate clamp is still in effect, so frame times would describe the display, not the code.\n"
                               + "[FrameBudget] " + clamp + "\n"
                               + "[FrameBudget] Turn vsync off in Project Settings > Quality for the active level and remove any target frame rate, then run again.");
                Status = "BENCHMARK ABORTED · frame-rate clamp in effect";
                BenchmarkLaunch.ExitIfUnattended(3);
                return;
            }

            config = cfg;
            driver = drv;
            sweep = cfg.sweepAgentCounts != null && cfg.sweepAgentCounts.Length > 0 ? (int[])cfg.sweepAgentCounts.Clone() : new[] { cfg.agentCount };
            warmupFrames = Mathf.Max(1, cfg.warmupFrameCount);   // the spawn frame is never measured
            if (warmupFrames != cfg.warmupFrameCount) Debug.LogWarning("[FrameBudget] warmupFrameCount raised to 1 so the spawn frame is discarded.");

            int m = cfg.measuredFrameCount;
            frameMs = new double[m];
            mainThreadMs = new double[m];
            simMs = new double[m];
            presentMs = new double[m];
            otherMs = new double[m];
            gcBytes = new double[m];
            drawCalls = new double[m];
            setPassCalls = new double[m];
            stepsPerFrame = new int[m];
            capHit = new bool[m];
            stepMs = new double[m * cfg.maxStepsPerFrame];

            summary.Clear();
            summary.Append(SummaryHeader).Append('\n');
            frames.Clear();
            frames.Append(FramesHeader).Append('\n');
            startedUtc = DateTime.UtcNow;
            environmentRow = RunEnvironment.CsvRow();
            sweepIndex = 0;
            run = 0;

            driver.UseConfig(cfg);
            Debug.Log("[FrameBudget] Benchmark started: config='" + cfg.name + "' techniques=" + cfg.TechniqueLabel
                      + " sweep=[" + string.Join(",", sweep) + "] runs=" + cfg.runsPerAgentCount
                      + " warmup=" + warmupFrames + " measured=" + m + " dt=" + cfg.fixedTimestep.ToString("R", CultureInfo.InvariantCulture)
                      + " maxSteps/frame=" + cfg.maxStepsPerFrame);
            Debug.Log("[FrameBudget] Environment: " + RunEnvironment.Describe());
            Debug.Log("[FrameBudget] Frame pacing: " + RunGuard.Describe());
            if (Debug.isDebugBuild)
            {
                Debug.LogWarning("[FrameBudget] This is a DEVELOPMENT build. Its instrumentation overhead is measured as if it were the code's cost; build without development mode for results.");
            }
            if (Application.isBatchMode)
            {
                Debug.LogWarning("[FrameBudget] Running in -batchmode: there is no Game view, so nothing is rendered. Draw calls and SetPass calls will read 0 and frame time excludes rendering. "
                                 + "For rendering-inclusive numbers run the same command without -batchmode, or run a Player build with " + BenchmarkLaunch.BenchmarkArg + ".");
            }
            BeginPoint();
        }

        private void BeginPoint()
        {
            int agents = sweep[sweepIndex];
            driver.RequestAgentCount(agents);

            // Start every point from a collected heap. The spawn frame and the warm-up frames are
            // discarded anyway; this just stops the previous point's garbage from being charged to this one.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            phase = Phase.WarmUp;
            frameCounter = 0;
            measured = 0;
            stepCount = 0;
            Status = "BENCHMARK · " + agents + " agents · run " + (run + 1) + "/" + config.runsPerAgentCount + " · warm-up " + warmupFrames + " frames";
        }

        /// <summary>Feed every frame's sample, right after it is taken and before the frame's simulation work.</summary>
        public void Tick(in FrameSample s)
        {
            switch (phase)
            {
                case Phase.WarmUp:
                    frameCounter++;
                    if (frameCounter >= warmupFrames)
                    {
                        phase = Phase.Measure;
                        droppedAtStart = driver.DroppedSimulationSeconds;
                        Status = "BENCHMARK · " + sweep[sweepIndex] + " agents · run " + (run + 1) + "/" + config.runsPerAgentCount + " · measuring " + frameMs.Length + " frames";
                    }
                    break;

                case Phase.Measure:
                    if (!s.HasFrameTime) break;
                    int k = measured;
                    frameMs[k] = s.FrameMs;
                    mainThreadMs[k] = s.MainThreadMs;
                    simMs[k] = s.SimMs;
                    presentMs[k] = s.PresentMs;
                    otherMs[k] = s.OtherMs;
                    gcBytes[k] = s.GcAllocatedBytes;
                    drawCalls[k] = s.DrawCalls;
                    setPassCalls[k] = s.SetPassCalls;
                    stepsPerFrame[k] = s.Steps;
                    capHit[k] = s.StepCapHit;
                    measured++;
                    if (measured >= frameMs.Length) FinishPoint();
                    break;
            }
        }

        /// <summary>Feed the duration of every simulation step; only steps taken inside the measured window are kept.</summary>
        public void RecordStep(double ms)
        {
            if (phase == Phase.Measure && stepCount < stepMs.Length) stepMs[stepCount++] = ms;
        }

        private void FinishPoint()
        {
            int agents = sweep[sweepIndex];
            int runNumber = run + 1;
            string configName = config.name;
            ulong stateHash = driver.World.StateHash();
            long totalSteps = driver.World.StepCount;   // equal total steps from the same seed must give equal state hashes
            double dropped = driver.DroppedSimulationSeconds - droppedAtStart;
            int cappedFrames = 0;
            FrameMetrics metrics = driver.Metrics;

            // Per-frame rows first: the percentile pass below sorts the arrays in place.
            for (int i = 0; i < measured; i++)
            {
                if (capHit[i]) cappedFrames++;
                frames.Append(Q(configName)).Append(',').Append(agents).Append(',').Append(runNumber).Append(',').Append(i).Append(',')
                      .Append(F(frameMs[i])).Append(',').Append(metrics.MainThreadValid ? F(mainThreadMs[i]) : "").Append(',')
                      .Append(F(simMs[i])).Append(',').Append(stepsPerFrame[i]).Append(',').Append(capHit[i] ? 1 : 0).Append(',')
                      .Append(F(presentMs[i])).Append(',').Append(F(otherMs[i])).Append(',')
                      .Append(metrics.GcAllocatedValid ? I(gcBytes[i]) : "").Append(',')
                      .Append(metrics.DrawCallsValid ? I(drawCalls[i]) : "").Append(',')
                      .Append(metrics.SetPassCallsValid ? I(setPassCalls[i]) : "").Append('\n');
            }

            Percentiles.MedianAndP95(frameMs, measured, out double frameMed, out double frameP95);
            Percentiles.MedianAndP95(simMs, measured, out double simMed, out double simP95);
            Percentiles.MedianAndP95(presentMs, measured, out double presentMed, out double presentP95);
            Percentiles.MedianAndP95(otherMs, measured, out double otherMed, out double otherP95);
            Percentiles.MedianAndP95(stepMs, stepCount, out double stepMed, out double stepP95);
            string mainStats = metrics.MainThreadValid ? Stats(mainThreadMs, measured, false) : ",";
            string gcStats = metrics.GcAllocatedValid ? Stats(gcBytes, measured, true) : ",";
            string drawStats = metrics.DrawCallsValid ? Stats(drawCalls, measured, true) : ",";
            string setPassStats = metrics.SetPassCallsValid ? Stats(setPassCalls, measured, true) : ",";

            summary.Append(Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(',')
                   .Append(environmentRow).Append(',')
                   .Append(RunGuard.VSyncCount).Append(',')
                   .Append(RunGuard.TargetFrameRate).Append(',')
                   .Append(RunGuard.RunInBackground ? 1 : 0).Append(',')
                   .Append(F(RunGuard.RefreshRateHz)).Append(',')
                   .Append(Q(configName)).Append(',')
                   .Append(agents).Append(',')
                   .Append(runNumber).Append(',')
                   .Append(Q(config.TechniqueLabel)).Append(',')
                   .Append(config.spatialHash ? 1 : 0).Append(',')
                   .Append(config.zeroAlloc ? 1 : 0).Append(',')
                   .Append(config.tickBudget ? 1 : 0).Append(',')
                   .Append(config.gpuInstancing ? 1 : 0).Append(',')
                   .Append(config.burstJobs ? 1 : 0).Append(',')
                   .Append(config.seed).Append(',')
                   .Append(config.fixedTimestep.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                   .Append(Q(driver.SteppingMode)).Append(',')
                   .Append(config.maxStepsPerFrame).Append(',')
                   .Append(warmupFrames).Append(',')
                   .Append(measured).Append(',')
                   .Append(stepCount).Append(',')
                   .Append(totalSteps).Append(',')
                   .Append(cappedFrames).Append(',')
                   .Append(F(dropped)).Append(',')
                   .Append(F(frameMed)).Append(',').Append(F(frameP95)).Append(',')
                   .Append(mainStats).Append(',')
                   .Append(F(simMed)).Append(',').Append(F(simP95)).Append(',')
                   .Append(F(stepMed)).Append(',').Append(F(stepP95)).Append(',')
                   .Append(F(presentMed)).Append(',').Append(F(presentP95)).Append(',')
                   .Append(F(otherMed)).Append(',').Append(F(otherP95)).Append(',')
                   .Append(gcStats).Append(',')
                   .Append(drawStats).Append(',')
                   .Append(setPassStats).Append(',')
                   .Append(stateHash.ToString("X16")).Append(',')
                   .Append(Q(metrics.InvalidCounters)).Append('\n');

            Debug.Log("[FrameBudget] " + agents + " agents, run " + runNumber + ": frame " + F(frameMed) + " / p95 " + F(frameP95)
                      + " ms | step " + F(stepMed) + " / p95 " + F(stepP95) + " ms (" + stepCount + " steps in window, " + totalSteps + " since spawn, " + cappedFrames + " capped frames)"
                      + " | gc " + (metrics.GcAllocatedValid ? gcStats : "n/a") + " B | draw " + (metrics.DrawCallsValid ? drawStats : "n/a")
                      + " | state " + stateHash.ToString("X16"));

            // Interleaved, not blocked: advance the agent count every point and the run index only
            // after a full pass, giving A B C D  A B C D  ... instead of A A A A  B B B B. This is
            // measured on a laptop, and a blocked order would map the thermal ramp straight onto the
            // configuration axis - the last configuration would look slower than it is, by an amount
            // indistinguishable from its real cost. Interleaving spreads drift across every
            // configuration and turns what remains into visible run-to-run spread. See docs/METHOD.md.
            sweepIndex++;
            if (sweepIndex >= sweep.Length)
            {
                sweepIndex = 0;
                run++;
            }
            if (run < config.runsPerAgentCount) BeginPoint();
            else Finish();
        }

        private void Finish()
        {
            phase = Phase.Finished;
            int exitCode = 0;
            try
            {
                // Committed results belong in the repository, next to the code that produced them;
                // persistentDataPath stays the default for ad-hoc runs that are nobody's evidence.
                string requested = CommandLine.GetString(OutputDirArg, null);
                string dir = requested != null
                    ? Path.GetFullPath(requested)
                    : Path.Combine(Application.persistentDataPath, "FrameBudget");
                Directory.CreateDirectory(dir);
                string stamp = startedUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string baseName = "benchmark_" + SanitizeFileName(config.name) + "_" + stamp;
                SummaryPath = Path.Combine(dir, baseName + ".csv");
                FramesPath = Path.Combine(dir, baseName + "_frames.csv");
                File.WriteAllText(SummaryPath, summary.ToString(), new UTF8Encoding(false));
                File.WriteAllText(FramesPath, frames.ToString(), new UTF8Encoding(false));
                Status = "BENCHMARK DONE · " + SummaryPath;
                Debug.Log("[FrameBudget] Benchmark complete.\n[FrameBudget] Summary CSV:   " + SummaryPath + "\n[FrameBudget] Per-frame CSV: " + FramesPath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Status = "BENCHMARK FAILED · could not write CSV: " + e.Message;
                Debug.LogError("[FrameBudget] Could not write benchmark CSV: " + e.Message);
                exitCode = 1;
            }
            BenchmarkLaunch.ExitIfUnattended(exitCode);
        }

        private static string Stats(double[] samples, int count, bool integer)
        {
            Percentiles.MedianAndP95(samples, count, out double med, out double p95);
            return integer ? I(med) + "," + I(p95) : F(med) + "," + F(p95);
        }

        private static string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.000", CultureInfo.InvariantCulture);
        private static string I(double v) => double.IsNaN(v) ? "" : v.ToString("0", CultureInfo.InvariantCulture);
        private static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        private static string SanitizeFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char ch in name) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
            return sb.Length == 0 ? "config" : sb.ToString();
        }
    }
}
