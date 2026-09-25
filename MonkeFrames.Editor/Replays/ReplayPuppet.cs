using MonkeFrames.Editor.Components;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using GorillaNetworking;
using GorillaTag.CosmeticSystem;
using System.Collections;

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
    internal readonly List<GameObject> AddressableCosmetics = new();
    internal PuppetBuilder.Ctx BuildCtx;            // lets us add cosmetics that load after recording starts

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
        foreach (GameObject instance in AddressableCosmetics)
            if (instance != null) Addressables.ReleaseInstance(instance);
        AddressableCosmetics.Clear();
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

    internal sealed class Ctx
    {
        public Transform Source;
        /// <summary>Extra gorillas to borrow parts from (loaded replays: cosmetics other rigs have loaded).</summary>
        public readonly List<Transform> Sources = new();
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
        var rendererCosmeticIds = new List<string>();
        var cosmeticByRenderer = CosmeticRendererIds(rig);
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

        HashSet<Renderer> worn = WornRenderers(rig);
        foreach (Renderer r in src.GetComponentsInChildren<Renderer>(false))
        {
            if (!Include(r, worn))
                continue;

            string path = PathOf(r.transform, src);
            if (path == null)
                continue;

            if (CopyRenderer(ctx, r, path, src))
            {
                rendererPaths.Add(path);
                rendererCosmeticIds.Add(cosmeticByRenderer.TryGetValue(r, out string cosmeticId) ? cosmeticId : "");
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
        track.RendererCosmeticIds = rendererCosmeticIds.ToArray();
        track.HeadPath = PathOf(head, src) ?? "";
        track.LeftHandPath = PathOf(lh, src) ?? "";
        track.RightHandPath = PathOf(rh, src) ?? "";
        track.MainSkinPath = rig.mainSkin != null ? PathOf(rig.mainSkin.transform, src) ?? "" : "";
        track.CosmeticIds = CosmeticIds(rig);

        // Replay pose data must include the skeleton anchors cosmetics attach to, even when
        // those anchors do not happen to be bones of one of the captured renderers.
        try
        {
            if (GTHardCodedBones.TryGetBoneXforms(rig, out Transform[] boneXforms, out _))
                foreach (CosmeticInfoV2 info in CosmeticInfos(track.CosmeticIds))
                    foreach (CosmeticPart part in CosmeticParts(info))
                        if (part.attachAnchors != null)
                            foreach (CosmeticAttachInfo anchor in part.attachAnchors)
                            {
                                int index = GTHardCodedBones.GetBoneIndex(anchor.parentBone);
                                if (index >= 0 && index < boneXforms.Length) AddMoving(boneXforms[index]);
                            }
        }
        catch (System.Exception ex) { System.Console.WriteLine($"[MonkeFrames::Replay] Cosmetic anchors unavailable while recording: {ex.Message}"); }

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

    /// <summary>
    /// Which renderers belong in a replay copy: everything visible on the gorilla, plus every
    /// cosmetic they're wearing even if the game has temporarily hidden it (Gorilla Tag hides
    /// cosmetics of players behind you / far away in busy lobbies to save performance).
    /// </summary>
    private static bool Include(Renderer r, HashSet<Renderer> worn)
    {
        if (r == null || !r.gameObject.activeInHierarchy)
            return false;
        if (r is not SkinnedMeshRenderer && r is not MeshRenderer)
            return false;
        if (worn.Contains(r))
            return true;
        return r.enabled && !r.forceRenderingOff;
    }

    private static HashSet<Renderer> WornRenderers(VRRig rig)
    {
        var set = new HashSet<Renderer>();
        try
        {
            if (rig.mainSkin != null) set.Add(rig.mainSkin);
            var items = rig.cosmeticSet.items;
            var registry = rig.cosmeticsObjectRegistry;
            if (items != null && registry != null)
                foreach (var item in items)
                {
                    if (string.IsNullOrEmpty(item.displayName)) continue;
                    var inst = registry.Cosmetic(item.displayName);
                    if (inst?.allRenderers == null) continue;
                    foreach (Renderer r in inst.allRenderers)
                        if (r != null) set.Add(r);
                }
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"[MonkeFrames::Replay] Cosmetic lookup failed: {ex.Message}");
        }
        return set;
    }

    private static string[] CosmeticIds(VRRig rig)
    {
        var ids = new List<string>();
        try
        {
            foreach (var item in rig.cosmeticSet.items)
                if (!string.IsNullOrEmpty(item.displayName) && !ids.Contains(item.displayName))
                    ids.Add(item.displayName);
        }
        catch { }
        return ids.ToArray();
    }

    private static IEnumerable<CosmeticInfoV2> CosmeticInfos(IEnumerable<string> ids)
    {
        CosmeticsController controller = CosmeticsController.instance;
        if (controller == null || ids == null) yield break;
        foreach (string id in ids)
        {
            CosmeticSO so = controller.GetCosmeticSOFromDisplayName(id);
            if (so != null) yield return so.info;
        }
    }

    private static IEnumerable<CosmeticPart> CosmeticParts(CosmeticInfoV2 info)
    {
        if (info.holdableParts != null) foreach (var part in info.holdableParts) yield return part;
        if (info.wardrobeParts != null) foreach (var part in info.wardrobeParts) yield return part;
        if (info.storeParts != null) foreach (var part in info.storeParts) yield return part;
        if (info.functionalParts != null) foreach (var part in info.functionalParts) yield return part;
    }

    /// <summary>Instantiate the actual game cosmetic prefabs on the replay's matching bone anchors.</summary>
    public static IEnumerator LoadCosmetics(ReplayTrack track, ReplayPuppet puppet, VRRig rig)
    {
        if (track?.CosmeticIds == null || puppet == null || rig == null)
            yield break;
        if (!GTHardCodedBones.TryGetBoneXforms(rig, out Transform[] sourceBones, out string error))
        {
            if (!string.IsNullOrEmpty(error)) System.Console.WriteLine($"[MonkeFrames::Replay] Could not resolve cosmetic anchors: {error}");
            yield break;
        }

        foreach (CosmeticInfoV2 info in CosmeticInfos(track.CosmeticIds))
        foreach (CosmeticPart part in CosmeticParts(info))
        {
            if (part.prefabAssetRef == null || part.attachAnchors == null) continue;
            foreach (CosmeticAttachInfo attach in part.attachAnchors)
            {
                int boneIndex = GTHardCodedBones.GetBoneIndex(attach.parentBone);
                if (boneIndex < 0 || boneIndex >= sourceBones.Length || sourceBones[boneIndex] == null) continue;

                string bonePath = PathOf(sourceBones[boneIndex], rig.transform);
                if (string.IsNullOrEmpty(bonePath)) continue;
                Transform parent = Node(puppet.BuildCtx, bonePath);
                var operation = part.prefabAssetRef.InstantiateAsync(parent, true);
                while (!operation.IsDone) yield return null;
                GameObject instance = operation.Result;
                if (instance == null)
                {
                    System.Console.WriteLine($"[MonkeFrames::Replay] Cosmetic prefab failed to load: {info.displayName}");
                    continue;
                }

                instance.transform.SetParent(parent, false);
                instance.transform.localPosition = attach.offset.pos;
                instance.transform.localRotation = attach.offset.rot;
                instance.transform.localScale = attach.offset.scale;
                puppet.AddressableCosmetics.Add(instance);
            }
        }
    }

    private static Dictionary<Renderer, string> CosmeticRendererIds(VRRig rig)
    {
        var result = new Dictionary<Renderer, string>();
        try
        {
            var registry = rig.cosmeticsObjectRegistry;
            foreach (var item in rig.cosmeticSet.items)
            {
                if (string.IsNullOrEmpty(item.displayName)) continue;
                var cosmetic = registry != null ? registry.Cosmetic(item.displayName) : null;
                if (cosmetic?.allRenderers == null) continue;
                foreach (Renderer renderer in cosmetic.allRenderers)
                    if (renderer != null) result[renderer] = item.displayName;
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// While recording: copy any cosmetics that appeared after the gorilla was first seen
    /// (cosmetics load in a moment after someone joins, or they change outfit).
    /// </summary>
    public static int AddNewRenderers(ReplayTrack track, VRRig rig)
    {
        ReplayPuppet p = track.Puppet;
        if (p == null || p.BuildCtx == null || rig == null) return 0;

        Transform src = rig.transform;
        var known = new HashSet<string>(track.RendererPaths);
        var added = new List<string>();
        var addedCosmeticIds = new List<string>();
        HashSet<Renderer> worn = WornRenderers(rig);
        var cosmeticByRenderer = CosmeticRendererIds(rig);

        foreach (Renderer r in src.GetComponentsInChildren<Renderer>(false))
        {
            if (!Include(r, worn)) continue;
            string path = PathOf(r.transform, src);
            if (path == null || known.Contains(path)) continue;
            if (CopyRenderer(p.BuildCtx, r, path, src))
            {
                known.Add(path);
                added.Add(path);
                addedCosmeticIds.Add(cosmeticByRenderer.TryGetValue(r, out string cosmeticId) ? cosmeticId : "");
            }
        }

        if (added.Count > 0)
        {
            var all = new List<string>(track.RendererPaths);
            all.AddRange(added);
            track.RendererPaths = all.ToArray();
            var allCosmeticIds = new List<string>(track.RendererCosmeticIds);
            allCosmeticIds.AddRange(addedCosmeticIds);
            track.RendererCosmeticIds = allCosmeticIds.ToArray();
        }
        return added.Count;
    }

    /// <summary>
    /// Load-time build: rebuild the gorilla from saved paths. Parts come from your own gorilla, or
    /// from any other gorilla in the game that has that cosmetic loaded.
    /// </summary>
    public static ReplayPuppet BuildFromSaved(ReplayTrack track, VRRig model)
    {
        Transform src = model != null ? model.transform : null;
        Ctx ctx = Begin(track, src);
        var matchingSources = new List<Transform>();
        var otherSources = new List<Transform>();
        if (src != null) otherSources.Add(src);
        try
        {
            foreach (VRRig other in VRRigCache.Instance.GetAllRigs())
                if (other != null && other.transform != src)
                {
                    bool hasRecordedCosmetic = false;
                    string[] wornIds = CosmeticIds(other);
                    foreach (string id in track.CosmeticIds)
                        if (System.Array.IndexOf(wornIds, id) >= 0) { hasRecordedCosmetic = true; break; }
                    (hasRecordedCosmetic ? matchingSources : otherSources).Add(other.transform);
                }
        }
        catch { }

        // A path can exist on every gorilla. Prefer the rig that actually wears one of the
        // recorded IDs so a local hat or shirt can never be substituted for the replay item.
        ctx.Sources.AddRange(matchingSources);
        ctx.Sources.AddRange(otherSources);

        if (ctx.Sources.Count > 0)
        {
            for (int rendererIndex = 0; rendererIndex < track.RendererPaths.Length; rendererIndex++)
            {
                string path = track.RendererPaths[rendererIndex];
                string requiredCosmetic = rendererIndex < track.RendererCosmeticIds.Length ? track.RendererCosmeticIds[rendererIndex] : "";
                // Recorded cosmetics are instantiated from their saved game asset below. Never
                // substitute another rig's renderer at the same hierarchy path.
                if (!string.IsNullOrEmpty(requiredCosmetic)) continue;
                foreach (Transform source in ctx.Sources)
                {
                    Transform t = Find(source, path);
                    if (t == null) continue;
                    Renderer r = t.GetComponent<SkinnedMeshRenderer>();
                    if (r == null) r = t.GetComponent<MeshRenderer>();
                    if (r == null) continue;
                    // Name tags would show someone else's name: skip them.
                    if (r.GetComponent<TMP_Text>() != null) break;
                    if (CopyRenderer(ctx, r, path, source)) break;
                }
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
        ctx.Puppet.BuildCtx = ctx;
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
        if (src == null)
            foreach (Transform other in ctx.Sources)
                if (other != ctx.Source && (src = Find(other, path)) != null)
                    break;
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

    private static bool CopyRenderer(Ctx ctx, Renderer r, string path, Transform srcRoot)
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
                string bp = bones[i] != null && srcRoot != null ? PathOf(bones[i], srcRoot) : null;
                mapped[i] = bp != null ? Node(ctx, bp) : null;
            }
            copy.bones = mapped;

            if (smr.rootBone != null && srcRoot != null)
            {
                string rp = PathOf(smr.rootBone, srcRoot);
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
