using System.Collections.Generic;
using MonkeFrames.Editor.Classes;
using UnityEngine;

namespace MonkeFrames.Editor.UI;

/// <summary>
/// Frame-rate independent tweening helpers for the immediate-mode UI.
/// Values only advance once per rendered frame (on Repaint), so OnGUI being
/// called several times per frame does not speed animations up.
/// </summary>
public static class Anim
{
    private class Slot
    {
        public float Value;
        public int Frame;
    }

    private static readonly Dictionary<string, Slot> Slots = new();

    /// <summary>Global animation speed multiplier (from settings). Very large when animations are off.</summary>
    public static float Speed
    {
        get
        {
            Settings s = Settings.current;
            if (s == null) return 1f;
            if (!s.Animations) return 1000f;
            return Mathf.Clamp(s.AnimationSpeed, 0.25f, 3f);
        }
    }

    public static bool Enabled => Settings.current == null || Settings.current.Animations;

    /// <summary>
    /// Smoothly move the value stored under <paramref name="key"/> towards <paramref name="target"/>.
    /// </summary>
    /// <param name="rate">Higher = snappier. ~12 feels responsive, ~6 feels floaty.</param>
    /// <param name="initial">Starting value the first time the key is seen (defaults to target).</param>
    public static float To(string key, float target, float rate = 14f, float? initial = null)
    {
        if (!Slots.TryGetValue(key, out Slot slot))
        {
            slot = new Slot { Value = initial ?? target, Frame = Time.frameCount };
            Slots[key] = slot;
        }

        bool repaint = Event.current == null || Event.current.type == EventType.Repaint;
        if (repaint && slot.Frame != Time.frameCount)
        {
            slot.Frame = Time.frameCount;
            slot.Value = Damp(slot.Value, target, rate * Speed, Time.unscaledDeltaTime);

            if (Mathf.Abs(slot.Value - target) < 0.0005f)
                slot.Value = target;
        }

        return slot.Value;
    }

    /// <summary>Reset a keyed value (e.g. to replay an intro animation).</summary>
    public static void Set(string key, float value)
    {
        if (!Slots.TryGetValue(key, out Slot slot))
            Slots[key] = slot = new Slot();

        slot.Value = value;
        slot.Frame = Time.frameCount;
    }

    public static float Damp(float current, float target, float lambda, float dt) =>
        Mathf.Lerp(current, target, 1f - Mathf.Exp(-lambda * dt));

    // ---- Easing curves (t in 0..1) ----

    public static float OutCubic(float t) { t = Mathf.Clamp01(t) - 1f; return t * t * t + 1f; }
    public static float InCubic(float t) { t = Mathf.Clamp01(t); return t * t * t; }

    public static float InOutCubic(float t)
    {
        t = Mathf.Clamp01(t);
        return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
    }

    public static float OutBack(float t)
    {
        t = Mathf.Clamp01(t);
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
    }
}
