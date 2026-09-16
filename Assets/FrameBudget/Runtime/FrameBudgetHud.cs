using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FrameBudget
{
    /// <summary>
    /// IMGUI instrument panel. This is a measuring display, not a product: it has to stay legible in
    /// a 720p screen recording, so every size scales with Screen.height. The text is rebuilt every
    /// frame with string concatenation on purpose (see <see cref="BuildText"/>). The frame-time graph
    /// is drawn with GL in one batch so the HUD costs a fixed handful of draw calls, not one per bar;
    /// press H to hide the HUD and read its own overhead off the counters.
    /// </summary>
    public sealed class FrameBudgetHud : IDisposable
    {
        private const string NotAvailable = "n/a (counter did not resolve)";
        private const string Controls = "Up/Down +-100   PgUp/PgDn +-1000   R respawn   H hide HUD";

        private string text = "";
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

        /// <summary>Keeps the agent-count text field in step with counts changed by keyboard.</summary>
        public void NotifyAgentCount(int count)
        {
            agentCountField = count.ToString();
        }

        /// <summary>Rebuilds the HUD text. Called every frame from Update, in every mode, so the cost is always present and always measured.</summary>
        public void BuildText(FrameBudgetDriver d)
        {
            FrameMetrics m = d.Metrics;
            SimConfig c = d.Config;

            // CONTROL: the whole HUD string is rebuilt with '+' concatenation every frame. Every '+'
            //          and every ToString() below allocates a fresh string, so this shows up in
            //          "GC Allocated In Frame" even when the simulation is idle.
            //          Replaced by the zeroAlloc technique (cached strings, rebuilt only when a value changes).
            text = "FRAME BUDGET   " + c.TechniqueLabel + "   Unity " + Application.unityVersion + (Application.isEditor ? " (Editor)" : " (Player)") + "\n"
                 + "Agents      " + d.AgentCount + "     seed " + c.seed + "     step " + (c.fixedTimestep * 1000f).ToString("F2") + " ms\n"
                 + "Frame       " + FormatMs(m.FrameMs) + "     main thread " + (m.MainThreadValid ? FormatMs(m.MainThreadMs) : NotAvailable) + "\n"
                 + "Sim step    " + FormatMs(m.StepMs) + "     steps/frame " + d.StepsLastFrame + BehindRealTime(d) + "\n"
                 + "Present     " + FormatMs(m.PresentMs) + "     other (render+engine) " + FormatMs(m.OtherMs) + "\n"
                 + "GC alloc    " + (m.GcAllocatedValid ? FormatBytes(m.GcBytes) : NotAvailable) + "\n"
                 + "Draw calls  " + (m.DrawCallsValid ? FormatCount(m.DrawCalls) : NotAvailable) + "     SetPass " + (m.SetPassCallsValid ? FormatCount(m.SetPassCalls) : NotAvailable) + "\n"
                 + Controls;
        }

        private static string BehindRealTime(FrameBudgetDriver d)
        {
            if (d.StepCapHitFrames == 0) return "";
            return "     behind real time: " + d.DroppedSimulationSeconds.ToString("F1") + " s dropped over " + d.StepCapHitFrames + " capped frames";
        }

        private static string FormatMs(RollingWindow w)
        {
            if (w.Count == 0) return "--";
            return w.Median.ToString("F2") + " ms  p95 " + w.P95.ToString("F2");
        }

        private static string FormatCount(RollingWindow w)
        {
            if (w.Count == 0) return "--";
            return w.Median.ToString("F0") + "  p95 " + w.P95.ToString("F0");
        }

        private static string FormatBytes(RollingWindow w)
        {
            if (w.Count == 0) return "--";
            return FormatByteValue(w.Median) + "  p95 " + FormatByteValue(w.P95);
        }

        private static string FormatByteValue(double bytes)
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

            float pad = Mathf.Round(10f * s);
            float panelWidth = Mathf.Min(Screen.width - 2f * pad, Mathf.Round(900f * s));

            content.text = text;
            float textHeight = textStyle.CalcHeight(content, panelWidth - 2f * pad);
            var panel = new Rect(pad, pad, panelWidth, textHeight + 2f * pad);
            GUI.Box(panel, GUIContent.none, boxStyle);
            GUI.Label(new Rect(panel.x + pad, panel.y + pad, panel.width - 2f * pad, textHeight), text, textStyle);

            float y = panel.yMax + pad;
            {
                float h = Mathf.Round(40f * s);
                float x = pad;
                float fieldW = Mathf.Round(130f * s);
                float bw = Mathf.Round(120f * s);

                agentCountField = GUI.TextField(new Rect(x, y, fieldW, h), agentCountField, 7, fieldStyle);
                x += fieldW + pad;
                if (GUI.Button(new Rect(x, y, bw, h), "Apply", buttonStyle) && int.TryParse(agentCountField, out int requested))
                {
                    d.RequestAgentCount(requested);
                }
                x += bw + pad;
                if (GUI.Button(new Rect(x, y, bw, h), "-1000", buttonStyle)) Adjust(d, -1000);
                x += bw + pad;
                if (GUI.Button(new Rect(x, y, bw, h), "+1000", buttonStyle)) Adjust(d, +1000);
                x += bw + pad;
                if (GUI.Button(new Rect(x, y, bw, h), "Respawn", buttonStyle)) d.RequestRespawn();
                y += h + pad;
            }

            var graph = new Rect(pad, y, panelWidth, Mathf.Round(180f * s));
            DrawGraph(d.Metrics, graph, s);
        }

        private void Adjust(FrameBudgetDriver d, int delta)
        {
            int n = Mathf.Max(0, d.AgentCount + delta);
            agentCountField = n.ToString();
            d.RequestAgentCount(n);
        }

        private void DrawGraph(FrameMetrics m, Rect r, float s)
        {
            RollingWindow frames = m.FrameMs;
            RollingWindow sim = m.SimMs;
            int n = frames.Count;
            double budget = FrameMetrics.BudgetMs;

            // Vertical scale: at least two budgets tall, grows in whole budgets so the budget line
            // never leaves the graph, clipped at twenty budgets (bars above that are drawn full height).
            double maxValue = n > 0 ? frames.Max : 0.0;
            double yMax = Math.Max(2.0 * budget, Math.Ceiling(maxValue / budget) * budget);
            yMax = Math.Min(yMax, 20.0 * budget);

            // Bars live in the plot area; the caption gets its own strip underneath so it never sits on top of the data.
            float capH = Mathf.Round(20f * s);
            var plot = new Rect(r.xMin, r.yMin, r.width, r.height - capH - 6f * s);
            float budgetY = plot.yMax - (float)(budget / yMax) * plot.height;

            if (Event.current.type == EventType.Repaint)
            {
                graphMaterial.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, Screen.width, Screen.height, 0f);   // GUI space: origin top-left, y down
                GL.Begin(GL.QUADS);

                Quad(r.xMin, r.yMin, r.xMax, r.yMax, new Color(0f, 0f, 0f, 0.7f));

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

                Quad(plot.xMin, budgetY - 1f, plot.xMax, budgetY + 1f, new Color(1f, 0.85f, 0.1f, 1f));

                GL.End();
                GL.PopMatrix();
            }

            GUI.Label(new Rect(plot.xMin + 6f * s, budgetY - capH - 2f * s, 300f * s, capH), budget.ToString("F1") + " ms budget", captionStyle);
            GUI.Label(new Rect(plot.xMax - 160f * s, plot.yMin + 2f * s, 154f * s, capH), yMax.ToString("F0") + " ms", captionStyle);
            GUI.Label(new Rect(r.xMin + 6f * s, plot.yMax + 3f * s, r.width - 12f * s, capH),
                "frame ms, last " + FrameMetrics.WindowSize + " frames   (blue = simulation, red = over budget)", captionStyle);
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
            boxTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
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
        }
    }
}
