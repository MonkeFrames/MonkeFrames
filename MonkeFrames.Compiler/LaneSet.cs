using System;
using System.Collections.Generic;
using MonkeFrames.Compiler.Models;
using UnityEngine;

using Keyframe = MonkeFrames.Compiler.Models.Keyframe;

namespace MonkeFrames.Compiler;

/// <summary>
/// Each property's own keyframes (Advanced Keyframe Editor). A property that is keyed on every keyframe and has no
/// timing of its own uses the normal MonkeFrames animation unchanged. Otherwise it moves from each of its keyframes
/// to its next one over the whole time between them (gliding through keyframes where it isn't keyed), with its own
/// speed curve, like a property in After Effects. The path between its keys is built exactly like MonkeFrames'
/// own: the Smooth spline, Auto smooth, or a straight line.
/// </summary>
internal class LaneSet
{
    private Project _p;
    private List<Keyframe> _keys;
    private float[] _start;           // time each keyframe starts (s); [n] = end
    private Lane _pos, _rot, _zoom, _time;

    private class Lane
    {
        public KeyLanes Id;
        public int[] Keyed;                     // keyframe indices keyed in this lane
        public int[] PrevKey;                   // per keyframe: position in Keyed of the last key at / before it
        public List<Keyframe> Keys;             // the lane's keys as a keyframe list (durations = time to the next key)
        public Compiler.SplineData Spline;      // Smooth spline through the lane's keys
        public Compiler.AutoPath Auto;          // Auto smooth path through the lane's keys (when Auto smooth is on)
    }

    /// <summary>Null when the project is a normal one (every property keyed everywhere, no own timing).</summary>
    public static LaneSet Create(Project project)
    {
        List<Keyframe> keys = project.Keyframes;
        if (keys == null || keys.Count == 0) return null;
        bool advanced = false;
        foreach (Keyframe k in keys)
            if (k.Keys != KeyLanes.All || k.Transition.HasLaneCurves) { advanced = true; break; }
        if (!advanced) return null;

        var s = new LaneSet { _p = project, _keys = keys, _start = new float[keys.Count + 1] };
        for (int i = 0; i < keys.Count; i++)
            s._start[i + 1] = s._start[i] + Mathf.Max(0f, keys[i].Transition.Duration);
        s._pos = s.Make(KeyLanes.Position);
        s._rot = s.Make(KeyLanes.Rotation);
        s._zoom = s.Make(KeyLanes.Zoom);
        s._time = s.Make(KeyLanes.TimeOfDay);
        return s;
    }

    private Lane Make(KeyLanes id)
    {
        int n = _keys.Count;
        bool simple = true;
        var keyed = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (_keys[i].Has(id)) keyed.Add(i);
            else simple = false;
            if (_keys[i].Transition.Lane(id).HasValue) simple = false;
        }
        if (simple) return null;                 // the normal animation
        if (keyed.Count == 0) keyed.Add(0);      // nothing keyed: hold the first keyframe's value

        var lane = new Lane { Id = id, Keyed = keyed.ToArray(), PrevKey = new int[n], Keys = new List<Keyframe>() };
        int m = -1;
        for (int i = 0; i < n; i++)
        {
            while (m + 1 < lane.Keyed.Length && lane.Keyed[m + 1] <= i) m++;
            lane.PrevKey[i] = m;
        }
        for (int k = 0; k < lane.Keyed.Length; k++)
        {
            Keyframe kf = _keys[lane.Keyed[k]];
            if (k + 1 < lane.Keyed.Length)
                kf.Transition.Duration = _start[lane.Keyed[k + 1]] - _start[lane.Keyed[k]];
            // a hold key stops the flow of the path like a cut
            LaneCurve? own = kf.Transition.Lane(id);
            if (own.HasValue && own.Value.Interp == LaneInterp.Hold) kf.Transition.Effect = TransitionEffect.Cut;
            else if (own.HasValue && own.Value.Interp == LaneInterp.Auto) kf.Transition.Effect = TransitionEffect.Smooth;
            lane.Keys.Add(kf);
        }
        lane.Spline = Compiler.SplineData.Create(lane.Keys, _p.Smoothness);
        if (_p.AutoSmooth) lane.Auto = Compiler.AutoPath.Create(lane.Keys, _p.AutoSmoothStrength);
        return lane;
    }

    private enum PathKind { Hold, Line, Spline, Auto }

    /// <summary>
    /// Where time t (in keyframe segment seg) is in the lane: the lane keys around it, the progress along the path
    /// and the kind of path. False before the first / after the last key (hold key a).
    /// </summary>
    private bool Span(Lane lane, int seg, float t, out int a, out int b, out float s, out PathKind kind, out bool smoothTime)
    {
        a = lane.PrevKey[seg];
        b = a + 1;
        s = 0f; kind = PathKind.Hold; smoothTime = false;
        if (a < 0) { a = 0; return false; }                        // before the first key: hold it
        if (b >= lane.Keyed.Length) return false;                  // after the last key: hold it

        float t0 = _start[lane.Keyed[a]], t1 = _start[lane.Keyed[b]];
        float u = t1 - t0 > 1e-6f ? Mathf.Clamp01((t - t0) / (t1 - t0)) : 1f;
        Transition tr = lane.Keys[a].Transition;                   // (effect already adjusted for hold / auto)
        LaneCurve? own = _keys[lane.Keyed[a]].Transition.Lane(lane.Id);

        if (own.HasValue && own.Value.Interp == LaneInterp.Hold) { kind = PathKind.Hold; s = 0f; return true; }
        if (tr.Effect == TransitionEffect.Smooth)
        {
            // the Smooth spline (keeps momentum); the speed along it is the own curve, or the custom speed
            kind = PathKind.Spline;
            s = own.HasValue ? own.Value.Evaluate(u)
              : tr.CustomSpeed ? Easing.Bezier(tr.CurveX1, tr.CurveY1, tr.CurveX2, tr.CurveY2, u) : u;
            smoothTime = !own.HasValue;
            return true;
        }
        if (!own.HasValue && tr.Effect == TransitionEffect.Cut) { kind = PathKind.Hold; s = 0f; return true; }
        s = own.HasValue ? own.Value.Evaluate(u) : Easing.Evaluate(tr, u);
        kind = lane.Auto != null ? PathKind.Auto : PathKind.Line;
        return true;
    }

    /// <summary>Replace the values of every property that has its own keyframes / timing.</summary>
    public void Apply(int seg, int j, int frames, ref Vector3 pos, ref Quaternion rot, ref float fov, ref float time)
    {
        float d = Mathf.Max(0f, _keys[seg].Transition.Duration);
        float t = _start[seg] + (frames > 0 ? d * j / frames : 0f);
        int a, b; float s; PathKind kind; bool sst;

        if (_pos != null)
        {
            Lane L = _pos;
            if (!Span(L, seg, t, out a, out b, out s, out kind, out _) || kind == PathKind.Hold) pos = L.Keys[a].Position;
            else if (kind == PathKind.Spline)
                pos = Transitions.Hermite(L.Spline.Positions[a], L.Spline.PositionTangents[a], L.Spline.Positions[b], L.Spline.PositionTangents[b], L.Keys[a].Transition.Duration, s);
            else if (kind == PathKind.Auto) pos = L.Auto.Position(a, s);
            else pos = Vector3.LerpUnclamped(L.Keys[a].Position, L.Keys[b].Position, s);
        }
        if (_rot != null)
        {
            Lane L = _rot;
            if (!Span(L, seg, t, out a, out b, out s, out kind, out _) || kind == PathKind.Hold) rot = L.Keys[a].QuatRotation;
            else if (kind == PathKind.Spline)
                rot = Transitions.ToQuaternion(Transitions.Hermite(L.Spline.Rotations[a], L.Spline.RotationTangents[a], L.Spline.Rotations[b], L.Spline.RotationTangents[b], L.Keys[a].Transition.Duration, s));
            else if (kind == PathKind.Auto) rot = L.Auto.Rotation(a, s);
            else rot = Quaternion.SlerpUnclamped(L.Keys[a].QuatRotation, L.Keys[b].QuatRotation, s);
        }
        if (_zoom != null)
        {
            Lane L = _zoom;
            if (!Span(L, seg, t, out a, out b, out s, out kind, out _) || kind == PathKind.Hold) fov = L.Keys[a].FieldOfView;
            else if (kind == PathKind.Spline)
                fov = Transitions.Hermite(new Vector4(L.Keys[a].FieldOfView, 0, 0, 0), new Vector4(L.Spline.FovTangents[a], 0, 0, 0),
                    new Vector4(L.Keys[b].FieldOfView, 0, 0, 0), new Vector4(L.Spline.FovTangents[b], 0, 0, 0), L.Keys[a].Transition.Duration, s).x;
            else if (kind == PathKind.Auto) fov = L.Auto.Fov(a, s);
            else fov = Mathf.LerpUnclamped(L.Keys[a].FieldOfView, L.Keys[b].FieldOfView, s);
        }
        if (_time != null)
        {
            Lane L = _time;
            if (Span(L, seg, t, out a, out b, out s, out kind, out sst) && kind != PathKind.Hold)
            {
                float h0 = L.Keys[a].TimeOfDay, h1 = L.Keys[b].TimeOfDay;
                if (h0 >= 0f && h1 >= 0f)
                {
                    if (sst) s = s * s * (3f - 2f * s);
                    float delta = Mathf.Repeat(h1 - h0 + 12f, 24f) - 12f;
                    time = Mathf.Repeat(h0 + delta * s, 24f);
                }
                else time = h0;
            }
            else time = L.Keys[a].TimeOfDay;
        }
    }
}

/// <summary>Handheld shake (After Effects' wiggle()): smooth, seeded noise on position, rotation and zoom.</summary>
internal static class Wiggler
{
    public static void Apply(Wiggle w, List<Keyframe> frames, int fps)
    {
        if (!w.Enabled || frames.Count == 0) return;
        float freq = Mathf.Max(0.01f, w.Frequency);
        for (int f = 0; f < frames.Count; f++)
        {
            float t = f / (float)Mathf.Max(1, fps) * freq;
            Keyframe k = frames[f];
            if (w.Position != 0f)
                k.Position += new Vector3(Fbm(t, w.Seed * 7 + 1), Fbm(t, w.Seed * 7 + 2), Fbm(t, w.Seed * 7 + 3)) * w.Position;
            if (w.Rotation != 0f)
            {
                Quaternion shake = Quaternion.Euler(Fbm(t, w.Seed * 7 + 4) * w.Rotation, Fbm(t, w.Seed * 7 + 5) * w.Rotation,
                    Fbm(t, w.Seed * 7 + 6) * w.Rotation * 0.5f);
                k.Rotation = (Quaternion.Euler(k.Rotation) * shake).eulerAngles;
            }
            if (w.Zoom != 0f)
                k.FieldOfView += Fbm(t, w.Seed * 7 + 7) * w.Zoom;
            frames[f] = k;
        }
    }

    private static float Hash(int i, int seed)
    {
        unchecked
        {
            uint h = (uint)(i * 374761393 + seed * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0xFFFFFF * 2f - 1f;
        }
    }

    private static float Value(float x, int seed)
    {
        int i = Mathf.FloorToInt(x);
        float f = x - i;
        float u = f * f * f * (f * (f * 6f - 15f) + 10f);
        return Mathf.Lerp(Hash(i, seed), Hash(i + 1, seed), u);
    }

    /// <summary>Two octaves, like wiggle(): mostly the main frequency with a little finer jitter (-1..1).</summary>
    private static float Fbm(float x, int seed) => Value(x, seed) * 0.75f + Value(x * 2.13f + 17.3f, seed + 101) * 0.25f;
}

/// <summary>
/// Time helpers shared by the compiler and the editors: when each keyframe starts, and which compiled frame is at a
/// given time (the compiled animation has ceil(duration * FPS) frames per keyframe).
/// </summary>
public static class Timeline
{
    /// <summary>Start time (s) of every keyframe; the extra last entry is the end of the animation.</summary>
    public static float[] Starts(List<Keyframe> keys)
    {
        var s = new float[keys.Count + 1];
        for (int i = 0; i < keys.Count; i++)
            s[i + 1] = s[i] + Mathf.Max(0f, keys[i].Transition.Duration);
        return s;
    }

    /// <summary>
    /// Frames a transition lasts: duration x FPS rounded up, ignoring float noise (7 / 60 s is 7 frames, not 8;
    /// 0.1 s at 60 FPS is 6 frames).
    /// </summary>
    public static int FrameCount(float duration, int fps) => Math.Max(0, (int)Math.Ceiling(duration * fps - 1e-3));

    /// <summary>First compiled frame of every keyframe; the extra last entry is the total frame count.</summary>
    public static int[] FrameStarts(List<Keyframe> keys, int fps)
    {
        var f = new int[keys.Count + 1];
        for (int i = 0; i < keys.Count; i++)
            f[i + 1] = f[i] + FrameCount(keys[i].Transition.Duration, fps);
        return f;
    }

    /// <summary>The compiled frame shown at time t (s).</summary>
    public static int FrameAt(List<Keyframe> keys, int fps, float t)
    {
        float[] s = Starts(keys);
        int[] f = FrameStarts(keys, fps);
        int n = keys.Count;
        if (n == 0 || f[n] == 0) return 0;
        if (t <= 0f) return 0;
        for (int i = 0; i < n; i++)
        {
            if (t < s[i + 1] || i == n - 1)
            {
                int frames = f[i + 1] - f[i];
                if (frames <= 0) continue;
                float d = s[i + 1] - s[i];
                int j = d > 1e-6f ? Mathf.FloorToInt((t - s[i]) / d * frames + 1e-4f) : 0;
                return Mathf.Clamp(f[i] + j, 0, f[n] - 1);
            }
        }
        return f[n] - 1;
    }

    /// <summary>The time (s) of a compiled frame.</summary>
    public static float TimeOf(List<Keyframe> keys, int fps, int frame)
    {
        float[] s = Starts(keys);
        int[] f = FrameStarts(keys, fps);
        int n = keys.Count;
        for (int i = 0; i < n; i++)
        {
            if (frame < f[i + 1])
            {
                int frames = f[i + 1] - f[i];
                return s[i] + (frames > 0 ? (s[i + 1] - s[i]) * (frame - f[i]) / frames : 0f);
            }
        }
        return n > 0 ? s[n] : 0f;
    }
}
