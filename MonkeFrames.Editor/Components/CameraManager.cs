using GorillaNetworking;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Replays;
using MonkeFrames.Editor.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using Keyframe = MonkeFrames.Compiler.Models.Keyframe;

namespace MonkeFrames.Editor.Components;

public class CameraManager : MonoBehaviour
{
    public static CameraManager Instance;

    public Camera Camera;

    public Vector3 Position;
    public Quaternion Rotation;
    public float FieldOfView = 70f;

    public GameObject CameraMarker;

    public bool InPlayback = false;

    /// <summary>Camera motion blur (per-keyframe, used during Player preview, playback and export).</summary>
    public MotionBlurController Blur
    {
        get
        {
            if (_blur == null)
                _blur = gameObject.GetComponent<MotionBlurController>() ?? gameObject.AddComponent<MotionBlurController>();
            return _blur;
        }
    }
    private MotionBlurController _blur;
    public bool Manual = false;

    /// <summary>True while an "Other Cameras" mode drives the camera (free-fly controls are paused).</summary>
    public bool ExternalControl = false;

    public CameraManager()
    {
        Instance = this;
    }

    private void Start()
    {
        Position = gameObject.transform.position;
        Rotation = gameObject.transform.rotation;

        Console.WriteLine("[MonkeFrames::CameraManager] All camera-based stuff should be set up");
    }

    public void SetModEnabled(bool enabled)
    {
        SetCinemachineState(!enabled);

        if (enabled)
            KeyframeManager.Instance.RefreshOrbs();
        else
            KeyframeManager.Instance.DeleteOrbs();

        if (CameraMarker == null)
            CameraMarker = KeyframeManager.Instance.CreateOrb("MonkeFrames Spectator Camera");

        CameraMarker.SetActive(enabled);
        UIManager.Instance.Drawing = enabled;
    }

    private void LateUpdate()
    {
        if (Keyboard.current.f1Key.wasPressedThisFrame && CinemachineState)
            SetModEnabled(true);

        PhotonNetworkController.Instance.disableAFKKick = !CinemachineState;

        if (CinemachineState)
            return;

        if (Camera == null)
            Camera = gameObject.GetComponent<Camera>();


        // Update values
        if (!Manual)
        {
            gameObject.transform.position = Position;
            gameObject.transform.rotation = Rotation;

            CameraMarker.transform.position = Position;
            CameraMarker.transform.rotation = Rotation;

            if (Camera != null)
                Camera.fieldOfView = FieldOfView;
        }

        FollowToNewMap();

        if (!InPlayback && !ExternalControl)
        {
            // Free cam: speed in metres per second (the same at any frame rate). Shift = fast, Ctrl = slow.
            Settings st = Settings.current;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            bool typing = GUIUtility.keyboardControl != 0;
            float speed = Mathf.Clamp(st?.CameraSpeed ?? 3f, 0.1f, 100f);
            if (Keyboard.current.shiftKey.isPressed)
                speed *= Mathf.Clamp(st?.CameraFastMultiplier ?? 4f, 1f, 25f);
            else if (Keyboard.current.ctrlKey.isPressed)
                speed *= Mathf.Clamp(st?.CameraSlowMultiplier ?? 0.2f, 0.02f, 1f);

            Vector3 input = Vector3.zero;
            if (!typing)
            {
                if (Keyboard.current.wKey.isPressed) input += transform.forward;
                if (Keyboard.current.sKey.isPressed) input -= transform.forward;
                if (Keyboard.current.dKey.isPressed) input += transform.right;
                if (Keyboard.current.aKey.isPressed) input -= transform.right;
                if (Keyboard.current.eKey.isPressed) input += transform.up;
                if (Keyboard.current.qKey.isPressed) input -= transform.up;
            }
            Vector3 targetVel = input.sqrMagnitude > 1e-6f ? input.normalized * speed : Vector3.zero;
            // smooth: glide up to speed and to a stop (~0.15 s); off: start and stop instantly
            _moveVel = (st?.CameraSmoothMove ?? true)
                ? Vector3.Lerp(_moveVel, targetVel, 1f - Mathf.Exp(-14f * dt))
                : targetVel;
            if (_moveVel.sqrMagnitude < 1e-8f) _moveVel = Vector3.zero;
            Position += _moveVel * dt;

            // Arrow keys: roll (degrees per second, also faster / slower with Shift / Ctrl). Same tilt as
            // right-drag: sticks at level for a moment, and letting go close to level glides it level.
            float rollSpeed = 40f * speed / Mathf.Clamp(st?.CameraSpeed ?? 3f, 0.1f, 100f) * dt;
            int rollDir = typing ? 0 : (Keyboard.current.rightArrowKey.isPressed ? 1 : 0) - (Keyboard.current.leftArrowKey.isPressed ? 1 : 0);
            if (rollDir != 0)
                ArrowRoll(rollDir * rollSpeed);
            else if (_arrowRolling)
            {
                _arrowRolling = false;
                LevelIfNearlyLevel();
            }

            UpdateFieldOfView();

            UpdateMouseLook();
        }
        else _moveVel = Vector3.zero;

        if (InPlayback && (Keyboard.current.spaceKey.wasPressedThisFrame || Keyboard.current.escapeKey.wasPressedThisFrame))
            StopPlayback();
    }

    Vector2 mousePos = new Vector2(0, 0);

    // ---- Loading into another map: the free camera comes along ----
    private Vector3 _lastPlayerPos;
    private bool _havePlayerPos;

    private void FollowToNewMap()
    {
        Transform head = null;
        try { head = GorillaTagger.Instance != null && GorillaTagger.Instance.headCollider != null ? GorillaTagger.Instance.headCollider.transform : null; } catch { }
        if (head == null) { _havePlayerPos = false; return; }
        Vector3 p = head.position;
        bool jumped = _havePlayerPos && (p - _lastPlayerPos).sqrMagnitude > 40f * 40f;   // teleported, not moved
        _lastPlayerPos = p; _havePlayerPos = true;
        if (!jumped || InPlayback || ExternalControl || !(Settings.current?.CameraFollowToNewMap ?? true)) return;
        // only if the camera was left behind (not when it's already near where you landed)
        if ((Position - p).sqrMagnitude < 60f * 60f) return;
        Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        CancelLookSmoothing();
        Position = p - fwd * 2.2f + Vector3.up * 0.6f;
        Vector3 look = p - Position;
        Rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
        UIManager.Instance.Status = "New map: the camera came with you.";
    }
    private Vector3 _moveVel;           // free cam velocity (m/s), for smooth starts and stops

    // ---- Mouse look (with optional Caps Lock smoothing) ----
    private Vector2 _smoothedLook;
    private float _roll, _smoothedRoll;
    private float _rawRoll;               // tilt before the "level" detent is applied
    private bool _looking, _tilting;
    private bool _leveling;               // gliding back to a level horizon
    private bool _arrowRolling;           // an arrow key is rolling the camera
    private float _lastRightClick = -10f;
    private Vector2 _rightClickPos;
    private bool _lookSettling;

    /// <summary>True while smoothed mouse look is still gliding to its target.</summary>
    public bool LookSettling => _lookSettling;

    private void UpdateMouseLook()
    {
        Settings settings = Settings.current;
        bool typing = GUIUtility.keyboardControl != 0;

        if (!typing && Keyboard.current.capsLockKey.wasPressedThisFrame && settings != null)
        {
            settings.SmoothMouseLook = !settings.SmoothMouseLook;
            UIManager.Instance.Status = settings.SmoothMouseLook
                ? "Smooth mouse look ON (Caps Lock to turn off)"
                : "Smooth mouse look OFF";
            Settings.Save();
        }

        // Left drag = look around (yaw/pitch). Right drag = tilt (roll).
        // A drag only starts when the click lands outside the MonkeFrames UI, so clicking
        // buttons and dragging windows never moves the camera.
        bool blocked = (UIManager.Instance != null && UIManager.Instance.IsPointerOverUI)
            || (UI.IntroScreen.Active && !UI.IntroScreen.Exiting);

        if (Mouse.current.leftButton.wasPressedThisFrame && !blocked) _looking = true;
        if (!Mouse.current.leftButton.isPressed) _looking = false;

        bool rightPressed = Mouse.current.rightButton.wasPressedThisFrame && !blocked;
        if (rightPressed) _tilting = true;
        bool wasTilting = _tilting;
        if (!Mouse.current.rightButton.isPressed) _tilting = false;

        // Double right-click = level the horizon.
        if (rightPressed && (settings?.DoubleClickLevel ?? true))
        {
            Vector2 mp = Mouse.current.position.ReadValue();
            if (Time.unscaledTime - _lastRightClick < 0.35f && (mp - _rightClickPos).sqrMagnitude < 400f)
            {
                StartLeveling();
                _lastRightClick = -10f;
                _tilting = false;
                UIManager.Instance.Status = "Horizon levelled.";
            }
            else
            {
                _lastRightClick = Time.unscaledTime;
                _rightClickPos = mp;
            }
        }

        bool dragging = _looking || _tilting;
        Cursor.lockState = dragging ? CursorLockMode.Locked : CursorLockMode.None;

        // Start from wherever the camera currently points (e.g. after "Go to keyframe" or the
        // arrow keys), instead of snapping back to the last mouse-look angle.
        bool justStarted = (_looking && Mouse.current.leftButton.wasPressedThisFrame)
            || (_tilting && Mouse.current.rightButton.wasPressedThisFrame);
        if (justStarted && !_lookSettling)
        {
            Vector3 e = Rotation.eulerAngles;
            float pitch = e.x > 180f ? e.x - 360f : e.x;
            float roll = e.z > 180f ? e.z - 360f : e.z;
            mousePos = new Vector2(e.y / 0.5f, -pitch / 0.5f);
            _roll = roll;
            _rawRoll = roll + Mathf.Sign(roll) * (Mathf.Abs(roll) > 0.01f ? SnapAngle : 0f);
            _smoothedLook = mousePos;
            _smoothedRoll = _roll;
        }
        if (_tilting) _leveling = false;

        Vector2 delta = Mouse.current.delta.ReadValue();
        if (_looking)
            mousePos += delta / 5f;
        if (_tilting)
        {
            // Magnetic "level" detent: the horizon sticks at 0° until you tilt past the snap angle.
            _rawRoll -= delta.x * 0.12f;
            float snap = SnapAngle;
            _roll = Mathf.Abs(_rawRoll) <= snap ? 0f : _rawRoll - Mathf.Sign(_rawRoll) * snap;
        }

        // Let go close to level: glide the rest of the way.
        if (wasTilting && !_tilting && (settings?.AutoLevel ?? true)
            && Mathf.Abs(_roll) > 0.001f && Mathf.Abs(_roll) < Mathf.Max(1f, settings?.AutoLevelAngle ?? 6f))
            StartLeveling();

        if (_leveling)
        {
            _roll = Mathf.Lerp(_roll, 0f, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
            if (Mathf.Abs(_roll) < 0.02f) { _roll = 0f; _leveling = false; }
            _rawRoll = _roll;
            if (!dragging && !_lookSettling)
            {
                // straighten the camera around its own view axis (where it looks doesn't change)
                Rotation = WithRoll(Rotation, _roll);
                _smoothedRoll = _roll;
                Vector3 e = Rotation.eulerAngles;
                float pitch = e.x > 180f ? e.x - 360f : e.x;
                mousePos = new Vector2(e.y / 0.5f, -pitch / 0.5f);
                _smoothedLook = mousePos;
            }
        }

        bool smooth = settings?.SmoothMouseLook == true;

        if (smooth)
        {
            if (dragging)
                _lookSettling = true;

            if (_lookSettling)
            {
                // Critically-damped follow: higher smoothing = lower rate = floatier camera.
                float amount = Mathf.Clamp01(settings.MouseSmoothing);
                float rate = Mathf.Lerp(22f, 2.5f, amount);
                float k = 1f - Mathf.Exp(-rate * Time.unscaledDeltaTime);
                _smoothedLook = Vector2.Lerp(_smoothedLook, mousePos, k);
                _smoothedRoll = Mathf.Lerp(_smoothedRoll, _roll, k);

                Rotation = Quaternion.Euler(-_smoothedLook.y * 0.5f, _smoothedLook.x * 0.5f, _smoothedRoll);

                if (!dragging && (_smoothedLook - mousePos).sqrMagnitude < 0.0004f && Mathf.Abs(_smoothedRoll - _roll) < 0.01f)
                    _lookSettling = false;
            }
        }
        else
        {
            _lookSettling = false;
            if (dragging)
            {
                _smoothedLook = mousePos;
                _smoothedRoll = _roll;
                Rotation = Quaternion.Euler(-mousePos.y * 0.5f, mousePos.x * 0.5f, _roll);
            }
        }
    }

    private static float SnapAngle =>
        (Settings.current?.AutoLevel ?? true) ? Mathf.Clamp(Settings.current?.LevelSnapAngle ?? 3f, 0f, 15f) : 0f;

    /// <summary>Glide the free camera's tilt back to a level horizon.</summary>
    public void StartLeveling()
    {
        if (!_lookSettling) _roll = RollOf(Rotation);
        _leveling = true;
    }

    /// <summary>Auto-level: if the tilt is within the level zone (Settings), glide it level.</summary>
    public void LevelIfNearlyLevel()
    {
        Settings s = Settings.current;
        if (!(s?.AutoLevel ?? true)) return;
        float roll = _lookSettling ? _roll : RollOf(Rotation);
        if (Mathf.Abs(roll) > 0.001f && Mathf.Abs(roll) < Mathf.Max(1f, s?.AutoLevelAngle ?? 6f))
            StartLeveling();
    }

    /// <summary>The camera's tilt (degrees, -180..180): how far its up leans from the world's up, around its view.</summary>
    private static float RollOf(Quaternion q)
    {
        Vector3 e = q.eulerAngles;
        return e.z > 180f ? e.z - 360f : e.z;
    }

    /// <summary>Same view direction, with this much tilt.</summary>
    private static Quaternion WithRoll(Quaternion q, float roll)
    {
        Vector3 e = q.eulerAngles;
        return Quaternion.Euler(e.x, e.y, roll);
    }

    /// <summary>Roll from the arrow keys, through the same tilt state (and level detent) as right-drag.</summary>
    private void ArrowRoll(float degrees)
    {
        if (!_arrowRolling)
        {
            _arrowRolling = true;
            _leveling = false;
            _roll = RollOf(_lookSettling ? Quaternion.Euler(0f, 0f, _roll) : Rotation);
            _rawRoll = _roll + Mathf.Sign(_roll) * (Mathf.Abs(_roll) > 0.01f ? SnapAngle : 0f);
        }
        _rawRoll += degrees;
        float snap = SnapAngle;
        _roll = Mathf.Abs(_rawRoll) <= snap ? 0f : _rawRoll - Mathf.Sign(_rawRoll) * snap;
        if (_lookSettling) return;            // the look smoothing carries _roll into the camera
        Rotation = WithRoll(Rotation, _roll);
        _smoothedRoll = _roll;
    }

    // ---- Scroll-wheel FOV (smoothed when Smooth Look is on) ----
    private float _fovTarget = -1f;
    private float _fovLastSet = -1f;

    private void UpdateFieldOfView()
    {
        float scroll = Mouse.current.scroll.ReadValue().y;
        bool smooth = Settings.current?.SmoothMouseLook == true;

        // Something else moved the FOV (Go to keyframe, playback, typing a value): follow it.
        if (_fovTarget < 0f || Mathf.Abs(FieldOfView - _fovLastSet) > 0.001f)
            _fovTarget = FieldOfView;

        // Scrolling a list inside a MonkeFrames window shouldn't zoom the camera.
        if (scroll != 0f && UIManager.Instance != null && UIManager.Instance.IsPointerOverUI)
            scroll = 0f;

        if (scroll != 0f)
            _fovTarget = NumberUtilities.Bounds(_fovTarget + scroll * 5f, 15, 150);

        if (smooth)
        {
            // Critically-damped glide towards the target; heavier Look smoothing = slower zoom.
            float amount = Mathf.Clamp01(Settings.current.MouseSmoothing);
            float rate = Mathf.Lerp(18f, 3f, amount);
            FieldOfView = Mathf.Lerp(FieldOfView, _fovTarget, 1f - Mathf.Exp(-rate * Time.unscaledDeltaTime));
            if (Mathf.Abs(FieldOfView - _fovTarget) < 0.01f)
                FieldOfView = _fovTarget;
        }
        else
        {
            FieldOfView = _fovTarget;
        }

        _fovLastSet = FieldOfView;
    }

    /// <summary>Stop any in-progress look smoothing (used when the camera is moved programmatically).</summary>
    public void CancelLookSmoothing()
    {
        _lookSettling = false;
        _smoothedLook = mousePos;
        _smoothedRoll = _roll;
    }
    public bool CinemachineState = true;

    public void SetCinemachineState(bool enabled)
    {
        CinemachineBrain brain = gameObject.GetComponent<CinemachineBrain>();
        gameObject.transform.Find("CM vcam1").gameObject.SetActive(enabled);
        brain.enabled = enabled;

        CinemachineState = enabled;
        Console.WriteLine($"[MonkeFrames::CameraManager] Cinemachine on TPC is now {(enabled ? "activated" : "deactivated")}");
    }

    int playbackPosition = 0;

    /// <summary>Seconds into the keyframe animation during Project > Play / Export (used to sync replays).</summary>
    public float PlaybackSeconds => playbackPosition / (float)Mathf.Max(1, KeyframeManager.Instance.Project.FPS);

    /// <summary>
    /// Time of the keyframe the camera is actually showing this frame. While exporting, the
    /// position counter is bumped before the next frame renders, so the shown frame is one behind.
    /// </summary>
    public float AppliedPlaybackSeconds =>
        Mathf.Max(0, doRecording ? playbackPosition - 1 : playbackPosition) / (float)Mathf.Max(1, KeyframeManager.Instance.Project.FPS);
    float playbackAccumulator = 0f;
    int playbackEnding;

    public bool doRecording = false;

    public Texture2D tex2d;
    public List<Keyframe> kCache;

    public RenderTexture renderTexture;
    public byte[] rBuffer;

    public BinaryWriter frameStream;
    public Process ffmpegProcess;

    /// <summary>Where the next export would go (never an existing file: "name 1.mp4", "name 2.mp4"...).</summary>
    public string outputMp4 => ExportPaths.Next(Path.Combine(Constants.DataFolder, "exports"), KeyframeManager.Instance.Project.Name);
    /// <summary>The file the current (or last) export writes, picked once when it starts.</summary>
    private string exportMp4;
    private string ffmpegLogPath => Path.Combine(Constants.DataFolder, "exports", "ffmpeg-log.txt");
    private readonly System.Text.StringBuilder ffmpegLog = new();
    private int exportWidth, exportHeight;

    // Export audio (replay voices and / or the project's music track) and progress.
    private string exportAudioPath;
    private int exportFrameCount;
    private int ffmpegFramesDone;
    private bool exportFinishing;

    /// <summary>A video is being captured or ffmpeg is still finishing it.</summary>
    public bool Exporting => doRecording || exportFinishing;

    /// <summary>0..1: frames captured while recording, then frames ffmpeg has encoded while it finishes.</summary>
    public float ExportProgress => doRecording
        ? (playbackEnding > 0 ? Mathf.Clamp01(playbackPosition / (float)playbackEnding) : 0f)
        : (exportFrameCount > 0 ? Mathf.Clamp01(Interlocked.CompareExchange(ref ffmpegFramesDone, 0, 0) / (float)exportFrameCount) : 0f);

    private void CleanupExportAudio()
    {
        if (string.IsNullOrEmpty(exportAudioPath)) return;
        try { if (File.Exists(exportAudioPath)) File.Delete(exportAudioPath); }
        catch (Exception ex) { Console.WriteLine($"[MonkeFrames::Export] Could not remove temporary audio: {ex.Message}"); }
        exportAudioPath = null;
    }

    /// <summary>Stops a failed export cleanly: playback ends, the encoder is closed, and the reason is shown.</summary>
    private void AbortExport(string reason)
    {
        Blur.Set(false, 0f);
        PhotorealLink.KeyframeHour = -1f;
        InPlayback = false;
        doRecording = false;
        UIManager.Instance.Drawing = true;
        KeyframeManager.Instance.RefreshOrbs();
        playbackPosition = 0;
        try { frameStream?.Close(); } catch { }
        frameStream = null;
        try { if (ffmpegProcess != null && !ffmpegProcess.HasExited) ffmpegProcess.Kill(); } catch { }
        try { ffmpegProcess?.Dispose(); } catch { }
        ffmpegProcess = null;
        if (renderTexture != null) { renderTexture.Release(); Destroy(renderTexture); renderTexture = null; }
        exportFinishing = false;
        CleanupExportAudio();
        SaveFfmpegLog();
        UIManager.Instance.Status = "Export failed: " + reason;
        Console.WriteLine($"[MonkeFrames::Export] Failed: {reason}");
        StopCoroutine("PlaybackCoroutine");
    }

    private void SaveFfmpegLog()
    {
        try { lock (ffmpegLog) File.WriteAllText(ffmpegLogPath, ffmpegLog.ToString()); } catch { }
    }

    private string LastFfmpegLine()
    {
        lock (ffmpegLog)
        {
            string[] lines = ffmpegLog.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[lines.Length - 1].Trim() : "";
        }
    }

    IEnumerator PlaybackCoroutine()
    {
        var waitFrame = new WaitForEndOfFrame();

        while (InPlayback)
        {
            yield return waitFrame;

            if (playbackPosition >= playbackEnding - 1)
            {
                Blur.Set(false, 0f);
                PhotorealLink.KeyframeHour = -1f;
                bool wasRecording = doRecording;
                InPlayback = false;
                UIManager.Instance.Drawing = true;
                KeyframeManager.Instance.RefreshOrbs();
                playbackPosition = 0;
                doRecording = false;

                // Plain playback (Project > Play) has no encoder to shut down.
                // (this coroutine just ends; it isn't stopped from inside, which would cut the encoder's
                // finishing step below off at its first wait)
                if (!wasRecording)
                {
                    UIManager.Instance.Status = "Playback finished.";
                    yield break;
                }

                UIManager.Instance.Status = "Finishing encoding..";
                exportFinishing = true;

                try { frameStream.Flush(); frameStream.Close(); } catch { }
                frameStream = null;
                if (renderTexture != null) { renderTexture.Release(); Destroy(renderTexture); renderTexture = null; }

                // Let ffmpeg finish the file without freezing the game. Long or high-FPS videos can take a
                // while, so only give up once ffmpeg has stopped making progress for a whole minute.
                float stalled = 0f;
                int lastDone = -1;
                while (ffmpegProcess != null && !ffmpegProcess.HasExited && stalled < 60f)
                {
                    int done = Interlocked.CompareExchange(ref ffmpegFramesDone, 0, 0);
                    if (done != lastDone) { lastDone = done; stalled = 0f; }
                    else stalled += Time.unscaledDeltaTime;
                    yield return null;
                }
                int exit = -1;
                try { if (ffmpegProcess != null && ffmpegProcess.HasExited) { ffmpegProcess.WaitForExit(); exit = ffmpegProcess.ExitCode; } else ffmpegProcess?.Kill(); } catch { }
                try { ffmpegProcess?.Dispose(); } catch { }
                ffmpegProcess = null;
                exportFinishing = false;
                CleanupExportAudio();
                SaveFfmpegLog();

                bool ok = exit == 0 && File.Exists(exportMp4) && new FileInfo(exportMp4).Length > 0;
                if (!ok)
                {
                    string why = LastFfmpegLine();
                    UIManager.Instance.Status = "Export failed" + (why.Length > 0 ? ": " + why : "") + " (details in exports\\ffmpeg-log.txt)";
                    yield break;
                }
                try { Process.Start("explorer.exe", $"/select,\"{exportMp4}\""); } catch { }
                UIManager.Instance.Status = $"Exported {exportMp4}";
                yield break;
            }

            Keyframe currentFrame = kCache[playbackPosition];

            foreach (MonkeFrames.Extensions.FramesExtension extension in MonkeFrames.Extensions.ExtensionManager.Plugins.Values)
            {
                try { extension.OnKeyframeStep(ref currentFrame); }
                catch (Exception ex) { Console.WriteLine($"[MonkeFrames::Extensions] OnKeyframeStep: {ex.Message}"); }
            }
            Blur.Set(currentFrame.MotionBlur, currentFrame.MotionBlurStrength);

            Position = currentFrame.Position;
            Rotation = currentFrame.QuatRotation;
            FieldOfView = currentFrame.FieldOfView;
            PhotorealLink.KeyframeHour = currentFrame.TimeOfDay;

            if (doRecording) {
                // the window was resized: the capture no longer matches what ffmpeg expects
                if (Screen.width != exportWidth || Screen.height != exportHeight)
                {
                    AbortExport("the game window changed size while exporting. Keep it the same size until it's done.");
                    yield break;
                }
                if (ffmpegProcess == null || ffmpegProcess.HasExited)
                {
                    string why = LastFfmpegLine();
                    AbortExport("ffmpeg stopped" + (why.Length > 0 ? ": " + why : "") + " (details in exports\\ffmpeg-log.txt)");
                    yield break;
                }

                ScreenCapture.CaptureScreenshotIntoRenderTexture(renderTexture);

                var request = AsyncGPUReadback.Request(renderTexture, 0, TextureFormat.RGBA32);

                while (!request.done)
                    yield return null;

                if (request.hasError)
                    continue;

                var nativeArray = request.GetData<byte>();
                nativeArray.CopyTo(rBuffer);

                bool written = true;
                try
                {
                    frameStream.Write(rBuffer, 0, rBuffer.Length);
                    frameStream.Flush();
                }
                catch (Exception ex) { written = false; Console.WriteLine($"[MonkeFrames::Export] Writing a frame failed: {ex.Message}"); }
                if (!written)
                {
                    string why = LastFfmpegLine();
                    AbortExport("ffmpeg stopped taking frames" + (why.Length > 0 ? ": " + why : "") + " (details in exports\\ffmpeg-log.txt)");
                    yield break;
                }
            }
            
            if (doRecording)
            {
                // Recording captures every compiled frame; ffmpeg timestamps them at the project FPS.
                playbackPosition++;
                yield return null;
            }
            else
            {
                // Live playback follows real time, skipping frames when the project FPS (e.g. 240/360)
                // is higher than the game's own frame rate, so it always plays at the right speed.
                yield return null;
                playbackAccumulator += Time.unscaledDeltaTime * KeyframeManager.Instance.Project.FPS;
                int step = Mathf.FloorToInt(playbackAccumulator);
                playbackAccumulator -= step;
                playbackPosition = Mathf.Min(playbackPosition + step, playbackEnding - 1);
            }
        }
    }

    public void StartRecording()
    {
        if (Exporting)
        {
            UIManager.Instance.Status = "A video is still being built - wait for it to finish.";
            return;
        }
        if (InPlayback)
        {
            UIManager.Instance.Status = "Already playing or exporting - wait for it to finish (Esc stops playback).";
            return;
        }
        string ffmpeg = HEncodeUtilities.FindFfmpeg(out string problem);
        if (ffmpeg == null)
        {
            UIManager.Instance.Status = "Can't export: " + problem;
            Console.WriteLine($"[MonkeFrames::Export] {problem}");
            return;
        }
        if (KeyframeManager.Instance.Project.CompiledKeyframes == null || KeyframeManager.Instance.Project.CompiledKeyframes.Count < 2)
        {
            UIManager.Instance.Status = "Can't export: add at least two keyframes (and compile) first.";
            return;
        }

        exportWidth = Screen.width;
        exportHeight = Screen.height;
        renderTexture = new RenderTexture(exportWidth, exportHeight, 24);
        rBuffer = new byte[exportWidth * exportHeight * 4];

        try { StartFfmpegEncoder(ffmpeg); }
        catch (Exception ex)
        {
            renderTexture.Release(); Destroy(renderTexture); renderTexture = null;
            UIManager.Instance.Status = "Can't export: ffmpeg didn't start (" + ex.Message + ")";
            Console.WriteLine($"[MonkeFrames::Export] ffmpeg didn't start: {ex}");
            return;
        }
        doRecording = true;
        StartPlayback();
    }

    public void StartFfmpegEncoder(string ffmpeg)
    {
        var project = KeyframeManager.Instance.Project;
        exportMp4 = outputMp4;   // picked once: the whole export uses this file
        Directory.CreateDirectory(Path.GetDirectoryName(exportMp4));

        string encoder = HEncodeUtilities.GetGoodEncoder(ffmpeg);
        exportFrameCount = project.CompiledKeyframes.Count;
        Interlocked.Exchange(ref ffmpegFramesDone, 0);

        // Replay voices (when a replay is following the keyframes) are mixed into a temporary WAV.
        CleanupExportAudio();
        bool hasVoice = false;
        ReplayManager replay = ReplayManager.Instance;
        if (replay != null && replay.Viewing && replay.SyncWithKeyframes && ReplayVoiceExporter.HasAudibleVoice(replay.Clip))
        {
            try
            {
                exportAudioPath = Path.Combine(Path.GetTempPath(), $"MonkeFrames-voice-{Guid.NewGuid():N}.wav");
                ReplayVoiceExporter.WriteWav(exportAudioPath, replay.Clip, exportFrameCount, project.FPS, replay.Speed, replay.VoiceVolume);
                hasVoice = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MonkeFrames::Export] Could not prepare replay voice audio: {ex}");
                CleanupExportAudio();
            }
        }
        // The project's music track (Player window), starting at its start time.
        bool hasMusic = !string.IsNullOrEmpty(project.AudioPath) && File.Exists(project.AudioPath);
        string musicStart = Mathf.Max(0f, project.AudioStartTime).ToString("0.000", CultureInfo.InvariantCulture);

        // raw RGBA frames in; H.264 out. The window can be an odd number of pixels wide / tall, which H.264
        // can't store, so one row / column is cropped off then. Options go before the output file (after it,
        // ffmpeg ignores them)
        const string crop = "crop=trunc(iw/2)*2:trunc(ih/2)*2";
        string inputs = $"-hide_banner -loglevel error -f rawvideo -pix_fmt rgba -s {exportWidth}x{exportHeight} -r {project.FPS} -i - ";
        string mapping;
        if (hasVoice && hasMusic)
        {
            inputs += $"-i \"{exportAudioPath}\" -ss {musicStart} -i \"{project.AudioPath}\" ";
            mapping = $"-filter_complex \"[0:v]{crop}[v];[1:a][2:a]amix=inputs=2:duration=first:normalize=0[a]\" -map \"[v]\" -map \"[a]\" ";
        }
        else if (hasVoice)
        {
            inputs += $"-i \"{exportAudioPath}\" ";
            mapping = $"-vf \"{crop}\" -map 0:v -map 1:a ";
        }
        else if (hasMusic)
        {
            // Music shorter than the video is padded with silence so the video is never cut short.
            inputs += $"-ss {musicStart} -i \"{project.AudioPath}\" ";
            mapping = $"-vf \"{crop}\" -af apad -map 0:v -map 1:a ";
        }
        else
            mapping = $"-vf \"{crop}\" ";
        string audio = hasVoice || hasMusic ? "-c:a aac -b:a 192k -shortest " : "";
        string arguments = inputs + mapping + $"-c:v {encoder} {HEncodeUtilities.QualityArgs(encoder)} -pix_fmt yuv420p " + audio +
                           $"-movflags +faststart -stats_period 0.25 -progress pipe:1 -nostats -y \"{exportMp4}\"";
        if (hasVoice || hasMusic)
            Console.WriteLine($"[MonkeFrames::Export] audio: {(hasVoice ? "replay voices" : "")}{(hasVoice && hasMusic ? " + " : "")}{(hasMusic ? project.AudioPath : "")}");

        Console.WriteLine($"[MonkeFrames::Export] ffmpeg {ffmpeg}");
        Console.WriteLine($"[MonkeFrames::Export] encoder {encoder} (GPU {SystemInfo.graphicsDeviceName}), {exportWidth}x{exportHeight} @ {project.FPS} fps");
        Console.WriteLine($"[MonkeFrames::Export] arguments {arguments}");

        lock (ffmpegLog) { ffmpegLog.Clear(); ffmpegLog.AppendLine($"ffmpeg {ffmpeg}"); ffmpegLog.AppendLine($"arguments {arguments}"); }
        ffmpegProcess = Process.Start(new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        });
        if (ffmpegProcess == null) throw new Exception("no process");
        // what ffmpeg says (errors), kept for the log and the status line
        ffmpegProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (ffmpegLog) ffmpegLog.AppendLine(e.Data); };
        ffmpegProcess.BeginErrorReadLine();
        // -progress pipe:1 reports "frame=N" as it encodes (drives the BUILDING VIDEO bar)
        ffmpegProcess.OutputDataReceived += (_, e) =>
        {
            string line = e.Data;
            if (line != null && line.StartsWith("frame=", StringComparison.Ordinal)
                && int.TryParse(line.Substring(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame))
                Interlocked.Exchange(ref ffmpegFramesDone, frame);
        };
        ffmpegProcess.BeginOutputReadLine();

        frameStream = new BinaryWriter(ffmpegProcess.StandardInput.BaseStream);
    }

    public void StartPlayback()
    {
        kCache = KeyframeManager.Instance.Project.CompiledKeyframes;
        InPlayback = true;
        UIManager.Instance.Drawing = false;
        KeyframeManager.Instance.DeleteOrbs();
        playbackPosition = 0;
        playbackAccumulator = 0f;
        playbackEnding = KeyframeManager.Instance.Project.CompiledKeyframes.Count;

        StartCoroutine("PlaybackCoroutine");
    }

    public void StopPlayback()
    {
        Blur.Set(false, 0f);
        PhotorealLink.KeyframeHour = -1f;
        InPlayback = false;
        UIManager.Instance.Drawing = true;
        KeyframeManager.Instance.RefreshOrbs();
        playbackPosition = 0;

        StopCoroutine("PlaybackCoroutine");
    }
}
