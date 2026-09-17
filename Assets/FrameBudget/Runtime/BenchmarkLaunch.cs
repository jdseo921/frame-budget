using System;
using UnityEngine;

namespace FrameBudget
{
    /// <summary>
    /// Reads the "run the benchmark unattended" request from the command line, so a built player and
    /// the editor CLI entry point share one mechanism:
    ///   -frameBudgetBenchmark                 start the benchmark as soon as the scene is up
    ///   -frameBudgetConfig &lt;asset name&gt;   which SimConfig to run (must be listed on the driver)
    ///   -frameBudgetOutput &lt;directory&gt;    where the CSVs are written (default: persistentDataPath)
    /// In the editor, <c>BenchmarkCli.Run</c> also raises a SessionState flag that survives the
    /// domain reload of entering Play mode. A benchmark requested this way is unattended: the
    /// process exits when it finishes, whether or not it is running in -batchmode.
    /// </summary>
    public static class BenchmarkLaunch
    {
        public const string BenchmarkArg = "-frameBudgetBenchmark";
        public const string ConfigArg = "-frameBudgetConfig";
        public const string SessionRequestKey = "FrameBudget.BenchmarkRequested";

        /// <summary>True when the benchmark was requested from the command line or the CLI entry point.</summary>
        public static bool Unattended { get; private set; }

        /// <summary>Marks the process as unattended so it exits when its work is done, for command-line paths other than the benchmark itself.</summary>
        public static void MarkUnattended()
        {
            Unattended = true;
        }

        public static bool TryGetRequest(out string configName)
        {
            configName = CommandLine.GetString(ConfigArg, null);
            bool requested = CommandLine.HasFlag(BenchmarkArg);

#if UNITY_EDITOR
            if (UnityEditor.SessionState.GetBool(SessionRequestKey, false))
            {
                UnityEditor.SessionState.EraseBool(SessionRequestKey);
                requested = true;
            }
#endif
            if (requested) Unattended = true;
            return requested;
        }

        /// <summary>Ends the process when running unattended (batch mode, or a command-line requested run); does nothing in an interactive session.</summary>
        public static void ExitIfUnattended(int exitCode)
        {
            if (!Application.isBatchMode && !Unattended) return;
            Debug.Log("[FrameBudget] Unattended run: exiting with code " + exitCode + ".");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(exitCode);
#else
            Application.Quit(exitCode);
#endif
        }
    }
}
