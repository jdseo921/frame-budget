using System.Text;
using UnityEngine;

namespace FrameBudget
{
    /// <summary>
    /// Removes everything that could pace a frame, then proves it was removed.
    ///
    /// This is the single most important guard in the harness. With vsync on, a frame that finishes
    /// in 4 ms and a frame that finishes in 15 ms both report about 16.7 ms, every metric saturates at
    /// the refresh rate, and the benchmark measures the display instead of the code - while looking
    /// entirely healthy. The same is true of any positive targetFrameRate. An unfocused window that
    /// is allowed to throttle produces the same class of lie more quietly.
    ///
    /// So the benchmark path sets all three and then reads them back. If a setting did not take, the
    /// run is aborted: no numbers at all is a better outcome than numbers that describe the monitor.
    /// The achieved values are recorded in every CSV row so a reader can confirm the clamps were off
    /// without taking this comment's word for it.
    /// </summary>
    public static class RunGuard
    {
        /// <summary>vsync divisor actually in effect after <see cref="ApplyAndVerify"/>; 0 means no vsync.</summary>
        public static int VSyncCount => QualitySettings.vSyncCount;

        /// <summary>Frame cap actually in effect; -1 (or any value &lt;= 0) means uncapped.</summary>
        public static int TargetFrameRate => Application.targetFrameRate;

        public static bool RunInBackground => Application.runInBackground;

        /// <summary>Refresh rate of the display, for comparison against measured frame times: numbers pinned near 1000/refresh are the signature of a clamp that escaped this guard.</summary>
        public static double RefreshRateHz
        {
            get
            {
                RefreshRate rate = Screen.currentResolution.refreshRateRatio;
                return rate.denominator == 0 ? 0.0 : (double)rate.numerator / rate.denominator;
            }
        }

        /// <summary>
        /// Applies the no-clamp settings and verifies them. Returns null when the run may proceed, or
        /// an operator-readable description of every setting that refused to take.
        /// </summary>
        public static string ApplyAndVerify()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Application.runInBackground = true;

            var problems = new StringBuilder();
            if (QualitySettings.vSyncCount != 0)
            {
                problems.Append("QualitySettings.vSyncCount is ").Append(QualitySettings.vSyncCount)
                        .Append(" after being set to 0 - every frame time would be quantised to the display's refresh interval. ");
            }
            if (Application.targetFrameRate > 0)
            {
                problems.Append("Application.targetFrameRate is ").Append(Application.targetFrameRate)
                        .Append(" after being set to -1 - frames would be paced to that rate. ");
            }
            if (!Application.runInBackground)
            {
                problems.Append("Application.runInBackground is false - an unfocused window would be throttled mid-run. ");
            }
            return problems.Length == 0 ? null : problems.ToString().TrimEnd();
        }

        /// <summary>One line for the log, so the settings appear in the run's own record as well as the CSV.</summary>
        public static string Describe()
        {
            return "vSyncCount=" + VSyncCount
                   + " targetFrameRate=" + TargetFrameRate
                   + " runInBackground=" + RunInBackground
                   + " displayRefresh=" + RefreshRateHz.ToString("F2") + "Hz";
        }
    }
}
