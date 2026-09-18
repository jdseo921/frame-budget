using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace FrameBudget
{
    /// <summary>
    /// Renders the README screenshot from the shipped player, so the image is produced by the build
    /// rather than captured by hand and can be regenerated when the numbers change.
    ///
    /// The whole path is inert unless <see cref="PathArg"/> is on the command line. Nothing here runs,
    /// allocates or is even constructed in a normal run: <see cref="FrameBudgetDriver"/> tests
    /// <see cref="Requested"/> once at start-up and only then adds the component. That matters because
    /// the published figures — 0.06 GC collections per frame among them — describe the shipped build,
    /// and a capture helper that cost a per-frame allocation would quietly invalidate them.
    ///
    /// Capture mode uses the BENCHMARK stepping rule, one fixed step per frame, not the interactive
    /// accumulator. In interactive mode a fast configuration runs no step on most frames, so the panel
    /// reads "0 step/frame" and the frame-time median describes frames that did no simulation work.
    /// That is not the number the README quotes.
    /// </summary>
    public static class PresentationCapture
    {
        public const string PathArg = "-frameBudgetCapture";
        public const string AgentsArg = "-frameBudgetAgents";
        public const string TechniquesArg = "-frameBudgetTechniques";
        public const string SettleFramesArg = "-frameBudgetSettleFrames";

        /// <summary>
        /// The rolling window the HUD reports its median and p95 over is
        /// <see cref="FrameMetrics.WindowSize"/> frames. Capturing before it is full reports a median
        /// over however many frames have arrived, which is a different statistic from the one in the
        /// README table. This is that window plus warm-up for the spawn and the first draws.
        /// </summary>
        public const int MinimumSettleFrames = FrameMetrics.WindowSize + 60;

        public const int DefaultSettleFrames = 240;

        /// <summary>True only when the capture flag carries an output path.</summary>
        public static bool Requested => !string.IsNullOrWhiteSpace(CommandLine.GetString(PathArg, null));

        public static string OutputPath => Path.GetFullPath(CommandLine.GetString(PathArg, "capture.png"));

        public static int SettleFrames
        {
            get
            {
                string raw = CommandLine.GetString(SettleFramesArg, null);
                if (raw == null) return DefaultSettleFrames;
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                {
                    Debug.LogWarning("[FrameBudget] " + SettleFramesArg + " is not an integer; using " + DefaultSettleFrames + ".");
                    return DefaultSettleFrames;
                }
                if (n < MinimumSettleFrames)
                {
                    Debug.LogWarning("[FrameBudget] " + SettleFramesArg + "=" + n + " would capture a rolling window that is still filling; raising it to "
                                     + MinimumSettleFrames + ".");
                    return MinimumSettleFrames;
                }
                return n;
            }
        }

        public static int AgentCount
        {
            get
            {
                string raw = CommandLine.GetString(AgentsArg, null);
                if (raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0) return n;
                if (raw != null) Debug.LogWarning("[FrameBudget] " + AgentsArg + " is not a positive integer; leaving the configured count.");
                return -1;
            }
        }

        /// <summary>Parses "spatialHash,zeroAlloc,gpuInstancing". Unknown names are refused rather than ignored.</summary>
        public static TechniqueCombination Techniques(out bool specified)
        {
            var t = new TechniqueCombination();
            string raw = CommandLine.GetString(TechniquesArg, null);
            specified = !string.IsNullOrWhiteSpace(raw);
            if (!specified) return t;

            foreach (string piece in raw.Split(','))
            {
                string name = piece.Trim();
                if (name.Length == 0) continue;
                if (string.Equals(name, "spatialHash", StringComparison.OrdinalIgnoreCase)) t.spatialHash = true;
                else if (string.Equals(name, "zeroAlloc", StringComparison.OrdinalIgnoreCase)) t.zeroAlloc = true;
                else if (string.Equals(name, "gpuInstancing", StringComparison.OrdinalIgnoreCase)) t.gpuInstancing = true;
                else if (string.Equals(name, "tickBudget", StringComparison.OrdinalIgnoreCase)) t.tickBudget = true;
                else if (string.Equals(name, "burstJobs", StringComparison.OrdinalIgnoreCase)) t.burstJobs = true;
                else Debug.LogError("[FrameBudget] Unknown technique '" + name + "' in " + TechniquesArg + "; it is not enabled.");
            }
            return t;
        }
    }

    /// <summary>
    /// Drives one capture and quits. Added by <see cref="FrameBudgetDriver"/> only when
    /// <see cref="PresentationCapture.Requested"/>, so it does not exist in a normal run.
    /// </summary>
    public sealed class PresentationCaptureBehaviour : MonoBehaviour
    {
        private FrameBudgetDriver driver;
        private int frame;
        private bool capturing;

        public void Bind(FrameBudgetDriver d)
        {
            driver = d;
        }

        private void Start()
        {
            int agents = PresentationCapture.AgentCount;
            TechniqueCombination techniques = PresentationCapture.Techniques(out bool specified);

            if (specified) driver.SetTechniques(techniques);
            driver.BeginPresentationCapture();
            if (agents > 0) driver.RequestAgentCount(agents);
            else driver.RequestRespawn();

            Debug.Log("[FrameBudget] Capture mode: agents=" + (agents > 0 ? agents.ToString(CultureInfo.InvariantCulture) : "configured")
                      + " techniques=" + (specified ? techniques.Label : "configured")
                      + " settleFrames=" + PresentationCapture.SettleFrames
                      + " output=" + PresentationCapture.OutputPath);
        }

        private void Update()
        {
            if (capturing) return;
            frame++;
            if (frame < PresentationCapture.SettleFrames) return;
            capturing = true;
            StartCoroutine(CaptureAtEndOfFrame());
        }

        /// <summary>
        /// Reads the back buffer after everything has drawn, IMGUI included.
        /// <c>ScreenCapture.CaptureScreenshot</c> is the obvious call and does not reliably contain
        /// OnGUI output across Unity versions and pipelines; an image of the agent field with no
        /// instrument panel is useless for this README. ReadPixels after
        /// <see cref="WaitForEndOfFrame"/> captures what is actually on screen.
        /// </summary>
        private IEnumerator CaptureAtEndOfFrame()
        {
            yield return new WaitForEndOfFrame();

            int w = Screen.width;
            int h = Screen.height;
            var texture = new Texture2D(w, h, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
            texture.Apply();

            byte[] png = texture.EncodeToPNG();
            UnityEngine.Object.Destroy(texture);

            string path = PresentationCapture.OutputPath;
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, png);

            FrameMetrics m = driver.Metrics;
            string figures = "agents=" + driver.AgentCount
                           + " techniques=" + driver.ActiveTechniques.Label
                           + " steps/frame=" + driver.StepsLastFrame
                           + " window=" + m.FrameMs.Count + "/" + FrameMetrics.WindowSize
                           + " frame_ms=" + F(m.FrameMs.Median)
                           + " step_ms=" + F(m.StepMs.Median)
                           + " draw_calls=" + (m.DrawCallsValid ? F0(m.DrawCalls.Median) : "n/a")
                           + " gc_collections_per_frame=" + (m.GcAllocatedValid ? F(m.CollectionsPerFrame.Median) : "n/a");

            Debug.Log("[FrameBudget] Capture written: " + path + " (" + w + "x" + h + ", " + png.Length + " bytes)");
            Debug.Log("[FrameBudget] Capture figures: " + figures);

            Application.Quit(0);
        }

        private static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
        private static string F0(double v) => v.ToString("F0", CultureInfo.InvariantCulture);
    }
}
