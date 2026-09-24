using System.IO;
using MonkeFrames.Editor.Classes.NewtonsoftConverters;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Utilities;
using Newtonsoft.Json;
using UnityEngine;

namespace MonkeFrames.Editor.Classes;

public class Settings
{
    public static Settings current;

    public Color AccentColor = Color.blue;
    public bool Autosave = true;

    /// <summary>Play the animated logo intro when MonkeFrames loads.</summary>
    public bool ShowIntro = true;

    /// <summary>Play a chime when a notification pops up.</summary>
    public bool NotificationSound = true;

    /// <summary>Notification chime volume, 0..1.</summary>
    public float NotificationVolume = 0.5f;

    /// <summary>Play UI transitions (window open/close, menus, hovers).</summary>
    public bool Animations = true;

    /// <summary>Multiplier for UI animation speed (0.5 = slower, 2 = faster).</summary>
    public float AnimationSpeed = 1f;

    /// <summary>New keyframes use the Smooth transition instead of Linear.</summary>
    public bool SmoothByDefault = false;

    /// <summary>Smooth (cinematic) mouse look. Toggled with Caps Lock.</summary>
    public bool SmoothMouseLook = false;

    /// <summary>How much mouse look is smoothed, 0 (light) to 1 (heavy, very floaty).</summary>
    public float MouseSmoothing = 0.5f;

    /// <summary>Let other MonkeFrames users see a camera model where your camera is.</summary>
    public bool ShareMyCamera = true;

    /// <summary>Show other MonkeFrames users' cameras.</summary>
    public bool ShowOtherCameras = true;

    /// <summary>Show recorded spectator cameras when watching replays.</summary>
    public bool ShowCamerasInReplays = true;

    // ---- Post Processing (MonkeFrames camera only) ----
    public bool Bloom = false;
    public float BloomIntensity = 0.8f;
    public float BloomThreshold = 0.5f;
    public float BloomSize = 0.65f;
    public float BloomScatter = 0.6f;
    public Color BloomTint = Color.white;

    public bool ChromaticAberration = false;
    public float ChromaticIntensity = 0.35f;

    public bool GlobalMotionBlur = false;
    public float GlobalMotionBlurStrength = 0.5f;
    public int MotionBlurSamples = 6;

    public bool DepthOfField = false;
    public bool DofAutoFocus = true;
    public float DofFocus = 3f;
    public float DofAperture = 0.35f;
    public int DofSamples = 12;
    public float DofFocusSpeed = 0.5f;

    public bool ColorGrading = false;
    public float Exposure = 0f;        // stops
    public float Contrast = 1f;
    public float Temperature = 0f;     // -1 cool .. 1 warm
    public float Tint = 0f;            // -1 green .. 1 magenta

    public bool Vignette = false;
    public float VignetteIntensity = 0.4f;
    public float VignetteSmoothness = 0.5f;
    public bool VignetteRounded = true;
    public Color VignetteColor = Color.black;

    public bool FilmGrain = false;
    public float GrainIntensity = 0.25f;
    public float GrainSize = 0.4f;

    public bool Letterbox = false;
    public float LetterboxAspect = 2.39f;

    public void ResetPostProcessing()
    {
        Bloom = false; BloomIntensity = 0.8f; BloomThreshold = 0.5f; BloomSize = 0.65f; BloomScatter = 0.6f; BloomTint = Color.white;
        ChromaticAberration = false; ChromaticIntensity = 0.35f;
        GlobalMotionBlur = false; GlobalMotionBlurStrength = 0.5f; MotionBlurSamples = 6;
        DepthOfField = false; DofAutoFocus = true; DofFocus = 3f; DofAperture = 0.35f; DofSamples = 12; DofFocusSpeed = 0.5f;
        ColorGrading = false; Exposure = 0f; Contrast = 1f; Temperature = 0f; Tint = 0f;
        Vignette = false; VignetteIntensity = 0.4f; VignetteSmoothness = 0.5f; VignetteRounded = true; VignetteColor = Color.black;
        FilmGrain = false; GrainIntensity = 0.25f; GrainSize = 0.4f;
        Letterbox = false; LetterboxAspect = 2.39f;
    }

    /// <summary>One-click looks for the Post Processing window.</summary>
    public void ApplyPostPreset(string name)
    {
        ResetPostProcessing();
        switch (name)
        {
            case "Cinematic":
                Bloom = true; BloomIntensity = 0.6f; BloomThreshold = 0.6f;
                ColorGrading = true; Contrast = 1.15f; Temperature = 0.15f; Exposure = -0.1f;
                Vignette = true; VignetteIntensity = 0.45f;
                Letterbox = true; LetterboxAspect = 2.39f;
                FilmGrain = true; GrainIntensity = 0.15f;
                GlobalMotionBlur = true; GlobalMotionBlurStrength = 0.4f;
                break;
            case "Dreamy":
                Bloom = true; BloomIntensity = 1.4f; BloomThreshold = 0.3f; BloomSize = 0.9f; BloomScatter = 0.8f;
                BloomTint = new Color(1f, 0.85f, 0.95f);
                ColorGrading = true; Contrast = 0.9f; Exposure = 0.2f; Tint = 0.15f;
                Vignette = true; VignetteIntensity = 0.3f; VignetteSmoothness = 0.9f;
                break;
            case "Action":
                Bloom = true; BloomIntensity = 0.7f;
                ChromaticAberration = true; ChromaticIntensity = 0.4f;
                GlobalMotionBlur = true; GlobalMotionBlurStrength = 0.8f;
                ColorGrading = true; Contrast = 1.25f; Temperature = -0.1f;
                Vignette = true; VignetteIntensity = 0.5f;
                break;
            case "Retro":
                ChromaticAberration = true; ChromaticIntensity = 0.6f;
                ColorGrading = true; Contrast = 1.1f; Temperature = 0.35f; Exposure = -0.2f;
                Vignette = true; VignetteIntensity = 0.6f; VignetteSmoothness = 0.7f;
                FilmGrain = true; GrainIntensity = 0.45f; GrainSize = 0.7f;
                break;
        }
    }

    public static void Load()
    {
        var settings = new JsonSerializerSettings
        {
            Converters = { new ColorConverter() }
        };

        if (!File.Exists(SystemUtilities.Combine(Constants.DataFolder, "config.json")))
        {
            current = new Settings();
            return;
        }

        string prefs = File.ReadAllText(SystemUtilities.Combine(Constants.DataFolder, "config.json"));
        current = JsonConvert.DeserializeObject<Settings>(prefs, settings) ?? new Settings();
    }

    public static void Save()
    {
        var settings = new JsonSerializerSettings
        {
            Converters = { new ColorConverter() },
            Formatting = Formatting.Indented,
        };

        string json = JsonConvert.SerializeObject(current, settings);

        Directory.CreateDirectory(Constants.DataFolder);
        File.WriteAllText(SystemUtilities.Combine(Constants.DataFolder, "config.json"), json);

        KeyframeManager.Instance.RefreshOrbs();
    }
}