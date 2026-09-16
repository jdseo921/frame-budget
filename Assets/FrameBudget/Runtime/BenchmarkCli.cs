#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FrameBudget
{
    /// <summary>
    /// Command-line entry point for unattended benchmark runs from the editor:
    /// <code>
    ///   Unity.exe -batchmode -projectPath &lt;project&gt; -executeMethod FrameBudget.BenchmarkCli.Run
    ///             -frameBudgetConfig Benchmark10k -logFile &lt;log path&gt;
    /// </code>
    /// Do not pass -quit: the benchmark exits the editor itself once the CSV is written.
    /// Do not pass -nographics: rendering would stop and the draw-call counters would (honestly) read zero.
    /// Note that -batchmode itself has no Game view either, so nothing renders there; drop -batchmode
    /// (the editor opens, runs the benchmark, and exits by itself) for rendering-inclusive numbers.
    /// </summary>
    public static class BenchmarkCli
    {
        public const string ScenePath = "Assets/FrameBudget/Scenes/FrameBudget.unity";

        public static void Run()
        {
            Debug.Log("[FrameBudget] BenchmarkCli.Run: opening " + ScenePath + " and entering Play mode (batchmode=" + Application.isBatchMode + ").");
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            SessionState.SetBool(BenchmarkLaunch.SessionRequestKey, true);
            EditorApplication.EnterPlaymode();
        }
    }
}
#endif
