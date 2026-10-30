using System;
using System.Collections.Generic;
using HarmonyLib;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace MonkeFrames.Editor.Replays;

// Added to an inactive ancestor BEFORE cloning a cached prefab, so its Awake,
// OnSpawn and OnEnable can all distinguish playback from a network-owned item.
internal sealed class ReplayCosmeticContext : MonoBehaviour
{
    public VRRig Template;
    public ReplayPuppet Puppet;
    private readonly List<TransferrableObject> items = new();
    private static readonly ConditionalWeakTable<TransferrableObject, ReplayCosmeticContext> owners = new();

    public static ReplayCosmeticContext For(Component component)
    {
        // Keep the scope during teardown even if Unity destroys the ancestor marker
        // before calling the item's OnDestroy. Weak keys do not retain dead items.
        if (component is TransferrableObject item && owners.TryGetValue(item, out var owner)) return owner;
        return component != null ? component.GetComponentInParent<ReplayCosmeticContext>(true) : null;
    }

    public void Initialize(TransferrableObject item)
    {
        if (!owners.TryGetValue(item, out _)) owners.Add(item, this);
        if (!items.Contains(item)) items.Add(item);
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
        // Custom item scripts may read rig/anchor metadata. The replay owns the
        // pose, so it must never follow the live template's dock or spawn views.
        item.targetRigSet = false;
        item.IsSpawned = true;
        Transform t = item.transform;
        Vector3 position = t.localPosition, scale = t.localScale;
        Quaternion rotation = t.localRotation;
        item.SetInitMatrix();
        t.SetLocalPositionAndRotation(position, rotation); t.localScale = scale;
        item.positionInitialized = true;
    }

    private void LateUpdate()
    {
        // Base OnEnable normally registers these with the game's triggered-update
        // loop. Playback owns that loop instead, avoiding local ownership validation.
        foreach (var item in items)
        {
            if (item == null || !item.isActiveAndEnabled) continue;
            try { item.LateUpdateReplicated(); item.LateUpdateShared(); }
            catch (Exception ex)
            {
                item.enabled = false;
                Console.WriteLine($"[MonkeFrames::Replay] {item.GetType().Name} visual update failed: {ex}");
            }
        }
    }
}

[HarmonyPatch]
internal static class ReplayTransferLifecycle
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        // Derived cosmetics still run their own visual initialization/updates.
        // Suppress only the base operations that register network ownership,
        // manipulate a live rig, or move the item out of the replay hierarchy.
        foreach (string name in new[] { "OnEnable", "OnDisable", "OnDestroy", "OnSpawn", "OnDespawn",
            "OnEnable_AfterAllCosmeticsSpawnedOrIsSceneObject", "UpdateFollowXform", "SpawnShareableObject",
            "SpawnTransferableObjectViews", "OnWorldShareableItemSpawn", "LateUpdateLocal", "LateUpdateShared",
            "LateUpdateReplicated", "LateUpdateReplicatedSceneObject" })
        {
            var method = AccessTools.DeclaredMethod(typeof(TransferrableObject), name);
            if (method != null) yield return method;
        }
    }
    private static bool Prefix(TransferrableObject __instance, System.Reflection.MethodBase __originalMethod)
    {
        var context = ReplayCosmeticContext.For(__instance);
        if (ReferenceEquals(context, null)) return true;
        if (__originalMethod.Name is "OnEnable" or "OnSpawn" or "OnEnable_AfterAllCosmeticsSpawnedOrIsSceneObject")
            context.Initialize(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(TransferrableObject), "Awake")]
internal static class ReplayTransferAwake
{
    private static void Prefix(TransferrableObject __instance)
    {
        var context = ReplayCosmeticContext.For(__instance);
        if (!ReferenceEquals(context, null)) context.Initialize(__instance);
    }
}
