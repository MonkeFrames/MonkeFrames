using System;

namespace MonkeFrames.Compiler.Models;

/// <summary>
/// The animatable properties of the camera, each with its own keyframes in the Advanced Keyframe Editor
/// (like the properties of a layer in After Effects).
/// </summary>
[Flags]
public enum KeyLanes
{
    /// <summary>No property is keyed at this keyframe.</summary>
    None = 0,
    /// <summary>Camera position.</summary>
    Position = 1,
    /// <summary>Camera rotation (orientation).</summary>
    Rotation = 2,
    /// <summary>Zoom (field of view).</summary>
    Zoom = 4,
    /// <summary>Time of day of the photoreal sky.</summary>
    TimeOfDay = 8,
    /// <summary>Every property (a normal MonkeFrames keyframe).</summary>
    All = Position | Rotation | Zoom | TimeOfDay,
}

/// <summary>How a property's value moves from one of its keyframes to the next (After Effects' temporal
/// interpolation).</summary>
public enum LaneInterp
{
    /// <summary>Constant speed.</summary>
    Linear = 0,
    /// <summary>Speed follows the bezier handles (influence and speed in and out, like the Graph Editor).</summary>
    Bezier = 1,
    /// <summary>The value holds until the next keyframe, then jumps.</summary>
    Hold = 2,
    /// <summary>Auto bezier: a smooth path through the keyframes that keeps its momentum.</summary>
    Auto = 3,
}

/// <summary>
/// One property's timing between two of its keyframes: a cubic bezier speed curve from (0,0) to (1,1)
/// (X = time, Y = progress). (X1, Y1) is the outgoing handle of the first keyframe, (X2, Y2) the incoming
/// handle of the next: X1 / (1 - X2) are the influences, Y1 / X1 and (1 - Y2) / (1 - X2) the speeds relative
/// to the average speed.
/// </summary>
public struct LaneCurve
{
    /// <summary>Interpolation type.</summary>
    public LaneInterp Interp = LaneInterp.Bezier;
    /// <summary>Outgoing handle (time).</summary>
    public float X1 = 1f / 3f;
    /// <summary>Outgoing handle (progress).</summary>
    public float Y1 = 1f / 3f;
    /// <summary>Incoming handle (time).</summary>
    public float X2 = 2f / 3f;
    /// <summary>Incoming handle (progress).</summary>
    public float Y2 = 2f / 3f;

    /// <summary>Create a linear lane curve.</summary>
    [Newtonsoft.Json.JsonConstructor]
    public LaneCurve() { }

    /// <summary>Progress (0..1, may overshoot) at linear time <paramref name="u"/> (0..1).</summary>
    public readonly float Evaluate(float u) => Interp switch
    {
        LaneInterp.Hold => u >= 1f ? 1f : 0f,
        LaneInterp.Linear => u,
        LaneInterp.Auto => u,
        _ => Easing.Bezier(X1, Y1, X2, Y2, u),
    };

    /// <summary>After Effects' Easy Ease (33.33% influence, speed 0, both ends).</summary>
    public static LaneCurve EasyEase => new LaneCurve { Interp = LaneInterp.Bezier, X1 = 1f / 3f, Y1 = 0f, X2 = 2f / 3f, Y2 = 1f };

    /// <summary>Constant speed.</summary>
    public static LaneCurve Linear => new LaneCurve { Interp = LaneInterp.Linear };

    /// <summary>The curve that gives the same timing as a MonkeFrames transition (to start editing from).</summary>
    public static LaneCurve From(Transition t)
    {
        if (t.Effect == TransitionEffect.Cut) return new LaneCurve { Interp = LaneInterp.Hold };
        if (t.Effect == TransitionEffect.Smooth && !t.CustomSpeed) return new LaneCurve { Interp = LaneInterp.Auto };
        if (t.UsesCurve) return new LaneCurve { Interp = LaneInterp.Bezier, X1 = t.CurveX1, Y1 = t.CurveY1, X2 = t.CurveX2, Y2 = t.CurveY2 };
        return t.Effect switch
        {
            TransitionEffect.Sine => new LaneCurve { Interp = LaneInterp.Bezier, X1 = 0.364f, Y1 = 0f, X2 = 0.636f, Y2 = 1f },
            TransitionEffect.EaseIn => new LaneCurve { Interp = LaneInterp.Bezier, X1 = 0.55f, Y1 = 0f, X2 = 0.9f, Y2 = 0.6f },
            TransitionEffect.EaseOut => new LaneCurve { Interp = LaneInterp.Bezier, X1 = 0.1f, Y1 = 0.4f, X2 = 0.45f, Y2 = 1f },
            _ => Linear,
        };
    }
}

/// <summary>
/// Handheld camera shake, like After Effects' wiggle(frequency, amplitude) expression. Applied on top of the
/// animation, the same every time the project is built (seeded).
/// </summary>
public struct Wiggle
{
    /// <summary>Shake on / off.</summary>
    public bool Enabled = false;
    /// <summary>Wiggles per second.</summary>
    public float Frequency = 1.5f;
    /// <summary>How far the camera drifts (metres).</summary>
    public float Position = 0.015f;
    /// <summary>How much the camera turns (degrees).</summary>
    public float Rotation = 0.6f;
    /// <summary>How much the zoom breathes (degrees of field of view).</summary>
    public float Zoom = 0f;
    /// <summary>Random seed (a different seed gives a different shake).</summary>
    public int Seed = 1;

    /// <summary>Create the default (off) shake.</summary>
    [Newtonsoft.Json.JsonConstructor]
    public Wiggle() { }
}
