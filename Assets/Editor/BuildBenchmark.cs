using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FrameBudget.EditorTools
{
    /// <summary>
    /// Builds the release Windows x64 player the benchmark is measured in. Editor numbers are not
    /// results, so this script exists before any number does.
    ///
    /// <code>
    ///   Unity.exe -batchmode -quit -projectPath &lt;project&gt; -executeMethod FrameBudget.EditorTools.BuildBenchmark.Build
    ///             -frameBudgetBuildPath Builds/Windows64/FrameBudget.exe -logFile &lt;log&gt;
    /// </code>
    ///
    /// IL2CPP is required and checked for first: a missing module stops the build with instructions
    /// rather than producing a Mono player that looks like the same thing. Mono is possible only by
    /// passing -frameBudgetAllowMono explicitly, and the backend is written into every CSV row either
    /// way, because Mono and IL2CPP timings are not comparable.
    /// Development build, deep profiling and profiler auto-connect are all off: a development player
    /// carries instrumentation overhead that would be measured as if it were the code's cost.
    /// </summary>
    public static class BuildBenchmark
    {
        public const string BuildPathArg = "-frameBudgetBuildPath";
        public const string AllowMonoArg = "-frameBudgetAllowMono";
        public const string DefaultBuildPath = "Builds/Windows64/FrameBudget.exe";

        /// <summary>Player variation folder that only exists when the Windows IL2CPP module is installed.</summary>
        private const string Il2CppVariation = "PlaybackEngines/windowsstandalonesupport/Variations/win64_player_nondevelopment_il2cpp";

        private const string InstallInstructions =
            "Windows Build Support (IL2CPP) is not installed for this editor.\n" +
            "  Install it:  Unity Hub -> Installs -> {0} -> gear icon -> Add modules -> tick \"Windows Build Support (IL2CPP)\" -> Install\n" +
            "  Expected to exist after installing: {1}\n" +
            "Refusing to fall back to Mono silently: Mono and IL2CPP timings are not comparable, and a\n" +
            "benchmark that quietly changes backend is worse than one that does not build.\n" +
            "To build on Mono deliberately, pass " + AllowMonoArg + "; the backend is recorded in every CSV row.";

        public static void Build()
        {
            try
            {
                string buildPath = CommandLine.GetString(BuildPathArg, DefaultBuildPath);
                bool allowMono = CommandLine.HasFlag(AllowMonoArg);

                ScriptingImplementation backend = ResolveBackend(allowMono, out string reason);
                Debug.Log("[BuildBenchmark] Scripting backend: " + backend + " (" + reason + ")");

                string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
                if (scenes.Length == 0) throw new InvalidOperationException("No enabled scenes in the build settings.");

                var named = NamedBuildTarget.Standalone;
                PlayerSettings.SetScriptingBackend(named, backend);
                if (backend == ScriptingImplementation.IL2CPP)
                {
                    PlayerSettings.SetIl2CppCompilerConfiguration(named, Il2CppCompilerConfiguration.Release);
                    PlayerSettings.SetIl2CppCodeGeneration(named, Il2CppCodeGeneration.OptimizeSpeed);
                }

                // The player must never pace itself: the benchmark measures how long a frame took, not
                // how long the display asked it to take. Runtime asserts this again before measuring.
                PlayerSettings.runInBackground = true;
                PlayerSettings.defaultIsNativeResolution = false;
                PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
                PlayerSettings.defaultScreenWidth = 1280;
                PlayerSettings.defaultScreenHeight = 720;
                PlayerSettings.resizableWindow = true;
                PlayerSettings.forceSingleInstance = false;
                PlayerSettings.visibleInBackground = true;

                // Instrumentation off. A development player is a different program from the one shipped.
                EditorUserBuildSettings.development = false;
                EditorUserBuildSettings.buildWithDeepProfilingSupport = false;
                EditorUserBuildSettings.connectProfiler = false;
                EditorUserBuildSettings.allowDebugging = false;
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);

                string fullPath = Path.GetFullPath(buildPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

                var options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = fullPath,
                    target = BuildTarget.StandaloneWindows64,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = BuildOptions.None,   // development off, deep profiling off, autoconnect off
                };

                Debug.Log("[BuildBenchmark] Building " + string.Join(", ", scenes) + " -> " + fullPath
                          + " | target=StandaloneWindows64 | backend=" + backend
                          + " | development=" + EditorUserBuildSettings.development
                          + " | deepProfiling=" + EditorUserBuildSettings.buildWithDeepProfilingSupport);

                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;
                Debug.Log("[BuildBenchmark] Result: " + summary.result
                          + " | errors=" + summary.totalErrors + " warnings=" + summary.totalWarnings
                          + " | size=" + (summary.totalSize / (1024 * 1024)) + " MB"
                          + " | time=" + summary.totalTime);

                if (summary.result != BuildResult.Succeeded)
                {
                    foreach (BuildStep step in report.steps)
                    {
                        foreach (BuildStepMessage message in step.messages)
                        {
                            if (message.type == LogType.Error || message.type == LogType.Exception)
                            {
                                Debug.LogError("[BuildBenchmark] " + step.name + ": " + message.content);
                            }
                        }
                    }
                    Fail("Build did not succeed: " + summary.result);
                    return;
                }

                Debug.Log("[BuildBenchmark] OK: " + fullPath);
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Fail("Build threw: " + e.Message);
            }
        }

        /// <summary>IL2CPP unless the module is missing; missing + no explicit opt-in is a hard stop.</summary>
        private static ScriptingImplementation ResolveBackend(bool allowMono, out string reason)
        {
            bool il2cppInstalled = IsIl2CppInstalled(out string expectedPath);
            if (il2cppInstalled)
            {
                if (allowMono)
                {
                    reason = "IL2CPP is installed but " + AllowMonoArg + " was passed; building Mono deliberately";
                    Debug.LogWarning("[BuildBenchmark] " + reason + ". These numbers are not comparable with IL2CPP numbers.");
                    return ScriptingImplementation.Mono2x;
                }
                reason = "Windows IL2CPP module present";
                return ScriptingImplementation.IL2CPP;
            }

            string message = string.Format(InstallInstructions, Application.unityVersion, expectedPath);
            if (!allowMono)
            {
                Debug.LogError("[BuildBenchmark] " + message);
                Fail("IL2CPP module missing.");
                throw new InvalidOperationException("IL2CPP module missing; build aborted.");
            }

            reason = "IL2CPP module missing and " + AllowMonoArg + " was passed";
            Debug.LogWarning("[BuildBenchmark] " + message + "\nProceeding on Mono because " + AllowMonoArg + " was passed.");
            return ScriptingImplementation.Mono2x;
        }

        private static bool IsIl2CppInstalled(out string expectedPath)
        {
            expectedPath = Path.Combine(EditorApplication.applicationContentsPath, Il2CppVariation).Replace('\\', '/');
            return Directory.Exists(expectedPath);
        }

        private static void Fail(string message)
        {
            Debug.LogError("[BuildBenchmark] FAILED: " + message);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}
