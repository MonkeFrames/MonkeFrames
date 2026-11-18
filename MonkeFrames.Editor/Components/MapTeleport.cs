using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GorillaLocomotion;
using GorillaNetworking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MonkeFrames.Editor.Components;

/// <summary>
/// Every Gorilla Tag map (zone), loaded on request: the map's scene is loaded and the MonkeFrames camera flies to
/// the map's spawn point. Your gorilla is moved there too only outside public lobbies (in a public lobby a
/// teleport looks like cheating to the game's anti-cheat and to other players).
/// </summary>
public static class MapTeleport
{
    /// <summary>A map you can go to.</summary>
    public sealed class Map
    {
        public GTZone Zone;
        public string Name;
    }

    private static readonly HashSet<GTZone> Skip = new() { GTZone.none, GTZone.customMaps, GTZone.tutorial };

    /// <summary>Friendly names for zones whose enum name isn't nice to read.</summary>
    private static readonly Dictionary<GTZone, string> Names = new()
    {
        { GTZone.forest, "Forest" }, { GTZone.city, "City" }, { GTZone.basement, "Basement" }, { GTZone.canyon, "Canyons" },
        { GTZone.beach, "Beach" }, { GTZone.mountain, "Mountains" }, { GTZone.skyJungle, "Clouds" }, { GTZone.cave, "Caves" },
        { GTZone.cityWithSkyJungle, "City + Clouds" }, { GTZone.rotating, "Rotating" }, { GTZone.Metropolis, "Metropolis" },
        { GTZone.cityNoBuildings, "City (no buildings)" }, { GTZone.attic, "Attic" }, { GTZone.arcade, "Arcade" },
        { GTZone.bayou, "Bayou" }, { GTZone.monkeBlocks, "Monke Blocks" }, { GTZone.mall, "Mall" }, { GTZone.mines, "Mines" },
        { GTZone.arena, "Arena" }, { GTZone.hoverboard, "Hoverpark" }, { GTZone.critters, "Critters" },
        { GTZone.ghostReactor, "Ghost Reactor" }, { GTZone.monkeBlocksShared, "Monke Blocks (shared)" },
        { GTZone.ghostReactorTunnel, "Ghost Reactor tunnel" }, { GTZone.ranked, "Ranked" }, { GTZone.ghostReactorDrill, "Ghost Reactor drill" },
        { GTZone.forestWithCity, "Forest + City" },
    };

    /// <summary>The maps this version of the game has (zones with a scene).</summary>
    public static List<Map> All()
    {
        var list = new List<Map>();
        try
        {
            if (ZoneManagement.instance == null) ZoneManagement.FindInstance();
            foreach (ZoneData z in ZoneManagement.instance.zones)
            {
                if (z == null || Skip.Contains(z.zone) || (string.IsNullOrEmpty(z.sceneName) && (z.rootGameObjects == null || z.rootGameObjects.Length == 0))) continue;
                if (list.Any(m => m.Zone == z.zone)) continue;
                list.Add(new Map { Zone = z.zone, Name = Names.TryGetValue(z.zone, out string n) ? n : Pretty(z.zone.ToString()) });
            }
        }
        catch (Exception ex) { Console.WriteLine($"[MonkeFrames::Maps] Couldn't list maps: {ex.Message}"); }
        return list.OrderBy(m => m.Name).ToList();
    }

    private static string Pretty(string s)
    {
        var b = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) b.Append(' ');
            b.Append(i == 0 ? char.ToUpperInvariant(s[i]) : s[i]);
        }
        return b.ToString();
    }

    public static bool Busy { get; private set; }

    /// <summary>Your gorilla can be moved without looking like cheating: offline or in a private room.</summary>
    public static bool CanMoveGorilla
    {
        get
        {
            try { return NetworkSystem.Instance == null || !NetworkSystem.Instance.InRoom || NetworkSystem.Instance.SessionIsPrivate; }
            catch { return false; }
        }
    }

    /// <summary>Load a map and go there (camera; and the gorilla when allowed and asked for).</summary>
    public static void Go(Map map, bool moveGorilla)
    {
        if (Busy || map == null || CameraManager.Instance == null) return;
        CameraManager.Instance.StartCoroutine(GoRoutine(map, moveGorilla && CanMoveGorilla));
    }

    private static IEnumerator GoRoutine(Map map, bool moveGorilla)
    {
        Busy = true;
        UIManager.Instance.Status = $"Loading {map.Name}...";
        bool started = false;
        try
        {
            ZoneManagement.SetActiveZone(map.Zone);
            started = true;
        }
        catch (Exception ex)
        {
            UIManager.Instance.Status = $"Couldn't load {map.Name}: {ex.Message}";
        }
        if (!started) { Busy = false; yield break; }

        float waited = 0f;
        while (!SafeLoaded(map.Zone) && waited < 20f) { waited += Time.unscaledDeltaTime; yield return null; }
        yield return null;   // objects in the new scene wake up
        yield return null;

        if (!SafeLoaded(map.Zone))
        {
            UIManager.Instance.Status = $"{map.Name} is taking a long time to load; try again in a moment.";
            Busy = false;
            yield break;
        }

        if (!FindSpawn(map.Zone, out Vector3 pos, out Quaternion rot))
        {
            UIManager.Instance.Status = $"Loaded {map.Name}, but couldn't find where to go in it. Fly there with the camera.";
            Busy = false;
            yield break;
        }

        // camera: a little behind and above the spawn, looking along it
        CameraManager cam = CameraManager.Instance;
        Vector3 fwd = rot * Vector3.forward; fwd.y = 0; if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward; fwd.Normalize();
        cam.CancelLookSmoothing();
        cam.Position = pos - fwd * 2.5f + Vector3.up * 1.6f;
        cam.Rotation = Quaternion.LookRotation((pos + Vector3.up * 0.8f) - cam.Position, Vector3.up);

        if (moveGorilla && GTPlayer.Instance != null)
        {
            try { GTPlayer.Instance.TeleportTo(pos + Vector3.up * 0.3f, Quaternion.LookRotation(fwd, Vector3.up)); }
            catch (Exception ex) { Console.WriteLine($"[MonkeFrames::Maps] Gorilla teleport failed: {ex.Message}"); }
        }
        UIManager.Instance.Status = moveGorilla ? $"Went to {map.Name}." : $"Camera went to {map.Name}." + (CanMoveGorilla ? "" : " (Your gorilla stays put in public lobbies.)");
        Busy = false;
    }

    private static bool SafeLoaded(GTZone zone)
    {
        try { return ZoneManagement.IsZoneLoaded(zone); } catch { return false; }
    }

    /// <summary>Where to arrive in a map: its spawn point, else its room join trigger, else the middle of the map on
    /// the ground.</summary>
    private static bool FindSpawn(GTZone zone, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero; rot = Quaternion.identity;
        try
        {
            foreach (SpawnPoint sp in UnityEngine.Object.FindObjectsByType<SpawnPoint>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (sp != null && sp.startZone == zone) { pos = sp.transform.position; rot = sp.transform.rotation; return true; }

            foreach (GorillaNetworkJoinTrigger t in UnityEngine.Object.FindObjectsByType<GorillaNetworkJoinTrigger>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (t != null && t.zone == zone)
                {
                    // just beside the trigger (not inside it, which would switch rooms)
                    Collider c = t.GetComponent<Collider>();
                    Vector3 p = c != null ? c.bounds.center : t.transform.position;
                    if (Ground(p + t.transform.forward * ((c != null ? c.bounds.extents.magnitude : 1f) + 2f), out pos)) { rot = t.transform.rotation; return true; }
                }

            ZoneData data = ZoneManagement.instance.GetZoneData(zone);
            var bounds = new Bounds();
            bool any = false;
            if (data?.rootGameObjects != null)
                foreach (GameObject root in data.rootGameObjects)
                    if (root != null)
                        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(false))
                        {
                            if (!any) { bounds = r.bounds; any = true; } else bounds.Encapsulate(r.bounds);
                        }
            if (!any)
            {
                Scene scene = SceneManager.GetSceneByName(data?.sceneName ?? "");
                if (scene.IsValid() && scene.isLoaded)
                    foreach (GameObject root in scene.GetRootGameObjects())
                        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(false))
                        {
                            if (!any) { bounds = r.bounds; any = true; } else bounds.Encapsulate(r.bounds);
                        }
            }
            if (any && Ground(bounds.center, out pos)) return true;
            if (any) { pos = bounds.center; return true; }
        }
        catch (Exception ex) { Console.WriteLine($"[MonkeFrames::Maps] Spawn lookup failed: {ex.Message}"); }
        return false;
    }

    private static bool Ground(Vector3 p, out Vector3 ground)
    {
        ground = p;
        if (Physics.Raycast(p + Vector3.up * 30f, Vector3.down, out RaycastHit hit, 200f, ~0, QueryTriggerInteraction.Ignore))
        {
            ground = hit.point;
            return true;
        }
        return false;
    }
}
