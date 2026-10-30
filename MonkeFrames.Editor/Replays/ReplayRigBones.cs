using System;
using GorillaTag.CosmeticSystem;
using UnityEngine;

namespace MonkeFrames.Editor.Replays;

internal static class ReplayRigBones
{
    public static Transform Head(VRRig rig)
    {
        if (rig == null) return null;
        if (GTHardCodedBones.TryGetBoneXforms(rig, out var bones, out _))
        {
            int index = GTHardCodedBones.GetBoneIndex(GTHardCodedBones.EBone.head);
            if (index >= 0 && index < bones.Length && bones[index] != null) return bones[index];
        }
        if (rig.mainSkin != null)
            foreach (var bone in rig.mainSkin.bones)
                if (bone != null && string.Equals(bone.name, "head", StringComparison.OrdinalIgnoreCase)) return bone;
        return rig.head?.rigTarget != null ? rig.head.rigTarget : rig.transform;
    }

    public static void EyeOffset(out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero; rotation = Quaternion.identity;
        var local = Components.CameraModes.LocalRig();
        var head = Head(local);
        var eye = GorillaTagger.Instance != null ? GorillaTagger.Instance.headCollider : null;
        if (head == null || eye == null) return;
        position = head.InverseTransformPoint(eye.transform.position);
        rotation = Quaternion.Inverse(head.rotation) * eye.transform.rotation;
    }
}
