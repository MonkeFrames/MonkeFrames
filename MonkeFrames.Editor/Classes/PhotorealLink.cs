using UnityEngine;

namespace MonkeFrames.Editor.Classes;

/// <summary>
/// Photoreal (the separate graphics mod) and MonkeFrames work together through a few shader globals, so neither
/// needs the other installed: MonkeFrames tells it the time of day of its keyframes, Photoreal tells MonkeFrames
/// whether it's drawing the camera image, whether it does the motion blur, and the hour its sky shows.
/// </summary>
public static class PhotorealLink
{
    private static readonly int ActiveId = Shader.PropertyToID("_PRActive");
    private static readonly int PostBlurId = Shader.PropertyToID("_PRPostBlur");
    private static readonly int ShownHourId = Shader.PropertyToID("_PRShownHour");
    private static readonly int KeyframeHourId = Shader.PropertyToID("_PRKeyframeHour");

    /// <summary>Time of day from the keyframes (hours 0-24) while a keyframed animation plays or a keyframe is
    /// shown; -1 = none (Photoreal uses its own time).</summary>
    public static float KeyframeHour
    {
        get => Shader.GetGlobalFloat(KeyframeHourId) - 1f;
        set => Shader.SetGlobalFloat(KeyframeHourId, value < 0f ? 0f : value + 1f);
    }

    /// <summary>Photoreal is drawing the camera image.</summary>
    public static bool Active => Shader.GetGlobalFloat(ActiveId) > 0.5f;

    /// <summary>Photoreal blurs the finished image itself (so MonkeFrames doesn't re-render for motion blur).</summary>
    public static bool PostBlur => Shader.GetGlobalFloat(PostBlurId) > 0.5f;

    /// <summary>The hour Photoreal's sky shows by its own settings (ignoring keyframes); -1 = Photoreal isn't running.</summary>
    public static float ShownHour => Shader.GetGlobalFloat(ShownHourId) - 1f;
}
