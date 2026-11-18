using BepInEx;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Components;
using System;
using System.Collections;
using GorillaTagScripts.Subscription.AlarmClocks;
using UnityEngine;
using System.Threading.Tasks;
using MonkeFrames.Extensions;
using MonkeFrames.Editor.Utilities;




#if DEBUG
using System.Runtime.InteropServices;
#endif

namespace MonkeFrames.Editor;

[BepInPlugin(Constants.Guid, Constants.Name, Constants.Version)]
public class Plugin : BaseUnityPlugin
{
    private static Plugin _instance;
    private bool _initializing, _initialized;

    public void Start()
    {
        _instance = this;
        HarmonyLib.Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, Constants.Guid);
        GorillaTagger.OnPlayerSpawned(OnPlayerSpawned);
    }

    /// <summary>The game says the player spawned: set up once the rig (and the alarm clocks) are ready.</summary>
    public static void OnPlayerSpawned()
    {
        // The callback can fire more than once; only ever initialise one editor.
        if (_instance == null || _instance._initializing || _instance._initialized)
            return;

        _instance._initializing = true;
        _instance.StartCoroutine(_instance.InitializeAfterWakeUp());
    }

    private IEnumerator InitializeAfterWakeUp()
    {
        yield return null;

        // Wait for the local rig, and for the game's alarm clocks to finish loading (if the player has any),
        // so adding our components doesn't race the game's own start-up.
        while (GorillaTagger.Instance == null || GorillaTagger.Instance.offlineVRRig == null || AlarmClocksLoading())
            yield return null;

        try
        {
            InitializeEditor();
            _initialized = true;
        }
        finally
        {
            _initializing = false;
        }
    }

    private static bool AlarmClocksLoading()
    {
        AlarmClockManager clocks = AlarmClockManager.Instance;
        if (clocks != null)
            return !clocks.Initialized;
        // No manager yet, but the player has alarm clocks saved: it's still coming.
        return !string.IsNullOrEmpty(PlayerPrefs.GetString("AlarmClock"));
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
        try { CameraModes.Instance?.LoadSettings(Settings.current.Sources); }
        catch (Exception ex) { Console.WriteLine($"[MonkeFrames::Initialize] Couldn't restore the camera source: {ex.Message}"); }
    };

    public static Action OnMonkeFramesUnloaded = () =>
    {
        Settings.Save();
        ExtensionManager.CallMethod("OnUnload");
    };

#if DEBUG
    Plugin()
    {
        AllocConsole();

        Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

        Console.Title = $"MonkeFrames {Constants.VersionID} (Build {Constants.BuildDate})";

        Console.WriteLine($"MonkeFrames Debug Build {Constants.VersionID} (Build {Constants.BuildDate})");

        Application.logMessageReceived += HandleLogMsg;

        Application.quitting += () => {
            Application.logMessageReceived -= HandleLogMsg;
            FreeConsole();
        };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    private static void HandleLogMsg(string logString, string stackTrace, LogType type)
    {
        if (type == LogType.Exception && stackTrace.Contains("MonkeFrames"))
        {
            Console.Error.WriteLine("An unhandled exception occured.");
            Console.Error.WriteLine($"Message:     {logString}");
            Console.Error.WriteLine($"Stack Trace: {stackTrace}");
        }
    }
#endif
}
