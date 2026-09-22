using MonkeFrames.Compiler;
using UnityEngine;

namespace MonkeFrames.Editor.UI;

/// <summary>
/// Interactive speed-curve editor: a cubic bezier timing curve with two draggable handles.
/// Shows progress over time (accent) and the resulting speed (white), with a dot that
/// plays the motion on a loop.
/// </summary>
public static class CurveEditor
{
    public const float MinY = -0.5f;
    public const float MaxY = 1.5f;

    private static int _dragging = -1;   // 0 = handle 1, 1 = handle 2
    private static int _dragControl;

    public static readonly (string name, Vector4 curve)[] Presets =
    [
        ("Linear",    new Vector4(0.00f, 0.00f, 1.00f, 1.00f)),
        ("Ease",      new Vector4(0.25f, 0.10f, 0.25f, 1.00f)),
        ("In",        new Vector4(0.55f, 0.00f, 1.00f, 0.45f)),
        ("Out",       new Vector4(0.00f, 0.55f, 0.45f, 1.00f)),
        ("In-Out",    new Vector4(0.65f, 0.00f, 0.35f, 1.00f)),
        ("Snappy",    new Vector4(0.85f, 0.00f, 0.15f, 1.00f)),
        ("Overshoot", new Vector4(0.34f, 1.40f, 0.64f, 1.00f)),
        ("Wind-up",   new Vector4(0.36f, -0.35f, 0.66f, 0.95f)),
    ];

    /// <summary>
    /// Draw the editor. When <paramref name="editable"/> is false the handles are hidden and
    /// <paramref name="preview"/> (a preset easing) is plotted instead.
    /// Returns true if the curve was changed this event.
    /// </summary>
    public static bool Draw(string id, Rect rect, ref Vector4 curve, bool editable, System.Func<float, float> preview = null)
    {
        Theme.Fill(rect, Theme.Field, 8);

        // Plot area, with room for overshoot above/below.
        Rect g = new Rect(rect.x + 12, rect.y + 20, rect.width - 24, rect.height - 34);
        Vector2 ToScreen(float x, float y) => new Vector2(g.x + g.width * x, g.yMax - g.height * Mathf.InverseLerp(MinY, MaxY, y));
        Vector2 ToCurve(Vector2 p) => new Vector2(Mathf.Clamp01((p.x - g.x) / g.width), Mathf.Lerp(MinY, MaxY, (g.yMax - p.y) / g.height));

        // Grid: 0 and 1 lines, quarter verticals, faint linear reference.
        Vector2 o = ToScreen(0, 0), one = ToScreen(1, 1);
        Theme.Fill(new Rect(g.x, o.y, g.width, 1), Theme.BorderStrong, 0);
        Theme.Fill(new Rect(g.x, one.y, g.width, 1), Theme.BorderStrong, 0);
        for (int i = 1; i < 4; i++)
            Theme.Fill(new Rect(g.x + g.width * i / 4f, g.y, 1, g.height), Theme.Border, 0);
        for (int s = 0; s <= 24; s++)
        {
            float t = s / 24f;
            Theme.Dot(ToScreen(t, t), 0.9f, new Color(1, 1, 1, 0.12f));
        }

        Vector4 cv = curve;
        float Eval(float t) => editable || preview == null ? Easing.Bezier(cv.x, cv.y, cv.z, cv.w, t) : preview(t);

        // Speed (derivative), normalised to the plot height, drawn behind the progress curve.
        const int samples = 64;
        float maxSpeed = 0.0001f;
        float[] speeds = new float[samples + 1];
        for (int s = 0; s <= samples; s++)
        {
            float t = s / (float)samples;
            float a = Eval(Mathf.Max(0, t - 0.01f)), b = Eval(Mathf.Min(1, t + 0.01f));
            speeds[s] = Mathf.Abs(b - a) / (Mathf.Min(1, t + 0.01f) - Mathf.Max(0, t - 0.01f));
            maxSpeed = Mathf.Max(maxSpeed, speeds[s]);
        }
        for (int s = 0; s <= samples; s++)
        {
            float t = s / (float)samples;
            float h = speeds[s] / maxSpeed * (o.y - one.y) * 0.9f;
            Theme.Fill(new Rect(g.x + g.width * t - 1.2f, o.y - h, 2.4f, h), new Color(1, 1, 1, 0.08f), 0);
            Theme.Dot(new Vector2(g.x + g.width * t, o.y - h), 1.1f, new Color(1, 1, 1, 0.55f));
        }

        // Progress curve
        for (int s = 0; s <= samples * 2; s++)
        {
            float t = s / (float)(samples * 2);
            Theme.Dot(ToScreen(t, Eval(t)), 1.6f, Theme.Accent);
        }

        // Legend
        Theme.Dot(new Vector2(rect.x + 14, rect.y + 10), 3f, Theme.Accent);
        Theme.DrawText(new Rect(rect.x + 20, rect.y + 1, 70, 18), "progress", Theme.MutedSmall, Theme.Text);
        Theme.Dot(new Vector2(rect.x + 82, rect.y + 10), 3f, Color.white.WithAlpha(0.7f));
        Theme.DrawText(new Rect(rect.x + 88, rect.y + 1, 60, 18), "speed", Theme.MutedSmall, Theme.Text);
        Theme.DrawText(new Rect(rect.x, rect.yMax - 16, rect.width - 8, 14), "time", Theme.MutedRight, Theme.TextMuted);

        bool changed = false;

        if (editable)
        {
            Vector2 p0 = ToScreen(0, 0), p3 = ToScreen(1, 1);

            // Handles glide smoothly when a preset is picked.
            Vector2 h1 = ToScreen(Anim.To(id + ".x1", curve.x, 22f), Anim.To(id + ".y1", curve.y, 22f));
            Vector2 h2 = ToScreen(Anim.To(id + ".x2", curve.z, 22f), Anim.To(id + ".y2", curve.w, 22f));

            DrawLine(p0, h1, Color.white.WithAlpha(0.45f));
            DrawLine(p3, h2, Color.white.WithAlpha(0.45f));

            int control = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;
            Vector2 mouse = e.mousePosition;

            for (int i = 0; i < 2; i++)
            {
                Vector2 h = i == 0 ? h1 : h2;
                bool hover = (mouse - h).sqrMagnitude < 14 * 14 || _dragging == i && GUIUtility.hotControl == _dragControl;
                float ha = Anim.To($"{id}.h{i}", hover ? 1f : 0f, 20f);
                Theme.Dot(h, 9f + 3f * ha, Theme.Accent.WithAlpha(0.25f + 0.2f * ha));
                Theme.Dot(h, 6f, Color.white);
                Theme.Dot(h, 3.5f, Theme.Accent);
            }

            switch (e.GetTypeForControl(control))
            {
                case EventType.MouseDown when e.button == 0:
                    float d1 = (mouse - h1).sqrMagnitude, d2 = (mouse - h2).sqrMagnitude;
                    if (Mathf.Min(d1, d2) < 16 * 16)
                    {
                        _dragging = d1 <= d2 ? 0 : 1;
                        _dragControl = control;
                        GUIUtility.hotControl = control;
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag when GUIUtility.hotControl == control && _dragging >= 0:
                    Vector2 c = ToCurve(mouse);
                    c.y = Mathf.Clamp(c.y, MinY, MaxY);
                    c.x = Mathf.Round(c.x * 100f) / 100f;
                    c.y = Mathf.Round(c.y * 100f) / 100f;
                    if (_dragging == 0) { curve.x = c.x; curve.y = c.y; Anim.Set(id + ".x1", c.x); Anim.Set(id + ".y1", c.y); }
                    else { curve.z = c.x; curve.w = c.y; Anim.Set(id + ".x2", c.x); Anim.Set(id + ".y2", c.y); }
                    changed = true;
                    e.Use();
                    break;

                case EventType.MouseUp when GUIUtility.hotControl == control:
                    GUIUtility.hotControl = 0;
                    _dragging = -1;
                    e.Use();
                    break;
            }

            Theme.DrawText(new Rect(rect.x + 8, rect.yMax - 16, rect.width - 60, 14),
                $"({curve.x:0.00}, {curve.y:0.00})  ({curve.z:0.00}, {curve.w:0.00})", Theme.MutedSmall, Theme.TextMuted);
        }

        // Playhead dot running along the curve on a loop (with a short pause at the end).
        float pt = Mathf.Clamp01(Mathf.Repeat(Time.unscaledTime * 0.55f, 1.3f));
        Vector2 dot = ToScreen(pt, Eval(pt));
        Theme.Dot(dot, 7f, Theme.Accent.WithAlpha(0.3f));
        Theme.Dot(dot, 4.5f, Color.white);

        return changed;
    }

    /// <summary>Row of preset chips. Returns the chosen preset index, or -1.</summary>
    public static int PresetRow(string id, Rect rect, Vector4 current)
    {
        int chosen = -1;
        float gap = 4f;
        float w = (rect.width - gap * (Presets.Length - 1)) / Presets.Length;

        for (int i = 0; i < Presets.Length; i++)
        {
            Rect r = new Rect(rect.x + i * (w + gap), rect.y, w, rect.height);
            bool active = (Presets[i].curve - current).sqrMagnitude < 0.0001f;
            if (Widgets.Ghost($"{id}.p{i}", r, Presets[i].name, Theme.LabelCenterSmall, active, 6))
                chosen = i;
        }

        return chosen;
    }

    private static void DrawLine(Vector2 a, Vector2 b, Color color)
    {
        int steps = Mathf.Max(2, Mathf.CeilToInt((b - a).magnitude / 4f));
        for (int i = 0; i <= steps; i++)
            Theme.Dot(Vector2.Lerp(a, b, i / (float)steps), 0.9f, color);
    }
}
