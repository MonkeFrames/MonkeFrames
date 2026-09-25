using MonkeFrames.Editor.Classes;
using UnityEngine;

namespace MonkeFrames.Editor.Components;

/// <summary>
/// Real camera motion blur ("sub-frame accumulation", like a film camera's open shutter).
///
/// Every frame the camera moves, it is rendered several extra times at in-between poses along
/// the path from where it was last frame to where it is now. Those renders are averaged into one
/// image which is drawn over the screen (behind the MonkeFrames UI). Because the samples are true
/// renders of the scene, the blur follows the camera's actual motion, has no ghost "trails", and
/// is captured by MP4 export.
///
/// Strength = shutter length: 0 blurs a short slice of the movement, 1 blurs the whole
/// movement between frames. Huge jumps (Cut transitions, teleports) are never blurred.
///
/// The shutter is centred on the current frame (half behind, half predicted ahead), so the
/// blurred image never looks like it trails behind the real camera.
///
/// When an Other Cameras mode is following a gorilla, that gorilla is the "anchor": for every
/// sample it is moved along with the camera, so the gorilla you're watching stays sharp while
/// the world streaks past (like a real camera riding along with them).
/// </summary>
[DefaultExecutionOrder(10000)] // fallback: after CameraManager has placed the camera for this frame
public class MotionBlurController : MonoBehaviour
{
    private const int PreviewSamples = 6;
    private const int ExportSamples = 12;
    private const float MaxBlurDistance = 6f;   // metres moved in one frame before we treat it as a cut
    private const float MaxBlurAngle = 50f;     // degrees turned in one frame before we treat it as a cut

    private bool _enabled;
    private float _strength = 0.5f;

    private RenderTexture _accum;
    private RenderTexture _sample;
    private Material _blend;
    private bool _haveFrame;
    private bool _failed;

    private bool _hasPrevPose;
    private Vector3 _prevPos;
    private Quaternion _prevRot;
    private float _prevFov;

    private Transform _anchor;
    private Transform _prevAnchor;
    private Vector3 _anchorPrevPos;

    /// <summary>The finished blurred / depth-of-field image for this frame (if any).</summary>
    public bool HaveFrame => _haveFrame;
    public RenderTexture Accum => _accum;

    /// <summary>Focus distance actually used this frame (for the Post Processing window).</summary>
    public float CurrentFocus { get; private set; } = 3f;

    /// <summary>Request blur on or off with a strength 0..1.</summary>
    /// <param name="anchor">Optional object that moves with the camera (the watched gorilla); it is kept sharp.</param>
    public void Set(bool enabled, float strength, Transform anchor = null)
    {
        _enabled = enabled;
        _anchor = anchor;
        if (enabled)
            _strength = Mathf.Clamp01(strength);
    }

    private int _tickFrame = -1;
    private bool _beforeRenderFailed;

    private void OnEnable() => Application.onBeforeRender += OnBeforeRender;
    private void OnDisable() => Application.onBeforeRender -= OnBeforeRender;

    // Preferred path: render the blur samples right before the frame is drawn, AFTER the
    // Other Cameras mode has placed the camera on the (fully updated) target player.
    // Doing it in LateUpdate could capture the camera one frame behind a fast-moving gorilla.
    private void OnBeforeRender()
    {
        if (_beforeRenderFailed || !CameraModes.BeforeRenderWorks)
            return;

        try
        {
            if (CameraModes.Instance != null)
                CameraModes.Instance.PlaceCamera();
            Tick();
        }
        catch (System.Exception ex)
        {
            // Some setups don't allow rendering from here; fall back to LateUpdate.
            _beforeRenderFailed = true;
            System.Console.WriteLine($"[MonkeFrames::MotionBlur] Falling back to LateUpdate: {ex.Message}");
        }
    }

    private void LateUpdate()
    {
        if (!_beforeRenderFailed && CameraModes.BeforeRenderWorks)
            return;

        if (CameraModes.Instance != null)
            CameraModes.Instance.PlaceCamera();
        Tick();
    }

    private void Tick()
    {
        if (_tickFrame == Time.frameCount)
            return;
        _tickFrame = Time.frameCount;

        _haveFrame = false;

        Camera cam = CameraManager.Instance != null ? CameraManager.Instance.Camera : null;
        if (cam == null)
            return;

        Transform tr = cam.transform;
        Vector3 curPos = tr.position;
        Quaternion curRot = tr.rotation;
        float curFov = cam.fieldOfView;

        // Track the anchor's movement this frame (reset when it changes).
        Transform anchor = _anchor;
        Vector3 anchorCur = anchor != null ? anchor.position : Vector3.zero;
        bool anchorValid = anchor != null && anchor == _prevAnchor
            && Vector3.Distance(_anchorPrevPos, anchorCur) < MaxBlurDistance;
        Vector3 anchorDelta = anchorValid ? _anchorPrevPos - anchorCur : Vector3.zero; // towards last frame

        // Post Processing window: global motion blur + depth of field.
        Settings st = Settings.current;
        bool globalBlur = st != null && st.GlobalMotionBlur;
        bool dof = st != null && st.DepthOfField;
        bool blurOn = _enabled || globalBlur;
        float strength = globalBlur ? Mathf.Max(_enabled ? _strength : 0f, st.GlobalMotionBlurStrength) : _strength;

        if (dof)
            CurrentFocus = UpdateFocus(cam, curPos, curRot, st);

        if ((blurOn || dof) && !_failed && (_hasPrevPose || dof))
        {
            float moved = _hasPrevPose ? Vector3.Distance(_prevPos, curPos) : 0f;
            float turned = _hasPrevPose ? Quaternion.Angle(_prevRot, curRot) : 0f;
            bool isCut = moved > MaxBlurDistance || turned > MaxBlurAngle;
            bool isMoving = _hasPrevPose && (moved > 0.0005f || turned > 0.02f || Mathf.Abs(curFov - _prevFov) > 0.01f);
            bool motion = blurOn && isMoving && !isCut;

            if (motion || dof)
            {
                try
                {
                    RenderBlur(cam, curPos, curRot, curFov, anchorValid && motion ? anchor : null, anchorCur, anchorDelta,
                        motion ? strength : -1f, dof ? st : null);
                }
                catch (System.Exception ex)
                {
                    _failed = true;
                    System.Console.WriteLine($"[MonkeFrames::MotionBlur] Disabled: {ex.Message}");
                    UIManager.Instance.Status = "Motion blur couldn't start on this setup and was turned off.";
                }
                finally
                {
                    // Always put the camera back exactly where it was.
                    tr.SetPositionAndRotation(curPos, curRot);
                    cam.fieldOfView = curFov;
                    if (anchorValid && anchor != null)
                        anchor.position = anchorCur;
                }
            }
        }

        _prevPos = curPos;
        _prevRot = curRot;
        _prevFov = curFov;
        _hasPrevPose = true;

        _prevAnchor = anchor;
        _anchorPrevPos = anchorCur;
    }

    private float _smoothFocus = -1f;

    /// <summary>Auto focus: the gorilla an Other Camera is filming, else whatever is in the middle of the view.</summary>
    private float UpdateFocus(Camera cam, Vector3 pos, Quaternion rot, Settings st)
    {
        float want = st.DofFocus;
        if (st.DofAutoFocus)
        {
            CameraModes cm = CameraModes.Instance;
            if (cm != null && cm.Mode != CameraMode.Free && cm.Target != null && cm.Target.Available && cm.Mode != CameraMode.FirstPerson)
                want = Vector3.Distance(pos, cm.Target.Head.position);
            else if (Physics.Raycast(pos + rot * Vector3.forward * 0.05f, rot * Vector3.forward, out RaycastHit hit, 200f, ~0, QueryTriggerInteraction.Ignore))
                want = hit.distance + 0.05f;
            else
                want = 50f;
        }
        want = Mathf.Clamp(want, 0.2f, 200f);

        if (_smoothFocus < 0f) _smoothFocus = want;
        // Glide focus changes like a real focus puller (in log space so near/far feel even).
        float speed = Mathf.Lerp(1.2f, 20f, Mathf.Clamp01(st.DofFocusSpeed));
        float k = 1f - Mathf.Exp(-speed * Mathf.Max(Time.unscaledDeltaTime, 0.0001f));
        _smoothFocus = Mathf.Exp(Mathf.Lerp(Mathf.Log(_smoothFocus), Mathf.Log(want), k));
        return _smoothFocus;
    }

    private void RenderBlur(Camera cam, Vector3 curPos, Quaternion curRot, float curFov,
        Transform anchor, Vector3 anchorCur, Vector3 anchorDelta, float motionStrength, Settings dof)
    {
        // Render at the size of the camera's on-screen area (the whole screen, or the Replay
        // Studio viewport), with a full-texture rect so the framing matches exactly.
        Rect viewport = cam.rect;
        _drawRect = new Rect(viewport.x * Screen.width, (1f - viewport.y - viewport.height) * Screen.height,
            viewport.width * Screen.width, viewport.height * Screen.height);
        EnsureResources(Mathf.Max(16, cam.pixelWidth), Mathf.Max(16, cam.pixelHeight));
        if (_blend == null)
            throw new System.Exception("no blend shader available");

        bool exporting = CameraManager.Instance.doRecording;
        bool motion = motionStrength >= 0f;
        int preview = Settings.current != null ? Mathf.Clamp(Settings.current.MotionBlurSamples, 2, 32) : PreviewSamples;
        int samples = motion ? (exporting ? Mathf.Max(ExportSamples, preview * 2) : preview) : 1;
        if (dof != null)
            samples = Mathf.Max(samples, Mathf.Clamp(exporting ? dof.DofSamples * 2 : dof.DofSamples, 4, 48));

        // Shutter: fraction of this frame's movement that is blurred (trailing behind the camera).
        float shutter = motion ? Mathf.Lerp(0.25f, 1f, motionStrength) : 0f;

        // Depth of field: the lens is a disc; every sample looks at the same focus point from a
        // different spot on that disc, so only things at the focus distance stay sharp.
        float aperture = dof != null ? Mathf.Lerp(0.004f, 0.09f, Mathf.Clamp01(dof.DofAperture)) : 0f;
        float focus = CurrentFocus;

        RenderTexture oldTarget = cam.targetTexture;
        Transform tr = cam.transform;
        cam.rect = new Rect(0, 0, 1, 1);

        try
        {
            for (int s = 0; s < samples; s++)
            {
                // Centred shutter: from half a shutter ahead (predicted) to half a shutter behind.
                // back < 0 = ahead of the current pose, back > 0 = towards last frame's pose.
                float u = samples == 1 ? 0.5f : s / (float)(samples - 1);
                float back = shutter * (u - 0.5f);

                Vector3 sp = Vector3.LerpUnclamped(curPos, _prevPos, back);
                Quaternion sr = Quaternion.SlerpUnclamped(curRot, _prevRot, back);
                if (aperture > 0f)
                {
                    // Even spread over the lens disc (golden-angle spiral).
                    float r = Mathf.Sqrt((s + 0.5f) / samples) * aperture;
                    float a = s * 2.39996323f;
                    Vector3 focusPoint = sp + sr * Vector3.forward * focus;
                    sp += sr * new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f);
                    sr = Quaternion.LookRotation(focusPoint - sp, sr * Vector3.up);
                }
                tr.SetPositionAndRotation(sp, sr);
                cam.fieldOfView = Mathf.Clamp(Mathf.LerpUnclamped(curFov, _prevFov, back), 1f, 179f);

                // Move the watched gorilla along with the camera so it stays sharp.
                if (anchor != null)
                    anchor.position = anchorCur + anchorDelta * back;

                cam.targetTexture = _sample;
                cam.Render();

                // Running average: accum = lerp(accum, sample, 1 / (s + 1))
                if (s == 0)
                {
                    Graphics.Blit(_sample, _accum);
                }
                else
                {
                    _blend.color = new Color(1f, 1f, 1f, 1f / (s + 1));
                    Graphics.Blit(_sample, _accum, _blend);
                }
            }
        }
        finally
        {
            cam.targetTexture = oldTarget;
            cam.rect = viewport;
            if (anchor != null)
                anchor.position = anchorCur;
        }

        _haveFrame = true;
    }

    private Rect _drawRect;

    private void EnsureResources(int w, int h)
    {

        if (_sample == null || _sample.width != w || _sample.height != h)
        {
            Release();

            _sample = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = "MonkeFrames Blur Sample",
                hideFlags = HideFlags.HideAndDontSave,
            };
            _accum = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = "MonkeFrames Blur Accumulation",
                hideFlags = HideFlags.HideAndDontSave,
            };
            _sample.Create();
            _accum.Create();
        }

        if (_blend == null)
        {
            // Any always-included alpha-blended shader that tints _MainTex by _Color works for averaging.
            Shader shader = Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default");
            if (shader != null)
                _blend = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }
    }

    private void OnGUI()
    {
        // Behind the MonkeFrames UI (higher depth = further back), on top of the game.
        GUI.depth = 100;

        if (!_haveFrame || _accum == null || Event.current.type != EventType.Repaint)
            return;
        // Post Processing draws the final image itself (with this blur as its input).
        if (PostFX.Instance != null && PostFX.Instance.Capturing)
            return;

        Color prev = GUI.color;
        GUI.color = Color.white;
        GUI.DrawTexture(_drawRect.width > 1 ? _drawRect : new Rect(0, 0, Screen.width, Screen.height), _accum, ScaleMode.StretchToFill, false);
        GUI.color = prev;
    }

    private void Release()
    {
        if (_sample != null) { _sample.Release(); Destroy(_sample); _sample = null; }
        if (_accum != null) { _accum.Release(); Destroy(_accum); _accum = null; }
    }

    private void OnDestroy()
    {
        Release();
        if (_blend != null)
            Destroy(_blend);
    }
}
