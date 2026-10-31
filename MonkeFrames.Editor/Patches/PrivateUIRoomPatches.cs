using MonkeFrames.Editor.Components;
using Unity.Cinemachine;
using HarmonyLib;

namespace MonkeFrames.Editor.Patches;

[HarmonyPatch(typeof(PrivateUIRoom), nameof(PrivateUIRoom.FindShoulderCamera))]
public class PrivateUIRoomPatches
{
    static bool Prefix(PrivateUIRoom __instance, ref bool __result)
    {
        // Alarm wake-up can ask for the shoulder camera before the editor exists.
        // Let the game initialize its own references until our camera is ready.
        var manager = CameraManager.Instance;
        if (manager == null) return true;
        var camera = manager.Camera != null ? manager.Camera : manager.GetComponent<UnityEngine.Camera>();
        var virtualCamera = manager.transform.Find("CM vcam1");
        if (camera == null || virtualCamera == null) return true;
        #pragma warning disable CS0618

        PrivateUIRoom._shoulderCameraReference = camera;
        PrivateUIRoom._virtualCameraReference = virtualCamera.GetComponent<CinemachineVirtualCamera>();

        __result = true;
        return false;
    }
}
