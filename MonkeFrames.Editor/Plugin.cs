using Cardboard;
using BepInEx;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Components;
using System;
using UnityEngine;
using MonkeFrames.Extensions;

namespace MonkeFrames.Editor;

[BepInDependency("dev.sirkingbinx.cardboard", "1.3.0")]
[BepInPlugin(Constants.Guid, Constants.Name, Constants.Version)]
public class Plugin : BaseUnityPlugin
{
    public void Awake()
    {
        CardboardHarmony.PatchInstance(this);
        CardboardEvents.OnPlayerSpawned += OnPlayerSpawned;
    }

    private void OnDestroy()
    {
        CardboardEvents.OnPlayerSpawned -= OnPlayerSpawned;
        Application.quitting -= OnMonkeFramesUnloaded;
        CardboardHarmony.UnpatchInstance();
    }

    public static void OnPlayerSpawned()
    {
        MonkeFrames.Extensions.FramesLog.Log("[MonkeFrames::Initialize] Initializing MonkeFrames...");

        Constants.Init();

        GameObject tpc = GorillaTagger.Instance.thirdPersonCamera.transform.Find("Shoulder Camera").gameObject;

        tpc.SetActive(true);

        tpc.AddComponent<CameraManager>();
        tpc.AddComponent<KeyframeManager>();
        tpc.AddComponent<UIManager>();
        tpc.AddComponent<ConditionManager>();
        tpc.AddComponent<CameraModes>();
        tpc.AddComponent<ObjectManager>();
        tpc.AddComponent<Replays.ReplayManager>();
        tpc.AddComponent<Mocap.MocapManager>();

        MonkeFrames.Extensions.FramesLog.Log("[MonkeFrames::Initialize] All components added");

        Application.quitting += OnMonkeFramesUnloaded;
    }

    public static Action OnMonkeFramesLoaded = () =>
    {
        MonkeFrames.Extensions.FramesLog.Log($"[MonkeFrames::Initialize] Welcome to MonkeFrames version {Constants.VersionID}");

        Settings.Load();

        // Warn if the game picked up a different (usually older) MonkeFrames.Compiler.dll,
        // e.g. a stray copy in the game's root folder, which is searched before BepInEx/plugins.
        try
        {
            string compilerPath = typeof(MonkeFrames.Compiler.Models.Project).Assembly.Location;
            string editorDir = System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            if (!string.IsNullOrEmpty(compilerPath) && !string.IsNullOrEmpty(editorDir) &&
                !string.Equals(System.IO.Path.GetDirectoryName(compilerPath), editorDir, StringComparison.OrdinalIgnoreCase))
            {
                string msg = $"Warning: MonkeFrames.Compiler.dll is being loaded from \"{compilerPath}\" instead of the plugin folder. Remove that copy and restart the game.";
                MonkeFrames.Extensions.FramesLog.Log("[MonkeFrames::Initialize] " + msg);
                UIManager.Instance.Status = msg;
            }
        }
        catch (Exception ex)
        {
            MonkeFrames.Extensions.FramesLog.Log($"[MonkeFrames::Initialize] Could not verify compiler location: {ex.Message}");
        }

        CameraManager.Instance.SetModEnabled(true);
        CameraModes.Instance?.LoadSettings(Settings.current.Sources);
    };

    public static Action OnMonkeFramesUnloaded = () =>
    {
        Settings.Save();
        ExtensionManager.CallMethod("OnUnload");
    };
}
