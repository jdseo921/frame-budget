using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FrameBudget
{
    /// <summary>One frame's worth of measurements. Counter fields hold -1 (and time fields NaN) when a counter did not resolve; never zero.</summary>
    public struct FrameSample
    {
        public bool HasFrameTime;
        public double FrameMs;          // wall-clock interval between consecutive frame starts (Stopwatch), not Time.deltaTime
        public double MainThreadMs;     // profiler "Main Thread" counter for the previous frame
        public double GpuMs;            // profiler "GPU Frame Time" counter: the other ceiling a CPU-side win can hide behind
        public double SimMs;            // sum of simulation step time in the frame (Stopwatch)
        public double PresentMs;        // time spent copying positions into Transforms (Stopwatch)
        public double OtherMs;          // FrameMs - SimMs - PresentMs: rendering, engine, editor overhead
        public int Steps;
        public bool StepCapHit;
        public long GcAllocatedBytes;   // bytes allocated during the frame; -1 when unmeasured or invalidated by a collection
        public int GcCollections;       // generation-0 collections that ran during the frame
        public long DrawCalls;
        public long SetPassCalls;
        public long Batches;            // renderer batch groups: the diagnostic for why SetPass scales with object count
    }

    /// <summary>
    /// The measuring device. Frame time comes from a Stopwatch interval between consecutive frame
    /// starts and from the profiler's "Main Thread" counter - never from Time.deltaTime, which is
    /// clamped by maximumDeltaTime, scaled by timeScale, and is the engine's opinion rather than a
    /// measurement we own. GC, draw-call and SetPass counts come from ProfilerRecorder handles whose
    /// names are verified at start-up: a counter that does not resolve is logged as an error and
    /// reported as n/a, not as zero. Rolling windows hold the last <see cref="WindowSize"/> frames
    /// for the HUD's median / p95 / graph.
    /// </summary>
    public sealed class FrameMetrics : IDisposable
    {
        public const int WindowSize = 120;

        /// <summary>Counts down in <see cref="BeginFrame"/>; zero in every run that is not capturing.</summary>
        private int samplesToDiscard;

        /// <summary>
        /// Keeps the next <paramref name="count"/> frames out of the rolling windows the HUD reports,
        /// so the figures on a captured image describe real frames rather than the frames that paid
        /// for the capture. Two is the useful default: the readback stalls the frame it runs in, and
        /// the encode and file write land inside the same interval, but the disturbance measurably
        /// outlives that one frame.
        /// </summary>
        public void DiscardNextSamples(int count = 2)
        {
            if (count > samplesToDiscard) samplesToDiscard = count;
        }
        public const double BudgetMs = 1000.0 / 60.0;

        public const string DrawCallsCounter = "Draw Calls Count";
        public const string SetPassCallsCounter = "SetPass Calls Count";
        public const string MainThreadCounter = "Main Thread";
        public const string GpuFrameTimeCounter = "GPU Frame Time";
        public const string BatchesCounter = "Batches Count";

        private ProfilerRecorder drawCallsRecorder;
        private ProfilerRecorder setPassCallsRecorder;
        private ProfilerRecorder mainThreadRecorder;
        private ProfilerRecorder gpuFrameTimeRecorder;
        private ProfilerRecorder batchesRecorder;

        /// <summary>Allocation counter reading at the previous frame boundary; -1 until the first sample.</summary>
        private long lastAllocationSample = -1L;

        /// <summary>Generation-0 collection count at the previous frame boundary; -1 until the first sample.</summary>
        private int lastCollectionCount = -1;

        /// <summary>Measured frames whose heap delta was discarded because a collection ran inside them.</summary>
        public int FramesWithCollection { get; private set; }

        /// <summary>Generation-0 collections seen since the last <see cref="ClearWindows"/>.</summary>
        public int CollectionsObserved { get; private set; }

        /// <summary>True when a runtime allocation counter passed verification (see <see cref="AllocationProbe"/>), not when a profiler counter resolved.</summary>
        public bool GcAllocatedValid { get; private set; }
        public bool DrawCallsValid { get; private set; }
        public bool SetPassCallsValid { get; private set; }
        public bool MainThreadValid { get; private set; }
        public bool GpuFrameTimeValid { get; private set; }
        public bool BatchesValid { get; private set; }

        /// <summary>Which runtime API the allocation numbers came from, for the log and the CSV.</summary>
        public string AllocationSource => AllocationProbe.SourceName;

        /// <summary>Semicolon-separated names of counters that failed to resolve, empty when all resolved. Written into the CSV.</summary>
        public string InvalidCounters { get; private set; } = "";

        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long lastFrameStartTicks = -1;

        public readonly RollingWindow FrameMs = new RollingWindow(WindowSize);
        public readonly RollingWindow MainThreadMs = new RollingWindow(WindowSize);
        public readonly RollingWindow GpuMs = new RollingWindow(WindowSize);
        public readonly RollingWindow SimMs = new RollingWindow(WindowSize);
        public readonly RollingWindow StepMs = new RollingWindow(WindowSize);
        public readonly RollingWindow PresentMs = new RollingWindow(WindowSize);
        public readonly RollingWindow OtherMs = new RollingWindow(WindowSize);
        public readonly RollingWindow GcBytes = new RollingWindow(WindowSize);

        /// <summary>
        /// Gen-0 collections per frame. Unlike <see cref="GcBytes"/> this is measurable in every
        /// frame however heavy the allocation is - a frame in which a collection ran has a heap
        /// delta that is not allocation, so its byte figure is discarded, but the collection itself
        /// is still counted. It is also the quantity that costs frame time, since a collection is a
        /// pause. That makes it the primary allocation metric and bytes the supporting detail.
        /// </summary>
        public readonly RollingWindow CollectionsPerFrame = new RollingWindow(WindowSize);
        public readonly RollingWindow DrawCalls = new RollingWindow(WindowSize);
        public readonly RollingWindow SetPassCalls = new RollingWindow(WindowSize);
        public readonly RollingWindow Batches = new RollingWindow(WindowSize);

        public FrameMetrics()
        {
            var invalid = new List<string>();
            drawCallsRecorder = Start(ProfilerCategory.Render, DrawCallsCounter, invalid, out bool drawValid);
            setPassCallsRecorder = Start(ProfilerCategory.Render, SetPassCallsCounter, invalid, out bool setPassValid);
            mainThreadRecorder = Start(ProfilerCategory.Internal, MainThreadCounter, invalid, out bool mainValid);
            gpuFrameTimeRecorder = Start(ProfilerCategory.Render, GpuFrameTimeCounter, invalid, out bool gpuValid);
            batchesRecorder = Start(ProfilerCategory.Render, BatchesCounter, invalid, out bool batchesValid);
            DrawCallsValid = drawValid;
            SetPassCallsValid = setPassValid;
            MainThreadValid = mainValid;
            GpuFrameTimeValid = gpuValid;
            BatchesValid = batchesValid;

            // Allocation does not come from a profiler counter: day 2 established that the counter
            // does not exist in a release player. It comes from a runtime API that is verified here,
            // on this build, by allocating a known number of bytes and checking the counter moved.
            GcAllocatedValid = AllocationProbe.Verify(out string allocationReport);
            Debug.Log("[FrameBudget] Allocation counter verification:\n    " + allocationReport.Replace("\n", "\n    "));
            if (!GcAllocatedValid)
            {
                invalid.Add("runtime/allocation");
                Debug.LogError("[FrameBudget] No allocation counter could be verified on this build. Allocation will be reported as n/a and left empty in the CSV - it is not zero.");
            }
            AllocationProbe.ReleaseProbeBuffer();

            InvalidCounters = string.Join(";", invalid);
            LogCountersOfInterest();
        }

        private static ProfilerRecorder Start(ProfilerCategory category, string counterName, List<string> invalid, out bool valid)
        {
            var recorder = ProfilerRecorder.StartNew(category, counterName, 1);
            valid = recorder.Valid;
            if (valid)
            {
                Debug.Log("[FrameBudget] ProfilerRecorder resolved: " + category.Name + " / \"" + counterName + "\"");
            }
            else
            {
                invalid.Add(category.Name + "/" + counterName);
                Debug.LogError("[FrameBudget] ProfilerRecorder did NOT resolve: " + category.Name + " / \"" + counterName
                               + "\" on Unity " + Application.unityVersion
                               + ". This metric will be shown as n/a and left empty in the CSV - it is not zero.");
            }
            return recorder;
        }

        /// <summary>Logs every available counter whose name is relevant to this instrument, so the names can be verified against this Unity version.</summary>
        private static void LogCountersOfInterest()
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            var sb = new StringBuilder();
            sb.Append("[FrameBudget] Profiler counters available on Unity ").Append(Application.unityVersion)
              .Append(" matching Draw / SetPass / Batches / GC / Main Thread / Frame Time (").Append(handles.Count).Append(" counters total):\n");
            foreach (ProfilerRecorderHandle handle in handles)
            {
                ProfilerRecorderDescription d = ProfilerRecorderHandle.GetDescription(handle);
                string name = d.Name;
                if (name.IndexOf("Draw", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("SetPass", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Batches", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("GC ", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Main Thread", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Frame Time", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sb.Append("    ").Append(d.Category.Name).Append(" / \"").Append(name).Append("\"  [").Append(d.UnitType).Append(']').Append('\n');
                }
            }
            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// Call once at the very start of each frame, before any simulation work. Everything sampled
        /// here describes the frame that just finished: the wall-clock interval since the previous
        /// call, the recorders' last completed frame, and the simulation timings the caller kept
        /// from its previous Update.
        /// </summary>
        public FrameSample BeginFrame(double simMsPreviousFrame, int stepsPreviousFrame, double presentMsPreviousFrame, bool stepCapHitPreviousFrame)
        {
            long now = clock.ElapsedTicks;
            var s = new FrameSample
            {
                HasFrameTime = lastFrameStartTicks >= 0,
                SimMs = simMsPreviousFrame,
                Steps = stepsPreviousFrame,
                PresentMs = presentMsPreviousFrame,
                StepCapHit = stepCapHitPreviousFrame,
                MainThreadMs = MainThreadValid ? mainThreadRecorder.LastValue / 1_000_000.0 : double.NaN,
                GpuMs = GpuFrameTimeValid ? gpuFrameTimeRecorder.LastValue / 1_000_000.0 : double.NaN,
                DrawCalls = DrawCallsValid ? drawCallsRecorder.LastValue : -1,
                SetPassCalls = SetPassCallsValid ? setPassCallsRecorder.LastValue : -1,
                Batches = BatchesValid ? batchesRecorder.LastValue : -1,
            };

            // Allocation across the frame that just finished. When the counter reports heap size
            // rather than cumulative allocation, a frame in which a collection ran has a delta that
            // is not allocation - the heap may even have shrunk - so that frame's bytes are
            // discarded and counted instead. See AllocationProbe for why this is the only counter
            // available in a release IL2CPP player.
            if (GcAllocatedValid)
            {
                long allocNow = AllocationProbe.Sample();
                int collectionsNow = AllocationProbe.CollectionCount();
                s.GcCollections = lastCollectionCount >= 0 && collectionsNow >= lastCollectionCount
                    ? collectionsNow - lastCollectionCount
                    : 0;

                bool collectionInvalidated = !AllocationProbe.IsCumulative && s.GcCollections > 0;
                s.GcAllocatedBytes = (lastAllocationSample >= 0 && allocNow >= lastAllocationSample && !collectionInvalidated)
                    ? allocNow - lastAllocationSample
                    : -1;

                lastAllocationSample = allocNow;
                lastCollectionCount = collectionsNow;
            }
            else
            {
                s.GcAllocatedBytes = -1;
                s.GcCollections = 0;
            }
            if (s.HasFrameTime)
            {
                s.FrameMs = (now - lastFrameStartTicks) * 1000.0 / Stopwatch.Frequency;
                s.OtherMs = s.FrameMs - s.SimMs - s.PresentMs;
            }
            lastFrameStartTicks = now;

            // A frame that paid for a screen readback is not a representative frame. ReadPixels
            // stalls the pipeline, and the interval measured here carries that cost, so letting it
            // into the rolling windows would print an inflated frame time, step time and p95 onto
            // the very image being captured - a clip reporting numbers the app does not produce.
            // The sample is still returned to the caller and the timing and allocation baselines
            // still advance; only the windows the HUD reads skip it. PresentationCapture sets this
            // immediately after each readback, and nothing else does.
            bool discardThisSample = samplesToDiscard > 0;
            if (discardThisSample) samplesToDiscard--;

            if (s.HasFrameTime && !discardThisSample)
            {
                FrameMs.Add(s.FrameMs);
                SimMs.Add(s.SimMs);
                PresentMs.Add(s.PresentMs);
                OtherMs.Add(s.OtherMs);
                if (MainThreadValid) MainThreadMs.Add(s.MainThreadMs);
                if (GpuFrameTimeValid) GpuMs.Add(s.GpuMs);
                if (GcAllocatedValid)
                {
                    CollectionsObserved += s.GcCollections;
                    CollectionsPerFrame.Add(s.GcCollections);
                    if (s.GcAllocatedBytes >= 0) GcBytes.Add(s.GcAllocatedBytes);
                    else FramesWithCollection++;
                }
                if (DrawCallsValid) DrawCalls.Add(s.DrawCalls);
                if (SetPassCallsValid) SetPassCalls.Add(s.SetPassCalls);
                if (BatchesValid) Batches.Add(s.Batches);
            }
            return s;
        }

        /// <summary>Records the duration of one simulation step (per-step cost, independent of how many steps a frame ran).</summary>
        public void RecordStep(double stepMs)
        {
            StepMs.Add(stepMs);
        }

        public void ClearWindows()
        {
            FrameMs.Clear();
            MainThreadMs.Clear();
            GpuMs.Clear();
            SimMs.Clear();
            StepMs.Clear();
            PresentMs.Clear();
            OtherMs.Clear();
            GcBytes.Clear();
            CollectionsPerFrame.Clear();
            DrawCalls.Clear();
            SetPassCalls.Clear();
            Batches.Clear();
            FramesWithCollection = 0;
            CollectionsObserved = 0;
        }

        public void Dispose()
        {
            drawCallsRecorder.Dispose();
            setPassCallsRecorder.Dispose();
            mainThreadRecorder.Dispose();
            gpuFrameTimeRecorder.Dispose();
            batchesRecorder.Dispose();
        }
    }
}
