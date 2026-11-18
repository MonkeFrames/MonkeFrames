using ExitGames.Client.Photon;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Replays;
using Photon.Pun;
using Photon.Realtime;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace MonkeFrames.Editor.Components;

/// <summary>
/// Shows where MonkeFrames users are filming from: while your MonkeFrames camera is active
/// (free cam or any Other Camera), other players who also have MonkeFrames see a camera model
/// at your camera. You never see your own. Uses a small unreliable Photon event (~12/s),
/// only sent to other players in the room, and only understood by MonkeFrames.
/// </summary>
public class SpectatorCams : MonoBehaviour
{
    public static SpectatorCams Instance;

    public const byte EventCode = 163;          // unused by Gorilla Tag
    private const float Magic = 7719f;
    private const float SendInterval = 1f / 20f;
    private const double Delay = 0.11;           // interpolation delay (a little over two packets)
    public const float ModelScale = 0.7f;

    internal sealed class Snap { public double Time; public Vector3 Pos; public Quaternion Rot; }

    public sealed class RemoteCam
    {
        public int Actor;
        public string Name = "Camera";
        public string Key = "";
        public Color Color = Color.gray;
        public GameObject Model;
        public bool Visible;
        public float LastReceived;
        public float Fov = 70f;
        public float NextIdentify;
        // Sender clock -> our clock (so packet timing jitter doesn't cause stutter)
        internal double ClockOffset = double.NaN;
        internal float LastSenderRaw = -1f;
        internal int SenderWraps;
        internal bool HasSmoothed;
        internal Vector3 SmoothPos;
        internal Quaternion SmoothRot = Quaternion.identity;
        internal readonly List<Snap> Snaps = new();
        // VR grabbing (VRCamGrab): who holds it (0 nobody), whether its owner lets others grab it, and while I hold
        // it in VR, where my hand has it (drawn there without waiting for the network)
        public int HeldBy;
        public bool Grabbable;
        // whose head it's on in first person (0 nobody, -1 unknown: an older MonkeFrames that doesn't say)
        public int OnHead;
        public bool HeldLocally;
        public Vector3 HoldPos;
        public Quaternion HoldRot = Quaternion.identity;
        public Vector3 Position => Model != null ? Model.transform.position : Vector3.zero;
        public Quaternion Rotation => Model != null ? Model.transform.rotation : Quaternion.identity;
    }

    private readonly Dictionary<int, RemoteCam> _remote = new();
    public IEnumerable<RemoteCam> Remote => _remote.Values;

    private float _nextSend;
    private bool _sentHidden = true;
    private bool _subscribed;

    public SpectatorCams() => Instance = this;

    private static Settings S => Settings.current;

    // =====================================================================
    //  Network
    // =====================================================================

    private void OnEnable() => Subscribe();

    private void OnDisable()
    {
        if (_subscribed && PhotonNetwork.NetworkingClient != null)
            PhotonNetwork.NetworkingClient.EventReceived -= OnEvent;
        _subscribed = false;
    }

    private void Subscribe()
    {
        if (_subscribed || PhotonNetwork.NetworkingClient == null) return;
        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
        _subscribed = true;
    }

    /// <summary>My Photon actor number (0 when not in a room).</summary>
    public static int MyActor()
    {
        try { return PhotonNetwork.InRoom && PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : 0; }
        catch { return 0; }
    }

    /// <summary>The live gorilla my camera is in first person on right now (null = none).</summary>
    public static VRRig FirstPersonRig()
    {
        try
        {
            CameraModes m = CameraModes.Instance;
            if (m == null || m.Mode == CameraMode.Free) return null;
            CameraMode shot = m.Mode == CameraMode.Director ? m.DirectorShot : m.Mode;
            if (shot != CameraMode.FirstPerson) return null;
            return m.Target is LiveSubject ls && ls.Available ? ls.Rig : null;
        }
        catch { return null; }
    }

    /// <summary>Actor number of the gorilla my camera is in first person on (0 = none), sent with the camera.</summary>
    public static int FirstPersonActor()
    {
        VRRig rig = FirstPersonRig();
        if (rig == null) return 0;
        if (rig.isOfflineVRRig) return MyActor();
        try { NetPlayer owner = rig.Creator; return owner != null ? owner.ActorNumber : 0; }
        catch { return 0; }
    }

    /// <summary>Someone's camera is in first person on MY head: hidden for me (it would fill my view), everyone
    /// else still sees it. Older MonkeFrames versions don't say whose head it's on: then it's hidden when it sits
    /// right at my head.</summary>
    private static bool OnMyHead(RemoteCam rc)
    {
        int me = MyActor();
        if (rc.OnHead > 0) return me != 0 && rc.OnHead == me;
        if (rc.OnHead == 0 || rc.Snaps.Count == 0) return false;
        try
        {
            Transform head = GorillaTagger.Instance != null && GorillaTagger.Instance.headCollider != null ? GorillaTagger.Instance.headCollider.transform : null;
            return head != null && (rc.Snaps[rc.Snaps.Count - 1].Pos - head.position).sqrMagnitude < 0.3f * 0.3f;
        }
        catch { return false; }
    }

    /// <summary>True while your MonkeFrames camera is the one being used (free cam, Other Cameras, playback).</summary>
    public static bool LocalCameraActive
    {
        get
        {
            CameraManager cm = CameraManager.Instance;
            return cm != null && cm.Camera != null && !cm.CinemachineState;
        }
    }

    private void Send()
    {
        if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom == null || PhotonNetwork.CurrentRoom.PlayerCount < 2)
            return;

        bool share = (S?.ShareMyCamera ?? true) && LocalCameraActive;
        if (!share && _sentHidden)
            return;

        float[] data = new float[15];
        data[0] = Magic;
        data[1] = 4f; // version (2 adds a send timestamp, 3 who holds it in VR + whether others may grab it, 4 whose head it's on)
        data[14] = share ? FirstPersonActor() : 0;
        data[12] = VRCamGrab.Instance != null ? VRCamGrab.Instance.HeldByForSharing : 0;
        data[13] = share && VRCamGrab.Instance != null && VRCamGrab.Instance.GrabbableForSharing ? 1f : 0f;
        data[11] = (float)(Time.realtimeSinceStartupAsDouble % 1000.0);
        if (share)
        {
            Transform t = CameraManager.Instance.Camera.transform;
            Vector3 p = t.position;
            Quaternion q = t.rotation;
            data[2] = p.x; data[3] = p.y; data[4] = p.z;
            data[5] = q.x; data[6] = q.y; data[7] = q.z; data[8] = q.w;
            data[9] = CameraManager.Instance.Camera.fieldOfView;
            data[10] = 1f;
        }

        try
        {
            PhotonNetwork.RaiseEvent(EventCode, data,
                new RaiseEventOptions { Receivers = ReceiverGroup.Others },
                SendOptions.SendUnreliable);
            _sentHidden = !share;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::SpectatorCams] Send failed: {ex.Message}");
        }
    }

    private void OnEvent(EventData ev)
    {
        if (ev == null || ev.Code != EventCode)
            return;
        if (ev.CustomData is not float[] d || d.Length < 11 || d[0] != Magic)
            return;

        int actor = ev.Sender;
        if (!_remote.TryGetValue(actor, out RemoteCam rc))
        {
            rc = new RemoteCam { Actor = actor };
            _remote[actor] = rc;
            Identify(rc);
        }

        rc.LastReceived = Time.unscaledTime;
        rc.Visible = d[10] > 0.5f;
        rc.HeldBy = d.Length >= 14 ? (int)d[12] : 0;
        rc.Grabbable = d.Length >= 14 && d[13] > 0.5f;
        rc.OnHead = d.Length >= 15 ? (int)d[14] : -1;
        if (!rc.Visible)
        {
            rc.Snaps.Clear();
            rc.HasSmoothed = false;
            return;
        }

        Vector3 pos = new Vector3(d[2], d[3], d[4]);
        Quaternion rot = new Quaternion(d[5], d[6], d[7], d[8]);
        if (float.IsNaN(pos.x) || float.IsInfinity(pos.x) || pos.sqrMagnitude > 1e8f)
            return;
        float m = Mathf.Sqrt(rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w);
        rot = m > 0.001f ? new Quaternion(rot.x / m, rot.y / m, rot.z / m, rot.w / m) : Quaternion.identity;

        rc.Fov = Mathf.Clamp(d[9], 5f, 170f);

        double now = Time.realtimeSinceStartupAsDouble;
        double t = now;
        if (d.Length >= 12)
        {
            // Put the packet on a smooth timeline using the sender's own clock, so uneven
            // network delivery doesn't make the camera stutter.
            float raw = d[11];
            if (rc.LastSenderRaw >= 0f && raw < rc.LastSenderRaw - 500f) rc.SenderWraps++;
            rc.LastSenderRaw = raw;
            double senderT = raw + rc.SenderWraps * 1000.0;
            double offset = now - senderT;
            if (double.IsNaN(rc.ClockOffset) || offset < rc.ClockOffset || offset - rc.ClockOffset > 1.0)
                rc.ClockOffset = offset;                  // fastest packet = least delay
            else
                rc.ClockOffset += (offset - rc.ClockOffset) * 0.01; // slow drift correction
            t = senderT + rc.ClockOffset;
        }

        // Keep in time order (unreliable packets can arrive out of order).
        int at = rc.Snaps.Count;
        while (at > 0 && rc.Snaps[at - 1].Time > t) at--;
        if (at > 0 && Math.Abs(rc.Snaps[at - 1].Time - t) < 0.0005) return; // duplicate
        rc.Snaps.Insert(at, new Snap { Time = t, Pos = pos, Rot = rot });
        while (rc.Snaps.Count > 20)
            rc.Snaps.RemoveAt(0);
    }

    private static void Identify(RemoteCam rc)
    {
        try
        {
            Player p = PhotonNetwork.CurrentRoom?.GetPlayer(rc.Actor);
            if (p != null)
            {
                rc.Name = string.IsNullOrEmpty(p.NickName) ? "Camera" : p.NickName;
                rc.Key = !string.IsNullOrEmpty(p.UserId) ? p.UserId : "actor" + rc.Actor;
            }

            foreach (VRRig rig in FindObjectsByType<VRRig>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                NetPlayer owner = null;
                try { owner = rig.Creator; } catch { }
                if (owner != null && owner.ActorNumber == rc.Actor)
                {
                    rc.Color = rig.playerColor;
                    rc.Name = CameraModes.PlayerName(rig);
                    break;
                }
            }
        }
        catch { }
    }

    // =====================================================================
    //  Frame loop
    // =====================================================================

    private bool _lastTintLogo;

    private void Update()
    {
        Subscribe();

        // Switched between the classic and the new camera model: rebuild every camera model.
        if (CamModel.WantNew != CamModel.UsingNew && CamModel.Loaded)
        {
            CamModel.Reset();
            foreach (RemoteCam r in _remote.Values)
                if (r.Model != null) { Destroy(r.Model); r.Model = null; }
            ReplayClip clip = ReplayManager.Instance?.Clip;
            if (clip != null)
                foreach (CamTrack c in clip.Cameras)
                    if (c.Model != null) { Destroy(c.Model); c.Model = null; }
        }

        // Re-colour every camera model when the logo tint setting changes.
        if (CamModel.TintLogo != _lastTintLogo)
        {
            _lastTintLogo = CamModel.TintLogo;
            foreach (RemoteCam r in _remote.Values)
                if (r.Model != null) CamModel.SetTint(r.Model, r.Color);
            ReplayClip clip = ReplayManager.Instance?.Clip;
            if (clip != null)
                foreach (CamTrack c in clip.Cameras)
                    if (c.Model != null) CamModel.SetTint(c.Model, c.Color);
        }

        if (Time.unscaledTime >= _nextSend)
        {
            _nextSend = Time.unscaledTime + SendInterval;
            Send();
        }

        bool show = S?.ShowOtherCameras ?? true;
        ReplayManager rm = ReplayManager.Instance;
        if (rm != null && rm.Viewing && rm.HideLive)
            show = false; // watching a replay with the live game hidden

        var gone = new List<int>();
        foreach (RemoteCam rc in _remote.Values)
        {
            float since = Time.unscaledTime - rc.LastReceived;
            if (since > 10f || !PhotonNetwork.InRoom)
            {
                gone.Add(rc.Actor);
                continue;
            }

            // Pick up name / gorilla colour changes.
            if (Time.unscaledTime >= rc.NextIdentify)
            {
                rc.NextIdentify = Time.unscaledTime + 2f;
                Color before = rc.Color;
                Identify(rc);
                if (rc.Model != null && !((Color32)before).Equals((Color32)rc.Color))
                    CamModel.SetTint(rc.Model, rc.Color);
            }

            bool visible = show && ((rc.Visible && since < 2f && rc.Snaps.Count > 0 && !OnMyHead(rc)) || rc.HeldLocally);
            if (visible && rc.Model == null)
                rc.Model = CamModel.Create("MonkeFrames Camera: " + rc.Name, rc.Color, rc.Name);
            if (rc.Model != null && rc.Model.activeSelf != visible)
                rc.Model.SetActive(visible);
            if (visible && rc.HeldLocally)
            {
                // in my hand (VR): exactly where I hold it; carry on smoothly from there once I let go
                rc.Model.transform.SetPositionAndRotation(rc.HoldPos, rc.HoldRot);
                rc.SmoothPos = rc.HoldPos; rc.SmoothRot = rc.HoldRot; rc.HasSmoothed = true;
                CamModel.FaceLabel(rc.Model);
            }
            else if (visible && rc.Snaps.Count > 0)
                Interpolate(rc);
        }

        foreach (int a in gone)
        {
            if (_remote[a].Model != null) Destroy(_remote[a].Model);
            _remote.Remove(a);
        }
    }

    private static void Interpolate(RemoteCam rc)
    {
        double t = Time.realtimeSinceStartupAsDouble - Delay;
        List<Snap> s = rc.Snaps;
        Vector3 pos; Quaternion rot;
        int last = s.Count - 1;

        if (s.Count == 1 || t <= s[0].Time)
        {
            pos = s[0].Pos; rot = s[0].Rot;
        }
        else if (t >= s[last].Time)
        {
            // Ran out of packets: keep gliding briefly in the direction it was moving.
            Snap a = s[last - 1], b = s[last];
            double span = Math.Max(0.001, b.Time - a.Time);
            float ahead = (float)Math.Min(t - b.Time, 0.25);
            Vector3 vel = (b.Pos - a.Pos) / (float)span;
            pos = b.Pos + vel * ahead * Mathf.Clamp01(1f - ahead / 0.25f);
            rot = b.Rot;
        }
        else
        {
            int i = 0;
            while (i < last - 1 && s[i + 1].Time < t) i++;
            Snap p1 = s[i], p2 = s[i + 1];
            Snap p0 = i > 0 ? s[i - 1] : p1;
            Snap p3 = i + 2 <= last ? s[i + 2] : p2;
            float u = (float)((t - p1.Time) / Math.Max(0.0001, p2.Time - p1.Time));

            // Centripetal-ish Catmull-Rom: smooth curves through the received points.
            pos = CatmullRom(p0.Pos, p1.Pos, p2.Pos, p3.Pos, u);
            rot = Quaternion.Slerp(p1.Rot, p2.Rot, u * u * (3f - 2f * u) * 0.35f + u * 0.65f);
        }

        // Final tiny smoothing pass to hide any remaining jitter.
        float dt = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
        if (!rc.HasSmoothed || (pos - rc.SmoothPos).sqrMagnitude > 25f)
        {
            rc.SmoothPos = pos;
            rc.SmoothRot = rot;
            rc.HasSmoothed = true;
        }
        else
        {
            float k = 1f - Mathf.Exp(-30f * dt);
            rc.SmoothPos = Vector3.Lerp(rc.SmoothPos, pos, k);
            rc.SmoothRot = Quaternion.Slerp(rc.SmoothRot, rot, k);
        }

        rc.Model.transform.SetPositionAndRotation(rc.SmoothPos, rc.SmoothRot);
        CamModel.FaceLabel(rc.Model);
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
    }

    /// <summary>Cameras to record into a replay this frame: yours (while active) and every visible remote one.</summary>
    public void CollectForReplay(List<(string key, string name, bool local, Color color, Vector3 pos, Quaternion rot, float fov)> into)
    {
        into.Clear();

        if (LocalCameraActive)
        {
            Camera c = CameraManager.Instance.Camera;
            VRRig me = CameraModes.LocalRig();
            into.Add(("local", "You", true, me != null ? me.playerColor : Color.gray, c.transform.position, c.transform.rotation, c.fieldOfView));
        }

        foreach (RemoteCam rc in _remote.Values)
            if (rc.Visible && rc.Snaps.Count > 0 && Time.unscaledTime - rc.LastReceived < 2f)
            {
                Snap last = rc.Snaps[rc.Snaps.Count - 1];
                Vector3 p = rc.Model != null && rc.Model.activeSelf ? rc.Model.transform.position : last.Pos;
                Quaternion q = rc.Model != null && rc.Model.activeSelf ? rc.Model.transform.rotation : last.Rot;
                into.Add((string.IsNullOrEmpty(rc.Key) ? "actor" + rc.Actor : rc.Key, rc.Name, false, rc.Color, p, q, rc.Fov));
            }
    }

    private void OnDestroy()
    {
        foreach (RemoteCam rc in _remote.Values)
            if (rc.Model != null) Destroy(rc.Model);
        _remote.Clear();
    }
}

/// <summary>Builds the spectator camera model (from the embedded OBJ) with a name tag and a blinking light.</summary>
public static class CamModel
{
    private static Mesh _mesh;
    private static bool _loadFailed;
    private static TMP_FontAsset _font;

    /// <summary>The new camera model (MonkeFrames 2.1 dev) is wanted instead of the classic one (Settings).</summary>
    public static bool WantNew => Settings.current?.NewCameraModel ?? false;
    /// <summary>Which model the loaded mesh / textures are.</summary>
    public static bool UsingNew { get; private set; }
    /// <summary>The model has been loaded (so switching models has something to rebuild).</summary>
    public static bool Loaded => _mesh != null || _loadFailed || _texturesLoaded;
    /// <summary>Bumped whenever the model is switched, so anything holding a model knows to rebuild it.</summary>
    public static int Generation { get; private set; }

    /// <summary>Forget the loaded model, textures and materials (the next Create loads the chosen model).</summary>
    public static void Reset()
    {
        _mesh = null;
        _loadFailed = false;
        _texturesLoaded = false;
        _albedo = null;
        _rgb = null;
        _materials.Clear();
        _glowBuilt = false;
        _glowTight = _glowWide = _glowTightCol = _glowWideCol = null;
        _glowMeshInner = _glowMeshOuter = null;
        _glowMats.Clear();
        _logoColor = default;
        Generation++;
    }

    /// <summary>World size of the body: the new model's OBJ is twice the size of the classic one.</summary>
    private static float BodyScale => UsingNew ? SpectatorCams.ModelScale * 0.5f : SpectatorCams.ModelScale;

    /// <summary>True when the model has a real UV map, so the textures can be used.</summary>
    public static bool HasUVs;

    /// <summary>
    /// Optional replacement files in BepInEx/plugins/MonkeFrames/SpectatorCamera/
    /// (SpectatorCamera.obj, Albedo.png, RGB.png). Used instead of the built-in ones if present.
    /// </summary>
    private static string CustomFile(string name)
    {
        try
        {
            string path = Path.Combine(Constants.MonkeFramesAssemblyFolder ?? "", "SpectatorCamera", name);
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private static Texture2D _albedo, _rgb;
    private static bool _texturesLoaded;
    private static readonly Dictionary<Color32, Material> _materials = new();

    private static Texture2D LoadTexture(string customName, string resource)
    {
        try
        {
            byte[] bytes;
            string custom = CustomFile(customName);
            if (custom != null)
                bytes = File.ReadAllBytes(custom);
            else
            {
                using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
                if (s == null) return null;
                using MemoryStream ms = new MemoryStream();
                s.CopyTo(ms);
                bytes = ms.ToArray();
            }

            Texture2D t = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!t.LoadImage(bytes, false)) return null;
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Point;
            t.anisoLevel = 1;
            return t;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::CamModel] Couldn't load {customName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Body material for a player colour. The glowing strips (RGB.png mask) take the gorilla's
    /// colour, both baked into the base texture and as emission, so they light up in that colour.
    /// </summary>
    /// <summary>True when the logo/glow should take the player's gorilla colour instead of its own colours.</summary>
    public static bool TintLogo => Settings.current?.TintCameraLogo ?? false;

    public static Material BodyMaterial(Color tint)
    {
        tint.a = 1f;
        bool tintLogo = TintLogo;
        Color32 key = tintLogo ? tint : new Color32(0, 0, 0, 0);
        if (_materials.TryGetValue(key, out Material cached) && cached != null)
            return cached;

        if (!_texturesLoaded)
        {
            _texturesLoaded = true;
            bool useNew = WantNew;
            _albedo = LoadTexture("Albedo.png", useNew ? "spectatorCamTex" : "camAlbedo");
            // the new model has no glow mask (no glowing logo)
            _rgb = useNew && CustomFile("RGB.png") == null ? null : LoadTexture("RGB.png", "camRGB");
            Console.WriteLine($"[MonkeFrames::CamModel] Textures: albedo {(_albedo != null ? _albedo.width + "x" + _albedo.height : "missing")}, glow mask {(_rgb != null ? _rgb.width + "x" + _rgb.height : "missing")}");
        }

        Material m;
        Mesh loaded = Mesh; // makes sure HasUVs is known
        if (loaded == null || !HasUVs || _albedo == null)
        {
            m = LitMaterial(Color.Lerp(new Color(0.32f, 0.33f, 0.36f), tint, 0.12f));
        }
        else
        {
            Texture2D baseMap = _albedo;
            if (_rgb != null && tintLogo)
            {
                // Bake the player colour into the logo.
                Color32[] a = _albedo.GetPixels32();
                int w = _albedo.width, h = _albedo.height;
                bool same = _rgb.width == w && _rgb.height == h;
                Color32[] mask = same ? _rgb.GetPixels32() : null;
                Color32 tc = tint;
                for (int i = 0; i < a.Length; i++)
                {
                    float k = same ? mask[i].r / 255f
                        : _rgb.GetPixelBilinear((i % w + 0.5f) / w, (i / w + 0.5f) / h).r;
                    if (k <= 0.01f) continue;
                    k = Mathf.Clamp01(k * 1.6f);
                    // Keep the logo's shading, swap its colour for the gorilla colour.
                    Color32 src = a[i];
                    float lum = Mathf.Max(src.r, Mathf.Max(src.g, src.b)) / 255f;
                    Color32 shaded = new Color32((byte)(tc.r * lum), (byte)(tc.g * lum), (byte)(tc.b * lum), 255);
                    a[i] = Color32.Lerp(src, shaded, k);
                }
                baseMap = new Texture2D(w, h, TextureFormat.RGBA32, true);
                baseMap.SetPixels32(a);
                baseMap.Apply(true, true);
                baseMap.wrapMode = TextureWrapMode.Clamp;
                baseMap.filterMode = FilterMode.Point;
                baseMap.anisoLevel = 1;
            }

            m = LitMaterial(Color.white);
            m.mainTexture = baseMap;
            m.mainTextureScale = Vector2.one;
            m.mainTextureOffset = Vector2.zero;
            if (m.HasProperty("_BaseMap"))
            {
                m.SetTexture("_BaseMap", baseMap);
                m.SetTextureScale("_BaseMap", Vector2.one);
                m.SetTextureOffset("_BaseMap", Vector2.zero);
            }
            // The glow itself comes from the additive glow shells + light (see AddGlow).
        }

        _materials[key] = m;
        return m;
    }

    /// <summary>Recolour an existing camera model (e.g. the player changed their gorilla colour).</summary>
    public static void SetTint(GameObject model, Color tint)
    {
        Transform body = model != null ? model.transform.Find("Body") : null;
        MeshRenderer mr = body != null ? body.GetComponent<MeshRenderer>() : null;
        if (mr != null)
            mr.sharedMaterial = BodyMaterial(tint);

        Material[] glow = GlowMaterials(tint);
        if (body != null && glow != null)
        {
            Transform inner = body.Find("Glow Inner"), outer = body.Find("Glow Outer");
            if (inner != null) inner.GetComponent<MeshRenderer>().sharedMaterial = glow[0];
            if (outer != null) outer.GetComponent<MeshRenderer>().sharedMaterial = glow[1];
        }
        Light l = body != null ? body.GetComponentInChildren<Light>() : null;
        if (l != null) l.color = LightColor(tint);
    }

    private static Color LightColor(Color tint) => TintLogo || _logoColor.a <= 0f ? tint : _logoColor;

    public static Mesh Mesh
    {
        get
        {
            if (_mesh == null && !_loadFailed)
            {
                UsingNew = WantNew;
                try
                {
                    string custom = CustomFile("SpectatorCamera.obj");
                    if (custom != null)
                    {
                        _mesh = ObjToMesh(File.ReadAllText(custom), out HasUVs);
                        Console.WriteLine($"[MonkeFrames::CamModel] Custom model {custom}: {_mesh.vertexCount} verts, UVs={HasUVs}");
                        if (!HasUVs)
                        {
                            Console.WriteLine("[MonkeFrames::CamModel] Custom model has no UV map, using the built-in one instead.");
                            _mesh = null;
                        }
                    }
                    if (_mesh == null)
                    {
                        string resource = UsingNew ? "spectatorCamObj" : "spectatorCam";
                        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
                        if (s == null) throw new Exception($"embedded model resource '{resource}' is missing");
                        using StreamReader r = new StreamReader(s);
                        string text = r.ReadToEnd();
                        _mesh = ObjToMesh(text, out HasUVs);
                        Console.WriteLine($"[MonkeFrames::CamModel] Built-in model: {_mesh.vertexCount} verts, UVs={HasUVs} ({(text.Length > 0 ? text.Split('\n')[0].Trim() : "empty")})");
                    }
                    _mesh.name = "MonkeFrames Spectator Camera";
                    if (!HasUVs)
                        Console.WriteLine("[MonkeFrames::CamModel] The camera model has no proper UV map, so it's drawn without its textures.");
                }
                catch (Exception ex)
                {
                    _loadFailed = true;
                    Console.WriteLine($"[MonkeFrames::CamModel] Couldn't load the camera model: {ex}");
                }
            }
            return _mesh;
        }
    }

    /// <summary>Create a camera model. It's on the Default layer so VR players and every camera can see it.</summary>
    public static GameObject Create(string name, Color tint, string label)
    {
        GameObject root = new GameObject(name);
        UnityEngine.Object.DontDestroyOnLoad(root);
        root.layer = 0;

        GameObject body = new GameObject("Body");
        body.layer = 0;
        body.transform.SetParent(root.transform, false);
        Mesh mesh = Mesh; // loads the chosen model (sets UsingNew)
        if (UsingNew)
            body.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // the new model faces the other way
        body.transform.localScale = Vector3.one * BodyScale;

        if (mesh != null)
        {
            body.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer mr = body.AddComponent<MeshRenderer>();
            mr.sharedMaterial = BodyMaterial(tint);
            AddGlow(body, tint);
        }
        else
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.Destroy(cube.GetComponent<Collider>());
            cube.transform.SetParent(body.transform, false);
            cube.transform.localScale = new Vector3(0.3f, 0.25f, 0.4f);
        }

        // Blinking "recording" light on top.
        GameObject rec = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        UnityEngine.Object.Destroy(rec.GetComponent<Collider>());
        rec.name = "Rec Light";
        rec.layer = 0;
        rec.transform.SetParent(root.transform, false);
        rec.transform.localPosition = UsingNew
            ? new Vector3(0f, 0.2f * BodyScale, -0.02f)
            : new Vector3(0.11f, 0.245f, 0.12f) * SpectatorCams.ModelScale;
        rec.transform.localScale = Vector3.one * (UsingNew ? 0.035f : 0.03f);
        rec.GetComponent<MeshRenderer>().sharedMaterial = MakeMaterial(new Color(1f, 0.15f, 0.15f), true);
        rec.AddComponent<Blink>();

        // Name tag
        TMP_FontAsset font = Font();
        if (font != null && !string.IsNullOrEmpty(label))
        {
            GameObject tag = new GameObject("Name");
            tag.layer = 0;
            tag.transform.SetParent(root.transform, false);
            tag.transform.localPosition = new Vector3(0f, 0.42f, 0f);
            TextMeshPro tmp = tag.AddComponent<TextMeshPro>();
            tmp.font = font;
            tmp.text = label;
            tmp.fontSize = 1.1f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.rectTransform.sizeDelta = new Vector2(3f, 0.5f);
        }

        return root;
    }

    /// <summary>Turn the name tag towards whoever is looking (your VR head, or your MonkeFrames camera).</summary>
    public static void FaceLabel(GameObject model)
    {
        Transform tag = model.transform.Find("Name");
        if (tag == null) return;

        Transform viewer = null;
        if (SpectatorCams.LocalCameraActive)
            viewer = CameraManager.Instance.Camera.transform;
        else if (GorillaTagger.Instance != null && GorillaTagger.Instance.headCollider != null)
            viewer = GorillaTagger.Instance.headCollider.transform;
        if (viewer == null) return;

        Vector3 d = tag.position - viewer.position;
        if (d.sqrMagnitude > 0.0001f)
            tag.rotation = Quaternion.LookRotation(d, Vector3.up);
    }

    private static TMP_FontAsset Font()
    {
        if (_font != null) return _font;
        try
        {
            VRRig rig = CameraModes.LocalRig();
            if (rig != null && rig.playerText1 != null)
                _font = rig.playerText1.font;
            if (_font == null)
                _font = TMP_Settings.defaultFontAsset;
        }
        catch { }
        return _font;
    }

    /// <summary>
    /// A matte material that's lit like everything else in Gorilla Tag: it copies the shader
    /// the gorillas themselves use (so it picks up the map's baked lighting), falling back to URP Lit.
    /// </summary>
    internal static Material LitMaterial(Color color)
    {
        Material template = UberTemplate();
        Material m;
        if (template != null)
        {
            // Same shader + settings as a normal textured Gorilla Tag object, so it's lit
            // (and matte) exactly like the cosmetics and props around it.
            m = new Material(template) { name = "MonkeFrames Spectator Camera" };
            foreach (string prop in m.GetTexturePropertyNames())
                if (prop != "_BaseMap" && prop != "_MainTex")
                    m.SetTexture(prop, null);
            if (m.HasProperty("_BaseMap_ST")) m.SetVector("_BaseMap_ST", new Vector4(1, 1, 0, 0));
            foreach (string kw in new[] { "_EMISSION", "_SPECULAR_HIGHLIGHT", "_REFLECTIONS", "_REFLECTIONS_MATCAP",
                "_REFLECTIONS_MATCAP_PERSP_AWARE", "_REFLECTIONS_BOX_PROJECT", "_REFLECTIONS_ALBEDO_TINT",
                "_REFLECTIONS_USE_NORMAL_TEX", "_GT_RIM_LIGHT", "_GT_RIM_LIGHT_FLAT", "_INNER_GLOW" })
            {
                if (m.IsKeywordEnabled(kw)) m.DisableKeyword(kw);
                if (m.HasProperty(kw)) m.SetFloat(kw, 0f);
            }
        }
        else
        {
            m = MakeMaterial(color, false);
        }

        m.color = color;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Color")) m.SetColor("_Color", color);

        // Matte
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        if (m.HasProperty("_SpecularHighlights")) m.SetFloat("_SpecularHighlights", 0f);
        if (m.HasProperty("_EnvironmentReflections")) m.SetFloat("_EnvironmentReflections", 0f);
        if (template == null)
        {
            m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            m.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
        }
        if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);   // model is double-sided
        return m;
    }

    private static Material _uberTemplate;
    private static bool _uberSearched;

    // Effects that would look wrong on the camera (or need data our model doesn't have).
    private static readonly string[] BadKeywords =
    [
        "_USE_TEX_ARRAY_ATLAS", "_USE_VERTEX_COLOR", "_ALPHATEST_ON", "_VERTEX_ANIM_FLAP", "_VERTEX_ANIM_WAVE",
        "_VERTEX_ROTATE", "_UV_WAVE_WARP", "_UV_SHIFT", "_MAINTEX_ROTATE", "_STEALTH_EFFECT", "_CRYSTAL_EFFECT",
        "_GRID_EFFECT", "_LIQUID_CONTAINER", "_LIQUID_VOLUME", "_WATER_EFFECT", "_HEIGHT_BASED_WATER_EFFECT",
        "_PARALLAX", "_PARALLAX_PLANAR", "_GRADIENT_MAP_ON", "_MASK_MAP_ON", "_USE_DEFORM_MAP", "_MOUTHCOMP",
        "_EYECOMP", "_TEXEL_SNAP_UVS", "USE_TEXTURE__AS_MASK", "_FX_LAVA_LAMP", "_UV_SOURCE__WORLD_PLANAR_Y",
        "_ALPHA_DETAIL_MAP", "_USE_WEATHER_MAP", "_EMISSION_USE_UV_WAVE_WARP",
    ];

    /// <summary>
    /// Find a plain textured material that uses Gorilla Tag's own shader (ideally a cosmetic on your
    /// gorilla, since those are lit like moving objects). Its shader variant is guaranteed to exist.
    /// </summary>
    private static Material UberTemplate()
    {
        if (_uberSearched) return _uberTemplate;
        _uberSearched = true;

        Shader uber = Shader.Find("GorillaTag/UberShader");
        if (uber == null) return null;

        Material best = null;
        int bestScore = int.MaxValue;

        void Consider(Material m, int bonus)
        {
            if (m == null || m.shader != uber || m.renderQueue > 2450) return;
            if (!m.IsKeywordEnabled("_USE_TEXTURE")) return;
            foreach (string bad in BadKeywords)
                if (m.IsKeywordEnabled(bad)) return;
            int score = m.shaderKeywords.Length * 10 - bonus;
            foreach (string kw in m.shaderKeywords)
                if (kw.StartsWith("_REFLECTIONS") || kw.StartsWith("_SPECULAR") || kw.StartsWith("_GT_RIM"))
                    score += 60;   // shiny: prefer a matte one
            if (score < bestScore) { bestScore = score; best = m; }
        }

        try
        {
            VRRig rig = CameraModes.LocalRig();
            if (rig != null)
                foreach (Renderer r in rig.GetComponentsInChildren<Renderer>(true))
                    foreach (Material m in r.sharedMaterials)
                        Consider(m, 25);   // prefer cosmetics: dynamic objects like us

            if (best == null)
                foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
                    Consider(m, 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::CamModel] Material search failed: {ex.Message}");
        }

        _uberTemplate = best;
        Console.WriteLine(best != null
            ? $"[MonkeFrames::CamModel] Using Gorilla Tag shader settings from \"{best.name}\" ({string.Join(" ", best.shaderKeywords)})"
            : "[MonkeFrames::CamModel] No Gorilla Tag material to copy, using URP Lit.");
        return best;
    }

    // ---------------- Glow ----------------

    private static Texture2D _glowTight, _glowWide, _glowTightCol, _glowWideCol;
    private static Color _logoColor;
    private static Mesh _glowMeshInner, _glowMeshOuter;
    private static bool _glowBuilt;
    private static readonly Dictionary<Color32, Material[]> _glowMats = new();

    /// <summary>Soft blurred copies of the RGB glow mask (white where it glows, alpha = strength).</summary>
    /// <summary>
    /// Soft blurred copy of the glow mask. With an albedo, the colour is the logo's own colour
    /// (spread into the halo); without one it's white so it can be tinted.
    /// </summary>
    private static Texture2D BlurredMask(Texture2D mask, int size, int radius, int passes, Texture2D albedo = null)
    {
        float[] a = Resample(mask, size, c => c.r / 255f);
        Blur(a, size, radius, passes);

        float max = 0.0001f;
        foreach (float v in a) max = Mathf.Max(max, v);

        float[] r = null, g = null, b = null;
        if (albedo != null)
        {
            float[] m = Resample(mask, size, c => c.r / 255f);
            float[] ar = Resample(albedo, size, c => c.r / 255f), ag = Resample(albedo, size, c => c.g / 255f), ab = Resample(albedo, size, c => c.b / 255f);
            r = new float[m.Length]; g = new float[m.Length]; b = new float[m.Length];
            for (int i = 0; i < m.Length; i++) { r[i] = ar[i] * m[i]; g[i] = ag[i] * m[i]; b[i] = ab[i] * m[i]; }
            Blur(r, size, radius, passes); Blur(g, size, radius, passes); Blur(b, size, radius, passes);
        }

        Color32[] px = new Color32[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            byte v = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Pow(a[i] / max, 0.8f) * 255f), 0, 255);
            if (r == null)
                px[i] = new Color32(v, v, v, v);
            else
            {
                // Colour = average logo colour under the halo, boosted to full brightness.
                float cr = r[i], cg = g[i], cb = b[i];
                float cm = Mathf.Max(cr, Mathf.Max(cg, cb));
                if (cm < 1e-5f) { cr = cg = cb = 1f; } else { cr /= cm; cg /= cm; cb /= cm; }
                px[i] = new Color32((byte)(cr * 255f), (byte)(cg * 255f), (byte)(cb * 255f), v);
            }
        }
        Texture2D t = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Trilinear,
            name = "MonkeFrames Camera Glow",
        };
        t.SetPixels32(px);
        t.Apply(true, false);   // stays readable: the glow shells are built by sampling it
        return t;
    }

    private static float[] Resample(Texture2D tex, int size, Func<Color32, float> ch)
    {
        int sw = tex.width, sh = tex.height;
        Color32[] src = tex.GetPixels32();
        float[] a = new float[size * size];
        int bx = Mathf.Max(1, sw / size), by = Mathf.Max(1, sh / size);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float sum = 0f; int n = 0;
            int x0 = x * sw / size, y0 = y * sh / size;
            for (int yy = y0; yy < Mathf.Min(sh, y0 + by); yy += Mathf.Max(1, by / 4))
            for (int xx = x0; xx < Mathf.Min(sw, x0 + bx); xx += Mathf.Max(1, bx / 4))
            {
                sum += ch(src[yy * sw + xx]);
                n++;
            }
            a[y * size + x] = n > 0 ? sum / n : 0f;
        }
        return a;
    }

    private static void Blur(float[] a, int size, int radius, int passes)
    {
        float[] tmp = new float[a.Length];
        for (int p = 0; p < passes; p++)
        {
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float sum = 0f; int n = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int xx = x + k;
                    if (xx < 0 || xx >= size) continue;
                    sum += a[y * size + xx]; n++;
                }
                tmp[y * size + x] = sum / n;
            }
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float sum = 0f; int n = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int yy = y + k;
                    if (yy < 0 || yy >= size) continue;
                    sum += tmp[yy * size + x]; n++;
                }
                a[y * size + x] = sum / n;
            }
        }

    }

    /// <summary>Copy of the faces that have glowing strips, pushed out along their normals.</summary>
    private static Mesh GlowShell(Mesh body, Texture2D mask, float push)
    {
        Vector3[] v = body.vertices;
        Vector3[] n = body.normals;
        Vector2[] uv = body.uv;
        int[] tris = body.triangles;
        if (uv == null || uv.Length != v.Length) return null;

        float Sample(Vector2 u) => mask.GetPixelBilinear(u.x, u.y).a;

        var map = new Dictionary<int, int>();
        var nv = new List<Vector3>();
        var nuv = new List<Vector2>();
        var nt = new List<int>();

        for (int i = 0; i < tris.Length; i += 3)
        {
            Vector2 a = uv[tris[i]], b = uv[tris[i + 1]], c = uv[tris[i + 2]];
            float m = Mathf.Max(Sample(a), Sample(b), Sample(c), Sample((a + b + c) / 3f),
                Sample((a + b) / 2f), Sample((b + c) / 2f), Sample((a + c) / 2f));
            if (m < 0.03f) continue;

            for (int k = 0; k < 3; k++)
            {
                int vi = tris[i + k];
                if (!map.TryGetValue(vi, out int ni))
                {
                    ni = nv.Count;
                    Vector3 nrm = n != null && n.Length == v.Length ? n[vi] : Vector3.zero;
                    nv.Add(v[vi] + nrm * push);
                    nuv.Add(uv[vi]);
                    map[vi] = ni;
                }
                nt.Add(ni);
            }
        }

        if (nt.Count == 0) return null;
        Mesh mesh = new Mesh { name = "MonkeFrames Camera Glow Shell" };
        mesh.SetVertices(nv);
        mesh.SetUVs(0, nuv);
        mesh.SetTriangles(nt, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Material GlowMaterial(Color tint, Texture2D tex, float strength)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit")
            ?? Shader.Find("Universal Render Pipeline/Particles/Unlit")
            ?? Shader.Find("Sprites/Default");
        Material m = new Material(sh) { name = "MonkeFrames Camera Glow" };
        m.mainTexture = tex;
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);

        Color c = tint * strength;
        c.a = 1f;
        m.color = c;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);

        // Additive: adds light on top of whatever is behind, like a bloom halo.
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 2f);
        if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
        if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
        if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
        if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
        m.SetOverrideTag("RenderType", "Transparent");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = 3100;
        return m;
    }

    /// <summary>Adds the glow (two soft shells over the strips + a small coloured light) to a camera body.</summary>
    private static void AddGlow(GameObject body, Color tint)
    {
        if (!_glowBuilt)
        {
            _glowBuilt = true;
            try
            {
                Mesh bodyMesh = Mesh;
                _ = BodyMaterial(tint); // loads the textures
                if (bodyMesh != null && HasUVs && _rgb != null)
                {
                    _glowTight = BlurredMask(_rgb, 256, 2, 2);
                    _glowWide = BlurredMask(_rgb, 64, 3, 3);
                    if (_albedo != null)
                    {
                        _glowTightCol = BlurredMask(_rgb, 256, 2, 2, _albedo);
                        _glowWideCol = BlurredMask(_rgb, 64, 3, 3, _albedo);
                        _logoColor = AverageLogoColor();
                    }
                    _glowMeshInner = GlowShell(bodyMesh, _glowWide, 0.003f);
                    _glowMeshOuter = GlowShell(bodyMesh, _glowWide, 0.014f);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MonkeFrames::CamModel] Glow unavailable: {ex.Message}");
            }
        }

        Material[] mats = GlowMaterials(tint);
        if (_glowMeshInner != null && mats != null)
        {
            Shell("Glow Inner", _glowMeshInner, mats[0]);
            if (_glowMeshOuter != null)
                Shell("Glow Outer", _glowMeshOuter, mats[1]);
        }

        GameObject lightGo = new GameObject("Glow Light") { layer = 0 };
        lightGo.transform.SetParent(body.transform, false);
        Light l = lightGo.AddComponent<Light>();
        l.type = LightType.Point;
        l.range = 0.9f;
        l.intensity = 0.9f;
        l.color = LightColor(tint);
        l.shadows = LightShadows.None;

        void Shell(string name, Mesh mesh, Material mat)
        {
            GameObject g = new GameObject(name) { layer = 0 };
            g.transform.SetParent(body.transform, false);
            g.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer r = g.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }
    }

    private static Material[] GlowMaterials(Color tint)
    {
        if (_glowTight == null || _glowWide == null) return null;
        tint.a = 1f;
        bool own = !TintLogo && _glowTightCol != null;
        Color32 key = own ? new Color32(0, 0, 0, 0) : tint;
        if (_glowMats.TryGetValue(key, out Material[] m) && m[0] != null)
            return m;
        m = own
            ? new[] { GlowMaterial(Color.white, _glowTightCol, 1.1f), GlowMaterial(Color.white, _glowWideCol, 0.55f) }
            : new[] { GlowMaterial(tint, _glowTight, 1.1f), GlowMaterial(tint, _glowWide, 0.55f) };
        _glowMats[key] = m;
        return m;
    }

    private static Color AverageLogoColor()
    {
        Color32[] a = _albedo.GetPixels32();
        Color32[] m = _rgb.width == _albedo.width && _rgb.height == _albedo.height ? _rgb.GetPixels32() : null;
        if (m == null) return new Color(0, 0, 0, 0);
        double r = 0, g = 0, b = 0, n = 0;
        for (int i = 0; i < a.Length; i++)
        {
            float k = m[i].r / 255f;
            if (k < 0.5f) continue;
            r += a[i].r * k; g += a[i].g * k; b += a[i].b * k; n += k;
        }
        if (n < 1) return new Color(0, 0, 0, 0);
        Color c = new Color((float)(r / n / 255), (float)(g / n / 255), (float)(b / n / 255), 1f);
        float mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        return mx > 0.001f ? new Color(c.r / mx, c.g / mx, c.b / mx, 1f) : c;
    }

    internal static Material MakeMaterial(Color color, bool unlit)
    {
        Shader sh = null;
        if (!unlit)
            sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("GorillaTag/UberShader");
        sh ??= Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");

        Material m = new Material(sh);
        m.color = color;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);   // model is double-sided
        return m;
    }

    /// <summary>
    /// Minimal OBJ reader (positions, normals, polygons). Converts Blender/OBJ right-handed
    /// coordinates to Unity's left-handed ones, so the lens faces forward (+Z).
    /// </summary>
    public static Mesh ObjToMesh(string text, out bool hasUVs)
    {
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var tex = new List<Vector2>();
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        var cache = new Dictionary<(int, int, int), int>();
        CultureInfo inv = CultureInfo.InvariantCulture;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length < 2) continue;
            string[] p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            try
            {
            switch (p[0])
            {
                case "v":
                    pos.Add(new Vector3(-float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv)));
                    break;
                case "vt":
                    tex.Add(new Vector2(float.Parse(p[1], inv), p.Length > 2 ? float.Parse(p[2], inv) : 0f));
                    break;
                case "vn":
                    nrm.Add(new Vector3(-float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv)));
                    break;
                case "f":
                {
                    var face = new List<int>();
                    for (int i = 1; i < p.Length; i++)
                    {
                        string[] idx = p[i].Split('/');
                        int vi = int.Parse(idx[0], inv);
                        vi = vi < 0 ? pos.Count + vi : vi - 1;
                        int ti = -1;
                        if (idx.Length > 1 && idx[1].Length > 0)
                        {
                            ti = int.Parse(idx[1], inv);
                            ti = ti < 0 ? tex.Count + ti : ti - 1;
                        }
                        int ni = -1;
                        if (idx.Length > 2 && idx[2].Length > 0)
                        {
                            ni = int.Parse(idx[2], inv);
                            ni = ni < 0 ? nrm.Count + ni : ni - 1;
                        }

                        if (!cache.TryGetValue((vi, ti, ni), out int v))
                        {
                            v = verts.Count;
                            verts.Add(pos[vi]);
                            norms.Add(ni >= 0 && ni < nrm.Count ? nrm[ni] : Vector3.zero);
                            uvs.Add(ti >= 0 && ti < tex.Count ? tex[ti] : Vector2.zero);
                            cache[(vi, ti, ni)] = v;
                        }
                        face.Add(v);
                    }

                    // Fan triangulation, winding reversed for the X flip.
                    for (int i = 1; i + 1 < face.Count; i++)
                    {
                        tris.Add(face[0]);
                        tris.Add(face[i + 1]);
                        tris.Add(face[i]);
                    }
                    break;
                }
            }
            }
            catch (Exception) { }   // a line we can't read (or a face pointing at a missing vertex) is skipped
        }

        Mesh mesh = new Mesh();
        if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);

        // A real UV map spreads vertices over the texture; an exported mesh without one has
        // most UVs piled on the same spot.
        var distinct = new HashSet<Vector2Int>();
        foreach (Vector2 uv in uvs)
            distinct.Add(new Vector2Int(Mathf.RoundToInt(uv.x * 512), Mathf.RoundToInt(uv.y * 512)));
        hasUVs = tex.Count > 0 && distinct.Count >= 16;
        bool haveNormals = nrm.Count > 0;
        if (haveNormals) mesh.SetNormals(norms);
        else mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}

/// <summary>Blinks the recording light.</summary>
public class Blink : MonoBehaviour
{
    private Renderer _r;
    private void Awake() => _r = GetComponent<Renderer>();
    private void Update()
    {
        if (_r != null)
            _r.enabled = Mathf.Repeat(Time.unscaledTime, 1.2f) < 0.8f;
    }
}
