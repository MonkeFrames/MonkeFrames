using MonkeFrames.Compiler.Models;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.UI;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Keyframe = MonkeFrames.Compiler.Models.Keyframe;

namespace MonkeFrames.Editor.Replays;

/// <summary>
/// "Replay Studio": a docked editing layout that only appears while you're editing a replay.
/// The game view shrinks into a viewport, with a camera preview + keyframe inspector on the
/// right, and replay transport + a keyframe timeline (in replay time) along the bottom.
/// </summary>
[DefaultExecutionOrder(9800)]
public class ReplayStudio : MonoBehaviour
{
    public static ReplayStudio Instance;

    // ---------------- Options ----------------
    /// <summary>Use the studio layout whenever a replay is open.</summary>
    public bool Enabled = true;
    /// <summary>The main viewport looks through the keyframe camera at the playhead.</summary>
    public bool FollowTimeline = false;
    public bool LivePreview = true;
    public bool ShowPath = true;

    public bool Active { get; private set; }

    // ---------------- Layout (GUI coordinates) ----------------
    public Rect Viewport, PreviewPanel, InspectorPanel, ReplayPanel, TimelinePanel;

    private static readonly string[] EffectNames = ["Linear", "Sine", "Ease In", "Ease Out", "Smooth", "Custom", "Cut"];
    private static readonly TransitionEffect[] EffectOrder =
    [
        TransitionEffect.Linear, TransitionEffect.Sine, TransitionEffect.EaseIn,
        TransitionEffect.EaseOut, TransitionEffect.Smooth, TransitionEffect.Custom, TransitionEffect.Cut,
    ];
    private static readonly float[] Speeds = [0.1f, 0.25f, 0.5f, 1f, 1.5f, 2f];
    private static readonly string[] SpeedLabels = ["0.1x", "0.25x", "0.5x", "1x", "1.5x", "2x"];

    // ---------------- State ----------------
    private bool _hadPlayer, _hadReplays;
    private bool _weSetExternal, _weSetBlur;
    private int _hash = int.MinValue;
    private ReplayClip _viewClip;
    private double _viewStart, _viewEnd = 10;

    private Camera _previewCam;
    private RenderTexture _previewRT;
    private float _nextPreview;
    private bool _previewFailed;

    private LineRenderer _path;
    private float _fovMin = 60, _fovMax = 90;

    // timeline interaction
    private int _dragKey = -1;
    private bool _dragMoved;
    private float _dragStartX;
    private float[] _dragTimes;
    private bool _scrubbing;
    private float _confirmClearUntil;

    private Vector2 _inspectorScroll;

    public ReplayStudio()
    {
        Instance = this;
    }

    private static ReplayManager RM => ReplayManager.Instance;
    private static KeyframeManager KM => KeyframeManager.Instance;
    private static List<Keyframe> Keys => KeyframeManager.Instance.Project.Keyframes;

    // =====================================================================
    //  Frame loop
    // =====================================================================

    private void Update()
    {
        ReplayManager rm = RM;
        UIManager ui = UIManager.Instance;
        CameraManager cm = CameraManager.Instance;

        bool want = Enabled && rm != null && rm.Viewing && rm.Clip != null && !rm.Recording
            && ui != null && ui.Drawing && cm != null && !cm.InPlayback && !cm.CinemachineState && KM != null;

        if (want != Active)
        {
            if (want) Enter();
            else Exit();
        }

        if (!Active)
            return;

        Layout();

        if (rm.Clip != _viewClip)
        {
            _viewClip = rm.Clip;
            _viewStart = 0;
            _viewEnd = Mathf.Max(1f, rm.Clip.Length);
        }

        RebuildIfChanged();
        HandleShortcuts(rm);
        UpdateFollow(rm, cm);
    }

    private void LateUpdate()
    {
        CameraManager cm = CameraManager.Instance;
        if (cm == null || cm.Camera == null)
            return;

        if (Active)
        {
            // Game view only fills the viewport. Camera.rect uses a bottom-left origin.
            cm.Camera.rect = new Rect(
                Viewport.x / Screen.width,
                1f - Viewport.yMax / Screen.height,
                Viewport.width / Screen.width,
                Viewport.height / Screen.height);

            UpdatePath();
            RenderPreview(cm);
        }
    }

    private void Enter()
    {
        Active = true;
        UIManager ui = UIManager.Instance;
        foreach (var w in ui.Windows)
        {
            if (w.Window.Name == "Player") { _hadPlayer = w.Visible; w.Visible = false; }
            if (w.Window.Name == "Replays") { _hadReplays = w.Visible; w.Visible = false; }
        }
        _hash = int.MinValue;
        UIManager.Instance.Status = "Replay Studio: V adds a keyframe at the playhead, Space plays / pauses.";
    }

    private void Exit()
    {
        Active = false;

        CameraManager cm = CameraManager.Instance;
        if (cm != null && cm.Camera != null)
            cm.Camera.rect = new Rect(0, 0, 1, 1);

        ReleaseFollow(cm);

        if (_path != null) _path.enabled = false;

        UIManager ui = UIManager.Instance;
        if (ui != null)
            foreach (var w in ui.Windows)
            {
                if (w.Window.Name == "Player" && _hadPlayer) w.Visible = true;
                if (w.Window.Name == "Replays" && _hadReplays) w.Visible = true;
            }
        _hadPlayer = _hadReplays = false;
    }

    private void Layout()
    {
        float W = Screen.width, H = Screen.height;
        float top = UIManager.MenuBarHeight;
        float right = Mathf.Clamp(W * 0.25f, 300f, 430f);
        float bottom = Mathf.Clamp(H * 0.32f, 240f, 360f);
        float replayW = Mathf.Clamp(W * 0.27f, 330f, 440f);

        Viewport = new Rect(0, top, W - right, H - top - bottom);

        float previewH = 30 + (right - 16) * 9f / 16f + 96;
        PreviewPanel = new Rect(W - right, top, right, previewH);
        InspectorPanel = new Rect(W - right, top + previewH, right, H - top - previewH - bottom);
        ReplayPanel = new Rect(0, H - bottom, replayW, bottom);
        TimelinePanel = new Rect(replayW, H - bottom, W - replayW, bottom);
    }

    /// <summary>True if the mouse (GUI coordinates) is over a studio panel, not the viewport.</summary>
    public bool OverPanels(Vector2 gui) =>
        Active && (PreviewPanel.Contains(gui) || InspectorPanel.Contains(gui) || ReplayPanel.Contains(gui) || TimelinePanel.Contains(gui));

    private void HandleShortcuts(ReplayManager rm)
    {
        if (GUIUtility.keyboardControl != 0 || Keyboard.current == null)
            return;
        var kb = Keyboard.current;

        if (kb.spaceKey.wasPressedThisFrame)
            rm.TogglePlay();
        if (kb.commaKey.wasPressedThisFrame)
            Step(rm, -1);
        if (kb.periodKey.wasPressedThisFrame)
            Step(rm, 1);
    }

    private static void Step(ReplayManager rm, int frames)
    {
        rm.Playing = false;
        rm.Seek(rm.Time + frames / (double)Mathf.Max(1, rm.Clip.Rate));
    }

    // =====================================================================
    //  Keyframes in replay time
    // =====================================================================

    /// <summary>Replay time of each keyframe. Keyframe 0 sits at the replay's In point.</summary>
    private float[] KeyTimes()
    {
        List<Keyframe> keys = Keys;
        float[] t = new float[keys.Count];
        float acc = RM.Clip.In;
        for (int i = 0; i < keys.Count; i++)
        {
            t[i] = acc;
            acc += keys[i].Transition.Duration;
        }
        return t;
    }

    private static void SetDuration(int i, float d)
    {
        Keyframe k = Keys[i];
        k.Transition.Duration = Mathf.Max(0.02f, d);
        Keys[i] = k;
    }

    private static Keyframe KeyFromCamera()
    {
        CameraManager cm = CameraManager.Instance;
        Keyframe k = new Keyframe
        {
            Position = cm.Position,
            Rotation = cm.Rotation.eulerAngles,
            FieldOfView = cm.FieldOfView,
        };
        k.Transition.Duration = 2f;
        if (Settings.current?.SmoothByDefault == true)
            k.Transition.Effect = TransitionEffect.Smooth;
        return k;
    }

    /// <summary>Add a keyframe from the current camera exactly at the replay playhead.</summary>
    public void AddKeyframeAtPlayhead()
    {
        ReplayManager rm = RM;
        if (rm?.Clip == null) return;

        List<Keyframe> keys = Keys;
        float t = (float)rm.Time;
        Keyframe k = KeyFromCamera();
        int index;

        if (keys.Count == 0)
        {
            rm.Clip.InPoint = t;
            keys.Add(k);
            index = 0;
        }
        else
        {
            float[] times = KeyTimes();

            // Same spot as an existing keyframe: update it instead.
            int near = -1;
            for (int i = 0; i < times.Length; i++)
                if (Mathf.Abs(times[i] - t) < 0.02f) near = i;

            if (near >= 0)
            {
                Keyframe old = keys[near];
                k.Transition = old.Transition;
                k.MotionBlur = old.MotionBlur;
                k.MotionBlurStrength = old.MotionBlurStrength;
                keys[near] = k;
                index = near;
            }
            else if (t < times[0])
            {
                // Before the first keyframe: the animation now starts here.
                k.Transition = keys[0].Transition;
                k.Transition.Duration = times[0] - t;
                rm.Clip.InPoint = t;
                keys.Insert(0, k);
                index = 0;
            }
            else
            {
                int i = times.Length - 1;
                while (i > 0 && times[i] > t) i--;

                k.Transition.Effect = keys[i].Transition.Effect;
                k.Transition.CustomSpeed = keys[i].Transition.CustomSpeed;
                k.MotionBlur = keys[i].MotionBlur;
                k.MotionBlurStrength = keys[i].MotionBlurStrength;

                if (i == times.Length - 1)
                {
                    SetDuration(i, t - times[i]);
                    keys.Add(k);
                    index = keys.Count - 1;
                }
                else
                {
                    float next = times[i + 1];
                    SetDuration(i, t - times[i]);
                    k.Transition.Duration = Mathf.Max(0.02f, next - t);
                    keys.Insert(i + 1, k);
                    index = i + 1;
                }
            }
        }

        rm.Clip.Dirty = true;
        UIManager.Instance.Selection = index;
        KM.RefreshOrbs();
        UIManager.Instance.Status = $"Keyframe {index} at {ReplayManager.FormatTime(t)}.";
    }

    /// <summary>Delete a keyframe without moving the others in time.</summary>
    public void DeleteKeyframe(int i)
    {
        List<Keyframe> keys = Keys;
        if (i < 0 || i >= keys.Count) return;

        float[] times = KeyTimes();
        if (i == 0 && keys.Count > 1)
            RM.Clip.InPoint = times[1];
        else if (i > 0 && i < keys.Count - 1)
            SetDuration(i - 1, times[i + 1] - times[i - 1]);

        keys.RemoveAt(i);
        UIManager.Instance.Selection = Mathf.Min(i, keys.Count - 1);
        RM.Clip.Dirty = true;
        KM.RefreshOrbs();
        UIManager.Instance.Status = $"Deleted keyframe {i}.";
    }

    /// <summary>Move keyframe i to replay time t, keeping every other keyframe where it is.</summary>
    private void MoveKey(int i, float t, float[] times)
    {
        List<Keyframe> keys = Keys;
        if (i < 0 || i >= keys.Count) return;
        ReplayClip clip = RM.Clip;
        const float gap = 0.05f;

        float lo = i > 0 ? times[i - 1] + gap : 0f;
        float hi = i + 1 < keys.Count ? times[i + 1] - gap : clip.Length;
        t = Mathf.Clamp(t, lo, Mathf.Max(lo, hi));

        if (i == 0)
            clip.InPoint = t;
        else
            SetDuration(i - 1, t - times[i - 1]);

        if (i + 1 < keys.Count)
            SetDuration(i, times[i + 1] - t);

        clip.Dirty = true;
    }

    private void RebuildIfChanged()
    {
        Project p = KM.Project;
        int h;
        unchecked
        {
            h = 17 * 31 + p.FPS;
            h = h * 31 + p.Smoothness.GetHashCode();
            foreach (Keyframe k in p.Keyframes)
            {
                h = h * 31 + k.Position.GetHashCode();
                h = h * 31 + k.Rotation.GetHashCode();
                h = h * 31 + k.FieldOfView.GetHashCode();
                h = h * 31 + (int)k.Transition.Effect;
                h = h * 31 + k.Transition.Duration.GetHashCode();
                h = h * 31 + (k.MotionBlur ? 1 : 0);
                h = h * 31 + k.Transition.CurveX1.GetHashCode() ^ k.Transition.CurveY1.GetHashCode();
                h = h * 31 + k.Transition.CurveX2.GetHashCode() ^ k.Transition.CurveY2.GetHashCode();
                h = h * 31 + (k.Transition.CustomSpeed ? 1 : 0);
            }
        }
        if (h == _hash) return;
        _hash = h;

        if (p.Keyframes.Count == 0)
        {
            p.CompiledKeyframes = new List<Keyframe>();
        }
        else
        {
            try { p.Build().Wait(); }
            catch (System.Exception ex) { System.Console.WriteLine($"[MonkeFrames::Studio] Build failed: {ex.Message}"); }
        }

        var frames = p.CompiledKeyframes;
        _fovMin = 179; _fovMax = 1;
        if (frames != null)
            foreach (Keyframe f in frames)
            {
                _fovMin = Mathf.Min(_fovMin, f.FieldOfView);
                _fovMax = Mathf.Max(_fovMax, f.FieldOfView);
            }
        if (_fovMax - _fovMin < 10f)
        {
            float mid = (_fovMax + _fovMin) / 2f;
            _fovMin = mid - 5f;
            _fovMax = mid + 5f;
        }

        _pathDirty = true;
    }

    /// <summary>The compiled keyframe camera at a replay time (null if there's no animation).</summary>
    private Keyframe? FrameAt(double time)
    {
        var frames = KM.Project.CompiledKeyframes;
        if (frames == null || frames.Count == 0 || RM?.Clip == null)
            return null;
        int i = Mathf.Clamp(Mathf.RoundToInt((float)((time - RM.Clip.In) * KM.Project.FPS)), 0, frames.Count - 1);
        return frames[i];
    }

    // =====================================================================
    //  Viewport follows the camera path
    // =====================================================================

    private void UpdateFollow(ReplayManager rm, CameraManager cm)
    {
        bool freeMode = CameraModes.Instance == null || CameraModes.Instance.Mode == CameraMode.Free;
        Keyframe? f = FollowTimeline && freeMode ? FrameAt(rm.Time) : null;

        if (f == null)
        {
            ReleaseFollow(cm);
            return;
        }

        Keyframe k = f.Value;
        cm.Position = k.Position;
        cm.Rotation = k.QuatRotation;
        cm.FieldOfView = k.FieldOfView;
        cm.ExternalControl = true;
        _weSetExternal = true;

        bool blur = rm.Playing && k.MotionBlur;
        if (blur || _weSetBlur)
            cm.Blur.Set(blur, k.MotionBlurStrength);
        _weSetBlur = blur;
    }

    private void ReleaseFollow(CameraManager cm)
    {
        if (cm == null) return;
        if (_weSetExternal)
        {
            cm.ExternalControl = CameraModes.Instance != null && CameraModes.Instance.Mode != CameraMode.Free;
            cm.CancelLookSmoothing();
            _weSetExternal = false;
        }
        if (_weSetBlur)
        {
            cm.Blur.Set(false, 0f);
            _weSetBlur = false;
        }
    }

    // =====================================================================
    //  Camera path line + live preview
    // =====================================================================

    private bool _pathDirty = true;

    private void UpdatePath()
    {
        var frames = KM.Project.CompiledKeyframes;
        bool show = ShowPath && frames != null && frames.Count > 1;

        if (!show)
        {
            if (_path != null) _path.enabled = false;
            return;
        }

        if (_path == null)
        {
            GameObject go = new GameObject("MonkeFrames Camera Path");
            DontDestroyOnLoad(go);
            _path = go.AddComponent<LineRenderer>();
            _path.useWorldSpace = true;
            _path.startWidth = _path.endWidth = 0.035f;
            Shader sh = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Sprites/Default");
            if (sh != null) _path.material = new Material(sh);
            _pathDirty = true;
        }

        if (_pathDirty)
        {
            _pathDirty = false;
            int n = Mathf.Min(frames.Count, 600);
            _path.positionCount = n;
            for (int i = 0; i < n; i++)
            {
                int fi = n == 1 ? 0 : Mathf.RoundToInt(i / (float)(n - 1) * (frames.Count - 1));
                _path.SetPosition(i, frames[fi].Position);
            }
            Color c = Theme.Accent;
            _path.startColor = c.WithAlpha(0.9f);
            _path.endColor = c.WithAlpha(0.9f);
            if (_path.material != null) _path.material.color = c;
        }

        _path.enabled = true;
    }

    private void RenderPreview(CameraManager cm)
    {
        if (!LivePreview || _previewFailed)
            return;

        Keyframe? f = FrameAt(RM.Time);
        if (f == null)
            return;

        if (Time.unscaledTime < _nextPreview)
            return;
        _nextPreview = Time.unscaledTime + 1f / 30f;

        int w = Mathf.Clamp(Mathf.RoundToInt(PreviewPanel.width - 16), 64, 720);
        int h = Mathf.RoundToInt(w * 9f / 16f);

        try
        {
            if (_previewRT == null || _previewRT.width != w || _previewRT.height != h)
            {
                if (_previewRT != null) { _previewRT.Release(); Destroy(_previewRT); }
                _previewRT = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "MonkeFrames Studio Preview", hideFlags = HideFlags.HideAndDontSave };
                _previewRT.Create();
            }

            if (_previewCam == null)
            {
                GameObject go = new GameObject("MonkeFrames Studio Preview Camera");
                DontDestroyOnLoad(go);
                _previewCam = go.AddComponent<Camera>();
                _previewCam.enabled = false;
            }

            RM.ApplyPose();

            _previewCam.CopyFrom(cm.Camera);
            _previewCam.enabled = false;
            _previewCam.rect = new Rect(0, 0, 1, 1);
            _previewCam.targetTexture = _previewRT;
            Keyframe k = f.Value;
            _previewCam.transform.SetPositionAndRotation(k.Position, k.QuatRotation);
            _previewCam.fieldOfView = k.FieldOfView;
            _previewCam.Render();
        }
        catch (System.Exception ex)
        {
            _previewFailed = true;
            System.Console.WriteLine($"[MonkeFrames::Studio] Preview disabled: {ex}");
            UIManager.Instance.Status = "Camera preview couldn't start on this setup.";
        }
    }

    private void OnDestroy()
    {
        if (_previewRT != null) { _previewRT.Release(); Destroy(_previewRT); }
        if (_previewCam != null) Destroy(_previewCam.gameObject);
        if (_path != null) Destroy(_path.gameObject);
    }

    // =====================================================================
    //  GUI
    // =====================================================================

    /// <summary>Called by UIManager.OnGUI, under the floating windows.</summary>
    public void DrawGUI()
    {
        if (!Active || RM?.Clip == null)
            return;

        Layout();
        ReplayManager rm = RM;

        DrawViewportFrame(rm);
        DrawPreview(rm);
        DrawInspector(rm);
        DrawReplayPanel(rm);
        DrawTimeline(rm);
    }

    private static void Panel(Rect r, string title, out Rect body)
    {
        Theme.Fill(r, Theme.Background, 0);
        Theme.Fill(new Rect(r.x, r.y, 1, r.height), Theme.Border, 0);
        Theme.Fill(new Rect(r.x, r.y, r.width, 1), Theme.Border, 0);
        Rect head = new Rect(r.x + 1, r.y + 1, r.width - 1, 26);
        Theme.Fill(head, Theme.Surface, 0);
        Theme.Fill(new Rect(head.x, head.y + 6, 3, head.height - 12), Theme.Accent, 1);
        Theme.DrawText(new Rect(head.x + 12, head.y, head.width - 20, head.height), title, Theme.Header, Theme.Text);
        body = new Rect(r.x + 10, r.y + 34, r.width - 20, r.height - 42);
    }

    // ---------------- Viewport ----------------

    private void DrawViewportFrame(ReplayManager rm)
    {
        Rect v = Viewport;
        Color edge = Theme.Accent.WithAlpha(0.5f);
        Theme.Fill(new Rect(v.x, v.yMax - 1, v.width, 1), edge, 0);
        Theme.Fill(new Rect(v.xMax - 1, v.y, 1, v.height), edge, 0);

        bool following = FollowTimeline && FrameAt(rm.Time) != null
            && (CameraModes.Instance == null || CameraModes.Instance.Mode == CameraMode.Free);
        string mode = following ? "VIEWING CAMERA PATH"
            : CameraModes.Instance != null && CameraModes.Instance.Mode != CameraMode.Free ? CameraModes.ModeName(CameraModes.Instance.Mode).ToUpper() + " CAMERA"
            : "FREE CAMERA";

        Chip(new Vector2(v.x + 10, v.y + 10), mode, following ? Theme.Accent : Theme.Raised, following ? Theme.OnAccent : Theme.Text);

        string time = (rm.Playing ? "▶ " : "") + "REPLAY " + ReplayManager.FormatTime(rm.Time);
        float tw = Theme.LabelCenterSmall.CalcSize(new GUIContent(time)).x + 20;
        Chip(new Vector2(v.xMax - tw - 10, v.y + 10), time, Theme.Danger.WithAlpha(0.85f), Color.white);
    }

    private static void Chip(Vector2 at, string text, Color bg, Color fg)
    {
        float w = Theme.LabelCenterSmall.CalcSize(new GUIContent(text)).x + 20;
        Rect r = new Rect(at.x, at.y, w, 22);
        Theme.Fill(r, bg, 11);
        Theme.DrawText(r, text, Theme.LabelCenterSmall, fg);
    }

    // ---------------- Camera preview ----------------

    private void DrawPreview(ReplayManager rm)
    {
        Panel(PreviewPanel, "CAMERA PREVIEW", out Rect b);

        float imgH = b.width * 9f / 16f;
        Rect img = new Rect(b.x, b.y, b.width, imgH);
        Theme.Fill(img, Color.black, 4);

        Keyframe? f = FrameAt(rm.Time);
        if (f == null)
        {
            GUI.Label(new Rect(img.x + 12, img.center.y - 20, img.width - 24, 40),
                "Add keyframes (V) to see your camera path here.", Theme.MutedWrap);
        }
        else if (!LivePreview || _previewRT == null || _previewFailed)
        {
            GUI.Label(new Rect(img.x, img.center.y - 12, img.width, 24), _previewFailed ? "Preview unavailable" : "Preview off", Theme.MutedCenter);
        }
        else
        {
            GUI.DrawTexture(img, _previewRT, ScaleMode.ScaleToFit, false);
            Theme.DrawText(new Rect(img.x + 8, img.yMax - 22, img.width - 16, 20),
                $"FOV {f.Value.FieldOfView:0}°", Theme.MutedSmall, Color.white.WithAlpha(0.8f));
        }

        float y = img.yMax + 8;
        FollowTimeline = Widgets.Switch("st.follow", new Rect(b.x, y, b.width, 24), FollowTimeline, "Viewport follows camera path",
            "Look through your keyframe camera in the main view. Turn off to fly freely and place keyframes.");
        y += 28;
        LivePreview = Widgets.Switch("st.preview", new Rect(b.x, y, b.width / 2f, 24), LivePreview, "Live preview");
        ShowPath = Widgets.Switch("st.path", new Rect(b.x + b.width / 2f, y, b.width / 2f, 24), ShowPath, "Show path",
            "Draw the camera's path in the world.");
    }

    // ---------------- Keyframe inspector ----------------

    private void DrawInspector(ReplayManager rm)
    {
        Panel(InspectorPanel, "KEYFRAME", out Rect b);
        List<Keyframe> keys = Keys;
        int sel = UIManager.Instance.Selection;

        if (sel < 0 || sel >= keys.Count)
        {
            GUI.Label(new Rect(b.x, b.y, b.width, 60),
                keys.Count == 0
                    ? "No keyframes yet. Scrub the replay, fly the camera where you want it and press V (or Add Keyframe)."
                    : "Click a keyframe on the timeline to edit it.",
                Theme.MutedWrap);
            return;
        }

        float[] times = KeyTimes();
        Keyframe k = keys[sel];
        float x = b.x, w = b.width, y = b.y;

        Rect content = new Rect(0, 0, w - 14, 330);
        _inspectorScroll = GUI.BeginScrollView(new Rect(b.x, b.y, b.width, b.height), _inspectorScroll, content);
        x = 0; y = 0; w = content.width;

        GUI.Label(new Rect(x, y, w, 22), $"Keyframe {sel}", Theme.Label);
        GUI.Label(new Rect(x, y, w, 22), "at " + ReplayManager.FormatTime(times[sel]), Theme.MutedRight);
        y += 26;

        GUI.Label(new Rect(x, y, w, 18), "Transition to next", Theme.MutedSmall);
        y += 20;
        int cur = System.Array.IndexOf(EffectOrder, k.Transition.Effect);
        int pick = Widgets.ChipGrid("st.effect", new Rect(x, y, w, 60), cur, EffectNames, 4, 28f, 4f);
        if (pick != cur && pick >= 0)
        {
            k.Transition.Effect = EffectOrder[pick];
            keys[sel] = k;
        }
        y += 68;

        GUI.Label(new Rect(x, y, 90, 24), sel == keys.Count - 1 ? "Hold" : "Duration");
        float dur = Widgets.FloatField("st.dur", new Rect(x + 90, y, 90, 24), "s", Theme.Accent, k.Transition.Duration);
        if (!Mathf.Approximately(dur, k.Transition.Duration))
        {
            SetDuration(sel, dur);
            k = keys[sel];
        }
        GUI.Label(new Rect(x + 190, y, w - 190, 24), "shifts later keys", Theme.MutedSmall);
        y += 30;

        GUI.Label(new Rect(x, y, 90, 24), "FOV");
        float fov = Widgets.Slider("st.fov", new Rect(x + 90, y, w - 90 - 50, 24), k.FieldOfView, 20f, 130f);
        GUI.Label(new Rect(x + w - 46, y, 46, 24), $"{k.FieldOfView:0}°", Theme.LabelRight);
        if (!Mathf.Approximately(fov, k.FieldOfView))
        {
            k.FieldOfView = Mathf.Round(fov);
            keys[sel] = k;
        }
        y += 30;

        bool blur = Widgets.Switch("st.blur", new Rect(x, y, 130, 24), k.MotionBlur, "Motion blur");
        GUI.enabled = blur;
        float str = Widgets.Slider("st.blurstr", new Rect(x + 140, y, w - 140 - 46, 24), k.MotionBlurStrength, 0f, 1f);
        GUI.Label(new Rect(x + w - 42, y, 42, 24), $"{str * 100f:0}%", Theme.LabelRight);
        GUI.enabled = true;
        if (blur != k.MotionBlur || !Mathf.Approximately(str, k.MotionBlurStrength))
        {
            k.MotionBlur = blur;
            k.MotionBlurStrength = str;
            keys[sel] = k;
        }
        y += 34;

        float bw = (w - 8) / 3f;
        if (GUI.Button(new Rect(x, y, bw, 28), "Go to (F)"))
        {
            rm.Seek(times[sel]);
            KM.GoToKeyframe(sel);
            FollowTimeline = false;
        }
        if (GUI.Button(new Rect(x + bw + 4, y, bw, 28), "Update (X)"))
            UpdateSelected();
        if (GUI.Button(new Rect(x + (bw + 4) * 2, y, bw, 28), "Delete", Theme.DangerButton))
            DeleteKeyframe(sel);
        y += 34;

        if (GUI.Button(new Rect(x, y, w, 26), "Open Keyframe Editor (speed curve)"))
            UIManager.Instance.OpenWindow("Keyframe Editor");

        GUI.EndScrollView();
    }

    /// <summary>Jump the replay and the camera to keyframe i.</summary>
    public void GoTo(int i)
    {
        if (i < 0 || i >= Keys.Count) return;
        RM.Playing = false;
        RM.Seek(KeyTimes()[i]);
        KM.GoToKeyframe(i);
        FollowTimeline = false;
    }

    private void UpdateSelected()
    {
        int sel = UIManager.Instance.Selection;
        List<Keyframe> keys = Keys;
        if (sel < 0 || sel >= keys.Count) return;

        Keyframe old = keys[sel];
        Keyframe k = KeyFromCamera();
        k.Transition = old.Transition;
        k.MotionBlur = old.MotionBlur;
        k.MotionBlurStrength = old.MotionBlurStrength;
        keys[sel] = k;
        KM.RefreshOrbs();
        UIManager.Instance.Status = $"Keyframe {sel} moved to the current camera.";
    }

    // ---------------- Replay transport ----------------

    private void DrawReplayPanel(ReplayManager rm)
    {
        ReplayClip clip = rm.Clip;
        Panel(ReplayPanel, "REPLAY", out Rect b);
        float x = b.x, w = b.width, y = b.y;

        Theme.DrawText(new Rect(x, y, w - 90, 22), clip.Name, Theme.Label, Theme.Text);
        if (GUI.Button(new Rect(x + w - 84, y - 1, 84, 24), "Library"))
            UIManager.Instance.OpenWindow("Replays");
        y += 28;

        // Transport
        float bw = 40;
        if (GUI.Button(new Rect(x, y, bw, 30), "|<")) { rm.Playing = false; rm.Seek(clip.In); }
        if (GUI.Button(new Rect(x + 44, y, bw, 30), "<")) Step(rm, -1);
        if (GUI.Button(new Rect(x + 88, y, 84, 30), rm.Playing ? "Pause" : "Play", Theme.AccentButton)) rm.TogglePlay();
        if (GUI.Button(new Rect(x + 176, y, bw, 30), ">")) Step(rm, 1);
        if (GUI.Button(new Rect(x + 220, y, bw, 30), ">|")) { rm.Playing = false; rm.Seek(clip.Out); }
        rm.Loop = Widgets.Switch("st.loop", new Rect(x + 270, y + 3, w - 270, 24), rm.Loop, "Loop");
        y += 38;

        // Speed
        GUI.Label(new Rect(x, y, 56, 26), "Speed");
        int sp = Widgets.Segmented("st.speed", new Rect(x + 56, y, w - 56, 26), System.Array.IndexOf(Speeds, rm.Speed), SpeedLabels);
        if (sp >= 0) rm.Speed = Speeds[sp];
        y += 34;

        // Frame slider
        int total = Mathf.Max(1, clip.FrameCount - 1);
        int frame = Mathf.RoundToInt((float)(rm.Time * clip.Rate));
        float f = Widgets.Slider("st.frame", new Rect(x, y, w, 24), frame, 0, total);
        if (Mathf.RoundToInt(f) != frame)
        {
            rm.Playing = false;
            rm.Seek(Mathf.Round(f) / clip.Rate);
        }
        y += 26;
        GUI.Label(new Rect(x, y, w, 20), $"Frame {frame} / {total}", Theme.MutedSmall);
        GUI.Label(new Rect(x, y, w, 20), $"{ReplayManager.FormatTime(rm.Time)} / {ReplayManager.FormatTime(clip.Length)}", Theme.MutedRight);
        y += 26;

        Widgets.Divider(x, y, w);
        y += 8;
        rm.HideLive = Widgets.Switch("st.hidelive", new Rect(x, y, w / 2f, 24), rm.HideLive, "Hide live players");
        rm.MuteGame = Widgets.Switch("st.mute", new Rect(x + w / 2f, y, w / 2f, 24), rm.MuteGame, "Mute game");
        y += 28;
        GUI.Label(new Rect(x, y, 60, 24), "Voices");
        rm.VoiceVolume = Widgets.Slider("st.vol", new Rect(x + 60, y, w - 60 - 110, 24), rm.VoiceVolume, 0f, 2f);
        GUI.Label(new Rect(x + w - 106, y, 40, 24), $"{rm.VoiceVolume * 100f:0}%", Theme.LabelRight);
        if (GUI.Button(new Rect(x + w - 62, y, 62, 24), "Exit"))
        {
            Enabled = false;
            UIManager.Instance.Status = "Replay Studio closed. Turn it back on from Replays > Replay Studio.";
        }
    }

    // ---------------- Timeline ----------------

    private void DrawTimeline(ReplayManager rm)
    {
        ReplayClip clip = rm.Clip;
        Panel(TimelinePanel, "TIMELINE", out Rect b);
        List<Keyframe> keys = Keys;
        int sel = UIManager.Instance.Selection;
        Event e = Event.current;

        // ---- Toolbar ----
        float tx = b.x, ty = b.y - 2;
        if (GUI.Button(new Rect(tx, ty, 124, 26), "Add Keyframe (V)", Theme.AccentButton)) AddKeyframeAtPlayhead();
        tx += 128;
        GUI.enabled = sel >= 0 && sel < keys.Count;
        if (GUI.Button(new Rect(tx, ty, 92, 26), "Update (X)")) UpdateSelected();
        tx += 96;
        if (GUI.Button(new Rect(tx, ty, 70, 26), "Delete")) DeleteKeyframe(sel);
        tx += 74;
        GUI.enabled = keys.Count > 0;
        bool confirming = Time.unscaledTime < _confirmClearUntil;
        if (GUI.Button(new Rect(tx, ty, 80, 26), confirming ? "Sure?" : "Clear All", confirming ? Theme.DangerButton : GUI.skin.button))
        {
            if (confirming)
            {
                keys.Clear();
                UIManager.Instance.Selection = -1;
                KM.RefreshOrbs();
                _confirmClearUntil = 0;
                UIManager.Instance.Status = "Cleared all keyframes.";
            }
            else _confirmClearUntil = Time.unscaledTime + 3f;
        }
        tx += 84;
        GUI.enabled = true;
        if (b.xMax - tx > 250)
        {
            if (GUI.Button(new Rect(b.xMax - 244, ty, 76, 26), "Save")) new Menus.FileMenu().SaveProject();
            if (GUI.Button(new Rect(b.xMax - 164, ty, 76, 26), "Open")) new Menus.FileMenu().OpenProject();
            if (GUI.Button(new Rect(b.xMax - 84, ty, 84, 26), "Export", Theme.AccentButton)) Export();
        }

        // ---- Tracks area ----
        const float labelW = 74f, rulerH = 20f, rowH = 22f;
        string[] rows = ["Keys", "Motion", "FOV", "Blur", "Gorillas", "Voice"];
        Rect area = new Rect(b.x, b.y + 32, b.width, rulerH + rows.Length * rowH + 4);
        Rect track = new Rect(area.x + labelW, area.y, area.width - labelW, area.height);

        Theme.Fill(area, Theme.Field, 6);
        double span = System.Math.Max(0.05, _viewEnd - _viewStart);
        float ToX(double t) => track.x + (float)((t - _viewStart) / span) * track.width;
        double ToT(float px) => _viewStart + (px - track.x) / track.width * span;

        // Trimmed-out regions
        float inX = Mathf.Clamp(ToX(clip.In), track.x, track.xMax), outX = Mathf.Clamp(ToX(clip.Out), track.x, track.xMax);
        Theme.Fill(new Rect(track.x, track.y, inX - track.x, track.height), Color.black.WithAlpha(0.35f), 0);
        Theme.Fill(new Rect(outX, track.y, track.xMax - outX, track.height), Color.black.WithAlpha(0.35f), 0);

        // Ruler
        double step = NiceStep(span / Mathf.Max(1f, track.width / 80f));
        for (double t = System.Math.Ceiling(_viewStart / step) * step; t <= _viewEnd; t += step)
        {
            float px = ToX(t);
            Theme.Fill(new Rect(px, track.y + rulerH - 6, 1, 6), Theme.TextMuted.WithAlpha(0.5f), 0);
            Theme.Fill(new Rect(px, track.y + rulerH, 1, track.height - rulerH), Theme.Border, 0);
            Theme.DrawText(new Rect(px + 3, track.y + 1, 70, 16), step < 1 ? $"{t:0.0}s" : ReplayManager.FormatTime(t).Replace(".0", ""), Theme.MutedSmall, Theme.TextMuted);
        }

        // Row labels + separators
        for (int r = 0; r < rows.Length; r++)
        {
            float ry = track.y + rulerH + r * rowH;
            Theme.DrawText(new Rect(area.x + 10, ry, labelW - 12, rowH), rows[r], Theme.MutedSmall, Theme.TextMuted);
            Theme.Fill(new Rect(area.x, ry, area.width, 1), Theme.Border, 0);
        }
        float RowY(int r) => track.y + rulerH + r * rowH;

        GUI.BeginClip(new Rect(track.x, track.y, track.width, track.height));
        // Everything inside the clip uses local coordinates.
        float ox = track.x, oy = track.y;
        float L(double t) => ToX(t) - ox;

        float[] times = keys.Count > 0 ? KeyTimes() : System.Array.Empty<float>();
        var frames = KM.Project.CompiledKeyframes;

        if (e.type == EventType.Repaint)
        {
            // Motion row: transition segments
            for (int i = 0; i < keys.Count; i++)
            {
                float a = L(times[i]);
                float z = L(times[i] + keys[i].Transition.Duration);
                bool last = i == keys.Count - 1;
                Color c = EffectColor(keys[i].Transition.Effect).WithAlpha(last ? 0.25f : (i == sel ? 0.9f : 0.6f));
                Rect seg = new Rect(a + 1, RowY(1) - oy + 4, Mathf.Max(2, z - a - 2), rowH - 8);
                Theme.Fill(seg, c, 3);
                if (seg.width > 50 && !last)
                    Theme.DrawText(seg, EffectNames[Mathf.Max(0, System.Array.IndexOf(EffectOrder, keys[i].Transition.Effect))], Theme.LabelCenterSmall, Color.white);

                // Blur row
                if (keys[i].MotionBlur && !last)
                    Theme.Fill(new Rect(a + 1, RowY(3) - oy + 7, Mathf.Max(2, z - a - 2), rowH - 14), Theme.AxisZ.WithAlpha(0.35f + 0.6f * keys[i].MotionBlurStrength), 3);
            }

            // FOV row: line of the compiled field of view
            if (frames != null && frames.Count > 1)
            {
                float fy0 = RowY(2) - oy + 3, fh = rowH - 6;
                float prevY = -1;
                for (float px = Mathf.Max(0, L(clip.In)); px < track.width; px += 2f)
                {
                    double t = ToT(px + ox);
                    Keyframe? fk = FrameAt(t);
                    if (fk == null) break;
                    if (t > clip.In + frames.Count / (double)KM.Project.FPS) break;
                    float v = Mathf.InverseLerp(_fovMin, _fovMax, fk.Value.FieldOfView);
                    float yy = fy0 + fh * (1f - v);
                    float y0 = prevY < 0 ? yy : Mathf.Min(prevY, yy), y1 = prevY < 0 ? yy : Mathf.Max(prevY, yy);
                    Theme.Fill(new Rect(px, y0, 2, Mathf.Max(1.5f, y1 - y0 + 1)), Theme.AxisY.WithAlpha(0.85f), 0);
                    prevY = yy;
                }
            }

            // Gorillas row: when each gorilla is present (thin lanes)
            int lanes = Mathf.Min(clip.Tracks.Count, 6);
            float laneH = lanes > 0 ? Mathf.Max(2f, (rowH - 6) / lanes) : 0;
            for (int i = 0; i < lanes; i++)
            {
                ReplayTrack t = clip.Tracks[i];
                float a = L(t.StartFrame / (double)clip.Rate), z = L(t.EndFrame / (double)clip.Rate);
                Theme.Fill(new Rect(a, RowY(4) - oy + 3 + i * laneH, Mathf.Max(2, z - a), laneH - 1), t.Color.WithAlpha(t.Hidden ? 0.2f : 0.85f), 0);
            }

            // Voice row
            foreach (ReplayTrack t in clip.Tracks)
            {
                if (t.Hidden || t.VoiceMuted) continue;
                float lastX = -10;
                foreach (AudioBlock ab in t.Audio)
                {
                    if (ab.Time < _viewStart || ab.Time > _viewEnd) continue;
                    float px = L(ab.Time);
                    if (px - lastX < 2f) continue;
                    lastX = px;
                    Theme.Fill(new Rect(px, RowY(5) - oy + 5, 2, rowH - 10), t.Color.WithAlpha(0.8f), 0);
                }
            }

            // In / out markers
            Theme.Fill(new Rect(L(clip.In) - 1, 0, 2, track.height), Theme.Text.WithAlpha(0.6f), 0);
            Theme.Fill(new Rect(L(clip.Out) - 1, 0, 2, track.height), Theme.Text.WithAlpha(0.6f), 0);
        }

        // Keys row: diamonds
        for (int i = 0; i < keys.Count; i++)
        {
            Vector2 c = new Vector2(L(times[i]), RowY(0) - oy + rowH / 2f);
            bool isSel = i == sel;
            float s = isSel ? 7f : 5.5f;
            if (e.type == EventType.Repaint)
                Diamond(c, s, isSel ? Theme.Accent : Theme.Text, isSel);
        }

        // Playhead
        float phx = L(rm.Time);
        if (e.type == EventType.Repaint)
        {
            Theme.Fill(new Rect(phx - 1, 0, 2, track.height), Theme.Danger, 0);
            Theme.Fill(new Rect(phx - 5, 0, 10, 10), Theme.Danger, 2);
        }

        GUI.EndClip();

        // ---- Mouse ----
        int id = GUIUtility.GetControlID(FocusType.Passive);
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown when e.button == 0 && track.Contains(e.mousePosition):
            {
                GUIUtility.hotControl = id;
                _dragKey = -1;
                _dragMoved = false;
                _scrubbing = false;

                // Diamond hit?
                float keysY = RowY(0) + rowH / 2f;
                if (Mathf.Abs(e.mousePosition.y - keysY) < rowH)
                {
                    float best = 9f;
                    for (int i = 0; i < keys.Count; i++)
                    {
                        float d = Mathf.Abs(ToX(times[i]) - e.mousePosition.x);
                        if (d < best) { best = d; _dragKey = i; }
                    }
                }

                // Motion segment click selects that keyframe
                if (_dragKey < 0 && e.mousePosition.y >= RowY(1) && e.mousePosition.y < RowY(2))
                {
                    double t = ToT(e.mousePosition.x);
                    for (int i = 0; i < keys.Count; i++)
                        if (t >= times[i] && t < times[i] + keys[i].Transition.Duration)
                        {
                            UIManager.Instance.Selection = i;
                            break;
                        }
                }

                if (_dragKey >= 0)
                {
                    UIManager.Instance.Selection = _dragKey;
                    _dragStartX = e.mousePosition.x;
                    _dragTimes = times;
                    rm.Playing = false;
                    rm.Seek(times[_dragKey]);
                    if (e.clickCount == 2)
                    {
                        KM.GoToKeyframe(_dragKey);
                        FollowTimeline = false;
                        _dragKey = -1;
                    }
                }
                else
                {
                    _scrubbing = true;
                    rm.Seek(ToT(e.mousePosition.x));
                }
                e.Use();
                break;
            }
            case EventType.MouseDrag when GUIUtility.hotControl == id:
                if (_dragKey >= 0)
                {
                    if (!_dragMoved && Mathf.Abs(e.mousePosition.x - _dragStartX) > 3f)
                        _dragMoved = true;
                    if (_dragMoved)
                    {
                        float t = (float)ToT(e.mousePosition.x);
                        // Snap to whole replay frames.
                        t = Mathf.Round(t * clip.Rate) / clip.Rate;
                        MoveKey(_dragKey, t, _dragTimes);
                        rm.Seek(KeyTimes()[_dragKey]);
                    }
                }
                else if (_scrubbing)
                {
                    rm.Playing = false;
                    rm.Seek(ToT(e.mousePosition.x));
                }
                e.Use();
                break;
            case EventType.MouseUp when GUIUtility.hotControl == id:
                GUIUtility.hotControl = 0;
                if (_dragKey >= 0 && _dragMoved)
                {
                    KM.RefreshOrbs();
                    UIManager.Instance.Status = $"Keyframe {_dragKey} moved to {ReplayManager.FormatTime(KeyTimes()[_dragKey])}.";
                }
                _dragKey = -1;
                _scrubbing = false;
                e.Use();
                break;
            case EventType.ScrollWheel when track.Contains(e.mousePosition):
            {
                float len = Mathf.Max(1f, clip.Length);
                double pivot = ToT(e.mousePosition.x);
                if (e.shift || Mathf.Abs(e.delta.x) > Mathf.Abs(e.delta.y))
                {
                    double pan = (e.shift ? e.delta.y : e.delta.x) * span * 0.05;
                    _viewStart += pan; _viewEnd += pan;
                }
                else
                {
                    double zoom = e.delta.y > 0 ? 1.15 : 1 / 1.15;
                    double ns = System.Math.Max(0.5, System.Math.Min(len, span * zoom));
                    double frac = (pivot - _viewStart) / span;
                    _viewStart = pivot - frac * ns;
                    _viewEnd = _viewStart + ns;
                }
                if (_viewStart < 0) { _viewEnd -= _viewStart; _viewStart = 0; }
                if (_viewEnd > len) { _viewStart = System.Math.Max(0, _viewStart - (_viewEnd - len)); _viewEnd = len; }
                e.Use();
                break;
            }
        }

        // Keep the playhead in view while playing.
        if (rm.Playing && (rm.Time > _viewEnd || rm.Time < _viewStart))
        {
            double s2 = _viewEnd - _viewStart;
            _viewStart = System.Math.Max(0, rm.Time - s2 * 0.1);
            _viewEnd = _viewStart + s2;
        }

        // ---- Footer ----
        float fy = area.yMax + 6;
        string selText = sel >= 0 && sel < keys.Count ? $"[Keyframe {sel}]  {ReplayManager.FormatTime(times[sel])}" : $"{keys.Count} keyframes";
        GUI.Label(new Rect(b.x, fy, b.width, 20), selText + $"   ·   Frame {Mathf.RoundToInt((float)(rm.Time * clip.Rate))}", Theme.MutedSmall);
        GUI.Label(new Rect(b.x, fy, b.width, 20), "drag keys to retime · double-click = go to · wheel = zoom · , . = step", Theme.MutedRight);
    }

    private static void Export()
    {
        CameraManager cm = CameraManager.Instance;
        if (Keys.Count < 2)
        {
            UIManager.Instance.Status = "Add at least two keyframes before exporting.";
            return;
        }
        ReplayManager rm = RM;
        rm.SyncWithKeyframes = true;
        rm.Playing = false;
        new Menus.ProjectMenu().ExportProject();
    }

    private static Color EffectColor(TransitionEffect e) => e switch
    {
        TransitionEffect.Linear => new Color(0.55f, 0.6f, 0.7f),
        TransitionEffect.Sine => new Color(0.35f, 0.75f, 0.85f),
        TransitionEffect.EaseIn => new Color(0.95f, 0.6f, 0.3f),
        TransitionEffect.EaseOut => new Color(0.95f, 0.8f, 0.3f),
        TransitionEffect.Smooth => new Color(0.55f, 0.85f, 0.45f),
        TransitionEffect.Custom => new Color(0.8f, 0.5f, 0.95f),
        TransitionEffect.Cut => new Color(0.9f, 0.35f, 0.4f),
        _ => Color.gray,
    };

    private static void Diamond(Vector2 c, float r, Color color, bool ring)
    {
        // Built from stacked strips (IMGUI has no rotated rects).
        if (ring)
            DiamondFill(c, r + 2f, Color.white.WithAlpha(0.9f));
        DiamondFill(c, r, color);
    }

    private static void DiamondFill(Vector2 c, float r, Color color)
    {
        for (float dy = -r; dy <= r; dy += 1f)
        {
            float half = r - Mathf.Abs(dy);
            Theme.Fill(new Rect(c.x - half, c.y + dy, half * 2f + 1f, 1f), color, 0);
        }
    }

    private static double NiceStep(double raw)
    {
        double[] steps = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300];
        foreach (double s in steps)
            if (s >= raw) return s;
        return 600;
    }
}
