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
    public float Smoothness
    {
        get => _smoothness;
        set => _smoothness = Math.Clamp(value, 0f, 1.5f);
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