using System;
using GorillaLocomotion;
using GorillaNetworking;
using MonkeFrames.Editor.Classes;
using UnityEngine;

namespace MonkeFrames.Editor.Components;

/// <summary>
/// Playing on desktop (no VR headset): the gorilla floats where it is instead of falling (no gravity), so it
/// doesn't drop out of the world when a map is switched or the map under it unloads, and the game never kicks
/// you for being AFK. Both are settings (Settings > Desktop mode).
/// </summary>
[DefaultExecutionOrder(10000)]   // after CameraManager, which also sets the AFK kick flag
public class DesktopMode : MonoBehaviour
{
    public static DesktopMode Instance { get; private set; }

    /// <summary>No VR headset is in use.</summary>
    public static bool IsDesktop
    {
        get
        {
            try { return !UnityEngine.XR.XRSettings.isDeviceActive; }
            catch { return false; }
        }
    }

    private bool _gravityOff;
    private Action<GTPlayer> _noGravity;

    private void Awake()
    {
        Instance = this;
        // our override replaces the game's gravity; a gentle brake stops any drift
        _noGravity = p =>
        {
            Rigidbody rb = p.playerRigidBody;
            if (rb != null && !rb.isKinematic) rb.linearVelocity *= 0.85f;
        };
    }

    private void LateUpdate()
    {
        Settings s = Settings.current;
        bool desktop = IsDesktop;

        // anti AFK kick: always on desktop (and whenever the MonkeFrames camera is in use, as before)
        try
        {
            if (desktop && (s?.DesktopAntiKick ?? true) && PhotonNetworkController.Instance != null)
                PhotonNetworkController.Instance.disableAFKKick = true;
        }
        catch { }

        bool wantNoGravity = desktop && (s?.DesktopNoGravity ?? true);
        GTPlayer player = GTPlayer.Instance;
        if (player == null) return;
        if (wantNoGravity && !_gravityOff)
        {
            player.SetGravityOverride(this, _noGravity);
            Rigidbody rb = player.playerRigidBody;
            if (rb != null && !rb.isKinematic) rb.linearVelocity = Vector3.zero;
            _gravityOff = true;
        }
        else if (!wantNoGravity && _gravityOff)
        {
            player.UnsetGravityOverride(this);
            _gravityOff = false;
        }
    }

    private void OnDestroy()
    {
        try { if (_gravityOff && GTPlayer.Instance != null) GTPlayer.Instance.UnsetGravityOverride(this); } catch { }
    }
}
