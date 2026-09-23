using GorillaNetworking;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

            Camera?.fieldOfView = FieldOfView;
        }

        if (!InPlayback)
        {
            float speed = 0.05f;

            if (Keyboard.current.shiftKey.isPressed)
                speed = 0.25f;
            if (Keyboard.current.ctrlKey.isPressed)
                speed = 0.005f;

            // Check keybinds
            if (Keyboard.current.wKey.isPressed)
                Position += transform.forward * speed;

            if (Keyboard.current.sKey.isPressed)
                Position -= transform.forward * speed;

            if (Keyboard.current.dKey.isPressed)
                Position += transform.right * speed;

            if (Keyboard.current.aKey.isPressed)
                Position -= transform.right * speed;

            if (Keyboard.current.eKey.isPressed)
                Position += transform.up * speed;

            if (Keyboard.current.qKey.isPressed)
                Position -= transform.up * speed;

            if (Keyboard.current.leftArrowKey.isPressed)
            {
                Vector3 eulers = Rotation.eulerAngles;
                eulers.z -= speed;

                Rotation = Quaternion.Euler(eulers);
            }

            if (Keyboard.current.rightArrowKey.isPressed)
            {
                Vector3 eulers = Rotation.eulerAngles;
                eulers.z += speed;

                Rotation = Quaternion.Euler(eulers);
            }

            UpdateFieldOfView();

            UpdateMouseLook();
        }

        if (InPlayback && Keyboard.current.spaceKey.wasPressedThisFrame)
            StopPlayback();
    }

    Vector2 mousePos = new Vector2(0, 0);

    // ---- Mouse look (with optional Caps Lock smoothing) ----
    private Vector2 _smoothedLook;
    private float _roll, _smoothedRoll;
    private bool _looking, _tilting;
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

        if (Mouse.current.rightButton.wasPressedThisFrame && !blocked) _tilting = true;
        if (!Mouse.current.rightButton.isPressed) _tilting = false;

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
            _smoothedLook = mousePos;
            _smoothedRoll = _roll;
        }

        Vector2 delta = Mouse.current.delta.ReadValue();
        if (_looking)
            mousePos += delta / 5f;
        if (_tilting)
            _roll -= delta.x * 0.12f;

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
    float playbackAccumulator = 0f;
    int playbackEnding;

    public bool doRecording = false;

    public Texture2D tex2d;
    public List<Keyframe> kCache;

    public RenderTexture renderTexture;
    public byte[] rBuffer;

    public BinaryWriter frameStream;
    public Process ffmpegProcess;

    public string outputMp4 => Path.Combine(Constants.DataFolder, "exports", KeyframeManager.Instance.Project.Name + ".mp4");

    IEnumerator PlaybackCoroutine()
    {
        var waitFrame = new WaitForEndOfFrame();

        while (InPlayback)
        {
            yield return waitFrame;

            if (playbackPosition >= playbackEnding - 1)
            {
                Blur.Set(false, 0f);
                bool wasRecording = doRecording;
                InPlayback = false;
                UIManager.Instance.Drawing = true;
                KeyframeManager.Instance.RefreshOrbs();
                playbackPosition = 0;
                doRecording = false;

                StopCoroutine("PlaybackCoroutine");

                // Plain playback (Project > Play) has no encoder to shut down.
                if (!wasRecording)
                {
                    UIManager.Instance.Status = "Playback finished.";
                    yield break;
                }

                UIManager.Instance.Status = "Finishing encoding..";

                frameStream.Flush();
                frameStream.Close();
                frameStream = null;

                ffmpegProcess.WaitForExit();
                ffmpegProcess.Dispose();
                ffmpegProcess = null;

                renderTexture.Release();
                Destroy(renderTexture);

                Process.Start("explorer.exe", $"/select,\"{outputMp4}\"");
                UIManager.Instance.Status = $"Exported {outputMp4}";
                yield break;
            }

            Keyframe currentFrame = kCache[playbackPosition];
            Blur.Set(currentFrame.MotionBlur, currentFrame.MotionBlurStrength);

            Position = currentFrame.Position;
            Rotation = currentFrame.QuatRotation;
            FieldOfView = currentFrame.FieldOfView;

            if (doRecording) {
                ScreenCapture.CaptureScreenshotIntoRenderTexture(renderTexture);

                var request = AsyncGPUReadback.Request(renderTexture, 0, TextureFormat.RGBA32);

                while (!request.done)
                    yield return null;

                if (request.hasError)
                    continue;

                var nativeArray = request.GetData<byte>();
                nativeArray.CopyTo(rBuffer);

                frameStream.Write(rBuffer, 0, rBuffer.Length);
                frameStream.Flush();

                Console.WriteLine($"Ffmpeg encode ... Flush frame {playbackPosition + 1}");
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
        renderTexture = new RenderTexture(Screen.width, Screen.height, 24);
        rBuffer = new byte[Screen.width * Screen.height * 4];

        StartFfmpegEncoder();
        doRecording = true;
        StartPlayback();
    }

    public void StartFfmpegEncoder()
    {
        var project = KeyframeManager.Instance.Project;

        Console.WriteLine("==== FFMPEG ENCODE STATS ====");

        string encoder = HEncodeUtilities.GetGoodEncoder();
        Console.WriteLine($"Encoding:     {encoder}");
        Console.WriteLine($"GPU dev name: {SystemInfo.graphicsDeviceName}");

        string arguments = $"-f rawvideo -pix_fmt rgba -s {Screen.width}x{Screen.height} -r {project.FPS} -i - " +
                           $"-c:v libx264 -pix_fmt yuv420p -y \"{outputMp4}\" " +
                           $"-loglevel quiet -preset ultrafast -c:v {encoder} -threads 0 " +
                           $"-crf 28";

        Console.WriteLine($"Arguments: {arguments}");

        Console.WriteLine("==== OK I'M DONE ====");

        ffmpegProcess = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Constants.MonkeFramesAssemblyFolder, "ffmpeg.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true
        });

        frameStream = new BinaryWriter(ffmpegProcess.StandardInput.BaseStream);

        Task.Run(() => {
            Win32Utilities.ShowMessageDialog("MonkeFrames Editor", "Your video is currently being processed. Please wait for the UI to appear before continuing.");
        });
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
        InPlayback = false;
        UIManager.Instance.Drawing = true;
        KeyframeManager.Instance.RefreshOrbs();
        playbackPosition = 0;

        StopCoroutine("PlaybackCoroutine");
    }
}
