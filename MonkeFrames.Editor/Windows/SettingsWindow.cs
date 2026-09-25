using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;
using MonkeFrames.Editor.Utilities;
using System.Collections.Generic;
using UnityEngine;

namespace MonkeFrames.Editor.Windows;

public class SettingsWindow : IEditorWindow
{
    public string Name => "Settings";
    public Rect Rect => new Rect(120, 50, 500, Mathf.Min(820, Screen.height - 80));

    // ---- Accent colour wheel state ----
    private float _h = -1f, _s, _v;
    private string _hex = "";
    private bool _wheelDrag;
    private static Texture2D _wheel;
    private const string HexControl = "mf.accent.hex";

    private Vector2 _scroll;
    private float _contentH = 1400f;

    public void OnDraw()
    {
        Rect view = new Rect(0, 34, Rect.width - 2, Rect.height - 34 - 46);
        bool needScroll = _contentH > view.height;
        float contentW = view.width - (needScroll ? 14f : 0f);

        _scroll = GUI.BeginScrollView(view, _scroll, new Rect(0, 0, contentW, _contentH));
        float end = Body(contentW, 18, 8);
        GUI.enabled = true;
        GUI.EndScrollView();
        if (Event.current.type == EventType.Repaint)
            _contentH = end + 12;

        // ---- Hint bar ----
        float ww = Rect.width;
        string hint = string.IsNullOrEmpty(GUI.tooltip) ? "Changes are saved when you close this window." : GUI.tooltip;
        Theme.Fill(new Rect(10, Rect.height - 36, ww - 20, 26), Theme.Surface, 6);
        GUI.Label(new Rect(20, Rect.height - 36, ww - 40, 26), hint, Theme.Muted);
        GUI.tooltip = "";
    }

    private float Body(float w, float x, float y)
    {

        // ---- Appearance ----
        GUI.Label(new Rect(x, y, 200, 20), "APPEARANCE", Theme.Header);
        y += 24;

        y = DrawAccentPicker(x, y, w - x * 2);

        Settings.current.Animations = Widgets.Switch("anim", new Rect(x, y, 260, 26), Settings.current.Animations,
            "UI animations", "Smooth window, menu and hover transitions.");
        y += 32;

        Settings.current.ShowIntro = Widgets.Switch("intro", new Rect(x, y, w - x * 2, 26), Settings.current.ShowIntro,
            "Intro on startup", "Play the animated MonkeFrames logo when the mod loads.");
        y += 32;

        bool soundWas = Settings.current.NotificationSound;
        Settings.current.NotificationSound = Widgets.Switch("notifsound", new Rect(x, y, w - x * 2, 26), Settings.current.NotificationSound,
            "Notification sound", "Play a soft chime when a notification pops up.");
        if (Settings.current.NotificationSound && !soundWas)
            NotificationSound.Play(force: true);
        y += 32;

        GUI.enabled = Settings.current.NotificationSound;
        GUI.Label(new Rect(x, y, 120, 24), new GUIContent("Sound volume", "How loud the notification chime is."));
        Settings.current.NotificationVolume = Widgets.Slider("notifvol", new Rect(x + 120, y, w - x * 2 - 234, 24),
            Settings.current.NotificationVolume, 0f, 1f);
        GUI.Label(new Rect(w - x - 108, y, 40, 24), $"{Settings.current.NotificationVolume * 100f:0}%", Theme.LabelRight);
        if (GUI.Button(new Rect(w - x - 62, y, 62, 24), "Test"))
            NotificationSound.Play(force: true);
        GUI.enabled = true;
        y += 34;

        GUI.enabled = Settings.current.Animations;
        GUI.Label(new Rect(x, y, 120, 24), new GUIContent("Animation speed", "How quickly transitions play."));
        Settings.current.AnimationSpeed = Widgets.Slider("animspeed", new Rect(x + 120, y, w - x * 2 - 170, 24),
            Settings.current.AnimationSpeed, 0.5f, 2f);
        GUI.Label(new Rect(w - x - 44, y, 44, 24), $"{Settings.current.AnimationSpeed:0.0}x", Theme.LabelRight);
        GUI.enabled = true;
        y += 38;

        Widgets.Divider(x, y - 8, w - x * 2);

        // ---- Editing ----
        GUI.Label(new Rect(x, y, 200, 20), "EDITING", Theme.Header);
        y += 24;

        Settings.current.Autosave = Widgets.Switch("autosave", new Rect(x, y, w - x * 2, 26), Settings.current.Autosave,
            "Autosave projects", "Automatically save any named project when its keyframes are changed.");
        y += 32;

        Settings.current.SmoothByDefault = Widgets.Switch("smoothdefault", new Rect(x, y, w - x * 2, 26), Settings.current.SmoothByDefault,
            "Smooth keyframes by default", "New keyframes use the Smooth transition, so the camera flows through them without stopping.");
        y += 40;

        Widgets.Divider(x, y - 8, w - x * 2);

        // ---- Camera ----
        GUI.Label(new Rect(x, y, 200, 20), "CAMERA", Theme.Header);
        y += 24;

        Settings.current.SmoothMouseLook = Widgets.Switch("smoothlook", new Rect(x, y, w - x * 2 - 90, 26), Settings.current.SmoothMouseLook,
            "Smooth mouse look", "Cinematic, floaty mouse look (left-drag) and tilt (right-drag). Press Caps Lock at any time to toggle it.");
        Rect key = new Rect(w - x - 80, y + 3, 80, 20);
        Theme.Fill(key, new Color(1, 1, 1, 0.08f), 5);
        Theme.DrawText(key, "Caps Lock", Theme.LabelCenterSmall, Theme.Text);
        y += 34;

        GUI.enabled = Settings.current.SmoothMouseLook;
        GUI.Label(new Rect(x, y, 120, 24), new GUIContent("Look smoothing", "Light = responsive, Heavy = slow and floaty."));
        Settings.current.MouseSmoothing = Widgets.Slider("mousesmooth", new Rect(x + 120, y, w - x * 2 - 190, 24),
            Settings.current.MouseSmoothing, 0f, 1f);
        float ms = Settings.current.MouseSmoothing;
        GUI.Label(new Rect(w - x - 64, y, 64, 24), ms < 0.34f ? "Light" : ms < 0.67f ? "Medium" : "Heavy", Theme.LabelRight);
        GUI.enabled = true;
        y += 38;

        Settings.current.AutoLevel = Widgets.Switch("autolevel", new Rect(x, y, w - x * 2, 26), Settings.current.AutoLevel,
            "Auto-level tilt", "While tilting (right-drag) the horizon sticks at level, and letting go close to level glides it the rest of the way.");
        y += 32;
        GUI.enabled = Settings.current.AutoLevel;
        GUI.Label(new Rect(x, y, 120, 24), new GUIContent("Level zone", "How close to level (in degrees) counts as 'nearly level'."));
        Settings.current.AutoLevelAngle = Widgets.Slider("autolevelang", new Rect(x + 120, y, w - x * 2 - 170, 24), Settings.current.AutoLevelAngle, 1f, 20f);
        Settings.current.LevelSnapAngle = Mathf.Clamp(Settings.current.AutoLevelAngle * 0.5f, 0.5f, 8f);
        GUI.Label(new Rect(w - x - 44, y, 44, 24), $"{Settings.current.AutoLevelAngle:0}°", Theme.LabelRight);
        GUI.enabled = true;
        y += 32;
        Settings.current.DoubleClickLevel = Widgets.Switch("dblevel", new Rect(x, y, w - x * 2 - 110, 26), Settings.current.DoubleClickLevel,
            "Double right-click to level", "Double right-click anywhere on the view to straighten the horizon.");
        Rect key2 = new Rect(w - x - 100, y + 3, 100, 20);
        Theme.Fill(key2, new Color(1, 1, 1, 0.08f), 5);
        Theme.DrawText(key2, "2x Right-click", Theme.LabelCenterSmall, Theme.Text);
        y += 40;

        Widgets.Divider(x, y - 8, w - x * 2);

        // ---- Spectator camera model ----
        GUI.Label(new Rect(x, y, 300, 20), "SPECTATOR CAMERA MODEL", Theme.Header);
        y += 24;
        Settings.current.ShareMyCamera = Widgets.Switch("sharecam", new Rect(x, y, w - x * 2, 26), Settings.current.ShareMyCamera,
            "Show my camera to other MonkeFrames users", "Players who also have MonkeFrames see a camera model where your camera is (in VR too). You never see your own.");
        y += 30;
        Settings.current.ShowOtherCameras = Widgets.Switch("othercams", new Rect(x, y, w - x * 2, 26), Settings.current.ShowOtherCameras,
            "Show other people's cameras", "See where other MonkeFrames users are filming from.");
        y += 30;
        Settings.current.ShowCamerasInReplays = Widgets.Switch("replaycams", new Rect(x, y, w - x * 2, 26), Settings.current.ShowCamerasInReplays,
            "Show cameras in replays", "Replays record every MonkeFrames camera (yours too) and show them as camera models.");
        y += 30;
        Settings.current.TintCameraLogo = Widgets.Switch("tintcamlogo", new Rect(x, y, w - x * 2, 26), Settings.current.TintCameraLogo,
            "Gorilla-colour camera glow", "On = the glowing logo on each camera takes that player's gorilla colour. Off = the logo's own colours.");
        y += 34;

        Widgets.Divider(x, y - 4, w - x * 2);
        y += 6;
        y = TagEffectSettings.Draw(x, y, w - x * 2);
        return y;
    }

    /// <summary>Accent colour: a hue/saturation wheel, a brightness slider and a hex code box.</summary>
    private float DrawAccentPicker(float x, float y, float width)
    {
        Color saved = Settings.current.AccentColor;
        if (_h < 0f)
        {
            Color.RGBToHSV(saved, out _h, out _s, out _v);
            _hex = ToHex(saved);
        }

        GUI.Label(new Rect(x, y, 200, 22), new GUIContent("Accent colour", "Used for highlights, keyframe markers and the camera line."));
        y += 26;

        // ---- Wheel ----
        const float size = 132f;
        Rect wheel = new Rect(x, y, size, size);
        Vector2 c = wheel.center;
        float radius = size / 2f;

        Color prev = GUI.color;
        GUI.color = new Color(_v, _v, _v, 1f);
        GUI.DrawTexture(wheel, Wheel(), ScaleMode.StretchToFill, true);
        GUI.color = prev;

        float ang = _h * Mathf.PI * 2f;
        Vector2 knob = c + new Vector2(Mathf.Cos(ang), -Mathf.Sin(ang)) * (_s * (radius - 2f));
        Color pending = Color.HSVToRGB(_h, _s, _v);
        Theme.Dot(knob, 8f, Color.white);
        Theme.Dot(knob, 6f, pending);

        Event e = Event.current;
        int id = GUIUtility.GetControlID(FocusType.Passive);
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown when e.button == 0 && (e.mousePosition - c).magnitude <= radius + 4f:
                GUIUtility.hotControl = id;
                _wheelDrag = true;
                PickFromWheel(e.mousePosition, c, radius);
                e.Use();
                break;
            case EventType.MouseDrag when GUIUtility.hotControl == id:
                PickFromWheel(e.mousePosition, c, radius);
                e.Use();
                break;
            case EventType.MouseUp when GUIUtility.hotControl == id:
                GUIUtility.hotControl = 0;
                _wheelDrag = false;
                e.Use();
                break;
        }

        // ---- Right side: preview, brightness, hex ----
        float rx = x + size + 18, rw = width - size - 18;
        Theme.Fill(new Rect(rx, y, rw, 34), Color.HSVToRGB(_h, _s, _v), 8);
        Theme.DrawText(new Rect(rx, y, rw, 34), "Preview", Theme.LabelCenter, _v * (0.3f + 0.7f * (1f - _s)) > 0.6f ? Color.black : Color.white);

        GUI.Label(new Rect(rx, y + 44, rw, 20), "Brightness", Theme.MutedSmall);
        float v = Widgets.Slider("accent.v", new Rect(rx, y + 62, rw, 22), _v, 0.15f, 1f);
        if (!Mathf.Approximately(v, _v))
        {
            _v = v;
            if (GUI.GetNameOfFocusedControl() != HexControl) _hex = ToHex(Color.HSVToRGB(_h, _s, _v));
        }

        GUI.Label(new Rect(rx, y + 92, 40, 26), "Hex");
        GUI.SetNextControlName(HexControl);
        string typed = GUI.TextField(new Rect(rx + 40, y + 92, rw - 40, 26), _hex ?? "");
        if (typed != _hex)
        {
            _hex = typed;
            if (TryParseHex(typed, out Color parsed))
            {
                Color.RGBToHSV(parsed, out _h, out _s, out _v);
                _v = Mathf.Max(_v, 0.15f);
            }
        }

        // Apply once you let go (rebuilding the UI theme every frame while dragging would be wasteful).
        Color chosen = Color.HSVToRGB(_h, _s, _v);
        if (!_wheelDrag && GUIUtility.hotControl == 0 && !Same(chosen, saved))
        {
            Settings.current.AccentColor = chosen;
            if (GUI.GetNameOfFocusedControl() != HexControl) _hex = ToHex(chosen);
            UIManager.Instance.Status = $"Accent colour set to {ToHex(chosen)}.";
        }

        return y + size + 12;
    }

    private void PickFromWheel(Vector2 mouse, Vector2 c, float radius)
    {
        Vector2 d = mouse - c;
        float a = Mathf.Atan2(-d.y, d.x);
        if (a < 0f) a += Mathf.PI * 2f;
        _h = a / (Mathf.PI * 2f);
        _s = Mathf.Clamp01(d.magnitude / (radius - 2f));
        _hex = ToHex(Color.HSVToRGB(_h, _s, _v));
    }

    private static bool Same(Color a, Color b) =>
        Mathf.Abs(a.r - b.r) < 0.002f && Mathf.Abs(a.g - b.g) < 0.002f && Mathf.Abs(a.b - b.b) < 0.002f;

    private static string ToHex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);

    private static bool TryParseHex(string text, out Color color)
    {
        color = Color.white;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        if (!t.StartsWith("#")) t = "#" + t;
        return (t.Length == 7 || t.Length == 4) && ColorUtility.TryParseHtmlString(t, out color);
    }

    /// <summary>Hue around the circle, saturation from the centre outwards.</summary>
    private static Texture2D Wheel()
    {
        if (_wheel != null) return _wheel;
        const int n = 256;
        _wheel = new Texture2D(n, n, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
        Color[] px = new Color[n * n];
        float r = n / 2f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x + 0.5f - r, dy = y + 0.5f - r;   // texture rows go up, like the maths
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Atan2(dy, dx);
                if (a < 0f) a += Mathf.PI * 2f;
                Color col = Color.HSVToRGB(a / (Mathf.PI * 2f), Mathf.Clamp01(dist / (r - 2f)), 1f);
                col.a = Mathf.Clamp01(r - 1f - dist);
                px[y * n + x] = col;
            }
        _wheel.SetPixels(px);
        _wheel.Apply();
        return _wheel;
    }

    public void OnClose()
    {
        KeyframeManager.Instance.RefreshOrbs();
        Settings.Save();
    }
}
