using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FrameBudget
{
    /// <summary>
    /// Measures managed allocation without the profiler, and says honestly which quantity it got.
    ///
    /// Day 2 found that the "GC Allocated In Frame" ProfilerRecorder counter does not exist in a
    /// release player, which left every GC column empty and blocked the zeroAlloc technique, whose
    /// entire claim is that it removes allocation. Day 3 surveyed the runtime alternatives in an
    /// actual IL2CPP player and found:
    ///
    ///   GC.GetAllocatedBytesForCurrentThread()   returns 0 always - IL2CPP's Boehm collector does
    ///                                            not keep per-thread allocation totals. Not
    ///                                            stripped; simply unimplemented.
    ///   GC.GetTotalAllocatedBytes(bool)          absent from the .NET Framework 4.8 class library
    ///                                            this project compiles against.
    ///   GC.GetTotalMemory(false)                 tracks a 1 MiB test allocation to within 4 KiB.
    ///   Profiler.GetMonoUsedSizeLong()           identical values; needs UnityEngine.Profiling.
    ///   GC.CollectionCount(0)                    works, and is how collections are detected.
    ///
    /// So allocation is measured as <b>managed heap growth</b> rather than cumulative allocation,
    /// and the difference matters. Boehm reclaims memory only when it collects, so between
    /// collections a rise in heap size is exactly the bytes allocated. Across a collection it is
    /// not: the heap can shrink while a great deal was allocated. Frames in which a collection ran
    /// are therefore excluded from the heap-growth statistic and counted separately, so that the two
    /// facts a reader needs - how much a frame allocates, and how often that forces a collection -
    /// are both reported and neither is silently averaged into the other.
    ///
    /// Nothing here is trusted on documentation. <see cref="Verify"/> allocates a known number of
    /// bytes and requires the counter to report them, on the machine and backend in use.
    /// </summary>
    public static class AllocationProbe
    {
        public enum Source
        {
            None = 0,

            /// <summary>Cumulative bytes allocated by this thread. Exact, but unimplemented under IL2CPP's Boehm collector.</summary>
            CurrentThreadAllocated = 1,

            /// <summary>Cumulative bytes allocated by the process. Absent from several of Unity's API profiles.</summary>
            TotalAllocated = 2,

            /// <summary>Managed heap bytes currently in use. Equals allocation between collections; meaningless across one.</summary>
            HeapInUse = 3,
        }

        private const int ProbeBytes = 1 << 20;   // 1 MiB
        private const long ToleranceBytes = 128 * 1024;

        private static byte[] probeKeepAlive;

        public static Source ActiveSource { get; private set; } = Source.None;
        public static bool Supported => ActiveSource != Source.None;

        /// <summary>True when the active counter is cumulative allocation, false when it is heap size and therefore invalidated by a collection.</summary>
        public static bool IsCumulative => ActiveSource == Source.CurrentThreadAllocated || ActiveSource == Source.TotalAllocated;

        public static string SourceName
        {
            get
            {
                switch (ActiveSource)
                {
                    case Source.CurrentThreadAllocated: return "GC.GetAllocatedBytesForCurrentThread";
                    case Source.TotalAllocated: return "GC.GetTotalAllocatedBytes";
                    case Source.HeapInUse: return "GC.GetTotalMemory(heap-growth-between-collections)";
                    default: return "none";
                }
            }
        }

        /// <summary>
        /// GC.GetTotalAllocatedBytes is not in every API profile this project could be built under,
        /// so it is bound at runtime. Bound as a delegate rather than invoked through MethodInfo,
        /// because MethodInfo.Invoke boxes, and an allocation counter that allocates on every read
        /// would corrupt the measurement it exists to take.
        /// </summary>
        private static Func<bool, long> totalAllocatedBytes;
        private static bool totalAllocatedResolved;

        private static Func<bool, long> TotalAllocatedBytes
        {
            get
            {
                if (totalAllocatedResolved) return totalAllocatedBytes;
                totalAllocatedResolved = true;
                try
                {
                    MethodInfo method = typeof(GC).GetMethod(
                        "GetTotalAllocatedBytes",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(bool) },
                        null);
                    if (method != null)
                    {
                        totalAllocatedBytes = (Func<bool, long>)Delegate.CreateDelegate(typeof(Func<bool, long>), method);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[FrameBudget] GC.GetTotalAllocatedBytes could not be bound on this build: " + e.Message);
                    totalAllocatedBytes = null;
                }
                return totalAllocatedBytes;
            }
        }

        /// <summary>Current value of the active counter, or -1 when none verified. Differences between consecutive samples give per-frame bytes.</summary>
        public static long Sample()
        {
            try
            {
                switch (ActiveSource)
                {
                    case Source.CurrentThreadAllocated:
                        return GC.GetAllocatedBytesForCurrentThread();
                    case Source.TotalAllocated:
                        Func<bool, long> total = TotalAllocatedBytes;
                        return total != null ? total(false) : -1L;
                    case Source.HeapInUse:
                        return GC.GetTotalMemory(false);
                    default:
                        return -1L;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[FrameBudget] Allocation counter " + SourceName + " threw during sampling and is now disabled: " + e.Message);
                ActiveSource = Source.None;
                return -1L;
            }
        }

        /// <summary>Generation-0 collection count. A frame across which this changes has a heap delta that is not allocation.</summary>
        public static int CollectionCount()
        {
            try
            {
                return GC.CollectionCount(0);
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Picks a counter and proves it works by allocating a known number of bytes and requiring
        /// the counter to move by that much. Cumulative counters are preferred over heap size,
        /// because heap size carries the collection caveat. Returns a report for the log.
        /// </summary>
        public static bool Verify(out string report)
        {
            ActiveSource = Source.None;
            var lines = new List<string>();

            foreach (Source candidate in new[] { Source.CurrentThreadAllocated, Source.TotalAllocated, Source.HeapInUse })
            {
                bool ok = TryVerify(candidate, out string line);
                lines.Add(line);
                if (ok)
                {
                    report = string.Join("\n", lines);
                    return true;
                }
            }

            ActiveSource = Source.None;
            lines.Add("No allocation counter survived verification on this build.");
            report = string.Join("\n", lines);
            return false;
        }

        private static bool TryVerify(Source candidate, out string report)
        {
            long before, after;
            try
            {
                if (candidate == Source.TotalAllocated && TotalAllocatedBytes == null)
                {
                    ActiveSource = Source.None;
                    report = Name(candidate) + ": absent from this build's class library.";
                    return false;
                }

                ActiveSource = candidate;

                // Touch the counter once first: the very first call can itself allocate, and that
                // would otherwise be charged to the probe and inflate the measured delta.
                Sample();

                before = Sample();
                probeKeepAlive = new byte[ProbeBytes];
                probeKeepAlive[0] = 1;
                probeKeepAlive[ProbeBytes - 1] = 2;
                after = Sample();
            }
            catch (Exception e)
            {
                ActiveSource = Source.None;
                report = Name(candidate) + ": threw (" + e.GetType().Name + ": " + e.Message + ") - not usable.";
                return false;
            }

            if (before < 0 || after < 0)
            {
                ActiveSource = Source.None;
                report = Name(candidate) + ": unavailable on this build.";
                return false;
            }

            long delta = after - before;
            bool ok = delta >= ProbeBytes && delta <= ProbeBytes + ToleranceBytes;
            report = Name(candidate) + ": allocated " + ProbeBytes + " B, counter moved " + delta + " B -> "
                     + (ok ? "VERIFIED" : "REJECTED (expected " + ProbeBytes + ".." + (ProbeBytes + ToleranceBytes) + " B)");
            if (!ok) ActiveSource = Source.None;
            return ok;
        }

        private static string Name(Source source)
        {
            switch (source)
            {
                case Source.CurrentThreadAllocated: return "GC.GetAllocatedBytesForCurrentThread()";
                case Source.TotalAllocated: return "GC.GetTotalAllocatedBytes(false)";
                case Source.HeapInUse: return "GC.GetTotalMemory(false)";
                default: return "none";
            }
        }

        public static void ReleaseProbeBuffer()
        {
            probeKeepAlive = null;
        }

        /// <summary>
        /// Diagnostic: tries every allocation-related counter this runtime might expose and reports
        /// how each responds to a known allocation. Used to explain a verification failure, and to
        /// record in the log what the runtime does offer. Never used on the measuring path.
        /// </summary>
        public static string[] SurveyCandidates()
        {
            var results = new List<string>();
            results.Add(Probe("GC.GetAllocatedBytesForCurrentThread()", () => GC.GetAllocatedBytesForCurrentThread()));

            Func<bool, long> total = TotalAllocatedBytes;
            results.Add(total == null
                ? "GC.GetTotalAllocatedBytes(false): absent from this class library"
                : Probe("GC.GetTotalAllocatedBytes(false)", () => total(false)));

            results.Add(Probe("GC.GetTotalMemory(false)", () => GC.GetTotalMemory(false)));
            results.Add(Probe("GC.CollectionCount(0)", () => GC.CollectionCount(0)));
            results.Add(Probe("Profiler.GetMonoUsedSizeLong()", () => UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong()));
            results.Add(Probe("Profiler.GetTotalAllocatedMemoryLong()", () => UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong()));
            return results.ToArray();
        }

        private static string Probe(string name, Func<long> read)
        {
            try
            {
                read();
                long before = read();
                var block = new byte[ProbeBytes];
                block[0] = 1;
                block[ProbeBytes - 1] = 2;
                long after = read();
                long delta = after - before;
                string verdict = delta >= ProbeBytes ? "TRACKS ALLOCATION"
                    : (delta != 0 ? "moves, but not by the allocated amount" : "does not move");
                return name + ": before=" + before + " after=" + after + " delta=" + delta + " B -> " + verdict
                       + " (block " + block.Length + " B)";
            }
            catch (Exception e)
            {
                return name + ": threw " + e.GetType().Name + " (" + e.Message + ")";
            }
        }
    }
}
