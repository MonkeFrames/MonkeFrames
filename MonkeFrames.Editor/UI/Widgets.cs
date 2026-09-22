using System.Collections.Generic;
using UnityEngine;

namespace MonkeFrames.Editor.UI;

/// <summary>
/// Custom animated controls built on top of IMGUI.
/// Every control takes a stable string id used to key its animation state.
/// </summary>
public static class Widgets
{
    private static bool IsHover(Rect r) => r.Contains(Event.current.mousePosition);

    /// <summary>A pill toggle switch with a sliding knob and colour fade.</summary>
    public static bool Switch(string id, Rect rect, bool value, string label, string tooltip = null)
    {
        Rect track = new Rect(rect.x, rect.y + (rect.height - 18) / 2f, 34, 18);
        Rect hit = new Rect(rect.x, rect.y, rect.width, rect.height);

        float t = Anim.To("sw.knob." + id, value ? 1f : 0f, 16f);
        float h = Anim.To("sw.hover." + id, IsHover(hit) ? 1f : 0f, 18f);

        Color off = Color.Lerp(Theme.Raised, Theme.Hover, h);
        Theme.Fill(track, Color.Lerp(off, Theme.Accent, t), 9);

        float knobX = Mathf.Lerp(track.x + 9, track.xMax - 9, Anim.OutCubic(t));
        Theme.Dot(new Vector2(knobX, track.center.y), 7f + h * 0.6f, Color.Lerp(new Color(0.8f, 0.82f, 0.88f), Color.white, t));

        if (!string.IsNullOrEmpty(label))
            GUI.Label(new Rect(track.xMax + 10, rect.y, rect.width - 44, rect.height), new GUIContent(label, tooltip), Theme.Label);

        if (GUI.Button(hit, new GUIContent("", tooltip), GUIStyle.none))
            value = !value;

        return value;
    }

    /// <summary>A segmented control with a highlight pill that glides to the selected option.</summary>
    public static int Segmented(string id, Rect rect, int selected, IList<string> options)
    {
        Theme.Fill(rect, Theme.BorderStrong, 7);
        Theme.Fill(new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2), Theme.Field, 6);

        float segW = rect.width / options.Count;
        float pos = Anim.To("seg." + id, selected, 16f);

        if (selected >= 0)
        {
            Rect pill = new Rect(rect.x + 2 + pos * segW, rect.y + 2, segW - 4, rect.height - 4);
            Theme.Fill(pill, Theme.Accent, 5);
        }

        for (int i = 0; i < options.Count; i++)
        {
            Rect r = new Rect(rect.x + i * segW, rect.y, segW, rect.height);
            float closeness = 1f - Mathf.Clamp01(Mathf.Abs(pos - i));
            float hover = Anim.To($"seg.{id}.h{i}", IsHover(r) && i != selected ? 1f : 0f, 18f);

            if (hover > 0.01f)
                Theme.Fill(new Rect(r.x + 2, r.y + 2, r.width - 4, r.height - 4), new Color(1, 1, 1, 0.06f * hover), 5);

            Color text = Color.Lerp(Color.Lerp(Theme.TextMuted, Theme.Text, hover), Theme.OnAccent, closeness);
            Theme.DrawText(r, options[i], Theme.LabelCenter, text);

            if (GUI.Button(r, GUIContent.none, GUIStyle.none))
                selected = i;
        }

        return selected;
    }

    /// <summary>
    /// Grid of options (several rows of a segmented control). The highlight glides in 2D
    /// to the selected cell. Returns the selected index.
    /// </summary>
    public static int ChipGrid(string id, Rect rect, int selected, IList<string> options, int columns, float rowHeight = 28f, float gap = 4f)
    {
        int rows = Mathf.CeilToInt(options.Count / (float)columns);
        float cellW = (rect.width - gap * (columns - 1)) / columns;

        Rect CellRect(int i) => new Rect(rect.x + (i % columns) * (cellW + gap), rect.y + (i / columns) * (rowHeight + gap), cellW, rowHeight);

        // Cell backgrounds
        for (int i = 0; i < options.Count; i++)
            Theme.Fill(CellRect(i), Theme.Field, 6);

        // Gliding highlight
        if (selected >= 0)
        {
            Rect target = CellRect(selected);
            float hx = Anim.To($"grid.{id}.x", target.x, 16f);
            float hy = Anim.To($"grid.{id}.y", target.y, 16f);
            Theme.Fill(new Rect(hx, hy, cellW, rowHeight), Theme.Accent, 6);
        }

        for (int i = 0; i < options.Count; i++)
        {
            Rect r = CellRect(i);
            float hover = Anim.To($"grid.{id}.h{i}", IsHover(r) && i != selected ? 1f : 0f, 18f);
            if (hover > 0.01f)
                Theme.Fill(r, new Color(1, 1, 1, 0.08f * hover), 6);

            Color text = i == selected ? Theme.OnAccent : Color.Lerp(Theme.TextMuted, Theme.Text, hover);
            Theme.DrawText(r, options[i], Theme.LabelCenter, text);

            if (GUI.Button(r, GUIContent.none, GUIStyle.none))
                selected = i;
        }

        return selected;
    }

    /// <summary>A slider with an accent-coloured fill that trails the thumb.</summary>
    public static float Slider(string id, Rect rect, float value, float min, float max)
    {
        rect = new Rect(rect.x, rect.center.y - 10, rect.width, 20);

        float t = Mathf.InverseLerp(min, max, value);
        float shown = Anim.To("sl." + id, t, 22f);

        const float thumb = 16f;
        float fillW = (rect.width - thumb) * shown + thumb / 2f;
        Theme.Fill(new Rect(rect.x, rect.center.y - 2.5f, fillW, 5), Theme.Accent, 2);

        return GUI.HorizontalSlider(rect, value, min, max);
    }

    /// <summary>Row of colour swatches with an animated selection ring. Returns the chosen index.</summary>
    public static int Swatches(string id, Rect rect, int selected, IList<Color> colors, float size = 26f)
    {
        float gap = 10f;
        for (int i = 0; i < colors.Count; i++)
        {
            Rect r = new Rect(rect.x + i * (size + gap), rect.y + (rect.height - size) / 2f, size, size);
            Vector2 c = r.center;

            float sel = Anim.To($"swatch.{id}.{i}", i == selected ? 1f : 0f, 14f);
            float hov = Anim.To($"swatch.{id}.h{i}", IsHover(r) ? 1f : 0f, 18f);

            float radius = size / 2f - 2f + hov * 1.5f - sel * 2.5f;
            if (sel > 0.01f)
            {
                Theme.Dot(c, size / 2f + 1.5f * sel, Color.white.WithAlpha(0.9f * sel));
                Theme.Dot(c, size / 2f - 0.5f * sel, Theme.Background.WithAlpha(1f));
            }
            Theme.Dot(c, radius, colors[i]);

            if (GUI.Button(r, GUIContent.none, GUIStyle.none))
                selected = i;
        }

        return selected;
    }

    /// <summary>A small rotating dots spinner.</summary>
    public static void Spinner(Vector2 center, float radius, Color color)
    {
        const int dots = 8;
        float time = Time.unscaledTime * 1.4f;
        for (int i = 0; i < dots; i++)
        {
            float a = i / (float)dots * Mathf.PI * 2f;
            float phase = Mathf.Repeat(time - i / (float)dots, 1f);
            Vector2 p = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            Theme.Dot(p, 1.2f + 1.3f * (1f - phase), color.WithAlpha(0.25f + 0.75f * (1f - phase)));
        }
    }

    /// <summary>Flat button with an animated hover background. Used for menu bar / list rows.</summary>
    public static bool Ghost(string id, Rect rect, string text, GUIStyle textStyle, bool active = false, int radius = 5)
    {
        float h = Anim.To("ghost." + id, IsHover(rect) || active ? 1f : 0f, 18f);
        if (h > 0.01f)
            Theme.Fill(rect, (active ? Theme.Accent.WithAlpha(0.22f) : new Color(1, 1, 1, 0.07f)) * new Color(1, 1, 1, h), radius);

        Theme.DrawText(rect, text, textStyle, Color.Lerp(Theme.TextMuted, Theme.Text, Mathf.Max(h, active ? 1 : 0)));
        return GUI.Button(rect, GUIContent.none, GUIStyle.none);
    }

    private static readonly Dictionary<string, string> EditBuffers = new();

    /// <summary>
    /// Text field that edits a float, with a coloured axis tag. While focused it keeps the raw
    /// text so partial input like "1." or "-" can be typed without being reformatted.
    /// </summary>
    public static float FloatField(string id, Rect rect, string axis, Color axisColor, float value)
    {
        Rect tag = new Rect(rect.x, rect.y + 2, 18, rect.height - 4);
        Theme.Fill(tag, axisColor.WithAlpha(0.18f), 4);
        Theme.DrawText(tag, axis, Theme.LabelCenter, axisColor);

        string name = "mf.float." + id;
        bool focused = GUI.GetNameOfFocusedControl() == name;

        string text = focused && EditBuffers.TryGetValue(name, out string buf) ? buf : value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        GUI.SetNextControlName(name);
        string edited = GUI.TextField(new Rect(rect.x + 22, rect.y, rect.width - 22, rect.height), text);

        if (focused)
            EditBuffers[name] = edited;
        else
            EditBuffers.Remove(name);

        if (edited == text)
            return value;

        return float.TryParse(edited, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : value;
    }

    /// <summary>Thin horizontal divider.</summary>
    public static void Divider(float x, float y, float width) =>
        Theme.Fill(new Rect(x, y, width, 1), Theme.Border, 0);
}
