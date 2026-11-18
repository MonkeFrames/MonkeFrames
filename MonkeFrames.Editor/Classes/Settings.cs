using System.Collections.Generic;
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

    public Color AccentColor = DefaultAccent;

    /// <summary>The default accent colour (yellow).</summary>
    public static readonly Color DefaultAccent = new Color(1f, 0.80f, 0.12f, 1f);

    /// <summary>Settings layout version (older saves get one-time updates).</summary>
    public int UiVersion = 0;
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

    /// <summary>Saved options shown in the Sources window.</summary>
    public SourcesSettings Sources = new();

    /// <summary>Free cam tilt: sticks at level, and glides to level when you let go close to it.</summary>
    public bool AutoLevel = true;
    public float LevelSnapAngle = 3f;    // detent around 0° while tilting
    public float AutoLevelAngle = 6f;    // let go within this many degrees = glide to level
    /// <summary>Double right-click levels the horizon.</summary>
    public bool DoubleClickLevel = true;

    /// <summary>Free cam (WASD / Q / E) speed in metres per second, the same at any frame rate.</summary>
    public float CameraSpeed = 3f;
    /// <summary>Hold Shift: this many times faster.</summary>
    public float CameraFastMultiplier = 4f;
    /// <summary>Hold Ctrl: this fraction of the speed (precise moves).</summary>
    public float CameraSlowMultiplier = 0.2f;
    /// <summary>The free cam speeds up and slows down smoothly instead of starting / stopping instantly.</summary>
    public bool CameraSmoothMove = true;
    /// <summary>When you load into another map (your gorilla is teleported far away), the free camera comes with you.</summary>
    public bool CameraFollowToNewMap = true;

    /// <summary>Let other MonkeFrames users see a camera model where your camera is.</summary>
    public bool ShareMyCamera = true;

    /// <summary>Show other MonkeFrames users' cameras.</summary>
    public bool ShowOtherCameras = true;

    /// <summary>Show recorded spectator cameras when watching replays.</summary>
    public bool ShowCamerasInReplays = true;

    /// <summary>Wearable cosmetics on replay gorillas are the real game cosmetic (they animate and react) instead of a still copy.</summary>
    public bool ReplayLiveCosmetics = true;

    /// <summary>Desktop (no headset): the gorilla floats instead of falling.</summary>
    public bool DesktopNoGravity = true;
    /// <summary>Desktop (no headset): never get kicked for being AFK.</summary>
    public bool DesktopAntiKick = true;

    /// <summary>Recolour the spectator camera's logo and glow with each player's gorilla colour.</summary>
    public bool TintCameraLogo = false;

    /// <summary>Use the newer spectator camera model (from MonkeFrames 2.1 dev) instead of the classic glowing one.</summary>
    public bool NewCameraModel = false;

    /// <summary>In VR: see your camera, and grab cameras (yours, and other MonkeFrames users' if they allow it) with the grip.</summary>
    public bool VRCameraGrab = true;
    /// <summary>Other MonkeFrames users in VR may pick up and move your camera.</summary>
    public bool LetOthersGrabMyCamera = true;
    /// <summary>A grabbed camera turns to look at whoever holds it (off = it points where the hand points).</summary>
    public bool VRGrabLooksAtHolder = true;
    /// <summary>Hold a grabbed camera against someone's head for 3 seconds to put it on them (first person).</summary>
    public bool VRHeadAttach = true;
    /// <summary>Holding a grabbed camera against your own face for 3 s puts it in first person on you.</summary>
    public bool VRSelfAttach = true;

    /// <summary>Start recording a replay whenever you join a lobby; stop and save when you leave.</summary>
    public bool AutoRecordReplays = false;

    // ---- Tag effects (neon explosion when someone gets tagged) ----
    public bool TagFX = true;
    public int TagFXStyle = 0;          // 0 neon burst, 1 explosion (fireball, smoke, debris, boom)
    public int TagFXWho = 0;            // 0 everyone, 1 only me, 2 only others
    public int TagFXColorMode = 0;      // 0 gorilla colour, 1 custom, 2 rainbow
    public Color TagFXColor = new Color(1f, 0.1f, 0.6f);
    public Color TagFXColor2 = new Color(0.3f, 0.95f, 1f);
    public float TagFXSize = 1f;
    public float TagFXDuration = 1.2f;
    public float TagFXIntensity = 1.5f;
    public int TagFXSparks = 90;
    public float TagFXSparkSpeed = 1f;
    public float TagFXGravity = 0.6f;
    public int TagFXRings = 2;
    public bool TagFXFlash = true;
    public bool TagFXLight = true;
    public bool TagFXSound = true;
    public float TagFXVolume = 0.5f;

    /// <summary>First person camera (Other Cameras > First Person): saved so it's the same after a restart.</summary>
    public FirstPersonSettings FirstPerson = new();

    /// <summary>Settings MonkeFrames doesn't know (e.g. the old "Gfx" graphics section, now the Photoreal mod's)
    /// are kept as they are when the file is saved again.</summary>
    [JsonExtensionData] public System.Collections.Generic.IDictionary<string, Newtonsoft.Json.Linq.JToken> Unknown;

    /// <summary>Tag explosions: also draw them in the VR headset (off = MonkeFrames camera only).</summary>
    public bool TagFXInVR = false;

    public static void Load()
    {
        var settings = new JsonSerializerSettings
        {
            Converters = { new ColorConverter() }
        };

        if (!File.Exists(SystemUtilities.Combine(Constants.DataFolder, "config.json")))
        {
            current = new Settings { UiVersion = 1 };
            return;
        }

        string prefs = File.ReadAllText(SystemUtilities.Combine(Constants.DataFolder, "config.json"));
        current = JsonConvert.DeserializeObject<Settings>(prefs, settings) ?? new Settings();
        current.Sources ??= new SourcesSettings();

        // 2.13: the default accent is yellow (was pure blue); a changed colour is kept
        if (current.UiVersion < 1)
        {
            Color a = current.AccentColor;
            if (Mathf.Abs(a.r) < 0.01f && Mathf.Abs(a.g) < 0.01f && Mathf.Abs(a.b - 1f) < 0.01f)
                current.AccentColor = DefaultAccent;
            current.UiVersion = 1;
        }
    }

    public static void Save()
    {
        current ??= new Settings();
        current.Sources ??= new SourcesSettings();
        try { CameraModes.Instance?.SaveSettings(current.Sources); } catch { }

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

/// <summary>First person camera settings (angle clamping, head-space offset, head cosmetics hiding and friends:
/// the first person features of Pokruk's Camera Mod, rebuilt for MonkeFrames).</summary>
[System.Serializable]
public class FirstPersonSettings
{
    public float FieldOfView = 100f;
    public float TurnSmooth = 0.35f;          // 0 = locked to the head, 1 = very floaty
    public bool LevelHorizon = true;          // roll lock
    public bool FollowPitch = true;           // look up / down with the head
    public bool OffsetFollowsHead = false;    // offset in head space (moves with head tilt) instead of a level frame
    public float Forward = 0.12f, Up = 0f, Side = 0f;
    public float Pitch = 0f, Yaw = 0f;
    public float BobSmooth = 0f;
    public float NearClip = 0.01f;            // 0 = game default
    public bool AngleClamp = true;            // the view never lags more than MaxAngle behind the head
    public float MaxAngle = 30f;
    public bool ShowClampGuide = false;       // circle of the clamp range + where the head points
    public bool HideHeadCosmetics = true;     // hats / face items hidden from the MonkeFrames camera only
    /// <summary>CastingShouldBeFree's first person: locked inside the head mesh (0.15 m up), looking exactly where the
    /// head looks, only turning smoothed. Off = the classic MonkeFrames first person with all its options.</summary>
    public bool CastingStyle = true;
}

/// <summary>Persistent camera mode and tuning values used by the Sources window.</summary>
public class SourcesSettings
{
    public int Mode;

    public float FieldOfView = 75f;
    public float Smoothing = 0.35f;
    public float PositionLag;
    public float LookHeight;
    public float Dutch;
    public float Shake;
    public bool MotionBlur;
    public float MotionBlurStrength = 0.5f;

    public bool OrbitAutoSpin = true;
    public float OrbitSpeed = 20f;
    public float OrbitDistance = 3f;
    public float OrbitHeight = 0.4f;
    public float OrbitAngle = 12f;
    public bool OrbitRelative;

    public float FollowDistance = 2.5f;
    public float FollowHeight = 0.8f;
    public float FollowSide;
    public float FollowTurnLag = 0.5f;

    public float ShoulderSide = 0.45f;
    public float ShoulderBack = 1.1f;
    public float ShoulderHeight = 0.22f;

    public bool TrackingAutoZoom;
    public float TrackingFrame = 1.2f;
    public float FrontDistance = 1.6f;
    public float FrontHeight = 0.1f;
    public float TopHeight = 6f;
    public bool TopRotate = true;
    public float SideAngle = 90f;
    public float SideDistance = 4f;
    public float SideHeight = 0.3f;
    public bool HandRight = true;
    public bool HandLookAtFace = true;
    public float DirectorInterval = 6f;
    public bool DirectorBlend = true;
}
