using MonkeFrames.Editor.Components;
using Unity.Cinemachine;
using HarmonyLib;

namespace MonkeFrames.Editor.Patches;

[HarmonyPatch(typeof(PrivateUIRoom), nameof(PrivateUIRoom.FindShoulderCamera))]
public class PrivateUIRoomPatches
{
    static bool Prefix(PrivateUIRoom __instance, ref bool __result)
    {
        #pragma warning disable CS0618

        PrivateUIRoom._shoulderCameraReference = CameraManager.Instance.Camera;
        PrivateUIRoom._virtualCameraReference = CameraManager.Instance.gameObject.transform.Find("CM vcam1").gameObject.GetComponent<CinemachineVirtualCamera>();

        __result = true;
        return false;
    }
}
