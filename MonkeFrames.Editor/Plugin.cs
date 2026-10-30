using BepInEx;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Components;
using System;
using System.Collections;
using GorillaTagScripts.Subscription.AlarmClocks;
using UnityEngine;
using MonkeFrames.Extensions;

namespace MonkeFrames.Editor;

[BepInPlugin(Constants.Guid, Constants.Name, Constants.Version)]
public class Plugin : BaseUnityPlugin
{
    private static Plugin instance;
    private bool initializing, initialized;

    public void Start()
    {
        instance = this;
        HarmonyLib.Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, Constants.Guid);
        GorillaTagger.OnPlayerSpawned(OnPlayerSpawned);
    }

    public static void OnPlayerSpawned()
    {
        if (instance == null || instance.initializing || instance.initialized) return;
        instance.initializing = true;
        instance.StartCoroutine(instance.InitializeAfterWakeUp());
    }

    private IEnumerator InitializeAfterWakeUp()
    {
        // The spawn callback can precede the alarm's input/teleport sequence. Do not
        // activate/reconfigure its camera until the game has finished waking the player.
        yield return null;
        while (GorillaTagger.Instance == null || GorillaTagger.Instance.offlineVRRig == null
            || (AlarmClockManager.Instance != null && !AlarmClockManager.Instance.Initialized)
            || (AlarmClockManager.Instance == null && !string.IsNullOrEmpty(PlayerPrefs.GetString("AlarmClock"))))
            yield return null;
        try { InitializeEditor(); initialized = true; }
        finally { initializing = false; }
    }

    private static void InitializeEditor()
    {
        Console.WriteLine("[MonkeFrames::Initialize] Initializing MonkeFrames...");

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

        Console.WriteLine("[MonkeFrames::Initialize] All components added");

        Application.quitting += OnMonkeFramesUnloaded;
    }

    public static Action OnMonkeFramesLoaded = () =>
    {
        Console.WriteLine($"[MonkeFrames::Initialize] Welcome to MonkeFrames version {Constants.VersionID}");

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
                Console.WriteLine("[MonkeFrames::Initialize] " + msg);
                UIManager.Instance.Status = msg;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::Initialize] Could not verify compiler location: {ex.Message}");
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
