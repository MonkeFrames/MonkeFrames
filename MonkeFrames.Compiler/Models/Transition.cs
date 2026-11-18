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

    /// <summary>Position's own timing to its next keyframe (Advanced Keyframe Editor). Null = this transition's.</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public LaneCurve? PositionCurve = null;
    /// <summary>Rotation's own timing to its next keyframe. Null = this transition's.</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public LaneCurve? RotationCurve = null;
    /// <summary>Zoom's own timing to its next keyframe. Null = this transition's.</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public LaneCurve? ZoomCurve = null;
    /// <summary>Time of day's own timing to its next keyframe. Null = this transition's.</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public LaneCurve? TimeCurve = null;

    /// <summary>`true` if any property has its own timing here.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public readonly bool HasLaneCurves => PositionCurve.HasValue || RotationCurve.HasValue || ZoomCurve.HasValue || TimeCurve.HasValue;

    /// <summary>A property's own timing (null = the transition's).</summary>
    public readonly LaneCurve? Lane(KeyLanes lane) => lane switch
    {
        KeyLanes.Position => PositionCurve,
        KeyLanes.Rotation => RotationCurve,
        KeyLanes.Zoom => ZoomCurve,
        KeyLanes.TimeOfDay => TimeCurve,
        _ => null,
    };

    /// <summary>Set a property's own timing (null = use the transition's).</summary>
    public void SetLane(KeyLanes lane, LaneCurve? curve)
    {
        switch (lane)
        {
            case KeyLanes.Position: PositionCurve = curve; break;
            case KeyLanes.Rotation: RotationCurve = curve; break;
            case KeyLanes.Zoom: ZoomCurve = curve; break;
            case KeyLanes.TimeOfDay: TimeCurve = curve; break;
        }
    }

    /// <summary>The timing a property actually uses (its own, or the one made from this transition).</summary>
    public readonly LaneCurve EffectiveLane(KeyLanes lane) => Lane(lane) ?? LaneCurve.From(this);

    /// <summary>A number that changes whenever the timing changes.</summary>
    public readonly int AnimationHash()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (int)Effect;
            h = h * 31 + Duration.GetHashCode();
            h = h * 31 + CurveX1.GetHashCode();
            h = h * 31 + CurveY1.GetHashCode();
            h = h * 31 + CurveX2.GetHashCode();
            h = h * 31 + CurveY2.GetHashCode();
            h = h * 31 + (CustomSpeed ? 1 : 0);
            h = h * 31 + Hash(PositionCurve);
            h = h * 31 + Hash(RotationCurve);
            h = h * 31 + Hash(ZoomCurve);
            h = h * 31 + Hash(TimeCurve);
            return h;
        }

        static int Hash(LaneCurve? c) => !c.HasValue ? 0 : unchecked(((((int)c.Value.Interp + 3) * 397 ^ c.Value.X1.GetHashCode()) * 397
            ^ c.Value.Y1.GetHashCode()) * 397 ^ c.Value.X2.GetHashCode() * 31 ^ c.Value.Y2.GetHashCode() * 7);
    }

    /// <summary>
    /// Create a new Transition.
    /// </summary>
    [Newtonsoft.Json.JsonConstructor]
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