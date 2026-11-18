using ExitGames.Client.Photon;
using MonkeFrames.Editor.Classes;
using Photon.Pun;
using Photon.Realtime;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace MonkeFrames.Editor.Components;

/// <summary>
/// MonkeFrames cameras you can pick up in VR:
/// - in VR you see your own camera model (the MonkeFrames camera itself never sees it)
/// - grip near a camera (yours, or another MonkeFrames user's if they allow it) to grab it; it moves with your hand
/// - while held it looks at you (a vlog / selfie shot), or points where your hand points (setting)
/// - trigger while holding: turn the camera around
/// - hold it against someone's head for 3 seconds (a bar fills, the controller ticks): it goes onto them
///   (first person on that player); grab it again to take it off
/// Someone else's camera is moved by sending its owner small Photon events (code 164, only to them); the owner's
/// MonkeFrames moves its camera there, and everyone sees it through the normal camera sharing (code 163).
/// The owner decides whether others may grab it (setting), and never while playing keyframes.
/// </summary>
public class VRCamGrab : MonoBehaviour
{
    public static VRCamGrab Instance;
    public VRCamGrab() => Instance = this;

    public const byte EventCode = 164;          // unused by Gorilla Tag (163 is the camera sharing)
    private const float Magic = 8233f;
    private const float GrabReach = 0.3f;       // m from the hand to a camera
    private const float HeadReach = 0.35f;      // m from a head to count as "against it"
    private const float OwnHeadReach = 0.25f;   // your own head: a little closer (you hold it near your face anyway)
    private const float AttachSeconds = 3f;
    private const float SendInterval = 1f / 30f;
    private const float HolderTimeout = 0.6f;   // s without word from someone holding my camera = they let go
    private const int CmdHold = 1, CmdRelease = 2, CmdAttach = 3;

    private static Settings S => Settings.current;

    private sealed class HandState
    {
        public bool Right;
        public bool GripWas, TrigWas;
        public int Held;                 // 0 nothing, -1 my own camera, else the actor whose camera it is
        public Vector3 LocalPos;         // where the camera sits relative to the hand
        public Quaternion LocalRot;
        public bool Flipped;
        public float Attach;             // seconds held against someone's head
        public VRRig AttachRig;
        public float NextTick, NextSend;
        public Vector3 Pos;
        public Quaternion Rot = Quaternion.identity;
        public bool Have;                // Pos / Rot valid (smoothing)
    }
    private readonly HandState _l = new() { Right = false }, _r = new() { Right = true };

    // my own camera model (only while I'm in VR)
    private GameObject _own;
    private Renderer[] _ownRenderers = Array.Empty<Renderer>();
    private int _ownGeneration;
    // someone else holding MY camera
    public int RemoteHolder { get; private set; }
    private float _holderLast;
    private Vector3 _holderPos;
    private Quaternion _holderRot = Quaternion.identity;
    // "putting it on their head" bar
    private GameObject _bar;
    private Transform _barFill;
    private Renderer[] _barRenderers = Array.Empty<Renderer>();

    private readonly List<VRRig> _rigs = new();
    private float _nextRigScan;
    private bool _subscribed;
    private readonly List<Renderer> _hiddenNow = new();

    public static bool InVR
    {
        get { try { return XRSettings.isDeviceActive; } catch { return false; } }
    }

    /// <summary>True while I'm holding my own camera in VR.</summary>
    public bool LocalHolding => _l.Held == -1 || _r.Held == -1;

    // =====================================================================
    //  Life cycle
    // =====================================================================

    private void OnEnable()
    {
        Subscribe();
        Application.onBeforeRender += OnBeforeRender;
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        RenderPipelineManager.endCameraRendering += OnEndCamera;
    }

    private void OnDisable()
    {
        if (_subscribed && PhotonNetwork.NetworkingClient != null)
            PhotonNetwork.NetworkingClient.EventReceived -= OnEvent;
        _subscribed = false;
        Application.onBeforeRender -= OnBeforeRender;
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        RenderPipelineManager.endCameraRendering -= OnEndCamera;
        Drop(_l); Drop(_r);
        EndRemoteHold();
    }

    private void Subscribe()
    {
        if (_subscribed || PhotonNetwork.NetworkingClient == null) return;
        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
        _subscribed = true;
    }

    private void Update()
    {
        try
        {
            Subscribe();
            bool vr = (S?.VRCameraGrab ?? true) && InVR && GorillaTagger.Instance != null && ControllerInputPoller.instance != null;
            if (!vr) { Drop(_l); Drop(_r); }
            UpdateOwnModel(vr);
            if (vr) { UpdateHand(_l); UpdateHand(_r); }
            UpdateRemoteHolder();
            UpdateBar();
        }
        catch (Exception ex) { Console.WriteLine($"[MonkeFrames::VRCamGrab] {ex.Message}"); }
    }

    // =====================================================================
    //  My camera in VR
    // =====================================================================

    private void UpdateOwnModel(bool vr)
    {
        // (in first person on my own head it would fill my view: hidden)
        VRRig fp = SpectatorCams.FirstPersonRig();
        bool show = vr && SpectatorCams.LocalCameraActive && !(fp != null && fp.isOfflineVRRig);
        if (_own != null && _ownGeneration != CamModel.Generation)
        {
            // the camera model was switched in Settings
            Destroy(_own);
            _own = null;
            _ownRenderers = Array.Empty<Renderer>();
        }
        if (show && _own == null)
        {
            VRRig me = CameraModes.LocalRig();
            _own = CamModel.Create("MonkeFrames Camera (yours)", me != null ? me.playerColor : Color.gray, "");
            _ownRenderers = _own.GetComponentsInChildren<Renderer>(true);
            _ownGeneration = CamModel.Generation;
        }
        if (_own != null && _own.activeSelf != show) _own.SetActive(show);
    }

    // =====================================================================
    //  Hands
    // =====================================================================

    private static Transform HandTransform(HandState h) =>
        h.Right ? GorillaTagger.Instance.rightHandTransform : GorillaTagger.Instance.leftHandTransform;

    private static Vector3 LocalHead()
    {
        try
        {
            if (GorillaTagger.Instance != null && GorillaTagger.Instance.headCollider != null)
                return GorillaTagger.Instance.headCollider.transform.position;
        }
        catch { }
        return Camera.main != null ? Camera.main.transform.position : Vector3.zero;
    }

    private void UpdateHand(HandState h)
    {
        Transform hand = HandTransform(h);
        if (hand == null) return;
        ControllerInputPoller ip = ControllerInputPoller.instance;
        bool grip = h.Right ? ip.rightGrab : ip.leftGrab;
        float trig = h.Right ? ip.rightControllerIndexFloat : ip.leftControllerIndexFloat;
        bool trigPressed = false;
        if (!h.TrigWas && trig > 0.7f) { trigPressed = true; h.TrigWas = true; }
        else if (h.TrigWas && trig < 0.3f) h.TrigWas = false;

        if (grip && !h.GripWas) TryGrab(h, hand);
        else if (!grip && h.GripWas) Drop(h);
        h.GripWas = grip;
        if (h.Held == 0) return;

        // the held camera stopped existing (owner left, sharing off, my camera switched off)
        if (h.Held == -1 ? !SpectatorCams.LocalCameraActive : FindRemote(h.Held) == null) { Drop(h); return; }

        if (trigPressed)
        {
            h.Flipped = !h.Flipped;
            Buzz(h, 0.4f, 0.05f);
        }

        // (placed right before the frame is drawn, in OnBeforeRender; here only where that isn't delivered)
        if (!CameraModes.BeforeRenderWorks) HeldPose(h, hand, Time.unscaledDeltaTime);

        // against someone's head for a few seconds: it goes onto them
        if (S?.VRHeadAttach ?? true)
        {
            VRRig near = NearestHead(h.Pos);
            if (near != null)
            {
                if (near != h.AttachRig) { h.AttachRig = near; h.Attach = 0f; h.NextTick = 0f; }
                h.Attach += Time.unscaledDeltaTime;
                if (h.Attach >= h.NextTick)
                {
                    h.NextTick += 0.5f;
                    Buzz(h, 0.15f + 0.25f * (h.Attach / AttachSeconds), 0.03f);
                }
                if (h.Attach >= AttachSeconds) { AttachTo(h, near); return; }
            }
            else
            {
                h.Attach = Mathf.Max(0f, h.Attach - Time.unscaledDeltaTime * 2f);
                if (h.Attach <= 0f) { h.AttachRig = null; h.NextTick = 0f; }
            }
        }

        if (h.Held == -1) ApplyOwn(h);
        else
        {
            SpectatorCams.RemoteCam rc = FindRemote(h.Held);
            rc.HoldPos = h.Pos; rc.HoldRot = h.Rot;
            if (Time.unscaledTime >= h.NextSend)
            {
                h.NextSend = Time.unscaledTime + SendInterval;
                Send(h.Held, CmdHold, h.Pos, h.Rot, 0, false);
            }
        }
    }

    /// <summary>Where the held camera goes: with the hand, looking at the holder (or where the hand points),
    /// turned around if flipped; smoothed a little so hand shake doesn't shake the picture.</summary>
    private void HeldPose(HandState h, Transform hand, float dt)
    {
        Vector3 p = hand.TransformPoint(h.LocalPos);
        Quaternion q;
        if (S?.VRGrabLooksAtHolder ?? true)
        {
            Vector3 f = LocalHead() - p;
            if (f.sqrMagnitude < 1e-4f) f = hand.forward;
            if (h.Flipped) f = -f;
            q = Quaternion.LookRotation(f.normalized, Vector3.up);
        }
        else
        {
            q = hand.rotation * h.LocalRot;
            if (h.Flipped) q *= Quaternion.Euler(0f, 180f, 0f);
        }
        if (!h.Have) { h.Pos = p; h.Rot = q; h.Have = true; return; }
        float k = 1f - Mathf.Exp(-22f * Mathf.Max(dt, 0f));
        h.Pos = Vector3.Lerp(h.Pos, p, k);
        h.Rot = Quaternion.Slerp(h.Rot, q, k);
    }

    private void TryGrab(HandState h, Transform hand)
    {
        Vector3 hp = hand.position;
        float best = GrabReach;
        int pick = 0;
        Vector3 camPos = default;
        Quaternion camRot = Quaternion.identity;

        CameraManager cm = CameraManager.Instance;
        VRRig fpOn = SpectatorCams.FirstPersonRig();
        bool onMyHead = fpOn != null && fpOn.isOfflineVRRig && SpectatorCams.LocalCameraActive;
        if (((_own != null && _own.activeSelf) || onMyHead) && cm != null && cm.Camera != null && !cm.InPlayback)
        {
            float d = Vector3.Distance(hp, cm.Camera.transform.position);
            if (d < best) { best = d; pick = -1; camPos = cm.Camera.transform.position; camRot = cm.Camera.transform.rotation; }
        }
        int me = MyActor();
        if (SpectatorCams.Instance != null)
            foreach (SpectatorCams.RemoteCam rc in SpectatorCams.Instance.Remote)
            {
                if (rc.Model == null || !rc.Model.activeSelf || !rc.Grabbable) continue;
                if (rc.HeldBy != 0 && rc.HeldBy != me && !rc.HeldLocally) continue;      // someone else has it
                float d = Vector3.Distance(hp, rc.Model.transform.position);
                if (d < best) { best = d; pick = rc.Actor; camPos = rc.Model.transform.position; camRot = rc.Model.transform.rotation; }
            }
        if (pick == 0) return;

        // the other hand had it: pass it over
        HandState other = h.Right ? _l : _r;
        if (other.Held == pick) other.Held = 0;

        h.Held = pick;
        Vector3 local = hand.InverseTransformPoint(camPos);
        h.LocalPos = Vector3.ClampMagnitude(local, 0.15f);        // held by the hand, not floating off
        h.LocalRot = Quaternion.Inverse(hand.rotation) * camRot;
        h.Flipped = false;
        h.Attach = 0f; h.AttachRig = null; h.NextTick = 0f;
        h.Have = false;
        h.Pos = camPos; h.Rot = camRot; h.Have = true;
        if (pick == -1)
        {
            CameraModes modes = CameraModes.Instance;
            if (modes != null && modes.Mode != CameraMode.Free) modes.SetMode(CameraMode.Free);
            cm.ExternalControl = true;                 // the free-fly keys don't fight the hand
        }
        else
        {
            SpectatorCams.RemoteCam rc = FindRemote(pick);
            if (rc != null) { rc.HeldLocally = true; rc.HoldPos = camPos; rc.HoldRot = camRot; }
            Send(pick, CmdHold, camPos, camRot, 0, true);
        }
        Buzz(h, 0.5f, 0.08f);
    }

    private void Drop(HandState h)
    {
        if (h.Held == 0) return;
        int held = h.Held;
        h.Held = 0;
        h.Attach = 0f; h.AttachRig = null;
        if (held == -1)
        {
            CameraManager cm = CameraManager.Instance;
            if (cm != null)
            {
                cm.ExternalControl = CameraModes.Instance != null && CameraModes.Instance.Mode != CameraMode.Free;
                if (!cm.ExternalControl) cm.LevelIfNearlyLevel();
            }
        }
        else
        {
            SpectatorCams.RemoteCam rc = FindRemote(held);
            if (rc != null) rc.HeldLocally = false;
            Send(held, CmdRelease, h.Pos, h.Rot, 0, true);
        }
    }

    private void AttachTo(HandState h, VRRig rig)
    {
        int held = h.Held;
        h.Held = 0;
        h.Attach = 0f; h.AttachRig = null;
        string who = IsMe(rig) ? "you (first person)" : CameraModes.PlayerName(rig);
        if (held == -1)
        {
            PutOn(rig);
            if (UIManager.Instance != null) UIManager.Instance.Status = $"Camera put on {who}";
        }
        else
        {
            SpectatorCams.RemoteCam rc = FindRemote(held);
            if (rc != null) rc.HeldLocally = false;
            int actor = ActorOf(rig);
            if (actor != 0) Send(held, CmdAttach, h.Pos, h.Rot, actor, true);
            else Send(held, CmdRelease, h.Pos, h.Rot, 0, true);
        }
        Buzz(h, 0.9f, 0.18f);
    }

    /// <summary>My camera onto a player's head: first person on them.</summary>
    private static void PutOn(VRRig rig)
    {
        CameraModes modes = CameraModes.Instance;
        if (modes == null || rig == null) return;
        modes.Target = LiveSubject.For(rig);
        modes.SetMode(CameraMode.FirstPerson);
        if (CameraManager.Instance != null) CameraManager.Instance.ExternalControl = true;
    }

    private void ApplyOwn(HandState h)
    {
        CameraManager cm = CameraManager.Instance;
        if (cm == null) return;
        cm.Position = h.Pos;
        cm.Rotation = h.Rot;
        if (cm.Camera != null) cm.Camera.transform.SetPositionAndRotation(h.Pos, h.Rot);
    }

    // the latest hand pose, right before the frame is drawn (no lag behind the hand)
    private void OnBeforeRender()
    {
        try
        {
            if (GorillaTagger.Instance == null) return;
            foreach (HandState h in new[] { _l, _r })
            {
                if (h.Held == 0) continue;
                Transform hand = HandTransform(h);
                if (hand == null) continue;
                HeldPose(h, hand, Time.unscaledDeltaTime);
                if (h.Held == -1) ApplyOwn(h);
                else
                {
                    SpectatorCams.RemoteCam rc = FindRemote(h.Held);
                    if (rc != null && rc.Model != null)
                    {
                        rc.HoldPos = h.Pos; rc.HoldRot = h.Rot;
                        rc.Model.transform.SetPositionAndRotation(h.Pos, h.Rot);
                    }
                }
            }
            // my own model sits on my camera wherever it is (held, flying, on someone's head)
            if (_own != null && _own.activeSelf && CameraManager.Instance != null && CameraManager.Instance.Camera != null)
            {
                Transform ct = CameraManager.Instance.Camera.transform;
                _own.transform.SetPositionAndRotation(ct.position, ct.rotation);
            }
        }
        catch { }
    }

    // my camera model and the bar are only for my VR view: hidden from every other camera (the MonkeFrames camera
    // mustn't film its own body, shadow cameras mustn't cast it)
    private void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
    {
        if (cam == null || cam.stereoEnabled || cam == Camera.main) return;   // (the VR headset view shows them)
        foreach (Renderer r in _ownRenderers) if (r != null && !r.forceRenderingOff) { r.forceRenderingOff = true; _hiddenNow.Add(r); }
        foreach (Renderer r in _barRenderers) if (r != null && !r.forceRenderingOff) { r.forceRenderingOff = true; _hiddenNow.Add(r); }
    }

    private void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
    {
        if (_hiddenNow.Count == 0) return;
        foreach (Renderer r in _hiddenNow) if (r != null) r.forceRenderingOff = false;
        _hiddenNow.Clear();
    }

    // =====================================================================
    //  "Putting it on their head" bar
    // =====================================================================

    private void UpdateBar()
    {
        HandState h = _l.Held != 0 && _l.Attach > 0.15f ? _l : _r.Held != 0 && _r.Attach > 0.15f ? _r : null;
        if (h == null) { if (_bar != null && _bar.activeSelf) _bar.SetActive(false); return; }
        if (_bar == null)
        {
            _bar = new GameObject("MonkeFrames Camera Attach Bar");
            DontDestroyOnLoad(_bar);
            GameObject back = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(back.GetComponent<Collider>());
            back.transform.SetParent(_bar.transform, false);
            back.transform.localScale = new Vector3(0.22f, 0.025f, 0.004f);
            back.GetComponent<Renderer>().sharedMaterial = CamModel.MakeMaterial(new Color(0.1f, 0.1f, 0.12f), true);
            GameObject pivot = new GameObject("Fill");
            pivot.transform.SetParent(_bar.transform, false);
            pivot.transform.localPosition = new Vector3(-0.105f, 0f, -0.004f);
            GameObject fill = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(fill.GetComponent<Collider>());
            fill.transform.SetParent(pivot.transform, false);
            fill.transform.localPosition = new Vector3(0.5f, 0f, 0f);
            fill.GetComponent<Renderer>().sharedMaterial = CamModel.MakeMaterial(new Color(1f, 0.25f, 0.25f), true);
            _barFill = pivot.transform;
            _barRenderers = _bar.GetComponentsInChildren<Renderer>(true);
        }
        if (!_bar.activeSelf) _bar.SetActive(true);
        Vector3 head = LocalHead();
        Vector3 at = h.Pos + Vector3.up * 0.2f;
        Vector3 look = at - head;
        _bar.transform.SetPositionAndRotation(at, Quaternion.LookRotation(look.sqrMagnitude > 1e-4f ? look.normalized : Vector3.forward, Vector3.up));
        float k = Mathf.Clamp01(h.Attach / AttachSeconds);
        _barFill.localScale = new Vector3(0.21f * Mathf.Max(k, 0.001f), 0.018f, 0.004f);
    }

    // =====================================================================
    //  Someone else holding MY camera
    // =====================================================================

    private bool AllowOthers
    {
        get
        {
            CameraManager cm = CameraManager.Instance;
            return (S?.LetOthersGrabMyCamera ?? true) && SpectatorCams.LocalCameraActive && cm != null && !cm.InPlayback && !LocalHolding;
        }
    }

    /// <summary>Who holds my camera right now, for the camera sharing packets (0 = nobody; me while I hold it).</summary>
    public int HeldByForSharing => LocalHolding ? MyActor() : RemoteHolder;
    public bool GrabbableForSharing => AllowOthers;

    private void OnEvent(EventData ev)
    {
        if (ev == null || ev.Code != EventCode) return;
        if (ev.CustomData is not float[] d || d.Length < 12 || d[0] != Magic) return;
        try
        {
            int sender = ev.Sender;
            int cmd = (int)d[2];
            if (!AllowOthers) { if (RemoteHolder != 0) EndRemoteHold(); return; }
            bool someoneElse = RemoteHolder != 0 && RemoteHolder != sender && Time.unscaledTime - _holderLast < HolderTimeout;
            if (someoneElse) return;
            Vector3 p = new Vector3(d[3], d[4], d[5]);
            Quaternion q = new Quaternion(d[6], d[7], d[8], d[9]);
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) || p.sqrMagnitude > 1e8f || !(m > 0.001f)) return;
            q = new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
            switch (cmd)
            {
                case CmdHold:
                    if (RemoteHolder != sender)
                    {
                        RemoteHolder = sender;
                        CameraModes modes = CameraModes.Instance;
                        if (modes != null && modes.Mode != CameraMode.Free) modes.SetMode(CameraMode.Free);
                        if (CameraManager.Instance != null) CameraManager.Instance.ExternalControl = true;
                        if (UIManager.Instance != null) UIManager.Instance.Status = $"{NameOf(sender)} picked up your camera";
                    }
                    _holderLast = Time.unscaledTime;
                    _holderPos = p; _holderRot = q;
                    break;
                case CmdRelease:
                    if (RemoteHolder == sender) { _holderPos = p; _holderRot = q; EndRemoteHold(); }
                    break;
                case CmdAttach:
                    VRRig rig = RigOf((int)d[10]);
                    if (RemoteHolder == sender || RemoteHolder == 0)
                    {
                        EndRemoteHold();
                        if (rig != null)
                        {
                            PutOn(rig);
                            if (UIManager.Instance != null) UIManager.Instance.Status = $"{NameOf(sender)} put your camera on {CameraModes.PlayerName(rig)}";
                        }
                    }
                    break;
            }
        }
        catch (Exception ex) { Console.WriteLine($"[MonkeFrames::VRCamGrab] Event: {ex.Message}"); }
    }

    private void UpdateRemoteHolder()
    {
        if (RemoteHolder == 0) return;
        if (!AllowOthers || Time.unscaledTime - _holderLast > HolderTimeout) { EndRemoteHold(); return; }
        CameraManager cm = CameraManager.Instance;
        if (cm == null) return;
        // their hand arrives ~30 times a second: glide between the updates
        float k = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
        cm.Position = Vector3.Lerp(cm.Position, _holderPos, k);
        cm.Rotation = Quaternion.Slerp(cm.Rotation, _holderRot, k);
    }

    private void EndRemoteHold()
    {
        if (RemoteHolder == 0) return;
        RemoteHolder = 0;
        CameraManager cm = CameraManager.Instance;
        if (cm != null) cm.ExternalControl = CameraModes.Instance != null && CameraModes.Instance.Mode != CameraMode.Free;
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static void Send(int actor, int cmd, Vector3 p, Quaternion q, int arg, bool reliable)
    {
        if (!PhotonNetwork.InRoom || actor <= 0) return;
        float[] d = new float[12];
        d[0] = Magic; d[1] = 1f; d[2] = cmd;
        d[3] = p.x; d[4] = p.y; d[5] = p.z;
        d[6] = q.x; d[7] = q.y; d[8] = q.z; d[9] = q.w;
        d[10] = arg;
        d[11] = (float)(Time.realtimeSinceStartupAsDouble % 1000.0);
        try
        {
            PhotonNetwork.RaiseEvent(EventCode, d, new RaiseEventOptions { TargetActors = new[] { actor } },
                reliable ? SendOptions.SendReliable : SendOptions.SendUnreliable);
        }
        catch (Exception ex) { Console.WriteLine($"[MonkeFrames::VRCamGrab] Send failed: {ex.Message}"); }
    }

    private static int MyActor()
    {
        try { return PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : 0; } catch { return 0; }
    }

    private static SpectatorCams.RemoteCam FindRemote(int actor)
    {
        if (SpectatorCams.Instance == null) return null;
        foreach (SpectatorCams.RemoteCam rc in SpectatorCams.Instance.Remote)
            if (rc.Actor == actor) return rc;
        return null;
    }

    private static string NameOf(int actor)
    {
        SpectatorCams.RemoteCam rc = FindRemote(actor);
        if (rc != null) return rc.Name;
        try
        {
            Player p = PhotonNetwork.CurrentRoom?.GetPlayer(actor);
            if (p != null && !string.IsNullOrEmpty(p.NickName)) return p.NickName;
        }
        catch { }
        return "Someone";
    }

    private void ScanRigs()
    {
        if (Time.unscaledTime < _nextRigScan) return;
        _nextRigScan = Time.unscaledTime + 1f;
        _rigs.Clear();
        foreach (VRRig rig in FindObjectsByType<VRRig>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (rig != null) _rigs.Add(rig);
    }

    private static bool IsMe(VRRig rig)
    {
        if (rig.isOfflineVRRig) return true;
        try { return rig.Creator != null && rig.Creator.ActorNumber == MyActor(); } catch { return false; }
    }

    private static int ActorOf(VRRig rig)
    {
        if (rig == null) return 0;
        if (IsMe(rig)) return MyActor();
        try { return rig.Creator != null ? rig.Creator.ActorNumber : 0; } catch { return 0; }
    }

    private VRRig RigOf(int actor)
    {
        if (actor == 0) return null;
        if (actor == MyActor()) return CameraModes.LocalRig();
        _nextRigScan = 0f;
        ScanRigs();
        foreach (VRRig rig in _rigs)
        {
            if (rig == null || IsMe(rig)) continue;
            try { if (rig.Creator != null && rig.Creator.ActorNumber == actor) return rig; } catch { }
        }
        return null;
    }

    /// <summary>The nearest other player's head within reach of a point (never my own head: holding the camera up
    /// to my face for a selfie mustn't put it on me).</summary>
    private VRRig NearestHead(Vector3 p)
    {
        ScanRigs();
        VRRig best = null;
        float bd = HeadReach;
        bool self = S?.VRSelfAttach ?? true;
        foreach (VRRig rig in _rigs)
        {
            if (rig == null || !rig.isActiveAndEnabled) continue;
            bool me = IsMe(rig);
            if (me && (!self || !rig.isOfflineVRRig)) continue;
            Transform head = LiveSubject.For(rig).Head;
            if (head == null) continue;
            float d = Vector3.Distance(p, head.position);
            if (d < (me ? Mathf.Min(bd, OwnHeadReach) : bd)) { bd = d; best = rig; }
        }
        return best;
    }

    private static void Buzz(HandState h, float amplitude, float seconds)
    {
        try { GorillaTagger.Instance.StartVibration(!h.Right, amplitude, seconds); } catch { }
    }

    private void OnDestroy()
    {
        if (_own != null) Destroy(_own);
        if (_bar != null) Destroy(_bar);
    }
}
