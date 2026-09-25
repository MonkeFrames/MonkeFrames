using System;
using System.Collections.Generic;
using System.Linq;
using MonkeFrames.Editor.Replays;
using UnityEngine;
using Keyframe = MonkeFrames.Compiler.Models.Keyframe;

namespace MonkeFrames.Editor.Components;

public static class KeyframeEditHistory
{
    private sealed class Snapshot
    {
        public List<Keyframe> Keyframes;
        public int Selection;
        public ReplayClip ReplayClip;
        public float ReplayInPoint;

        public Snapshot(KeyframeManager manager)
        {
            Keyframes = manager.Project.Keyframes.ToList();
            Selection = UIManager.Instance.Selection;
            ReplayStudio studio = ReplayStudio.Instance;
            ReplayClip = studio != null && studio.Active ? ReplayManager.Instance?.Clip : null;
            ReplayInPoint = ReplayClip?.InPoint ?? 0f;
        }
    }

    private sealed class History
    {
        public readonly Stack<Snapshot> Undo = new();
        public readonly Stack<Snapshot> Redo = new();
        public void Clear() { Undo.Clear(); Redo.Clear(); }
    }

    private static readonly History ProjectHistory = new();
    private static readonly History ReplayHistory = new();
    private static Keyframe? Clipboard;
    private static Snapshot PendingSnapshot;
    private static History PendingHistory;

    private static bool InReplayEditor => ReplayStudio.Instance != null && ReplayStudio.Instance.Active
        && ReplayManager.Instance?.Clip != null;
    private static History CurrentHistory => InReplayEditor ? ReplayHistory : ProjectHistory;

    public static void ResetHistory()
    {
        ProjectHistory.Clear();
        ReplayHistory.Clear();
        Clipboard = null;
        PendingSnapshot = null;
        PendingHistory = null;
    }

    public static void Execute(Action edit)
    {
        KeyframeManager manager = KeyframeManager.Instance;
        if (manager?.Project == null || edit == null)
            return;

        History history = CurrentHistory;
        Snapshot before = new Snapshot(manager);
        edit();
        history.Undo.Push(before);
        history.Redo.Clear();
    }

    public static void BeginEdit()
    {
        KeyframeManager manager = KeyframeManager.Instance;
        if (manager?.Project == null || PendingSnapshot != null)
            return;

        PendingHistory = CurrentHistory;
        PendingSnapshot = new Snapshot(manager);
    }

    public static void CommitEdit()
    {
        if (PendingSnapshot == null || PendingHistory == null)
            return;

        PendingHistory.Undo.Push(PendingSnapshot);
        PendingHistory.Redo.Clear();
        PendingSnapshot = null;
        PendingHistory = null;
    }

    public static void CancelEdit()
    {
        PendingSnapshot = null;
        PendingHistory = null;
    }

    public static void CopySelection()
    {
        if (!TryGetSelection(out Keyframe keyframe))
            return;

        Clipboard = keyframe;
        UIManager.Instance.Status = "Copied selected keyframe.";
    }

    public static void CutSelection()
    {
        if (!TryGetSelection(out Keyframe keyframe))
            return;

        Clipboard = keyframe;
        if (InReplayEditor)
            ReplayStudio.Instance.DeleteKeyframe(UIManager.Instance.Selection);
        else
            KeyframeManager.Instance.DeleteKeyframe(UIManager.Instance.Selection);
        UIManager.Instance.Status = "Cut selected keyframe.";
    }

    public static void PasteAfterSelection()
    {
        if (Clipboard == null)
            return;

        KeyframeManager manager = KeyframeManager.Instance;
        if (manager?.Project == null)
            return;

        Keyframe pasted = Clipboard.Value;
        pasted.GUID = Guid.NewGuid().ToString();
        pasted.Compiled = false;

        if (InReplayEditor)
            Execute(() => ReplayStudio.Instance.InsertPastedKeyframe(pasted));
        else
            Execute(() =>
            {
                int selection = UIManager.Instance.Selection;
                int insertAt = selection >= 0 && selection < manager.Project.Keyframes.Count
                    ? selection + 1
                    : manager.Project.Keyframes.Count;
                manager.Project.Keyframes.Insert(insertAt, pasted);
                UIManager.Instance.Selection = insertAt;
                manager.CreateOrb(pasted);
            });
        UIManager.Instance.Status = "Pasted keyframe after the selection.";
    }

    public static void Undo() => Restore(CurrentHistory.Undo, CurrentHistory.Redo);

    public static void Redo() => Restore(CurrentHistory.Redo, CurrentHistory.Undo);

    private static bool TryGetSelection(out Keyframe keyframe)
    {
        keyframe = default;
        KeyframeManager manager = KeyframeManager.Instance;
        int selection = UIManager.Instance.Selection;
        if (manager?.Project == null || selection < 0 || selection >= manager.Project.Keyframes.Count)
            return false;

        keyframe = manager.Project.Keyframes[selection];
        return true;
    }

    private static void Restore(Stack<Snapshot> source, Stack<Snapshot> destination)
    {
        KeyframeManager manager = KeyframeManager.Instance;
        if (manager?.Project == null || source.Count == 0)
            return;

        Snapshot snapshot = source.Pop();
        ReplayClip currentClip = InReplayEditor ? ReplayManager.Instance.Clip : null;
        if (!ReferenceEquals(snapshot.ReplayClip, currentClip))
        {
            source.Clear();
            destination.Clear();
            return;
        }

        destination.Push(new Snapshot(manager));

        manager.Project.Keyframes.Clear();
        manager.Project.Keyframes.AddRange(snapshot.Keyframes);

        if (snapshot.ReplayClip != null)
        {
            snapshot.ReplayClip.InPoint = snapshot.ReplayInPoint;
            snapshot.ReplayClip.Dirty = true;
        }

        UIManager.Instance.Selection = Mathf.Clamp(snapshot.Selection, -1, manager.Project.Keyframes.Count - 1);

        manager.RefreshOrbs();
        ReplayManager.Instance?.Seek(ReplayManager.Instance.Time);

        UIManager.Instance.Status = source == CurrentHistory.Undo ? "Undid keyframe edit." : "Redid keyframe edit.";
    }
}
