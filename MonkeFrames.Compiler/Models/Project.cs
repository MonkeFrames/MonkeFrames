using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MonkeFrames.Compiler.Models;

/// <summary>
/// Projects hold metadata for an animation.
/// </summary>
public class Project
{
    /// <summary>
    /// Display name of the project.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// The version of the MonkeFrames compiler that the project was exported with.
    /// </summary>
    public string Version { get; internal set; }

    /// <summary>
    /// The exporter that managed the generation of the project.
    /// </summary>
    public Exporter Exporter { get; internal set; }

    /// <summary>
    /// Keyframes associated with the project.
    /// </summary>
    public List<Keyframe> Keyframes { get; set; }

    /// <summary>
    /// Spawned 3D objects associated with the project.
    /// </summary>
    public List<PlacedObject> PlacedObjects { get; set; }

    private int _fps = 60;

    /// <summary>
    /// Frames-per-second generated during compilation.
    /// </summary>
    public int FPS {
        get => _fps;
        set {
            if (Array.IndexOf(SupportedFPS, value) >= 0)
                _fps = value;
            else
                throw new ArgumentException($"FPS must be one of: {string.Join(", ", SupportedFPS)}.", nameof(value));
        }
    }

    /// <summary>
    /// Frame rates a project can use.
    /// </summary>
    public static readonly int[] SupportedFPS = [24, 30, 48, 50, 60, 90, 120, 144, 165, 240, 300, 360];

    private float _smoothness = 1f;

    /// <summary>
    /// The amount of smoothing applied to compiled keyframes.
    /// </summary>
    public float Smoothness
    {
        get => _smoothness;
        set => _smoothness = Math.Clamp(value, 0f, 1.5f);
    }

    /// <summary>
    /// Path to an audio file (.mp3, .wav, .ogg) to play along with the animation.
    /// </summary>
    public string AudioPath { get; set; }

    /// <summary>
    /// Start time offset in seconds for the audio file.
    /// </summary>
    public float AudioStartTime { get; set; }

    /// <summary>
    /// Auto smooth: the camera follows a smooth curve through all keyframes instead of straight lines with corners,
    /// and its turning and zoom are smoothed the same way - while every transition keeps its own timing exactly
    /// (its effect or custom speed graph decides how far along the curve the camera is), and every keyframe is hit
    /// exactly.
    /// </summary>
    public bool AutoSmooth { get; set; } = false;

    /// <summary>
    /// How strong <see cref="AutoSmooth"/> is: 0 = just rounds off the corners at keyframes, 1 = wide, sweeping curves.
    /// </summary>
    public float AutoSmoothStrength
    {
        get => field;
        set => field = Math.Clamp(value, 0f, 1f);
    } = 0.7f;

    /// <summary>
    /// Handheld camera shake on top of the animation (After Effects' wiggle()).
    /// </summary>
    public Wiggle Wiggle { get; set; } = new Wiggle();

    /// <summary>A number that changes whenever anything that affects the compiled animation changes.</summary>
    public int AnimationHash()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + FPS;
            h = h * 31 + Smoothness.GetHashCode();
            h = h * 31 + (AutoSmooth ? 1 : 0);
            h = h * 31 + AutoSmoothStrength.GetHashCode();
            Wiggle w = Wiggle;
            h = h * 31 + (w.Enabled ? 1 : 0);
            h = h * 31 + w.Frequency.GetHashCode();
            h = h * 31 + w.Position.GetHashCode();
            h = h * 31 + w.Rotation.GetHashCode();
            h = h * 31 + w.Zoom.GetHashCode();
            h = h * 31 + w.Seed;
            if (Keyframes != null)
                foreach (Keyframe k in Keyframes)
                    h = h * 31 + k.AnimationHash();
            return h;
        }
    }

    /// <summary>
    /// A list of built keyframes for the project for use with cameras.
    /// </summary>
    [JsonIgnore]
    public List<Keyframe> CompiledKeyframes;

    /// <summary>
    /// Represents if the project has been compiled.
    /// </summary>
    [JsonIgnore]
    public bool IsCompiled;

    /// <summary>
    /// Build the project. Shorthand for Compiler.Build(p)
    /// </summary>
    public async Task Build(Action<string> onStatusUpdate = null) => await Compiler.Build(this);

    /// <summary>
    /// Convert the project into savable JSON data. Shorthand for Compiler.ConvertToJson(p)
    /// </summary>
    public string ToJson() => Compiler.ConvertToJson(this);

    /// <summary>
    /// Loads a project from JSON data. Shorthand for Compiler.ConvertFromJson(p)
    /// </summary>
    public static Project FromJson(string json) => Compiler.ConvertFromJson(json);

    /// <summary>
    /// Create a new project.
    /// </summary>
    public Project(string projectName, Exporter projectExporter)
    {
        Name = projectName;
        Version = Constants.Version;
        Exporter = projectExporter;
        FPS = 60;
        Keyframes = new List<Keyframe>();
        PlacedObjects = new List<PlacedObject>();
    }
}