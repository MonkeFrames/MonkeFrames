using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using GorillaNetworking;
using GorillaTag.CosmeticSystem;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Components;

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
    /// <summary>The gorilla's visible head mesh, and a point in the middle of the head (0.15 m up it): what every
    /// camera mode aims at / sits in. The recorded head target can be missing or off (your own gorilla).</summary>
    public Transform HeadMeshNode, HeadCentreNode;

    internal readonly List<UnityEngine.Object> Owned = new();   // copied materials / meshes to clean up
    internal readonly List<GameObject> AddressableCosmetics = new();
    /// <summary>Shared cosmetic prefabs this gorilla uses (released when it goes away).</summary>
    internal readonly Dictionary<object, ReplayCosmeticCache.Lease> CosmeticLeases = new();
    /// <summary>The name above the gorilla's head.</summary>
    internal TextMeshPro NameTag;
    internal PuppetBuilder.Ctx BuildCtx;            // lets us add cosmetics that load after recording starts

    public override string DisplayName => Track?.Name ?? "Gorilla";
    public override bool IsLocal => Track != null && Track.IsLocal;
    public override bool IsReplay => true;
    public override bool Available => this != null && gameObject.activeInHierarchy;
    public override Transform Head => HeadCentreNode != null ? HeadCentreNode : HeadNode != null ? HeadNode : transform;
    public override Transform Hand(bool right)
    {
        Transform t = right ? RightHandNode : LeftHandNode;
        return t != null ? t : Head;
    }

    private void LateUpdate()
    {
        // (renaming a gorilla in the Replays window updates its name tag)
        if (NameTag != null && NameTag.text != DisplayName) NameTag.text = DisplayName;
    }

    /// <summary>Free copied materials / meshes (OnDestroy isn't called for objects that were never active).</summary>
    public void ReleaseOwned()
    {
        foreach (GameObject instance in AddressableCosmetics)
            if (instance != null) { instance.SetActive(false); Destroy(instance); }
        AddressableCosmetics.Clear();
        foreach (ReplayCosmeticCache.Lease lease in CosmeticLeases.Values) lease.Dispose();
        CosmeticLeases.Clear();
        foreach (UnityEngine.Object o in Owned)
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
                UnityEngine.Object.DontDestroyOnLoad(go);
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
        /// <summary>What each copied renderer was made from (so the replay file can store it).</summary>
        public readonly Dictionary<string, RendRec> Records = new();
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
        HashSet<Renderer> ownHead = OwnHeadItems(rig);
        var liveRenderers = new List<Renderer>();
        foreach (Renderer r in src.GetComponentsInChildren<Renderer>(true))
        {
            if (!Include(r, worn, ownHead) || IsNameTag(rig, r))
                continue;

            string path = PathOf(r.transform, src);
            if (path == null)
                continue;

            if (CopyRenderer(ctx, r, path, src))
            {
                liveRenderers.Add(r);
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

        // the fur, and where the player's eyes sit in the head (official MonkeFrames builds' first person)
        track.FurAppearance = ReplayFur.Capture(rig);
        ReplayRigBones.EyeOffset(CameraModes.LocalRig(), out track.EyeLocalPosition, out track.EyeLocalRotation);
        track.HasEyePose = true;
        // items changing size / switching on and off while recording
        ReplayEvents.Begin(track, liveParts, liveRenderers);

        ReplayPuppet puppet = Finish(ctx, track, HeadMeshPath(rig));
        CreateNameTag(ctx, rig);
        return puppet;
    }

    /// <summary>The game's name tag (a replay gets its own one, CreateNameTag).</summary>
    private static bool IsNameTag(VRRig rig, Renderer r)
    {
        try { return rig.playerText1 != null && r.gameObject == rig.playerText1.gameObject; }
        catch { return false; }
    }

    /// <summary>A name tag above a replay gorilla, made like the game's own, showing the track's name.</summary>
    private static void CreateNameTag(Ctx ctx, VRRig sourceRig)
    {
        try
        {
            if (ctx?.Puppet == null || sourceRig == null || sourceRig.playerText1 == null) return;
            Transform tag = sourceRig.playerText1.transform;
            string parentPath = tag.parent != null ? PathOf(tag.parent, sourceRig.transform) : "";
            if (parentPath == null) return;
            Transform parent = Node(ctx, parentPath);
            GameObject copy = UnityEngine.Object.Instantiate(tag.gameObject, parent, false);
            copy.transform.SetLocalPositionAndRotation(tag.localPosition, tag.localRotation);
            copy.transform.localScale = tag.localScale;
            SetVisibleLayers(copy, parent.gameObject.layer);
            TextMeshPro text = copy.GetComponent<TextMeshPro>();
            if (text == null) { UnityEngine.Object.Destroy(copy); return; }
            // nothing but the text: no game scripts updating it with someone else's name
            foreach (MonoBehaviour b in copy.GetComponentsInChildren<MonoBehaviour>(true))
                if (b != null && b is not TMP_Text) UnityEngine.Object.Destroy(b);
            text.text = ctx.Puppet.Track?.Name ?? "Gorilla";
            text.enabled = true;
            if (text.renderer != null) text.renderer.forceRenderingOff = false;
            copy.SetActive(true);
            ctx.Puppet.NameTag = text;
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[MonkeFrames::Replay] Could not create a replay name tag: {ex.Message}");
        }
    }

    /// <summary>Path of a gorilla's head mesh below its root (the same in every gorilla).</summary>
    internal static string HeadMeshPath(VRRig rig)
    {
        try { return rig != null && rig.headMesh != null ? PathOf(rig.headMesh.transform, rig.transform) : null; }
        catch { return null; }
    }

    /// <summary>
    /// Which renderers belong in a replay copy: everything visible on the gorilla, plus every
    /// cosmetic they're wearing even if the game has temporarily hidden it (Gorilla Tag hides
    /// cosmetics of players behind you / far away in busy lobbies to save performance).
    /// </summary>
    private static bool Include(Renderer r, HashSet<Renderer> worn, HashSet<Renderer> ownHead = null)
    {
        if (r == null)
            return false;
        if (r is not SkinnedMeshRenderer && r is not MeshRenderer)
            return false;
        // Your own hats, face items and face are always kept, even while the first person camera (or the game)
        // is hiding them.
        if (ownHead != null && ownHead.Contains(r))
            return true;
        // A worn cosmetic counts even while the game hides it: Gorilla Tag switches off your own hat / face
        // items (they'd fill your VR view) and far-away players' cosmetics. Only parts switched off on
        // their own (another state of the cosmetic) stay out.
        if (worn.Contains(r))
            return r.gameObject.activeSelf;
        return r.gameObject.activeInHierarchy && r.enabled && (!r.forceRenderingOff || CameraModes.HiddenByFirstPerson(r));
    }

    /// <summary>
    /// Your own gorilla's hat and face cosmetics and the parts of its head (face, eyes): the first person camera
    /// hides these from itself, and the game hides some of your own head items, but a replay must keep them.
    /// Only for your own gorilla. A hat that's hidden as a whole counts with all its parts; otherwise only the
    /// parts that are switched on (so a cosmetic's other states don't show up as well).
    /// </summary>
    internal static HashSet<Renderer> OwnHeadItems(VRRig rig)
    {
        var set = new HashSet<Renderer>();
        try
        {
            if (rig == null || !rig.isOfflineVRRig) return set;
            var registry = rig.cosmeticsObjectRegistry;
            if (registry != null && rig.cosmeticSet?.items != null)
                foreach (var item in rig.cosmeticSet.items)
                {
                    if (string.IsNullOrEmpty(item.displayName)) continue;
                    if (item.itemCategory != CosmeticsController.CosmeticCategory.Hat &&
                        item.itemCategory != CosmeticsController.CosmeticCategory.Face) continue;
                    var inst = registry.Cosmetic(item.displayName);
                    if (inst?.allRenderers == null) continue;
                    bool anyOn = false;
                    foreach (Renderer r in inst.allRenderers)
                        if (r != null && r.gameObject.activeSelf) { anyOn = true; break; }
                    foreach (Renderer r in inst.allRenderers)
                        if (r != null && (!anyOn || r.gameObject.activeSelf)) set.Add(r);
                }
            if (rig.headMesh != null)
                foreach (Renderer r in rig.headMesh.GetComponentsInChildren<Renderer>(true))
                    if (r != null && r != rig.mainSkin && r.gameObject.activeSelf) set.Add(r);
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[MonkeFrames::Replay] Head cosmetic lookup failed: {ex.Message}");
        }
        return set;
    }

    /// <summary>
    /// When a recording stops: anything the gorilla wears that still isn't on its replay copy (for example a hat
    /// that was hidden the whole time) is copied straight from the gorilla, so it's in the saved file too.
    /// </summary>
    public static int CopyMissingWorn(ReplayTrack track, VRRig rig)
    {
        ReplayPuppet p = track?.Puppet;
        if (p == null || p.BuildCtx == null || rig == null || rig.cosmeticsObjectRegistry == null) return 0;
        int total = 0;
        try
        {
            Transform src = rig.transform;
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < track.RendererPaths.Length && i < track.RendererCosmeticIds.Length; i++)
                if (!string.IsNullOrEmpty(track.RendererCosmeticIds[i]) && p.BuildCtx.Records.ContainsKey(track.RendererPaths[i]))
                    have.Add(track.RendererCosmeticIds[i]);

            var paths = new List<string>(track.RendererPaths);
            var ids = new List<string>(track.RendererCosmeticIds);
            while (ids.Count < paths.Count) ids.Add("");
            foreach (var item in rig.cosmeticSet.items)
            {
                string id = item.displayName;
                if (string.IsNullOrEmpty(id) || have.Contains(id)) continue;
                var inst = rig.cosmeticsObjectRegistry.Cosmetic(id);
                if (inst?.allRenderers == null || inst.allRenderers.Count == 0) continue;
                bool anyOn = false;
                foreach (Renderer r in inst.allRenderers)
                    if (r != null && r.gameObject.activeSelf) { anyOn = true; break; }
                int copied = 0;
                foreach (Renderer r in inst.allRenderers)
                {
                    if (r == null || (anyOn && !r.gameObject.activeSelf)) continue;
                    if (r is not SkinnedMeshRenderer && r is not MeshRenderer) continue;
                    if (r.GetComponent<TMP_Text>() != null) continue;
                    string path = PathOf(r.transform, src);
                    if (path == null || p.BuildCtx.Records.ContainsKey(path)) continue;
                    if (CopyRenderer(p.BuildCtx, r, path, src))
                    {
                        copied++;
                        paths.Add(path);
                        ids.Add(id);
                    }
                }
                total += copied;
                System.Console.WriteLine(copied > 0
                    ? $"[MonkeFrames::Replay] {track.Name}: \"{id}\" wasn't recorded, copied it from the gorilla ({copied} parts)"
                    : $"[MonkeFrames::Replay] {track.Name}: \"{id}\" wasn't recorded and couldn't be copied (it's loaded from the game when the replay opens)");
            }
            if (total > 0)
            {
                track.RendererPaths = paths.ToArray();
                track.RendererCosmeticIds = ids.ToArray();
                var allIds = new List<string>(track.CosmeticIds ?? Array.Empty<string>());
                foreach (var item in rig.cosmeticSet.items)
                    if (!string.IsNullOrEmpty(item.displayName) && !allIds.Contains(item.displayName)) allIds.Add(item.displayName);
                track.CosmeticIds = allIds.ToArray();
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[MonkeFrames::Replay] Copying missing cosmetics failed: {ex.Message}");
        }
        return total;
    }

    internal static HashSet<Renderer> WornRenderers(VRRig rig)
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
            if (string.IsNullOrEmpty(id)) continue;
            CosmeticSO so = controller.GetCosmeticSOFromDisplayName(id);
            if (so != null)
            {
                yield return so.info;
                continue;
            }

            if (controller.TryGetCosmeticInfoV2(id, out CosmeticInfoV2 info))
            {
                yield return info;
                continue;
            }

            if (controller.v2_allCosmetics != null)
            {
                foreach (CosmeticInfoV2 v2 in controller.v2_allCosmetics)
                {
                    if (string.Equals(v2.displayName, id, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(v2.playFabID, id, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return v2;
                        break;
                    }
                }
            }
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

        // The cosmetics catalogue has to be loaded before any cosmetic can be looked up (MonkeFrames dev fix)
        CosmeticsController controller = CosmeticsController.instance;
        if (controller != null && !controller.v2_allCosmeticsInfoAssetRef_isLoaded)
        {
            IEnumerator loadCatalog = null;
            try { loadCatalog = controller.V2_allCosmeticsInfoAssetRefSO_LoadCoroutine(); }
            catch (Exception ex) { Console.WriteLine($"[MonkeFrames::Replay] Could not load the cosmetics catalogue: {ex.Message}"); }
            if (loadCatalog != null)
                yield return controller.StartCoroutine(loadCatalog);
        }
        if (puppet == null) yield break;

        if (!GTHardCodedBones.TryGetBoneXforms(rig, out Transform[] sourceBones, out string error))
        {
            if (!string.IsNullOrEmpty(error)) System.Console.WriteLine($"[MonkeFrames::Replay] Could not resolve cosmetic anchors: {error}");
            yield break;
        }

        var loadedCosmeticIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Cosmetics already rebuilt from the replay file's stored looks (or copied from a gorilla wearing them)
        var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (track.RendererPaths != null && track.RendererCosmeticIds != null && puppet.BuildCtx != null)
            for (int i = 0; i < track.RendererPaths.Length && i < track.RendererCosmeticIds.Length; i++)
                if (!string.IsNullOrEmpty(track.RendererCosmeticIds[i]) && puppet.BuildCtx.Records.ContainsKey(track.RendererPaths[i]))
                    copied.Add(track.RendererCosmeticIds[i]);
        bool live = Settings.current?.ReplayLiveCosmetics ?? true;

        foreach (CosmeticInfoV2 info in CosmeticInfos(track.CosmeticIds))
        {
            string id = info.displayName ?? "";
            bool hasCopy = copied.Contains(id);
            // Holdables keep their recorded copy: it follows the hands exactly as recorded (and their size and
            // parts switching on / off). Wearables become the real game cosmetic, so they animate and react like
            // they do in the game ("Animated cosmetics in replays"); without that, the exact copy is used.
            bool holdable = info.holdableParts != null && info.holdableParts.Length > 0;
            // Clothing that bends with the body (shirts, hoodies, pants...) always keeps its exact recorded copy:
            // re-skinning the game's version onto a replay gorilla's bones can stretch it.
            bool copySkinned = false;
            if (hasCopy)
                for (int i = 0; i < track.RendererPaths.Length && i < track.RendererCosmeticIds.Length && !copySkinned; i++)
                    if (string.Equals(track.RendererCosmeticIds[i], id, StringComparison.OrdinalIgnoreCase)
                        && puppet.BuildCtx.Nodes.TryGetValue(track.RendererPaths[i], out Transform cn) && cn != null
                        && cn.GetComponent<SkinnedMeshRenderer>() != null)
                        copySkinned = true;
            if (hasCopy && (!live || holdable || copySkinned)) { loadedCosmeticIds.Add(id); continue; }
            bool loadedAnyPart = false, keepCopy = false;
            var spawned = new List<GameObject>();
            foreach (CosmeticPart part in CosmeticParts(info))
            {
                if (keepCopy) break;
                if (part.prefabAssetRef == null || !part.prefabAssetRef.RuntimeKeyIsValid() || part.attachAnchors == null) continue;
                object key = part.prefabAssetRef.RuntimeKey;
                if (!puppet.CosmeticLeases.TryGetValue(key, out ReplayCosmeticCache.Lease lease))
                {
                    lease = ReplayCosmeticCache.Acquire(key);
                    puppet.CosmeticLeases.Add(key, lease);
                }
                var operation = lease.Handle;
                while (!operation.IsDone) yield return null;
                if (puppet == null) yield break;
                if (operation.Status != UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded || operation.Result == null)
                {
                    System.Console.WriteLine($"[MonkeFrames::Replay] Cosmetic prefab failed to load: {id}");
                    puppet.CosmeticLeases.Remove(key);
                    lease.Dispose();
                    continue;
                }
                // same rule for the game's version: anything skinned stays the recorded copy
                if (hasCopy && operation.Result.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0)
                {
                    keepCopy = true;
                    break;
                }
                foreach (CosmeticAttachInfo attach in part.attachAnchors)
                {
                    int boneIndex = GTHardCodedBones.GetBoneIndex(attach.parentBone);
                    if (boneIndex < 0 || boneIndex >= sourceBones.Length || sourceBones[boneIndex] == null) continue;

                    string bonePath = PathOf(sourceBones[boneIndex], rig.transform);
                    if (string.IsNullOrEmpty(bonePath)) continue;
                    Transform parent = Node(puppet.BuildCtx, bonePath);

                    // Built switched off, under a context that makes its scripts belong to this replay gorilla
                    GameObject holder = new GameObject("Replay Cosmetic " + id);
                    holder.SetActive(false);
                    holder.transform.SetParent(parent, false);
                    holder.layer = parent.gameObject.layer;
                    puppet.AddressableCosmetics.Add(holder);
                    spawned.Add(holder);
                    ReplayCosmeticContext context = holder.AddComponent<ReplayCosmeticContext>();
                    context.Template = rig;
                    context.Puppet = puppet;

                    GameObject instance = UnityEngine.Object.Instantiate(operation.Result, holder.transform, false);
                    instance.transform.SetLocalPositionAndRotation(attach.offset.pos, attach.offset.rot);
                    instance.transform.localScale = attach.offset.scale;
                    SetVisibleLayers(instance, parent.gameObject.layer);
                    foreach (TransferrableObject item in instance.GetComponentsInChildren<TransferrableObject>(true))
                    {
                        try
                        {
                            context.Initialize(item);
                            item.OnSpawn(rig);
                        }
                        catch (Exception ex)
                        {
                            item.enabled = false;
                            System.Console.WriteLine($"[MonkeFrames::Replay] {item.GetType().Name} couldn't start: {ex.Message}");
                        }
                    }

                    // no physics: it only looks like the cosmetic
                    foreach (Collider col in instance.GetComponentsInChildren<Collider>(true)) col.enabled = false;
                    foreach (Rigidbody rb in instance.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.detectCollisions = false; }

                    // Rebind any SkinnedMeshRenderers (clothing, shirts, badges) to puppet bones
                    RebindSkinnedMeshes(puppet.BuildCtx, instance);
                    holder.SetActive(true);
                    loadedAnyPart = true;
                }
            }
            if (keepCopy)
            {
                // part of it bends with the body: the whole cosmetic stays the exact recorded copy
                foreach (GameObject h in spawned)
                {
                    puppet.AddressableCosmetics.Remove(h);
                    if (h != null) UnityEngine.Object.Destroy(h);
                }
                loadedCosmeticIds.Add(id);
            }
            else if (loadedAnyPart && !string.IsNullOrEmpty(id))
            {
                loadedCosmeticIds.Add(id);
                // the real cosmetic replaces the still copy
                if (hasCopy)
                    for (int i = 0; i < track.RendererPaths.Length && i < track.RendererCosmeticIds.Length; i++)
                        if (string.Equals(track.RendererCosmeticIds[i], id, StringComparison.OrdinalIgnoreCase)
                            && puppet.BuildCtx.Nodes.TryGetValue(track.RendererPaths[i], out Transform copy) && copy != null)
                        {
                            Renderer cr = copy.GetComponent<Renderer>();
                            if (cr != null) cr.forceRenderingOff = true;
                        }
            }
            else if (hasCopy) loadedCosmeticIds.Add(id);
        }

        // Fallback for non-Addressable cosmetics or cosmetics without asset refs:
        // Copy their saved renderer paths from available rigs in the scene
        if (track.RendererPaths != null && track.RendererCosmeticIds != null && puppet.BuildCtx != null)
        {
            for (int i = 0; i < track.RendererPaths.Length; i++)
            {
                string cosmeticId = i < track.RendererCosmeticIds.Length ? track.RendererCosmeticIds[i] : "";
                if (string.IsNullOrEmpty(cosmeticId) || loadedCosmeticIds.Contains(cosmeticId)) continue;

                string path = track.RendererPaths[i];
                if (puppet.BuildCtx.Records.ContainsKey(path)) continue;
                if (puppet.BuildCtx.Nodes.TryGetValue(path, out Transform existing) && existing != null && existing.GetComponent<Renderer>() != null) continue;

                foreach (Transform source in puppet.BuildCtx.Sources)
                {
                    Transform t = Find(source, path);
                    if (t == null) continue;
                    Renderer r = t.GetComponent<SkinnedMeshRenderer>() ?? (Renderer)t.GetComponent<MeshRenderer>();
                    if (r == null || r.GetComponent<TMP_Text>() != null) continue;
                    // only from a gorilla actually wearing that cosmetic (another item can sit at the same spot)
                    VRRig owner = source.GetComponent<VRRig>();
                    if (owner == null || !CosmeticRendererIds(owner).TryGetValue(r, out string ownerId)
                        || !string.Equals(ownerId, cosmeticId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (CopyRenderer(puppet.BuildCtx, r, path, source)) break;
                }
            }
        }
    }

    private static void RebindSkinnedMeshes(Ctx ctx, GameObject instance)
    {
        if (ctx == null || instance == null) return;
        foreach (SkinnedMeshRenderer smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr.bones == null || smr.bones.Length == 0) continue;
            Transform[] remapped = new Transform[smr.bones.Length];
            for (int b = 0; b < smr.bones.Length; b++)
            {
                if (smr.bones[b] == null) continue;
                string bName = smr.bones[b].name;
                foreach (var kv in ctx.Nodes)
                {
                    if (kv.Value != null && kv.Value.name == bName)
                    {
                        remapped[b] = kv.Value;
                        break;
                    }
                }
            }
            smr.bones = remapped;

            if (smr.rootBone != null)
            {
                string rName = smr.rootBone.name;
                foreach (var kv in ctx.Nodes)
                {
                    if (kv.Value != null && kv.Value.name == rName)
                    {
                        smr.rootBone = kv.Value;
                        break;
                    }
                }
            }
            if (smr.rootBone == null && remapped.Length > 0 && remapped[0] != null)
                smr.rootBone = remapped[0];

            smr.updateWhenOffscreen = true;
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
        HashSet<Renderer> ownHead = OwnHeadItems(rig);
        var cosmeticByRenderer = CosmeticRendererIds(rig);

        foreach (Renderer r in src.GetComponentsInChildren<Renderer>(true))
        {
            if (!Include(r, worn, ownHead) || IsNameTag(rig, r)) continue;
            string path = PathOf(r.transform, src);
            if (path == null || known.Contains(path)) continue;
            if (CopyRenderer(p.BuildCtx, r, path, src))
            {
                ReplayEvents.AddRenderer(track, r);
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

            var currentCosmeticIds = new HashSet<string>(track.CosmeticIds ?? System.Array.Empty<string>());
            foreach (var id in addedCosmeticIds)
                if (!string.IsNullOrEmpty(id)) currentCosmeticIds.Add(id);
            track.CosmeticIds = new List<string>(currentCosmeticIds).ToArray();
        }
        return added.Count;
    }

    /// <summary>
    /// Load-time build: rebuild the gorilla from saved paths. Parts come from your own gorilla, or
    /// from any other gorilla in the game that has that cosmetic loaded.
    /// </summary>
    public static ReplayPuppet BuildFromSaved(ReplayTrack track, VRRig model, LookBank bank = null, TrackLook look = null)
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

        int fromGame = 0, fromFile = 0, missing = 0;
        {
            for (int rendererIndex = 0; rendererIndex < track.RendererPaths.Length; rendererIndex++)
            {
                string path = track.RendererPaths[rendererIndex];
                bool done = false, isText = false;
                RendLook saved = look?.Find(path);
                // Cosmetics saved in the file are rebuilt exactly as recorded; everything else
                // (the gorilla body) is borrowed from a gorilla in the game.
                bool preferFile = saved != null && bank != null && saved.Mesh >= 0 && saved.Mesh < bank.Meshes.Count && bank.Meshes[saved.Mesh].Stored;
                // A cosmetic the replay only knows by its ID (no saved look) is loaded from the game's own
                // asset afterwards (PuppetBuilder.LoadCosmetics): never substitute another rig's renderer
                // at the same hierarchy path.
                string requiredCosmetic = track.RendererCosmeticIds != null && rendererIndex < track.RendererCosmeticIds.Length ? track.RendererCosmeticIds[rendererIndex] : "";
                if (saved == null && !string.IsNullOrEmpty(requiredCosmetic)) continue;
                if (!preferFile)
                    foreach (Transform source in ctx.Sources)
                    {
                        Transform t = Find(source, path);
                        if (t == null) continue;
                        Renderer r = t.GetComponent<SkinnedMeshRenderer>();
                        if (r == null) r = t.GetComponent<MeshRenderer>();
                        if (r == null) continue;
                        // Name tags would show someone else's name: skip them.
                        if (r.GetComponent<TMP_Text>() != null) { isText = true; break; }
                        if (CopyRenderer(ctx, r, path, source)) { done = true; fromGame++; break; }
                    }
                if (!done && !isText && saved != null && bank != null)
                {
                    if (FromBank(ctx, bank, saved)) { done = true; fromFile++; }
                }
                if (!done && !isText) missing++;
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

        // Put every part back exactly where it was on that gorilla.
        if (look != null)
            foreach (NodePose n in look.Nodes)
                if (ctx.Nodes.TryGetValue(n.Path, out Transform t) && t != null && t != ctx.Puppet.transform)
                {
                    t.SetLocalPositionAndRotation(n.Pos, n.Rot);
                    t.localScale = n.Scale;
                }

        if (fromFile > 0 || missing > 0)
            System.Console.WriteLine($"[MonkeFrames::Replay] {track.Name}: {fromGame} parts from the game, {fromFile} from the replay file, {missing} missing");
        ReplayPuppet built = Finish(ctx, track, HeadMeshPath(model));
        CreateNameTag(ctx, model);
        return built;
    }

    /// <summary>Rebuild one renderer from the mesh / materials stored in the replay file.</summary>
    private static bool FromBank(Ctx ctx, LookBank bank, RendLook saved)
    {
        Mesh mesh = bank.GetMesh(saved.Mesh);
        if (mesh == null) return false;
        Material[] mats = new Material[saved.Mats.Length];
        for (int i = 0; i < mats.Length; i++) mats[i] = bank.GetMat(saved.Mats[i]);

        Transform node = Node(ctx, saved.Path);
        if (saved.Skinned)
        {
            SkinnedMeshRenderer copy = node.gameObject.AddComponent<SkinnedMeshRenderer>();
            copy.sharedMesh = mesh;
            copy.sharedMaterials = mats;
            Transform[] bones = new Transform[saved.Bones.Length];
            for (int i = 0; i < bones.Length; i++)
                bones[i] = string.IsNullOrEmpty(saved.Bones[i]) ? null : Node(ctx, saved.Bones[i]);
            copy.bones = bones;
            if (!string.IsNullOrEmpty(saved.RootBone)) copy.rootBone = Node(ctx, saved.RootBone);
            copy.localBounds = new Bounds(saved.Center, saved.Extents * 2f);
            copy.updateWhenOffscreen = true;
        }
        else
        {
            node.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            node.gameObject.AddComponent<MeshRenderer>().sharedMaterials = mats;
        }

        ctx.Records[saved.Path] = new RendRec
        {
            Skinned = saved.Skinned, Mesh = mesh, Materials = mats, BonePaths = saved.Bones, RootBonePath = saved.RootBone,
            Bounds = new Bounds(saved.Center, saved.Extents * 2f),
        };
        return true;
    }

    private static Ctx Begin(ReplayTrack track, Transform source)
    {
        GameObject root = new GameObject("Replay " + track.Name);
        root.SetActive(false);
        root.transform.SetParent(Container, false);
        if (source != null)
        {
            root.layer = VisibleLayer(source.gameObject.layer, 0);
            root.transform.SetPositionAndRotation(source.position, source.rotation);
            root.transform.localScale = source.localScale;
        }

        Ctx ctx = new Ctx { Source = source, Puppet = root.AddComponent<ReplayPuppet>() };
        ctx.Puppet.Track = track;
        ctx.Puppet.BuildCtx = ctx;
        ctx.Nodes[""] = root.transform;
        return ctx;
    }

    private static ReplayPuppet Finish(Ctx ctx, ReplayTrack track, string headMeshPath = null)
    {
        ReplayPuppet p = ctx.Puppet;
        p.Parts = new Transform[track.Paths.Length];
        for (int i = 0; i < track.Paths.Length; i++)
            p.Parts[i] = Node(ctx, track.Paths[i]);

        p.HeadNode = string.IsNullOrEmpty(track.HeadPath) ? null : Node(ctx, track.HeadPath);
        if (!string.IsNullOrEmpty(headMeshPath))
        {
            // (the head mesh rides on the recorded head bone, so it's exactly where the head is drawn)
            p.HeadMeshNode = Node(ctx, headMeshPath);
            GameObject centre = new GameObject("MonkeFrames Head Centre");
            centre.layer = p.HeadMeshNode.gameObject.layer;
            centre.transform.SetParent(p.HeadMeshNode, false);
            centre.transform.localPosition = new Vector3(0f, 0.15f, 0f);
            p.HeadCentreNode = centre.transform;
        }
        p.LeftHandNode = string.IsNullOrEmpty(track.LeftHandPath) ? null : Node(ctx, track.LeftHandPath);
        p.RightHandNode = string.IsNullOrEmpty(track.RightHandPath) ? null : Node(ctx, track.RightHandPath);

        track.Puppet = p;
        return p;
    }

    private static int _mirrorOnly = -2;

    /// <summary>A layer every camera draws: Gorilla Tag puts your own head cosmetics on "MirrorOnly" so only mirrors
    /// show them; a replay copy must be seen by the camera.</summary>
    private static int VisibleLayer(int layer, int fallback)
    {
        if (_mirrorOnly == -2) _mirrorOnly = LayerMask.NameToLayer("MirrorOnly");
        if (_mirrorOnly < 0 || layer != _mirrorOnly) return layer;
        return fallback != _mirrorOnly ? fallback : 0;
    }

    private static void SetVisibleLayers(GameObject go, int fallback)
    {
        foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            t.gameObject.layer = VisibleLayer(t.gameObject.layer, fallback);
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
            go.layer = VisibleLayer(src.gameObject.layer, parent.gameObject.layer);
            src.GetLocalPositionAndRotation(out Vector3 lp, out Quaternion lq);
            t.SetLocalPositionAndRotation(lp, lq);
            t.localScale = src.localScale;
        }
        else
        {
            go.layer = VisibleLayer(parent.gameObject.layer, 0);
            t.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            t.localScale = Vector3.one;
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
            copy.sharedMaterials = BakeBlocks(ctx, smr, CopyMaterials(ctx, smr.sharedMaterials));

            Transform[] bones = smr.bones;
            Transform[] mapped = new Transform[bones.Length];
            string[] bonePaths = new string[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                string bp = bones[i] != null && srcRoot != null ? PathOf(bones[i], srcRoot) : null;
                bonePaths[i] = bp ?? "";
                mapped[i] = bp != null ? Node(ctx, bp) : null;
                if (mapped[i] == null && bones[i] != null)
                {
                    string bName = bones[i].name;
                    foreach (var kv in ctx.Nodes)
                    {
                        if (kv.Value != null && kv.Value.name == bName)
                        {
                            mapped[i] = kv.Value;
                            break;
                        }
                    }
                }
            }
            copy.bones = mapped;

            string rootPath = "";
            if (smr.rootBone != null && srcRoot != null)
            {
                string rp = PathOf(smr.rootBone, srcRoot);
                if (rp != null) { copy.rootBone = Node(ctx, rp); rootPath = rp; }
            }
            if (copy.rootBone == null && smr.rootBone != null)
            {
                string rName = smr.rootBone.name;
                foreach (var kv in ctx.Nodes)
                {
                    if (kv.Value != null && kv.Value.name == rName)
                    {
                        copy.rootBone = kv.Value;
                        break;
                    }
                }
            }
            if (copy.rootBone == null && copy.bones != null && copy.bones.Length > 0)
                copy.rootBone = copy.bones[0];

            ctx.Records[path] = new RendRec
            {
                Skinned = true, Mesh = smr.sharedMesh, Materials = copy.sharedMaterials,
                BonePaths = bonePaths, RootBonePath = rootPath, Bounds = smr.localBounds,
            };

            copy.localBounds = smr.localBounds;
            copy.updateWhenOffscreen = true;
            copy.quality = smr.quality;
            copy.shadowCastingMode = smr.shadowCastingMode;
            copy.receiveShadows = smr.receiveShadows;
            copy.forceRenderingOff = false;
            copy.enabled = true;

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
                mesh = UnityEngine.Object.Instantiate(mesh);
                ctx.Puppet.Owned.Add(mesh);
            }

            node.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer copy = node.gameObject.AddComponent<MeshRenderer>();
            copy.sharedMaterials = BakeBlocks(ctx, mr, CopyMaterials(ctx, mr.sharedMaterials));
            copy.shadowCastingMode = mr.shadowCastingMode;
            copy.receiveShadows = mr.receiveShadows;
            copy.forceRenderingOff = false;
            copy.enabled = true;
            if (mr.GetComponent<TMP_Text>() == null)
                ctx.Records[path] = new RendRec { Mesh = mesh, Materials = copy.sharedMaterials, Bounds = mesh.bounds };
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gorilla Tag sets some cosmetic colours / atlas slices per renderer (MaterialPropertyBlock)
    /// instead of in the material. Bake those into our own material copies so the replay keeps them.
    /// </summary>
    private static Material[] BakeBlocks(Ctx ctx, Renderer r, Material[] mats)
    {
        try
        {
            if (!r.HasPropertyBlock()) return mats;
            var whole = new MaterialPropertyBlock();
            r.GetPropertyBlock(whole);
            var slot = new MaterialPropertyBlock();
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                slot.Clear();
                r.GetPropertyBlock(slot, i);
                if (whole.isEmpty && slot.isEmpty) continue;

                Material m = new Material(mats[i]) { name = mats[i].name.Replace(" (Instance)", "") + " (Instance)" };
                ctx.Puppet.Owned.Add(m);
                Shader sh = m.shader;
                for (int p = 0; p < sh.GetPropertyCount(); p++)
                {
                    string n = sh.GetPropertyName(p);
                    foreach (MaterialPropertyBlock b in new[] { whole, slot })
                    {
                        if (b.isEmpty) continue;
                        switch (sh.GetPropertyType(p))
                        {
                            case UnityEngine.Rendering.ShaderPropertyType.Color:
                                if (b.HasColor(n)) m.SetColor(n, b.GetColor(n));
                                else if (b.HasVector(n)) m.SetVector(n, b.GetVector(n));
                                break;
                            case UnityEngine.Rendering.ShaderPropertyType.Vector:
                                if (b.HasVector(n)) m.SetVector(n, b.GetVector(n));
                                break;
                            case UnityEngine.Rendering.ShaderPropertyType.Float:
                            case UnityEngine.Rendering.ShaderPropertyType.Range:
                                if (b.HasFloat(n)) m.SetFloat(n, b.GetFloat(n));
                                break;
                            case UnityEngine.Rendering.ShaderPropertyType.Int:
                                if (b.HasInt(n)) m.SetInteger(n, b.GetInt(n));
                                else if (b.HasFloat(n)) m.SetFloat(n, b.GetFloat(n));
                                break;
                            case UnityEngine.Rendering.ShaderPropertyType.Texture:
                                if (b.HasTexture(n) && b.GetTexture(n) != null) m.SetTexture(n, b.GetTexture(n));
                                break;
                        }
                    }
                    // Texture tiling/offset lives in <name>_ST
                    if (sh.GetPropertyType(p) == UnityEngine.Rendering.ShaderPropertyType.Texture)
                        foreach (MaterialPropertyBlock b in new[] { whole, slot })
                            if (!b.isEmpty && b.HasVector(n + "_ST")) m.SetVector(n + "_ST", b.GetVector(n + "_ST"));
                }
                mats[i] = m;
            }
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"[MonkeFrames::Replay] Couldn't copy colours for {r.name}: {ex.Message}");
        }
        return mats;
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
