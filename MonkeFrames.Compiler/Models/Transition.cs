namespace MonkeFrames.Compiler.Models;

/// <summary>
/// Transition data such as movement styling and duration.
/// </summary>
public struct Transition
{
    /// <summary>
    /// The type of transition to apply.
    /// </summary>
    public TransitionEffect Effect = TransitionEffect.Linear;

    /// <summary>
    /// The amount of time the transitioning lasts.
    /// </summary>
    public float Duration = 5f;

    /// <summary>
    /// Custom speed curve, as a cubic bezier from (0,0) to (1,1) with two control points
    /// (same idea as CSS cubic-bezier). X = time, Y = progress. Y may go below 0 or above 1 for
    /// anticipation/overshoot. Used by <see cref="TransitionEffect.Custom"/>, and by
    /// <see cref="TransitionEffect.Smooth"/> when <see cref="CustomSpeed"/> is on.
    /// </summary>
    public float CurveX1 = 0.25f;
    /// <inheritdoc cref="CurveX1"/>
    public float CurveY1 = 0.1f;
    /// <inheritdoc cref="CurveX1"/>
    public float CurveX2 = 0.25f;
    /// <inheritdoc cref="CurveX1"/>
    public float CurveY2 = 1f;

    /// <summary>
    /// For Smooth transitions: shape the speed along the spline with the custom curve.
    /// </summary>
    public bool CustomSpeed = false;

    /// <summary>
    /// `true` if this transition's timing comes from the editable curve.
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    public readonly bool UsesCurve => Effect == TransitionEffect.Custom || (Effect == TransitionEffect.Smooth && CustomSpeed);

    /// <summary>
    /// Create a new Transition.
    /// </summary>
    public Transition() { }

    /// <summary>
    /// The default transition.
    /// </summary>
    public static Transition Linear => new Transition
    {
        Effect = TransitionEffect.Linear,
        Duration = 5f
    };

}

/// <summary>
/// Transition style to apply.
/// </summary>
public enum TransitionEffect
{
    /// <summary>
    /// Basic direct-line transition.
    /// </summary>
    Linear = 0,

    /// <summary>
    /// The camera stays at the keyframe's position for the entire duration of the transition.
    /// </summary>
    Cut,

    /// <summary>
    /// The camera's movement delta is determined by a sine wave, applying slower movement to the tips of the transition.
    /// </summary>
    Sine,

    /// <summary>
    /// Smooth keyframes: the camera follows a curved spline through the keyframes and keeps its momentum
    /// instead of stopping at each one. Velocity is continuous across keyframes, even with different durations.
    /// How curvy the path is can be tuned with <see cref="Project.Smoothness"/>.
    /// </summary>
    Smooth,

    /// <summary>
    /// Starts slowly and accelerates into the next keyframe.
    /// </summary>
    EaseIn,

    /// <summary>
    /// Starts quickly and decelerates into the next keyframe.
    /// </summary>
    EaseOut,

    /// <summary>
    /// Straight path whose speed follows the editable speed curve (CurveX1..CurveY2).
    /// </summary>
    Custom
}