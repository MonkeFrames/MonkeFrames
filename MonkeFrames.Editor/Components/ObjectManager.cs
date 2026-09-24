using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MonkeFrames.Compiler.Models;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.UI;
using MonkeFrames.Editor.Utilities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MonkeFrames.Editor.Components;

public class SpawnedObjectInstance
{
    public PlacedObject Data;
    public GameObject GameObject;
    public MeshFilter MeshFilter;
    public MeshRenderer Renderer;
    public LineRenderer SelectionOutline;
}

public class ObjectManager : MonoBehaviour
{
    public static ObjectManager Instance;

    public static string ObjectsFolder
    {
        get
        {
            string dir = Constants.DataFolder;
            return Path.Combine(Constants.DataFolder, "objects");
        }
    }

    private readonly Dictionary<string, Mesh> _meshCache = new(StringComparer.OrdinalIgnoreCase);
    public List<SpawnedObjectInstance> SpawnedObjects = new();
    public int SelectedIndex = -1;

    public SpawnedObjectInstance SelectedObject =>
        SelectedIndex >= 0 && SelectedIndex < SpawnedObjects.Count ? SpawnedObjects[SelectedIndex] : null;

    private Project _lastProject;

    public ObjectManager()
    {
        Instance = this;
    }

    public void Awake()
    {
        Instance = this;
        EnsureDirectoryExists();
    }

    public void Start()
    {
        Instance = this;
        EnsureDirectoryExists();
        SyncWithProject();
    }

    public void EnsureDirectoryExists()
    {
        try
        {
            string folder = ObjectsFolder;
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                Console.WriteLine($"[MonkeFrames::ObjectManager] Created Objects folder at: {folder}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::ObjectManager] Could not create Objects folder: {ex.Message}");
        }
    }

    public List<string> GetAvailableObjectFiles()
    {
        EnsureDirectoryExists();
        try
        {
            string folder = ObjectsFolder;
            if (!Directory.Exists(folder))
                return new List<string>();

            return Directory.GetFiles(folder, "*.obj", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy(name => name)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::ObjectManager] Error scanning Objects folder: {ex.Message}");
            return new List<string>();
        }
    }

    public Mesh GetOrLoadMesh(string objFileName)
    {
        if (string.IsNullOrEmpty(objFileName))
            return null;

        if (_meshCache.TryGetValue(objFileName, out Mesh cached) && cached != null)
            return cached;

        string fullPath = Path.Combine(ObjectsFolder, objFileName);
        if (!File.Exists(fullPath))
        {
            Console.WriteLine($"[MonkeFrames::ObjectManager] File not found: {fullPath}");
            return null;
        }

        try
        {
            string text = File.ReadAllText(fullPath);
            Mesh mesh = CamModel.ObjToMesh(text, out bool hasUVs);
            if (mesh != null && mesh.vertexCount > 0)
            {
                mesh.name = objFileName;
                _meshCache[objFileName] = mesh;
                return mesh;
            }

            Console.WriteLine($"[MonkeFrames::ObjectManager] .obj file '{objFileName}' produced an empty mesh.");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::ObjectManager] Failed to load/parse '{objFileName}': {ex.Message}");
            return null;
        }
    }

    private Texture2D TryLoadObjectTexture(string objFileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(objFileName);
        string folder = ObjectsFolder;

        // 1. Check if .mtl file exists and references a texture (map_Kd)
        string mtlPath = Path.Combine(folder, baseName + ".mtl");
        if (File.Exists(mtlPath))
        {
            try
            {
                foreach (string rawLine in File.ReadAllLines(mtlPath))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("map_Kd", StringComparison.OrdinalIgnoreCase))
                    {
                        string texFileName = line.Substring(6).Trim().Trim('"', '\'');
                        string texPath = Path.Combine(folder, Path.GetFileName(texFileName));
                        Console.WriteLine($"[MonkeFrames::ObjectManager] Trying to load MTL texture from: {texPath}");
                        Texture2D mtlTex = LoadTextureFromFile(texPath);
                        if (mtlTex != null)
                        {
                            Console.WriteLine($"[MonkeFrames::ObjectManager] Successfully loaded MTL texture.");
                            return mtlTex;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MonkeFrames::ObjectManager] Error reading .mtl file '{mtlPath}': {ex.Message}");
            }
        }

        // 2. Fallback: check for matching image files (e.g. objname.png, objname.jpg, objname.jpeg)
        string[] exts = new[] { ".png", ".jpg", ".jpeg" };
        foreach (string ext in exts)
        {
            string candidate = Path.Combine(folder, baseName + ext);
            Console.WriteLine($"[MonkeFrames::ObjectManager] Trying fallback texture: {candidate}");
            Texture2D imgTex = LoadTextureFromFile(candidate);
            if (imgTex != null)
            {
                Console.WriteLine($"[MonkeFrames::ObjectManager] Successfully loaded fallback texture.");
                return imgTex;
            }
        }

        return null;
    }

    private static Texture2D LoadTextureFromFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            byte[] bytes = File.ReadAllBytes(filePath);
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (tex.LoadImage(bytes, false))
            {
                tex.wrapMode = TextureWrapMode.Repeat;
                tex.filterMode = FilterMode.Bilinear;
                return tex;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::ObjectManager] Error loading texture from '{filePath}': {ex.Message}");
        }

        return null;
    }

    public SpawnedObjectInstance SpawnObject(string objFileName, Vector3? initialPos = null, Vector3? initialRot = null, Vector3? initialScale = null, PlacedObject existingData = null)
    {
        Mesh mesh = GetOrLoadMesh(objFileName);
        if (mesh == null)
        {
            UIManager.Instance.Status = $"Could not spawn '{objFileName}': Invalid or corrupted .obj file.";
            return null;
        }

        GameObject go = new GameObject($"Object: {objFileName}");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.layer = 0;

        MeshFilter mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        MeshRenderer mr = go.AddComponent<MeshRenderer>();

        Shader shader = Shader.Find("Standard")
            ?? Shader.Find("GorillaTag/UberShader")
            ?? Shader.Find("Universal Render Pipeline/Unlit")
            ?? Shader.Find("Unlit/Texture");

        Material mat = new Material(shader);
        mat.color = Color.white;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);

        // Try to load texture via .mtl file or matching image file (.png/.jpg)
        Texture2D tex = TryLoadObjectTexture(objFileName);
        if (tex != null)
        {
            mat.mainTexture = tex;
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_Tex")) mat.SetTexture("_Tex", tex);
        }

        mr.sharedMaterial = mat;

        Vector3 pos = initialPos ?? (existingData != null ? existingData.Position : (CameraManager.Instance != null ? CameraManager.Instance.Position : Vector3.zero));
        Vector3 rotVec = initialRot ?? (existingData != null ? existingData.Rotation : Vector3.zero);
        Vector3 scale = initialScale ?? (existingData != null ? existingData.Scale : Vector3.one);

        go.transform.position = pos;
        go.transform.rotation = Quaternion.Euler(rotVec);
        go.transform.localScale = scale;

        PlacedObject data = existingData;
        if (data == null)
        {
            data = new PlacedObject
            {
                ObjectName = objFileName,
                Position = pos,
                Rotation = rotVec,
                Scale = scale
            };

            Project p = KeyframeManager.Instance?.Project;
            if (p != null)
            {
                p.PlacedObjects ??= new List<PlacedObject>();
                p.PlacedObjects.Add(data);
            }
        }

        // Selection outline renderer
        GameObject outlineObj = new GameObject("SelectionOutline");
        outlineObj.transform.SetParent(go.transform, false);
        LineRenderer line = outlineObj.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.startWidth = 0.03f;
        line.endWidth = 0.03f;
        line.material = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default"));
        line.startColor = Settings.current?.AccentColor ?? Color.yellow;
        line.endColor = Settings.current?.AccentColor ?? Color.yellow;
        line.positionCount = 16;
        SetupBoundingBoxOutline(line, mesh.bounds);
        outlineObj.SetActive(false);

        SpawnedObjectInstance inst = new SpawnedObjectInstance
        {
            Data = data,
            GameObject = go,
            MeshFilter = mf,
            Renderer = mr,
            SelectionOutline = line
        };

        SpawnedObjects.Add(inst);
        SelectedIndex = SpawnedObjects.Count - 1;
        UpdateSelectionVisuals();

        UIManager.Instance.Status = $"Spawned object '{objFileName}'.";
        return inst;
    }

    private static void SetupBoundingBoxOutline(LineRenderer line, Bounds bounds)
    {
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;

        // Draw a wireframe box around bounds
        Vector3[] corners = new Vector3[]
        {
            new Vector3(min.x, min.y, min.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(min.x, max.y, min.z),
            new Vector3(min.x, min.y, min.z), // loop back front

            new Vector3(min.x, min.y, max.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(max.x, max.y, max.z),
            new Vector3(min.x, max.y, max.z),
            new Vector3(min.x, min.y, max.z), // loop back rear

            new Vector3(min.x, max.y, max.z),
            new Vector3(min.x, max.y, min.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(max.x, max.y, max.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(max.x, min.y, min.z)
        };

        line.positionCount = corners.Length;
        line.SetPositions(corners);
    }

    public void DeleteSelectedObject()
    {
        if (SelectedIndex >= 0 && SelectedIndex < SpawnedObjects.Count)
        {
            DeleteObject(SelectedIndex);
        }
    }

    public void DeleteObject(int index)
    {
        if (index < 0 || index >= SpawnedObjects.Count)
            return;

        SpawnedObjectInstance inst = SpawnedObjects[index];
        if (inst.GameObject != null)
            UnityEngine.Object.Destroy(inst.GameObject);

        Project p = KeyframeManager.Instance?.Project;
        if (p != null && p.PlacedObjects != null && inst.Data != null)
        {
            p.PlacedObjects.Remove(inst.Data);
        }

        SpawnedObjects.RemoveAt(index);
        if (SelectedIndex >= SpawnedObjects.Count)
            SelectedIndex = SpawnedObjects.Count - 1;

        UpdateSelectionVisuals();
        UIManager.Instance.Status = "Deleted object.";
    }

    public void SelectObject(int index)
    {
        SelectedIndex = index;
        UpdateSelectionVisuals();
    }

    public void UpdateSelectionVisuals()
    {
        Color accent = Settings.current?.AccentColor ?? Color.yellow;

        for (int i = 0; i < SpawnedObjects.Count; i++)
        {
            SpawnedObjectInstance inst = SpawnedObjects[i];
            bool selected = i == SelectedIndex;

            if (inst.SelectionOutline != null)
            {
                inst.SelectionOutline.gameObject.SetActive(selected);
                if (selected)
                {
                    inst.SelectionOutline.startColor = accent;
                    inst.SelectionOutline.endColor = accent;
                }
            }
        }
    }

    public void ApplyTransformToSelected(Vector3 pos, Vector3 rot, Vector3 scale)
    {
        SpawnedObjectInstance sel = SelectedObject;
        if (sel == null || sel.GameObject == null)
            return;

        sel.GameObject.transform.position = pos;
        sel.GameObject.transform.rotation = Quaternion.Euler(rot);
        sel.GameObject.transform.localScale = scale;

        if (sel.Data != null)
        {
            sel.Data.Position = pos;
            sel.Data.Rotation = rot;
            sel.Data.Scale = scale;
        }
    }

    public void MoveSelectedToCamera()
    {
        SpawnedObjectInstance sel = SelectedObject;
        if (sel == null || sel.GameObject == null)
            return;

        Vector3 camPos = CameraManager.Instance != null && CameraManager.Instance.Camera != null
            ? CameraManager.Instance.Camera.transform.position
            : (GorillaTagger.Instance != null && GorillaTagger.Instance.headCollider != null
                ? GorillaTagger.Instance.headCollider.transform.position
                : Vector3.zero);

        sel.GameObject.transform.position = camPos;
        if (sel.Data != null)
        {
            sel.Data.Position = camPos;
        }

        UIManager.Instance.Status = $"Moved '{sel.Data?.ObjectName ?? "object"}' to camera position.";
    }

    public void SyncWithProject()
    {
        Project p = KeyframeManager.Instance?.Project;
        if (p == _lastProject && p != null && SpawnedObjects.Count > 0)
            return;

        ClearAllSpawnedGameObjects();

        _lastProject = p;
        if (p == null || p.PlacedObjects == null)
            return;

        foreach (PlacedObject data in p.PlacedObjects.ToList())
        {
            SpawnObject(data.ObjectName, data.Position, data.Rotation, data.Scale, data);
        }

        SelectedIndex = SpawnedObjects.Count > 0 ? 0 : -1;
        UpdateSelectionVisuals();
    }

    public void ClearAllSpawnedGameObjects()
    {
        foreach (SpawnedObjectInstance inst in SpawnedObjects)
        {
            if (inst.GameObject != null)
                UnityEngine.Object.Destroy(inst.GameObject);
        }
        SpawnedObjects.Clear();
        SelectedIndex = -1;
    }

    public void Update()
    {
        Project p = KeyframeManager.Instance?.Project;
        if (p != _lastProject)
        {
            SyncWithProject();
        }

        if (CameraManager.Instance != null && CameraManager.Instance.InPlayback)
            return;

        if (GUIUtility.keyboardControl != 0)
            return;

        if (Keyboard.current != null && Keyboard.current.lKey.wasPressedThisFrame)
        {
            MoveSelectedToCamera();
        }
    }

    public void OnDestroy()
    {
        ClearAllSpawnedGameObjects();
    }
}
