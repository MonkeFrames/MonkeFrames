using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MonkeFrames.Compiler;
using MonkeFrames.Compiler.Models;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using Keyframe = MonkeFrames.Compiler.Models.Keyframe;
using KeyShape = MonkeFrames.Editor.UI.Draw2D.KeyShape;

namespace MonkeFrames.Editor.Windows;

/// <summary>
/// After Effects style keyframing for the MonkeFrames camera: a timeline with a lane per property (Position,
/// Rotation, Zoom, Time of Day) where each property has its own keyframes at any time, keyframe interpolation icons,
/// Easy Ease, hold and auto bezier keys, and a Graph Editor (speed and value graphs with influence / speed
/// handles). It edits the project's normal MonkeFrames keyframes, so the Keyframe Editor, Player, export and
/// Replay Studio all see the same animation.
/// </summary>
public class AdvancedKeyframeEditor : IEditorWindow
{
    public string Name => "Advanced Keyframe Editor (BETA)";
    public Rect Rect => new Rect(20, Mathf.Max(60, Screen.height - 560), Mathf.Min(1340, Screen.width - 40), 520);

    public static AdvancedKeyframeEditor Instance { get; private set; }

    /// <summary>The editor takes the keyboard (Space, F9, Delete, J / K ...) while the mouse is over it.</summary>
    public static bool CapturingKeys => Instance != null && Instance.Visible && Instance.PointerOver && UIManager.Instance != null && UIManager.Instance.Visible;

    /// <summary>The editor is playing the animation (with the project's music).</summary>
    public static bool PlayingPreview => Instance != null && Instance.Visible && Instance._playing;

    /// <summary>Show the camera's path in the world (After Effects' motion path, one dot per frame).</summary>
    public static bool ShowPath => Instance != null && Instance.Visible && Instance._showPath;

    /// <summary>Playhead time (s) and the selected keyframe times, for the motion path.</summary>
    public static float PlayheadTime => Instance?._time ?? 0f;
    public static IEnumerable<int> SelectedKeyframes => Instance == null ? Enumerable.Empty<int>() : Instance.SelectedIndices();

    private static Compiler.Models.Project P => KeyframeManager.Instance.Project;
    private static List<Keyframe> Keys => P.Keyframes;

    public AdvancedKeyframeEditor() { Instance = this; }

    // =====================================================================
    //  State
    // =====================================================================

    private float _time;                    // playhead (s)
    private bool _playing;
    private float _viewStart = -0.25f;      // time at the left edge of the timeline
    private float _pps = 80f;               // pixels per second
    private bool _fitPending = true;
    private bool _graph;                    // Graph Editor
    private int _graphMode;                 // 0 speed graph, 1 value graph
    private KeyLanes _graphLanes = KeyLanes.Position;
    private bool _snap = true;
    private bool _showPath = true;
    private float _gMin = 0f, _gMax = 1f;   // graph value range

    private readonly HashSet<(string guid, KeyLanes lane)> _sel = new();

    private readonly List<(List<Keyframe> keys, Wiggle wiggle)> _undo = new(), _redo = new();
    private (KeyLanes lane, float t, Keyframe values, LaneCurve curve)[] _clipboard;

    // derived data (rebuilt whenever the animation changes)
    private int _hash = int.MinValue;
    private float[] _starts = new float[1];
    private int[] _frameStarts = new int[1];
    private float _length;
    private List<Keyframe> _frames = new();
    private readonly Dictionary<KeyLanes, float[]> _speed = new();
    private readonly Dictionary<KeyLanes, Vector3[]> _value = new();

    // layout (window-local)
    private float _w, _h;
    private Rect _toolbar, _left, _ruler, _rows, _status, _graphRect, _leftBottom;
    private float _tlX, _tlW;
    private const float LeftW = 372f, RulerH = 26f, SummaryH = 26f, RowH = 30f, AudioH = 34f, StatusH = 22f;

    // interaction
    private enum DragKind { None, Scrub, Keys, Marquee, Handle, Value, Resize, Pan, GraphKey }
    private DragKind _drag;
    private Vector2 _dragStart;
    private bool _dragMoved;
    private int _dragControl;
    private float[] _dragStarts;
    private HashSet<int> _dragMoving;
    private float _dragKeyTime;
    private (KeyLanes lane, int key, bool outgoing, int comp) _dragHandle;
    private float _panStartView;
    private Vector2 _resizeStartSize;
    private string _hoverInfo;

    private string _typingId, _typingText;
    private string _scrubId;
    private float _scrubStartValue;
    private bool _scrubBegan;

    private int _lastKeysFrame = -1;
    private bool _orbsDirty;

    private IEditorWindowManager _mgr;
    private IEditorWindowManager Mgr => _mgr ??= UIManager.Instance?.Windows.FirstOrDefault(w => w.Window == this);
    private bool Visible => Mgr != null && Mgr.Visible;
    private bool PointerOver
    {
        get
        {
            if (Mgr == null || Mouse.current == null) return false;
            Vector2 p = Mouse.current.position.ReadValue();
            return Mgr.WindowPosition.Contains(new Vector2(p.x, Screen.height - p.y));
        }
    }

    private static readonly Color PlayheadColor = new(0.30f, 0.62f, 1f);
    private static readonly Color KeyGrey = new(0.80f, 0.82f, 0.86f);
    private static readonly Color RowA = new(1f, 1f, 1f, 0.025f);
    private static readonly Color RowB = new(1f, 1f, 1f, 0.05f);

    private static Color LaneColor(KeyLanes l) => l switch
    {
        KeyLanes.Position => new Color(1f, 0.62f, 0.28f),
        KeyLanes.Rotation => new Color(0.42f, 0.76f, 1f),
        KeyLanes.Zoom => new Color(0.78f, 0.55f, 1f),
        _ => new Color(1f, 0.86f, 0.32f),
    };

    private static GUIStyle _tiny, _tinyRight, _valStyle, _valStyleRight, _mono;
    private static void Styles()
    {
        if (_tiny != null) return;
        _tiny = new GUIStyle(Theme.MutedSmall) { fontSize = 11, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
        _tinyRight = new GUIStyle(_tiny) { alignment = TextAnchor.MiddleRight };
        _valStyle = new GUIStyle(Theme.Label) { fontSize = 12, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
        _valStyleRight = new GUIStyle(_valStyle) { alignment = TextAnchor.MiddleRight };
        _mono = new GUIStyle(Theme.Title) { fontSize = 16, alignment = TextAnchor.MiddleLeft };
    }

    // =====================================================================
    //  Window
    // =====================================================================

    public void OnOpen()
    {
        MotionPath.Ensure();
        (UIManager.Instance?.Windows.FirstOrDefault(w => w.Window is Player)?.Window as Player)?.EnsureAudio();
        _hash = int.MinValue;
        _fitPending = true;
        Refresh(force: true);
    }

    public void OnClose()
    {
        _playing = false;
        Player.PreviewAudio(false, _time, false);
        if (_orbsDirty) { KeyframeManager.Instance.RefreshOrbs(); _orbsDirty = false; }
    }

    public void OnDraw()
    {
        Styles();
        if (KeyframeManager.Instance?.Project == null) return;
        Event e = Event.current;

        if (CameraManager.Instance.InPlayback)
        {
            Widgets.Spinner(new Vector2(40, 70), 7f, Theme.Accent);
            GUI.Label(new Rect(60, 58, 400, 24), "Playing back / exporting...", Theme.Muted);
            return;
        }

        if (e.type == EventType.Layout)
        {
            Refresh(force: false);
            if (Time.frameCount != _lastKeysFrame) { _lastKeysFrame = Time.frameCount; HandleKeyboard(); }
        }

        Layout();
        if (_fitPending && _tlW > 50) { Fit(); _fitPending = false; }

        if (_playing && e.type == EventType.Repaint) Advance();

        DrawToolbar();
        DrawLeft();
        DrawRuler();
        if (_graph) DrawGraph(); else DrawRows();
        DrawPlayheadLine();
        DrawLeftBottom();
        DrawStatus();
        DrawResizeGrip();
        HandleWheel();

        if (e.type == EventType.MouseUp && _orbsDirty && _drag == DragKind.None)
        {
            KeyframeManager.Instance.RefreshOrbs();
            _orbsDirty = false;
        }
    }

    private void Layout()
    {
        Rect wr = Mgr != null ? Mgr.WindowPosition : Rect;
        _w = wr.width; _h = wr.height;
        float top = Theme.TitleHeight + 6;
        _toolbar = new Rect(10, top, _w - 20, 28);
        float y = _toolbar.yMax + 6;
        _status = new Rect(10, _h - StatusH - 6, _w - 20, StatusH);
        _left = new Rect(10, y, LeftW, _status.y - 6 - y);
        _tlX = _left.xMax + 8;
        _tlW = _w - 10 - _tlX;
        _ruler = new Rect(_tlX, y, _tlW, RulerH);
        float rowsTop = _ruler.yMax;
        _rows = new Rect(_tlX, rowsTop, _tlW, _status.y - 6 - rowsTop);
        _graphRect = _rows;
        float lanesBottom = rowsTop + SummaryH + LaneEditing.Lanes.Length * RowH + (Player.LoadedClip != null ? AudioH : 0f);
        _leftBottom = new Rect(10, lanesBottom + 10, LeftW, _status.y - 8 - (lanesBottom + 10));
    }

    // =====================================================================
    //  Data
    // =====================================================================

    /// <summary>Rebuild the animation and everything derived from it when the project changed.</summary>
    private void Refresh(bool force)
    {
        int h = P.AnimationHash();
        if (!force && h == _hash) return;
        LaneEditing.Build(P);
        if (LaneEditing.SyncUnkeyed(P)) { _orbsDirty = true; }
        _hash = P.AnimationHash();
        Derive();
    }

    /// <summary>Call after changing keyframes.</summary>
    private void Changed(bool orbsNow = false)
    {
        Refresh(force: true);
        if (orbsNow) { KeyframeManager.Instance.RefreshOrbs(); _orbsDirty = false; }
        else _orbsDirty = true;
        ApplyCamera();
    }

    private void Derive()
    {
        _starts = Timeline.Starts(Keys);
        _frameStarts = Timeline.FrameStarts(Keys, P.FPS);
        _length = _starts[Keys.Count];
        _frames = LaneEditing.Clean ?? new List<Keyframe>();
        int n = _frames.Count;
        float fps = P.FPS;
        var sp = new float[n]; var sr = new float[n]; var sz = new float[n]; var st = new float[n];
        var vp = new Vector3[n]; var vr = new Vector3[n]; var vz = new Vector3[n]; var vt = new Vector3[n];
        Vector3 prevE = Vector3.zero, rawPrev = Vector3.zero;
        float prevT = 0f, rawPrevT = 0f;
        for (int f = 0; f < n; f++)
        {
            Keyframe k = _frames[f];
            vp[f] = k.Position;
            Vector3 raw = k.Rotation;
            if (f == 0) prevE = new Vector3(Mathf.DeltaAngle(0, raw.x), Mathf.DeltaAngle(0, raw.y), Mathf.DeltaAngle(0, raw.z));
            else prevE += new Vector3(Mathf.DeltaAngle(rawPrev.x, raw.x), Mathf.DeltaAngle(rawPrev.y, raw.y), Mathf.DeltaAngle(rawPrev.z, raw.z));
            rawPrev = raw;
            vr[f] = prevE;
            vz[f] = new Vector3(k.FieldOfView, 0, 0);
            float tod = k.TimeOfDay;
            if (f == 0 || tod < 0 || rawPrevT < 0) prevT = tod;
            else prevT += Mathf.Repeat(tod - rawPrevT + 12f, 24f) - 12f;
            rawPrevT = tod;
            vt[f] = new Vector3(prevT, 0, 0);
            if (f + 1 < n)
            {
                Keyframe b = _frames[f + 1];
                sp[f] = (b.Position - k.Position).magnitude * fps;
                sr[f] = Quaternion.Angle(k.QuatRotation, b.QuatRotation) * fps;
                sz[f] = Mathf.Abs(b.FieldOfView - k.FieldOfView) * fps;
                st[f] = k.TimeOfDay >= 0 && b.TimeOfDay >= 0 ? Mathf.Abs(Mathf.Repeat(b.TimeOfDay - k.TimeOfDay + 12f, 24f) - 12f) * fps : 0f;
            }
            else if (f > 0) { sp[f] = sp[f - 1]; sr[f] = sr[f - 1]; sz[f] = sz[f - 1]; st[f] = st[f - 1]; }
        }
        _speed[KeyLanes.Position] = sp; _speed[KeyLanes.Rotation] = sr; _speed[KeyLanes.Zoom] = sz; _speed[KeyLanes.TimeOfDay] = st;
        _value[KeyLanes.Position] = vp; _value[KeyLanes.Rotation] = vr; _value[KeyLanes.Zoom] = vz; _value[KeyLanes.TimeOfDay] = vt;

        // forget selected keys that no longer exist
        _sel.RemoveWhere(s => !Keys.Any(k => k.GUID == s.guid && k.Has(s.lane)));
    }

    /// <summary>The compiled frame at time t (same rule as Timeline.FrameAt, using the cached times).</summary>
    private int FrameAt(float t)
    {
        int n = Keys.Count;
        if (n == 0 || _frameStarts.Length < n + 1 || _starts.Length < n + 1 || _frameStarts[n] == 0 || t <= 0f) return 0;
        int lo = 0, hi = n - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (_starts[mid] <= t) lo = mid; else hi = mid - 1; }
        for (int i = lo; i < n; i++)
        {
            int frames = _frameStarts[i + 1] - _frameStarts[i];
            if (frames <= 0) continue;
            float d = _starts[i + 1] - _starts[i];
            int j = d > 1e-6f ? Mathf.FloorToInt((t - _starts[i]) / d * frames + 1e-4f) : 0;
            return Mathf.Clamp(_frameStarts[i] + j, 0, _frameStarts[n] - 1);
        }
        return _frameStarts[n] - 1;
    }
    private float FrameTime => LaneEditing.FrameTime(P);
    private float SnapTime(float t) => Mathf.Round(t / FrameTime) * FrameTime;

    private int IndexOf(string guid)
    {
        for (int i = 0; i < Keys.Count; i++) if (Keys[i].GUID == guid) return i;
        return -1;
    }

    private IEnumerable<int> SelectedIndices() =>
        _sel.Select(s => IndexOf(s.guid)).Where(i => i >= 0).Distinct().OrderBy(i => i);

    private bool Selected(int idx, KeyLanes lane) => idx >= 0 && idx < Keys.Count && _sel.Contains((Keys[idx].GUID, lane));

    private void Select(int idx, KeyLanes lane, bool add = true)
    {
        if (idx < 0 || idx >= Keys.Count) return;
        if (add) _sel.Add((Keys[idx].GUID, lane)); else _sel.Remove((Keys[idx].GUID, lane));
        UIManager.Instance.Selection = idx;
    }

    private List<(KeyLanes lane, float t)> SelectionByTime() =>
        _sel.Select(s => (s.lane, i: IndexOf(s.guid))).Where(x => x.i >= 0).Select(x => (x.lane, _starts[x.i])).ToList();

    private void ReselectByTime(List<(KeyLanes lane, float t)> list)
    {
        _sel.Clear();
        float[] s = Timeline.Starts(Keys);
        float half = FrameTime * 0.6f;
        foreach (var (lane, t) in list)
            for (int i = 0; i < Keys.Count; i++)
                if (Keys[i].Has(lane) && Mathf.Abs(s[i] - t) <= half) { _sel.Add((Keys[i].GUID, lane)); break; }
    }

    // ---- undo ----

    private void PushUndo()
    {
        _undo.Add((new List<Keyframe>(Keys), P.Wiggle));
        if (_undo.Count > 80) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void Undo()
    {
        if (_undo.Count == 0) { UIManager.Instance.Status = "Nothing to undo."; return; }
        _redo.Add((new List<Keyframe>(Keys), P.Wiggle));
        Restore(_undo[^1]); _undo.RemoveAt(_undo.Count - 1);
        UIManager.Instance.Status = "Undo.";
    }

    private void Redo()
    {
        if (_redo.Count == 0) { UIManager.Instance.Status = "Nothing to redo."; return; }
        _undo.Add((new List<Keyframe>(Keys), P.Wiggle));
        Restore(_redo[^1]); _redo.RemoveAt(_redo.Count - 1);
        UIManager.Instance.Status = "Redo.";
    }

    private void Restore((List<Keyframe> keys, Wiggle wiggle) snap)
    {
        Keys.Clear();
        Keys.AddRange(snap.keys);
        P.Wiggle = snap.wiggle;
        UIManager.Instance.Selection = Mathf.Min(UIManager.Instance.Selection, Keys.Count - 1);
        Changed(orbsNow: true);
    }

    // =====================================================================
    //  Time <-> pixels
    // =====================================================================

    private float X(float t) => _tlX + (t - _viewStart) * _pps;
    private float T(float x) => _viewStart + (x - _tlX) / _pps;

    private void Fit()
    {
        float len = Mathf.Max(_length, 1f);
        _pps = Mathf.Clamp((_tlW - 40f) / len, 4f, 4000f);
        _viewStart = -20f / _pps;
    }

    private void ZoomAt(float x, float factor)
    {
        float t = T(x);
        _pps = Mathf.Clamp(_pps * factor, 4f, 6000f);
        _viewStart = t - (x - _tlX) / _pps;
    }

    private void HandleWheel()
    {
        Event e = Event.current;
        if (e.type != EventType.ScrollWheel) return;
        Vector2 m = e.mousePosition;
        if (m.x < _tlX || m.y < _ruler.y || m.y > _status.y) return;
        if (e.shift) _viewStart += e.delta.y * 18f / _pps;
        else ZoomAt(m.x, e.delta.y > 0 ? 1f / 1.15f : 1.15f);
        e.Use();
    }

    private static string Timecode(float t, int fps)
    {
        if (t < 0) t = 0;
        int totalFrames = Mathf.RoundToInt(t * fps);
        int f = totalFrames % fps, s = totalFrames / fps;
        return $"{s / 60}:{s % 60:00}:{f:00}";
    }

    // =====================================================================
    //  Toolbar
    // =====================================================================

    private void DrawToolbar()
    {
        Rect r = _toolbar;
        float x = r.x;
        bool Btn(string label, float w, string tip, bool active = false, GUIStyle style = null)
        {
            Rect b = new Rect(x, r.y, w, r.height);
            x += w + 4;
            if (active) Theme.Fill(b, Theme.Accent.WithAlpha(0.25f), 6);
            return GUI.Button(b, new GUIContent(label, tip), style ?? GUI.skin.button);
        }

        if (Btn(_playing ? "❚❚" : "▶", 40, "Play / pause (Space)", _playing, Theme.AccentButton)) TogglePlay();
        if (Btn("⏮", 32, "Go to start (Home)")) SetTime(0f, true);
        if (Btn("◀", 28, "Previous frame (Page Up)")) SetTime(_time - FrameTime, true);
        if (Btn("▶", 28, "Next frame (Page Down)")) SetTime(_time + FrameTime, true);
        if (Btn("⏭", 32, "Go to end (End)")) SetTime(_length, true);

        // timecode (drag to scrub, click to type)
        Rect tc = new Rect(x + 4, r.y, 108, r.height);
        x += 116;
        ValueField("ake.timecode", tc, _time, v => Timecode(v, P.FPS), 1f / 60f, v => SetTime(Mathf.Max(0, v), true), ParseTimecode, _mono, PlayheadColor);
        GUI.Label(new Rect(x - 6, r.y + 6, 120, 18), $"/ {Timecode(_length, P.FPS)}  {P.FPS} fps", _tiny);
        x += 112;

        x += 6;
        if (Btn("◆ Key all", 82, "Key every property at the playhead from the MonkeFrames camera (fly there first)")) KeyAllFromCamera();
        if (Btn("Easy Ease", 78, "F9: ease both sides of the selected keys. Shift+F9 ease in, Ctrl+Shift+F9 ease out")) EaseSelected(true, true);
        if (Btn(_graph ? "Graph ✓" : "Graph", 66, "Graph Editor (Shift+F3)", _graph)) _graph = !_graph;
        if (_graph)
        {
            int m = Widgets.Segmented("ake.gmode", new Rect(x, r.y, 128, r.height), _graphMode, new[] { "Speed", "Value" });
            if (m != _graphMode) { _graphMode = m; }
            x += 134;
        }
        if (Btn("Snap", 50, "Snap keys to the playhead and other keys", _snap)) _snap = !_snap;
        if (Btn("Path", 50, "Show the camera's motion path in the world (one dot per frame: far apart = fast)", _showPath)) _showPath = !_showPath;
        if (Btn("Fit", 40, "Zoom the timeline to the whole animation")) Fit();
        if (Btn("Rove", 50, "Roving keys: retime the selected keyframes so the camera moves at an even speed between the first and last")) Rove();

        float rx = r.xMax;
        Rect redo = new Rect(rx - 34, r.y, 34, r.height), undo = new Rect(rx - 72, r.y, 34, r.height);
        if (GUI.Button(undo, new GUIContent("↶", "Undo (Ctrl+Z)"))) Undo();
        if (GUI.Button(redo, new GUIContent("↷", "Redo (Ctrl+Shift+Z)"))) Redo();
    }

    private static bool ParseTimecode(string s, out float v)
    {
        v = 0f;
        s = s.Trim();
        string[] p = s.Split(':');
        int fps = P.FPS;
        try
        {
            if (p.Length == 3) { v = int.Parse(p[0]) * 60f + int.Parse(p[1]) + int.Parse(p[2]) / (float)fps; return true; }
            if (p.Length == 2) { v = int.Parse(p[0]) * 60f + float.Parse(p[1], CultureInfo.InvariantCulture); return true; }
            if (s.EndsWith("f") && int.TryParse(s.TrimEnd('f'), out int fr)) { v = fr / (float)fps; return true; }
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
        catch { return false; }
    }

    // =====================================================================
    //  Left panel: properties
    // =====================================================================

    private float LaneY(int laneIndex) => _rows.y + SummaryH + laneIndex * RowH;

    private void DrawLeft()
    {
        // header (beside the ruler)
        Rect head = new Rect(_left.x, _ruler.y, _left.width, RulerH);
        Theme.Fill(head, Theme.Raised, 6);
        Theme.DrawText(new Rect(head.x + 10, head.y, 200, head.height), "Camera", Theme.Header, Theme.Text);
        Theme.DrawText(new Rect(head.x, head.y, head.width - 10, head.height), $"{Keys.Count} keyframes · {_length:0.00}s", _tinyRight, Theme.TextMuted);

        // summary row
        Rect sum = new Rect(_left.x, _rows.y, _left.width, SummaryH);
        Draw2D.Rect(sum, RowB);
        Theme.DrawText(new Rect(sum.x + 10, sum.y, 200, sum.height), "◆  All keyframes", _valStyle, Theme.TextMuted);

        for (int li = 0; li < LaneEditing.Lanes.Length; li++)
        {
            KeyLanes lane = LaneEditing.Lanes[li];
            Rect row = new Rect(_left.x, LaneY(li), _left.width, RowH);
            Draw2D.Rect(row, li % 2 == 0 ? RowA : RowB);
            if (_graph && (_graphLanes & lane) != 0) Draw2D.Rect(new Rect(row.x, row.y, 3, row.height), LaneColor(lane));
            DrawLaneHeader(lane, row);
        }

        if (Player.LoadedClip != null)
        {
            Rect ar = new Rect(_left.x, LaneY(LaneEditing.Lanes.Length), _left.width, AudioH);
            Draw2D.Rect(ar, RowA);
            Theme.DrawText(new Rect(ar.x + 10, ar.y, ar.width - 20, ar.height), "♪  " + Player.LoadedClip.name, _valStyle, Theme.TextMuted);
        }
    }

    private void DrawLaneHeader(KeyLanes lane, Rect row)
    {
        float cy = row.y + row.height / 2f;
        int keyCount = LaneEditing.CountKeys(P, lane);
        bool animated = keyCount >= 2;

        // stopwatch
        Rect sw = new Rect(row.x + 6, row.y + 5, 20, 20);
        Theme.Dot(sw.center, 8f, animated ? Theme.Accent.WithAlpha(0.9f) : Theme.Raised);
        Theme.DrawText(sw, "⏱", Theme.LabelCenterSmall, animated ? Theme.OnAccent : Theme.TextMuted);
        if (GUI.Button(sw, new GUIContent("", animated ? "Stopwatch: remove this property's animation (keep the value at the playhead)" : "Stopwatch: start animating this property (adds a key at the playhead)"), GUIStyle.none))
            ToggleStopwatch(lane, animated);

        // name (click in the Graph Editor to show / hide this property's curve)
        Rect name = new Rect(row.x + 30, row.y, 74, row.height);
        Color nc = _graph && (_graphLanes & lane) != 0 ? LaneColor(lane) : Theme.Text;
        Theme.DrawText(name, LaneEditing.Name(lane), _valStyle, nc);
        if (GUI.Button(name, new GUIContent("", "Click: show this property in the Graph Editor"), GUIStyle.none))
        {
            if (!_graph) { _graph = true; _graphLanes = lane; }
            else if (Event.current.shift) _graphLanes ^= lane;
            else _graphLanes = lane;
            if (_graphLanes == KeyLanes.None) _graphLanes = lane;
        }

        // values at the playhead
        float vx = row.x + 104;
        int keyHere = LaneEditing.KeyAt(P, lane, _time);
        Keyframe now = keyHere >= 0 ? Keys[keyHere] : LaneEditing.Sample(P, _time);
        Color vc = keyHere >= 0 ? new Color(1f, 0.85f, 0.35f) : new Color(0.45f, 0.72f, 1f);
        switch (lane)
        {
            case KeyLanes.Position:
                for (int c = 0; c < 3; c++)
                {
                    int cc = c;
                    ValueField($"ake.pos{c}", new Rect(vx + c * 58, row.y + 4, 56, 22), now.Position[c], v => v.ToString("0.00", CultureInfo.InvariantCulture), 0.01f,
                        v => EditLane(lane, k => { Vector3 p = k.Position; p[cc] = v; k.Position = p; return k; }), null, _valStyle, vc);
                }
                break;
            case KeyLanes.Rotation:
                for (int c = 0; c < 3; c++)
                {
                    int cc = c;
                    float shown = Mathf.DeltaAngle(0, now.Rotation[c]);
                    ValueField($"ake.rot{c}", new Rect(vx + c * 58, row.y + 4, 56, 22), shown, v => v.ToString("0.0", CultureInfo.InvariantCulture) + "°", 0.25f,
                        v => EditLane(lane, k => { Vector3 r = k.Rotation; r[cc] = v; k.Rotation = r; return k; }), null, _valStyle, vc);
                }
                break;
            case KeyLanes.Zoom:
                ValueField("ake.fov", new Rect(vx, row.y + 4, 70, 22), now.FieldOfView, v => v.ToString("0.0", CultureInfo.InvariantCulture) + "°", 0.1f,
                    v => EditLane(lane, k => { k.FieldOfView = Mathf.Clamp(v, 1f, 170f); return k; }), null, _valStyle, vc);
                break;
            case KeyLanes.TimeOfDay:
                ValueField("ake.tod", new Rect(vx, row.y + 4, 70, 22), now.TimeOfDay, v => v < 0 ? "—" : $"{(int)v:00}:{(int)(Mathf.Repeat(v, 1f) * 60f):00}", 0.02f,
                    v => EditLane(lane, k => { k.TimeOfDay = Mathf.Repeat(v < 0 ? 12f : v, 24f); return k; }), ParseHours, _valStyle, vc);
                break;
        }

        // keyframe navigator: previous key, add / remove key, next key
        float nx = row.xMax - 92;
        int prev = -1, next = -1;
        for (int i = Keys.Count - 1; i >= 0; i--) if (Keys[i].Has(lane) && _starts[i] < _time - FrameTime * 0.5f) { prev = i; break; }
        for (int i = 0; i < Keys.Count; i++) if (Keys[i].Has(lane) && _starts[i] > _time + FrameTime * 0.5f) { next = i; break; }
        GUI.enabled = prev >= 0;
        if (GUI.Button(new Rect(nx, row.y + 5, 20, 20), new GUIContent("◀", "Previous keyframe"), Theme.LabelCenterSmall)) SetTime(_starts[prev], true);
        GUI.enabled = true;
        Rect kb = new Rect(nx + 21, row.y + 5, 20, 20);
        Draw2D.Key(kb.center, 11, KeyShape.Diamond, KeyShape.Diamond, keyHere >= 0 ? new Color(1f, 0.85f, 0.35f) : new Color(1, 1, 1, 0.15f), new Color(0, 0, 0, 0.6f));
        if (GUI.Button(kb, new GUIContent("", keyHere >= 0 ? "Remove the key at the playhead" : "Add a key at the playhead"), GUIStyle.none))
            ToggleKeyHere(lane, keyHere);
        GUI.enabled = next >= 0;
        if (GUI.Button(new Rect(nx + 42, row.y + 5, 20, 20), new GUIContent("▶", "Next keyframe"), Theme.LabelCenterSmall)) SetTime(_starts[next], true);
        GUI.enabled = true;

        // key this property from the MonkeFrames camera
        Rect cam = new Rect(nx + 66, row.y + 5, 22, 20);
        if (lane != KeyLanes.TimeOfDay && GUI.Button(cam, new GUIContent("⌖", $"Key {LaneEditing.Name(lane)} at the playhead from where the MonkeFrames camera is now"), Theme.LabelCenterSmall))
            KeyFromCamera(lane);
    }

    private static bool ParseHours(string s, out float v)
    {
        v = 0f;
        s = s.Trim();
        if (s.Contains(":"))
        {
            string[] p = s.Split(':');
            if (p.Length == 2 && int.TryParse(p[0], out int hh) && int.TryParse(p[1], out int mm)) { v = hh + mm / 60f; return true; }
            return false;
        }
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    // ---- value fields: drag left / right to change (shift = x10, ctrl = x0.1), click to type ----

    private void ValueField(string id, Rect r, float value, Func<float, string> format, float step, Action<float> set,
        TryParse parse, GUIStyle style, Color color)
    {
        Event e = Event.current;
        if (_typingId == id)
        {
            string ctrl = "ake.type." + id;
            GUI.SetNextControlName(ctrl);
            _typingText = GUI.TextField(r, _typingText);
            if (GUI.GetNameOfFocusedControl() != ctrl && e.type == EventType.Repaint && _typingFocused) { CommitTyping(set, parse); return; }
            if (GUI.GetNameOfFocusedControl() != ctrl) { GUI.FocusControl(ctrl); }
            else _typingFocused = true;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)) { CommitTyping(set, parse); e.Use(); }
            else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { _typingId = null; GUI.FocusControl(null); e.Use(); }
            return;
        }

        bool hover = r.Contains(e.mousePosition);
        string text = format(value);
        Theme.DrawText(r, text, style, color);
        if (hover && _drag == DragKind.None)
            Draw2D.Dashed(new Vector2(r.x, r.yMax - 3), new Vector2(r.x + Mathf.Min(r.width, style.CalcSize(new GUIContent(text)).x), r.yMax - 3), color.WithAlpha(0.6f));

        int id2 = GUIUtility.GetControlID(id.GetHashCode(), FocusType.Passive, r);
        switch (e.GetTypeForControl(id2))
        {
            case EventType.MouseDown when hover && e.button == 0:
                GUIUtility.hotControl = id2;
                _scrubId = id; _scrubStartValue = value; _scrubBegan = false; _dragStart = e.mousePosition;
                e.Use();
                break;
            case EventType.MouseDrag when GUIUtility.hotControl == id2 && _scrubId == id:
                float dx = e.mousePosition.x - _dragStart.x;
                if (!_scrubBegan && Mathf.Abs(dx) > 2f) { _scrubBegan = true; if (id != "ake.timecode") PushUndo(); }
                if (_scrubBegan)
                {
                    float mul = e.shift ? 10f : e.control ? 0.1f : 1f;
                    set(_scrubStartValue < 0 && id == "ake.tod" ? 12f + dx * step * mul : _scrubStartValue + dx * step * mul);
                }
                e.Use();
                break;
            case EventType.MouseUp when GUIUtility.hotControl == id2:
                GUIUtility.hotControl = 0;
                if (!_scrubBegan) { _typingId = id; _typingText = text.TrimEnd('°'); _typingFocused = false; }
                else if (_orbsDirty) { KeyframeManager.Instance.RefreshOrbs(); _orbsDirty = false; }
                _scrubId = null;
                e.Use();
                break;
        }
    }

    private delegate bool TryParse(string s, out float v);
    private bool _typingFocused;

    private void CommitTyping(Action<float> set, TryParse parse)
    {
        string s = _typingText ?? "";
        _typingId = null;
        GUI.FocusControl(null);
        bool ok = parse != null ? parse(s, out float v) : float.TryParse(s.Replace("°", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        if (!ok) { UIManager.Instance.Status = $"\"{s}\" isn't a number."; return; }
        PushUndo();
        set(v);
        if (_orbsDirty) { KeyframeManager.Instance.RefreshOrbs(); _orbsDirty = false; }
    }

    // =====================================================================
    //  Ruler, rows, playhead
    // =====================================================================

    private void DrawRuler()
    {
        Rect r = _ruler;
        Theme.Fill(r, Theme.Raised, 6);
        GUI.BeginClip(new Rect(r.x, r.y, r.width, _status.y - r.y));
        float ox = r.x;
        float step = TickStep(70f);
        float minor = step / 5f;
        float t0 = Mathf.Floor(T(r.x) / minor) * minor;
        for (float t = t0; X(t) < r.xMax; t += minor)
        {
            if (t < -1e-4f) continue;
            float x = X(t) - ox;
            bool major = Mathf.Abs(t / step - Mathf.Round(t / step)) < 1e-3f;
            Draw2D.Rect(new Rect(x, r.height - (major ? 10 : 5), 1, major ? 10 : 5), Theme.TextMuted.WithAlpha(major ? 0.7f : 0.35f));
            if (major) Theme.DrawText(new Rect(x + 3, 1, 80, 14), Label(t, step), _tiny, Theme.TextMuted);
        }
        // the end of the animation
        float ex = X(_length) - ox;
        Draw2D.Rect(new Rect(ex, 0, 2, r.height), Theme.Accent.WithAlpha(0.6f));
        GUI.EndClip();

        // playhead handle
        float px = X(_time);
        if (px >= r.x - 6 && px <= r.xMax + 6)
        {
            Rect hd = new Rect(px - 7, r.y + 2, 14, 12);
            Theme.Fill(hd, PlayheadColor, 3);
        }

        Event e = Event.current;
        int id = GUIUtility.GetControlID(FocusType.Passive);
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown when r.Contains(e.mousePosition) && e.button == 0:
                GUIUtility.hotControl = id;
                _drag = DragKind.Scrub;
                SetTime(T(e.mousePosition.x), true, snap: !e.shift);
                e.Use();
                break;
            case EventType.MouseDrag when GUIUtility.hotControl == id && _drag == DragKind.Scrub:
                SetTime(T(e.mousePosition.x), true, snap: !e.shift);
                e.Use();
                break;
            case EventType.MouseUp when GUIUtility.hotControl == id:
                GUIUtility.hotControl = 0;
                _drag = DragKind.None;
                e.Use();
                break;
        }
    }

    private string Label(float t, float step)
    {
        if (step < 1f - 1e-4f)
        {
            int fps = P.FPS;
            int s = Mathf.FloorToInt(t + 1e-4f);
            int f = Mathf.RoundToInt((t - s) * fps);
            return f == 0 ? $"{s}s" : $"{f}f";
        }
        return t >= 60f ? $"{(int)(t / 60)}:{(int)t % 60:00}" : $"{t:0}s";
    }

    private float TickStep(float minPx)
    {
        float fps = P.FPS;
        float[] steps = { 1f / fps, 2f / fps, 5f / fps, 10f / fps, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 15f, 30f, 60f, 120f, 300f };
        foreach (float s in steps) if (s * _pps >= minPx) return s;
        return 600f;
    }

    private void DrawPlayheadLine()
    {
        float px = X(_time);
        if (px < _tlX || px > _tlX + _tlW) return;
        float bottom = _status.y - 4;
        Draw2D.Rect(new Rect(px - 0.5f, _ruler.y + 12, 1.5f, bottom - _ruler.y - 12), PlayheadColor.WithAlpha(0.9f));
    }

    private void DrawRows()
    {
        Event e = Event.current;
        GUI.BeginClip(new Rect(_rows.x, _rows.y, _rows.width, _rows.height));
        Vector2 mouse = e.mousePosition + new Vector2(_rows.x, _rows.y);
        float ox = _rows.x, oy = _rows.y;

        // backgrounds and grid
        Draw2D.Rect(new Rect(0, 0, _rows.width, SummaryH), RowB);
        for (int li = 0; li < LaneEditing.Lanes.Length; li++)
            Draw2D.Rect(new Rect(0, LaneY(li) - oy, _rows.width, RowH), li % 2 == 0 ? RowA : RowB);
        float step = TickStep(70f);
        for (float t = Mathf.Max(0, Mathf.Floor(T(_rows.x) / step) * step); X(t) < _rows.xMax; t += step)
            Draw2D.Rect(new Rect(X(t) - ox, 0, 1, _rows.height), new Color(1, 1, 1, 0.035f));
        // after the end
        float endX = X(_length) - ox;
        if (endX < _rows.width) Draw2D.Rect(new Rect(endX, 0, _rows.width - endX, _rows.height), new Color(0, 0, 0, 0.25f));

        // audio waveform
        if (Player.LoadedClip != null) DrawWaveform(new Rect(0, LaneY(LaneEditing.Lanes.Length) - oy, _rows.width, AudioH), ox);

        // speed strip on the summary row (how fast the camera moves: brighter = faster)
        if (_frames.Count > 1 && _speed.TryGetValue(KeyLanes.Position, out float[] spd))
        {
            float max = 0.001f;
            foreach (float s in spd) max = Mathf.Max(max, s);
            for (float x = Mathf.Max(0, X(0) - ox); x < Mathf.Min(_rows.width, endX); x += 3)
            {
                int f = FrameAt(T(x + ox));
                float k = f < spd.Length ? spd[f] / max : 0f;
                Draw2D.Rect(new Rect(x, SummaryH - 4, 3, 3), LaneColor(KeyLanes.Position).WithAlpha(0.15f + 0.6f * k));
            }
        }

        // keys
        var hits = new List<(Vector2 p, int idx, KeyLanes lane)>();
        for (int i = 0; i < Keys.Count; i++)
        {
            float x = X(_starts[i]) - ox;
            if (x < -10 || x > _rows.width + 10) continue;
            Keyframe k = Keys[i];
            // summary key: one per MonkeFrames keyframe
            bool allSel = LaneEditing.Lanes.Where(l => k.Has(l)).All(l => Selected(i, l)) && k.Keys != KeyLanes.None;
            Vector2 sp = new Vector2(x, SummaryH / 2f);
            Draw2D.Key(sp, 10, KeyShape.Diamond, KeyShape.Diamond, allSel ? Theme.Accent : KeyGrey.WithAlpha(k.Keys == KeyLanes.All ? 0.9f : 0.55f), new Color(0, 0, 0, 0.7f));
            hits.Add((sp + new Vector2(ox, oy), i, KeyLanes.All));

            for (int li = 0; li < LaneEditing.Lanes.Length; li++)
            {
                KeyLanes lane = LaneEditing.Lanes[li];
                if (!k.Has(lane)) continue;
                Vector2 c = new Vector2(x, LaneY(li) - oy + RowH / 2f);
                int prev = LaneEditing.PrevKey(P, lane, i);
                int next = LaneEditing.NextKey(P, lane, i);
                KeyShape left = prev >= 0 ? LaneEditing.InShape(LaneEditing.Out(P, lane, prev)) : KeyShape.Diamond;
                KeyShape right = next >= 0 ? LaneEditing.OutShape(LaneEditing.Out(P, lane, i)) : left;
                if (prev < 0) left = right;
                bool sel = Selected(i, lane);
                Color fill = sel ? new Color(1f, 0.85f, 0.3f) : KeyGrey;
                Draw2D.Key(c, 14, left, right, fill, new Color(0, 0, 0, 0.75f));
                hits.Add((c + new Vector2(ox, oy), i, lane));
            }
        }

        // lines between a property's keys (where it's animating)
        for (int li = 0; li < LaneEditing.Lanes.Length; li++)
        {
            KeyLanes lane = LaneEditing.Lanes[li];
            float y = LaneY(li) - oy + RowH / 2f;
            for (int i = 0; i < Keys.Count; i++)
            {
                if (!Keys[i].Has(lane)) continue;
                int n = LaneEditing.NextKey(P, lane, i);
                if (n < 0) break;
                LaneCurve c = LaneEditing.Out(P, lane, i);
                float x0 = X(_starts[i]) - ox + 9, x1 = X(_starts[n]) - ox - 9;
                if (x1 > x0) Draw2D.Rect(new Rect(x0, y - 0.5f, x1 - x0, 1), c.Interp == LaneInterp.Hold ? new Color(1, 1, 1, 0.08f) : LaneColor(lane).WithAlpha(0.35f));
            }
        }

        // marquee
        if (_drag == DragKind.Marquee && _dragMoved)
        {
            Rect m = MarqueeRect(mouse);
            m.position -= new Vector2(ox, oy);
            Draw2D.Rect(m, Theme.Accent.WithAlpha(0.12f));
            Draw2D.Frame(m, Theme.Accent.WithAlpha(0.7f));
        }
        GUI.EndClip();

        // hover info
        _hoverInfo = null;
        foreach (var h in hits)
            if ((h.p - e.mousePosition).sqrMagnitude < 64f)
            {
                _hoverInfo = h.lane == KeyLanes.All ? $"Keyframe {h.idx + 1} at {Timecode(_starts[h.idx], P.FPS)}  (drag to retime, double-click to go there)" : KeyInfo(h.idx, h.lane);
                break;
            }

        RowsInput(hits);
    }

    private string KeyInfo(int idx, KeyLanes lane)
    {
        int prev = LaneEditing.PrevKey(P, lane, idx), next = LaneEditing.NextKey(P, lane, idx);
        string inS = prev >= 0 ? LaneEditing.Describe(LaneEditing.Out(P, lane, prev), false) : "—";
        string outS = next >= 0 ? LaneEditing.Describe(LaneEditing.Out(P, lane, idx), true) : "—";
        return $"{LaneEditing.Name(lane)} key at {Timecode(_starts[idx], P.FPS)}   in: {inS}   out: {outS}";
    }

    private Rect MarqueeRect(Vector2 mouse) =>
        Rect.MinMaxRect(Mathf.Min(_dragStart.x, mouse.x), Mathf.Min(_dragStart.y, mouse.y), Mathf.Max(_dragStart.x, mouse.x), Mathf.Max(_dragStart.y, mouse.y));

    private void RowsInput(List<(Vector2 p, int idx, KeyLanes lane)> hits)
    {
        Event e = Event.current;
        int id = GUIUtility.GetControlID(FocusType.Passive);
        Vector2 m = e.mousePosition;
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown when _rows.Contains(m) && (e.button == 1 || e.button == 2):
                GUIUtility.hotControl = id; _drag = DragKind.Pan; _dragStart = m; _panStartView = _viewStart; e.Use();
                break;
            case EventType.MouseDown when _rows.Contains(m) && e.button == 0:
            {
                GUIUtility.hotControl = id;
                _dragStart = m; _dragMoved = false;
                var hit = hits.Where(h => (h.p - m).sqrMagnitude < 81f).OrderBy(h => (h.p - m).sqrMagnitude).Cast<(Vector2 p, int idx, KeyLanes lane)?>().FirstOrDefault();
                if (hit.HasValue)
                {
                    var (_, idx, lane) = hit.Value;
                    if (e.clickCount == 2) { SetTime(_starts[idx], true); KeyframeManager.Instance.GoToKeyframe(idx); }
                    if (e.control && lane != KeyLanes.All) { ToggleAuto(idx, lane); _drag = DragKind.None; GUIUtility.hotControl = 0; e.Use(); break; }
                    var lanes = lane == KeyLanes.All ? LaneEditing.Lanes.Where(l => Keys[idx].Has(l)).ToArray() : new[] { lane };
                    bool already = lanes.All(l => Selected(idx, l));
                    if (e.shift) { foreach (var l in lanes) Select(idx, l, !already); }
                    else if (!already) { _sel.Clear(); foreach (var l in lanes) Select(idx, l); }
                    UIManager.Instance.Selection = idx;
                    _drag = DragKind.Keys;
                    _dragKeyTime = _starts[idx];
                }
                else
                {
                    if (!e.shift) _sel.Clear();
                    _drag = DragKind.Marquee;
                }
                e.Use();
                break;
            }
            case EventType.MouseDrag when GUIUtility.hotControl == id:
                if (_drag == DragKind.Pan) { _viewStart = _panStartView - (m.x - _dragStart.x) / _pps; e.Use(); break; }
                if (!_dragMoved && (m - _dragStart).sqrMagnitude > 9f)
                {
                    _dragMoved = true;
                    if (_drag == DragKind.Keys) BeginKeyDrag();
                }
                if (_dragMoved && _drag == DragKind.Keys) KeyDrag(m.x);
                e.Use();
                break;
            case EventType.MouseUp when GUIUtility.hotControl == id:
                GUIUtility.hotControl = 0;
                if (_drag == DragKind.Marquee && _dragMoved)
                {
                    Rect r = MarqueeRect(m);
                    foreach (var h in hits)
                        if (r.Contains(h.p))
                        {
                            if (h.lane == KeyLanes.All) { foreach (var l in LaneEditing.Lanes) if (Keys[h.idx].Has(l)) Select(h.idx, l); }
                            else Select(h.idx, h.lane);
                        }
                }
                else if (_drag == DragKind.Marquee && !_dragMoved && !e.shift)
                {
                    SetTime(T(m.x), true, snap: true);   // click on empty space: move the playhead there
                }
                if (_drag == DragKind.Keys && _dragMoved) EndKeyDrag();
                _drag = DragKind.None;
                e.Use();
                break;
        }
    }

    // ---- dragging keys in time ----

    private void BeginKeyDrag()
    {
        PushUndo();
        // A key dragged on its own leaves its keyframe: split it off so only the dragged properties move.
        var byIdx = _sel.Select(s => (i: IndexOf(s.guid), s.lane)).Where(x => x.i >= 0).GroupBy(x => x.i).OrderByDescending(g => g.Key).ToList();
        foreach (var g in byIdx)
        {
            int i = g.Key;
            KeyLanes selLanes = KeyLanes.None;
            foreach (var x in g) selLanes |= x.lane;
            Keyframe k = Keys[i];
            if ((k.Keys & ~selLanes) == KeyLanes.None) continue;   // the whole keyframe moves
            Keyframe part = LaneEditing.Clone(k);
            part.Keys = selLanes;
            part.Transition.Duration = k.Transition.Duration;
            k.Keys &= ~selLanes;
            k.Transition.Duration = 0f;
            Keys[i] = k;
            Keys.Insert(i + 1, part);
            foreach (var x in g) { _sel.Remove((k.GUID, x.lane)); _sel.Add((part.GUID, x.lane)); }
        }
        _dragStarts = Timeline.Starts(Keys);
        _dragMoving = new HashSet<int>(SelectedIndices());
        _dragMoving.Remove(0);
        if (_dragMoving.Count == 0) UIManager.Instance.Status = "The first keyframe always starts the animation at 0:00.";
    }

    private void KeyDrag(float mouseX)
    {
        if (_dragMoving == null || _dragMoving.Count == 0) return;
        float dt = (mouseX - _dragStart.x) / _pps;
        float target = _dragKeyTime + dt;
        if (_snap && !Event.current.shift)
        {
            float best = 8f / _pps;
            float snapTo = float.NaN;
            void Try(float t) { if (Mathf.Abs(t - target) < best) { best = Mathf.Abs(t - target); snapTo = t; } }
            Try(_time);
            for (int i = 0; i < _dragStarts.Length - 1; i++) if (!_dragMoving.Contains(i)) Try(_dragStarts[i]);
            if (!float.IsNaN(snapTo)) dt = snapTo - _dragKeyTime;
        }
        float applied = LaneEditing.Shift(P, _dragMoving, _dragStarts, dt);
        _hoverInfo = $"Δ {applied:+0.000;-0.000}s  → {Timecode(_dragKeyTime + applied, P.FPS)}";
        Refresh(force: true);
        _orbsDirty = true;
    }

    private void EndKeyDrag()
    {
        var keep = SelectionByTime();
        LaneEditing.Tidy(P);
        Refresh(force: true);
        ReselectByTime(keep);
        KeyframeManager.Instance.RefreshOrbs();
        _orbsDirty = false;
        _dragMoving = null;
    }

    // =====================================================================
    //  Graph Editor
    // =====================================================================

    private float GY(float v) => _graphRect.yMax - 10 - (v - _gMin) / Mathf.Max(1e-6f, _gMax - _gMin) * (_graphRect.height - 30);
    private float GV(float y) => _gMin + (_graphRect.yMax - 10 - y) / Mathf.Max(1e-6f, _graphRect.height - 30) * (_gMax - _gMin);

    private IEnumerable<KeyLanes> GraphLanes() => LaneEditing.Lanes.Where(l => (_graphLanes & l) != 0);

    private float SegmentDistance(KeyLanes lane, int a, int b)
    {
        float[] s = _speed[lane];
        int f0 = _frameStarts[a], f1 = Mathf.Min(_frameStarts[b], s.Length);
        float d = 0;
        for (int f = f0; f < f1; f++) d += s[f];
        return d / Mathf.Max(1, P.FPS);
    }

    private static int Components(KeyLanes lane) => lane == KeyLanes.Position || lane == KeyLanes.Rotation ? 3 : 1;

    private float LaneValue(KeyLanes lane, int idx, int comp)
    {
        Vector3[] v = _value[lane];
        if (v.Length == 0) return 0;
        return v[Mathf.Clamp(_frameStarts[idx], 0, v.Length - 1)][comp];
    }

    private void DrawGraph()
    {
        Rect g = _graphRect;
        Theme.Fill(g, Theme.Field, 6);
        Event e = Event.current;
        int n = _frames.Count;
        string unit(KeyLanes l) => _graphMode == 0
            ? l switch { KeyLanes.Position => "m/s", KeyLanes.TimeOfDay => "h/s", _ => "°/s" }
            : l switch { KeyLanes.Position => "m", KeyLanes.TimeOfDay => "h", _ => "°" };

        if (n < 2)
        {
            Theme.DrawText(g, "Add at least two keyframes to see the graph.", Theme.MutedCenter, Theme.TextMuted);
            return;
        }

        // vertical range: fit what's visible (frozen while dragging a handle)
        float lo = float.MaxValue, hi = float.MinValue;
        int f0 = Mathf.Clamp(FrameAt(Mathf.Max(0, T(g.x))), 0, n - 1), f1 = Mathf.Clamp(FrameAt(T(g.xMax)), 0, n - 1);
        foreach (KeyLanes lane in GraphLanes())
        {
            if (_graphMode == 0) { float[] s = _speed[lane]; for (int f = f0; f <= f1; f++) { lo = Mathf.Min(lo, s[f]); hi = Mathf.Max(hi, s[f]); } lo = 0; }
            else
            {
                Vector3[] v = _value[lane];
                if (lane == KeyLanes.TimeOfDay && Keys.All(k => k.TimeOfDay < 0)) continue;
                for (int f = f0; f <= f1; f++) for (int c = 0; c < Components(lane); c++) { lo = Mathf.Min(lo, v[f][c]); hi = Mathf.Max(hi, v[f][c]); }
            }
        }
        if (lo > hi) { lo = 0; hi = 1; }
        float pad = Mathf.Max((hi - lo) * 0.12f, _graphMode == 0 ? 0.05f : 0.5f);
        if (_drag != DragKind.Handle && _drag != DragKind.GraphKey)
        {
            _gMin = Anim.To("ake.gmin", _graphMode == 0 ? 0f : lo - pad, 10f);
            _gMax = Anim.To("ake.gmax", hi + pad, 10f);
        }

        // grid with value labels
        GUI.BeginClip(g);
        float span = _gMax - _gMin;
        float gstep = NiceStep(span / 5f);
        for (float v = Mathf.Ceil(_gMin / gstep) * gstep; v <= _gMax; v += gstep)
        {
            float y = GY(v) - g.y;
            Draw2D.Rect(new Rect(0, y, g.width, 1), new Color(1, 1, 1, Mathf.Abs(v) < gstep * 0.01f ? 0.15f : 0.05f));
            Theme.DrawText(new Rect(4, y - 14, 120, 14), v.ToString(gstep < 0.1f ? "0.00" : gstep < 1 ? "0.0" : "0", CultureInfo.InvariantCulture), _tiny, Theme.TextMuted.WithAlpha(0.7f));
        }
        float endX = X(_length) - g.x;
        if (endX < g.width) Draw2D.Rect(new Rect(endX, 0, g.width - endX, g.height), new Color(0, 0, 0, 0.25f));

        // curves
        var pts = new List<Vector2>();
        float legendX = g.width - 10;
        foreach (KeyLanes lane in GraphLanes().Reverse())
        {
            Color lc = LaneColor(lane);
            int comps = _graphMode == 0 ? 1 : Components(lane);
            for (int c = 0; c < comps; c++)
            {
                Color cc = comps == 1 ? lc : c == 0 ? Theme.AxisX : c == 1 ? Theme.AxisY : Theme.AxisZ;
                pts.Clear();
                for (float x = Mathf.Max(0, X(0) - g.x); x <= Mathf.Min(g.width, endX); x += 2f)
                {
                    int f = Mathf.Clamp(FrameAt(T(x + g.x)), 0, n - 1);
                    float v = _graphMode == 0 ? _speed[lane][f] : _value[lane][f][c];
                    if (lane == KeyLanes.TimeOfDay && _frames[f].TimeOfDay < 0) v = float.NaN;
                    pts.Add(new Vector2(x, float.IsNaN(v) ? float.NaN : GY(v) - g.y));
                }
                Draw2D.Polyline(pts, cc, 1.6f);
            }
            string label = $"{LaneEditing.Name(lane)} ({unit(lane)})";
            float lw = _tiny.CalcSize(new GUIContent(label)).x;
            legendX -= lw + 18;
            Theme.Dot(new Vector2(legendX + 4, 10), 4, lc);
            Theme.DrawText(new Rect(legendX + 11, 3, lw + 4, 14), label, _tiny, Theme.Text);
        }

        // keys and handles
        var keyHits = new List<(Vector2 p, int idx, KeyLanes lane, int comp)>();
        var handleHits = new List<(Vector2 p, KeyLanes lane, int key, bool outgoing, int comp)>();
        foreach (KeyLanes lane in GraphLanes())
        {
            int comps = _graphMode == 0 ? 1 : Components(lane);
            for (int i = 0; i < Keys.Count; i++)
            {
                if (!Keys[i].Has(lane)) continue;
                float x = X(_starts[i]) - g.x;
                if (x < -40 || x > g.width + 40) continue;
                int next = LaneEditing.NextKey(P, lane, i), prev = LaneEditing.PrevKey(P, lane, i);
                bool sel = Selected(i, lane);
                for (int c = 0; c < comps; c++)
                {
                    float kv = _graphMode == 0 ? _speed[lane][Mathf.Clamp(_frameStarts[i], 0, n - 1)] : LaneValue(lane, i, c);
                    if (_graphMode == 1 && lane == KeyLanes.TimeOfDay && Keys[i].TimeOfDay < 0) continue;
                    Vector2 kp = new Vector2(x, GY(kv) - g.y);
                    if (sel)
                    {
                        if (next >= 0) DrawHandle(lane, i, next, true, c, kp, g, handleHits);
                        if (prev >= 0) DrawHandle(lane, prev, i, false, c, kp, g, handleHits);
                    }
                    Draw2D.Box(kp, sel ? 8 : 6, sel ? new Color(1f, 0.85f, 0.3f) : Color.white, new Color(0, 0, 0, 0.8f));
                    keyHits.Add((kp + g.position, i, lane, c));
                }
            }
        }
        GUI.EndClip();

        if (_graphMode == 0)
            Theme.DrawText(new Rect(g.x + 8, g.yMax - 18, g.width - 16, 16), "Speed graph: drag a handle up / down for speed, left / right for influence.  Ctrl+click a key: auto bezier.", _tiny, Theme.TextMuted);
        else
            Theme.DrawText(new Rect(g.x + 8, g.yMax - 18, g.width - 16, 16), "Value graph: drag keys up / down to change values, handles to shape the curve.  X red, Y green, Z blue.", _tiny, Theme.TextMuted);

        // hover info
        foreach (var h in handleHits)
            if ((h.p + g.position - e.mousePosition).sqrMagnitude < 64f)
            {
                LaneCurve c = LaneEditing.Out(P, h.lane, h.key);
                _hoverInfo = $"{LaneEditing.Name(h.lane)} {(h.outgoing ? "outgoing" : "incoming")} handle: " + LaneEditing.Describe(c, h.outgoing);
            }
        if (_hoverInfo == null)
            foreach (var k in keyHits)
                if ((k.p - e.mousePosition).sqrMagnitude < 64f) { _hoverInfo = KeyInfo(k.idx, k.lane); break; }

        GraphInput(keyHits, handleHits.Select(h => (h.p + g.position, h.lane, h.key, h.outgoing, h.comp)).ToList());
    }

    private static float NiceStep(float raw)
    {
        if (raw <= 0) return 1;
        float p = Mathf.Pow(10, Mathf.Floor(Mathf.Log10(raw)));
        float f = raw / p;
        return (f < 1.5f ? 1 : f < 3.5f ? 2 : f < 7.5f ? 5 : 10) * p;
    }

    /// <summary>Draw one bezier handle of the stretch a -> b (outgoing at a, or incoming at b).</summary>
    private void DrawHandle(KeyLanes lane, int a, int b, bool outgoing, int comp, Vector2 keyPoint, Rect g, List<(Vector2, KeyLanes, int, bool, int)> hits)
    {
        LaneCurve c = LaneEditing.Out(P, lane, a);
        if (c.Interp == LaneInterp.Hold) return;
        LaneCurve bz = LaneEditing.AsBezier(c);
        float D = _starts[b] - _starts[a];
        if (D <= 1e-5f) return;
        Vector2 hp;
        if (_graphMode == 0)
        {
            float avg = SegmentDistance(lane, a, b) / D;
            float sp = outgoing ? (bz.X1 > 1e-4f ? avg * bz.Y1 / bz.X1 : 0f) : (1f - bz.X2 > 1e-4f ? avg * (1f - bz.Y2) / (1f - bz.X2) : 0f);
            float ht = outgoing ? _starts[a] + bz.X1 * D : _starts[b] - (1f - bz.X2) * D;
            keyPoint = new Vector2(keyPoint.x, GY(sp) - g.y);
            hp = new Vector2(X(ht) - g.x, GY(sp) - g.y);
        }
        else
        {
            float va = LaneValue(lane, a, comp), vb = LaneValue(lane, b, comp);
            float ht = outgoing ? _starts[a] + bz.X1 * D : _starts[a] + bz.X2 * D;
            float hv = va + (outgoing ? bz.Y1 : bz.Y2) * (vb - va);
            hp = new Vector2(X(ht) - g.x, GY(hv) - g.y);
        }
        Color hc = c.Interp == LaneInterp.Bezier ? new Color(1f, 0.85f, 0.3f) : new Color(1f, 1f, 1f, 0.5f);
        Draw2D.Line(keyPoint, hp, hc.WithAlpha(0.8f), 1.2f);
        Theme.Dot(hp, 4.5f, new Color(0, 0, 0, 0.8f));
        Theme.Dot(hp, 3.3f, hc);
        hits.Add((hp, lane, a, outgoing, comp));
    }

    private void GraphInput(List<(Vector2 p, int idx, KeyLanes lane, int comp)> keyHits, List<(Vector2 p, KeyLanes lane, int key, bool outgoing, int comp)> handleHits)
    {
        Event e = Event.current;
        Rect g = _graphRect;
        int id = GUIUtility.GetControlID(FocusType.Passive);
        Vector2 m = e.mousePosition;
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown when g.Contains(m) && (e.button == 1 || e.button == 2):
                GUIUtility.hotControl = id; _drag = DragKind.Pan; _dragStart = m; _panStartView = _viewStart; e.Use();
                break;
            case EventType.MouseDown when g.Contains(m) && e.button == 0:
            {
                GUIUtility.hotControl = id;
                _dragStart = m; _dragMoved = false;
                var h = handleHits.Where(x => (x.p - m).sqrMagnitude < 81f).OrderBy(x => (x.p - m).sqrMagnitude).ToList();
                if (h.Count > 0)
                {
                    _dragHandle = (h[0].lane, h[0].key, h[0].outgoing, h[0].comp);
                    _drag = DragKind.Handle;
                    PushUndo();
                    e.Use();
                    break;
                }
                var k = keyHits.Where(x => (x.p - m).sqrMagnitude < 81f).OrderBy(x => (x.p - m).sqrMagnitude).ToList();
                if (k.Count > 0)
                {
                    var (_, idx, lane, comp) = k[0];
                    if (e.control) { ToggleAuto(idx, lane); GUIUtility.hotControl = 0; e.Use(); break; }
                    if (e.shift) Select(idx, lane, !Selected(idx, lane));
                    else if (!Selected(idx, lane)) { _sel.Clear(); Select(idx, lane); }
                    _drag = _graphMode == 1 ? DragKind.GraphKey : DragKind.Keys;
                    _dragHandle = (lane, idx, false, comp);
                    _dragKeyTime = _starts[idx];
                    _scrubStartValue = LaneValue(lane, idx, comp);
                    e.Use();
                    break;
                }
                if (!e.shift) _sel.Clear();
                _drag = DragKind.Marquee;
                e.Use();
                break;
            }
            case EventType.MouseDrag when GUIUtility.hotControl == id:
                if (_drag == DragKind.Pan) { _viewStart = _panStartView - (m.x - _dragStart.x) / _pps; e.Use(); break; }
                if (!_dragMoved && (m - _dragStart).sqrMagnitude > 9f)
                {
                    _dragMoved = true;
                    if (_drag == DragKind.Keys) BeginKeyDrag();
                    if (_drag == DragKind.GraphKey)
                    {
                        // value graph: mostly vertical = change the value, mostly horizontal = retime
                        if (Mathf.Abs(m.x - _dragStart.x) > Mathf.Abs(m.y - _dragStart.y)) { _drag = DragKind.Keys; BeginKeyDrag(); }
                        else PushUndo();
                    }
                }
                if (_dragMoved)
                {
                    if (_drag == DragKind.Keys) KeyDrag(m.x);
                    else if (_drag == DragKind.Handle) HandleDrag(m);
                    else if (_drag == DragKind.GraphKey) GraphValueDrag(m);
                }
                e.Use();
                break;
            case EventType.MouseUp when GUIUtility.hotControl == id:
                GUIUtility.hotControl = 0;
                if (_drag == DragKind.Marquee && _dragMoved)
                {
                    Rect r = MarqueeRect(m);
                    foreach (var k in keyHits) if (r.Contains(k.p)) Select(k.idx, k.lane);
                }
                else if (_drag == DragKind.Marquee && !_dragMoved && !e.shift) SetTime(T(m.x), true, snap: true);
                if (_drag == DragKind.Keys && _dragMoved) EndKeyDrag();
                if ((_drag == DragKind.Handle || _drag == DragKind.GraphKey) && _orbsDirty) { KeyframeManager.Instance.RefreshOrbs(); _orbsDirty = false; }
                _drag = DragKind.None;
                e.Use();
                break;
        }
    }

    private void HandleDrag(Vector2 m)
    {
        var (lane, a, outgoing, comp) = _dragHandle;
        int b = LaneEditing.NextKey(P, lane, a);
        if (b < 0) return;
        float D = _starts[b] - _starts[a];
        if (D <= 1e-5f) return;
        LaneCurve c = LaneEditing.AsBezier(LaneEditing.Out(P, lane, a));
        float t = T(m.x);
        if (_graphMode == 0)
        {
            float avg = SegmentDistance(lane, a, b) / D;
            float sp = Mathf.Max(0f, GV(m.y));
            if (outgoing)
            {
                c.X1 = Mathf.Clamp((t - _starts[a]) / D, 0.001f, 1f);
                if (avg > 1e-5f) c.Y1 = Mathf.Clamp(sp * c.X1 / avg, -2f, 3f);
            }
            else
            {
                float infl = Mathf.Clamp((_starts[b] - t) / D, 0.001f, 1f);
                c.X2 = 1f - infl;
                if (avg > 1e-5f) c.Y2 = Mathf.Clamp(1f - sp * infl / avg, -2f, 3f);
            }
            _hoverInfo = $"{(outgoing ? "Out" : "In")}: influence {(outgoing ? c.X1 : 1f - c.X2) * 100f:0.0}%, speed {sp:0.00}";
        }
        else
        {
            float va = LaneValue(lane, a, comp), vb = LaneValue(lane, b, comp);
            float u = Mathf.Clamp01((t - _starts[a]) / D);
            float y = Mathf.Abs(vb - va) > 1e-4f ? (GV(m.y) - va) / (vb - va) : (outgoing ? c.Y1 : c.Y2);
            if (outgoing) { c.X1 = Mathf.Max(0.001f, u); c.Y1 = Mathf.Clamp(y, -2f, 3f); }
            else { c.X2 = Mathf.Min(0.999f, u); c.Y2 = Mathf.Clamp(y, -2f, 3f); }
        }
        LaneEditing.SetOut(P, lane, a, c);
        Changed();
    }

    private void GraphValueDrag(Vector2 m)
    {
        var (lane, idx, _, comp) = _dragHandle;
        if (idx < 0 || idx >= Keys.Count || !Keys[idx].Has(lane)) return;
        float v = _scrubStartValue + (GV(m.y) - GV(_dragStart.y));
        Keyframe k = Keys[idx];
        switch (lane)
        {
            case KeyLanes.Position: { Vector3 p = k.Position; p[comp] = v; k.Position = p; break; }
            case KeyLanes.Rotation: { Vector3 r = k.Rotation; r[comp] = v; k.Rotation = r; break; }
            case KeyLanes.Zoom: k.FieldOfView = Mathf.Clamp(v, 1f, 170f); break;
            case KeyLanes.TimeOfDay: k.TimeOfDay = Mathf.Repeat(v, 24f); break;
        }
        Keys[idx] = k;
        Changed();
    }

    // =====================================================================
    //  Bottom-left: interpolation, presets, velocity, camera shake
    // =====================================================================

    private void DrawLeftBottom()
    {
        Rect r = _leftBottom;
        if (r.height < 40) return;
        Theme.Fill(r, Theme.Raised.WithAlpha(0.6f), 8);
        float x = r.x + 10, y = r.y + 8, w = r.width - 20;

        Theme.DrawText(new Rect(x, y, w, 16), "KEYFRAME INTERPOLATION", _tiny, Theme.Accent);
        y += 18;
        string[] names = { "Easy Ease", "Ease In", "Ease Out", "Linear", "Hold", "Auto" };
        string[] tips = { "F9", "Shift+F9: ease into the key", "Ctrl+Shift+F9: ease out of the key", "Constant speed through the key", "Ctrl+Alt+H: keep the value until the next key", "Ctrl+click a key: auto bezier (smooth momentum)" };
        float bw = (w - 5 * 4) / 6f;
        for (int i = 0; i < names.Length; i++)
        {
            if (GUI.Button(new Rect(x + i * (bw + 4), y, bw, 24), new GUIContent(names[i], tips[i]), Theme.LabelCenterSmall)) Interp(i);
        }
        y += 30;

        if (r.yMax - y > 30)
        {
            Theme.DrawText(new Rect(x, y, w, 16), "EASE PRESETS (outgoing)", _tiny, Theme.Accent);
            y += 18;
            var presets = CurveEditor.Presets;
            float pw = (w - (presets.Length - 1) * 3) / presets.Length;
            for (int i = 0; i < presets.Length; i++)
            {
                if (Widgets.Ghost("ake.pre" + i, new Rect(x + i * (pw + 3), y, pw, 22), presets[i].name, _tiny, false, 5))
                    ApplyPreset(presets[i].curve);
            }
            y += 28;
        }

        // keyframe velocity (like After Effects' Keyframe Velocity dialog) for one selected key
        var one = _sel.Select(s => (i: IndexOf(s.guid), s.lane)).Where(s => s.i >= 0).ToList();
        if (one.Count == 1 && r.yMax - y > 52)
        {
            var (idx, lane) = one[0];
            Theme.DrawText(new Rect(x, y, w, 16), $"KEYFRAME VELOCITY · {LaneEditing.Name(lane)} at {Timecode(_starts[idx], P.FPS)}", _tiny, Theme.Accent);
            y += 18;
            VelocityRow(new Rect(x, y, w, 22), lane, idx, false);
            y += 24;
            VelocityRow(new Rect(x, y, w, 22), lane, idx, true);
            y += 28;
        }

        if (r.yMax - y > 48)
        {
            Theme.DrawText(new Rect(x, y, w, 16), "CAMERA SHAKE  ·  wiggle(freq, amount)", _tiny, Theme.Accent);
            y += 18;
            Wiggle wg = P.Wiggle;
            bool on = Widgets.Switch("ake.wiggle", new Rect(x, y, 56, 24), wg.Enabled, "");
            float fx = x + 54;
            GUI.Label(new Rect(fx, y, 40, 24), "freq", _tiny);
            float freq = Widgets.FloatField("ake.wf", new Rect(fx + 30, y + 1, 60, 22), "Hz", Theme.AxisY, wg.Frequency);
            GUI.Label(new Rect(fx + 96, y, 40, 24), "turn", _tiny);
            float rot = Widgets.FloatField("ake.wr", new Rect(fx + 128, y + 1, 60, 22), "°", Theme.AxisZ, wg.Rotation);
            GUI.Label(new Rect(fx + 194, y, 40, 24), "move", _tiny);
            float pos = Widgets.FloatField("ake.wp", new Rect(fx + 230, y + 1, 70, 22), "m", Theme.AxisX, wg.Position);
            if (on != wg.Enabled || freq != wg.Frequency || rot != wg.Rotation || pos != wg.Position)
            {
                if (on != wg.Enabled) PushUndo();
                wg.Enabled = on; wg.Frequency = Mathf.Clamp(freq, 0.05f, 30f); wg.Rotation = Mathf.Clamp(rot, 0f, 30f); wg.Position = Mathf.Clamp(pos, 0f, 2f);
                P.Wiggle = wg;
                Changed();
            }
        }
    }

    private void VelocityRow(Rect r, KeyLanes lane, int idx, bool outgoing)
    {
        int other = outgoing ? LaneEditing.NextKey(P, lane, idx) : LaneEditing.PrevKey(P, lane, idx);
        GUI.Label(new Rect(r.x, r.y, 70, r.height), outgoing ? "Outgoing" : "Incoming", _tiny);
        if (other < 0) { GUI.Label(new Rect(r.x + 70, r.y, 200, r.height), "—", _tiny); return; }
        int a = outgoing ? idx : other, b = outgoing ? other : idx;
        LaneCurve c = LaneEditing.Out(P, lane, a);
        if (c.Interp == LaneInterp.Hold || c.Interp == LaneInterp.Auto)
        {
            GUI.Label(new Rect(r.x + 70, r.y, 220, r.height), LaneEditing.Describe(c, outgoing), _tiny);
            return;
        }
        LaneCurve bz = LaneEditing.AsBezier(c);
        float D = _starts[b] - _starts[a];
        float avg = D > 1e-5f ? SegmentDistance(lane, a, b) / D : 0f;
        float infl = outgoing ? bz.X1 : 1f - bz.X2;
        float speed = outgoing ? (bz.X1 > 1e-4f ? avg * bz.Y1 / bz.X1 : 0f) : (1f - bz.X2 > 1e-4f ? avg * (1f - bz.Y2) / (1f - bz.X2) : 0f);
        string unit = lane == KeyLanes.Position ? "m/s" : lane == KeyLanes.TimeOfDay ? "h/s" : "°/s";
        float ns = Widgets.FloatField($"ake.v{outgoing}s", new Rect(r.x + 70, r.y, 120, r.height), "v", Theme.AxisY, (float)Math.Round(speed, 3));
        GUI.Label(new Rect(r.x + 194, r.y, 30, r.height), unit, _tiny);
        float ni = Widgets.FloatField($"ake.v{outgoing}i", new Rect(r.x + 232, r.y, 90, r.height), "%", Theme.AxisZ, (float)Math.Round(infl * 100f, 2));
        if (Mathf.Abs(ns - (float)Math.Round(speed, 3)) > 1e-4f || Mathf.Abs(ni - (float)Math.Round(infl * 100f, 2)) > 1e-3f)
        {
            PushUndo();
            float newInfl = Mathf.Clamp(ni / 100f, 0.001f, 1f);
            if (outgoing) { bz.X1 = newInfl; bz.Y1 = avg > 1e-5f ? Mathf.Clamp(ns * newInfl / avg, -2f, 3f) : 0f; }
            else { bz.X2 = 1f - newInfl; bz.Y2 = avg > 1e-5f ? Mathf.Clamp(1f - ns * newInfl / avg, -2f, 3f) : 1f; }
            LaneEditing.SetOut(P, lane, a, bz);
            Changed(orbsNow: true);
        }
    }

    private void DrawStatus()
    {
        Rect r = _status;
        string text = _hoverInfo;
        if (text == null)
        {
            int n = _sel.Count;
            text = n == 0
                ? "Click keys to select · drag to retime · drag empty space to box-select · wheel zoom, Shift+wheel / right-drag pan · Space play · J / K previous / next key"
                : $"{n} key{(n == 1 ? "" : "s")} selected · F9 Easy Ease · Ctrl+Alt+H hold · Delete remove · Ctrl+C / Ctrl+V copy / paste at the playhead";
        }
        Theme.DrawText(r, text, _tiny, Theme.TextMuted);
    }

    private void DrawResizeGrip()
    {
        Rect grip = new Rect(_w - 18, _h - 18, 16, 16);
        for (int i = 0; i < 3; i++)
            Draw2D.Line(new Vector2(grip.xMax - 3 - i * 4, grip.yMax - 2), new Vector2(grip.xMax - 2, grip.yMax - 3 - i * 4), Theme.TextMuted.WithAlpha(0.6f), 1f);
        Event e = Event.current;
        int id = GUIUtility.GetControlID(FocusType.Passive);
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown when grip.Contains(e.mousePosition) && e.button == 0:
                GUIUtility.hotControl = id; _drag = DragKind.Resize; _dragStart = GUIUtility.GUIToScreenPoint(e.mousePosition);
                _resizeStartSize = new Vector2(_w, _h); e.Use();
                break;
            case EventType.MouseDrag when GUIUtility.hotControl == id && _drag == DragKind.Resize:
                if (Mgr != null)
                {
                    Vector2 d = GUIUtility.GUIToScreenPoint(e.mousePosition) - _dragStart;
                    Mgr.RequestedSize = new Vector2(Mathf.Clamp(_resizeStartSize.x + d.x, 820, Screen.width),
                        Mathf.Clamp(_resizeStartSize.y + d.y, 420, Screen.height - 40));
                }
                e.Use();
                break;
            case EventType.MouseUp when GUIUtility.hotControl == id:
                GUIUtility.hotControl = 0; _drag = DragKind.None; e.Use();
                break;
        }
    }

    // =====================================================================
    //  Audio waveform
    // =====================================================================

    private AudioClip _waveClip;
    private float[] _peaks;

    private void DrawWaveform(Rect r, float ox)
    {
        AudioClip clip = Player.LoadedClip;
        if (clip == null) return;
        if (_waveClip != clip)
        {
            _waveClip = clip;
            _peaks = null;
            try
            {
                int ch = Mathf.Max(1, clip.channels);
                var data = new float[clip.samples * ch];
                if (clip.GetData(data, 0))
                {
                    const int buckets = 8000;
                    _peaks = new float[buckets];
                    int per = Mathf.Max(1, data.Length / buckets);
                    for (int b = 0; b < buckets; b++)
                    {
                        float m = 0;
                        int s0 = b * per, s1 = Mathf.Min(data.Length, s0 + per);
                        for (int s = s0; s < s1; s += 4) m = Mathf.Max(m, Mathf.Abs(data[s]));
                        _peaks[b] = m;
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[MonkeFrames::AdvancedKeyframes] Waveform unavailable: {ex.Message}"); }
        }
        if (_peaks == null) return;
        float start = P.AudioStartTime, len = clip.length;
        float mid = r.y + r.height / 2f;
        Color wc = new Color(0.45f, 0.95f, 0.7f, 0.55f);
        for (float x = Mathf.Max(0, X(0) - ox); x < r.width; x += 2)
        {
            float at = T(x + ox) + start;
            if (at < 0 || at >= len) continue;
            float pk = _peaks[Mathf.Clamp((int)(at / len * _peaks.Length), 0, _peaks.Length - 1)];
            float hh = pk * (r.height / 2f - 3);
            Draw2D.Rect(new Rect(x, mid - hh, 1.5f, hh * 2 + 1), wc);
        }
    }

    // =====================================================================
    //  Playback / camera
    // =====================================================================

    private void TogglePlay()
    {
        _playing = !_playing;
        if (_playing && _time >= _length - FrameTime) _time = 0f;
        Player.PreviewAudio(_playing, _time, true);
        if (!_playing) CameraManager.Instance.Blur.Set(false, 0f);
    }

    private void Advance()
    {
        _time += Time.unscaledDeltaTime;
        if (_time >= _length)
        {
            _time = 0f;
            Player.PreviewAudio(true, 0f, true);
        }
        else Player.PreviewAudio(true, _time, false);
        ApplyCamera(playing: true);
        // keep the playhead in view
        if (X(_time) > _tlX + _tlW - 20) _viewStart = _time - 20f / _pps;
    }

    private void SetTime(float t, bool preview, bool snap = true)
    {
        _time = Mathf.Clamp(snap ? SnapTime(t) : t, 0f, Mathf.Max(0f, _length));
        if (preview) { ApplyCamera(); Player.PreviewAudio(_playing, _time, true); }
    }

    /// <summary>Show the animation at the playhead through the MonkeFrames camera.</summary>
    private void ApplyCamera(bool playing = false)
    {
        var frames = P.CompiledKeyframes;
        if (frames == null || frames.Count == 0 || CameraManager.Instance == null) return;
        Keyframe f = frames[Mathf.Clamp(FrameAt(_time), 0, frames.Count - 1)];
        CameraManager.Instance.Blur.Set(playing && f.MotionBlur, f.MotionBlurStrength);
        CameraManager.Instance.CancelLookSmoothing();
        CameraManager.Instance.Position = f.Position;
        CameraManager.Instance.Rotation = f.QuatRotation;
        CameraManager.Instance.FieldOfView = f.FieldOfView;
        PhotorealLink.KeyframeHour = f.TimeOfDay;
    }

    // =====================================================================
    //  Editing commands
    // =====================================================================

    /// <summary>Change a property's value at the playhead (adds a key there when the property is animated).</summary>
    private void EditLane(KeyLanes lane, Func<Keyframe, Keyframe> edit)
    {
        if (Keys.Count == 0) { UIManager.Instance.Status = "Add a keyframe first (V, or ◆ Key all)."; return; }
        int idx = LaneEditing.KeyAt(P, lane, _time);
        if (idx < 0)
        {
            int count = LaneEditing.CountKeys(P, lane);
            if (count >= 2) idx = LaneEditing.KeyAtTime(P, _time, lane);
            else
            {
                // not animated: the value is the same at every time (like After Effects with the stopwatch off)
                idx = 0;
                for (int i = 0; i < Keys.Count; i++) if (Keys[i].Has(lane)) { idx = i; break; }
                Keyframe k0 = Keys[idx]; k0.Keys |= lane; Keys[idx] = k0;
            }
        }
        Keys[idx] = edit(Keys[idx]);
        Changed();
    }

    private void ToggleKeyHere(KeyLanes lane, int keyHere)
    {
        if (Keys.Count == 0) { UIManager.Instance.Status = "Add a keyframe first (V, or ◆ Key all)."; return; }
        PushUndo();
        if (keyHere >= 0)
        {
            if (LaneEditing.CountKeys(P, lane) <= 1) { UIManager.Instance.Status = "A property keeps at least one key (use the stopwatch to stop animating it)."; return; }
            LaneEditing.Unkey(P, keyHere, lane);
            UIManager.Instance.Status = $"Removed the {LaneEditing.Name(lane)} key at {Timecode(_time, P.FPS)}.";
        }
        else
        {
            int i = LaneEditing.KeyAtTime(P, _time, lane);
            UIManager.Instance.Status = $"Added a {LaneEditing.Name(lane)} key at {Timecode(_time, P.FPS)}.";
            _sel.Clear(); Select(i, lane);
        }
        Changed(orbsNow: true);
    }

    private void ToggleStopwatch(KeyLanes lane, bool animated)
    {
        if (Keys.Count == 0) return;
        PushUndo();
        if (animated)
        {
            // keep the value at the playhead everywhere: one key on the first keyframe
            Keyframe now = LaneEditing.Sample(P, _time);
            for (int i = Keys.Count - 1; i >= 1; i--) if (Keys[i].Has(lane)) LaneEditing.Unkey(P, i, lane);
            Keyframe k0 = Keys[0];
            LaneEditing.CopyLane(ref k0, now, lane);
            k0.Keys |= lane;
            k0.Transition.SetLane(lane, null);
            Keys[0] = k0;
            UIManager.Instance.Status = $"{LaneEditing.Name(lane)} is no longer animated.";
        }
        else
        {
            int i = LaneEditing.KeyAtTime(P, _time, lane);
            if (i != 0) { Keyframe k0 = Keys[0]; k0.Keys |= lane; Keys[0] = k0; }
            UIManager.Instance.Status = $"{LaneEditing.Name(lane)} is animated: change its value at another time to add keys.";
        }
        Changed(orbsNow: true);
    }

    private void KeyFromCamera(KeyLanes lane)
    {
        CameraManager cam = CameraManager.Instance;
        PushUndo();
        Keyframe src = new Keyframe { Position = cam.Position, Rotation = cam.Rotation.eulerAngles, FieldOfView = cam.FieldOfView };
        if (Keys.Count == 0)
        {
            KeyframeManager.Instance.CreateKeyframe();
            Changed(orbsNow: true);
            return;
        }
        int i = LaneEditing.KeyAtTime(P, _time, lane);
        Keyframe k = Keys[i];
        LaneEditing.CopyLane(ref k, src, lane & ~KeyLanes.TimeOfDay);
        Keys[i] = k;
        _sel.Clear(); Select(i, lane);
        UIManager.Instance.Status = $"Keyed {LaneEditing.Name(lane)} from the camera at {Timecode(_time, P.FPS)}.";
        Changed(orbsNow: true);
    }

    private void KeyAllFromCamera()
    {
        CameraManager cam = CameraManager.Instance;
        PushUndo();
        if (Keys.Count == 0)
        {
            KeyframeManager.Instance.CreateKeyframe();
            Changed(orbsNow: true);
            return;
        }
        Keyframe src = new Keyframe { Position = cam.Position, Rotation = cam.Rotation.eulerAngles, FieldOfView = cam.FieldOfView };
        int i = LaneEditing.KeyAtTime(P, _time, KeyLanes.Position | KeyLanes.Rotation | KeyLanes.Zoom);
        Keyframe k = Keys[i];
        LaneEditing.CopyLane(ref k, src, KeyLanes.Position | KeyLanes.Rotation | KeyLanes.Zoom);
        Keys[i] = k;
        _sel.Clear();
        foreach (var l in new[] { KeyLanes.Position, KeyLanes.Rotation, KeyLanes.Zoom }) Select(i, l);
        UIManager.Instance.Status = $"Keyed the camera at {Timecode(_time, P.FPS)}.";
        Changed(orbsNow: true);
    }

    private List<(int idx, KeyLanes lane)> SelectedKeys() =>
        _sel.Select(s => (i: IndexOf(s.guid), s.lane)).Where(x => x.i >= 0).OrderBy(x => x.i).ToList();

    private void EaseSelected(bool easeIn, bool easeOut)
    {
        var keys = SelectedKeys();
        if (keys.Count == 0) { UIManager.Instance.Status = "Select keys first (click, Shift+click or drag a box)."; return; }
        PushUndo();
        foreach (var (idx, lane) in keys)
        {
            if (easeOut) LaneEditing.EditOut(P, lane, idx, LaneEditing.EaseOut);
            if (easeIn) LaneEditing.EditIn(P, lane, idx, LaneEditing.EaseIn);
        }
        UIManager.Instance.Status = $"{(easeIn && easeOut ? "Easy Ease" : easeIn ? "Easy Ease In" : "Easy Ease Out")} on {keys.Count} key{(keys.Count == 1 ? "" : "s")}.";
        Changed(orbsNow: true);
    }

    /// <summary>0 Easy Ease, 1 ease in, 2 ease out, 3 linear, 4 hold, 5 auto bezier.</summary>
    private void Interp(int kind)
    {
        var keys = SelectedKeys();
        if (keys.Count == 0) { UIManager.Instance.Status = "Select keys first (click, Shift+click or drag a box)."; return; }
        switch (kind)
        {
            case 0: EaseSelected(true, true); return;
            case 1: EaseSelected(true, false); return;
            case 2: EaseSelected(false, true); return;
        }
        PushUndo();
        foreach (var (idx, lane) in keys)
        {
            switch (kind)
            {
                case 3:
                    LaneEditing.EditOut(P, lane, idx, LaneEditing.LinearOut);
                    LaneEditing.EditIn(P, lane, idx, LaneEditing.LinearIn);
                    break;
                case 4:
                    LaneEditing.EditOut(P, lane, idx, c => c.Interp == LaneInterp.Hold ? LaneCurve.Linear : new LaneCurve { Interp = LaneInterp.Hold });
                    break;
                case 5:
                    LaneEditing.EditOut(P, lane, idx, _ => new LaneCurve { Interp = LaneInterp.Auto });
                    LaneEditing.EditIn(P, lane, idx, _ => new LaneCurve { Interp = LaneInterp.Auto });
                    break;
            }
        }
        string[] names = { "", "", "", "Linear", "Hold", "Auto Bezier" };
        UIManager.Instance.Status = $"{names[kind]} on {keys.Count} key{(keys.Count == 1 ? "" : "s")}.";
        Changed(orbsNow: true);
    }

    private void ToggleAuto(int idx, KeyLanes lane)
    {
        PushUndo();
        bool isAuto = LaneEditing.Out(P, lane, idx).Interp == LaneInterp.Auto;
        LaneCurve to = isAuto ? LaneCurve.Linear : new LaneCurve { Interp = LaneInterp.Auto };
        LaneEditing.EditOut(P, lane, idx, _ => to);
        LaneEditing.EditIn(P, lane, idx, _ => to);
        UIManager.Instance.Status = $"{LaneEditing.Name(lane)} key: {(isAuto ? "Linear" : "Auto Bezier")}.";
        Changed(orbsNow: true);
    }

    private void ApplyPreset(Vector4 curve)
    {
        var keys = SelectedKeys();
        if (keys.Count == 0) { UIManager.Instance.Status = "Select keys first: the preset shapes how each one moves on to its next key."; return; }
        PushUndo();
        foreach (var (idx, lane) in keys)
            LaneEditing.EditOut(P, lane, idx, _ => LaneEditing.Simplify(new LaneCurve { Interp = LaneInterp.Bezier, X1 = curve.x, Y1 = curve.y, X2 = curve.z, Y2 = curve.w }));
        Changed(orbsNow: true);
    }

    private void DeleteSelected()
    {
        var keys = SelectedKeys();
        if (keys.Count == 0) return;
        PushUndo();
        int skipped = 0;
        foreach (var (idx, lane) in keys.OrderByDescending(k => k.idx))
        {
            if (idx >= Keys.Count) continue;
            if (LaneEditing.CountKeys(P, lane) <= 1) { skipped++; continue; }
            LaneEditing.Unkey(P, idx, lane);
        }
        _sel.Clear();
        UIManager.Instance.Status = skipped > 0 ? "Deleted keys (each property keeps at least one)." : $"Deleted {keys.Count} key{(keys.Count == 1 ? "" : "s")}.";
        Changed(orbsNow: true);
    }

    private void Copy()
    {
        var keys = SelectedKeys();
        if (keys.Count == 0) return;
        float t0 = keys.Min(k => _starts[k.idx]);
        _clipboard = keys.Select(k => (k.lane, _starts[k.idx] - t0, Keys[k.idx],
            LaneEditing.NextKey(P, k.lane, k.idx) >= 0 ? LaneEditing.Out(P, k.lane, k.idx) : LaneCurve.Linear)).ToArray();
        UIManager.Instance.Status = $"Copied {keys.Count} key{(keys.Count == 1 ? "" : "s")}.";
    }

    private void Paste()
    {
        if (_clipboard == null || _clipboard.Length == 0 || Keys.Count == 0) return;
        PushUndo();
        var pasted = new List<(KeyLanes, float)>();
        foreach (var (lane, dt, values, curve) in _clipboard)
        {
            int i = LaneEditing.KeyAtTime(P, _time + dt, lane);
            Keyframe k = Keys[i];
            LaneEditing.CopyLane(ref k, values, lane);
            k.Transition.SetLane(lane, curve);
            Keys[i] = k;
            pasted.Add((lane, Timeline.Starts(Keys)[i]));
            Refresh(force: true);
        }
        ReselectByTime(pasted);
        UIManager.Instance.Status = $"Pasted {_clipboard.Length} key{(_clipboard.Length == 1 ? "" : "s")} at {Timecode(_time, P.FPS)}.";
        Changed(orbsNow: true);
    }

    /// <summary>Roving keyframes: retime the keyframes between the first and last selected so the camera moves
    /// at an even speed (their positions stay the same).</summary>
    private void Rove()
    {
        var idx = SelectedIndices().ToList();
        if (idx.Count < 2) { idx = Enumerable.Range(0, Keys.Count).ToList(); }
        int a = idx.First(), b = idx.Last();
        if (b - a < 2) { UIManager.Instance.Status = "Roving needs at least three keyframes (select the first and last of the stretch)."; return; }
        float[] sp = _speed[KeyLanes.Position];
        var dist = new float[b - a + 1];
        for (int i = a + 1; i <= b; i++)
        {
            float d = 0;
            int f0 = _frameStarts[i - 1], f1 = Mathf.Min(_frameStarts[i], sp.Length);
            for (int f = f0; f < f1; f++) d += sp[f];
            dist[i - a] = dist[i - a - 1] + d / P.FPS;
        }
        if (dist[b - a] < 1e-4f) { UIManager.Instance.Status = "The camera doesn't move there."; return; }
        PushUndo();
        float t0 = _starts[a], t1 = _starts[b];
        var times = new float[b - a + 1];
        for (int i = a; i <= b; i++) times[i - a] = t0 + (t1 - t0) * dist[i - a] / dist[b - a];
        for (int i = a; i < b; i++)
        {
            Keyframe k = Keys[i];
            k.Transition.Duration = Mathf.Max(FrameTime, Mathf.Round((times[i - a + 1] - times[i - a]) / FrameTime) * FrameTime);
            Keys[i] = k;
        }
        UIManager.Instance.Status = $"Roved {b - a - 1} keyframe{(b - a - 1 == 1 ? "" : "s")}: even speed from {Timecode(t0, P.FPS)} to {Timecode(t1, P.FPS)}.";
        Changed(orbsNow: true);
    }

    // =====================================================================
    //  Keyboard
    // =====================================================================

    private void HandleKeyboard()
    {
        if (!CapturingKeys || GUIUtility.keyboardControl != 0 || _typingId != null) return;
        Keyboard kb = Keyboard.current;
        if (kb == null) return;
        bool ctrl = kb.ctrlKey.isPressed, shift = kb.shiftKey.isPressed, alt = kb.altKey.isPressed;

        if (kb.spaceKey.wasPressedThisFrame) TogglePlay();
        if (kb.f9Key.wasPressedThisFrame) { if (ctrl && shift) EaseSelected(false, true); else if (shift) EaseSelected(true, false); else EaseSelected(true, true); }
        if (kb.hKey.wasPressedThisFrame && ctrl && alt) Interp(4);
        if (kb.deleteKey.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame) DeleteSelected();
        if (ctrl && kb.zKey.wasPressedThisFrame) { if (shift) Redo(); else Undo(); }
        if (ctrl && kb.yKey.wasPressedThisFrame) Redo();
        if (ctrl && kb.cKey.wasPressedThisFrame) Copy();
        if (ctrl && kb.vKey.wasPressedThisFrame) Paste();
        if (ctrl && kb.aKey.wasPressedThisFrame)
        {
            _sel.Clear();
            for (int i = 0; i < Keys.Count; i++) foreach (var l in LaneEditing.Lanes) if (Keys[i].Has(l)) _sel.Add((Keys[i].GUID, l));
        }
        if (kb.escapeKey.wasPressedThisFrame) _sel.Clear();
        if (kb.homeKey.wasPressedThisFrame) SetTime(0, true);
        if (kb.endKey.wasPressedThisFrame) SetTime(_length, true);
        if (kb.pageUpKey.wasPressedThisFrame || (ctrl && kb.leftArrowKey.wasPressedThisFrame)) SetTime(_time - FrameTime * (shift ? 10 : 1), true);
        if (kb.pageDownKey.wasPressedThisFrame || (ctrl && kb.rightArrowKey.wasPressedThisFrame)) SetTime(_time + FrameTime * (shift ? 10 : 1), true);
        if (kb.jKey.wasPressedThisFrame) { float t = _starts.Take(Keys.Count).Where(s => s < _time - FrameTime * 0.5f).DefaultIfEmpty(0).Max(); SetTime(t, true); }
        if (kb.kKey.wasPressedThisFrame) { float t = _starts.Take(Keys.Count).Where(s => s > _time + FrameTime * 0.5f).DefaultIfEmpty(_length).Min(); SetTime(t, true); }
        if (kb.f3Key.wasPressedThisFrame && shift) _graph = !_graph;
        if (kb.equalsKey.wasPressedThisFrame) ZoomAt(X(_time), 1.4f);
        if (kb.minusKey.wasPressedThisFrame) ZoomAt(X(_time), 1f / 1.4f);
    }
}
