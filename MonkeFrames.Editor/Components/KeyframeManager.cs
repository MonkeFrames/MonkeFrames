using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MonkeFrames.Compiler.Models;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Replays;
using MonkeFrames.Editor.Utilities;
using UnityEngine;
using UnityEngine.InputSystem;
using Keyframe = MonkeFrames.Compiler.Models.Keyframe;

namespace MonkeFrames.Editor.Components;

public class KeyframeManager : MonoBehaviour
{
    public static KeyframeManager Instance;
    public Project Project;

    public Dictionary<Keyframe, GameObject> Objects = new Dictionary<Keyframe, GameObject>();

    public bool IsCompiling;

    // Undo / redo
    private readonly KeyframeHistory _history = new KeyframeHistory();
    private Project _historyProject;
    private ReplayClip _historyClip;
    private int _historyRevision;
    private string _editGroup;
    private bool _batching;

    // Cut / copy / paste
    private List<Keyframe> _clipboard;

    // Extra keyframes selected with Shift / Ctrl+click in the Keyframe Editor (by GUID, so they survive list
    // changes). UIManager.Selection stays the primary selection.
    private readonly HashSet<string> _extraSelection = new();
    private string _selectionAnchor;

    public KeyframeManager()
    {
        Instance = this;
    }

    /// <summary>Keyframes can be changed right now (not compiling, playing back or exporting).</summary>
    public bool CanEdit => Project != null && !IsCompiling && CameraManager.Instance != null
        && !CameraManager.Instance.InPlayback && !CameraManager.Instance.Exporting;

    private bool ValidSelection => Project != null && UIManager.Instance.Selection >= 0 && UIManager.Instance.Selection < Project.Keyframes.Count;

    private static bool InStudio => ReplayStudio.Instance != null && ReplayStudio.Instance.Active;

    public GameObject CreateOrb(string name)
    {
        GameObject mainOrb = new GameObject(name);

        LineRenderer line = mainOrb.AddComponent<LineRenderer>();

        line.startColor = Settings.current.AccentColor;
        line.endColor = Settings.current.AccentColor;
        line.startWidth = 0.05f;
        line.endWidth = 0.15f;

        line.SetPosition(0, mainOrb.transform.position);
        line.SetPosition(1, mainOrb.transform.position + (mainOrb.transform.forward * 0.25f));

        line.material.shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");

        return mainOrb;
    }

    public void CreateOrb(Keyframe keyframe)
    {
        GameObject mainOrb = CreateOrb($"MonkeFrames keyframe visual");

        mainOrb.transform.position = keyframe.Position;
        mainOrb.transform.rotation = keyframe.QuatRotation;

        LineRenderer line = mainOrb.GetComponent<LineRenderer>();

        line.SetPosition(0, mainOrb.transform.position);
        line.SetPosition(1, mainOrb.transform.position + (mainOrb.transform.forward * 0.25f));

        Objects.TryAdd(keyframe, mainOrb);
    }

    public void Start()
    {
        Instance = this;
        Console.WriteLine("[MonkeFrames::KeyframeManager] creating new project \"new project\"");

        Project = new Project("new project", Constants.Exporter);
        Project.FPS = 60;

        Console.WriteLine("[MonkeFrames::KeyframeManager] Keyframe manager is running");
    }

    public void Update()
    {
        // Drop the history as soon as the project or the viewed replay changes.
        CheckHistoryContext();

        if (!CanEdit || Keyboard.current == null)
            return;

        // Ctrl+V / Ctrl+X... are Edit menu shortcuts, not "add" / "replace keyframe".
        if (Keyboard.current.ctrlKey.isPressed || Keyboard.current.leftMetaKey.isPressed || Keyboard.current.rightMetaKey.isPressed)
            return;

        // Don't treat typing in a text field (keyframe values, room codes...) as shortcuts.
        if (GUIUtility.keyboardControl != 0)
            return;

        // The Advanced Keyframe Editor has its own keys (Delete, Ctrl+V...) while the mouse is over it.
        if (Windows.AdvancedKeyframeEditor.CapturingKeys)
            return;

        // In Replay Studio, keyframes are placed at the replay playhead and deleting keeps timing.
        var studio = ReplayStudio.Instance;
        bool inStudio = studio != null && studio.Active;

        if (Keyboard.current.vKey.wasPressedThisFrame)
        {
            if (inStudio) studio.AddKeyframeAtPlayhead();
            else CreateKeyframe();
        }

        if (Keyboard.current.fKey.wasPressedThisFrame && UIManager.Instance.Selection != -1)
        {
            if (inStudio) studio.GoTo(UIManager.Instance.Selection);
            else GoToKeyframe(UIManager.Instance.Selection);
        }

        if (Keyboard.current.tKey.wasPressedThisFrame)
            CreateKeyframe(lookAtPlayer: true);

        if (Keyboard.current.xKey.wasPressedThisFrame && UIManager.Instance.Selection != -1)
            CreateKeyframe(replaceKeyframeIdx: UIManager.Instance.Selection);

        if (Keyboard.current.deleteKey.wasPressedThisFrame && UIManager.Instance.Selection != -1)
            DeleteSelection();
    }

    // =====================================================================
    //  Undo / redo
    // =====================================================================

    /// <summary>Clears the history when the project or the replay being edited changes.</summary>
    private void CheckHistoryContext()
    {
        ReplayManager rm = ReplayManager.Instance;
        ReplayClip clip = rm != null && rm.Viewing ? rm.Clip : null;
        if (_historyProject == Project && _historyClip == clip)
            return;

        _history.Clear();
        _editGroup = null;
        _historyProject = Project;
        _historyClip = clip;
    }

    private KeyframeHistory.State CaptureState()
    {
        ValidateSelection();
        return new KeyframeHistory.State(Project.Keyframes, UIManager.Instance.Selection, _extraSelection.ToArray(), _historyClip?.InPoint);
    }

    /// <summary>Saves the keyframes as an undo step. Call right before changing them.</summary>
    public void RecordUndo()
    {
        if (Project == null || _batching)
            return;

        CheckHistoryContext();
        _history.Record(CaptureState());
        _historyRevision++;
        _editGroup = null;
        Project.IsCompiled = false;
    }

    /// <summary>
    /// Call before GUI that edits keyframe properties, then <see cref="EndPropertyEdit"/> after it. If anything
    /// changed, one undo step is saved; a continuous drag or typing in one field stays one step.
    /// </summary>
    internal KeyframeHistory.State BeginPropertyEdit()
    {
        Event e = Event.current;
        if (!CanEdit || e == null || e.type == EventType.Repaint || e.type == EventType.Layout || e.type == EventType.Used)
            return null;

        CheckHistoryContext();
        KeyframeHistory.State state = CaptureState();
        state.Revision = _historyRevision;
        return state;
    }

    /// <param name="group">Names what's being edited (e.g. "keyframe:" + GUID), so edits to another keyframe start a new step.</param>
    internal void EndPropertyEdit(KeyframeHistory.State before, string group)
    {
        // Something inside already saved its own step (Delete, Apply to all...), or nothing changed.
        if (before == null || Project == null || before.Revision != _historyRevision || before.Matches(Project.Keyframes, _historyClip?.InPoint))
            return;

        string key = $"{group}:{GUIUtility.hotControl}:{GUIUtility.keyboardControl}";
        if (_editGroup != key || (GUIUtility.hotControl == 0 && GUIUtility.keyboardControl == 0))
        {
            _history.Record(before);
            _historyRevision++;
        }
        _editGroup = key;
        Project.IsCompiled = false;
        if (_historyClip != null)
            _historyClip.Dirty = true;
    }

    public void Undo() => RestoreHistory(redo: false);

    public void Redo() => RestoreHistory(redo: true);

    private void RestoreHistory(bool redo)
    {
        // The Advanced Keyframe Editor has its own undo while the mouse is over it.
        if (Windows.AdvancedKeyframeEditor.CapturingKeys || !CanEdit)
            return;

        CheckHistoryContext();
        KeyframeHistory.State state = redo ? _history.Redo(CaptureState()) : _history.Undo(CaptureState());
        if (state == null)
        {
            UIManager.Instance.Status = redo ? "Nothing to redo." : "Nothing to undo.";
            return;
        }

        _historyRevision++;
        _editGroup = null;
        GUIUtility.keyboardControl = 0;   // a focused text field would keep showing the old value

        Project.Keyframes = new List<Keyframe>(state.Keys);
        Project.IsCompiled = false;
        UIManager.Instance.Selection = Mathf.Clamp(state.Selection, -1, Project.Keyframes.Count - 1);
        _extraSelection.Clear();
        foreach (string guid in state.ExtraSelection) _extraSelection.Add(guid);
        _selectionAnchor = PrimaryGuid();
        ValidateSelection();

        if (_historyClip != null && state.ReplayInPoint.HasValue)
        {
            _historyClip.InPoint = state.ReplayInPoint.Value;
            _historyClip.Dirty = true;
        }

        RefreshOrbs();
        UIManager.Instance.Status = redo ? "Redid keyframe edit." : "Undid keyframe edit.";
    }

    // =====================================================================
    //  Cut / copy / paste
    // =====================================================================

    public void CopySelection()
    {
        if (Windows.AdvancedKeyframeEditor.CapturingKeys || !ValidSelection)
            return;

        _clipboard = SelectedIndices().Select(i => Project.Keyframes[i]).ToList();
        UIManager.Instance.Status = _clipboard.Count == 1 ? "Copied keyframe." : $"Copied {_clipboard.Count} keyframes.";
    }

    public void CutSelection()
    {
        if (Windows.AdvancedKeyframeEditor.CapturingKeys || !CanEdit || !ValidSelection)
            return;

        CopySelection();
        DeleteSelection();
    }

    public void Paste()
    {
        if (Windows.AdvancedKeyframeEditor.CapturingKeys || !CanEdit || _clipboard == null || _clipboard.Count == 0)
            return;

        List<Keyframe> pasted = _clipboard.Select(k =>
        {
            k.GUID = Guid.NewGuid().ToString();
            k.Compiled = false;
            return k;
        }).ToList();

        RecordUndo();
        _batching = true;   // the whole paste is one undo step
        try
        {
            if (InStudio)
            {
                // Each one goes in after the one before it (Replay Studio keeps the timing).
                foreach (Keyframe k in pasted)
                    ReplayStudio.Instance.InsertPastedKeyframe(k);
            }
            else
            {
                int insertAt = ValidSelection ? UIManager.Instance.Selection + 1 : Project.Keyframes.Count;
                Project.Keyframes.InsertRange(insertAt, pasted);
                UIManager.Instance.Selection = insertAt + pasted.Count - 1;
                RefreshOrbs();
            }
        }
        finally
        {
            _batching = false;
        }

        // Select everything that was pasted (the last one is the primary selection).
        _extraSelection.Clear();
        for (int i = 0; i < pasted.Count - 1; i++) _extraSelection.Add(pasted[i].GUID);
        _selectionAnchor = PrimaryGuid();

        UIManager.Instance.Status = pasted.Count == 1 ? "Pasted keyframe." : $"Pasted {pasted.Count} keyframes.";
    }

    // =====================================================================
    //  Multi-selection
    // =====================================================================

    private string PrimaryGuid() => ValidSelection ? Project.Keyframes[UIManager.Instance.Selection].GUID : null;

    /// <summary>Drops extra selections that no longer exist, or that belong to an old primary selection
    /// (another window selected something else).</summary>
    private void ValidateSelection()
    {
        if (_extraSelection.Count == 0)
            return;

        string primary = PrimaryGuid();
        if (primary == null || primary != _selectionAnchor)
        {
            _extraSelection.Clear();
            return;
        }

        HashSet<string> present = new HashSet<string>(Project.Keyframes.Select(k => k.GUID));
        _extraSelection.RemoveWhere(g => g == primary || !present.Contains(g));
    }

    /// <summary>Number of selected keyframes (primary plus extras).</summary>
    public int SelectionCount
    {
        get
        {
            ValidateSelection();
            return ValidSelection ? 1 + _extraSelection.Count : 0;
        }
    }

    public bool IsSelected(int index)
    {
        if (Project == null || index < 0 || index >= Project.Keyframes.Count)
            return false;
        if (index == UIManager.Instance.Selection)
            return true;
        ValidateSelection();
        return _extraSelection.Contains(Project.Keyframes[index].GUID);
    }

    /// <summary>Indices of every selected keyframe, in list order.</summary>
    public List<int> SelectedIndices()
    {
        List<int> result = new List<int>();
        if (!ValidSelection)
            return result;

        ValidateSelection();
        for (int i = 0; i < Project.Keyframes.Count; i++)
            if (i == UIManager.Instance.Selection || _extraSelection.Contains(Project.Keyframes[i].GUID))
                result.Add(i);
        return result;
    }

    /// <summary>Select <paramref name="primary"/> plus <paramref name="extras"/> (indices).</summary>
    public void SetSelection(int primary, IEnumerable<int> extras)
    {
        UIManager.Instance.Selection = primary;
        _extraSelection.Clear();
        if (Project != null && extras != null)
            foreach (int i in extras)
                if (i >= 0 && i < Project.Keyframes.Count && i != primary)
                    _extraSelection.Add(Project.Keyframes[i].GUID);
        _selectionAnchor = PrimaryGuid();
    }

    public void ClearExtraSelection() => _extraSelection.Clear();

    /// <summary>Deletes every selected keyframe as one undo step (Replay Studio keeps the others' timing).</summary>
    public void DeleteSelection()
    {
        if (!CanEdit || !ValidSelection)
            return;

        List<int> indices = SelectedIndices();
        if (indices.Count == 1)
        {
            if (InStudio) ReplayStudio.Instance.DeleteKeyframe(indices[0]);
            else DeleteKeyframe(indices[0]);
            return;
        }

        RecordUndo();
        _batching = true;
        try
        {
            for (int n = indices.Count - 1; n >= 0; n--)
            {
                if (InStudio) ReplayStudio.Instance.DeleteKeyframe(indices[n]);
                else DeleteKeyframe(indices[n]);
            }
        }
        finally
        {
            _batching = false;
        }

        _extraSelection.Clear();
        UIManager.Instance.Selection = Mathf.Min(indices[0], Project.Keyframes.Count - 1);
        UIManager.Instance.Status = $"Deleted {indices.Count} keyframes.";
    }

    // =====================================================================
    //  Keyframes
    // =====================================================================

    /// <summary>Move the camera to a keyframe's position, rotation and FOV.</summary>
    public void GoToKeyframe(int index)
    {
        if (index < 0 || index >= Project.Keyframes.Count)
            return;

        Keyframe k = Project.Keyframes[index];

        CameraManager.Instance.CancelLookSmoothing();
        CameraManager.Instance.Position = k.Position;
        CameraManager.Instance.Rotation = k.QuatRotation;
        CameraManager.Instance.FieldOfView = k.FieldOfView;
        PhotorealLink.KeyframeHour = k.TimeOfDay;
    }

    private Project _lastClosed;
    private static string LastClosedPath => System.IO.Path.Combine(Utilities.SaveUtilities.ProjectDirectory, "_last closed project.frames");

    /// <summary>Leave the current keyframe project: stops playback, keeps a backup (Reopen Last Closed brings it
    /// back) and starts a fresh, empty project.</summary>
    public void CloseProject()
    {
        CameraManager cm = CameraManager.Instance;
        if (cm != null && cm.InPlayback) cm.StopPlayback();
        Project old = Project;
        if (old != null && old.Keyframes.Count > 0)
        {
            _lastClosed = old;
            try
            {
                System.IO.Directory.CreateDirectory(Utilities.SaveUtilities.ProjectDirectory);
                System.IO.File.WriteAllText(LastClosedPath, old.ToJson());
            }
            catch (Exception ex) { Console.WriteLine($"[MonkeFrames::KeyframeManager] Couldn't back up the closed project: {ex.Message}"); }
        }
        Project = new Project("new project", Constants.Exporter) { FPS = old != null ? old.FPS : 60 };
        UIManager.Instance.Selection = -1;
        _extraSelection.Clear();
        CheckHistoryContext();
        PhotorealLink.KeyframeHour = -1f;
        if (cm != null) cm.Blur.Set(false, 0f);
        RefreshOrbs();
        UIManager.Instance.Status = old != null && old.Keyframes.Count > 0
            ? $"Closed \"{old.Name}\". Project > Reopen Last Closed brings it back."
            : "Started a new, empty project.";
    }

    /// <summary>Brings back the project closed last (this session, or the backup file).</summary>
    public void ReopenLastClosed()
    {
        Project p = _lastClosed;
        if (p == null)
        {
            try
            {
                if (System.IO.File.Exists(LastClosedPath))
                    p = Project.FromJson(System.IO.File.ReadAllText(LastClosedPath));
            }
            catch { }
        }
        if (p == null) { UIManager.Instance.Status = "There's no closed project to reopen."; return; }
        _lastClosed = null;
        LoadProject(p);
        UIManager.Instance.Status = $"Reopened \"{p.Name}\".";
    }

    public void LoadProject(Project p)
    {
        UIManager.Instance.Selection = -1;
        _extraSelection.Clear();
        Project = p;
        CheckHistoryContext();
        RefreshOrbs();
        ObjectManager.Instance?.SyncWithProject();
    }

    public void StartBuild()
    {
        Task.Run(async () => {
            MonkeFrames.Extensions.ExtensionManager.CallMethod("OnCompilationStart", Project);
            IsCompiling = true;
            await Task.Delay(100); // give frame time to process
            await Project.Build();
            IsCompiling = false;
            MonkeFrames.Extensions.ExtensionManager.CallMethod("OnCompilationEnd", Project);
        });
    }

    public void StartBuildAndRun()
    {
        Task.Run(async () => {
            MonkeFrames.Extensions.ExtensionManager.CallMethod("OnCompilationStart", Project);
            IsCompiling = true;
            await Task.Delay(100); // give frame time to process
            await Project.Build();
            IsCompiling = false;
            MonkeFrames.Extensions.ExtensionManager.CallMethod("OnCompilationEnd", Project);
            CameraManager.Instance.StartPlayback();
        });
    }

    public Keyframe CreateKeyframe(int replaceKeyframeIdx = -1, bool lookAtPlayer = false)
    {
        return CreateKeyframeCore(replaceKeyframeIdx, lookAtPlayer);
    }

    private Keyframe CreateKeyframeCore(int replaceKeyframeIdx, bool lookAtPlayer)
    {
        if (!CanEdit || replaceKeyframeIdx < -1 || replaceKeyframeIdx >= Project.Keyframes.Count)
            return default;

        RecordUndo();

        Keyframe k = new Keyframe();

        k.Position = CameraManager.Instance.Position;
        k.Rotation = CameraManager.Instance.Rotation.eulerAngles;
        k.FieldOfView = CameraManager.Instance.FieldOfView;

        if (lookAtPlayer)
            k.Rotation = Quaternion.LookRotation(GorillaTagger.Instance.headCollider.transform.position - k.Position).eulerAngles;

        k.Position = new Vector3(MathF.Round(k.Position.x, 2), MathF.Round(k.Position.y, 2), MathF.Round(k.Position.z, 2));
        k.Rotation = new Vector3(MathF.Round(k.Rotation.x, 2), MathF.Round(k.Rotation.y, 2), MathF.Round(k.Rotation.z, 2));

        k.Transition.Duration = 5f;

        if (Settings.current?.SmoothByDefault == true)
            k.Transition.Effect = TransitionEffect.Smooth;

        if (replaceKeyframeIdx != -1)
        {
            // Replacing a keyframe keeps its timing and transition; only the camera changes.
            Keyframe old = Project.Keyframes[replaceKeyframeIdx];
            k.Transition = old.Transition;
            k.MotionBlur = old.MotionBlur;
            k.MotionBlurStrength = old.MotionBlurStrength;

            try { Objects[old].Destroy(); Objects.Remove(old); } catch { }
            Project.Keyframes.RemoveAt(replaceKeyframeIdx);
            Project.Keyframes.Insert(replaceKeyframeIdx, k);
        } else
        {
            Project.Keyframes.Add(k);
            UIManager.Instance.Selection = Project.Keyframes.IndexOf(k);
        }

        CreateOrb(k);

        string posStr = $"Position: {UnityUtilities.Vector3ToString(k.Position)}";
        string rotStr = $"Rotation: {UnityUtilities.Vector3ToString(k.Rotation)}";

        UIManager.Instance.Status = $"Created keyframe {Project.Keyframes.IndexOf(k)} with properties {{ {posStr}, {rotStr}, FOV: {k.FieldOfView} }} ";

        return k;
    }

    public void DeleteKeyframe(int index)
    {
        if (Project == null || index < 0 || index >= Project.Keyframes.Count || !CanEdit)
            return;

        RecordUndo();
        Keyframe keyframe = Project.Keyframes[index];
        if (Objects.TryGetValue(keyframe, out GameObject orb))
            orb.Destroy();
        Objects.Remove(keyframe);
        Project.Keyframes.RemoveAt(index);
        UIManager.Instance.Selection = Mathf.Min(index, Project.Keyframes.Count - 1);
    }

    public void DeleteOrbs()
    {
        Objects.Values.ForEach(g => g.Destroy());
        Objects.Clear();
    }

    public void RefreshOrbs()
    {
        DeleteOrbs();
        Project?.Keyframes.ForEach(CreateOrb);
    }
}
