using MonkeFrames.Editor.Classes;
using UnityEngine;
using MonkeFrames.Editor.UI;
using UnityEngine.Rendering;

namespace MonkeFrames.Editor.Components;

/// <summary>
/// Post processing for the MonkeFrames camera: bloom (makes emission glow), chromatic aberration
/// and vignette. (Motion blur and depth of field are rendered by <see cref="MotionBlurController"/>
/// and fed in here.)
///
/// Gorilla Tag ships without Unity's post-processing shaders, so this is built only from shaders
/// the game does include (URP Unlit with different blend modes): while bloom / chromatic
/// aberration are on, the camera renders into a texture, the effects are layered on with
/// full-screen quads, and the result is drawn to the screen behind the MonkeFrames UI (so it
/// also ends up in MP4 exports). Only your spectator view is affected, never the VR view.
/// </summary>
[DefaultExecutionOrder(9900)] // after Replay Studio sets the viewport
public class PostFX : MonoBehaviour
{
    public static PostFX Instance;

    /// <summary>True while the camera renders into our texture and we draw the final image.</summary>
    public bool Capturing { get; private set; }
    public bool Failed { get; private set; }

    private RenderTexture _src, _final, _ca;
    private readonly RenderTexture[] _chain = new RenderTexture[8];
    private Rect _viewport = new Rect(0, 0, 1, 1);
    private Rect _drawRect;
    private int _processedFrame = -1;
    private Texture _output;

    private Material _ui;
    private RenderTexture _clean, _half, _caMix, _graded, _bright;
    private bool _prevHdr;
    private readonly RenderTexture[] _tmp = new RenderTexture[8];

    private Texture2D _vignette;
    private float _vigIntensity = -1f, _vigSmooth = -1f;

    public PostFX() => Instance = this;

    private static Settings S => Settings.current;

    private static bool NeedsCapture(Settings s) => s != null && (s.Bloom || s.ChromaticAberration || s.ColorGrading);

    // ---------------- Frame loop ----------------

    private void LateUpdate()
    {
        CameraManager cm = CameraManager.Instance;
        Camera cam = cm != null ? cm.Camera : null;
        if (cam == null) return;

        bool want = !Failed && NeedsCapture(S) && !cm.CinemachineState && EnsureMaterials();

        if (want)
        {
            // Replay Studio may have shrunk the view into a viewport: remember it, then render
            // full-texture and place the result in that area ourselves.
            var studio = Replays.ReplayStudio.Instance;
            _viewport = studio != null && studio.Active ? cam.rect : new Rect(0, 0, 1, 1);
            int w = Mathf.Max(16, Mathf.RoundToInt(_viewport.width * Screen.width));
            int h = Mathf.Max(16, Mathf.RoundToInt(_viewport.height * Screen.height));
            _src = EnsureFmt(_src, w, h, Hdr, "MonkeFrames PostFX Source", 24);
            if (!Capturing) _prevHdr = cam.allowHDR;
            cam.allowHDR = true;   // lets lights / emission go above white so bloom can find them

            cam.rect = new Rect(0, 0, 1, 1);
            cam.targetTexture = _src;
            Capturing = true;
        }
        else if (Capturing)
        {
            if (cam.targetTexture == _src) cam.targetTexture = null;
            cam.allowHDR = _prevHdr;
            var studio = Replays.ReplayStudio.Instance;
            cam.rect = studio != null && studio.Active ? _viewport : new Rect(0, 0, 1, 1);
            Capturing = false;
        }

        _drawRect = new Rect(_viewport.x * Screen.width, (1f - _viewport.y - _viewport.height) * Screen.height,
            _viewport.width * Screen.width, _viewport.height * Screen.height);
        if (!Capturing)
        {
            Rect r = cam.rect;
            _drawRect = new Rect(r.x * Screen.width, (1f - r.y - r.height) * Screen.height, r.width * Screen.width, r.height * Screen.height);
        }
    }

    private void OnGUI()
    {
        // Above the game / motion blur overlay (depth 100), below the MonkeFrames UI (depth 0).
        GUI.depth = 90;
        if (Event.current.type != EventType.Repaint)
            return;

        Settings s = S;
        if (s == null) return;
        CameraManager cm = CameraManager.Instance;
        if (cm == null || cm.CinemachineState) return;

        if (Capturing)
        {
            if (_processedFrame != Time.frameCount)
            {
                _processedFrame = Time.frameCount;
                try
                {
                    MotionBlurController blur = cm.Camera != null ? cm.Camera.GetComponent<MotionBlurController>() : null;
                    Texture input = blur != null && blur.HaveFrame && blur.Accum != null ? blur.Accum : _src;
                    _output = Process(input, s);
                }
                catch (System.Exception ex)
                {
                    Failed = true;
                    System.Console.WriteLine($"[MonkeFrames::PostFX] Disabled: {ex}");
                    UIManager.Instance.Status = "Post processing couldn't start on this setup and was turned off.";
                    _output = _src;
                }
            }

            if (_output != null)
                GUI.DrawTexture(_drawRect, _output, ScaleMode.StretchToFill, false);
        }

        Color prev = GUI.color;
        GUI.color = Color.white;

        if (s.FilmGrain && s.GrainIntensity > 0.001f)
        {
            // Fresh random offset every frame so the grain dances like film.
            Texture2D g = Grain();
            float scale = Mathf.Lerp(3f, 0.8f, Mathf.Clamp01(s.GrainSize));
            float tw = _drawRect.width / g.width * scale, th = _drawRect.height / g.height * scale;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(s.GrainIntensity) * 0.6f);
            GUI.DrawTextureWithTexCoords(_drawRect, g, new Rect(Random.value, Random.value, tw, th), true);
            GUI.color = Color.white;
        }

        if (s.Vignette && s.VignetteIntensity > 0.001f)
        {
            GUI.color = new Color(s.VignetteColor.r, s.VignetteColor.g, s.VignetteColor.b, 1f);
            GUI.DrawTexture(_drawRect, Vignette(s.VignetteIntensity, s.VignetteSmoothness, s.VignetteRounded), ScaleMode.StretchToFill, true);
            GUI.color = Color.white;
        }

        if (s.Letterbox)
        {
            float aspect = Mathf.Clamp(s.LetterboxAspect, 1f, 4f);
            float viewAspect = _drawRect.width / Mathf.Max(1f, _drawRect.height);
            if (viewAspect < aspect)
            {
                float bar = (_drawRect.height - _drawRect.width / aspect) / 2f;
                Theme.Fill(new Rect(_drawRect.x, _drawRect.y, _drawRect.width, bar), Color.black, 0);
                Theme.Fill(new Rect(_drawRect.x, _drawRect.yMax - bar, _drawRect.width, bar), Color.black, 0);
            }
        }

        GUI.color = prev;
    }

    // ---------------- Effects ----------------

    private Texture Process(Texture input, Settings s)
    {
        int w = input.width, h = input.height;
        RenderTexture prevActive = RenderTexture.active;

        try
        {
            // Clean copy of the frame with alpha forced to 1 (so the blend weights below are exact).
            // (Unity 6's UI shader multiplies colour by alpha, so alpha must be exactly 1: start
            // from opaque black and write the frame with an alpha that tops it up to 1.)
            _clean = EnsureHalf(_clean, w, h, "MonkeFrames PostFX Frame");
            ClearOpaque(_clean);
            Pass(input, _clean, new Color(1f, 1f, 1f, 0.5f), new Vector4(0, 0, 0, 1), true, 1f, clampWeight: true);
            Texture cur = _clean;

            if (s.ColorGrading)
            {
                // Exposure + white balance as per-channel gain, contrast around a mid-grey pivot:
                // out = (x + p/c - p) * c * gain  ==  ((x - p) * c + p) * gain
                float exp = Mathf.Pow(2f, Mathf.Clamp(s.Exposure, -4f, 4f));
                float temp = Mathf.Clamp(s.Temperature, -1f, 1f) * 0.18f, tint = Mathf.Clamp(s.Tint, -1f, 1f) * 0.12f;
                Vector3 gain = new Vector3(1f + temp + tint * 0.5f, 1f - tint, 1f - temp + tint * 0.5f) * exp;
                float c = Mathf.Clamp(s.Contrast, 0.3f, 2.5f);
                const float pivot = 0.2f;
                float a = pivot / c - pivot;
                _graded = EnsureHalf(_graded, w, h, "MonkeFrames PostFX Graded");
                Pass(cur, _graded, new Color(c * gain.x, c * gain.y, c * gain.z, 1f), new Vector4(a, a, a, 0f), true);
                cur = _graded;
            }

            if (s.Bloom && s.BloomIntensity > 0.001f)
            {
                // ---- Our own bloom (the game ships without Unity's) ----
                // 1) Bright pass at full res: keep only light above the threshold (soft ramp),
                //    stored in 16-bit so gradients stay smooth, scaled by 1/R to fit HDR highlights.
                const float R = 4f;
                float t = Mathf.Lerp(0.35f, 0.95f, Mathf.Clamp01(s.BloomThreshold));
                float k = 1f / Mathf.Max(0.05f, 1f - t);
                _bright = EnsureFmt(_bright, w, h, Bright16, "MonkeFrames Bloom Bright");
                Pass(cur, _bright, new Color(k / R, k / R, k / R, 1f), new Vector4(-t, -t, -t, 0f), true);

                // 2) Blur pyramid: halve the size each step with a 4-tap tent filter
                //    (smooth, round glow, no blocky squares).
                int levels = Mathf.RoundToInt(Mathf.Lerp(4f, 8f, Mathf.Clamp01(s.BloomSize)));
                int lw = w, lh = h, made = 0;
                Texture prevTex = _bright;
                for (int i = 0; i < levels && i < _chain.Length; i++)
                {
                    lw = Mathf.Max(2, lw / 2); lh = Mathf.Max(2, lh / 2);
                    if (lw <= 2 && lh <= 2) break;
                    _chain[i] = EnsureFmt(_chain[i], lw, lh, Bright16, "MonkeFrames Bloom " + i);
                    Tent(prevTex, _chain[i]);
                    prevTex = _chain[i];
                    made = i + 1;
                }

                // 3) Back up the pyramid: each level = its own detail + the tent-upsampled level below,
                //    so the glow has a bright core and a wide, soft falloff. Scatter = how much of the
                //    wide halo is kept.
                float scatter = Mathf.Lerp(0.35f, 0.85f, Mathf.Clamp01(s.BloomScatter));
                for (int i = made - 1; i >= 1; i--)
                {
                    _tmp[i - 1] = EnsureFmt(_tmp[i - 1], _chain[i - 1].width, _chain[i - 1].height, Bright16, "MonkeFrames Bloom Up " + i);
                    Tent(_chain[i], _tmp[i - 1]);
                    Pass(_tmp[i - 1], _chain[i - 1], new Color(1f, 1f, 1f, scatter), Vector4.zero, false);
                }

                // 4) frame + bloom * intensity, done as 2 x lerp(frame, bloom * intensity, 0.5) in a
                //    float buffer (the game's UI shader only blends by "lerp").
                _half = EnsureHalf(_half, w, h, "MonkeFrames PostFX Mix");
                Pass(cur, _half, Color.white, Vector4.zero, true);
                float b = s.BloomIntensity * 1.6f * R;
                Color tintC = s.BloomTint;
                if (made > 0)
                    Pass(_chain[0], _half, new Color(b * tintC.r, b * tintC.g, b * tintC.b, 0.5f), Vector4.zero, false);
                else
                    Pass(cur, _half, Color.white, Vector4.zero, true);
                _final = EnsureHalf(_final, w, h, "MonkeFrames PostFX Final");
                Pass(_half, _final, new Color(2f, 2f, 2f, 1f), Vector4.zero, true);
                cur = _final;
            }

            if (s.ChromaticAberration && s.ChromaticIntensity > 0.001f)
            {
                // Red a little bigger, green in place, blue a little smaller: colour fringes that grow
                // towards the edges like a real lens. Summed as (R + G + B) / 3, then x3.
                float m = s.ChromaticIntensity * 0.03f;
                _caMix = EnsureHalf(_caMix, w, h, "MonkeFrames PostFX CA Mix");
                Pass(cur, _caMix, new Color(1f, 0f, 0f, 1f), Vector4.zero, true, 1f + m);
                Pass(cur, _caMix, new Color(0f, 1f, 0f, 0.5f), Vector4.zero, false, 1f);
                Pass(cur, _caMix, new Color(0f, 0f, 1f, 1f / 3f), Vector4.zero, false, 1f - m);
                _ca = Ensure(_ca, w, h, 0, "MonkeFrames PostFX CA");
                Pass(_caMix, _ca, new Color(3f, 3f, 3f, 1f), Vector4.zero, true);
                cur = _ca;
            }

            return cur;
        }
        finally
        {
            RenderTexture.active = prevActive;
        }
    }

    // ---------------- Helpers ----------------

    /// <summary>
    /// One full-screen pass with the game's built-in UI shader:
    ///   out = (tex + add) * color, blended as lerp(dst, out, out.a).
    /// replace = write alpha too (dst fully replaced when alpha is 1); otherwise only RGB is written,
    /// so the destination keeps alpha 1 for the next pass. 'zoom' scales the picture around its centre.
    /// </summary>
    private void Pass(Texture src, RenderTexture dst, Color color, Vector4 add, bool replace, float zoom = 1f, bool clampWeight = false, Vector2 shift = default)
    {
        // SetVector, not SetColor: SetColor would gamma-convert the numbers (2 would become ~4.6).
        _ui.SetVector("_Color", new Vector4(color.r, color.g, color.b, color.a));
        _ui.SetVector("_TextureSampleAdd", add);
        _ui.SetFloat("_ColorMask", replace ? 15f : 14f);
        float sc = 1f / Mathf.Max(0.01f, zoom);
        _ui.SetTextureScale("_MainTex", new Vector2(sc, sc));
        _ui.SetTextureOffset("_MainTex", new Vector2((1f - sc) / 2f, (1f - sc) / 2f) + shift);
        Graphics.Blit(src, dst, _ui);
    }

    /// <summary>
    /// Downsize/upsize 'src' into 'dst' with a 4-tap tent filter: the average of four bilinear
    /// samples placed diagonally one texel out, i.e. a smooth 4x4 weighted blur per step.
    /// </summary>
    private void Tent(Texture src, RenderTexture dst)
    {
        float tx = 1f / src.width, ty = 1f / src.height;
        Pass(src, dst, Color.white, Vector4.zero, true, 1f, false, new Vector2(-tx, -ty));
        Pass(src, dst, new Color(1f, 1f, 1f, 1f / 2f), Vector4.zero, false, 1f, false, new Vector2(tx, -ty));
        Pass(src, dst, new Color(1f, 1f, 1f, 1f / 3f), Vector4.zero, false, 1f, false, new Vector2(-tx, ty));
        Pass(src, dst, new Color(1f, 1f, 1f, 1f / 4f), Vector4.zero, false, 1f, false, new Vector2(tx, ty));
    }

    private static RenderTextureFormat Bright16 =>
        SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB64) ? RenderTextureFormat.ARGB64 : RenderTextureFormat.ARGB32;

    private static RenderTextureFormat Hdr =>
        SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;

    private static RenderTexture EnsureFmt(RenderTexture rt, int w, int h, RenderTextureFormat fmt, string name, int depth = 0)
    {
        if (rt != null && rt.width == w && rt.height == h && rt.format == fmt)
            return rt;
        if (rt != null) { rt.Release(); Destroy(rt); }
        rt = new RenderTexture(w, h, depth, fmt, RenderTextureReadWrite.Linear)
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        rt.Create();
        return rt;
    }

    private static void ClearOpaque(RenderTexture rt)
    {
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, new Color(0f, 0f, 0f, 1f));
        RenderTexture.active = prev;
    }

    private Texture2D _grain;

    private Texture2D Grain()
    {
        if (_grain != null) return _grain;
        const int n = 256;
        _grain = new Texture2D(n, n, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point,
            hideFlags = HideFlags.HideAndDontSave,
        };
        Color32[] px = new Color32[n * n];
        System.Random r = new System.Random(1234);
        for (int i = 0; i < px.Length; i++)
        {
            // Light and dark specks around mid-grey; alpha = how far from grey.
            float v = (float)(r.NextDouble() + r.NextDouble() + r.NextDouble()) / 3f;
            byte c = (byte)(v > 0.5f ? 255 : 0);
            byte a = (byte)Mathf.Clamp(Mathf.Abs(v - 0.5f) * 2f * 255f * 1.6f, 0, 255);
            px[i] = new Color32(c, c, c, a);
        }
        _grain.SetPixels32(px);
        _grain.Apply();
        return _grain;
    }

    private bool EnsureMaterials()
    {
        if (_ui != null) return true;

        Shader sh = Shader.Find("UI/Default");
        if (sh == null)
        {
            Failed = true;
            UIManager.Instance.Status = "Post processing isn't available (shader missing).";
            return false;
        }
        _ui = new Material(sh) { hideFlags = HideFlags.HideAndDontSave, name = "MonkeFrames PostFX" };
        return true;
    }

    private static RenderTexture EnsureHalf(RenderTexture rt, int w, int h, string name)
    {
        if (rt != null && rt.width == w && rt.height == h)
            return rt;
        if (rt != null) { rt.Release(); Destroy(rt); }
        RenderTextureFormat fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)
            ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
        rt = new RenderTexture(w, h, 0, fmt, RenderTextureReadWrite.Linear)
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        rt.Create();
        return rt;
    }

    private static RenderTexture Ensure(RenderTexture rt, int w, int h, int depth, string name)
    {
        if (rt != null && rt.width == w && rt.height == h)
            return rt;
        if (rt != null) { rt.Release(); Destroy(rt); }
        rt = new RenderTexture(w, h, depth, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        rt.Create();
        return rt;
    }

    private bool _vigRounded;

    private Texture2D Vignette(float intensity, float smooth, bool rounded)
    {
        if (_vignette != null && Mathf.Approximately(intensity, _vigIntensity) && Mathf.Approximately(smooth, _vigSmooth) && rounded == _vigRounded)
            return _vignette;
        _vigIntensity = intensity;
        _vigSmooth = smooth;
        _vigRounded = rounded;

        const int n = 256;
        if (_vignette == null)
            _vignette = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };

        float inner = Mathf.Lerp(0.75f, 0.15f, Mathf.Clamp01(smooth));
        Color32[] px = new Color32[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
            // Rounded = a circle on screen; otherwise it follows the frame's rectangle.
            float r = rounded
                ? Mathf.Sqrt(dx * dx * (16f / 9f) * (16f / 9f) / 3.16f + dy * dy) / 1.25f
                : Mathf.Pow(Mathf.Pow(Mathf.Abs(dx), 4f) + Mathf.Pow(Mathf.Abs(dy), 4f), 0.25f);
            float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(inner, 1f, r)) * Mathf.Clamp01(intensity) * 1.15f;
            px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255));
        }
        _vignette.SetPixels32(px);
        _vignette.Apply();
        return _vignette;
    }

    private void OnDestroy()
    {
        CameraManager cm = CameraManager.Instance;
        if (Capturing && cm != null && cm.Camera != null && cm.Camera.targetTexture == _src)
            cm.Camera.targetTexture = null;
        foreach (RenderTexture rt in new[] { _src, _final, _ca, _clean, _half, _caMix, _graded, _bright })
            if (rt != null) { rt.Release(); Destroy(rt); }
        foreach (RenderTexture rt in _chain)
            if (rt != null) { rt.Release(); Destroy(rt); }
        foreach (RenderTexture rt in _tmp)
            if (rt != null) { rt.Release(); Destroy(rt); }
    }
}
