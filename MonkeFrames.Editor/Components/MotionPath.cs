using System.Collections.Generic;
using System.Linq;
using MonkeFrames.Compiler;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Windows;
using UnityEngine;
using Keyframe = MonkeFrames.Compiler.Models.Keyframe;

namespace MonkeFrames.Editor.Components;

/// <summary>
/// The camera's motion path in the world while the Advanced Keyframe Editor is open, like After Effects': a line
/// along the path with a dot for every frame (dots far apart = the camera is fast there), a marker for every
/// keyframe (selected ones highlighted) and one for the playhead.
/// </summary>
public class MotionPath : MonoBehaviour
{
    private static MotionPath _instance;

    public static void Ensure()
    {
        if (_instance != null) return;
        GameObject go = new GameObject("MonkeFrames Motion Path");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<MotionPath>();
    }

    private LineRenderer _line;
    private MeshFilter _dots, _keys, _selKeys;
    private Transform _head;
    private Material _dotMat, _keyMat, _selMat, _headMat;
    private int _hash = int.MinValue, _selHash = int.MinValue;
    private readonly List<GameObject> _parts = new();

    private void Awake()
    {
        _line = new GameObject("Path").AddComponent<LineRenderer>();
        _line.transform.SetParent(transform, false);
        _line.material = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
        _line.startWidth = _line.endWidth = 0.012f;
        _line.useWorldSpace = true;
        _line.numCapVertices = 2;
        _parts.Add(_line.gameObject);

        _dotMat = Unlit(new Color(1f, 1f, 1f, 1f));
        _keyMat = Unlit(new Color(0.85f, 0.87f, 0.92f));
        _selMat = Unlit(new Color(1f, 0.85f, 0.3f));
        _headMat = Unlit(new Color(0.30f, 0.62f, 1f));
        _dots = Part("Frames", _dotMat);
        _keys = Part("Keyframes", _keyMat);
        _selKeys = Part("Selected keyframes", _selMat);

        GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(head.GetComponent<Collider>());
        head.name = "Playhead";
        head.transform.SetParent(transform, false);
        head.transform.localScale = Vector3.one * 0.07f;
        head.GetComponent<MeshRenderer>().sharedMaterial = _headMat;
        _head = head.transform;
        _parts.Add(head);
        Show(false);
    }

    private static Material Unlit(Color c)
    {
        // (the particle shader is the one the keyframe orbs use, so it's always in the game)
        Shader s = Shader.Find("Universal Render Pipeline/Unlit");
        if (s == null) s = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        var m = new Material(s);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        m.color = c;
        return m;
    }

    private MeshFilter Part(string name, Material mat)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);
        MeshFilter mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        _parts.Add(go);
        return mf;
    }

    private bool _shown = true;
    private void Show(bool on)
    {
        if (_shown == on) return;
        _shown = on;
        foreach (GameObject g in _parts) if (g != null) g.SetActive(on);
    }

    private void LateUpdate()
    {
        var project = KeyframeManager.Instance?.Project;
        bool show = project != null && AdvancedKeyframeEditor.ShowPath && UIManager.Instance != null && UIManager.Instance.Drawing
            && CameraManager.Instance != null && !CameraManager.Instance.InPlayback && !CameraManager.Instance.CinemachineState
            && LaneEditing.Clean != null && LaneEditing.Clean.Count > 1;
        Show(show);
        if (!show) return;

        List<Keyframe> frames = LaneEditing.Clean;
        int h = project.AnimationHash() ^ frames.Count;
        if (h != _hash) { _hash = h; _selHash = int.MinValue; Rebuild(project, frames); }

        int[] sel = AdvancedKeyframeEditor.SelectedKeyframes.ToArray();
        int sh = sel.Aggregate(17, (a, b) => a * 31 + b);
        if (sh != _selHash)
        {
            _selHash = sh;
            int[] fs = Timeline.FrameStarts(project.Keyframes, project.FPS);
            var pts = new List<Vector3>();
            foreach (int i in sel) if (i < fs.Length - 1) pts.Add(frames[Mathf.Clamp(fs[i], 0, frames.Count - 1)].Position);
            Markers(_selKeys.sharedMesh, pts, 0.055f);
        }

        int f = Timeline.FrameAt(project.Keyframes, project.FPS, AdvancedKeyframeEditor.PlayheadTime);
        _head.position = frames[Mathf.Clamp(f, 0, frames.Count - 1)].Position;
    }

    private void Rebuild(Compiler.Models.Project project, List<Keyframe> frames)
    {
        int n = frames.Count;
        int stride = Mathf.Max(1, n / 2000);
        var line = new List<Vector3>();
        for (int i = 0; i < n; i += stride) line.Add(frames[i].Position);
        line.Add(frames[n - 1].Position);
        _line.positionCount = line.Count;
        _line.SetPositions(line.ToArray());
        Color c = Settings.current?.AccentColor ?? new Color(1f, 0.4f, 0.3f);
        _line.startColor = _line.endColor = new Color(c.r, c.g, c.b, 0.85f);

        // one dot per frame (fewer on very long animations)
        int dotStride = Mathf.Max(1, n / 1500);
        var dots = new List<Vector3>();
        for (int i = 0; i < n; i += dotStride) dots.Add(frames[i].Position);
        Markers(_dots.sharedMesh, dots, 0.014f);

        int[] fs = Timeline.FrameStarts(project.Keyframes, project.FPS);
        var keys = new List<Vector3>();
        for (int i = 0; i < project.Keyframes.Count; i++) keys.Add(frames[Mathf.Clamp(fs[i], 0, n - 1)].Position);
        Markers(_keys.sharedMesh, keys, 0.045f);
    }

    /// <summary>Little octahedrons at each point.</summary>
    private static void Markers(Mesh mesh, List<Vector3> pts, float size)
    {
        mesh.Clear();
        if (pts.Count == 0) return;
        Vector3[] dirs = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        int[] tri = { 2, 4, 0, 2, 0, 5, 2, 5, 1, 2, 1, 4, 3, 0, 4, 3, 5, 0, 3, 1, 5, 3, 4, 1 };
        var v = new Vector3[pts.Count * 6];
        var col = new Color[pts.Count * 6];
        var t = new int[pts.Count * 24];
        for (int p = 0; p < pts.Count; p++)
        {
            for (int d = 0; d < 6; d++) { v[p * 6 + d] = pts[p] + dirs[d] * size; col[p * 6 + d] = Color.white; }
            for (int k = 0; k < 24; k++) t[p * 24 + k] = p * 6 + tri[k];
        }
        mesh.vertices = v;
        mesh.colors = col;
        mesh.triangles = t;
        mesh.RecalculateBounds();
    }
}
