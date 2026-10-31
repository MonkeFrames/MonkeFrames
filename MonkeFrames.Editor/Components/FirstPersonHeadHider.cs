using UnityEngine;
using UnityEngine.Rendering;

namespace MonkeFrames.Editor.Components;

// Hide only the filmed gorilla's head for this camera's render. Restore it before
// preview/VR cameras render; never change the recording or the live head collider.
internal sealed class FirstPersonHeadHider : MonoBehaviour
{
    private Camera camera;
    private Transform head, hidden;
    private Vector3 scale;
    private float nearClip;
    private bool configured;

    public void Configure(Camera value, Transform bone)
    {
        Restore();
        if (configured && (camera != value || bone == null))
        {
            if (camera != null) camera.nearClipPlane = nearClip;
            configured = false;
        }
        camera = value; head = bone;
        if (camera != null && bone != null && !configured)
        {
            nearClip = camera.nearClipPlane;
            camera.nearClipPlane = Mathf.Min(nearClip, 0.01f);
            configured = true;
        }
    }

    private void OnEnable()
    {
        Camera.onPreCull += Begin;
        Camera.onPostRender += End;
        RenderPipelineManager.beginCameraRendering += BeginSrp;
        RenderPipelineManager.endCameraRendering += EndSrp;
    }
    private void OnDisable()
    {
        Camera.onPreCull -= Begin; Camera.onPostRender -= End;
        RenderPipelineManager.beginCameraRendering -= BeginSrp;
        RenderPipelineManager.endCameraRendering -= EndSrp;
        Configure(camera, null);
    }
    private void BeginSrp(ScriptableRenderContext context, Camera value) => Begin(value);
    private void EndSrp(ScriptableRenderContext context, Camera value) => End(value);
    private void Begin(Camera value)
    {
        if (value != camera || head == null || hidden != null) return;
        hidden = head; scale = head.localScale;
        head.localScale = Vector3.zero;
    }
    private void End(Camera value) { if (value == camera) Restore(); }
    private void Restore()
    {
        if (hidden != null) hidden.localScale = scale;
        hidden = null;
    }
}
