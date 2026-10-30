using System.Collections.Generic;
using Keyframe = MonkeFrames.Compiler.Models.Keyframe;

namespace MonkeFrames.Editor.Classes;

internal sealed class KeyframeHistory
{
    internal sealed class State
    {
        public Keyframe[] Keys;
        public int Selection, Revision;
        public float? ReplayInPoint;
        public State(List<Keyframe> keys, int selection, float? start)
        { Keys = keys.ToArray(); Selection = selection; ReplayInPoint = start; }

        public bool Matches(List<Keyframe> keys, float? start)
        {
            if (Keys.Length != keys.Count || ReplayInPoint != start) return false;
            for (int i = 0; i < Keys.Length; i++)
            {
                var a = Keys[i]; var b = keys[i];
                if (a.GUID != b.GUID || !a.Position.Equals(b.Position) || !a.Rotation.Equals(b.Rotation)
                    || a.FieldOfView != b.FieldOfView || a.MotionBlur != b.MotionBlur
                    || a.MotionBlurStrength != b.MotionBlurStrength || !a.Transition.Equals(b.Transition)) return false;
            }
            return true;
        }
    }
    private readonly List<State> undo = new(), redo = new();
    public void Clear() { undo.Clear(); redo.Clear(); }
    public void Record(State state) { Push(undo, state); redo.Clear(); }
    public State Undo(State current) => Move(undo, redo, current);
    public State Redo(State current) => Move(redo, undo, current);
    private static State Move(List<State> from, List<State> to, State current)
    {
        if (from.Count == 0) return null;
        var result = from[from.Count - 1]; from.RemoveAt(from.Count - 1);
        Push(to, current); return result;
    }
    private static void Push(List<State> list, State state)
    {
        if (list.Count == 100) list.RemoveAt(0);
        list.Add(state);
    }
}
