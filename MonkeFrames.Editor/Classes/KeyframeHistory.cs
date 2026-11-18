using System.Collections.Generic;
using MonkeFrames.Compiler.Models;

namespace MonkeFrames.Editor.Classes;

/// <summary>Undo / redo stacks of keyframe-list snapshots (Edit > Undo, Redo).</summary>
internal sealed class KeyframeHistory
{
    private const int MaxSteps = 100;

    /// <summary>A copy of the keyframe list (keyframes are structs, so copying the array copies them).</summary>
    internal sealed class State
    {
        public readonly Keyframe[] Keys;
        public readonly int Selection;
        public readonly string[] ExtraSelection;
        public readonly float? ReplayInPoint;

        /// <summary>History revision when this was captured (used by property edits).</summary>
        public int Revision;

        public State(List<Keyframe> keys, int selection, string[] extraSelection, float? replayInPoint)
        {
            Keys = keys.ToArray();
            Selection = selection;
            ExtraSelection = extraSelection ?? [];
            ReplayInPoint = replayInPoint;
        }

        /// <summary>True if <paramref name="keys"/> holds exactly the same keyframes (every animated field).</summary>
        public bool Matches(List<Keyframe> keys, float? replayInPoint)
        {
            if (Keys.Length != keys.Count || ReplayInPoint != replayInPoint)
                return false;

            for (int i = 0; i < Keys.Length; i++)
            {
                Keyframe a = Keys[i], b = keys[i];
                if (a.GUID != b.GUID || a.AnimationHash() != b.AnimationHash())
                    return false;
            }
            return true;
        }
    }

    private readonly List<State> _undo = new();
    private readonly List<State> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>Save the state before a change. A new change drops the redo stack.</summary>
    public void Record(State before)
    {
        Push(_undo, before);
        _redo.Clear();
    }

    /// <summary>Returns the state to restore (or null), and keeps <paramref name="current"/> for redo.</summary>
    public State Undo(State current) => Move(_undo, _redo, current);

    /// <summary>Returns the state to restore (or null), and keeps <paramref name="current"/> for undo.</summary>
    public State Redo(State current) => Move(_redo, _undo, current);

    private static State Move(List<State> from, List<State> to, State current)
    {
        if (from.Count == 0)
            return null;

        State state = from[from.Count - 1];
        from.RemoveAt(from.Count - 1);
        Push(to, current);
        return state;
    }

    private static void Push(List<State> list, State state)
    {
        if (list.Count >= MaxSteps)
            list.RemoveAt(0);
        list.Add(state);
    }
}
