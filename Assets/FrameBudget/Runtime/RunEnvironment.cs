using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace FrameBudget
{
    /// <summary>
    /// Everything about the machine and the build that a number depends on, captured from the running
    /// process rather than typed into a document. A CSV row carries all of it, so a row can be read
    /// years later, on another machine, without this repository's documentation: the reader can see
    /// which CPU and GPU produced it, which scripting backend and render pipeline were compiled in,
    /// and whether it came from a player or the editor.
    ///
    /// The scripting backend matters more than it looks: Mono and IL2CPP differ by a large factor on
    /// the tight numeric loops this benchmark spends its time in, so rows from the two backends must
    /// never be compared, and a row that does not say which one it used cannot be trusted at all.
    /// </summary>
    public static class RunEnvironment
    {
        public const string CsvHeader =
            "unity_version,scripting_backend,render_pipeline,build_type,is_batchmode,is_development_build," +
            "platform,operating_system,device_model,cpu,cpu_cores,cpu_frequency_mhz,system_memory_mb," +
            "gpu,gpu_api,gpu_memory_mb,screen";

        public static string ScriptingBackend
        {
            get
            {
#if ENABLE_IL2CPP
                return "IL2CPP";
#elif ENABLE_MONO
                return "Mono";
#else
                return "unknown";
#endif
            }
        }

        /// <summary>"Built-in" when no scriptable pipeline asset is active, otherwise the asset's type name.</summary>
        public static string RenderPipeline
        {
            get
            {
                RenderPipelineAsset asset = QualitySettings.renderPipeline != null
                    ? QualitySettings.renderPipeline
                    : GraphicsSettings.defaultRenderPipeline;
                return asset == null ? "Built-in" : asset.GetType().Name;
            }
        }

        public static string BuildType => Application.isEditor ? "Editor" : "Player";

        /// <summary>The comma-separated values matching <see cref="CsvHeader"/>. Constant for a process, so callers cache it.</summary>
        public static string CsvRow()
        {
            var sb = new StringBuilder(384);
            sb.Append(Q(Application.unityVersion)).Append(',')
              .Append(Q(ScriptingBackend)).Append(',')
              .Append(Q(RenderPipeline)).Append(',')
              .Append(Q(BuildType)).Append(',')
              .Append(Application.isBatchMode ? 1 : 0).Append(',')
              .Append(Debug.isDebugBuild ? 1 : 0).Append(',')
              .Append(Q(Application.platform.ToString())).Append(',')
              .Append(Q(SystemInfo.operatingSystem)).Append(',')
              .Append(Q(SystemInfo.deviceModel)).Append(',')
              .Append(Q(SystemInfo.processorType)).Append(',')
              .Append(SystemInfo.processorCount).Append(',')
              .Append(SystemInfo.processorFrequency).Append(',')
              .Append(SystemInfo.systemMemorySize).Append(',')
              .Append(Q(SystemInfo.graphicsDeviceName)).Append(',')
              .Append(Q(SystemInfo.graphicsDeviceType.ToString())).Append(',')
              .Append(SystemInfo.graphicsMemorySize).Append(',')
              .Append(Q(Screen.width + "x" + Screen.height));
            return sb.ToString();
        }

        /// <summary>One line for the log, so the run's own record says where it ran.</summary>
        public static string Describe()
        {
            return "Unity " + Application.unityVersion + " · " + ScriptingBackend + " · " + RenderPipeline + " RP · " + BuildType
                   + (Application.isBatchMode ? " · batchmode" : "")
                   + (Debug.isDebugBuild ? " · DEVELOPMENT BUILD" : "")
                   + " · " + SystemInfo.processorType + " (" + SystemInfo.processorCount + " threads @ " + SystemInfo.processorFrequency + " MHz)"
                   + " · " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + ")"
                   + " · " + SystemInfo.systemMemorySize + " MB RAM"
                   + " · " + Screen.width + "x" + Screen.height
                   + " · " + SystemInfo.operatingSystem;
        }

        private static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    }
}
