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
    private const float SendInterval = 1f / 12f;
    private const float Delay = 0.12f;           // interpolation delay for smooth motion
    public const float ModelScale = 0.7f;

    internal sealed class Snap { public float Time; public Vector3 Pos; public Quaternion Rot; }

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
        internal readonly List<Snap> Snaps = new();
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

        float[] data = new float[11];
        data[0] = Magic;
        data[1] = 1f; // version
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
        if (!rc.Visible)
        {
            rc.Snaps.Clear();
            return;
        }

        Vector3 pos = new Vector3(d[2], d[3], d[4]);
        Quaternion rot = new Quaternion(d[5], d[6], d[7], d[8]);
        if (float.IsNaN(pos.x) || float.IsInfinity(pos.x) || pos.sqrMagnitude > 1e8f)
            return;
        float m = Mathf.Sqrt(rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w);
        rot = m > 0.001f ? new Quaternion(rot.x / m, rot.y / m, rot.z / m, rot.w / m) : Quaternion.identity;

        rc.Fov = Mathf.Clamp(d[9], 5f, 170f);
        rc.Snaps.Add(new Snap { Time = Time.unscaledTime, Pos = pos, Rot = rot });
        if (rc.Snaps.Count > 12)
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

    private void Update()
    {
        Subscribe();

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

            bool visible = show && rc.Visible && since < 2f && rc.Snaps.Count > 0;
            if (visible && rc.Model == null)
                rc.Model = CamModel.Create("MonkeFrames Camera: " + rc.Name, rc.Color, rc.Name);
            if (rc.Model != null && rc.Model.activeSelf != visible)
                rc.Model.SetActive(visible);
            if (visible)
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
        float t = Time.unscaledTime - Delay;
        List<Snap> s = rc.Snaps;
        Vector3 pos; Quaternion rot;

        if (s.Count == 1 || t <= s[0].Time)
        {
            pos = s[0].Pos; rot = s[0].Rot;
        }
        else if (t >= s[s.Count - 1].Time)
        {
            pos = s[s.Count - 1].Pos; rot = s[s.Count - 1].Rot;
        }
        else
        {
            int i = 0;
            while (i < s.Count - 2 && s[i + 1].Time < t) i++;
            float a = Mathf.InverseLerp(s[i].Time, s[i + 1].Time, t);
            pos = Vector3.Lerp(s[i].Pos, s[i + 1].Pos, a);
            rot = Quaternion.Slerp(s[i].Rot, s[i + 1].Rot, a);
        }

        rc.Model.transform.SetPositionAndRotation(pos, rot);
        CamModel.FaceLabel(rc.Model);
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
            t.filterMode = FilterMode.Trilinear;
            t.anisoLevel = 4;
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
    public static Material BodyMaterial(Color tint)
    {
        tint.a = 1f;
        Color32 key = tint;
        if (_materials.TryGetValue(key, out Material cached) && cached != null)
            return cached;

        if (!_texturesLoaded)
        {
            _texturesLoaded = true;
            _albedo = LoadTexture("Albedo.png", "camAlbedo");
            _rgb = LoadTexture("RGB.png", "camRGB");
        }

        Material m;
        Mesh loaded = Mesh; // makes sure HasUVs is known
        if (loaded == null || !HasUVs || _albedo == null)
        {
            m = MakeMaterial(Color.Lerp(new Color(0.32f, 0.33f, 0.36f), tint, 0.12f), false);
        }
        else
        {
            Texture2D baseMap = _albedo;
            if (_rgb != null)
            {
                // Bake the player colour into the strips.
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
                    a[i] = Color32.Lerp(a[i], tc, k);
                }
                baseMap = new Texture2D(w, h, TextureFormat.RGBA32, true);
                baseMap.SetPixels32(a);
                baseMap.Apply(true, true);
                baseMap.wrapMode = TextureWrapMode.Clamp;
                baseMap.filterMode = FilterMode.Trilinear;
                baseMap.anisoLevel = 4;
            }

            m = MakeMaterial(Color.white, false);
            m.mainTexture = baseMap;
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", baseMap);

            if (_rgb != null && m.HasProperty("_EmissionMap"))
            {
                m.SetTexture("_EmissionMap", _rgb);
                m.SetColor("_EmissionColor", tint * 2.2f);
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
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
    }

    public static Mesh Mesh
    {
        get
        {
            if (_mesh == null && !_loadFailed)
            {
                try
                {
                    string custom = CustomFile("SpectatorCamera.obj");
                    string text;
                    if (custom != null)
                    {
                        text = File.ReadAllText(custom);
                        Console.WriteLine($"[MonkeFrames::CamModel] Using custom model {custom}");
                    }
                    else
                    {
                        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("spectatorCam");
                        using StreamReader r = new StreamReader(s);
                        text = r.ReadToEnd();
                    }
                    _mesh = ObjToMesh(text, out HasUVs);
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
        body.transform.localScale = Vector3.one * SpectatorCams.ModelScale;

        Mesh mesh = Mesh;
        if (mesh != null)
        {
            body.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer mr = body.AddComponent<MeshRenderer>();
            mr.sharedMaterial = BodyMaterial(tint);
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
        rec.transform.localPosition = new Vector3(0f, 0.2f * SpectatorCams.ModelScale, -0.02f);
        rec.transform.localScale = Vector3.one * 0.035f;
        rec.GetComponent<MeshRenderer>().sharedMaterial = MakeMaterial(new Color(1f, 0.15f, 0.15f), true);
        rec.AddComponent<Blink>();

        // Name tag
        TMP_FontAsset font = Font();
        if (font != null && !string.IsNullOrEmpty(label))
        {
            GameObject tag = new GameObject("Name");
            tag.layer = 0;
            tag.transform.SetParent(root.transform, false);
            tag.transform.localPosition = new Vector3(0f, 0.32f, 0f);
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

    private static Material MakeMaterial(Color color, bool unlit)
    {
        Shader sh = null;
        if (!unlit)
            sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("GorillaTag/UberShader");
        sh ??= Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");

        Material m = new Material(sh);
        m.color = color;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.55f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.2f);
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
        hasUVs = tex.Count > 0 && distinct.Count > uvs.Count / 4;
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
