using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonkeFrames.Editor.Replays;

// Only plain data goes into the file: Unity instance IDs do not survive a restart.
internal static class ReplayFur
{
    internal sealed class Appearance
    {
        public int Version;
        public string Mesh, RootBone;
        public int VertexCount, SubMeshCount;
        public string[] Bones;
        public float[][] BindPoses;
        public Dictionary<string, float[]> BoneScales;
        public MaterialData[] Materials;
    }
    internal sealed class MaterialData
    {
        public string Name, Shader;
        public int RenderQueue;
        public string[] Keywords;
        public List<Property> Properties = new();
    }
    internal sealed class Property
    {
        public string Name, Texture;
        public int Type;
        public int Integer;
        public int Dimension;
        public int Width, Height;
        public float[] Values;
    }

    public static string Capture(VRRig rig)
    {
        if (rig == null || rig.mainSkin == null) return "";
        var renderer = rig.mainSkin;
        Material[] materials = renderer.sharedMaterials;
        var appearance = new Appearance { Version = 2, Mesh = renderer.sharedMesh != null ? renderer.sharedMesh.name : "", Materials = new MaterialData[materials.Length] };
        CaptureSkeleton(appearance, renderer, rig.transform);
        var block = new MaterialPropertyBlock();
        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material == null || material.shader == null) continue;
            var data = new MaterialData { Name = material.name, Shader = material.shader.name, RenderQueue = material.renderQueue, Keywords = material.shaderKeywords };
            renderer.GetPropertyBlock(block, i);
            // The renderer-wide block is used when there is no slot-specific block.
            if (block.isEmpty) renderer.GetPropertyBlock(block);
            for (int p = 0; p < material.shader.GetPropertyCount(); p++)
            {
                string name = material.shader.GetPropertyName(p);
                ShaderPropertyType type = material.shader.GetPropertyType(p);
                var property = new Property { Name = name, Type = (int)type };
                bool overridden = block.HasProperty(Shader.PropertyToID(name));
                switch (type)
                {
                    case ShaderPropertyType.Color:
                        Color color = overridden ? block.GetColor(name) : material.GetColor(name);
                        property.Values = new[] { color.r, color.g, color.b, color.a }; break;
                    case ShaderPropertyType.Vector:
                        Vector4 vector = overridden ? block.GetVector(name) : material.GetVector(name);
                        property.Values = new[] { vector.x, vector.y, vector.z, vector.w }; break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        property.Values = new[] { overridden ? block.GetFloat(name) : material.GetFloat(name) }; break;
                    case ShaderPropertyType.Int:
                        property.Integer = overridden ? block.GetInteger(name) : material.GetInteger(name);
                        property.Values = Array.Empty<float>(); break;
                    case ShaderPropertyType.Texture:
                        Texture texture = overridden ? block.GetTexture(name) : material.GetTexture(name);
                        TextureDimension dimension = material.shader.GetPropertyTextureDimension(p);
                        // Shader defaults can expose a 2D placeholder for an unbound array.
                        // Do not serialize it as if it were a valid atlas assignment.
                        if (texture != null && texture.dimension != dimension) continue;
                        property.Dimension = (int)dimension;
                        property.Texture = texture != null ? texture.name : "";
                        property.Width = texture != null ? texture.width : 0;
                        property.Height = texture != null ? texture.height : 0;
                        Vector2 scale = material.GetTextureScale(name), offset = material.GetTextureOffset(name);
                        property.Values = new[] { scale.x, scale.y, offset.x, offset.y }; break;
                    default: continue;
                }
                data.Properties.Add(property);
            }
            appearance.Materials[i] = data;
        }
        return JsonConvert.SerializeObject(appearance);
    }

    public static void Apply(ReplayTrack track, ReplayPuppet puppet)
    {
        if (string.IsNullOrEmpty(track.FurAppearance) || puppet?.BuildCtx == null) return;
        if (!puppet.BuildCtx.Nodes.TryGetValue(track.MainSkinPath, out Transform node)) return;
        var renderer = node.GetComponent<SkinnedMeshRenderer>();
        if (renderer == null) return;
        var appearance = JsonConvert.DeserializeObject<Appearance>(track.FurAppearance);
        if (appearance?.Materials == null) return;
        Material[] templates = Resources.FindObjectsOfTypeAll<Material>();
        Texture[] textures = Resources.FindObjectsOfTypeAll<Texture>();
        Material[] previous = renderer.sharedMaterials;
        var materials = new Material[appearance.Materials.Length];
        for (int i = 0; i < materials.Length; i++)
        {
            var data = appearance.Materials[i];
            if (data == null) continue;
            Material template = Array.Find(templates, m => m != null && Clean(m.name) == Clean(data.Name) && m.shader != null && m.shader.name == data.Shader);
            Shader shader = Shader.Find(data.Shader);
            if (template == null && shader == null && i < previous.Length) template = previous[i];
            if (template == null && shader == null) continue;
            Material material = template != null ? new Material(template) : new Material(shader);
            puppet.Owned.Add(material);
            material.name = data.Name; material.renderQueue = data.RenderQueue;
            material.shaderKeywords = data.Keywords ?? Array.Empty<string>();
            foreach (Property property in data.Properties ?? new List<Property>())
            {
                if (property == null || property.Values == null) continue;
                int propertyIndex = material.shader.FindPropertyIndex(property.Name);
                if (propertyIndex < 0) continue;
                ShaderPropertyType actualType = material.shader.GetPropertyType(propertyIndex);
                ShaderPropertyType savedType = (ShaderPropertyType)property.Type;
                bool floating = actualType is ShaderPropertyType.Float or ShaderPropertyType.Range;
                if (actualType != savedType && !(floating && savedType is ShaderPropertyType.Float or ShaderPropertyType.Range)) continue;
                // Legacy integer snapshots were read through GetInt (a float API),
                // which returned bogus zeroes. Keep the real template's integer values.
                if (actualType == ShaderPropertyType.Int && appearance.Version < 2) continue;
                float[] v = property.Values;
                int required = actualType == ShaderPropertyType.Int ? 0
                    : actualType is ShaderPropertyType.Color or ShaderPropertyType.Vector or ShaderPropertyType.Texture ? 4 : 1;
                if (v.Length < required) continue;
                switch (actualType)
                {
                    case ShaderPropertyType.Color: material.SetColor(property.Name, new Color(v[0], v[1], v[2], v[3])); break;
                    case ShaderPropertyType.Vector: material.SetVector(property.Name, new Vector4(v[0], v[1], v[2], v[3])); break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range: material.SetFloat(property.Name, v[0]); break;
                    case ShaderPropertyType.Int: material.SetInteger(property.Name, property.Integer); break;
                    case ShaderPropertyType.Texture:
                        ApplyTexture(material, propertyIndex, property, textures);
                        break;
                }
            }
            materials[i] = material;
        }
        RestoreSkeleton(appearance, renderer, puppet);
        renderer.sharedMaterials = materials;
        renderer.SetPropertyBlock(null);
    }

    private static void ApplyTexture(Material material, int index, Property property, Texture[] textures)
    {
        TextureDimension expected = material.shader.GetPropertyTextureDimension(index);
        if (property.Dimension != 0 && (TextureDimension)property.Dimension != expected) return;
        // Atlas/lightmap arrays are supplied by the active scene. An empty or
        // unresolved snapshot must not replace them with a default 2D texture.
        if (string.IsNullOrEmpty(property.Texture)) return;
        bool Matches(Texture texture) => texture != null && texture.name == property.Texture
            && texture.dimension == expected
            && (property.Width == 0 || texture.width == property.Width)
            && (property.Height == 0 || texture.height == property.Height);
        Texture texture = material.GetTexture(property.Name);
        if (!Matches(texture)) texture = Array.Find(textures, Matches);
        if (texture == null) return;
        material.SetTexture(property.Name, texture);
        if (expected == TextureDimension.Tex2D)
        {
            float[] v = property.Values;
            material.SetTextureScale(property.Name, new Vector2(v[0], v[1]));
            material.SetTextureOffset(property.Name, new Vector2(v[2], v[3]));
        }
    }

    private static void CaptureSkeleton(Appearance appearance, SkinnedMeshRenderer renderer, Transform root)
    {
        Mesh mesh = renderer.sharedMesh;
        if (mesh == null || !mesh.isReadable) return;
        appearance.VertexCount = mesh.vertexCount;
        appearance.SubMeshCount = mesh.subMeshCount;
        Matrix4x4[] poses = mesh.bindposes;
        appearance.BindPoses = new float[poses.Length][];
        for (int i = 0; i < poses.Length; i++)
        {
            appearance.BindPoses[i] = new float[16];
            for (int j = 0; j < 16; j++) appearance.BindPoses[i][j] = poses[i][j];
        }
        Transform[] bones = renderer.bones;
        appearance.Bones = new string[bones.Length];
        appearance.BoneScales = new Dictionary<string, float[]>();
        for (int i = 0; i < bones.Length; i++)
        {
            appearance.Bones[i] = bones[i] != null ? PuppetBuilder.PathOf(bones[i], root) : null;
            for (Transform bone = bones[i]; bone != null && bone != root; bone = bone.parent)
            {
                string path = PuppetBuilder.PathOf(bone, root);
                if (path == null) break;
                Vector3 scale = bone.localScale;
                appearance.BoneScales[path] = new[] { scale.x, scale.y, scale.z };
            }
        }
        appearance.RootBone = renderer.rootBone != null ? PuppetBuilder.PathOf(renderer.rootBone, root) : null;
    }

    private static void RestoreSkeleton(Appearance appearance, SkinnedMeshRenderer renderer, ReplayPuppet puppet)
    {
        // A name alone cannot identify a skinned mesh. Older files have no binding
        // metadata: keep the mesh and bones copied together from the rig template.
        if (appearance.Bones == null || appearance.BindPoses == null
            || appearance.Bones.Length != appearance.BindPoses.Length) return;
        var bones = new Transform[appearance.Bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            string path = appearance.Bones[i];
            if (path == null || !puppet.BuildCtx.Nodes.TryGetValue(path, out bones[i]) || bones[i] == null) return;
        }
        bool Compatible(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable || mesh.name != appearance.Mesh || mesh.vertexCount != appearance.VertexCount
                || mesh.subMeshCount != appearance.SubMeshCount) return false;
            Matrix4x4[] poses = mesh.bindposes;
            if (poses.Length != appearance.BindPoses.Length) return false;
            for (int i = 0; i < poses.Length; i++)
            {
                float[] saved = appearance.BindPoses[i];
                if (saved == null || saved.Length != 16) return false;
                for (int j = 0; j < 16; j++)
                    if (Mathf.Abs(poses[i][j] - saved[j]) > 0.00001f) return false;
            }
            return true;
        }
        Mesh mesh = renderer.sharedMesh;
        if (!Compatible(mesh)) mesh = Array.Find(Resources.FindObjectsOfTypeAll<Mesh>(), Compatible);
        if (mesh == null) return;
        renderer.sharedMesh = mesh;
        renderer.bones = bones;
        if (appearance.RootBone != null && puppet.BuildCtx.Nodes.TryGetValue(appearance.RootBone, out var root))
            renderer.rootBone = root;
        if (appearance.BoneScales != null)
            foreach (var entry in appearance.BoneScales)
                if (entry.Value?.Length == 3 && puppet.BuildCtx.Nodes.TryGetValue(entry.Key, out var bone))
                    bone.localScale = new Vector3(entry.Value[0], entry.Value[1], entry.Value[2]);
    }

    private static string Clean(string name) => (name ?? "").Replace(" (Instance)", "").Replace(" (Clone)", "");
}
