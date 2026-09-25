using UnityEngine;

namespace MonkeFrames.Editor.Components;

/// <summary>
/// Anything the Other Cameras can point at: a live gorilla in the lobby, or a gorilla in a replay.
/// </summary>
public abstract class CamSubject : MonoBehaviour
{
    public abstract string DisplayName { get; }
    public abstract bool IsLocal { get; }
    public virtual bool IsReplay => false;

    /// <summary>True while this subject can be filmed right now.</summary>
    public abstract bool Available { get; }

    public abstract Transform Head { get; }
    public abstract Transform Hand(bool right);

    /// <summary>The object that carries the whole gorilla (kept sharp by motion blur).</summary>
    public virtual Transform Root => transform;
}

/// <summary>A live gorilla (VRRig) in the current lobby, or yourself.</summary>
public class LiveSubject : CamSubject
{
    private VRRig _rig;

    public VRRig Rig => _rig != null ? _rig : _rig = GetComponent<VRRig>();

    public static LiveSubject For(VRRig rig)
    {
        if (rig == null) return null;
        LiveSubject s = rig.GetComponent<LiveSubject>();
        return s != null ? s : rig.gameObject.AddComponent<LiveSubject>();
    }

    public override string DisplayName => CameraModes.PlayerName(Rig);
    public override bool IsLocal => Rig != null && Rig.isOfflineVRRig;
    public override bool Available => Rig != null && Rig.isActiveAndEnabled;

    public override Transform Head
    {
        get
        {
            VRRig rig = Rig;
            if (rig.isOfflineVRRig && GorillaTagger.Instance != null && GorillaTagger.Instance.headCollider != null)
                return GorillaTagger.Instance.headCollider.transform;
            if (rig.head != null && rig.head.rigTarget != null)
                return rig.head.rigTarget;
            if (rig.headMesh != null)
                return rig.headMesh.transform;
            return rig.transform;
        }
    }

    public override Transform Hand(bool right)
    {
        VRRig rig = Rig;
        if (rig.isOfflineVRRig && GorillaTagger.Instance != null)
        {
            Transform t = right ? GorillaTagger.Instance.rightHandTransform : GorillaTagger.Instance.leftHandTransform;
            if (t != null) return t;
        }

        VRMap map = right ? rig.rightHand : rig.leftHand;
        if (map != null && map.rigTarget != null)
            return map.rigTarget;

        return Head;
    }
}
