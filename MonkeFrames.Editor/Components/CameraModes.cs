using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Replays;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace MonkeFrames.Editor.Components;

// Values are stable (appended only) so nothing saved elsewhere changes meaning.
public enum CameraMode
{
    Free = 0,
    Orbit,
    FirstPerson,
    Follow,
    Shoulder,
    Tracking,
    Front,
    TopDown,
    SideView,
    HandCam,
    Director,
}

/// <summary>
/// "Other Cameras": live camera modes that follow a gorilla (you or anyone in the lobby).
///
/// No lag: the camera is placed at the very last moment before the frame renders
/// (Application.onBeforeRender), after every gorilla, including remote players, has moved.
/// Cameras are locked rigidly to the player's position; "Smoothing" only eases the camera's
/// angle/offset changes, never its position relative to the player, so it never trails behind.
/// It writes CameraManager's Position / Rotation / FieldOfView so keyframe capture (V), MP4
/// recording and motion blur all see the same pose.
/// </summary>
[DefaultExecutionOrder(-50)]
public class CameraModes : MonoBehaviour
{
    public static CameraModes Instance;

    // ---- Current selection ----
    public CameraMode Mode { get; private set; } = CameraMode.Free;
    /// <summary>Who the camera is filming: a live gorilla or a gorilla in a replay.</summary>
    public CamSubject Target;

    // ---- Common options ----
    public float FieldOfView = 75f;
    public float Smoothing = 0.35f;       // eases angle / offset changes (0 = instant)
    public float PositionLag = 0f;        // optional drag behind the player (0 = locked, no lag)
    public float LookHeight = 0f;         // aim above (+) / below (-) the head, metres
    public float Dutch = 0f;              // camera roll in degrees
    public float Shake = 0f;              // handheld camera shake 0..1
    public bool MotionBlur = false;
    public float MotionBlurStrength = 0.5f;

    // ---- Orbit ----
    public bool OrbitAutoSpin = true;
    public float OrbitSpeed = 20f;
    public float OrbitDistance = 3f;
    public float OrbitHeight = 0.4f;
    public bool OrbitRelative = false;
    public float OrbitAngle { get => _orbitPitch; set => _orbitPitch = Mathf.Clamp(value, -80f, 85f); }

    // ---- First person (saved in the settings file, see FirstPersonSettings) ----
    private readonly FirstPersonSettings _fpFallback = new();
    public FirstPersonSettings Fp => Settings.current?.FirstPerson ?? _fpFallback;
    public bool LevelHorizon { get => Fp.LevelHorizon; set => Fp.LevelHorizon = value; }          // roll lock
    public float ForwardOffset { get => Fp.Forward; set => Fp.Forward = value; }
    public float FpUp { get => Fp.Up; set => Fp.Up = value; }            // metres above (+) / below (-) the eyes
    public float FpSide { get => Fp.Side; set => Fp.Side = value; }      // metres right (+) / left (-)
    public float FpPitch { get => Fp.Pitch; set => Fp.Pitch = value; }  // look up (+) / down (-), degrees
    public float FpYaw { get => Fp.Yaw; set => Fp.Yaw = value; }        // look right (+) / left (-), degrees
    public bool FpFollowPitch { get => Fp.FollowPitch; set => Fp.FollowPitch = value; }   // look up/down with the head
    public float FpNearClip { get => Fp.NearClip; set => Fp.NearClip = value; }           // 0 = game default
    public bool FpOffsetFollowsHead { get => Fp.OffsetFollowsHead; set => Fp.OffsetFollowsHead = value; }
    public bool FpAngleClamp { get => Fp.AngleClamp; set => Fp.AngleClamp = value; }
    public float FpMaxAngle { get => Fp.MaxAngle; set => Fp.MaxAngle = value; }
    public bool FpShowClampGuide { get => Fp.ShowClampGuide; set => Fp.ShowClampGuide = value; }
    public bool FpHideHeadCosmetics { get => Fp.HideHeadCosmetics; set => Fp.HideHeadCosmetics = value; }
    public bool FpCastingStyle { get => Fp.CastingStyle; set => Fp.CastingStyle = value; }
    public float FpFieldOfView { get => Fp.FieldOfView; set => Fp.FieldOfView = value; }
    public float FpTurnSmooth { get => Fp.TurnSmooth; set => Fp.TurnSmooth = value; }
    public float FpBobSmooth { get => Fp.BobSmooth; set => Fp.BobSmooth = value; }
    private float _savedNearClip = -1f;

    // ---- Follow ----
    public float FollowDistance = 2.5f;
    public float FollowHeight = 0.8f;
    public float FollowSide = 0f;
    public float FollowTurnLag = 0.5f;

    // ---- Shoulder ----
    public float ShoulderSide = 0.45f;
    public float ShoulderBack = 1.1f;
    public float ShoulderHeight = 0.22f;

    // ---- Tracking ----
    public bool TrackingAutoZoom = false;
    public float TrackingFrame = 1.2f;

    // ---- Front (selfie) ----
    public float FrontDistance = 1.6f;
    public float FrontHeight = 0.1f;

    // ---- Top down ----
    public float TopHeight = 6f;
    public bool TopRotate = true;

    // ---- Side view ----
    public float SideAngle = 90f;
    public float SideDistance = 4f;
    public float SideHeight = 0.3f;

    // ---- Hand cam ----
    public bool HandRight = true;
    public bool HandLookAtFace = true;

    // ---- Director ----
    public float DirectorInterval = 6f;
    public bool DirectorBlend = true;
    public CameraMode DirectorShot => _directorShot;

    // ---- Internal state ----
    private float _orbitYaw, _orbitPitch = 12f;
    private Vector3 _offset;              // smoothed camera offset from the pivot
    private Quaternion _smoothRot = Quaternion.identity;
    private float _smoothFov = 70f;
    private Vector3 _lagPivot;
    private float _facingYaw;             // smoothed direction the gorilla faces
    private Vector3 _trackPos;
    private bool _dragging;
    private bool _active;
    private bool _snap;

    private CameraMode _directorShot = CameraMode.Orbit;
    private float _directorNext;
    private static readonly CameraMode[] DirectorShots =
        [CameraMode.Orbit, CameraMode.Follow, CameraMode.Front, CameraMode.Shoulder, CameraMode.TopDown, CameraMode.SideView];

    private Vector3 _freePos;
    private Quaternion _freeRot;
    private float _freeFov;

    private readonly List<CamSubject> _players = new();
    private readonly List<CamSubject> _allSubjects = new();
    private float _nextScan;

    private int _placedFrame = -1;
    private static int _beforeRenderFrame = -10;

    public CameraModes()
    {
        Instance = this;
    }

    private void Start()
    {
        // Fallback placement in case onBeforeRender isn't delivered on this setup.
        if (GetComponent<CameraModesLate>() == null)
            gameObject.AddComponent<CameraModesLate>();
    }

    private void OnEnable() => Application.onBeforeRender += OnBeforeRender;
    private void OnDisable() => Application.onBeforeRender -= OnBeforeRender;

    private void OnBeforeRender()
    {
        _beforeRenderFrame = Time.frameCount;
        PlaceCamera();
    }

    /// <summary>True when onBeforeRender is working, so late-update fallbacks can stand down.</summary>
    internal static bool BeforeRenderWorks => Time.frameCount - _beforeRenderFrame <= 2;

    // ---------------- Public API ----------------

    public static string ModeName(CameraMode m) => m switch
    {
        CameraMode.Free => "Free",
        CameraMode.Orbit => "Orbit",
        CameraMode.FirstPerson => "First Person",
        CameraMode.Follow => "Follow",
        CameraMode.Shoulder => "Shoulder",
        CameraMode.Tracking => "Tracking",
        CameraMode.Front => "Front",
        CameraMode.TopDown => "Top Down",
        CameraMode.SideView => "Side View",
        CameraMode.HandCam => "Hand Cam",
        CameraMode.Director => "Director",
        _ => m.ToString(),
    };

    public static string Describe(CameraMode m) => m switch
    {
        CameraMode.Free => "Fly freely and build keyframes (the normal MonkeFrames camera).",
        CameraMode.Orbit => "Circles the gorilla and keeps them centred. Left-drag or A/D + W/S to move around, Q/E for height, scroll to zoom.",
        CameraMode.FirstPerson => "Locked inside the gorilla's head. Only turning is smoothed.",
        CameraMode.Follow => "Chases behind the gorilla like a drone, turning as they turn. Scroll to change distance.",
        CameraMode.Shoulder => "Over-the-shoulder view that looks where the gorilla looks. Scroll to change distance.",
        CameraMode.Tracking => "Stays where the camera is now and keeps the gorilla in frame, like a camera operator.",
        CameraMode.Front => "Selfie view: in front of the gorilla looking back at their face. Scroll to change distance.",
        CameraMode.TopDown => "Bird's-eye view straight down on the gorilla. Scroll to change height.",
        CameraMode.SideView => "Side-on view from a fixed direction, like a 2D platformer. Scroll to change distance.",
        CameraMode.HandCam => "A camera strapped to the gorilla's hand, like a GoPro or selfie stick.",
        CameraMode.Director => "Automatic shots: cuts or glides between Orbit, Follow, Front, Shoulder, Top Down and Side View.",
        _ => "",
    };

    /// <summary>Everyone you can film: replay gorillas first (while a replay is showing), then the live lobby.</summary>
    public IReadOnlyList<CamSubject> Players
    {
        get
        {
            _allSubjects.Clear();
            ReplayManager rm = ReplayManager.Instance;
            if (rm != null && rm.Viewing)
                foreach (ReplayPuppet p in rm.Puppets())
                    _allSubjects.Add(p);
            _allSubjects.AddRange(_players);
            return _allSubjects;
        }
    }

    /// <summary>A sensible default target: the first replay gorilla on screen while watching a replay, otherwise you.</summary>
    public CamSubject DefaultTarget()
    {
        ReplayManager rm = ReplayManager.Instance;
        if (rm != null && rm.Viewing)
            foreach (ReplayPuppet p in rm.Puppets())
                if (p.Available)
                    return p;
        return LiveSubject.For(LocalRig());
    }

    public void SetMode(CameraMode mode)
    {
        if (mode == Mode)
            return;

        CameraManager cam = CameraManager.Instance;
        ApplyNearClip(cam != null ? cam.Camera : null, -1f);

        if (Mode == CameraMode.Free)
        {
            _freePos = cam.Position;
            _freeRot = cam.Rotation;
            _freeFov = cam.FieldOfView;
        }

        if (Target == null || !Target.Available)
            Target = DefaultTarget();

        // Blend from wherever the camera is right now.
        _smoothRot = cam.Rotation;
        _smoothFov = cam.FieldOfView;
        _trackPos = cam.Position;
        _snap = false;

        if (Target != null)
        {
            Vector3 head = HeadPosition(Target);
            _lagPivot = head;
            _offset = cam.Position - head;
            _facingYaw = YawOf(HeadForward(Target));

            Vector3 flat = Vector3.ProjectOnPlane(cam.Position - head, Vector3.up);
            _orbitYaw = flat.sqrMagnitude > 0.01f ? Quaternion.LookRotation(-flat).eulerAngles.y : 0f;
            if (OrbitRelative)
                _orbitYaw -= _facingYaw;
        }

        if (mode == CameraMode.Director)
        {
            _directorShot = CameraMode.Orbit;
            _directorNext = Time.unscaledTime + DirectorInterval;
        }

        Mode = mode;
        cam.ExternalControl = mode != CameraMode.Free;

        if (mode == CameraMode.Free)
        {
            cam.Position = _freePos;
            cam.Rotation = _freeRot;
            cam.FieldOfView = _freeFov;
            cam.Blur.Set(false, 0f);
            cam.CancelLookSmoothing();
            Cursor.lockState = CursorLockMode.None;
        }

        UIManager.Instance.Status = mode == CameraMode.Free
            ? "Back to the free camera."
            : $"{ModeName(mode)} camera on {(Target != null ? Target.DisplayName : "nobody")}.";
    }

    public void SetTarget(CamSubject rig, bool quiet = false)
    {
        if (rig == null) return;

        // Keep the camera's current offset so switching players glides instead of jumping.
        CameraManager cam = CameraManager.Instance;
        Vector3 head = HeadPosition(rig);
        _offset = cam.Position - head;
        _lagPivot = head;
        _facingYaw = YawOf(HeadForward(rig));

        Target = rig;
        if (!quiet)
            UIManager.Instance.Status = $"Now watching {rig.DisplayName}{(rig.IsReplay ? " (replay)" : "")}.";
    }

    public void SwapShoulder() => ShoulderSide = -ShoulderSide;

    public void ResetSettings()
    {
        FieldOfView = 75f; Smoothing = 0.35f; PositionLag = 0f; LookHeight = 0f; Dutch = 0f; Shake = 0f;
        MotionBlur = false; MotionBlurStrength = 0.5f;
        OrbitAutoSpin = true; OrbitSpeed = 20f; OrbitDistance = 3f; OrbitHeight = 0.4f; OrbitRelative = false; _orbitPitch = 12f;
        if (Mode == CameraMode.FirstPerson)
        {
            var d = new FirstPersonSettings();
            if (Settings.current != null) Settings.current.FirstPerson = d;
            else { LevelHorizon = d.LevelHorizon; FpNearClip = d.NearClip; }
        }
        FollowDistance = 2.5f; FollowHeight = 0.8f; FollowSide = 0f; FollowTurnLag = 0.5f;
        ShoulderSide = 0.45f; ShoulderBack = 1.1f; ShoulderHeight = 0.22f;
        TrackingAutoZoom = false; TrackingFrame = 1.2f;
        FrontDistance = 1.6f; FrontHeight = 0.1f;
        TopHeight = 6f; TopRotate = true;
        SideAngle = 90f; SideDistance = 4f; SideHeight = 0.3f;
        HandRight = true; HandLookAtFace = true;
        DirectorInterval = 6f; DirectorBlend = true;
        UIManager.Instance.Status = "Camera settings reset.";
    }

    /// <summary>Restore the Sources window's last saved mode and camera options.</summary>
    public void LoadSettings(SourcesSettings settings)
    {
        if (settings == null) return;

        FieldOfView = settings.FieldOfView;
        Smoothing = settings.Smoothing;
        PositionLag = settings.PositionLag;
        LookHeight = settings.LookHeight;
        Dutch = settings.Dutch;
        Shake = settings.Shake;
        MotionBlur = settings.MotionBlur;
        MotionBlurStrength = settings.MotionBlurStrength;

        OrbitAutoSpin = settings.OrbitAutoSpin;
        OrbitSpeed = settings.OrbitSpeed;
        OrbitDistance = settings.OrbitDistance;
        OrbitHeight = settings.OrbitHeight;
        OrbitAngle = settings.OrbitAngle;
        OrbitRelative = settings.OrbitRelative;

        FollowDistance = settings.FollowDistance;
        FollowHeight = settings.FollowHeight;
        FollowSide = settings.FollowSide;
        FollowTurnLag = settings.FollowTurnLag;
        ShoulderSide = settings.ShoulderSide;
        ShoulderBack = settings.ShoulderBack;
        ShoulderHeight = settings.ShoulderHeight;
        TrackingAutoZoom = settings.TrackingAutoZoom;
        TrackingFrame = settings.TrackingFrame;
        FrontDistance = settings.FrontDistance;
        FrontHeight = settings.FrontHeight;
        TopHeight = settings.TopHeight;
        TopRotate = settings.TopRotate;
        SideAngle = settings.SideAngle;
        SideDistance = settings.SideDistance;
        SideHeight = settings.SideHeight;
        HandRight = settings.HandRight;
        HandLookAtFace = settings.HandLookAtFace;
        DirectorInterval = settings.DirectorInterval;
        DirectorBlend = settings.DirectorBlend;

        CameraMode savedMode = System.Enum.IsDefined(typeof(CameraMode), settings.Mode)
            ? (CameraMode)settings.Mode
            : CameraMode.Free;
        if (savedMode != Mode)
            SetMode(savedMode);
    }

    /// <summary>Copy the current Sources window options into the user settings before saving.</summary>
    public void SaveSettings(SourcesSettings settings)
    {
        if (settings == null) return;

        settings.Mode = (int)Mode;
        settings.FieldOfView = FieldOfView;
        settings.Smoothing = Smoothing;
        settings.PositionLag = PositionLag;
        settings.LookHeight = LookHeight;
        settings.Dutch = Dutch;
        settings.Shake = Shake;
        settings.MotionBlur = MotionBlur;
        settings.MotionBlurStrength = MotionBlurStrength;

        settings.OrbitAutoSpin = OrbitAutoSpin;
        settings.OrbitSpeed = OrbitSpeed;
        settings.OrbitDistance = OrbitDistance;
        settings.OrbitHeight = OrbitHeight;
        settings.OrbitAngle = OrbitAngle;
        settings.OrbitRelative = OrbitRelative;

        settings.FollowDistance = FollowDistance;
        settings.FollowHeight = FollowHeight;
        settings.FollowSide = FollowSide;
        settings.FollowTurnLag = FollowTurnLag;
        settings.ShoulderSide = ShoulderSide;
        settings.ShoulderBack = ShoulderBack;
        settings.ShoulderHeight = ShoulderHeight;
        settings.TrackingAutoZoom = TrackingAutoZoom;
        settings.TrackingFrame = TrackingFrame;
        settings.FrontDistance = FrontDistance;
        settings.FrontHeight = FrontHeight;
        settings.TopHeight = TopHeight;
        settings.TopRotate = TopRotate;
        settings.SideAngle = SideAngle;
        settings.SideDistance = SideDistance;
        settings.SideHeight = SideHeight;
        settings.HandRight = HandRight;
        settings.HandLookAtFace = HandLookAtFace;
        settings.DirectorInterval = DirectorInterval;
        settings.DirectorBlend = DirectorBlend;
    }

    public static string PlayerName(VRRig rig)
    {
        if (rig == null) return "nobody";
        if (rig.isOfflineVRRig) return "You";

        string name = null;
        try { name = rig.Creator?.SanitizedNickName; } catch { }
        if (string.IsNullOrEmpty(name)) name = rig.playerNameVisible;
        return string.IsNullOrEmpty(name) ? "Gorilla" : name;
    }

    // ---------------- Early: player list + input ----------------

    private void LateUpdate()
    {
        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 1f;
            ScanPlayers();
        }

        SaveFirstPersonIfChanged();
        CameraManager cam = CameraManager.Instance;
        _active = cam != null && Mode != CameraMode.Free && !cam.CinemachineState && !cam.InPlayback;
        if (!_active)
            return;

        if (Target == null || !Target.Available)
        {
            // A replay gorilla that just isn't in this part of the replay: hold the shot until they're back.
            ReplayManager rm = ReplayManager.Instance;
            if (Target is ReplayPuppet && rm != null && rm.Viewing)
            {
                _active = false;
                return;
            }

            Target = DefaultTarget();
            if (Target == null) { _active = false; return; }
            UIManager.Instance.Status = $"That player left, watching {Target.DisplayName} instead.";
        }

        // Make sure the motion blur component exists so it can render after we place the camera.
        _ = cam.Blur;

        HandleInput(Mathf.Max(Time.unscaledDeltaTime, 0.0001f));

        if (Mode == CameraMode.Director && Time.unscaledTime >= _directorNext)
            NextDirectorShot();
    }

    private void NextDirectorShot()
    {
        CameraMode next = _directorShot;
        for (int i = 0; i < 8 && next == _directorShot; i++)
            next = DirectorShots[Random.Range(0, DirectorShots.Length)];

        _directorShot = next;
        _directorNext = Time.unscaledTime + Mathf.Max(1f, DirectorInterval);
        if (next == CameraMode.Orbit)
            _orbitYaw = Random.Range(0f, 360f);
        _snap = !DirectorBlend;
    }

    // ---------------- Place the camera (last moment before render) ----------------

    internal void PlaceCamera()
    {
        // Replay gorillas must be posed for this frame before we aim at them.
        if (ReplayManager.Instance != null)
            ReplayManager.Instance.ApplyPose();

        if (!_active || Target == null || _placedFrame == Time.frameCount)
            return;
        _placedFrame = Time.frameCount;

        CameraManager cam = CameraManager.Instance;
        float dt = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);

        Transform headT = Target.Head;
        Vector3 head = headT.position;
        if (_haveLastHead && (head - _lastHead).sqrMagnitude > 3f * 3f) _snap = true;
        _lastHead = head; _haveLastHead = true;
        Quaternion headRot = headT.rotation;
        Vector3 headFwd = headRot * Vector3.forward;

        CameraMode shotNow = Mode == CameraMode.Director ? _directorShot : Mode;
        bool fp = shotNow == CameraMode.FirstPerson;

        // Smoothing eases angle / offset changes only.
        float rate = Mathf.Lerp(40f, 2.5f, Mathf.Clamp01(fp ? FpTurnSmooth : Smoothing));
        float k = _snap ? 1f : 1f - Mathf.Exp(-rate * dt);

        // Optional positional lag (0 = rigidly locked to the player = no lag).
        float lag = fp ? FpBobSmooth : PositionLag;
        if (lag <= 0.001f || _snap)
            _lagPivot = head;
        else
            _lagPivot = Vector3.Lerp(_lagPivot, head, 1f - Mathf.Exp(-Mathf.Lerp(40f, 2f, lag) * dt));
        Vector3 pivot = _lagPivot;
        Vector3 aim = pivot + Vector3.up * LookHeight;

        // Smoothed facing direction (for follow / front / shoulder / top-down).
        float turnRate = Mathf.Lerp(14f, 1f, Mathf.Clamp01(FollowTurnLag));
        _facingYaw = _snap ? YawOf(headFwd) : Mathf.LerpAngle(_facingYaw, YawOf(headFwd), 1f - Mathf.Exp(-turnRate * dt));
        Quaternion facing = Quaternion.Euler(0f, _facingYaw, 0f);

        CameraMode shot = Mode == CameraMode.Director ? _directorShot : Mode;

        Vector3 pos;
        Quaternion rot;

        switch (shot)
        {
            case CameraMode.FirstPerson:
            {
                // CastingShouldBeFree's first person (GPL-2.0, see THIRD-PARTY-NOTICES): the camera sits inside the
                // gorilla's head mesh, 0.15 m up, and looks exactly where the head looks; only turning is smoothed.
                Transform fpHead = FpCastingStyle ? CastingHead(Target) : null;
                if (fpHead != null)
                {
                    pos = fpHead.TransformPoint(new Vector3(0f, 0.15f, 0f));
                    Quaternion headLook = fpHead.rotation;
                    float ts = Mathf.Clamp01(FpTurnSmooth);
                    _smoothRot = _snap || ts <= 0.001f
                        ? headLook
                        : Quaternion.Slerp(_smoothRot, headLook, 1f - Mathf.Exp(-dt * Mathf.Lerp(40f, 0.8f, Mathf.Pow(ts, 0.6f))));
                    rot = _smoothRot;
                    _offset = pos - pivot;
                    _fpHeadFwd = fpHead.forward;
                    break;
                }

                // Offsets: in head space (they move with every head tilt, like a camera strapped to the head),
                // or in a level frame (looking up / down doesn't swing the camera vertically)
                if (FpOffsetFollowsHead)
                    pos = pivot + headRot * new Vector3(FpSide, FpUp, ForwardOffset);
                else
                {
                    Quaternion frame = LevelHorizon ? Quaternion.Euler(0f, YawOf(headFwd), 0f) : headRot;
                    pos = pivot + headFwd * ForwardOffset + frame * new Vector3(FpSide, FpUp, 0f);
                }

                Vector3 lookDir = FpFollowPitch ? headFwd : Flat(headFwd);
                Quaternion baseRot = LevelHorizon || !FpFollowPitch ? LookAt(Vector3.zero, lookDir) : headRot;
                Quaternion want = baseRot * Quaternion.Euler(-FpPitch, FpYaw, 0f);
                // angle clamping: however fast the head turns, the smoothed view never falls more than MaxAngle behind
                if (FpAngleClamp && !_snap && Quaternion.Angle(_smoothRot, want) > FpMaxAngle)
                    _smoothRot = Quaternion.RotateTowards(want, _smoothRot, Mathf.Max(FpMaxAngle, 0f));
                _smoothRot = _snap ? want : Quaternion.Slerp(_smoothRot, want, k);
                if (LevelHorizon)
                {
                    // roll lock: whatever the smoothing did, the horizon stays level
                    Vector3 f = _smoothRot * Vector3.forward;
                    if (Mathf.Abs(f.y) < 0.999f) _smoothRot = Quaternion.LookRotation(f, Vector3.up);
                }
                rot = _smoothRot;
                _offset = pos - pivot;
                _fpHeadFwd = headFwd;
                break;
            }

            case CameraMode.HandCam:
            {
                Transform hand = Target.Hand(HandRight);
                pos = hand.position + hand.rotation * new Vector3(0f, 0.04f, 0.06f);
                Quaternion want = HandLookAtFace ? LookAt(pos, head) : hand.rotation;
                _smoothRot = Quaternion.Slerp(_smoothRot, want, k);
                rot = _smoothRot;
                _offset = pos - pivot;
                break;
            }

            case CameraMode.Tracking:
            {
                pos = _trackPos;
                _smoothRot = Quaternion.Slerp(_smoothRot, LookAt(pos, aim), k);
                rot = _smoothRot;
                break;
            }

            case CameraMode.Shoulder:
            {
                Vector3 want = facing * new Vector3(ShoulderSide, ShoulderHeight, -ShoulderBack);
                _offset = Vector3.Lerp(_offset, want, k);
                pos = pivot + _offset;
                _smoothRot = Quaternion.Slerp(_smoothRot, LookAt(pos, head + headFwd * 6f + Vector3.up * LookHeight), k);
                rot = _smoothRot;
                break;
            }

            case CameraMode.TopDown:
            {
                Vector3 want = Vector3.up * TopHeight;
                _offset = Vector3.Lerp(_offset, want, k);
                pos = pivot + _offset;
                Vector3 upDir = TopRotate ? facing * Vector3.forward : Vector3.forward;
                _smoothRot = Quaternion.Slerp(_smoothRot, Quaternion.LookRotation(Vector3.down, upDir), k);
                rot = _smoothRot;
                break;
            }

            default:
            {
                // Offset-based modes: camera rides rigidly with the player; only the offset eases.
                Vector3 want = shot switch
                {
                    CameraMode.Orbit => Vector3.up * OrbitHeight
                        + Quaternion.Euler(_orbitPitch, _orbitYaw + (OrbitRelative ? _facingYaw : 0f), 0f) * Vector3.back * OrbitDistance,
                    CameraMode.Follow => facing * new Vector3(FollowSide, 0f, -FollowDistance) + Vector3.up * FollowHeight,
                    CameraMode.Front => facing * new Vector3(0f, FrontHeight, FrontDistance),
                    CameraMode.SideView => Quaternion.Euler(0f, SideAngle, 0f) * new Vector3(0f, SideHeight, -SideDistance),
                    _ => _offset,
                };

                _offset = Vector3.Lerp(_offset, want, k);
                pos = pivot + _offset;

                // Always aim exactly at the gorilla so they stay centred with zero lag.
                Vector3 lookPoint = shot == CameraMode.Follow ? aim + Vector3.up * 0.1f : aim;
                Quaternion look = LookAt(pos, lookPoint);
                _smoothRot = _snap ? look : Quaternion.Slerp(_smoothRot, look, 1f - Mathf.Exp(-60f * dt));
                rot = _smoothRot;
                break;
            }
        }

        // ---- Field of view ----
        float wantFov = shot == CameraMode.FirstPerson ? FpFieldOfView : FieldOfView;
        if (shot == CameraMode.Tracking && TrackingAutoZoom)
        {
            float dist = Mathf.Max(0.1f, Vector3.Distance(pos, head));
            wantFov = Mathf.Clamp(2f * Mathf.Atan(TrackingFrame * 0.5f / dist) * Mathf.Rad2Deg, 5f, 120f);
        }
        _smoothFov = _snap ? wantFov : Mathf.Lerp(_smoothFov, wantFov, k);

        // ---- Dutch angle + handheld shake ----
        Quaternion extra = Quaternion.Euler(0f, 0f, Dutch);
        if (Shake > 0.001f)
        {
            float t = Time.unscaledTime;
            float amp = Shake * 2.2f;
            float sx = (Mathf.PerlinNoise(t * 0.9f, 0.1f) - 0.5f) + 0.35f * (Mathf.PerlinNoise(t * 2.7f, 3.3f) - 0.5f);
            float sy = (Mathf.PerlinNoise(1.7f, t * 0.8f) - 0.5f) + 0.35f * (Mathf.PerlinNoise(5.1f, t * 2.4f) - 0.5f);
            float sz = (Mathf.PerlinNoise(t * 0.6f, 7.7f) - 0.5f);
            extra = extra * Quaternion.Euler(sx * amp * 2f, sy * amp * 2f, sz * amp);
        }
        rot = rot * extra;
        _snap = false;

        // ---- Apply now, and tell CameraManager so everything else sees the same pose ----
        cam.Position = pos;
        cam.Rotation = rot;
        cam.FieldOfView = _smoothFov;
        cam.transform.SetPositionAndRotation(pos, rot);
        if (cam.Camera != null)
        {
            cam.Camera.fieldOfView = _smoothFov;
            ApplyNearClip(cam.Camera, shot == CameraMode.FirstPerson && FpNearClip > 0.0005f ? FpNearClip : -1f);
        }
        if (cam.CameraMarker != null)
            cam.CameraMarker.transform.SetPositionAndRotation(pos, rot);

        // The watched gorilla is kept sharp in the blur while the world streaks past.
        cam.Blur.Set(MotionBlur, MotionBlurStrength, Target != null ? Target.Root : null);
    }

    private Vector3 _lastHead;
    private bool _haveLastHead;

    // ---------------- First person extras ----------------

    /// <summary>The head mesh casting-style first person rides on (live and replay gorillas).</summary>
    private static Transform CastingHead(CamSubject target)
    {
        try
        {
            if (target is ReplayPuppet rp) return rp.HeadMeshNode;
            return target is LiveSubject ls && ls.Rig != null && ls.Rig.headMesh != null ? ls.Rig.headMesh.transform : null;
        }
        catch { return null; }
    }

    /// <summary>A replay gorilla's hat and face cosmetics (copied ones, by their recorded cosmetic ID, and the real
    /// game cosmetics loaded for it), never its body.</summary>
    private static void AddReplayHatAndFace(ReplayPuppet p, List<Renderer> into)
    {
        try
        {
            ReplayTrack t = p.Track;
            var controller = GorillaNetworking.CosmeticsController.instance;
            if (t == null || controller == null || p.BuildCtx == null) return;
            bool IsHeadItem(string id)
            {
                if (string.IsNullOrEmpty(id)) return false;
                var c = controller.GetItemFromDict(id).itemCategory;
                return c == GorillaNetworking.CosmeticsController.CosmeticCategory.Hat || c == GorillaNetworking.CosmeticsController.CosmeticCategory.Face;
            }
            for (int i = 0; i < t.RendererPaths.Length && i < t.RendererCosmeticIds.Length; i++)
                if (IsHeadItem(t.RendererCosmeticIds[i]) && p.BuildCtx.Nodes.TryGetValue(t.RendererPaths[i], out Transform n) && n != null)
                {
                    Renderer r = n.GetComponent<Renderer>();
                    if (r != null && !into.Contains(r)) into.Add(r);
                }
            foreach (GameObject holder in p.AddressableCosmetics)
                if (holder != null && holder.name.StartsWith("Replay Cosmetic ") && IsHeadItem(holder.name.Substring("Replay Cosmetic ".Length)))
                    foreach (Renderer r in holder.GetComponentsInChildren<Renderer>(false))
                        if (!into.Contains(r)) into.Add(r);
            // never the body
            if (!string.IsNullOrEmpty(t.MainSkinPath) && p.BuildCtx.Nodes.TryGetValue(t.MainSkinPath, out Transform skin) && skin != null)
                into.Remove(skin.GetComponent<Renderer>());
        }
        catch { }
    }

    /// <summary>Renderers of the hat and face cosmetics a gorilla wears (CastingShouldBeFree hides exactly these).</summary>
    private static void AddHatAndFaceRenderers(VRRig rig, List<Renderer> into)
    {
        try
        {
            if (rig == null || rig.cosmeticsObjectRegistry == null) return;
            foreach (var item in rig.cosmeticSet.items)
            {
                if (string.IsNullOrEmpty(item.displayName)) continue;
                if (item.itemCategory != GorillaNetworking.CosmeticsController.CosmeticCategory.Face &&
                    item.itemCategory != GorillaNetworking.CosmeticsController.CosmeticCategory.Hat) continue;
                var inst = rig.cosmeticsObjectRegistry.Cosmetic(item.displayName);
                if (inst?.allRenderers == null) continue;
                foreach (Renderer r in inst.allRenderers)
                    if (r != null && !into.Contains(r)) into.Add(r);
            }
            if (rig.headMesh != null)
                foreach (Renderer r in rig.headMesh.GetComponentsInChildren<Renderer>(false))
                    if (r != null && r != rig.mainSkin && !into.Contains(r)) into.Add(r);
        }
        catch { }
    }

    private Vector3 _fpHeadFwd = Vector3.forward;
    private readonly List<Renderer> _headRenderers = new();
    private readonly List<Renderer> _hiddenHead = new();
    private Transform _headRenderersOf;
    private float _nextHeadScan;
    private string _fpSaved;
    private float _nextFpSaveCheck;

    /// <summary>Hats, glasses and face items on the watched gorilla's head are hidden from the MonkeFrames camera
    /// (only while it renders, so VR and everyone else still see them).</summary>
    private void OnBeginCameraFp(ScriptableRenderContext ctx, Camera c)
    {
        CameraManager cm = CameraManager.Instance;
        if (cm == null || c != cm.Camera) return;
        CameraMode shot = Mode == CameraMode.Director ? _directorShot : Mode;
        if (!_active || shot != CameraMode.FirstPerson || !FpHideHeadCosmetics || Target == null) return;
        Transform head = Target.Head;
        if (head == null) return;
        if (head != _headRenderersOf || Time.unscaledTime >= _nextHeadScan)
        {
            _headRenderersOf = head;
            _nextHeadScan = Time.unscaledTime + 2f;
            _headRenderers.Clear();
            // the head bone: hats, glasses, face items and the face all hang under it (the recorded head *target*
            // is a separate tracking object with nothing on it)
            Transform scan = head;
            if (Target is ReplayPuppet rp)
                scan = rp.HeadMeshNode != null && rp.HeadMeshNode.parent != null ? rp.HeadMeshNode.parent : rp.HeadNode != null ? rp.HeadNode : head;
            else if (Target is LiveSubject live && live.Rig != null && live.Rig.headMesh != null && live.Rig.headMesh.transform.parent != null)
                scan = live.Rig.headMesh.transform.parent;
            scan.GetComponentsInChildren(false, _headRenderers);
            if (scan != head)
                foreach (Renderer r in head.GetComponentsInChildren<Renderer>(false))
                    if (!_headRenderers.Contains(r)) _headRenderers.Add(r);
            // hats and face items wherever they hang (your own aren't under the head collider)
            if (Target is LiveSubject ls) AddHatAndFaceRenderers(ls.Rig, _headRenderers);
            else if (Target is ReplayPuppet puppet) AddReplayHatAndFace(puppet, _headRenderers);
        }
        foreach (Renderer r in _headRenderers)
            if (r != null && r.enabled && !r.forceRenderingOff) { r.forceRenderingOff = true; _hiddenHead.Add(r); }
    }

    private void OnEndCameraFp(ScriptableRenderContext ctx, Camera c) => RestoreHead();

    private void RestoreHead()
    {
        if (_hiddenHead.Count == 0) return;
        foreach (Renderer r in _hiddenHead) if (r != null) r.forceRenderingOff = false;
        _hiddenHead.Clear();
    }

    /// <summary>
    /// Put back anything the first person camera hid (hats, face items, the face) before the replay recorder
    /// looks at the gorilla, so a replay recorded in first person still has them.
    /// </summary>
    internal static void RestoreHiddenHead() => Instance?.RestoreHead();

    /// <summary>True for a renderer the first person camera is hiding from itself right now.</summary>
    internal static bool HiddenByFirstPerson(Renderer r) => Instance != null && r != null && Instance._hiddenHead.Contains(r);

    private void Awake()
    {
        // desktop mode (no gravity / anti AFK kick without a headset) lives next to the camera modes
        if (GetComponent<DesktopMode>() == null) gameObject.AddComponent<DesktopMode>();
        RenderPipelineManager.beginCameraRendering += OnBeginCameraFp;
        RenderPipelineManager.endCameraRendering += OnEndCameraFp;
    }

    private void OnDestroy()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraFp;
        RenderPipelineManager.endCameraRendering -= OnEndCameraFp;
    }

    /// <summary>First person settings are saved to the settings file a moment after they change.</summary>
    private void SaveFirstPersonIfChanged()
    {
        if (Settings.current == null || Time.unscaledTime < _nextFpSaveCheck) return;
        _nextFpSaveCheck = Time.unscaledTime + 1.5f;
        string now = Newtonsoft.Json.JsonConvert.SerializeObject(Settings.current.FirstPerson);
        if (_fpSaved == null) { _fpSaved = now; return; }
        if (now == _fpSaved) return;
        _fpSaved = now;
        try { Settings.Save(); } catch { }
    }

    /// <summary>Angle-clamp guide: a circle for how far the view may lag behind the head, a dot where the head points.</summary>
    private static Texture2D _guideRing, _guideDot;
    private void OnGUI()
    {
        if (Event.current.type != EventType.Repaint || !_active || !FpShowClampGuide || !FpAngleClamp) return;
        CameraMode shot = Mode == CameraMode.Director ? _directorShot : Mode;
        if (shot != CameraMode.FirstPerson) return;
        CameraManager cm = CameraManager.Instance;
        Camera c = cm != null ? cm.Camera : null;
        if (c == null) return;
        _guideRing ??= GuideTex(true);
        _guideDot ??= GuideTex(false);
        Rect vp = new Rect(c.rect.x * Screen.width, (1f - c.rect.y - c.rect.height) * Screen.height, c.rect.width * Screen.width, c.rect.height * Screen.height);
        float r = Mathf.Tan(Mathf.Clamp(FpMaxAngle, 1f, 85f) * Mathf.Deg2Rad) / Mathf.Tan(c.fieldOfView * 0.5f * Mathf.Deg2Rad) * vp.height * 0.5f;
        Vector2 center = vp.center;
        GUI.DrawTexture(new Rect(center.x - r, center.y - r, r * 2f, r * 2f), _guideRing);
        Vector3 sp = c.WorldToScreenPoint(c.transform.position + _fpHeadFwd);
        if (sp.z > 0f)
        {
            Vector2 p = new Vector2(sp.x, Screen.height - sp.y);
            GUI.DrawTexture(new Rect(p.x - 6f, p.y - 6f, 12f, 12f), _guideDot);
        }
    }

    private static Texture2D GuideTex(bool ring)
    {
        const int n = 128;
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = ring ? Mathf.Clamp01(1f - Mathf.Abs(d - 0.97f) * 60f) * 0.8f + (d < 0.97f ? 0.08f : 0f)
                           : Mathf.Clamp01((1f - d) * 8f);
            px[y * n + x] = ring ? new Color32(255, 255, 255, (byte)(a * 255)) : new Color32(255, 70, 70, (byte)(a * 255));
        }
        t.SetPixels32(px);
        t.Apply();
        return t;
    }

    /// <summary>Custom near clip for first person (so the gorilla's own head/face doesn't block the view). -1 restores it.</summary>
    public void ApplyNearClip(Camera c, float near)
    {
        if (c == null) return;
        if (near > 0f)
        {
            if (_savedNearClip < 0f) _savedNearClip = c.nearClipPlane;
            c.nearClipPlane = near;
        }
        else if (_savedNearClip > 0f)
        {
            c.nearClipPlane = _savedNearClip;
            _savedNearClip = -1f;
        }
    }

    public void ApplyFirstPersonPreset(string name)
    {
        (ForwardOffset, FpUp, FpSide, FpPitch, FpYaw) = name switch
        {
            "Eyes" => (0.12f, 0f, 0f, 0f, 0f),
            "Forehead" => (0.1f, 0.12f, 0f, -4f, 0f),
            "Chest" => (0.16f, -0.38f, 0f, 4f, 0f),
            "Behind head" => (-0.45f, 0.18f, 0f, -6f, 0f),
            "Side" => (0.05f, 0.05f, 0.3f, 0f, -8f),
            "Head cam" => (0f, 0f, 0f, 0f, 0f),
            _ => (ForwardOffset, FpUp, FpSide, FpPitch, FpYaw),
        };
        if (name == "Head cam")
        {
            // head-mounted camera look (like Pokruk's Camera Mod first person): wide FOV, offset fixed to the head,
            // light smoothing held in by angle clamping, roll locked, cosmetics out of the way
            FpOffsetFollowsHead = true; FpFollowPitch = true; LevelHorizon = true;
            FpFieldOfView = 100f; FpTurnSmooth = 0.3f; FpAngleClamp = true; FpMaxAngle = 30f;
            FpNearClip = 0.01f; FpHideHeadCosmetics = true; FpBobSmooth = 0f;
        }
    }

    private static Quaternion LookAt(Vector3 from, Vector3 to)
    {
        Vector3 d = to - from;
        return d.sqrMagnitude < 0.000001f ? Quaternion.identity : Quaternion.LookRotation(d, Vector3.up);
    }

    private static float YawOf(Vector3 v) => Quaternion.LookRotation(Flat(v)).eulerAngles.y;

    private void HandleInput(float dt)
    {
        if (Mouse.current == null || Keyboard.current == null)
            return;

        bool overUI = UIManager.Instance != null && UIManager.Instance.IsPointerOverUI;
        bool typing = GUIUtility.keyboardControl != 0;
        float scroll = overUI ? 0f : Mouse.current.scroll.ReadValue().y;
        float step = Mathf.Sign(scroll);

        switch (Mode)
        {
            case CameraMode.Orbit:
            {
                if (Mouse.current.leftButton.wasPressedThisFrame && !overUI) _dragging = true;
                if (!Mouse.current.leftButton.isPressed) _dragging = false;

                if (_dragging)
                {
                    Vector2 d = Mouse.current.delta.ReadValue();
                    _orbitYaw += d.x * 0.25f;
                    OrbitAngle = _orbitPitch - d.y * 0.25f;
                }

                if (scroll != 0f)
                    OrbitDistance = Mathf.Clamp(OrbitDistance - step * 0.35f, 0.8f, 20f);

                if (!typing)
                {
                    float speed = Keyboard.current.shiftKey.isPressed ? 3f : 1f;
                    var kb = Keyboard.current;
                    if (kb.aKey.isPressed) _orbitYaw += 70f * speed * dt;
                    if (kb.dKey.isPressed) _orbitYaw -= 70f * speed * dt;
                    if (kb.wKey.isPressed) OrbitAngle = _orbitPitch + 45f * speed * dt;
                    if (kb.sKey.isPressed) OrbitAngle = _orbitPitch - 45f * speed * dt;
                    if (kb.eKey.isPressed) OrbitHeight = Mathf.Clamp(OrbitHeight + 1.2f * speed * dt, -3f, 6f);
                    if (kb.qKey.isPressed) OrbitHeight = Mathf.Clamp(OrbitHeight - 1.2f * speed * dt, -3f, 6f);
                }
                break;
            }

            case CameraMode.Follow:
                _dragging = false;
                if (scroll != 0f) FollowDistance = Mathf.Clamp(FollowDistance - step * 0.3f, 0.5f, 15f);
                break;

            case CameraMode.Shoulder:
                _dragging = false;
                if (scroll != 0f) ShoulderBack = Mathf.Clamp(ShoulderBack - step * 0.15f, 0.3f, 4f);
                break;

            case CameraMode.Front:
                _dragging = false;
                if (scroll != 0f) FrontDistance = Mathf.Clamp(FrontDistance - step * 0.2f, 0.4f, 8f);
                break;

            case CameraMode.TopDown:
                _dragging = false;
                if (scroll != 0f) TopHeight = Mathf.Clamp(TopHeight - step * 0.5f, 1f, 40f);
                break;

            case CameraMode.SideView:
                _dragging = false;
                if (scroll != 0f) SideDistance = Mathf.Clamp(SideDistance - step * 0.35f, 0.8f, 20f);
                break;

            default:
                _dragging = false;
                break;
        }

        Cursor.lockState = _dragging ? CursorLockMode.Locked : CursorLockMode.None;
    }

    // ---------------- Gorilla helpers ----------------

    private void ScanPlayers()
    {
        _players.Clear();

        VRRig local = LocalRig();
        if (local != null)
            _players.Add(LiveSubject.For(local));

        VRRig[] rigs = Object.FindObjectsByType<VRRig>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (VRRig rig in rigs)
        {
            if (rig == null || !rig.isActiveAndEnabled || rig.isOfflineVRRig)
                continue;

            NetPlayer owner = null;
            try { owner = rig.Creator; } catch { }
            if (owner == null || owner.IsLocal)
                continue;

            _players.Add(LiveSubject.For(rig));
        }
    }

    public static VRRig LocalRig()
    {
        try
        {
            if (GorillaTagger.Instance != null && GorillaTagger.Instance.offlineVRRig != null)
                return GorillaTagger.Instance.offlineVRRig;
        }
        catch { }

        foreach (VRRig rig in Object.FindObjectsByType<VRRig>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (rig.isOfflineVRRig)
                return rig;

        return null;
    }

    private static Vector3 HeadPosition(CamSubject t) => t.Head.position;
    private static Vector3 HeadForward(CamSubject t) => t.Head.forward;

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 0.0001f ? Vector3.forward : v.normalized;
    }
}

/// <summary>
/// Fallback: if Application.onBeforeRender isn't being delivered, place the camera late in the
/// frame instead (after the gorillas have moved, before motion blur renders).
/// </summary>
[DefaultExecutionOrder(9000)]
public class CameraModesLate : MonoBehaviour
{
    private void LateUpdate()
    {
        if (!CameraModes.BeforeRenderWorks)
            CameraModes.Instance?.PlaceCamera();
    }
}
