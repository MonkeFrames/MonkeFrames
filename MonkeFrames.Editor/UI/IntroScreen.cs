using UnityEngine;

namespace MonkeFrames.Editor.UI;

/// <summary>
/// Minimal animated intro / loading screen shown when MonkeFrames starts.
///
/// Timeline (seconds, 10 total):
///   0.0 - 1.0  backdrop fades in
///   0.6 - 3.4  logo fades in with a soft left-to-right wipe and a gentle rise
///   3.4 - 8.5  logo slowly "breathes"; a quiet ring ripples from the camera hand at 3.5s and 6.5s
///   1.8 - 8.5  a thin loading line draws out under the logo
///   9.0 - 10.0 logo drifts up and fades, backdrop lifts to reveal the editor
/// Any key or mouse click skips straight to the exit.
/// </summary>
public static class IntroScreen
{
    public const float ExitStart = 9.0f;
    public const float ExitLength = 1.0f;
    public const float Total = ExitStart + ExitLength;

    private const int Strips = 32;

    public static Texture2D Logo;

    private static float _start = -1f;
    private static bool _done;

    public static bool Active => !_done && Logo != null;

    /// <summary>True once the exit animation has started, so the editor can begin drawing underneath.</summary>
    public static bool Exiting => _start >= 0 && Time.unscaledTime - _start >= ExitStart;

    /// <summary>Play the intro again (menu item).</summary>
    public static void Replay()
    {
        _start = -1f;
        _done = false;
    }

    public static void Skip() => _done = true;

    /// <summary>Draw the intro. Call at the end of OnGUI so it sits on top of the editor.</summary>
    public static void Draw()
    {
        if (!Active)
            return;

        Event e = Event.current;

        if (_start < 0)
        {
            if (e.type != EventType.Repaint) return;
            _start = Time.unscaledTime;
        }

        // Skip on any key or click: jump straight to the exit animation.
        if ((e.type == EventType.KeyDown || e.type == EventType.MouseDown) && Time.unscaledTime - _start < ExitStart)
        {
            _start = Time.unscaledTime - ExitStart;
            e.Use();
        }

        float t = Time.unscaledTime - _start;
        if (t >= Total)
        {
            _done = true;
            return;
        }

        float sw = Screen.width, sh = Screen.height;
        float exit = Anim.InOutCubic(Mathf.Clamp01((t - ExitStart) / ExitLength));
        float backdrop = Anim.InOutCubic(Mathf.Clamp01(t / 1.0f))
            * (1f - Anim.InOutCubic(Mathf.Clamp01((t - ExitStart - 0.25f) / (ExitLength - 0.25f))));

        // Slow breathing once the logo has landed.
        float breathe = t > 3.4f ? (Mathf.Sin((t - 3.4f) * 1.1f) * 0.5f + 0.5f) * (1f - exit) : 0f;

        Color prev = GUI.color;

        // Swallow input so nothing underneath is clicked during the intro.
        if (e.isMouse || e.isKey)
            e.Use();

        // ---- Backdrop ----
        Theme.Fill(new Rect(0, 0, sw, sh), new Color(0.04f, 0.045f, 0.055f, 0.98f * backdrop), 0);

        // ---- Logo ----
        float logoW = Mathf.Min(sw * 0.46f, 880f);
        float logoH = logoW * Logo.height / Logo.width;
        Vector2 center = new Vector2(sw / 2f, sh * 0.45f - exit * 20f - breathe * 4f);
        float grow = 1f + breathe * 0.012f;
        Rect logo = new Rect(center.x - logoW * grow / 2f, center.y - logoH * grow / 2f, logoW * grow, logoH * grow);
        float logoAlpha = 1f - exit;

        DrawStrips(logo, t, Color.white.WithAlpha(logoAlpha));

        // ---- Quiet rings from the camera hand ----
        Vector2 hand = new Vector2(logo.x + logo.width * 0.868f, logo.y + logo.height * 0.27f);
        // A quiet ring every 3 seconds while the logo is on screen.
        for (float ringAt = 3.5f; ringAt < ExitStart - 1f; ringAt += 3.0f)
            DrawRing(hand, t - ringAt, Color.white, logoW, logoAlpha);

        // ---- Thin loading line ----
        float ui = Anim.InOutCubic(Mathf.Clamp01((t - 1.6f) / 0.7f)) * (1f - Anim.InOutCubic(Mathf.Clamp01((t - ExitStart) / 0.5f)));
        if (ui > 0.01f)
        {
            GUI.color = new Color(1, 1, 1, ui);

            float lineW = logoW * 0.42f;
            float y = logo.yMax + 44f;
            float progress = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 1.8f) / 6.7f));

            Theme.Fill(new Rect(center.x - lineW / 2f, y, lineW, 2), new Color(1, 1, 1, 0.12f), 0);
            float fw = lineW * progress;
            Theme.Fill(new Rect(center.x - fw / 2f, y, fw, 2), new Color(1, 1, 1, 0.85f), 0);

            Theme.DrawText(new Rect(0, y + 14, sw, 20), progress < 1f ? "loading" : "ready",
                Theme.MutedCenter, Theme.TextMuted.WithAlpha(0.7f));

            Theme.DrawText(new Rect(0, sh - 36, sw, 20), "click to skip", Theme.MutedCenter, Theme.TextMuted.WithAlpha(0.35f));
        }

        GUI.color = prev;
    }

    /// <summary>Draw the logo as vertical strips that drop in one after another.</summary>
    private static void DrawStrips(Rect logo, float t, Color color)
    {
        Color prev = GUI.color;
        float stripW = logo.width / Strips;

        for (int i = 0; i < Strips; i++)
        {
            // Soft wipe: each strip starts a touch after the one to its left and eases up into place.
            float local = Mathf.Clamp01((t - 0.6f - i * (1.6f / Strips)) / 1.2f);
            if (local <= 0f) continue;

            float e = Anim.InOutCubic(local);
            float drop = (1f - e) * 14f;
            float a = e;

            GUI.color = new Color(color.r, color.g, color.b, color.a * a * prev.a);
            Rect r = new Rect(logo.x + i * stripW, logo.y + drop, stripW + 0.5f, logo.height);
            GUI.DrawTextureWithTexCoords(r, Logo, new Rect(i / (float)Strips, 0f, 1f / Strips, 1f));
        }

        GUI.color = prev;
    }

    /// <summary>An expanding, fading ring.</summary>
    private static void DrawRing(Vector2 center, float t, Color color, float logoW, float alpha)
    {
        const float length = 2.0f;
        if (t <= 0f || t >= length) return;

        float p = t / length;
        float radius = Mathf.Lerp(18f, logoW * 0.22f, Anim.OutCubic(p));
        float a = Mathf.Sin(p * Mathf.PI) * 0.35f * alpha;
        const int dots = 96;

        for (int i = 0; i < dots; i++)
        {
            float ang = i / (float)dots * Mathf.PI * 2f;
            Theme.Dot(center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * radius, 1.1f, color.WithAlpha(a));
        }
    }
}
