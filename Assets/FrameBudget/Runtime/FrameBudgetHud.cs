// FEATURE-FROZEN. This HUD is the instrument the demo video films. Further layout work is out of
// scope for the week; change it only if a measurement is unreadable on camera, and name that
// measurement in the commit message.
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FrameBudget
{
    /// <summary>
    /// IMGUI instrument panel, drawn in an opaque column on the left of the screen: a median / p95
    /// table, the controls, and a 120-frame frame-time graph with the budget line. The column is
    /// sized to its own text and reported through <see cref="ColumnWidth"/> so the world camera can
    /// be kept to the right of it; the two never overlap. Everything scales with Screen.height so it
    /// reads in a 720p recording. Graph labels live in their own gutter and caption strip, so bars can
    /// never cover them. The text is rebuilt every frame with string concatenation on purpose (see
    /// <see cref="BuildText"/>). The graph is drawn with GL in one batch, so the HUD costs a fixed
    /// handful of draw calls; press H to hide it and read its own overhead off the counters.
    /// </summary>
    public sealed class FrameBudgetHud : IDisposable
    {
        private const int LabelWidth = 14;
        private const int ColumnChars = 10;
        private const string NotResolved = "n/a - counter did not resolve";
        private const string Controls = "1/2/3 spatialHash · zeroAlloc · gpuInstancing\nUp/Dn +-100 · PgUp/PgDn +-1000 · R respawn\nB benchmark · H hide HUD";

        private static readonly Func<double, string> MsFormat = v => v.ToString("F2") + " ms";
        private static readonly Func<double, string> CountFormat = v => v.ToString("F0");
        private static readonly Func<double, string> BytesFormat = FormatBytes;
        private static readonly Func<double, string> CollectionsFormat = v => v.ToString("F2");

        private static readonly Color ColumnBackground = new Color(0.04f, 0.04f, 0.05f, 1f);
        private static readonly Color PanelBackground = new Color(0.10f, 0.10f, 0.12f, 1f);
        private static readonly Color Separator = new Color(0.35f, 0.35f, 0.40f, 1f);

        private string text = "";

        /// <summary>Reused by the zeroAlloc path so that building the panel does not allocate.</summary>
        private readonly System.Text.StringBuilder builder = new System.Text.StringBuilder(1024);

        private string agentCountField = "0";
        private readonly GUIContent content = new GUIContent();

        private bool stylesReady;
        private GUIStyle textStyle;
        private GUIStyle captionStyle;
        private GUIStyle buttonStyle;
        private GUIStyle fieldStyle;
        private GUIStyle boxStyle;
        private Texture2D boxTexture;
        private Font monoFont;
        private Material graphMaterial;

        /// <summary>Pixels reserved on the left of the screen by the last <see cref="Draw"/>; 0 when the HUD is not being drawn.</summary>
        public float ColumnWidth { get; private set; }

        /// <summary>Keeps the agent-count text field in step with counts changed by keyboard or benchmark.</summary>
        public void NotifyAgentCount(int count)
        {
            agentCountField = count.ToString();
        }

        /// <summary>Tell the HUD it is not being drawn this frame, so it stops reserving screen space.</summary>
        public void NotifyHidden()
        {
            ColumnWidth = 0f;
        }

        /// <summary>Rebuilds the HUD text. Called every frame from Update, in every mode, so the cost is always present and always measured.</summary>
        public void BuildText(FrameBudgetDriver d)
        {
            if (d.ActiveTechniques.zeroAlloc)
            {
                BuildTextZeroAlloc(d);
                return;
            }

            FrameMetrics m = d.Metrics;
            SimConfig c = d.Config;

            // CONTROL: the whole HUD string is rebuilt with '+' concatenation every frame. Every '+',
            //          every ToString() and every Pad below allocates a fresh string, so this shows up
            //          in "GC Allocated In Frame" even when the simulation is idle.
            //          Replaced by the zeroAlloc technique (cached strings, rebuilt only when a value changes).
            text = "FRAME BUDGET · " + c.TechniqueLabel + " · Unity " + Application.unityVersion + (Application.isEditor ? " (Editor)" : " (Player)") + "\n"
                 + d.AgentCount + " agents · seed " + c.seed + " · dt " + (c.fixedTimestep * 1000f).ToString("F2") + " ms · " + d.StepsLastFrame + " step/frame\n"
                 + RealTime(d) + "\n"
                 + "\n"
                 + "".PadRight(LabelWidth) + "median".PadLeft(ColumnChars) + "p95".PadLeft(ColumnChars) + "\n"
                 + Row("Frame", m.FrameMs, true, MsFormat)
                 + Row("Main thread", m.MainThreadMs, m.MainThreadValid, MsFormat)
                 + Row("Sim step", m.StepMs, true, MsFormat)
                 + Row("Present", m.PresentMs, true, MsFormat)
                 + Row("Render+engine", m.OtherMs, true, MsFormat)
                 + Row("GC collect/fr", m.CollectionsPerFrame, m.GcAllocatedValid, CollectionsFormat)
                 + Row("GC bytes/fr", m.GcBytes, m.GcAllocatedValid, BytesFormat)
                 + Row("Draw calls", m.DrawCalls, m.DrawCallsValid, CountFormat)
                 + Row("SetPass", m.SetPassCalls, m.SetPassCallsValid, CountFormat)
                 + "\n"
                 + (d.Benchmark.IsRunning || d.Benchmark.IsFinished ? d.Benchmark.Status : Controls);
        }

        /// <summary>
        /// The same panel built without allocating: digits are written into a reused StringBuilder a
        /// character at a time, and the finished text is only turned into a string when it differs
        /// from the one already on screen. A frame in which no displayed value changed therefore
        /// allocates nothing at all.
        ///
        /// One string per changed frame remains, because IMGUI takes a string and there is no way to
        /// hand it a builder. That is roughly a kilobyte where the naive path produced dozens of
        /// temporaries, and the measurement in results/ shows what it comes to.
        /// </summary>
        private void BuildTextZeroAlloc(FrameBudgetDriver d)
        {
            FrameMetrics m = d.Metrics;
            SimConfig c = d.Config;

            builder.Length = 0;
            builder.Append("FRAME BUDGET · ").Append(d.ActiveTechniques.Label)
                   .Append(" · Unity ").Append(Application.unityVersion)
                   .Append(Application.isEditor ? " (Editor)" : " (Player)").Append('\n');

            ZeroAllocText.AppendInt(builder, d.AgentCount);
            builder.Append(" agents · seed ");
            ZeroAllocText.AppendInt(builder, c.seed);
            builder.Append(" · dt ");
            ZeroAllocText.AppendFixed(builder, c.fixedTimestep * 1000f, 2);
            builder.Append(" ms · ");
            ZeroAllocText.AppendInt(builder, d.StepsLastFrame);
            builder.Append(" step/frame\n");

            if (d.StepCapHitFrames == 0)
            {
                builder.Append("real time: keeping up\n\n");
            }
            else
            {
                builder.Append("real time: behind ");
                ZeroAllocText.AppendFixed(builder, d.DroppedSimulationSeconds, 1);
                builder.Append(" s (");
                ZeroAllocText.AppendInt(builder, d.StepCapHitFrames);
                builder.Append(" capped frames)\n\n");
            }

            ZeroAllocText.AppendPadRight(builder, "", LabelWidth);
            int mark = builder.Length;
            builder.Append("median");
            ZeroAllocText.RightAlignSince(builder, mark, ColumnChars);
            mark = builder.Length;
            builder.Append("p95");
            ZeroAllocText.RightAlignSince(builder, mark, ColumnChars);
            builder.Append('\n');

            AppendRow("Frame", m.FrameMs, true, Unit.Milliseconds);
            AppendRow("Main thread", m.MainThreadMs, m.MainThreadValid, Unit.Milliseconds);
            AppendRow("Sim step", m.StepMs, true, Unit.Milliseconds);
            AppendRow("Present", m.PresentMs, true, Unit.Milliseconds);
            AppendRow("Render+engine", m.OtherMs, true, Unit.Milliseconds);
            AppendRow("GC collect/fr", m.CollectionsPerFrame, m.GcAllocatedValid, Unit.Collections);
            AppendRow("GC bytes/fr", m.GcBytes, m.GcAllocatedValid, Unit.Bytes);
            AppendRow("Draw calls", m.DrawCalls, m.DrawCallsValid, Unit.Count);
            AppendRow("SetPass", m.SetPassCalls, m.SetPassCallsValid, Unit.Count);

            builder.Append('\n');
            builder.Append(d.Benchmark.IsRunning || d.Benchmark.IsFinished ? d.Benchmark.Status : Controls);

            // Only materialise a string when the panel actually changed.
            if (!ZeroAllocText.ContentEquals(builder, text)) text = builder.ToString();
        }

        private enum Unit { Milliseconds, Bytes, Count, Collections }

        private void AppendRow(string label, RollingWindow window, bool valid, Unit unit)
        {
            ZeroAllocText.AppendPadRight(builder, label, LabelWidth);
            if (!valid)
            {
                builder.Append(NotResolved).Append('\n');
                return;
            }
            AppendCell(window.Count == 0 ? double.NaN : window.Median, unit);
            AppendCell(window.Count == 0 ? double.NaN : window.P95, unit);
            builder.Append('\n');
        }

        private void AppendCell(double value, Unit unit)
        {
            int mark = builder.Length;
            if (double.IsNaN(value))
            {
                builder.Append("--");
            }
            else
            {
                switch (unit)
                {
                    case Unit.Milliseconds:
                        ZeroAllocText.AppendFixed(builder, value, 2);
                        builder.Append(" ms");
                        break;
                    case Unit.Collections:
                        ZeroAllocText.AppendFixed(builder, value, 2);
                        break;
                    case Unit.Bytes:
                        if (value >= 1048576.0) { ZeroAllocText.AppendFixed(builder, value / 1048576.0, 2); builder.Append(" MB"); }
                        else if (value >= 1024.0) { ZeroAllocText.AppendFixed(builder, value / 1024.0, 1); builder.Append(" KB"); }
                        else { ZeroAllocText.AppendFixed(builder, value, 0); builder.Append(" B"); }
                        break;
                    default:
                        ZeroAllocText.AppendFixed(builder, value, 0);
                        break;
                }
            }
            ZeroAllocText.RightAlignSince(builder, mark, ColumnChars);
        }

        private static string RealTime(FrameBudgetDriver d)
        {
            if (d.StepCapHitFrames == 0) return "real time: keeping up";
            return "real time: behind " + d.DroppedSimulationSeconds.ToString("F1") + " s (" + d.StepCapHitFrames + " capped frames)";
        }

        private static string Row(string label, RollingWindow w, bool valid, Func<double, string> format)
        {
            string padded = label.PadRight(LabelWidth);
            if (!valid) return padded + NotResolved + "\n";
            if (w.Count == 0) return padded + "--".PadLeft(ColumnChars) + "--".PadLeft(ColumnChars) + "\n";
            return padded + format(w.Median).PadLeft(ColumnChars) + format(w.P95).PadLeft(ColumnChars) + "\n";
        }

        private static string FormatBytes(double bytes)
        {
            if (bytes >= 1048576.0) return (bytes / 1048576.0).ToString("F2") + " MB";
            if (bytes >= 1024.0) return (bytes / 1024.0).ToString("F1") + " KB";
            return bytes.ToString("F0") + " B";
        }

        /// <summary>Call from OnGUI.</summary>
        public void Draw(FrameBudgetDriver d)
        {
            EnsureStyles();

            float s = Screen.height / 720f;    // 1.0 at 720p, 1.5 at 1080p
            int fontSize = Mathf.Max(12, Mathf.RoundToInt(19f * s));
            textStyle.fontSize = fontSize;
            buttonStyle.fontSize = fontSize;
            fieldStyle.fontSize = fontSize;
            captionStyle.fontSize = Mathf.Max(10, Mathf.RoundToInt(15f * s));

            // The column is as wide as its widest line of text needs, within limits, so the world keeps the rest.
            float pad = Mathf.Round(10f * s);
            content.text = text;
            textStyle.wordWrap = false;
            float textWidth = textStyle.CalcSize(content).x;
            textStyle.wordWrap = true;
            float panelWidth = Mathf.Clamp(textWidth + 2f * pad, Mathf.Round(480f * s), Mathf.Round(Screen.width * 0.55f));
            ColumnWidth = panelWidth + 2f * pad;

            if (Event.current.type == EventType.Repaint)
            {
                graphMaterial.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, Screen.width, Screen.height, 0f);   // GUI space: origin top-left, y down
                GL.Begin(GL.QUADS);
                Quad(0f, 0f, ColumnWidth, Screen.height, ColumnBackground);
                Quad(ColumnWidth - Mathf.Max(1f, s), 0f, ColumnWidth, Screen.height, Separator);
                GL.End();
                GL.PopMatrix();
            }

            float textHeight = textStyle.CalcHeight(content, panelWidth - 2f * pad);
            var panel = new Rect(pad, pad, panelWidth, textHeight + 2f * pad);
            GUI.Box(panel, GUIContent.none, boxStyle);
            GUI.Label(new Rect(panel.x + pad, panel.y + pad, panel.width - 2f * pad, textHeight), text, textStyle);

            float y = panel.yMax + pad;
            if (!d.Benchmark.IsRunning)
            {
                float h = Mathf.Round(40f * s);
                float w = Mathf.Round((panelWidth - 3f * pad) / 4f);
                float x = pad;

                agentCountField = GUI.TextField(new Rect(x, y, w, h), agentCountField, 7, fieldStyle);
                x += w + pad;
                if (GUI.Button(new Rect(x, y, w, h), "Apply", buttonStyle) && int.TryParse(agentCountField, out int requested))
                {
                    d.RequestAgentCount(requested);
                }
                x += w + pad;
                if (GUI.Button(new Rect(x, y, w, h), "Respawn", buttonStyle)) d.RequestRespawn();
                x += w + pad;
                if (GUI.Button(new Rect(x, y, w, h), "Benchmark", buttonStyle)) d.StartBenchmark();
                y += h + pad;
            }

            DrawGraph(d.Metrics, new Rect(pad, y, panelWidth, Mathf.Round(170f * s)), s);
        }

        private void DrawGraph(FrameMetrics m, Rect r, float s)
        {
            RollingWindow frames = m.FrameMs;
            RollingWindow sim = m.SimMs;
            int n = frames.Count;
            double budget = FrameMetrics.BudgetMs;

            // Vertical scale: at least two budgets tall, grows in whole budgets so the budget line
            // never leaves the plot, clipped at twenty budgets (taller bars are drawn full height).
            double maxValue = n > 0 ? frames.Max : 0.0;
            double yMax = Math.Max(2.0 * budget, Math.Ceiling(maxValue / budget) * budget);
            yMax = Math.Min(yMax, 20.0 * budget);

            // Bars are confined to the plot. Axis labels live in a gutter on the right and the caption
            // in a strip underneath, so no bar can ever paint over a label.
            float capH = Mathf.Round(20f * s);
            float gutter = Mathf.Round(110f * s);
            var plot = new Rect(r.xMin, r.yMin, r.width - gutter, r.height - capH - 6f * s);
            float budgetY = plot.yMax - (float)(budget / yMax) * plot.height;

            if (Event.current.type == EventType.Repaint)
            {
                graphMaterial.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, Screen.width, Screen.height, 0f);
                GL.Begin(GL.QUADS);

                Quad(r.xMin, r.yMin, r.xMax, r.yMax, PanelBackground);
                Quad(plot.xMin, plot.yMin, plot.xMax, plot.yMax, Color.black);

                float barW = plot.width / FrameMetrics.WindowSize;
                float barInner = Mathf.Max(1f, barW - Mathf.Max(1f, s));
                var over = new Color(0.95f, 0.25f, 0.2f, 1f);
                var under = new Color(0.2f, 0.85f, 0.35f, 1f);
                var simColor = new Color(0.3f, 0.55f, 1f, 1f);
                for (int i = 0; i < n; i++)
                {
                    double v = frames[i];
                    float x0 = plot.xMin + i * barW;
                    float x1 = x0 + barInner;
                    float h = (float)Math.Min(v / yMax, 1.0) * plot.height;
                    Quad(x0, plot.yMax - h, x1, plot.yMax, v > budget ? over : under);
                    if (i < sim.Count)
                    {
                        float hs = (float)Math.Min(sim[i] / yMax, 1.0) * plot.height;
                        Quad(x0, plot.yMax - hs, x1, plot.yMax, simColor);
                    }
                }

                // Budget line across the plot, with a short tick into the gutter pointing at its label.
                Quad(plot.xMin, budgetY - 1f, plot.xMax + 5f * s, budgetY + 1f, new Color(1f, 0.85f, 0.1f, 1f));

                GL.End();
                GL.PopMatrix();
            }

            float labelX = plot.xMax + 8f * s;
            float labelW = gutter - 8f * s;
            float budgetLabelY = Mathf.Clamp(budgetY - capH * 0.5f, plot.yMin + capH, plot.yMax - 2f * capH);
            GUI.Label(new Rect(labelX, plot.yMin, labelW, capH), yMax.ToString("F0") + " ms", captionStyle);
            GUI.Label(new Rect(labelX, budgetLabelY, labelW, capH), budget.ToString("F1") + " budget", captionStyle);
            GUI.Label(new Rect(labelX, plot.yMax - capH, labelW, capH), "0", captionStyle);
            GUI.Label(new Rect(r.xMin + 6f * s, plot.yMax + 3f * s, r.width - 12f * s, capH),
                "frame ms · last " + FrameMetrics.WindowSize + " frames · blue = simulation · red = over budget", captionStyle);
        }

        private static void Quad(float x0, float y0, float x1, float y1, Color color)
        {
            GL.Color(color);
            GL.Vertex3(x0, y0, 0f);
            GL.Vertex3(x1, y0, 0f);
            GL.Vertex3(x1, y1, 0f);
            GL.Vertex3(x0, y1, 0f);
        }

        private void EnsureStyles()
        {
            if (stylesReady) return;

            boxTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            boxTexture.SetPixel(0, 0, PanelBackground);
            boxTexture.Apply();

            monoFont = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Menlo", "DejaVu Sans Mono", "Courier New" }, 19);
            if (monoFont != null) monoFont.hideFlags = HideFlags.HideAndDontSave;

            boxStyle = new GUIStyle(GUI.skin.box);
            boxStyle.normal.background = boxTexture;
            boxStyle.border = new RectOffset(0, 0, 0, 0);

            textStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, wordWrap = true, richText = false };
            if (monoFont != null) textStyle.font = monoFont;
            textStyle.normal.textColor = Color.white;

            captionStyle = new GUIStyle(textStyle) { wordWrap = false };
            captionStyle.normal.textColor = new Color(1f, 0.85f, 0.1f);

            buttonStyle = new GUIStyle(GUI.skin.button);
            fieldStyle = new GUIStyle(GUI.skin.textField);
            if (monoFont != null) fieldStyle.font = monoFont;

            var shader = Shader.Find("Hidden/Internal-Colored");
            graphMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            graphMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            graphMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            graphMaterial.SetInt("_Cull", (int)CullMode.Off);
            graphMaterial.SetInt("_ZWrite", 0);
            graphMaterial.SetInt("_ZTest", (int)CompareFunction.Always);

            stylesReady = true;
        }

        public void Dispose()
        {
            if (boxTexture != null) UnityEngine.Object.Destroy(boxTexture);
            if (graphMaterial != null) UnityEngine.Object.Destroy(graphMaterial);
            if (monoFont != null) UnityEngine.Object.Destroy(monoFont);
            boxTexture = null;
            graphMaterial = null;
            monoFont = null;
            stylesReady = false;
            ColumnWidth = 0f;
        }
    }
}
