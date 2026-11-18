using GorillaGameModes;
using MonkeFrames.Editor.Components;
using System;
using UnityEngine;

namespace MonkeFrames.Editor.Replays;

/// <summary>
/// Game mode of the room (for the replay's info) and each replay gorilla's tagged / infected / game-mode body look.
/// The game shows those looks by swapping the body's first material for VRRig.materialsToChangeTo[VRRig.setMatIndex].
/// </summary>
internal static class ReplayGameMode
{
    // ---------------- Recording ----------------

    /// <summary>Readable name of the current game mode, or null when there isn't one (not in a room).</summary>
    public static string Current()
    {
        try
        {
            GorillaGameManager gm = GorillaGameManager.instance;
            if (gm == null) return null;
            GameModeType type = gm.GameType();
            switch (type)
            {
                case GameModeType.Casual: return "Casual";
                case GameModeType.Infection: return "Infection";
                case GameModeType.HuntDown: return "Hunt";
                case GameModeType.Paintbrawl: return "Paintbrawl";
                case GameModeType.Ambush: return "Ambush";
                case GameModeType.FreezeTag: return "Freeze Tag";
                case GameModeType.Ghost: return "Ghost";
                case GameModeType.Guardian: return "Guardian";
                case GameModeType.PropHunt: return "Prop Hunt";
                case GameModeType.InfectionCompetitive: return "Competitive Infection";
                case GameModeType.SuperInfect: return "Super Infection";
                case GameModeType.SuperCasual: return "Super Casual";
            }
            string name = gm.GameModeName();
            if (string.IsNullOrWhiteSpace(name)) name = type.ToString();
            return name.Length > 1 ? char.ToUpperInvariant(name[0]) + name.Substring(1).ToLowerInvariant() : name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Add the current game mode to the clip's list ("Casual / Infection") if it's new.</summary>
    public static void Note(ReplayClip clip)
    {
        if (clip == null) return;
        string mode = Current();
        if (string.IsNullOrEmpty(mode)) return;
        if (string.IsNullOrEmpty(clip.GameMode)) { clip.GameMode = mode; return; }
        foreach (string m in clip.GameMode.Split(new[] { " / " }, StringSplitOptions.None))
            if (m == mode) return;
        clip.GameMode += " / " + mode;
    }

    public static int MaterialIndexOf(VRRig rig)
    {
        try { return rig != null ? rig.setMatIndex : 0; }
        catch { return 0; }
    }

    // ---------------- Playback ----------------

    /// <summary>Per replay gorilla: which look is on its body and what its normal materials are.</summary>
    internal sealed class BodyState
    {
        public Renderer Renderer;
        public int Applied;
        public Material[] Normal;
        public Material Untagged;      // used for index 0 when the gorilla's copy was made while tagged
    }

    /// <summary>Show the body look the gorilla had at local frame f. Only touches materials when the look changes.</summary>
    public static void Apply(ReplayTrack t, float f)
    {
        ReplayPuppet p = t.Puppet;
        if (p == null) return;

        BodyState s = t.Body;
        if (t.MatChanges.Count == 0 && (s == null || s.Applied == 0))
            return;

        int want = t.MaterialAt(f);
        Renderer r = BodyRenderer(t, p);
        if (r == null) return;

        if (s == null || s.Renderer != r)
        {
            // New (or rebuilt) gorilla: its materials right now are its normal look.
            s = t.Body = new BodyState { Renderer = r };
        }
        if (s.Applied == want) return;

        try
        {
            if (s.Normal == null)
                s.Normal = r.sharedMaterials;

            if (want <= 0)
            {
                Material[] mats = (Material[])s.Normal.Clone();
                if (mats.Length > 0 && LooksTagged(mats[0]))
                {
                    Material plain = Untagged(t, p, s);
                    if (plain != null) mats[0] = plain;
                }
                r.sharedMaterials = mats;
                s.Applied = want;
                return;
            }

            Material tagged = TaggedMaterial(want);
            if (tagged == null) return;      // no live gorilla to take the look from: keep what's there
            Material[] m = (Material[])s.Normal.Clone();
            if (m.Length == 0) m = new Material[1];
            m[0] = tagged;
            r.sharedMaterials = m;
            s.Applied = want;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::Replay] Body look couldn't be changed for {t.Name}: {ex.Message}");
            t.MatChanges.Clear();      // don't retry every frame
        }
    }

    /// <summary>Forget the cached body state (the puppet was destroyed or rebuilt).</summary>
    public static void Reset(ReplayTrack t) => t.Body = null;

    private static Renderer BodyRenderer(ReplayTrack t, ReplayPuppet p)
    {
        if (p.BuildCtx == null || string.IsNullOrEmpty(t.MainSkinPath)) return null;
        if (!p.BuildCtx.Nodes.TryGetValue(t.MainSkinPath, out Transform node) || node == null) return null;
        return node.GetComponent<SkinnedMeshRenderer>();
    }

    private static Material[] LiveMaterials()
    {
        VRRig rig = CameraModes.LocalRig();
        if (rig == null) rig = VRRig.LocalRig;
        return rig != null ? rig.materialsToChangeTo : null;
    }

    private static Material TaggedMaterial(int index)
    {
        Material[] mats = LiveMaterials();
        return mats != null && index > 0 && index < mats.Length ? mats[index] : null;
    }

    /// <summary>True if this material is one of the game's tagged looks (the copy was made while the gorilla was tagged).</summary>
    private static bool LooksTagged(Material m)
    {
        Material[] mats = LiveMaterials();
        if (m == null || mats == null) return false;
        string name = Clean(m.name);
        for (int i = 1; i < mats.Length; i++)
            if (mats[i] != null && Clean(mats[i].name) == name)
                return true;
        return false;
    }

    private static Material Untagged(ReplayTrack t, ReplayPuppet p, BodyState s)
    {
        if (s.Untagged != null) return s.Untagged;
        Material[] mats = LiveMaterials();
        if (mats == null || mats.Length == 0 || mats[0] == null) return null;
        Material m = new Material(mats[0]);
        m.color = t.Color;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", t.Color);
        p.Owned.Add(m);
        return s.Untagged = m;
    }

    private static string Clean(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        return name.Replace("(Instance)", "").Replace("(Clone)", "").Trim();
    }
}
