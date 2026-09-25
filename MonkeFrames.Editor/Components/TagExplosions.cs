using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.UI;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonkeFrames.Editor.Components;

/// <summary>
/// Neon explosion whenever someone gets tagged (their gorilla switches to the tagged / infected
/// look). Everything is generated in code: a flash, expanding shockwave rings, glowing sparks that
/// streak out and fall, floating embers, a coloured light and a synthesised "whoomp".
/// Seen in VR and by the MonkeFrames camera. Fully adjustable in Settings > Tag Effects.
/// </summary>
public class TagExplosions : MonoBehaviour
{
    public static TagExplosions Instance;
    public TagExplosions() => Instance = this;

    private static Settings S => Settings.current;

    // ---------------- Tag detection ----------------

    private sealed class Watch { public int Actor = int.MinValue; public int Mat; public float Since; }
    private readonly Dictionary<VRRig, Watch> _watch = new();
    private readonly List<VRRig> _rigs = new();
    private float _nextRigScan;

    private void Update()
    {
        Settings s = S;
        if (s != null && s.TagFX)
            DetectTags(s);
        UpdateBursts();
    }

    private void DetectTags(Settings s)
    {
        float now = Time.unscaledTime;
        if (now >= _nextRigScan)
        {
            _nextRigScan = now + 1f;
            _rigs.Clear();
            _rigs.AddRange(FindObjectsByType<VRRig>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
            var gone = new List<VRRig>();
            foreach (VRRig r in _watch.Keys)
                if (r == null || !_rigs.Contains(r)) gone.Add(r);
            foreach (VRRig r in gone) _watch.Remove(r);
        }

        foreach (VRRig rig in _rigs)
        {
            if (rig == null || !rig.isActiveAndEnabled) continue;

            int actor = -1;
            if (!rig.isOfflineVRRig)
            {
                try { actor = rig.Creator != null ? rig.Creator.ActorNumber : -1; } catch { }
                if (actor < 0) continue;   // not someone's gorilla right now
            }
            int mat = rig.setMatIndex;

            if (!_watch.TryGetValue(rig, out Watch w))
            {
                _watch[rig] = new Watch { Actor = actor, Mat = mat, Since = now };
                continue;
            }
            if (w.Actor != actor)
            {
                // The game reused this gorilla for another player: don't count that as a tag.
                w.Actor = actor; w.Mat = mat; w.Since = now;
                continue;
            }
            if (mat == w.Mat) continue;

            bool tagged = w.Mat == 0 && mat != 0 && now - w.Since > 0.75f;
            w.Mat = mat;
            w.Since = now;
            if (!tagged) continue;

            bool me = rig.isOfflineVRRig;
            if (s.TagFXWho == 1 && !me) continue;
            if (s.TagFXWho == 2 && me) continue;

            Explode(BodyPosition(rig), rig.playerColor);
        }
    }

    private static Vector3 BodyPosition(VRRig rig)
    {
        try
        {
            Transform head = rig.head != null && rig.head.rigTarget != null ? rig.head.rigTarget : null;
            if (head != null) return head.position + Vector3.down * 0.25f * rig.transform.lossyScale.y;
        }
        catch { }
        return rig.transform.position;
    }

    /// <summary>Preview button: explode in front of the MonkeFrames camera (or on your gorilla).</summary>
    public void Preview()
    {
        Vector3 pos;
        CameraManager cm = CameraManager.Instance;
        if (SpectatorCams.LocalCameraActive)
            pos = cm.Camera.transform.position + cm.Camera.transform.forward * 3f;
        else
        {
            VRRig me = CameraModes.LocalRig();
            pos = me != null ? BodyPosition(me) + (me.head?.rigTarget != null ? me.head.rigTarget.forward * 1.5f : Vector3.forward * 1.5f) : Vector3.zero;
        }
        VRRig local = CameraModes.LocalRig();
        Explode(pos, local != null ? local.playerColor : new Color(1f, 0.4f, 0.1f));
    }

    // ---------------- Bursts ----------------

    private struct Spark
    {
        public Vector3 Pos, Vel;
        public float Size, Life, Age, Drag, Grav;
        public Color Color;
        public bool Ember;
    }

    private sealed class Burst
    {
        public GameObject Go;
        public Mesh Mesh;
        public Light Light;
        public Vector3 Center;
        public float Age, Duration, Size, Intensity;
        public Color Primary, Secondary;
        public bool Flash, Rainbow;
        public Spark[] Sparks;
        public Quaternion[] Rings;
        public Color[] RingColors;
        public float Hue;
    }

    private readonly List<Burst> _bursts = new();
    private Material _mat;
    private Texture2D _tex;
    private AudioClip _boom;

    // Scratch buffers for the mesh
    private readonly List<Vector3> _v = new();
    private readonly List<Vector2> _uv = new();
    private readonly List<Color32> _c = new();
    private readonly List<int> _t = new();

    public void Explode(Vector3 pos, Color gorilla)
    {
        Settings s = S;
        if (s == null || !EnsureMaterial()) return;
        if (_bursts.Count >= 12) Kill(_bursts[0]);

        gorilla.a = 1f;
        Color primary = s.TagFXColorMode switch
        {
            0 => Neon(gorilla),
            1 => s.TagFXColor,
            _ => Color.HSVToRGB(Random.value, 1f, 1f),
        };
        Color secondary = s.TagFXColor2;

        Burst b = new Burst
        {
            Center = pos,
            Duration = Mathf.Clamp(s.TagFXDuration, 0.2f, 5f),
            Size = Mathf.Clamp(s.TagFXSize, 0.1f, 5f),
            Intensity = Mathf.Clamp(s.TagFXIntensity, 0f, 5f),
            Primary = primary,
            Secondary = secondary,
            Flash = s.TagFXFlash,
            Rainbow = s.TagFXColorMode == 2,
            Hue = Random.value,
        };

        b.Go = new GameObject("MonkeFrames Tag Explosion") { layer = 0 };
        b.Go.transform.position = pos;
        b.Mesh = new Mesh { name = "MonkeFrames Tag Explosion" };
        b.Mesh.MarkDynamic();
        b.Go.AddComponent<MeshFilter>().sharedMesh = b.Mesh;
        MeshRenderer mr = b.Go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = _mat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;

        // Sparks: fast streaks + slow floating embers
        int count = Mathf.Clamp(s.TagFXSparks, 0, 400);
        float speed = Mathf.Clamp(s.TagFXSparkSpeed, 0f, 4f);
        b.Sparks = new Spark[count];
        for (int i = 0; i < count; i++)
        {
            bool ember = i % 5 == 4;
            Vector3 dir = Random.onUnitSphere;
            dir.y = Mathf.Abs(dir.y) * 0.8f + dir.y * 0.2f + 0.15f;   // a bit more upwards than down
            float v = ember ? Random.Range(0.4f, 1.2f) : Random.Range(2.5f, 7.5f);
            Color c = b.Rainbow ? Color.HSVToRGB(Mathf.Repeat(b.Hue + Random.Range(0f, 0.35f), 1f), 1f, 1f)
                : Random.value < 0.72f ? primary : secondary;
            b.Sparks[i] = new Spark
            {
                Pos = pos + dir * 0.05f * b.Size,
                Vel = dir.normalized * v * speed * b.Size,
                Size = (ember ? Random.Range(0.05f, 0.1f) : Random.Range(0.025f, 0.05f)) * b.Size,
                Life = b.Duration * (ember ? Random.Range(0.9f, 1.3f) : Random.Range(0.45f, 0.95f)),
                Drag = ember ? 1.2f : Random.Range(2f, 3.5f),
                Grav = (ember ? -0.4f : 1f) * Mathf.Clamp(s.TagFXGravity, 0f, 3f) * 6f * b.Size,
                Color = c,
                Ember = ember,
            };
        }

        int rings = Mathf.Clamp(s.TagFXRings, 0, 4);
        b.Rings = new Quaternion[rings];
        b.RingColors = new Color[rings];
        for (int i = 0; i < rings; i++)
        {
            b.Rings[i] = i == 0 ? Quaternion.Euler(90f, 0f, 0f)
                : Quaternion.Euler(Random.Range(-60f, 60f) + 90f * (i % 2), Random.Range(0f, 360f), Random.Range(-40f, 40f));
            b.RingColors[i] = b.Rainbow ? Color.HSVToRGB(Mathf.Repeat(b.Hue + i * 0.33f, 1f), 1f, 1f) : i % 2 == 0 ? primary : secondary;
        }

        if (s.TagFXLight)
        {
            b.Light = b.Go.AddComponent<Light>();
            b.Light.type = LightType.Point;
            b.Light.color = primary;
            b.Light.range = 5f * b.Size;
            b.Light.intensity = 0f;
            b.Light.shadows = LightShadows.None;
        }

        if (s.TagFXSound && s.TagFXVolume > 0.001f)
        {
            try
            {
                AudioSource a = b.Go.AddComponent<AudioSource>();
                a.spatialBlend = 0.7f;
                a.minDistance = 3f;
                a.maxDistance = 60f;
                a.rolloffMode = AudioRolloffMode.Linear;
                a.pitch = Random.Range(0.93f, 1.07f) / Mathf.Lerp(1f, 1.3f, Mathf.InverseLerp(1f, 3f, b.Size));
                a.PlayOneShot(Boom(), Mathf.Clamp01(s.TagFXVolume));
            }
            catch { }
        }

        _bursts.Add(b);
        Build(b);
    }

    /// <summary>Make any colour neon: full saturation and brightness (keeps near-greys white-ish).</summary>
    private static Color Neon(Color c)
    {
        Color.RGBToHSV(c, out float h, out float sat, out float v);
        return Color.HSVToRGB(h, sat < 0.12f ? sat : Mathf.Max(0.75f, sat), 1f);
    }

    private void UpdateBursts()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        for (int i = _bursts.Count - 1; i >= 0; i--)
        {
            Burst b = _bursts[i];
            b.Age += dt;

            float longest = b.Duration * 1.35f;
            if (b.Go == null || b.Age > longest)
            {
                Kill(b);
                continue;
            }

            for (int k = 0; k < b.Sparks.Length; k++)
            {
                ref Spark p = ref b.Sparks[k];
                p.Age += dt;
                if (p.Age > p.Life) continue;
                p.Vel *= Mathf.Exp(-p.Drag * dt);
                p.Vel += Vector3.down * p.Grav * dt;
                if (p.Ember) p.Vel += new Vector3(Mathf.Sin(p.Age * 7f + k) * 0.3f, 0f, Mathf.Cos(p.Age * 5f + k) * 0.3f) * dt;
                p.Pos += p.Vel * dt;
            }

            if (b.Light != null)
            {
                float lt = b.Age / (b.Duration * 0.6f);
                b.Light.intensity = lt >= 1f ? 0f : Mathf.Clamp(S?.TagFXIntensity ?? 1f, 0f, 5f) * 6f * Mathf.Pow(1f - lt, 2f) * Mathf.Clamp01(b.Age * 30f);
            }

            Build(b);
        }
    }

    private void Kill(Burst b)
    {
        if (b.Go != null) Destroy(b.Go);
        if (b.Mesh != null) Destroy(b.Mesh);
        _bursts.Remove(b);
    }

    // ---------------- Mesh ----------------

    private void Build(Burst b)
    {
        _v.Clear(); _uv.Clear(); _c.Clear(); _t.Clear();
        Vector3 o = b.Center;
        float t = b.Age / b.Duration;

        // Flash + core glow (three crossed quads each, so it looks round from any angle)
        if (b.Flash)
        {
            float ft = Mathf.Clamp01(b.Age / (b.Duration * 0.18f));
            float fr = Mathf.Lerp(0.2f, 1.1f, EaseOut(ft)) * b.Size;
            float fa = (1f - ft) * (1f - ft);
            if (fa > 0.001f) Star(Vector3.zero, fr, Color.Lerp(Color.white, b.Primary, 0.25f), fa * 1.4f, false);

            float ct = Mathf.Clamp01(b.Age / (b.Duration * 0.55f));
            float cr = Mathf.Lerp(0.35f, 0.9f, EaseOut(ct)) * b.Size;
            float ca = (1f - ct) * 0.9f;
            if (ca > 0.001f) Star(Vector3.zero, cr, b.Primary, ca, false);
        }

        // Shockwave rings
        for (int i = 0; i < b.Rings.Length; i++)
        {
            float delay = i * 0.06f * b.Duration;
            float rt = Mathf.Clamp01((b.Age - delay) / (b.Duration * 0.7f));
            if (rt <= 0f || rt >= 1f) continue;
            float r = Mathf.Lerp(0.1f, 2.6f + i * 0.5f, EaseOut(rt)) * b.Size;
            float a = Mathf.Pow(1f - rt, 1.6f);
            Color c = b.Rainbow ? Color.HSVToRGB(Mathf.Repeat(b.Hue + i * 0.33f + rt * 0.3f, 1f), 1f, 1f) : b.RingColors[i];
            Quad(Vector3.zero, b.Rings[i] * Vector3.right * r, b.Rings[i] * Vector3.up * r, c, a, true);
        }

        // Sparks: two crossed quads stretched along their direction of travel
        for (int k = 0; k < b.Sparks.Length; k++)
        {
            Spark p = b.Sparks[k];
            if (p.Age > p.Life) continue;
            float lt = p.Age / p.Life;
            float a = p.Ember ? Mathf.Sin(lt * Mathf.PI) * (0.7f + 0.3f * Mathf.Sin(p.Age * 25f + k))
                              : (1f - lt) * (1f - lt * 0.5f);
            Vector3 d = p.Vel;
            float sp = d.magnitude;
            d = sp > 0.001f ? d / sp : Vector3.up;
            float len = p.Ember ? p.Size : p.Size + sp * 0.045f * (1f - lt * 0.5f);
            Vector3 perp = Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 perp2 = Vector3.Cross(d, perp);
            Vector3 c0 = p.Pos - o;
            // Sparks cool down from white-hot to their colour.
            Color col = Color.Lerp(Color.white, p.Color, Mathf.Clamp01(lt * 3f + (p.Ember ? 1f : 0f)));
            Quad(c0, d * len, perp * p.Size, col, a, false);
            Quad(c0, d * len, perp2 * p.Size, col, a, false);
        }

        Mesh m = b.Mesh;
        m.Clear();
        if (_v.Count > 65000) return;
        m.SetVertices(_v);
        m.SetUVs(0, _uv);
        m.SetColors(_c);
        m.SetTriangles(_t, 0, false);
        m.bounds = new Bounds(Vector3.zero, Vector3.one * 12f * b.Size);

        // Overall brightness (HDR, so bloom picks it up): K * I in rgb, 1/K in alpha (see EnsureMaterial).
        const float K = 16f;
        float I = b.Intensity * 1.6f;
        MeshRenderer mr = b.Go.GetComponent<MeshRenderer>();
        var mpb = new MaterialPropertyBlock();
        mpb.SetVector("_Color", new Vector4(K * I, K * I, K * I, 1f / K));
        mr.SetPropertyBlock(mpb);

        // Not in the VR headset unless asked: draw only into the MonkeFrames camera.
        bool inVR = S?.TagFXInVR ?? false;
        mr.enabled = inVR;
        if (b.Light != null) b.Light.enabled = inVR;
        if (!inVR)
        {
            Camera cam = CameraManager.Instance != null ? CameraManager.Instance.Camera : null;
            if (cam != null)
            {
                var rp = new RenderParams(_mat)
                {
                    camera = cam,
                    matProps = mpb,
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = false,
                    worldBounds = new Bounds(b.Center, Vector3.one * 12f * b.Size),
                };
                Graphics.RenderMesh(rp, m, 0, b.Go.transform.localToWorldMatrix);
            }
        }
    }

    private void Star(Vector3 c, float r, Color col, float a, bool ring)
    {
        Quad(c, Vector3.right * r, Vector3.up * r, col, a, ring);
        Quad(c, Vector3.forward * r, Vector3.up * r, col, a, ring);
        Quad(c, Vector3.right * r, Vector3.forward * r, col, a, ring);
    }

    /// <summary>A quad centred at c spanning ±ax, ±ay. ring = use the ring half of the texture.</summary>
    private void Quad(Vector3 c, Vector3 ax, Vector3 ay, Color col, float a, bool ring)
    {
        int i = _v.Count;
        _v.Add(c - ax - ay); _v.Add(c - ax + ay); _v.Add(c + ax + ay); _v.Add(c + ax - ay);
        float u0 = ring ? 0.5f : 0f, u1 = ring ? 1f : 0.5f;
        _uv.Add(new Vector2(u0, 0)); _uv.Add(new Vector2(u0, 1)); _uv.Add(new Vector2(u1, 1)); _uv.Add(new Vector2(u1, 0));
        Color32 c32 = new Color(col.r, col.g, col.b, Mathf.Clamp01(a));
        _c.Add(c32); _c.Add(c32); _c.Add(c32); _c.Add(c32);
        _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
        _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
    }

    private static float EaseOut(float x) => 1f - Mathf.Pow(1f - Mathf.Clamp01(x), 3f);

    // ---------------- Material / texture / sound ----------------

    /// <summary>
    /// Uses the game's UI shader (always included) as an almost-additive glow: it multiplies colour
    /// by alpha and blends One / OneMinusSrcAlpha, so a big colour with a tiny alpha adds light and
    /// barely darkens what's behind.
    /// </summary>
    private bool EnsureMaterial()
    {
        if (_mat != null) return true;
        Shader sh = Shader.Find("UI/Default");
        if (sh == null) return false;
        _mat = new Material(sh) { name = "MonkeFrames Tag Explosion", renderQueue = 3500, hideFlags = HideFlags.HideAndDontSave };
        _mat.mainTexture = Tex();
        _mat.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual);
        return true;
    }

    /// <summary>Left half: soft glowing dot. Right half: soft ring.</summary>
    private Texture2D Tex()
    {
        if (_tex != null) return _tex;
        const int n = 64;
        _tex = new Texture2D(n * 2, n, TextureFormat.RGBA32, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Trilinear,
            hideFlags = HideFlags.HideAndDontSave,
            name = "MonkeFrames Tag Explosion",
        };
        Color32[] px = new Color32[n * 2 * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            // Dot: bright core + soft falloff
            float dot = Mathf.Clamp01(Mathf.Exp(-r * r * 6f) * 1.1f + Mathf.Exp(-r * r * 40f) * 0.6f) * Mathf.Clamp01((1f - r) * 4f);
            // Ring: thin bright edge with a soft glow inside
            float ring = Mathf.Exp(-Mathf.Pow((r - 0.86f) / 0.06f, 2f)) + 0.35f * Mathf.Exp(-Mathf.Pow((r - 0.8f) / 0.18f, 2f));
            ring = Mathf.Clamp01(ring) * Mathf.Clamp01((1f - r) * 12f);
            px[y * n * 2 + x] = new Color32(255, 255, 255, (byte)(dot * 255f));
            px[y * n * 2 + n + x] = new Color32(255, 255, 255, (byte)(ring * 255f));
        }
        _tex.SetPixels32(px);
        _tex.Apply(true, true);
        return _tex;
    }

    /// <summary>A deep "whoomp": falling sine + noise burst + a short sparkly shimmer.</summary>
    private AudioClip Boom()
    {
        if (_boom != null) return _boom;
        const int rate = 44100;
        const float len = 1.1f;
        int n = (int)(rate * len);
        float[] d = new float[n];
        System.Random rnd = new System.Random(7);
        float phase = 0f, lp = 0f, sparkle = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Exp(-t * 4.5f) * Mathf.Clamp01(t * 400f);
            float f = Mathf.Lerp(34f, 150f, Mathf.Exp(-t * 9f));
            phase += 2f * Mathf.PI * f / rate;
            float thump = Mathf.Sin(phase) * env;

            float white = (float)(rnd.NextDouble() * 2.0 - 1.0);
            lp += (white - lp) * Mathf.Lerp(0.02f, 0.35f, Mathf.Exp(-t * 14f));
            float noise = lp * Mathf.Exp(-t * 7f) * 0.9f;

            sparkle += 2f * Mathf.PI * (2400f + 900f * Mathf.Sin(t * 60f)) / rate;
            float shimmer = Mathf.Sin(sparkle) * Mathf.Exp(-t * 6f) * 0.05f * (0.5f + 0.5f * Mathf.Sin(t * 170f));

            d[i] = thump * 0.8f + noise + shimmer;
        }
        float peak = 0.0001f;
        foreach (float v in d) peak = Mathf.Max(peak, Mathf.Abs(v));
        for (int i = 0; i < n; i++) d[i] = d[i] / peak * 0.9f;
        _boom = AudioClip.Create("MonkeFrames Tag Boom", n, 1, rate, false);
        _boom.SetData(d, 0);
        return _boom;
    }

    private void OnDestroy()
    {
        foreach (Burst b in _bursts.ToArray()) Kill(b);
    }
}

/// <summary>The "Tag Effects" section of the Settings window.</summary>
public static class TagEffectSettings
{
    private static readonly Dictionary<string, string> _hex = new();

    public static float Draw(float x, float y, float w)
    {
        Settings s = Settings.current;
        if (s == null) return y;

        GUI.Label(new Rect(x, y, w - 60, 22), "TAG EFFECTS", Theme.Header);
        s.TagFX = Widgets.Switch("tfx.on", new Rect(x + w - 44, y, 44, 24), s.TagFX, "", "Neon explosion whenever someone gets tagged.");
        y += 30;

        if (!s.TagFX)
        {
            GUI.Label(new Rect(x, y, w, 36), "A neon explosion whenever someone gets tagged. Turn it on to customise it.", Theme.MutedWrap);
            return y + 40;
        }

        // Presets + preview
        string[] presets = ["Neon", "Rainbow", "Subtle", "Huge", "Fireworks"];
        float pw = (w - 4 * 4) / presets.Length;
        for (int i = 0; i < presets.Length; i++)
            if (GUI.Button(new Rect(x + i * (pw + 4), y, pw, 24), presets[i]))
            {
                ApplyPreset(s, presets[i]);
                TagExplosions.Instance?.Preview();
            }
        y += 30;
        if (GUI.Button(new Rect(x, y, w, 28), "Preview explosion", Theme.AccentButton))
            TagExplosions.Instance?.Preview();
        y += 36;

        GUI.Label(new Rect(x, y, 90, 26), "Show for");
        int who = Widgets.Segmented("tfx.who", new Rect(x + 90, y, w - 90, 26), s.TagFXWho, ["Everyone", "Only me", "Others"]);
        if (who >= 0) s.TagFXWho = who;
        y += 32;

        GUI.Label(new Rect(x, y, 90, 26), "Colour");
        int cm = Widgets.Segmented("tfx.cm", new Rect(x + 90, y, w - 90, 26), s.TagFXColorMode, ["Gorilla colour", "Custom", "Rainbow"]);
        if (cm >= 0) s.TagFXColorMode = cm;
        y += 32;
        if (s.TagFXColorMode == 1)
            s.TagFXColor = ColorRow("tfx.c1", x, ref y, w, "Main", s.TagFXColor);
        if (s.TagFXColorMode != 2)
            s.TagFXColor2 = ColorRow("tfx.c2", x, ref y, w, "Accent", s.TagFXColor2);

        s.TagFXSize = Row("tfx.size", x, ref y, w, "Size", s.TagFXSize, 0.2f, 3f, $"{s.TagFXSize:0.0}x");
        s.TagFXDuration = Row("tfx.dur", x, ref y, w, "Duration", s.TagFXDuration, 0.3f, 3f, $"{s.TagFXDuration:0.0} s");
        s.TagFXIntensity = Row("tfx.int", x, ref y, w, "Glow", s.TagFXIntensity, 0f, 4f, $"{s.TagFXIntensity * 100f / 1.5f:0}%");
        s.TagFXSparks = Mathf.RoundToInt(Row("tfx.sp", x, ref y, w, "Sparks", s.TagFXSparks, 0f, 300f, s.TagFXSparks.ToString()));
        s.TagFXSparkSpeed = Row("tfx.sps", x, ref y, w, "Spark speed", s.TagFXSparkSpeed, 0f, 3f, $"{s.TagFXSparkSpeed:0.0}x");
        s.TagFXGravity = Row("tfx.g", x, ref y, w, "Gravity", s.TagFXGravity, 0f, 2f, $"{s.TagFXGravity * 100f:0}%");
        s.TagFXRings = Mathf.RoundToInt(Row("tfx.r", x, ref y, w, "Rings", s.TagFXRings, 0f, 4f, s.TagFXRings.ToString()));

        s.TagFXInVR = Widgets.Switch("tfx.vr", new Rect(x, y, w, 26), s.TagFXInVR, "Also show in VR headset",
            "Off = only the MonkeFrames camera (recordings, free cam) sees the explosions.");
        y += 32;
        s.TagFXFlash = Widgets.Switch("tfx.flash", new Rect(x, y, w / 2f, 26), s.TagFXFlash, "Flash", "Bright white flash at the start.");
        s.TagFXLight = Widgets.Switch("tfx.light", new Rect(x + w / 2f, y, w / 2f, 26), s.TagFXLight, "Light up area", "Lights up nearby walls and gorillas in the explosion's colour.");
        y += 32;
        s.TagFXSound = Widgets.Switch("tfx.sound", new Rect(x, y, w / 2f, 26), s.TagFXSound, "Sound", "A deep neon \"whoomp\".");
        GUI.enabled = s.TagFXSound;
        s.TagFXVolume = Widgets.Slider("tfx.vol", new Rect(x + w / 2f, y, w / 2f - 50, 26), s.TagFXVolume, 0f, 1f);
        GUI.Label(new Rect(x + w - 46, y, 46, 26), $"{s.TagFXVolume * 100f:0}%", Theme.LabelRight);
        GUI.enabled = true;
        return y + 34;
    }

    private static void ApplyPreset(Settings s, string name)
    {
        switch (name)
        {
            case "Neon":
                s.TagFXColorMode = 0; s.TagFXColor2 = new Color(0.3f, 0.95f, 1f);
                s.TagFXSize = 1f; s.TagFXDuration = 1.2f; s.TagFXIntensity = 1.5f; s.TagFXSparks = 90; s.TagFXSparkSpeed = 1f;
                s.TagFXGravity = 0.6f; s.TagFXRings = 2; s.TagFXFlash = true; s.TagFXLight = true;
                break;
            case "Rainbow":
                s.TagFXColorMode = 2; s.TagFXSize = 1.2f; s.TagFXDuration = 1.5f; s.TagFXIntensity = 1.6f; s.TagFXSparks = 140;
                s.TagFXSparkSpeed = 1.1f; s.TagFXGravity = 0.5f; s.TagFXRings = 3; s.TagFXFlash = true; s.TagFXLight = true;
                break;
            case "Subtle":
                s.TagFXColorMode = 0; s.TagFXColor2 = Color.white;
                s.TagFXSize = 0.6f; s.TagFXDuration = 0.8f; s.TagFXIntensity = 0.8f; s.TagFXSparks = 30; s.TagFXSparkSpeed = 0.7f;
                s.TagFXGravity = 0.8f; s.TagFXRings = 1; s.TagFXFlash = false; s.TagFXLight = false;
                break;
            case "Huge":
                s.TagFXColorMode = 0; s.TagFXColor2 = new Color(1f, 0.35f, 0.9f);
                s.TagFXSize = 2.4f; s.TagFXDuration = 2f; s.TagFXIntensity = 2.2f; s.TagFXSparks = 220; s.TagFXSparkSpeed = 1.4f;
                s.TagFXGravity = 0.4f; s.TagFXRings = 4; s.TagFXFlash = true; s.TagFXLight = true;
                break;
            case "Fireworks":
                s.TagFXColorMode = 2; s.TagFXSize = 1.4f; s.TagFXDuration = 2.4f; s.TagFXIntensity = 1.8f; s.TagFXSparks = 260;
                s.TagFXSparkSpeed = 1.8f; s.TagFXGravity = 1.3f; s.TagFXRings = 1; s.TagFXFlash = true; s.TagFXLight = true;
                break;
        }
    }

    private static float Row(string id, float x, ref float y, float w, string label, float value, float min, float max, string shown)
    {
        GUI.Label(new Rect(x, y, 100, 24), label);
        value = Widgets.Slider(id, new Rect(x + 100, y, w - 100 - 64, 24), value, min, max);
        GUI.Label(new Rect(x + w - 60, y, 60, 24), shown, Theme.LabelRight);
        y += 30;
        return value;
    }

    private static Color ColorRow(string id, float x, ref float y, float w, string label, Color value)
    {
        GUI.Label(new Rect(x, y, 100, 26), label);
        Theme.Fill(new Rect(x + 100, y + 2, 40, 22), value, 5);

        string name = "tfx.hex." + id;
        bool focused = GUI.GetNameOfFocusedControl() == name;
        string shown = focused && _hex.TryGetValue(id, out string buf) ? buf : "#" + ColorUtility.ToHtmlStringRGB(value);
        GUI.SetNextControlName(name);
        string typed = GUI.TextField(new Rect(x + 148, y, 96, 26), shown);
        if (focused) _hex[id] = typed;
        string t = typed.Trim();
        if (!t.StartsWith("#")) t = "#" + t;
        if (typed != shown && (t.Length == 7 || t.Length == 4) && ColorUtility.TryParseHtmlString(t, out Color c))
            value = c;

        // Neon quick picks
        Color[] quick = [new Color(1f, 0.1f, 0.6f), new Color(0.2f, 1f, 1f), new Color(0.4f, 1f, 0.2f), new Color(1f, 0.55f, 0.05f), new Color(0.6f, 0.3f, 1f), Color.white];
        float qx = x + 252;
        for (int i = 0; i < quick.Length && qx + 22 <= x + w; i++, qx += 26)
        {
            Rect r = new Rect(qx, y + 3, 22, 20);
            Theme.Fill(r, quick[i], 5);
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) value = quick[i];
        }
        y += 32;
        return value;
    }
}
