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

        public const string ClipDirArg = "-frameBudgetClip";
        public const string ClipFramesArg = "-frameBudgetClipFrames";
        public const string ClipToggleAtArg = "-frameBudgetClipToggleAt";
        public const string ClipIntervalArg = "-frameBudgetClipInterval";

        /// <summary>Six seconds at 10 fps.</summary>
        public const int DefaultClipFrames = 60;

        /// <summary>Two seconds of the before state, then four seconds after the switch.</summary>
        public const int DefaultClipToggleAt = 20;

        /// <summary>
        /// Capture every nth rendered frame. At roughly 5.6 ms a frame the app runs near 180 fps, so
        /// every 18th frame is a 10 fps clip at close to real-time speed and leaves about 94% of
        /// frames untouched by a readback. Those captured frames are kept out of the metric windows
        /// as well (see <see cref="FrameMetrics.DiscardNextSamples"/>), but keeping them a small
        /// minority is what stops the capture changing the behavior it is filming.
        /// </summary>
        public const int DefaultClipInterval = 18;

        public static bool ClipRequested => !string.IsNullOrWhiteSpace(CommandLine.GetString(ClipDirArg, null));

        public static string ClipDirectory => Path.GetFullPath(CommandLine.GetString(ClipDirArg, "clip"));

        public static int ClipFrames => PositiveOr(ClipFramesArg, DefaultClipFrames);

        public static int ClipToggleAt => PositiveOr(ClipToggleAtArg, DefaultClipToggleAt, allowZero: true);

        public static int ClipInterval => PositiveOr(ClipIntervalArg, DefaultClipInterval);

        private static int PositiveOr(string arg, int fallback, bool allowZero = false)
        {
            string raw = CommandLine.GetString(arg, null);
            if (raw == null) return fallback;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && (n > 0 || (allowZero && n == 0))) return n;
            Debug.LogWarning("[FrameBudget] " + arg + " is not a valid count; using " + fallback + ".");
            return fallback;
        }

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

    /// <summary>
    /// Writes the README's toggle clip as a numbered PNG sequence: 10,000 agents with spatialHash and
    /// zeroAlloc already on, gpuInstancing switched on part-way through.
    ///
    /// The switch does NOT respawn. Instancing is a change to how the same agents are submitted, so
    /// the field has to be visually identical either side of the cut - that is the whole point of the
    /// shot. Only the draw-call counter and the technique row move.
    ///
    /// Added by <see cref="FrameBudgetDriver"/> only when <see cref="PresentationCapture.ClipRequested"/>.
    /// </summary>
    public sealed class ClipCaptureBehaviour : MonoBehaviour
    {
        private FrameBudgetDriver driver;
        private int frame;
        private int captured;
        private bool busy;
        private bool toggled;
        private string directory;
        private int settle, interval, total, toggleAt;
        private Texture2D buffer;

        private void OnDestroy()
        {
            if (buffer != null) UnityEngine.Object.Destroy(buffer);
        }

        public void Bind(FrameBudgetDriver d)
        {
            driver = d;
        }

        private void Start()
        {
            directory = PresentationCapture.ClipDirectory;
            settle = PresentationCapture.SettleFrames;
            interval = PresentationCapture.ClipInterval;
            total = PresentationCapture.ClipFrames;
            toggleAt = PresentationCapture.ClipToggleAt;
            Directory.CreateDirectory(directory);

            int agents = PresentationCapture.AgentCount;
            driver.SetTechniques(new TechniqueCombination { spatialHash = true, zeroAlloc = true, gpuInstancing = false });
            driver.BeginPresentationCapture();
            if (agents > 0) driver.RequestAgentCount(agents);
            else driver.RequestRespawn();

            Debug.Log("[FrameBudget] Clip mode: agents=" + (agents > 0 ? agents.ToString(CultureInfo.InvariantCulture) : "configured")
                      + " frames=" + total + " toggleAt=" + toggleAt + " interval=" + interval
                      + " settleFrames=" + settle + " output=" + directory);
        }

        private void Update()
        {
            if (busy || captured >= total) return;
            frame++;
            if (frame < settle) return;
            if ((frame - settle) % interval != 0) return;
            busy = true;
            StartCoroutine(CaptureFrame());
        }

        private IEnumerator CaptureFrame()
        {
            yield return new WaitForEndOfFrame();

            int w = Screen.width;
            int h = Screen.height;
            // One texture for the whole clip. Allocating a fresh 1280x720 RGB24 buffer per captured
            // frame is 2.7 MB of churn sixty times over, and the collections that follow land on
            // frames after the capture, where they would show up in the panel being filmed.
            if (buffer == null || buffer.width != w || buffer.height != h)
            {
                if (buffer != null) UnityEngine.Object.Destroy(buffer);
                buffer = new Texture2D(w, h, TextureFormat.RGB24, false);
            }
            buffer.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
            buffer.Apply();
            byte[] png = buffer.EncodeToPNG();

            // The interval measured at the start of the next frame includes this readback, and the
            // disturbance outlives that one frame.
            driver.Metrics.DiscardNextSamples();

            string path = Path.Combine(directory, "frame_" + captured.ToString("D4", CultureInfo.InvariantCulture) + ".png");
            File.WriteAllBytes(path, png);

            FrameMetrics m = driver.Metrics;
            if (captured == 0 || captured == toggleAt || captured == total - 1)
            {
                Debug.Log("[FrameBudget] Clip frame " + captured + ": " + driver.ActiveTechniques.Label
                          + " frame_ms=" + m.FrameMs.Median.ToString("F2", CultureInfo.InvariantCulture)
                          + " draw_calls=" + (m.DrawCallsValid ? m.DrawCalls.Median.ToString("F0", CultureInfo.InvariantCulture) : "n/a")
                          + " window=" + m.FrameMs.Count + "/" + FrameMetrics.WindowSize);
            }

            captured++;

            // Switched one capture early so the nominated index is already showing the new state,
            // and without a respawn so the agents do not jump across the cut.
            if (!toggled && captured == toggleAt)
            {
                TechniqueCombination t = driver.ActiveTechniques;
                t.gpuInstancing = true;
                driver.SetTechniques(t);
                toggled = true;
                Debug.Log("[FrameBudget] Clip: gpuInstancing enabled before capture index " + toggleAt + " (no respawn).");
            }

            busy = false;

            if (captured >= total)
            {
                Debug.Log("[FrameBudget] Clip written: " + captured + " frames in " + directory);
                Application.Quit(0);
            }
        }
    }
}
