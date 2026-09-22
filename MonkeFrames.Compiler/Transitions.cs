using System;
using MonkeFrames.Compiler.Models;
using UnityEngine;

namespace MonkeFrames.Compiler;

/// <summary>
/// Easing curves used by transitions. Public so editors can preview them.
/// </summary>
public static class Easing
{
    /// <summary>
    /// Map linear progress <paramref name="t"/> (0..1) through the curve for <paramref name="effect"/>.
    /// For <see cref="TransitionEffect.Smooth"/> this returns the ease used when no neighbouring
    /// keyframes exist; the real smooth path is computed by the spline in <see cref="Compiler"/>.
    /// </summary>
    public static float Evaluate(TransitionEffect effect, float t)
    {
        t = Mathf.Clamp01(t);

        return effect switch
        {
            TransitionEffect.Cut => 0f,
            TransitionEffect.Sine => 0.5f - 0.5f * Mathf.Cos(t * Mathf.PI),
            TransitionEffect.EaseIn => t * t * t,
            TransitionEffect.EaseOut => 1f - Mathf.Pow(1f - t, 3f),
            TransitionEffect.Smooth => t * t * (3f - 2f * t),
            _ => t,
        };
    }

    /// <summary>
    /// Progress (0..1, may overshoot) for a transition at linear time <paramref name="t"/>,
    /// using the custom speed curve when the transition has one.
    /// </summary>
    public static float Evaluate(Transition transition, float t) =>
        transition.UsesCurve
            ? Bezier(transition.CurveX1, transition.CurveY1, transition.CurveX2, transition.CurveY2, t)
            : Evaluate(transition.Effect, t);

    /// <summary>
    /// Evaluate a CSS-style cubic bezier timing curve: P0=(0,0), P1=(x1,y1), P2=(x2,y2), P3=(1,1).
    /// Returns y for the given x (time).
    /// </summary>
    public static float Bezier(float x1, float y1, float x2, float y2, float x)
    {
        x = Mathf.Clamp01(x);
        x1 = Mathf.Clamp01(x1);
        x2 = Mathf.Clamp01(x2);

        if (x <= 0f) return 0f;
        if (x >= 1f) return 1f;

        // Solve bezierX(u) = x for u with Newton-Raphson, falling back to bisection.
        float u = x;
        for (int i = 0; i < 8; i++)
        {
            float err = BezierCoord(x1, x2, u) - x;
            if (Mathf.Abs(err) < 1e-5f) return BezierCoord(y1, y2, u);
            float d = BezierSlope(x1, x2, u);
            if (Mathf.Abs(d) < 1e-6f) break;
            u -= err / d;
        }

        float lo = 0f, hi = 1f;
        u = x;
        for (int i = 0; i < 30; i++)
        {
            float cx = BezierCoord(x1, x2, u);
            if (Mathf.Abs(cx - x) < 1e-5f) break;
            if (cx < x) lo = u; else hi = u;
            u = (lo + hi) * 0.5f;
        }

        return BezierCoord(y1, y2, u);
    }

    private static float BezierCoord(float p1, float p2, float u)
    {
        float v = 1f - u;
        return 3f * v * v * u * p1 + 3f * v * u * u * p2 + u * u * u;
    }

    private static float BezierSlope(float p1, float p2, float u)
    {
        float v = 1f - u;
        return 3f * v * v * p1 + 6f * v * u * (p2 - p1) + 3f * u * u * (1f - p2);
    }

    // ---- Cubic Hermite basis ----

    internal static float H00(float s) => 2 * s * s * s - 3 * s * s + 1;
    internal static float H10(float s) => s * s * s - 2 * s * s + s;
    internal static float H01(float s) => -2 * s * s * s + 3 * s * s;
    internal static float H11(float s) => s * s * s - s * s;
}

internal static class Transitions
{
    public static Vector3 Interpolate(Transition tr, Vector3 start, Vector3 end, int current, int total) =>
        Vector3.LerpUnclamped(start, end, Easing.Evaluate(tr, Progress(current, total)));

    public static Quaternion Interpolate(Transition tr, Quaternion start, Quaternion end, int current, int total) =>
        Quaternion.SlerpUnclamped(start, end, Easing.Evaluate(tr, Progress(current, total)));

    public static float Interpolate(Transition tr, float start, float end, int current, int total) =>
        Mathf.LerpUnclamped(start, end, Easing.Evaluate(tr, Progress(current, total)));

    public static float Progress(int current, int total) =>
        total <= 0 ? 1f : (float)current / total;

    /// <summary>Cubic Hermite interpolation of a Vector4 (used for positions, quaternions and FOV).</summary>
    public static Vector4 Hermite(Vector4 p0, Vector4 m0, Vector4 p1, Vector4 m1, float duration, float s) =>
        Easing.H00(s) * p0 + Easing.H10(s) * duration * m0 + Easing.H01(s) * p1 + Easing.H11(s) * duration * m1;

    public static Vector4 ToVector4(Quaternion q) => new Vector4(q.x, q.y, q.z, q.w);

    public static Quaternion ToQuaternion(Vector4 v)
    {
        v = v.normalized;
        return new Quaternion(v.x, v.y, v.z, v.w);
    }
}
