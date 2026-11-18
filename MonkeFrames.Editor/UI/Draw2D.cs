using System.Collections.Generic;
using UnityEngine;

namespace MonkeFrames.Editor.UI;

/// <summary>
/// Fast immediate-mode drawing for editors that need many primitives (timelines, graphs): lines, polylines,
/// plain rectangles and After Effects style keyframe icons. Everything is drawn only on Repaint and is tinted by
/// GUI.color, so it fades with the window.
/// </summary>
public static class Draw2D
{
    private static Texture2D _white;
    public static Texture2D White
    {
        get
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            return _white;
        }
    }

    private static bool Repaint => Event.current.type == EventType.Repaint;

    private static void Tint(Color c, out Color prev)
    {
        prev = GUI.color;
        GUI.color = new Color(c.r, c.g, c.b, c.a * prev.a);
    }

    /// <summary>A plain (square-cornered) filled rectangle. Much cheaper than Theme.Fill.</summary>
    public static void Rect(Rect r, Color c)
    {
        if (!Repaint || r.width <= 0f || r.height <= 0f) return;
        Tint(c, out Color prev);
        GUI.DrawTexture(r, White);
        GUI.color = prev;
    }

    /// <summary>Rectangle outline.</summary>
    public static void Frame(Rect r, Color c, float w = 1f)
    {
        Rect(new Rect(r.x, r.y, r.width, w), c);
        Rect(new Rect(r.x, r.yMax - w, r.width, w), c);
        Rect(new Rect(r.x, r.y, w, r.height), c);
        Rect(new Rect(r.xMax - w, r.y, w, r.height), c);
    }

    /// <summary>A straight line of the given width.</summary>
    public static void Line(Vector2 a, Vector2 b, Color c, float width = 1.5f)
    {
        if (!Repaint) return;
        Vector2 d = b - a;
        float len = d.magnitude;
        if (len < 0.01f) return;
        // axis-aligned lines don't need a rotated matrix
        if (Mathf.Abs(d.y) < 0.001f) { Rect(new Rect(Mathf.Min(a.x, b.x), a.y - width / 2f, len, width), c); return; }
        if (Mathf.Abs(d.x) < 0.001f) { Rect(new Rect(a.x - width / 2f, Mathf.Min(a.y, b.y), width, len), c); return; }
        Matrix4x4 m = GUI.matrix;
        float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        GUIUtility.RotateAroundPivot(angle, a);
        Tint(c, out Color prev);
        GUI.DrawTexture(new Rect(a.x, a.y - width / 2f, len, width), White);
        GUI.color = prev;
        GUI.matrix = m;
    }

    /// <summary>Connected line through points (NaN points break the line).</summary>
    public static void Polyline(List<Vector2> pts, Color c, float width = 1.5f)
    {
        if (!Repaint) return;
        for (int i = 1; i < pts.Count; i++)
        {
            Vector2 a = pts[i - 1], b = pts[i];
            if (float.IsNaN(a.y) || float.IsNaN(b.y)) continue;
            Line(a, b, c, width);
        }
    }

    /// <summary>Dashed line.</summary>
    public static void Dashed(Vector2 a, Vector2 b, Color c, float width = 1f, float dash = 4f, float gap = 3f)
    {
        float len = (b - a).magnitude;
        if (len < 0.01f) return;
        Vector2 dir = (b - a) / len;
        for (float s = 0; s < len; s += dash + gap)
            Line(a + dir * s, a + dir * Mathf.Min(len, s + dash), c, width);
    }

    // ---- Keyframe icons (After Effects style) ----

    /// <summary>Shapes of keyframe icon halves.</summary>
    public enum KeyShape
    {
        /// <summary>Linear: a diamond.</summary>
        Diamond,
        /// <summary>Eased (bezier): an hourglass.</summary>
        Hourglass,
        /// <summary>Auto bezier: a circle.</summary>
        Circle,
        /// <summary>Hold: a square.</summary>
        Square,
    }

    private static readonly Dictionary<KeyShape, Texture2D> _shapes = new();

    private static Texture2D Shape(KeyShape s)
    {
        if (_shapes.TryGetValue(s, out Texture2D t) && t != null) return t;
        const int n = 48;
        t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float a = 0f;
                // 4x supersampling for smooth edges
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                    {
                        float u = ((x + (sx + 0.5f) / 4f) / n) * 2f - 1f, v = ((y + (sy + 0.5f) / 4f) / n) * 2f - 1f;
                        bool inside = s switch
                        {
                            KeyShape.Diamond => Mathf.Abs(u) + Mathf.Abs(v) <= 0.98f,
                            KeyShape.Hourglass => Mathf.Abs(v) <= 0.9f && Mathf.Abs(u) <= 0.18f + 0.8f * Mathf.Abs(v),
                            KeyShape.Circle => u * u + v * v <= 0.86f,
                            _ => Mathf.Abs(u) <= 0.78f && Mathf.Abs(v) <= 0.78f,
                        };
                        if (inside) a += 1f / 16f;
                    }
                px[y * n + x] = new Color(1, 1, 1, a);
            }
        t.SetPixels(px);
        t.Apply();
        _shapes[s] = t;
        return t;
    }

    /// <summary>
    /// A keyframe icon: the left half shows how the value arrives (incoming interpolation), the right half how it
    /// leaves (outgoing), exactly like After Effects.
    /// </summary>
    public static void Key(Vector2 c, float size, KeyShape left, KeyShape right, Color fill, Color outline)
    {
        if (!Repaint) return;
        float h = size / 2f;
        float o = size * 0.18f;
        Tint(outline, out Color prev);
        GUI.DrawTextureWithTexCoords(new Rect(c.x - h - o, c.y - h - o, h + o, size + o * 2), Shape(left), new Rect(0, 0, 0.5f, 1));
        GUI.DrawTextureWithTexCoords(new Rect(c.x, c.y - h - o, h + o, size + o * 2), Shape(right), new Rect(0.5f, 0, 0.5f, 1));
        GUI.color = prev;
        Tint(fill, out prev);
        GUI.DrawTextureWithTexCoords(new Rect(c.x - h, c.y - h, h, size), Shape(left), new Rect(0, 0, 0.5f, 1));
        GUI.DrawTextureWithTexCoords(new Rect(c.x, c.y - h, h, size), Shape(right), new Rect(0.5f, 0, 0.5f, 1));
        GUI.color = prev;
    }

    /// <summary>A small filled square (graph points / handles).</summary>
    public static void Box(Vector2 c, float size, Color fill, Color outline)
    {
        Rect(new Rect(c.x - size / 2f - 1, c.y - size / 2f - 1, size + 2, size + 2), outline);
        Rect(new Rect(c.x - size / 2f, c.y - size / 2f, size, size), fill);
    }
}
