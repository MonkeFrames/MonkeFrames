using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonkeFrames.Editor.Replays;

/// <summary>
/// The gorilla's fur: the body's materials (with the per-renderer colours / textures Gorilla Tag sets) and the
/// skeleton they're skinned to, saved as JSON in each track and put back on the replay gorilla when it loads.
/// Without this a loaded replay gorilla gets the body of whoever is watching, only tinted. (From MonkeFrames 2.1.)
/// </summary>
internal static class ReplayFur
{
    internal sealed class Appearance
    {
        public int Version;
        public string Mesh;
        public string RootBone;
        public int VertexCount;
        public int SubMeshCount;
        public string[] Bones;
        public float[][] BindPoses;
        public Dictionary<string, float[]> BoneScales;
        public MaterialData[] Materials;
    }

    internal sealed class MaterialData
    {
        public string Name;
        public string Shader;
        public int RenderQueue;
        public string[] Keywords;
        public List<Property> Properties = new();
    }

    internal sealed class Property
    {
        public string Name;
        public string Texture;
        public int Type;
        public int Integer;
        public int Dimension;
        public int Width;
        public int Height;
        public float[] Values;
    }

    /// <summary>The live gorilla's fur as JSON ("" if it has no body).</summary>
    public static string Capture(VRRig rig)
    {
        try
        {
            if (rig == null || rig.mainSkin == null) return "";
            SkinnedMeshRenderer skin = rig.mainSkin;
            Material[] mats = (Material[])skin.sharedMaterials.Clone();
            // tagged / infected right now: save the normal fur, not the lava look (that's in the material timeline)
            try
            {
                if (rig.setMatIndex != 0 && rig.materialsToChangeTo != null && rig.materialsToChangeTo.Length > 0
                    && rig.materialsToChangeTo[0] != null && mats.Length > 0)
                    mats[0] = rig.materialsToChangeTo[0];
            }
            catch { }
            var a = new Appearance
            {
                Version = 2,
                Mesh = skin.sharedMesh != null ? skin.sharedMesh.name : "",
                Materials = new MaterialData[mats.Length],
            };
            CaptureSkeleton(a, skin, rig.transform);

            var block = new MaterialPropertyBlock();
            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                if (m == null || m.shader == null) continue;
                var data = new MaterialData { Name = m.name, Shader = m.shader.name, RenderQueue = m.renderQueue, Keywords = m.shaderKeywords };
                skin.GetPropertyBlock(block, i);
                if (block.isEmpty) skin.GetPropertyBlock(block);
                for (int p = 0; p < m.shader.GetPropertyCount(); p++)
                {
                    string name = m.shader.GetPropertyName(p);
                    ShaderPropertyType type = m.shader.GetPropertyType(p);
                    var prop = new Property { Name = name, Type = (int)type };
                    bool fromBlock = block.HasProperty(Shader.PropertyToID(name));
                    switch (type)
                    {
                        case ShaderPropertyType.Color:
                        {
                            Color c = fromBlock ? block.GetColor(name) : m.GetColor(name);
                            prop.Values = new[] { c.r, c.g, c.b, c.a };
                            break;
                        }
                        case ShaderPropertyType.Vector:
                        {
                            Vector4 v = fromBlock ? block.GetVector(name) : m.GetVector(name);
                            prop.Values = new[] { v.x, v.y, v.z, v.w };
                            break;
                        }
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range:
                            prop.Values = new[] { fromBlock ? block.GetFloat(name) : m.GetFloat(name) };
                            break;
                        case ShaderPropertyType.Int:
                            prop.Integer = fromBlock ? block.GetInteger(name) : m.GetInteger(name);
                            prop.Values = Array.Empty<float>();
                            break;
                        case ShaderPropertyType.Texture:
                        {
                            Texture t = fromBlock ? block.GetTexture(name) : m.GetTexture(name);
                            TextureDimension dim = m.shader.GetPropertyTextureDimension(p);
                            if (t != null && t.dimension != dim) continue;
                            prop.Dimension = (int)dim;
                            prop.Texture = t != null ? t.name : "";
                            prop.Width = t != null ? t.width : 0;
                            prop.Height = t != null ? t.height : 0;
                            Vector2 scale = m.GetTextureScale(name), offset = m.GetTextureOffset(name);
                            prop.Values = new[] { scale.x, scale.y, offset.x, offset.y };
                            break;
                        }
                        default:
                            continue;
                    }
                    data.Properties.Add(prop);
                }
                a.Materials[i] = data;
            }
            return JsonConvert.SerializeObject(a);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::Replay] Fur couldn't be recorded: {ex.Message}");
            return "";
        }
    }

    /// <summary>Put the recorded fur back on a replay gorilla's body.</summary>
    public static void Apply(ReplayTrack track, ReplayPuppet puppet)
    {
        try
        {
            if (track == null || string.IsNullOrEmpty(track.FurAppearance) || puppet?.BuildCtx == null) return;
            if (string.IsNullOrEmpty(track.MainSkinPath) || !puppet.BuildCtx.Nodes.TryGetValue(track.MainSkinPath, out Transform node) || node == null) return;
            SkinnedMeshRenderer skin = node.GetComponent<SkinnedMeshRenderer>();
            if (skin == null) return;
            Appearance a = JsonConvert.DeserializeObject<Appearance>(track.FurAppearance);
            if (a?.Materials == null) return;

            Material[] allMats = Resources.FindObjectsOfTypeAll<Material>();
            Texture[] allTex = Resources.FindObjectsOfTypeAll<Texture>();
            Material[] current = skin.sharedMaterials;
            var result = new Material[a.Materials.Length];
            for (int i = 0; i < result.Length; i++)
            {
                MaterialData data = a.Materials[i];
                if (data == null) { result[i] = i < current.Length ? current[i] : null; continue; }
                Material template = Array.Find(allMats, m => m != null && Clean(m.name) == Clean(data.Name) && m.shader != null && m.shader.name == data.Shader);
                Shader shader = Shader.Find(data.Shader);
                if (template == null && shader == null && i < current.Length) template = current[i];
                if (template == null && shader == null) continue;

                Material mat = template != null ? new Material(template) : new Material(shader);
                puppet.Owned.Add(mat);
                mat.name = data.Name;
                mat.renderQueue = data.RenderQueue;
                mat.shaderKeywords = data.Keywords ?? Array.Empty<string>();
                foreach (Property prop in data.Properties ?? new List<Property>())
                {
                    if (prop?.Values == null) continue;
                    int index = mat.shader.FindPropertyIndex(prop.Name);
                    if (index < 0) continue;
                    ShaderPropertyType have = mat.shader.GetPropertyType(index), saved = (ShaderPropertyType)prop.Type;
                    bool haveFloat = have == ShaderPropertyType.Float || have == ShaderPropertyType.Range;
                    bool savedFloat = saved == ShaderPropertyType.Float || saved == ShaderPropertyType.Range;
                    if (have != saved && !(haveFloat && savedFloat)) continue;
                    if (have == ShaderPropertyType.Int && a.Version < 2) continue;
                    float[] v = prop.Values;
                    int need = have == ShaderPropertyType.Int ? 0
                        : have == ShaderPropertyType.Color || have == ShaderPropertyType.Vector || have == ShaderPropertyType.Texture ? 4 : 1;
                    if (v.Length < need) continue;
                    switch (have)
                    {
                        case ShaderPropertyType.Color: mat.SetColor(prop.Name, new Color(v[0], v[1], v[2], v[3])); break;
                        case ShaderPropertyType.Vector: mat.SetVector(prop.Name, new Vector4(v[0], v[1], v[2], v[3])); break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range: mat.SetFloat(prop.Name, v[0]); break;
                        case ShaderPropertyType.Int: mat.SetInteger(prop.Name, prop.Integer); break;
                        case ShaderPropertyType.Texture: ApplyTexture(mat, index, prop, allTex); break;
                    }
                }
                result[i] = mat;
            }
            RestoreSkeleton(a, skin, puppet);
            skin.sharedMaterials = result;
            skin.SetPropertyBlock(null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::Replay] Fur couldn't be restored for {track?.Name}: {ex.Message}");
        }
    }

    private static void ApplyTexture(Material mat, int index, Property prop, Texture[] textures)
    {
        TextureDimension expected = mat.shader.GetPropertyTextureDimension(index);
        if ((prop.Dimension != 0 && prop.Dimension != (int)expected) || string.IsNullOrEmpty(prop.Texture)) return;
        bool Matches(Texture t) => t != null && t.name == prop.Texture && t.dimension == expected
            && (prop.Width == 0 || t.width == prop.Width) && (prop.Height == 0 || t.height == prop.Height);
        Texture tex = mat.GetTexture(prop.Name);
        if (!Matches(tex)) tex = Array.Find(textures, Matches);
        if (tex == null) return;
        mat.SetTexture(prop.Name, tex);
        if (expected == TextureDimension.Tex2D)
        {
            mat.SetTextureScale(prop.Name, new Vector2(prop.Values[0], prop.Values[1]));
            mat.SetTextureOffset(prop.Name, new Vector2(prop.Values[2], prop.Values[3]));
        }
    }

    private static void CaptureSkeleton(Appearance a, SkinnedMeshRenderer skin, Transform root)
    {
        Mesh mesh = skin.sharedMesh;
        if (mesh == null || !mesh.isReadable) return;
        a.VertexCount = mesh.vertexCount;
        a.SubMeshCount = mesh.subMeshCount;
        Matrix4x4[] bind = mesh.bindposes;
        a.BindPoses = new float[bind.Length][];
        for (int i = 0; i < bind.Length; i++)
        {
            a.BindPoses[i] = new float[16];
            for (int j = 0; j < 16; j++) a.BindPoses[i][j] = bind[i][j];
        }
        Transform[] bones = skin.bones;
        a.Bones = new string[bones.Length];
        a.BoneScales = new Dictionary<string, float[]>();
        for (int i = 0; i < bones.Length; i++)
        {
            a.Bones[i] = bones[i] != null ? PuppetBuilder.PathOf(bones[i], root) : null;
            for (Transform t = bones[i]; t != null && t != root; t = t.parent)
            {
                string path = PuppetBuilder.PathOf(t, root);
                if (path == null) break;
                Vector3 s = t.localScale;
                a.BoneScales[path] = new[] { s.x, s.y, s.z };
            }
        }
        a.RootBone = skin.rootBone != null ? PuppetBuilder.PathOf(skin.rootBone, root) : null;
    }

    private static void RestoreSkeleton(Appearance a, SkinnedMeshRenderer skin, ReplayPuppet puppet)
    {
        if (a.Bones == null || a.BindPoses == null || a.Bones.Length != a.BindPoses.Length) return;
        var bones = new Transform[a.Bones.Length];
        for (int i = 0; i < bones.Length; i++)
            if (a.Bones[i] == null || !puppet.BuildCtx.Nodes.TryGetValue(a.Bones[i], out bones[i]) || bones[i] == null)
                return;

        bool Compatible(Mesh m)
        {
            if (m == null || !m.isReadable || m.name != a.Mesh || m.vertexCount != a.VertexCount || m.subMeshCount != a.SubMeshCount) return false;
            Matrix4x4[] bp = m.bindposes;
            if (bp.Length != a.BindPoses.Length) return false;
            for (int i = 0; i < bp.Length; i++)
            {
                float[] saved = a.BindPoses[i];
                if (saved == null || saved.Length != 16) return false;
                for (int k = 0; k < 16; k++) if (Mathf.Abs(bp[i][k] - saved[k]) > 1e-5f) return false;
            }
            return true;
        }

        Mesh mesh = skin.sharedMesh;
        if (!Compatible(mesh)) mesh = Array.Find(Resources.FindObjectsOfTypeAll<Mesh>(), Compatible);
        if (mesh == null) return;
        skin.sharedMesh = mesh;
        skin.bones = bones;
        if (a.RootBone != null && puppet.BuildCtx.Nodes.TryGetValue(a.RootBone, out Transform rootBone)) skin.rootBone = rootBone;
        if (a.BoneScales == null) return;
        foreach (var kv in a.BoneScales)
            if (kv.Value != null && kv.Value.Length == 3 && puppet.BuildCtx.Nodes.TryGetValue(kv.Key, out Transform t))
                t.localScale = new Vector3(kv.Value[0], kv.Value[1], kv.Value[2]);
    }

    private static string Clean(string name) => (name ?? "").Replace(" (Instance)", "").Replace(" (Clone)", "");
}
