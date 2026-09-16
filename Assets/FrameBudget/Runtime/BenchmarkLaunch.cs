using System;
using UnityEngine;

namespace FrameBudget
{
    /// <summary>
    /// Reads the "run the benchmark unattended" request from the command line, so a built player and
    /// the editor CLI entry point share one mechanism:
    ///   -frameBudgetBenchmark                 start the benchmark as soon as the scene is up
    ///   -frameBudgetConfig &lt;asset name&gt;   which SimConfig to run (must be listed on the driver)
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

        public static bool TryGetRequest(out string configName)
        {
            configName = null;
            bool requested = false;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], BenchmarkArg, StringComparison.OrdinalIgnoreCase))
                {
                    requested = true;
                }
                else if (string.Equals(args[i], ConfigArg, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    configName = args[i + 1];
                }
            }

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
