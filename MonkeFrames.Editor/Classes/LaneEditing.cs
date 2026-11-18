using System;
using System.Collections.Generic;
using MonkeFrames.Compiler;
using MonkeFrames.Compiler.Models;
using UnityEngine;
using Keyframe = MonkeFrames.Compiler.Models.Keyframe;

namespace MonkeFrames.Editor.Classes;

/// <summary>
/// After Effects style keyframe editing on top of MonkeFrames keyframes. A MonkeFrames keyframe is a moment in
/// time; each property (lane) can be keyed there or not. Adding a key to one property at any time splits the
/// keyframe timeline there (the new keyframe only keys that property, so everything else glides straight through).
/// </summary>
public static class LaneEditing
{
    public static readonly KeyLanes[] Lanes = { KeyLanes.Position, KeyLanes.Rotation, KeyLanes.Zoom, KeyLanes.TimeOfDay };

    public static string Name(KeyLanes lane) => lane switch
    {
        KeyLanes.Position => "Position",
        KeyLanes.Rotation => "Rotation",
        KeyLanes.Zoom => "Zoom",
        KeyLanes.TimeOfDay => "Time of Day",
        _ => lane.ToString(),
    };

    public static float FrameTime(Project p) => 1f / Mathf.Max(1, p.FPS);

    /// <summary>The animation without camera shake (what keys and handles are edited against).</summary>
    public static List<Keyframe> Clean { get; private set; } = new();

    /// <summary>Build the project: <see cref="Clean"/> without shake, then the real animation (with shake).</summary>
    public static void Build(Project p)
    {
        Wiggle w = p.Wiggle;
        if (w.Enabled)
        {
            var off = w; off.Enabled = false; p.Wiggle = off;
            Compiler.Compiler.Build(p).Wait();
            Clean = p.CompiledKeyframes;
            p.Wiggle = w;
            Compiler.Compiler.Build(p).Wait();
        }
        else
        {
            Compiler.Compiler.Build(p).Wait();
            Clean = p.CompiledKeyframes;
        }
    }

    /// <summary>The (shake-free) camera at time t.</summary>
    public static Keyframe Sample(Project p, float t)
    {
        if (Clean == null || Clean.Count == 0)
            return p.Keyframes.Count > 0 ? p.Keyframes[0] : new Keyframe();
        int f = Timeline.FrameAt(p.Keyframes, p.FPS, t);
        return Clean[Mathf.Clamp(f, 0, Clean.Count - 1)];
    }

    // ---- values of one property ----

    public static void CopyLane(ref Keyframe dst, Keyframe src, KeyLanes lanes)
    {
        if ((lanes & KeyLanes.Position) != 0) dst.Position = src.Position;
        if ((lanes & KeyLanes.Rotation) != 0) dst.Rotation = src.Rotation;
        if ((lanes & KeyLanes.Zoom) != 0) dst.FieldOfView = src.FieldOfView;
        if ((lanes & KeyLanes.TimeOfDay) != 0) dst.TimeOfDay = src.TimeOfDay;
    }

    /// <summary>The keyframe index keyed in <paramref name="lane"/> at time t (within half a frame), or -1.</summary>
    public static int KeyAt(Project p, KeyLanes lane, float t)
    {
        float[] s = Timeline.Starts(p.Keyframes);
        float half = FrameTime(p) * 0.5f;
        for (int i = 0; i < p.Keyframes.Count; i++)
            if (p.Keyframes[i].Has(lane) && Mathf.Abs(s[i] - t) <= half) return i;
        return -1;
    }

    public static int PrevKey(Project p, KeyLanes lane, int idx)
    {
        for (int i = idx - 1; i >= 0; i--) if (p.Keyframes[i].Has(lane)) return i;
        return -1;
    }

    public static int NextKey(Project p, KeyLanes lane, int idx)
    {
        for (int i = idx + 1; i < p.Keyframes.Count; i++) if (p.Keyframes[i].Has(lane)) return i;
        return -1;
    }

    public static int CountKeys(Project p, KeyLanes lane)
    {
        int c = 0;
        foreach (Keyframe k in p.Keyframes) if (k.Has(lane)) c++;
        return c;
    }

    /// <summary>
    /// Make sure <paramref name="lanes"/> are keyed at time t: the keyframe already there, or a new one that splits
    /// the timeline (it starts with the animation's values at t, so nothing visibly changes). Returns its index.
    /// </summary>
    public static int KeyAtTime(Project p, float t, KeyLanes lanes)
    {
        List<Keyframe> keys = p.Keyframes;
        if (keys.Count == 0) return -1;
        t = Mathf.Max(0f, t);
        float[] s = Timeline.Starts(keys);
        float half = FrameTime(p) * 0.5f;
        int n = keys.Count;
        for (int i = 0; i < n; i++)
            if (Mathf.Abs(s[i] - t) <= half)
            {
                Keyframe k = keys[i];
                k.Keys |= lanes;
                keys[i] = k;
                return i;
            }

        Keyframe here = Sample(p, t);
        int at;
        Keyframe nk;
        if (t > s[n - 1])
        {
            // after the last keyframe: the last one now moves on to the new one, which keeps the end time
            Keyframe last = keys[n - 1];
            float end = s[n];
            nk = Clone(last);
            last.Transition.Duration = t - s[n - 1];
            keys[n - 1] = last;
            nk.Transition.Duration = Mathf.Max(end - t, FrameTime(p));
            at = n;
        }
        else
        {
            int i = 0;
            while (i + 1 < n && s[i + 1] <= t) i++;
            Keyframe a = keys[i];
            nk = Clone(a);
            // split on a frame boundary of this transition, so the two halves have exactly as many frames as before
            float frame = FrameTime(p);
            float first = Mathf.Clamp(Mathf.Round((t - s[i]) / frame) * frame, frame, Mathf.Max(frame, s[i + 1] - s[i] - frame));
            nk.Transition.Duration = Mathf.Max(0f, a.Transition.Duration - first);
            a.Transition.Duration = first;
            t = s[i] + first;
            here = Sample(p, t);
            keys[i] = a;
            at = i + 1;
        }
        CopyLane(ref nk, here, KeyLanes.All);
        nk.Keys = lanes;
        keys.Insert(at, nk);
        return at;
    }

    /// <summary>A copy with a new identity (for a new keyframe).</summary>
    public static Keyframe Clone(Keyframe k)
    {
        k.GUID = Guid.NewGuid().ToString();
        k.Compiled = false;
        return k;
    }

    /// <summary>
    /// Remove the keys of <paramref name="lanes"/> from keyframe idx. A keyframe left with no keys (other than the
    /// first) is deleted, its time going to the keyframe before it, so nothing else moves in time.
    /// </summary>
    public static void Unkey(Project p, int idx, KeyLanes lanes)
    {
        List<Keyframe> keys = p.Keyframes;
        if (idx < 0 || idx >= keys.Count) return;
        Keyframe k = keys[idx];
        k.Keys &= ~lanes;
        keys[idx] = k;
        if (k.Keys == KeyLanes.None && idx > 0)
            RemoveKeepTime(p, idx);
    }

    /// <summary>Delete keyframe idx (not the first), giving its time to the one before it.</summary>
    public static void RemoveKeepTime(Project p, int idx)
    {
        List<Keyframe> keys = p.Keyframes;
        if (idx <= 0 || idx >= keys.Count) return;
        Keyframe prev = keys[idx - 1];
        prev.Transition.Duration += keys[idx].Transition.Duration;
        keys[idx - 1] = prev;
        keys.RemoveAt(idx);
    }

    /// <summary>
    /// Move keyframes in time by dt (seconds), keeping every other keyframe where it is. Keyframes can't pass their
    /// neighbours (dt is limited) and the first keyframe stays at 0. Returns the dt actually applied.
    /// </summary>
    public static float Shift(Project p, HashSet<int> moving, float[] startsBefore, float dt)
    {
        List<Keyframe> keys = p.Keyframes;
        int n = keys.Count;
        if (n == 0 || moving.Count == 0) return 0f;
        float frame = FrameTime(p);
        // limit dt so no moving keyframe passes a fixed one
        for (int i = 0; i < n; i++)
        {
            if (!moving.Contains(i)) continue;
            if (i == 0) return 0f;
            if (dt > 0)
            {
                int j = i + 1;
                while (j < n && moving.Contains(j)) j++;
                if (j < n) dt = Mathf.Min(dt, startsBefore[j] - startsBefore[i]);
            }
            else
            {
                int j = i - 1;
                while (j >= 0 && moving.Contains(j)) j--;
                if (j >= 0) dt = Mathf.Max(dt, startsBefore[j] - startsBefore[i]);
            }
        }
        dt = Mathf.Round(dt / frame) * frame;
        var t = new float[n];
        for (int i = 0; i < n; i++) t[i] = startsBefore[i] + (moving.Contains(i) ? dt : 0f);
        for (int i = 0; i + 1 < n; i++)
        {
            Keyframe k = keys[i];
            k.Transition.Duration = Mathf.Max(0f, t[i + 1] - t[i]);
            keys[i] = k;
        }
        return dt;
    }

    /// <summary>
    /// After keys were dragged: keyframes that ended up at the same moment with different properties keyed become
    /// one keyframe again, and empty keyframes are removed.
    /// </summary>
    public static void Tidy(Project p)
    {
        List<Keyframe> keys = p.Keyframes;
        float half = FrameTime(p) * 0.5f;
        for (int i = keys.Count - 1; i >= 1; i--)
            if (keys[i].Keys == KeyLanes.None) RemoveKeepTime(p, i);
        for (int i = 0; i + 1 < keys.Count; i++)
        {
            Keyframe a = keys[i], b = keys[i + 1];
            if (a.Transition.Duration > half || (a.Keys & b.Keys) != 0) continue;
            // b joins a (a is at the same time): b's keyed values and timing move into a
            CopyLane(ref a, b, b.Keys);
            foreach (KeyLanes lane in Lanes)
                if (b.Has(lane)) a.Transition.SetLane(lane, b.Transition.Lane(lane));
            a.Keys |= b.Keys;
            a.Transition.Duration = b.Transition.Duration;
            keys[i] = a;
            keys.RemoveAt(i + 1);
            i--;
        }
    }

    /// <summary>
    /// Keyframes that don't key a property still store a value for it: keep it equal to the animation there, so
    /// orbs, "Go to keyframe" and the classic editor show where the camera really is.
    /// </summary>
    public static bool SyncUnkeyed(Project p)
    {
        if (Clean == null || Clean.Count == 0) return false;
        List<Keyframe> keys = p.Keyframes;
        int[] fs = Timeline.FrameStarts(keys, p.FPS);
        bool changed = false;
        for (int i = 0; i < keys.Count; i++)
        {
            Keyframe k = keys[i];
            if (k.Keys == KeyLanes.All) continue;
            Keyframe at = Clean[Mathf.Clamp(fs[i], 0, Clean.Count - 1)];
            if (!k.Has(KeyLanes.Position) && (k.Position - at.Position).sqrMagnitude > 1e-8f) { k.Position = at.Position; changed = true; }
            if (!k.Has(KeyLanes.Rotation) && Quaternion.Angle(k.QuatRotation, at.QuatRotation) > 0.01f) { k.Rotation = at.Rotation; changed = true; }
            if (!k.Has(KeyLanes.Zoom) && Mathf.Abs(k.FieldOfView - at.FieldOfView) > 1e-4f) { k.FieldOfView = at.FieldOfView; changed = true; }
            if (!k.Has(KeyLanes.TimeOfDay) && Mathf.Abs(k.TimeOfDay - at.TimeOfDay) > 1e-4f) { k.TimeOfDay = at.TimeOfDay; changed = true; }
            keys[i] = k;
        }
        return changed;
    }

    // ---- timing (temporal interpolation) ----

    /// <summary>The timing a lane uses from keyframe idx to its next key.</summary>
    public static LaneCurve Out(Project p, KeyLanes lane, int idx) => p.Keyframes[idx].Transition.EffectiveLane(lane);

    public static void SetOut(Project p, KeyLanes lane, int idx, LaneCurve c)
    {
        if (idx < 0 || idx >= p.Keyframes.Count) return;
        Keyframe k = p.Keyframes[idx];
        k.Transition.SetLane(lane, c);
        p.Keyframes[idx] = k;
    }

    /// <summary>Change how the value leaves keyframe idx (its outgoing handle).</summary>
    public static void EditOut(Project p, KeyLanes lane, int idx, Func<LaneCurve, LaneCurve> edit)
    {
        if (idx < 0 || NextKey(p, lane, idx) < 0) return;
        SetOut(p, lane, idx, edit(Out(p, lane, idx)));
    }

    /// <summary>Change how the value arrives at keyframe idx (the incoming handle, stored on the previous key).</summary>
    public static void EditIn(Project p, KeyLanes lane, int idx, Func<LaneCurve, LaneCurve> edit)
    {
        int prev = PrevKey(p, lane, idx);
        if (prev < 0) return;
        SetOut(p, lane, prev, edit(Out(p, lane, prev)));
    }

    /// <summary>Turn any curve into an editable bezier (linear / auto become linear handles).</summary>
    public static LaneCurve AsBezier(LaneCurve c)
    {
        if (c.Interp == LaneInterp.Bezier) return c;
        return new LaneCurve { Interp = LaneInterp.Bezier, X1 = 1f / 3f, Y1 = 1f / 3f, X2 = 2f / 3f, Y2 = 2f / 3f };
    }

    public static LaneCurve EaseOut(LaneCurve c) { c = AsBezier(c); c.X1 = 1f / 3f; c.Y1 = 0f; return c; }
    public static LaneCurve EaseIn(LaneCurve c) { c = AsBezier(c); c.X2 = 2f / 3f; c.Y2 = 1f; return c; }
    public static LaneCurve LinearOut(LaneCurve c) { c = AsBezier(c); c.X1 = 1f / 3f; c.Y1 = 1f / 3f; return Simplify(c); }
    public static LaneCurve LinearIn(LaneCurve c) { c = AsBezier(c); c.X2 = 2f / 3f; c.Y2 = 2f / 3f; return Simplify(c); }

    /// <summary>Bezier with straight handles on both ends is just linear.</summary>
    public static LaneCurve Simplify(LaneCurve c)
    {
        if (c.Interp == LaneInterp.Bezier && Mathf.Abs(c.X1 - c.Y1) < 1e-4f && Mathf.Abs(c.X2 - c.Y2) < 1e-4f)
            return LaneCurve.Linear;
        return c;
    }

    /// <summary>Shape of a key icon half: how the value leaves / arrives at a key.</summary>
    public static Editor.UI.Draw2D.KeyShape OutShape(LaneCurve c) => c.Interp switch
    {
        LaneInterp.Hold => Editor.UI.Draw2D.KeyShape.Square,
        LaneInterp.Auto => Editor.UI.Draw2D.KeyShape.Circle,
        LaneInterp.Linear => Editor.UI.Draw2D.KeyShape.Diamond,
        _ => Mathf.Abs(c.X1 - c.Y1) < 0.02f ? Editor.UI.Draw2D.KeyShape.Diamond : Editor.UI.Draw2D.KeyShape.Hourglass,
    };

    public static Editor.UI.Draw2D.KeyShape InShape(LaneCurve c) => c.Interp switch
    {
        LaneInterp.Hold => Editor.UI.Draw2D.KeyShape.Diamond,
        LaneInterp.Auto => Editor.UI.Draw2D.KeyShape.Circle,
        LaneInterp.Linear => Editor.UI.Draw2D.KeyShape.Diamond,
        _ => Mathf.Abs(c.X2 - c.Y2) < 0.02f ? Editor.UI.Draw2D.KeyShape.Diamond : Editor.UI.Draw2D.KeyShape.Hourglass,
    };

    /// <summary>A description of a curve end, like After Effects' Keyframe Velocity (influence, speed).</summary>
    public static string Describe(LaneCurve c, bool outgoing)
    {
        switch (c.Interp)
        {
            case LaneInterp.Hold: return outgoing ? "Hold" : "Linear";
            case LaneInterp.Auto: return "Auto Bezier";
            case LaneInterp.Linear: return "Linear";
        }
        float infl = outgoing ? c.X1 : 1f - c.X2;
        float speed = outgoing ? (c.X1 > 1e-4f ? c.Y1 / c.X1 : 0f) : (1f - c.X2 > 1e-4f ? (1f - c.Y2) / (1f - c.X2) : 0f);
        if (speed < 0.02f && Mathf.Abs(infl - 1f / 3f) < 0.01f) return "Easy Ease";
        return $"Bezier {infl * 100f:0}% influence, {speed * 100f:0}% speed";
    }
}
