using System.Collections.Generic;
using MonkeFrames.Editor.Classes;
using UnityEngine;

namespace MonkeFrames.Editor.UI;

/// <summary>
/// MonkeFrames' custom dark theme. Every texture is generated procedurally at runtime
/// (anti-aliased rounded rectangles, soft shadows, circles) so no extra assets are needed.
/// The accent colour follows Settings.AccentColor and the skin rebuilds when it changes.
/// </summary>
public static class Theme
{
    // ---- Palette ----
    public static readonly Color Background   = new(0.070f, 0.078f, 0.098f, 0.97f);
    public static readonly Color Surface      = new(0.105f, 0.115f, 0.142f, 1f);
    public static readonly Color Raised       = new(0.150f, 0.162f, 0.198f, 1f);
    public static readonly Color Hover        = new(0.195f, 0.210f, 0.258f, 1f);
    public static readonly Color Field        = new(0.055f, 0.062f, 0.078f, 1f);
    public static readonly Color Border       = new(1f, 1f, 1f, 0.07f);
    public static readonly Color BorderStrong = new(1f, 1f, 1f, 0.14f);
    public static readonly Color Text         = new(1f, 1f, 1f, 1f);
    public static readonly Color TextMuted    = new(0.800f, 0.820f, 0.870f, 1f);
    public static readonly Color Danger       = new(0.930f, 0.330f, 0.360f, 1f);
    public static readonly Color AxisX        = new(0.960f, 0.380f, 0.400f, 1f);
    public static readonly Color AxisY        = new(0.450f, 0.850f, 0.450f, 1f);
    public static readonly Color AxisZ        = new(0.400f, 0.620f, 1.000f, 1f);

    public const float TitleHeight = 30f;

    /// <summary>The user's accent colour, softened slightly so pure primaries look good on dark.</summary>
    public static Color Accent
    {
        get
        {
            Color raw = Settings.current?.AccentColor ?? new Color(0.35f, 0.5f, 1f);
            Color c = Color.Lerp(raw, Color.white, 0.22f);
            c.a = 1f;
            return c;
        }
    }

    /// <summary>Black or white, whichever reads better on top of the accent colour.</summary>
    public static Color OnAccent
    {
        get
        {
            Color a = Accent;
            float lum = 0.2126f * a.r + 0.7152f * a.g + 0.0722f * a.b;
            return lum > 0.6f ? new Color(0.06f, 0.06f, 0.08f) : Color.white;
        }
    }

    public static Color WithAlpha(this Color c, float a) { c.a = a; return c; }

    // ---- Skin ----
    private static GUISkin _skin;
    private static Color _skinAccent;

    public static GUISkin Skin
    {
        get
        {
            if (_skin == null || _skinAccent != Accent)
                BuildSkin();
            return _skin;
        }
    }

    public static GUIStyle WindowStyle { get; private set; }
    public static GUIStyle PopupStyle { get; private set; }
    public static GUIStyle Title { get; private set; }
    public static GUIStyle Header { get; private set; }
    public static GUIStyle Label { get; private set; }
    public static GUIStyle LabelCenter { get; private set; }
    public static GUIStyle LabelRight { get; private set; }
    public static GUIStyle Muted { get; private set; }
    public static GUIStyle MutedSmall { get; private set; }
    public static GUIStyle MutedRight { get; private set; }
    public static GUIStyle MutedCenter { get; private set; }
    public static GUIStyle MutedWrap { get; private set; }
    public static GUIStyle LabelCenterSmall { get; private set; }
    public static GUIStyle AccentButton { get; private set; }
    public static GUIStyle DangerButton { get; private set; }
    public static GUIStyle Big { get; private set; }

    private static void BuildSkin()
    {
        _skinAccent = Accent;
        Color accent = _skinAccent;

        GUISkin baseSkin = GUI.skin;
        GUISkin skin = Object.Instantiate(baseSkin);
        skin.hideFlags = HideFlags.HideAndDontSave;

        // Window (rounded body + soft drop shadow drawn through 'overflow')
        WindowStyle = new GUIStyle(GUIStyle.none)
        {
            normal = { background = ShadowPanel(10, 14, Background, Border) },
            border = Uniform(14 + 10 + 2),
            overflow = Uniform(14),
            padding = new RectOffset(0, 0, 0, 0),
        };
        WindowStyle.onNormal.background = WindowStyle.normal.background;
        skin.window = WindowStyle;

        PopupStyle = new GUIStyle(GUIStyle.none)
        {
            normal = { background = ShadowPanel(8, 10, new Color(0.085f, 0.094f, 0.118f, 0.99f), BorderStrong) },
            border = Uniform(10 + 8 + 2),
            overflow = Uniform(10),
        };
        PopupStyle.onNormal.background = PopupStyle.normal.background;

        skin.box = new GUIStyle(baseSkin.box)
        {
            normal = { background = Rounded(8, Surface, Border), textColor = Text },
            border = Uniform(10),
            padding = Uniform(8),
        };

        // Buttons
        GUIStyle button = new GUIStyle(baseSkin.button)
        {
            fontSize = 14,
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(10, 10, 4, 4),
            margin = Uniform(3),
            border = Uniform(8),
            overflow = new RectOffset(0, 0, 0, 0),
        };
        SetState(button.normal, Rounded(6, Raised, Border), Text);
        SetState(button.hover, Rounded(6, Hover, BorderStrong), Color.white);
        SetState(button.active, Rounded(6, Color.Lerp(Hover, accent, 0.45f), accent.WithAlpha(0.6f)), Color.white);
        SetState(button.focused, button.normal.background, Text);
        SetState(button.onNormal, Rounded(6, accent, accent), OnAccent);
        SetState(button.onHover, Rounded(6, Color.Lerp(accent, Color.white, 0.12f), accent), OnAccent);
        SetState(button.onActive, Rounded(6, Color.Lerp(accent, Color.black, 0.15f), accent), OnAccent);
        SetState(button.onFocused, button.onNormal.background, OnAccent);
        skin.button = button;

        AccentButton = new GUIStyle(button) { fontStyle = FontStyle.Bold };
        SetState(AccentButton.normal, Rounded(6, accent, accent), OnAccent);
        SetState(AccentButton.hover, Rounded(6, Color.Lerp(accent, Color.white, 0.15f), accent), OnAccent);
        SetState(AccentButton.active, Rounded(6, Color.Lerp(accent, Color.black, 0.2f), accent), OnAccent);
        SetState(AccentButton.focused, AccentButton.normal.background, OnAccent);

        DangerButton = new GUIStyle(button);
        SetState(DangerButton.hover, Rounded(6, Color.Lerp(Hover, Danger, 0.35f), Danger.WithAlpha(0.6f)), Color.white);
        SetState(DangerButton.active, Rounded(6, Danger, Danger), Color.white);

        // Labels
        GUIStyle label = new GUIStyle(baseSkin.label)
        {
            fontSize = 14,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(2, 2, 1, 1),
            wordWrap = false,
            clipping = TextClipping.Clip,
        };
        SetText(label, Text);
        skin.label = label;
        Label = label;

        LabelCenter = new GUIStyle(label) { alignment = TextAnchor.MiddleCenter };
        LabelRight = new GUIStyle(label) { alignment = TextAnchor.MiddleRight };
        Title = new GUIStyle(label) { fontStyle = FontStyle.Bold, fontSize = 14 };
        Header = new GUIStyle(label) { fontStyle = FontStyle.Bold, fontSize = 12 };
        SetText(Header, Color.Lerp(accent, Color.white, 0.55f));
        LabelCenterSmall = new GUIStyle(label) { alignment = TextAnchor.MiddleCenter, fontSize = 12 };
        Big = new GUIStyle(label) { fontStyle = FontStyle.Bold, fontSize = 24 };

        Muted = new GUIStyle(label);
        SetText(Muted, TextMuted);
        MutedSmall = new GUIStyle(Muted) { fontSize = 12 };
        MutedRight = new GUIStyle(Muted) { alignment = TextAnchor.MiddleRight };
        MutedCenter = new GUIStyle(Muted) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
        MutedWrap = new GUIStyle(Muted) { alignment = TextAnchor.UpperLeft, wordWrap = true, fontSize = 13 };

        // Text fields
        GUIStyle field = new GUIStyle(baseSkin.textField)
        {
            fontSize = 14,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(8, 8, 2, 2),
            margin = Uniform(3),
            border = Uniform(8),
        };
        SetState(field.normal, Rounded(6, Field, Border), Text);
        SetState(field.hover, Rounded(6, Field, BorderStrong), Text);
        SetState(field.active, Rounded(6, Field, accent), Color.white);
        SetState(field.focused, Rounded(6, Field, accent), Color.white);
        SetState(field.onNormal, field.normal.background, Text);
        SetState(field.onFocused, field.focused.background, Color.white);
        skin.textField = field;
        skin.textArea = new GUIStyle(field) { alignment = TextAnchor.UpperLeft, wordWrap = true };

        // Toggle (checkbox): box on the left, label text on the right
        GUIStyle toggle = new GUIStyle(baseSkin.toggle)
        {
            fontSize = 14,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(26, 4, 2, 2),
            margin = Uniform(3),
            border = new RectOffset(20, 0, 0, 0),
            overflow = new RectOffset(0, 0, 0, 0),
            imagePosition = ImagePosition.TextOnly,
        };
        Texture2D boxOff = CheckBox(false, Raised, BorderStrong, accent);
        Texture2D boxOffHover = CheckBox(false, Hover, BorderStrong, accent);
        Texture2D boxOn = CheckBox(true, accent, accent, OnAccent);
        SetState(toggle.normal, boxOff, Text);
        SetState(toggle.hover, boxOffHover, Color.white);
        SetState(toggle.active, boxOffHover, Color.white);
        SetState(toggle.focused, boxOff, Text);
        SetState(toggle.onNormal, boxOn, Text);
        SetState(toggle.onHover, boxOn, Color.white);
        SetState(toggle.onActive, boxOn, Color.white);
        SetState(toggle.onFocused, boxOn, Text);
        skin.toggle = toggle;

        // Slider: thin track, round thumb. Track drawn centred in a 20px rect.
        skin.horizontalSlider = new GUIStyle(GUIStyle.none)
        {
            normal = { background = Track(Hover) },
            border = new RectOffset(6, 6, 0, 0),
            padding = new RectOffset(0, 0, 2, 2),
            margin = new RectOffset(4, 4, 2, 2),
            fixedHeight = 20,
        };
        skin.horizontalSliderThumb = new GUIStyle(GUIStyle.none)
        {
            normal = { background = Circle(32, accent, Color.white.WithAlpha(0.9f), 3f) },
            hover = { background = Circle(32, Color.Lerp(accent, Color.white, 0.2f), Color.white, 3f) },
            active = { background = Circle(32, Color.white, accent, 5f) },
            fixedWidth = 16,
            fixedHeight = 16,
        };

        // Scrollbars: slim, no arrow buttons
        GUIStyle vTrack = new GUIStyle(GUIStyle.none)
        {
            normal = { background = Rounded(2, new Color(1, 1, 1, 0.035f), Color.clear) },
            border = Uniform(4),
            fixedWidth = 8,
            margin = new RectOffset(4, 1, 2, 2),
        };
        GUIStyle vThumb = new GUIStyle(GUIStyle.none)
        {
            normal = { background = Rounded(2, new Color(1, 1, 1, 0.20f), Color.clear) },
            hover = { background = Rounded(2, new Color(1, 1, 1, 0.32f), Color.clear) },
            active = { background = Rounded(2, accent.WithAlpha(0.8f), Color.clear) },
            border = Uniform(4),
            fixedWidth = 8,
        };
        skin.verticalScrollbar = vTrack;
        skin.verticalScrollbarThumb = vThumb;
        skin.verticalScrollbarUpButton = GUIStyle.none;
        skin.verticalScrollbarDownButton = GUIStyle.none;

        skin.horizontalScrollbar = new GUIStyle(vTrack) { fixedWidth = 0, fixedHeight = 8, margin = new RectOffset(2, 2, 4, 1) };
        skin.horizontalScrollbarThumb = new GUIStyle(vThumb) { fixedWidth = 0, fixedHeight = 8 };
        skin.horizontalScrollbarLeftButton = GUIStyle.none;
        skin.horizontalScrollbarRightButton = GUIStyle.none;
        skin.scrollView = new GUIStyle(GUIStyle.none);

        skin.settings.cursorColor = accent;
        skin.settings.selectionColor = accent.WithAlpha(0.35f);
        skin.settings.doubleClickSelectsWord = true;

        _skin = skin;
    }

    // ---- Immediate drawing helpers (tinted by GUI.color, so they fade with windows) ----

    private static readonly Dictionary<int, GUIStyle> RoundStyles = new();

    /// <summary>Draw a filled, anti-aliased rounded rectangle.</summary>
    public static void Fill(Rect r, Color color, int radius = 6)
    {
        if (Event.current.type != EventType.Repaint || r.width <= 0 || r.height <= 0)
            return;

        radius = Mathf.Max(0, Mathf.Min(radius, Mathf.FloorToInt(Mathf.Min(r.width, r.height) / 2f) - 2));

        if (!RoundStyles.TryGetValue(radius, out GUIStyle style))
        {
            style = new GUIStyle(GUIStyle.none)
            {
                normal = { background = Rounded(radius, Color.white, Color.clear) },
                border = Uniform(radius + 2),
            };
            RoundStyles[radius] = style;
        }

        Color prevColor = GUI.color;
        Color prevBg = GUI.backgroundColor;
        GUI.color = new Color(color.r, color.g, color.b, color.a * prevColor.a);
        GUI.backgroundColor = Color.white;
        style.Draw(r, false, false, false, false);
        GUI.color = prevColor;
        GUI.backgroundColor = prevBg;
    }

    private static Texture2D _circle;

    /// <summary>Draw a filled circle.</summary>
    public static void Dot(Vector2 center, float radius, Color color)
    {
        if (Event.current.type != EventType.Repaint)
            return;

        _circle ??= Circle(64, Color.white, Color.clear, 0f);
        Color prev = GUI.color;
        GUI.color = new Color(color.r, color.g, color.b, color.a * prev.a);
        GUI.DrawTexture(new Rect(center.x - radius, center.y - radius, radius * 2, radius * 2), _circle);
        GUI.color = prev;
    }

    /// <summary>Draw a text label in a specific colour (respects the current fade alpha).</summary>
    public static void DrawText(Rect r, string text, GUIStyle style, Color color)
    {
        Color prev = GUI.contentColor;
        GUI.contentColor = color;
        GUI.Label(r, text, style);
        GUI.contentColor = prev;
    }

    // ---- Texture generation ----

    private static readonly List<Texture2D> Generated = new();

    private static RectOffset Uniform(int v) => new RectOffset(v, v, v, v);

    private static void SetState(GUIStyleState state, Texture2D bg, Color text)
    {
        state.background = bg;
        state.textColor = text;
    }

    private static void SetText(GUIStyle s, Color c)
    {
        s.normal.textColor = s.hover.textColor = s.active.textColor = s.focused.textColor = c;
        s.onNormal.textColor = s.onHover.textColor = s.onActive.textColor = s.onFocused.textColor = c;
        s.normal.background = s.hover.background = s.active.background = s.focused.background = null;
    }

    private static Texture2D NewTexture(int w, int h)
    {
        Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };
        Generated.Add(t);
        return t;
    }

    /// <summary>Signed distance from p to a rounded box centred at the origin.</summary>
    private static float SdRoundBox(Vector2 p, Vector2 halfSize, float r)
    {
        Vector2 q = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) - halfSize + new Vector2(r, r);
        Vector2 qm = new Vector2(Mathf.Max(q.x, 0), Mathf.Max(q.y, 0));
        return qm.magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - r;
    }

    private static Color Over(Color top, Color bottom)
    {
        float a = top.a + bottom.a * (1 - top.a);
        if (a <= 0.0001f) return Color.clear;
        Color c = (top * top.a + bottom * bottom.a * (1 - top.a)) / a;
        c.a = a;
        return c;
    }

    /// <summary>Rounded rectangle with a 1px inner border. Use with border = radius + 2.</summary>
    public static Texture2D Rounded(int radius, Color fill, Color border)
    {
        int size = radius * 2 + 6;
        Texture2D t = NewTexture(size, size);
        Vector2 half = new Vector2(size / 2f, size / 2f);
        Color[] px = new Color[size * size];

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            Vector2 p = new Vector2(x + 0.5f, y + 0.5f) - half;
            float d = SdRoundBox(p, half, radius);
            float body = Mathf.Clamp01(0.5f - d);
            float ring = Mathf.Clamp01(0.5f - d) * Mathf.Clamp01(d + 1.5f);

            Color c = fill;
            c.a *= body;
            if (border.a > 0)
                c = Over(new Color(border.r, border.g, border.b, border.a * ring), c);

            px[y * size + x] = c;
        }

        t.SetPixels(px);
        t.Apply();
        return t;
    }

    /// <summary>Rounded panel surrounded by a soft drop shadow. Use with overflow = shadow, border = shadow + radius + 2.</summary>
    private static Texture2D ShadowPanel(int radius, int shadow, Color fill, Color border)
    {
        int body = radius * 2 + 6;
        int size = body + shadow * 2;
        Texture2D t = NewTexture(size, size);
        Vector2 center = new Vector2(size / 2f, size / 2f);
        Vector2 half = new Vector2(body / 2f, body / 2f);
        Color[] px = new Color[size * size];
        float sigma = shadow / 2.4f;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            Vector2 p = new Vector2(x + 0.5f, y + 0.5f) - center;

            // Shadow is offset downward (texture y is flipped: lower y = bottom of the rect).
            float ds = SdRoundBox(p + new Vector2(0, 3f), half, radius);
            float sh = 0.55f * Mathf.Exp(-Mathf.Pow(Mathf.Max(ds, 0f), 2f) / (2f * sigma * sigma));
            if (ds < 0) sh = 0.55f;

            float d = SdRoundBox(p, half, radius);
            float bodyA = Mathf.Clamp01(0.5f - d);
            float ring = Mathf.Clamp01(0.5f - d) * Mathf.Clamp01(d + 1.5f);

            Color c = new Color(0, 0, 0, sh);
            Color f = fill; f.a *= bodyA;
            c = Over(f, c);
            if (border.a > 0)
                c = Over(new Color(border.r, border.g, border.b, border.a * ring), c);

            px[y * size + x] = c;
        }

        t.SetPixels(px);
        t.Apply();
        return t;
    }

    /// <summary>Filled circle with optional ring.</summary>
    private static Texture2D Circle(int size, Color fill, Color ring, float ringWidth)
    {
        Texture2D t = NewTexture(size, size);
        Color[] px = new Color[size * size];
        float r = size / 2f - 1f;
        Vector2 c0 = new Vector2(size / 2f, size / 2f);
        float aa = size / 20f;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = (new Vector2(x + 0.5f, y + 0.5f) - c0).magnitude - r;
            float a = Mathf.Clamp01(0.5f - d / aa);
            Color c = fill;
            if (ringWidth > 0 && d > -ringWidth * (size / 16f))
                c = Color.Lerp(fill, ring, Mathf.Clamp01((d + ringWidth * (size / 16f)) / aa));
            c.a *= a;
            px[y * size + x] = c;
        }

        t.SetPixels(px);
        t.Apply();
        return t;
    }

    /// <summary>12x20 track texture: a 4px rounded bar centred vertically, transparent above/below.</summary>
    private static Texture2D Track(Color color)
    {
        const int w = 12, h = 20;
        Texture2D t = NewTexture(w, h);
        Color[] px = new Color[w * h];
        Vector2 half = new Vector2(w / 2f, 2.5f);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            Vector2 p = new Vector2(x + 0.5f - w / 2f, y + 0.5f - h / 2f);
            float d = SdRoundBox(p, half, 2.5f);
            Color c = color;
            c.a *= Mathf.Clamp01(0.5f - d);
            px[y * w + x] = c;
        }

        t.SetPixels(px);
        t.Apply();
        return t;
    }

    /// <summary>20x20 checkbox: 16px rounded box at the left, optional check mark.</summary>
    private static Texture2D CheckBox(bool on, Color fill, Color border, Color check)
    {
        const int s = 20;
        Texture2D t = NewTexture(s, s);
        Color[] px = new Color[s * s];
        Vector2 center = new Vector2(8f, 10f);
        Vector2 half = new Vector2(8f, 8f);

        // Check mark as two line segments (in texture space, y up).
        Vector2 a = new Vector2(4.2f, 10.2f), b = new Vector2(7f, 7.2f), c = new Vector2(12f, 13f);

        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
            float d = SdRoundBox(p - center, half, 4f);
            float bodyA = Mathf.Clamp01(0.5f - d);
            float ring = bodyA * Mathf.Clamp01(d + 1.5f);

            Color col = fill; col.a *= bodyA;
            col = Over(new Color(border.r, border.g, border.b, border.a * ring), col);

            if (on)
            {
                float dl = Mathf.Min(SegDist(p, a, b), SegDist(p, b, c)) - 1.1f;
                float ca = Mathf.Clamp01(0.5f - dl) * bodyA;
                col = Over(new Color(check.r, check.g, check.b, ca), col);
            }

            px[y * s + x] = col;
        }

        t.SetPixels(px);
        t.Apply();
        return t;
    }

    private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 pa = p - a, ba = b - a;
        float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
        return (pa - ba * h).magnitude;
    }
}
