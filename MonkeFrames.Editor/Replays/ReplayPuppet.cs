using MonkeFrames.Editor.Components;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace MonkeFrames.Editor.Replays;

/// <summary>
/// A script-free copy of a gorilla (meshes + skeleton only) that a replay moves around.
/// Other Cameras can film it exactly like a live player.
/// </summary>
public class ReplayPuppet : CamSubject
{
    public ReplayTrack Track;
    public Transform[] Parts = System.Array.Empty<Transform>();
    public Transform HeadNode, LeftHandNode, RightHandNode;

    internal readonly List<Object> Owned = new();   // copied materials / meshes to clean up

    public override string DisplayName => Track?.Name ?? "Gorilla";
    public override bool IsLocal => Track != null && Track.IsLocal;
    public override bool IsReplay => true;
    public override bool Available => this != null && gameObject.activeInHierarchy;
    public override Transform Head => HeadNode != null ? HeadNode : transform;
    public override Transform Hand(bool right)
    {
        Transform t = right ? RightHandNode : LeftHandNode;
        return t != null ? t : Head;
    }

    /// <summary>Free copied materials / meshes (OnDestroy isn't called for objects that were never active).</summary>
    public void ReleaseOwned()
    {
        foreach (Object o in Owned)
            if (o != null)
                Destroy(o);
        Owned.Clear();
    }

    private void OnDestroy() => ReleaseOwned();
}

/// <summary>Builds <see cref="ReplayPuppet"/>s from live gorillas (while recording) or from a saved replay.</summary>
public static class PuppetBuilder
{
    private static Transform _container;

    public static Transform Container
    {
        get
        {
            if (_container == null)
            {
                GameObject go = new GameObject("MonkeFrames Replay Gorillas");
                Object.DontDestroyOnLoad(go);
                _container = go.transform;
            }
            return _container;
        }
    }

    // ---------------- Paths ----------------

    /// <summary>Path of t below root, e.g. "rig/body/head". Duplicate sibling names get "#n".</summary>
    public static string PathOf(Transform t, Transform root)
    {
        if (t == null || t == root) return "";
        var parts = new List<string>();
        while (t != null && t != root)
        {
            parts.Add(Segment(t));
            t = t.parent;
        }
        if (t != root) return null;   // not under root
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static string Segment(Transform t)
    {
        Transform p = t.parent;
        if (p == null) return t.name;

        int same = 0, index = 0;
        for (int i = 0; i < p.childCount; i++)
        {
            Transform c = p.GetChild(i);
            if (c.name != t.name) continue;
            if (c == t) index = same;
            same++;
        }
        return same > 1 ? $"{t.name}#{index}" : t.name;
    }

    public static Transform Find(Transform root, string path)
    {
        if (root == null || path == null) return null;
        if (path.Length == 0) return root;

        Transform cur = root;
        foreach (string seg in path.Split('/'))
        {
            string name = seg;
            int want = 0;
            int hash = seg.LastIndexOf('#');
            if (hash > 0 && int.TryParse(seg.Substring(hash + 1), out int n))
            {
                name = seg.Substring(0, hash);
                want = n;
            }

            Transform next = null;
            int seen = 0;
            for (int i = 0; i < cur.childCount; i++)
            {
                Transform c = cur.GetChild(i);
                if (c.name != name) continue;
                if (seen++ == want) { next = c; break; }
            }
            if (next == null) return null;
            cur = next;
        }
        return cur;
    }

    // ---------------- Building ----------------

    private sealed class Ctx
    {
        public Transform Source;
        public ReplayPuppet Puppet;
        public readonly Dictionary<string, Transform> Nodes = new();
        public readonly Dictionary<Material, Material> Mats = new();
    }

    /// <summary>
    /// Record-time build: copy the gorilla's visible meshes and skeleton, and fill in the track's
    /// paths. Returns the live transforms to sample each frame (same order as track.Paths).
    /// </summary>
    public static ReplayPuppet BuildFromLive(ReplayTrack track, VRRig rig, out Transform[] liveParts)
    {
        Transform src = rig.transform;
        Ctx ctx = Begin(track, src);

        var rendererPaths = new List<string>();
        var moving = new List<Transform>();
        var movingSet = new HashSet<Transform>();

        void AddMoving(Transform t)
        {
            // The part and everything above it (up to the root) can move.
            var chain = new List<Transform>();
            Transform c = t;
            while (c != null && c != src)
            {
                chain.Add(c);
                c = c.parent;
            }
            if (c != src)
                return; // not part of this gorilla
            for (int i = chain.Count - 1; i >= 0; i--)
                if (movingSet.Add(chain[i]))
                    moving.Add(chain[i]);
        }

        foreach (Renderer r in src.GetComponentsInChildren<Renderer>(false))
        {
            if (r == null || !r.enabled || r.forceRenderingOff)
                continue;
            if (r is not SkinnedMeshRenderer && r is not MeshRenderer)
                continue;

            string path = PathOf(r.transform, src);
            if (path == null)
                continue;

            if (CopyRenderer(ctx, r, path))
            {
                rendererPaths.Add(path);
                if (r is MeshRenderer)
                    AddMoving(r.transform);   // e.g. held items or cosmetics that move on their own
                if (r is SkinnedMeshRenderer smr)
                {
                    if (smr.rootBone != null) AddMoving(smr.rootBone);
                    foreach (Transform b in smr.bones)
                        if (b != null) AddMoving(b);
                }
            }
        }

        Transform head = rig.head != null && rig.head.rigTarget != null ? rig.head.rigTarget
            : rig.headMesh != null ? rig.headMesh.transform : null;
        Transform lh = rig.leftHand != null ? rig.leftHand.rigTarget : null;
        Transform rh = rig.rightHand != null ? rig.rightHand.rigTarget : null;
        if (head != null) AddMoving(head);
        if (lh != null) AddMoving(lh);
        if (rh != null) AddMoving(rh);

        track.RendererPaths = rendererPaths.ToArray();
        track.HeadPath = PathOf(head, src) ?? "";
        track.LeftHandPath = PathOf(lh, src) ?? "";
        track.RightHandPath = PathOf(rh, src) ?? "";
        track.MainSkinPath = rig.mainSkin != null ? PathOf(rig.mainSkin.transform, src) ?? "" : "";

        var paths = new List<string>();
        var live = new List<Transform>();
        foreach (Transform t in moving)
        {
            string p = PathOf(t, src);
            if (string.IsNullOrEmpty(p)) continue;
            paths.Add(p);
            live.Add(t);
        }
        track.Paths = paths.ToArray();
        liveParts = live.ToArray();

        return Finish(ctx, track);
    }

    /// <summary>Load-time build: rebuild the gorilla from saved paths, using your own gorilla as the model.</summary>
    public static ReplayPuppet BuildFromSaved(ReplayTrack track, VRRig model)
    {
        Transform src = model != null ? model.transform : null;
        Ctx ctx = Begin(track, src);

        if (src != null)
        {
            foreach (string path in track.RendererPaths)
            {
                Transform t = Find(src, path);
                if (t == null) continue;
                Renderer r = t.GetComponent<SkinnedMeshRenderer>();
                if (r == null) r = t.GetComponent<MeshRenderer>();
                // Name tags come from your own gorilla here, so they'd show your name: skip them.
                if (r != null && r.GetComponent<TMP_Text>() == null)
                    CopyRenderer(ctx, r, path);
            }

            // Tint the body with the player's colour.
            if (!string.IsNullOrEmpty(track.MainSkinPath) && ctx.Nodes.TryGetValue(track.MainSkinPath, out Transform skin))
            {
                Renderer r = skin.GetComponent<Renderer>();
                if (r != null && r.sharedMaterials.Length > 0 && r.sharedMaterials[0] != null)
                {
                    Material[] mats = r.sharedMaterials;
                    Material m = new Material(mats[0]);
                    ctx.Puppet.Owned.Add(m);
                    m.color = track.Color;
                    if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", track.Color);
                    mats[0] = m;
                    r.sharedMaterials = mats;
                }
            }
        }

        foreach (string p in track.Paths)
            Node(ctx, p);

        return Finish(ctx, track);
    }

    private static Ctx Begin(ReplayTrack track, Transform source)
    {
        GameObject root = new GameObject("Replay " + track.Name);
        root.SetActive(false);
        root.transform.SetParent(Container, false);
        if (source != null)
        {
            root.layer = source.gameObject.layer;
            root.transform.SetPositionAndRotation(source.position, source.rotation);
            root.transform.localScale = source.localScale;
        }

        Ctx ctx = new Ctx { Source = source, Puppet = root.AddComponent<ReplayPuppet>() };
        ctx.Puppet.Track = track;
        ctx.Nodes[""] = root.transform;
        return ctx;
    }

    private static ReplayPuppet Finish(Ctx ctx, ReplayTrack track)
    {
        ReplayPuppet p = ctx.Puppet;
        p.Parts = new Transform[track.Paths.Length];
        for (int i = 0; i < track.Paths.Length; i++)
            p.Parts[i] = Node(ctx, track.Paths[i]);

        p.HeadNode = string.IsNullOrEmpty(track.HeadPath) ? null : Node(ctx, track.HeadPath);
        p.LeftHandNode = string.IsNullOrEmpty(track.LeftHandPath) ? null : Node(ctx, track.LeftHandPath);
        p.RightHandNode = string.IsNullOrEmpty(track.RightHandPath) ? null : Node(ctx, track.RightHandPath);

        track.Puppet = p;
        return p;
    }

    /// <summary>Get or create the puppet transform for a path (copying the source's rest pose if we have one).</summary>
    private static Transform Node(Ctx ctx, string path)
    {
        if (path == null) return null;
        if (ctx.Nodes.TryGetValue(path, out Transform n))
            return n;

        int slash = path.LastIndexOf('/');
        string parentPath = slash < 0 ? "" : path.Substring(0, slash);
        string seg = slash < 0 ? path : path.Substring(slash + 1);
        Transform parent = Node(ctx, parentPath);

        int hash = seg.LastIndexOf('#');
        string name = hash > 0 ? seg.Substring(0, hash) : seg;

        GameObject go = new GameObject(name);
        Transform t = go.transform;
        t.SetParent(parent, false);

        Transform src = ctx.Source != null ? Find(ctx.Source, path) : null;
        if (src != null)
        {
            go.layer = src.gameObject.layer;
            src.GetLocalPositionAndRotation(out Vector3 lp, out Quaternion lq);
            t.SetLocalPositionAndRotation(lp, lq);
            t.localScale = src.localScale;
        }
        else
        {
            go.layer = parent.gameObject.layer;
        }

        ctx.Nodes[path] = t;
        return t;
    }

    private static bool CopyRenderer(Ctx ctx, Renderer r, string path)
    {
        if (r is SkinnedMeshRenderer smr)
        {
            if (smr.sharedMesh == null) return false;

            Transform node = Node(ctx, path);
            SkinnedMeshRenderer copy = node.gameObject.AddComponent<SkinnedMeshRenderer>();
            copy.sharedMesh = smr.sharedMesh;
            copy.sharedMaterials = CopyMaterials(ctx, smr.sharedMaterials);

            Transform[] bones = smr.bones;
            Transform[] mapped = new Transform[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                string bp = bones[i] != null && ctx.Source != null ? PathOf(bones[i], ctx.Source) : null;
                mapped[i] = bp != null ? Node(ctx, bp) : null;
            }
            copy.bones = mapped;

            if (smr.rootBone != null && ctx.Source != null)
            {
                string rp = PathOf(smr.rootBone, ctx.Source);
                if (rp != null) copy.rootBone = Node(ctx, rp);
            }

            copy.localBounds = smr.localBounds;
            copy.updateWhenOffscreen = true;
            copy.quality = smr.quality;
            copy.shadowCastingMode = smr.shadowCastingMode;
            copy.receiveShadows = smr.receiveShadows;

            if (smr.sharedMesh.blendShapeCount > 0)
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                    copy.SetBlendShapeWeight(i, smr.GetBlendShapeWeight(i));
            return true;
        }

        if (r is MeshRenderer mr)
        {
            MeshFilter mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return false;

            Transform node = Node(ctx, path);
            Mesh mesh = mf.sharedMesh;

            // Text (name tags) regenerates its mesh when the text changes, so keep our own copy.
            if (mr.GetComponent<TMP_Text>() != null)
            {
                mesh = Object.Instantiate(mesh);
                ctx.Puppet.Owned.Add(mesh);
            }

            node.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer copy = node.gameObject.AddComponent<MeshRenderer>();
            copy.sharedMaterials = CopyMaterials(ctx, mr.sharedMaterials);
            copy.shadowCastingMode = mr.shadowCastingMode;
            copy.receiveShadows = mr.receiveShadows;
            return true;
        }

        return false;
    }

    /// <summary>Per-player material instances (body colour etc.) are copied so the puppet keeps its look
    /// even if the game later reuses that gorilla for someone else. Shared materials are reused.</summary>
    private static Material[] CopyMaterials(Ctx ctx, Material[] mats)
    {
        Material[] result = new Material[mats.Length];
        for (int i = 0; i < mats.Length; i++)
        {
            Material m = mats[i];
            if (m == null) continue;

            if (m.name.Contains("(Clone)") || m.name.Contains("(Instance)"))
            {
                if (!ctx.Mats.TryGetValue(m, out Material copy))
                {
                    copy = new Material(m);
                    ctx.Mats[m] = copy;
                    ctx.Puppet.Owned.Add(copy);
                }
                result[i] = copy;
            }
            else
            {
                result[i] = m;
            }
        }
        return result;
    }
}
