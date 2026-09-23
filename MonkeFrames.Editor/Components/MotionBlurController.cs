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
/// </summary>
[DefaultExecutionOrder(10000)] // after CameraManager has placed the camera for this frame
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

    /// <summary>Request blur on or off with a strength 0..1.</summary>
    public void Set(bool enabled, float strength)
    {
        _enabled = enabled;
        if (enabled)
            _strength = Mathf.Clamp01(strength);
    }

    private void LateUpdate()
    {
        _haveFrame = false;

        Camera cam = CameraManager.Instance != null ? CameraManager.Instance.Camera : null;
        if (cam == null)
            return;

        Transform tr = cam.transform;
        Vector3 curPos = tr.position;
        Quaternion curRot = tr.rotation;
        float curFov = cam.fieldOfView;

        if (_enabled && !_failed && _hasPrevPose)
        {
            float moved = Vector3.Distance(_prevPos, curPos);
            float turned = Quaternion.Angle(_prevRot, curRot);
            bool isCut = moved > MaxBlurDistance || turned > MaxBlurAngle;
            bool isMoving = moved > 0.0005f || turned > 0.02f || Mathf.Abs(curFov - _prevFov) > 0.01f;

            if (!isCut && isMoving)
            {
                try
                {
                    RenderBlur(cam, curPos, curRot, curFov);
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
                }
            }
        }

        _prevPos = curPos;
        _prevRot = curRot;
        _prevFov = curFov;
        _hasPrevPose = true;
    }

    private void RenderBlur(Camera cam, Vector3 curPos, Quaternion curRot, float curFov)
    {
        EnsureResources();
        if (_blend == null)
            throw new System.Exception("no blend shader available");

        bool exporting = CameraManager.Instance.doRecording;
        int samples = exporting ? ExportSamples : PreviewSamples;

        // Shutter: fraction of this frame's movement that is blurred (trailing behind the camera).
        float shutter = Mathf.Lerp(0.25f, 1f, _strength);

        RenderTexture oldTarget = cam.targetTexture;
        Transform tr = cam.transform;

        try
        {
            for (int s = 0; s < samples; s++)
            {
                // Sample 0 is the current pose; later samples step back towards the previous pose.
                float back = samples == 1 ? 0f : shutter * s / (samples - 1);

                tr.SetPositionAndRotation(
                    Vector3.Lerp(curPos, _prevPos, back),
                    Quaternion.Slerp(curRot, _prevRot, back));
                cam.fieldOfView = Mathf.Lerp(curFov, _prevFov, back);

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
        }

        _haveFrame = true;
    }

    private void EnsureResources()
    {
        int w = Screen.width, h = Screen.height;

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

        Color prev = GUI.color;
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _accum, ScaleMode.StretchToFill, false);
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
