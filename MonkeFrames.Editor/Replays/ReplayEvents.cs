using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace MonkeFrames.Editor.Replays;

/// <summary>
/// What a gorilla's items do besides moving: holdables and interactive cosmetics change size (Gorilla Tag scales a
/// holdable between sitting on the body and being held) and switch parts on and off (held / docked versions,
/// opened / closed states, effects). Only changes are stored, so a gorilla whose items never change adds nothing.
/// </summary>
public sealed class ReplayEvents
{
    public const byte KindScale = 0;     // Index = moving part (track.Paths), Value = local scale
    public const byte KindActive = 1;    // Index = renderer (track.RendererPaths), Value.x = 1 shown / 0 hidden

    public struct Event
    {
        public int Frame;     // frame of the track (0 = the track's first frame)
        public byte Kind;
        public int Index;
        public Vector3 Value;
    }

    public readonly List<Event> Items = new();
    public bool Empty => Items.Count == 0;

    // ---------------- File ----------------

    public void Write(BinaryWriter w)
    {
        w.Write(Items.Count);
        foreach (Event e in Items)
        {
            w.Write(e.Frame);
            w.Write(e.Kind);
            w.Write(e.Index);
            if (e.Kind == KindScale) { w.Write(e.Value.x); w.Write(e.Value.y); w.Write(e.Value.z); }
            else w.Write(e.Value.x > 0.5f);
        }
    }

    public static ReplayEvents Read(BinaryReader r)
    {
        var ev = new ReplayEvents();
        int n = r.ReadInt32();
        if (n < 0 || n > 50_000_000) throw new InvalidDataException("Replay item events are damaged");
        for (int i = 0; i < n; i++)
        {
            var e = new Event { Frame = r.ReadInt32(), Kind = r.ReadByte(), Index = r.ReadInt32() };
            e.Value = e.Kind == KindScale ? new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) : new Vector3(r.ReadBoolean() ? 1f : 0f, 0, 0);
            ev.Items.Add(e);
        }
        return ev;
    }

    // ---------------- Recording ----------------

    private sealed class Recorder
    {
        public Transform[] Parts;
        public Vector3[] LastScale;
        public bool[] ScaleLogged;
        public readonly List<Renderer> Renderers = new();
        public readonly List<bool> LastActive = new();
        public readonly List<bool> ActiveLogged = new();
    }

    private static readonly ConditionalWeakTable<ReplayTrack, Recorder> Recorders = new();

    /// <summary>Start watching a gorilla being recorded (its moving parts and copied renderers, in track order).</summary>
    internal static void Begin(ReplayTrack track, Transform[] liveParts, IList<Renderer> liveRenderers)
    {
        var rec = new Recorder { Parts = liveParts, LastScale = new Vector3[liveParts.Length], ScaleLogged = new bool[liveParts.Length] };
        for (int i = 0; i < liveParts.Length; i++) rec.LastScale[i] = liveParts[i] != null ? liveParts[i].localScale : Vector3.one;
        foreach (Renderer r in liveRenderers) AddRenderer(rec, r);
        Recorders.Remove(track);
        Recorders.Add(track, rec);
    }

    /// <summary>A renderer that appeared after recording started (added to the end of track.RendererPaths).</summary>
    internal static void AddRenderer(ReplayTrack track, Renderer r)
    {
        if (Recorders.TryGetValue(track, out Recorder rec)) AddRenderer(rec, r);
    }

    private static void AddRenderer(Recorder rec, Renderer r)
    {
        rec.Renderers.Add(r);
        rec.LastActive.Add(r != null && r.gameObject.activeInHierarchy);
        rec.ActiveLogged.Add(false);
    }

    /// <summary>Called for every recorded frame of the track (frame = frames since the track started).</summary>
    public static void Sample(ReplayTrack track, int frame)
    {
        if (track == null || !Recorders.TryGetValue(track, out Recorder rec)) return;
        List<Event> items = track.Events.Items;
        for (int i = 0; i < rec.Parts.Length; i++)
        {
            Transform t = rec.Parts[i];
            if (t == null) continue;
            Vector3 s = t.localScale;
            if ((s - rec.LastScale[i]).sqrMagnitude < 1e-8f) continue;
            if (!rec.ScaleLogged[i])
            {
                // the first change: also store what it was before, so seeking back restores it
                items.Add(new Event { Frame = 0, Kind = KindScale, Index = i, Value = rec.LastScale[i] });
                rec.ScaleLogged[i] = true;
            }
            items.Add(new Event { Frame = frame, Kind = KindScale, Index = i, Value = s });
            rec.LastScale[i] = s;
        }
        // (only whether an item's object is switched on: the game's culling and first person hiding use the
        // renderer's own flags, which replays ignore)
        for (int i = 0; i < rec.Renderers.Count; i++)
        {
            Renderer r = rec.Renderers[i];
            bool on = r != null && r.gameObject.activeInHierarchy;
            if (on == rec.LastActive[i]) continue;
            if (!rec.ActiveLogged[i])
            {
                items.Add(new Event { Frame = 0, Kind = KindActive, Index = i, Value = new Vector3(rec.LastActive[i] ? 1 : 0, 0, 0) });
                rec.ActiveLogged[i] = true;
            }
            items.Add(new Event { Frame = frame, Kind = KindActive, Index = i, Value = new Vector3(on ? 1 : 0, 0, 0) });
            rec.LastActive[i] = on;
        }
    }

    internal static void End(ReplayTrack track) => Recorders.Remove(track);

    // ---------------- Playback ----------------

    private sealed class Player
    {
        public int[] Kinds, Indices;
        public List<(int frame, Vector3 value)>[] Timelines;
        public int[] Applied;   // last applied event index per timeline
    }

    private static readonly ConditionalWeakTable<ReplayTrack, Player> Players = new();

    private static Player For(ReplayTrack track)
    {
        if (Players.TryGetValue(track, out Player p)) return p;
        var map = new Dictionary<(byte, int), List<(int, Vector3)>>();
        foreach (Event e in track.Events.Items)
        {
            if (!map.TryGetValue((e.Kind, e.Index), out var list)) map[(e.Kind, e.Index)] = list = new List<(int, Vector3)>();
            list.Add((e.Frame, e.Value));
        }
        p = new Player { Kinds = new int[map.Count], Indices = new int[map.Count], Timelines = new List<(int, Vector3)>[map.Count], Applied = new int[map.Count] };
        int k = 0;
        foreach (var kv in map)
        {
            kv.Value.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            p.Kinds[k] = kv.Key.Item1;
            p.Indices[k] = kv.Key.Item2;
            p.Timelines[k] = kv.Value;
            p.Applied[k] = -1;
            k++;
        }
        Players.Add(track, p);
        return p;
    }

    /// <summary>Show the items as they were at frame (frames since the track started).</summary>
    public static void Apply(ReplayTrack track, ReplayPuppet puppet, int frame)
    {
        if (track == null || puppet == null || track.Events.Empty) return;
        Player p = For(track);
        for (int k = 0; k < p.Timelines.Length; k++)
        {
            var list = p.Timelines[k];
            int lo = 0, hi = list.Count - 1, at = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (list[mid].frame <= frame) { at = mid; lo = mid + 1; } else hi = mid - 1;
            }
            if (at < 0) at = 0;
            if (at == p.Applied[k]) continue;
            p.Applied[k] = at;
            Vector3 v = list[at].value;
            int idx = p.Indices[k];
            if (p.Kinds[k] == KindScale)
            {
                if (idx >= 0 && idx < puppet.Parts.Length && puppet.Parts[idx] != null) puppet.Parts[idx].localScale = v;
            }
            else if (idx >= 0 && idx < track.RendererPaths.Length && puppet.BuildCtx != null
                     && puppet.BuildCtx.Nodes.TryGetValue(track.RendererPaths[idx], out Transform node) && node != null)
            {
                Renderer r = node.GetComponent<Renderer>();
                if (r != null) r.enabled = v.x > 0.5f;
            }
        }
    }

    /// <summary>Forget cached playback state (after the events change, e.g. a new recording).</summary>
    public static void Reset(ReplayTrack track) { if (track != null) Players.Remove(track); }
}
