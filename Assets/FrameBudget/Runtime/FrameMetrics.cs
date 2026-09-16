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
        public double SimMs;            // sum of simulation step time in the frame (Stopwatch)
        public double PresentMs;        // time spent copying positions into Transforms (Stopwatch)
        public double OtherMs;          // FrameMs - SimMs - PresentMs: rendering, engine, editor overhead
        public int Steps;
        public bool StepCapHit;
        public long GcAllocatedBytes;
        public long DrawCalls;
        public long SetPassCalls;
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
        public const double BudgetMs = 1000.0 / 60.0;

        public const string GcAllocatedCounter = "GC Allocated In Frame";
        public const string DrawCallsCounter = "Draw Calls Count";
        public const string SetPassCallsCounter = "SetPass Calls Count";
        public const string MainThreadCounter = "Main Thread";

        private ProfilerRecorder gcAllocatedRecorder;
        private ProfilerRecorder drawCallsRecorder;
        private ProfilerRecorder setPassCallsRecorder;
        private ProfilerRecorder mainThreadRecorder;

        public bool GcAllocatedValid { get; private set; }
        public bool DrawCallsValid { get; private set; }
        public bool SetPassCallsValid { get; private set; }
        public bool MainThreadValid { get; private set; }

        /// <summary>Semicolon-separated names of counters that failed to resolve, empty when all resolved. Written into the CSV.</summary>
        public string InvalidCounters { get; private set; } = "";

        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long lastFrameStartTicks = -1;

        public readonly RollingWindow FrameMs = new RollingWindow(WindowSize);
        public readonly RollingWindow MainThreadMs = new RollingWindow(WindowSize);
        public readonly RollingWindow SimMs = new RollingWindow(WindowSize);
        public readonly RollingWindow StepMs = new RollingWindow(WindowSize);
        public readonly RollingWindow PresentMs = new RollingWindow(WindowSize);
        public readonly RollingWindow OtherMs = new RollingWindow(WindowSize);
        public readonly RollingWindow GcBytes = new RollingWindow(WindowSize);
        public readonly RollingWindow DrawCalls = new RollingWindow(WindowSize);
        public readonly RollingWindow SetPassCalls = new RollingWindow(WindowSize);

        public FrameMetrics()
        {
            var invalid = new List<string>();
            gcAllocatedRecorder = Start(ProfilerCategory.Memory, GcAllocatedCounter, invalid, out bool gcValid);
            drawCallsRecorder = Start(ProfilerCategory.Render, DrawCallsCounter, invalid, out bool drawValid);
            setPassCallsRecorder = Start(ProfilerCategory.Render, SetPassCallsCounter, invalid, out bool setPassValid);
            mainThreadRecorder = Start(ProfilerCategory.Internal, MainThreadCounter, invalid, out bool mainValid);
            GcAllocatedValid = gcValid;
            DrawCallsValid = drawValid;
            SetPassCallsValid = setPassValid;
            MainThreadValid = mainValid;
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
                GcAllocatedBytes = GcAllocatedValid ? gcAllocatedRecorder.LastValue : -1,
                DrawCalls = DrawCallsValid ? drawCallsRecorder.LastValue : -1,
                SetPassCalls = SetPassCallsValid ? setPassCallsRecorder.LastValue : -1,
            };
            if (s.HasFrameTime)
            {
                s.FrameMs = (now - lastFrameStartTicks) * 1000.0 / Stopwatch.Frequency;
                s.OtherMs = s.FrameMs - s.SimMs - s.PresentMs;
            }
            lastFrameStartTicks = now;

            if (s.HasFrameTime)
            {
                FrameMs.Add(s.FrameMs);
                SimMs.Add(s.SimMs);
                PresentMs.Add(s.PresentMs);
                OtherMs.Add(s.OtherMs);
                if (MainThreadValid) MainThreadMs.Add(s.MainThreadMs);
                if (GcAllocatedValid) GcBytes.Add(s.GcAllocatedBytes);
                if (DrawCallsValid) DrawCalls.Add(s.DrawCalls);
                if (SetPassCallsValid) SetPassCalls.Add(s.SetPassCalls);
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
            SimMs.Clear();
            StepMs.Clear();
            PresentMs.Clear();
            OtherMs.Clear();
            GcBytes.Clear();
            DrawCalls.Clear();
            SetPassCalls.Clear();
        }

        public void Dispose()
        {
            gcAllocatedRecorder.Dispose();
            drawCallsRecorder.Dispose();
            setPassCallsRecorder.Dispose();
            mainThreadRecorder.Dispose();
        }
    }
}
