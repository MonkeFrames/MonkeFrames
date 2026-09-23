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