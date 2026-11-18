using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using GorillaTag.CosmeticSystem;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace MonkeFrames.Editor.Replays;

// Game cosmetics on replay gorillas (from MonkeFrames 2.1): each cosmetic prefab is loaded once and shared by every
// replay gorilla wearing it, and its scripts (TransferrableObject: holdables, wearables that animate) run as if a
// real player wore it, without networking or grabbing anything from the live game.

/// <summary>Cosmetic prefabs loaded once and shared; released when the last replay gorilla using one goes away.</summary>
internal static class ReplayCosmeticCache
{
    internal sealed class Lease : IDisposable
    {
        internal object Key;
        public AsyncOperationHandle<GameObject> Handle;

        public void Dispose()
        {
            if (Key == null) return;
            if (Entries.TryGetValue(Key, out Entry e) && --e.References == 0)
            {
                Entries.Remove(Key);
                if (e.Handle.IsValid()) Addressables.Release(e.Handle);
            }
            Key = null;
        }
    }

    private sealed class Entry
    {
        public AsyncOperationHandle<GameObject> Handle;
        public int References;
    }

    private static readonly Dictionary<object, Entry> Entries = new();

    public static Lease Acquire(object key)
    {
        if (!Entries.TryGetValue(key, out Entry e))
        {
            e = new Entry { Handle = Addressables.LoadAssetAsync<GameObject>(key) };
            Entries.Add(key, e);
        }
        e.References++;
        return new Lease { Key = key, Handle = e.Handle };
    }
}

/// <summary>
/// Sits on a replay cosmetic: its TransferrableObjects belong to the replay gorilla (they think the local rig is
/// their owner, but never network, sync or attach to it) and update their visuals every frame.
/// </summary>
internal sealed class ReplayCosmeticContext : MonoBehaviour
{
    public VRRig Template;
    public ReplayPuppet Puppet;

    private readonly List<TransferrableObject> _items = new();
    private static readonly ConditionalWeakTable<TransferrableObject, ReplayCosmeticContext> Owners = new();

    public static ReplayCosmeticContext For(Component component)
    {
        if (component is TransferrableObject t && Owners.TryGetValue(t, out ReplayCosmeticContext owner))
            return owner;
        return component != null ? component.GetComponentInParent<ReplayCosmeticContext>(true) : null;
    }

    public void Initialize(TransferrableObject item)
    {
        if (!Owners.TryGetValue(item, out _)) Owners.Add(item, this);
        if (!_items.Contains(item)) _items.Add(item);
        item.isSceneObject = false;
        item.ownerRig = Template;
        if (Template != null)
        {
            if (Template.isOfflineVRRig) item.myRig = Template;
            else item.myOnlineRig = Template;
            item.targetRig = Template;
            item.targetDockPositions = Template.GetComponent<BodyDockPositions>();
            item.anchorOverrides = Template.GetComponent<VRRigAnchorOverrides>();
        }
        item.targetRigSet = false;
        item.IsSpawned = true;
        // SetInitMatrix moves the object; keep it where the replay put it
        Transform t = item.transform;
        Vector3 pos = t.localPosition, scale = t.localScale;
        Quaternion rot = t.localRotation;
        item.SetInitMatrix();
        t.SetLocalPositionAndRotation(pos, rot);
        t.localScale = scale;
        item.positionInitialized = true;
    }

    private void LateUpdate()
    {
        foreach (TransferrableObject item in _items)
        {
            if (item == null || !item.isActiveAndEnabled) continue;
            try
            {
                item.LateUpdateReplicated();
                item.LateUpdateShared();
            }
            catch (Exception ex)
            {
                item.enabled = false;
                Console.WriteLine($"[MonkeFrames::Replay] {item.GetType().Name} visual update failed: {ex.Message}");
            }
        }
    }
}

/// <summary>A replay cosmetic's TransferrableObject is set up by its replay context, not by the game.</summary>
[HarmonyPatch(typeof(TransferrableObject), "Awake")]
internal static class ReplayTransferAwake
{
    private static void Prefix(TransferrableObject __instance)
    {
        ReplayCosmeticContext.For(__instance)?.Initialize(__instance);
    }
}

/// <summary>The game's own lifecycle / networking / follow code never runs for replay cosmetics.</summary>
[HarmonyPatch]
internal static class ReplayTransferLifecycle
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        string[] names =
        {
            "OnEnable", "OnDisable", "OnDestroy", "OnSpawn", "OnDespawn", "OnEnable_AfterAllCosmeticsSpawnedOrIsSceneObject",
            "UpdateFollowXform", "SpawnShareableObject", "SpawnTransferableObjectViews", "OnWorldShareableItemSpawn",
            "LateUpdateLocal", "LateUpdateShared", "LateUpdateReplicated", "LateUpdateReplicatedSceneObject",
        };
        foreach (string name in names)
        {
            MethodInfo m = AccessTools.DeclaredMethod(typeof(TransferrableObject), name);
            if (m != null) yield return m;
        }
    }

    private static bool Prefix(TransferrableObject __instance, MethodBase __originalMethod)
    {
        ReplayCosmeticContext ctx = ReplayCosmeticContext.For(__instance);
        if (ctx == null) return true;
        // (a cosmetic's own overrides of these still run; only the shared base code, which networks and
        // attaches to rigs, is skipped)
        switch (__originalMethod.Name)
        {
            case "OnEnable":
            case "OnSpawn":
            case "OnEnable_AfterAllCosmeticsSpawnedOrIsSceneObject":
                ctx.Initialize(__instance);
                break;
        }
        return false;
    }
}

/// <summary>Where the player's eyes are relative to the head bone (stored in replays for compatibility).</summary>
internal static class ReplayRigBones
{
    public static Transform Head(VRRig rig)
    {
        if (rig == null) return null;
        try
        {
            if (GTHardCodedBones.TryGetBoneXforms(rig, out Transform[] bones, out _))
            {
                int i = GTHardCodedBones.GetBoneIndex(GTHardCodedBones.EBone.head);
                if (i >= 0 && i < bones.Length && bones[i] != null) return bones[i];
            }
        }
        catch { }
        if (rig.mainSkin != null)
            foreach (Transform b in rig.mainSkin.bones)
                if (b != null && string.Equals(b.name, "head", StringComparison.OrdinalIgnoreCase)) return b;
        return rig.head?.rigTarget != null ? rig.head.rigTarget : rig.transform;
    }

    public static void EyeOffset(VRRig rig, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        Transform head = Head(rig);
        SphereCollider eye = rig != null && rig.isOfflineVRRig && GorillaTagger.Instance != null ? GorillaTagger.Instance.headCollider : null;
        if (head == null || eye == null) return;
        position = head.InverseTransformPoint(eye.transform.position);
        rotation = Quaternion.Inverse(head.rotation) * eye.transform.rotation;
    }
}
