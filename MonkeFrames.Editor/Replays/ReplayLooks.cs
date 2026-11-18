using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonkeFrames.Editor.Replays;

/// <summary>
/// What every gorilla looked like (cosmetics included), stored inside the replay file so a saved
/// replay can be rebuilt later even when nobody in the lobby has those cosmetics loaded.
///
/// Meshes are copied straight from the GPU vertex/index buffers (works for meshes the game
/// doesn't keep readable), materials are stored as shader + keywords + properties + texture names.
/// The plain gorilla body isn't stored: every copy of the game already has it.
/// </summary>
public sealed class LookBank
{
    public readonly List<MeshBlob> Meshes = new();
    public readonly List<MatBlob> Mats = new();
    public readonly List<TrackLook> Tracks = new();   // same order as ReplayClip.Tracks

    // Runtime caches (rebuilt objects), released when another replay loads.
    [NonSerialized] private Mesh[] _meshObjs;
    [NonSerialized] private Material[] _matObjs;
    private static readonly List<UnityEngine.Object> _runtime = new();

    public TrackLook For(int track) => track >= 0 && track < Tracks.Count ? Tracks[track] : null;

    // =====================================================================
    //  Capture (main thread, just before saving)
    // =====================================================================

    public static LookBank Capture(ReplayClip clip)
    {
        LookBank bank = new LookBank();
        var meshIndex = new Dictionary<Mesh, int>();
        var matIndex = new Dictionary<Material, int>();

        // Meshes every copy of the game has (the gorilla itself): store by name only.
        var builtIn = new HashSet<Mesh>();
        try
        {
            VRRig me = Components.CameraModes.LocalRig();
            if (me != null)
            {
                HashSet<Renderer> worn = PuppetBuilder.WornRenderers(me);
                foreach (Renderer r in me.GetComponentsInChildren<Renderer>(true))
                {
                    if (worn.Contains(r) && r != me.mainSkin) continue;   // your cosmetics: store them
                    Mesh m = r is SkinnedMeshRenderer s ? s.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                    if (m != null) builtIn.Add(m);
                }
            }
        }
        catch { }

        int stored = 0, named = 0;
        foreach (ReplayTrack t in clip.Tracks)
        {
            TrackLook look = new TrackLook();
            bank.Tracks.Add(look);
            PuppetBuilder.Ctx ctx = t.Puppet != null ? t.Puppet.BuildCtx : null;
            if (ctx == null) continue;

            foreach (var kv in ctx.Nodes)
            {
                if (kv.Value == null) continue;
                kv.Value.GetLocalPositionAndRotation(out Vector3 lp, out Quaternion lq);
                look.Nodes.Add(new NodePose { Path = kv.Key, Pos = lp, Rot = lq, Scale = kv.Value.localScale });
            }

            foreach (var kv in ctx.Records)
            {
                RendRec rec = kv.Value;
                if (rec.Mesh == null) continue;

                if (!meshIndex.TryGetValue(rec.Mesh, out int mi))
                {
                    MeshBlob blob = MeshBlob.From(rec.Mesh, !builtIn.Contains(rec.Mesh));
                    if (blob.Stored) stored++; else named++;
                    mi = bank.Meshes.Count;
                    bank.Meshes.Add(blob);
                    meshIndex[rec.Mesh] = mi;
                }

                int[] mats = new int[rec.Materials.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    Material m = rec.Materials[i];
                    if (m == null) { mats[i] = -1; continue; }
                    if (!matIndex.TryGetValue(m, out int idx))
                    {
                        idx = bank.Mats.Count;
                        bank.Mats.Add(MatBlob.From(m));
                        matIndex[m] = idx;
                    }
                    mats[i] = idx;
                }

                look.Renderers.Add(new RendLook
                {
                    Path = kv.Key,
                    Skinned = rec.Skinned,
                    Mesh = mi,
                    Mats = mats,
                    Bones = rec.BonePaths ?? Array.Empty<string>(),
                    RootBone = rec.RootBonePath ?? "",
                    Center = rec.Bounds.center,
                    Extents = rec.Bounds.extents,
                });
            }
        }

        Console.WriteLine($"[MonkeFrames::Replay] Stored looks: {stored} meshes, {named} by name, {bank.Mats.Count} materials");
        return bank;
    }

    // =====================================================================
    //  Restore (main thread, while loading)
    // =====================================================================

    public static void ReleaseRuntime()
    {
        foreach (UnityEngine.Object o in _runtime)
            if (o != null) UnityEngine.Object.Destroy(o);
        _runtime.Clear();
        _meshLookup = null;
        _matLookup = null;
        _texLookup = null;
    }

    private static Dictionary<string, List<Mesh>> _meshLookup;
    private static Dictionary<string, Material> _matLookup;
    private static Dictionary<string, Texture> _texLookup;

    private static void BuildLookups()
    {
        if (_meshLookup != null) return;
        _meshLookup = new Dictionary<string, List<Mesh>>();
        _matLookup = new Dictionary<string, Material>();
        _texLookup = new Dictionary<string, Texture>();
        foreach (Mesh m in Resources.FindObjectsOfTypeAll<Mesh>())
        {
            if (m == null || string.IsNullOrEmpty(m.name)) continue;
            if (!_meshLookup.TryGetValue(m.name, out var list)) _meshLookup[m.name] = list = new List<Mesh>();
            list.Add(m);
        }
        foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            if (m != null && !string.IsNullOrEmpty(m.name) && !_matLookup.ContainsKey(m.name + "|" + m.shader?.name))
                _matLookup[m.name + "|" + m.shader?.name] = m;
        foreach (Texture t in Resources.FindObjectsOfTypeAll<Texture>())
            if (t != null && !string.IsNullOrEmpty(t.name) && !_texLookup.ContainsKey(t.name))
                _texLookup[t.name] = t;
    }

    public Mesh GetMesh(int i)
    {
        if (i < 0 || i >= Meshes.Count) return null;
        _meshObjs ??= new Mesh[Meshes.Count];
        if (_meshObjs[i] != null) return _meshObjs[i];

        BuildLookups();
        MeshBlob b = Meshes[i];
        Mesh m = null;
        // Prefer the game's own mesh if it happens to be loaded.
        if (_meshLookup.TryGetValue(b.Name ?? "", out var list))
            foreach (Mesh c in list)
                if (c.vertexCount == b.VertexCount) { m = c; break; }
        if (m == null && b.Stored)
        {
            try
            {
                m = b.ToMesh();
                _runtime.Add(m);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MonkeFrames::Replay] Couldn't rebuild mesh {b.Name}: {ex.Message}");
            }
        }
        _meshObjs[i] = m;
        return m;
    }

    public Material GetMat(int i)
    {
        if (i < 0 || i >= Mats.Count) return null;
        _matObjs ??= new Material[Mats.Count];
        if (_matObjs[i] != null) return _matObjs[i];

        BuildLookups();
        MatBlob b = Mats[i];
        Material m = null;
        if (!b.Baked && _matLookup.TryGetValue(b.Name + "|" + b.Shader, out Material live))
            m = live;
        else
        {
            try
            {
                m = b.ToMaterial(_texLookup);
                if (m != null) _runtime.Add(m);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MonkeFrames::Replay] Couldn't rebuild material {b.Name}: {ex.Message}");
            }
        }
        _matObjs[i] = m;
        return m;
    }

    // =====================================================================
    //  File IO
    // =====================================================================

    public void Write(BinaryWriter w)
    {
        w.Write(Meshes.Count);
        foreach (MeshBlob m in Meshes) m.Write(w);
        w.Write(Mats.Count);
        foreach (MatBlob m in Mats) m.Write(w);
        w.Write(Tracks.Count);
        foreach (TrackLook t in Tracks) t.Write(w);
    }

    public static LookBank Read(BinaryReader r)
    {
        LookBank b = new LookBank();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) b.Meshes.Add(MeshBlob.Read(r));
        n = r.ReadInt32();
        for (int i = 0; i < n; i++) b.Mats.Add(MatBlob.Read(r));
        n = r.ReadInt32();
        for (int i = 0; i < n; i++) b.Tracks.Add(TrackLook.Read(r));
        return b;
    }

    internal static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
    internal static Vector3 ReadV3(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    internal static void WriteV4(BinaryWriter w, Vector4 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); w.Write(v.w); }
    internal static Vector4 ReadV4(BinaryReader r) => new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    internal static void WriteBytes(BinaryWriter w, byte[] b) { w.Write(b?.Length ?? 0); if (b != null) w.Write(b); }
    internal static byte[] ReadBytes(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 256 * 1024 * 1024) throw new InvalidDataException("Bad block size in replay");
        byte[] b = r.ReadBytes(n);
        if (b.Length != n) throw new EndOfStreamException("Replay file is cut short");
        return b;
    }
}

/// <summary>What a copied renderer was made from (kept while recording / after loading, for saving).</summary>
internal sealed class RendRec
{
    public bool Skinned;
    public Mesh Mesh;
    public Material[] Materials = Array.Empty<Material>();
    public string[] BonePaths;
    public string RootBonePath;
    public Bounds Bounds;
}

public sealed class TrackLook
{
    public readonly List<RendLook> Renderers = new();
    public readonly List<NodePose> Nodes = new();

    public RendLook Find(string path)
    {
        foreach (RendLook r in Renderers)
            if (r.Path == path) return r;
        return null;
    }

    public void Write(BinaryWriter w)
    {
        w.Write(Renderers.Count);
        foreach (RendLook r in Renderers) r.Write(w);
        w.Write(Nodes.Count);
        foreach (NodePose n in Nodes)
        {
            w.Write(n.Path ?? "");
            LookBank.WriteV3(w, n.Pos);
            LookBank.WriteV4(w, new Vector4(n.Rot.x, n.Rot.y, n.Rot.z, n.Rot.w));
            LookBank.WriteV3(w, n.Scale);
        }
    }

    public static TrackLook Read(BinaryReader r)
    {
        TrackLook t = new TrackLook();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) t.Renderers.Add(RendLook.Read(r));
        n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            string p = r.ReadString();
            Vector3 pos = LookBank.ReadV3(r);
            Vector4 q = LookBank.ReadV4(r);
            Vector3 s = LookBank.ReadV3(r);
            t.Nodes.Add(new NodePose { Path = p, Pos = pos, Rot = new Quaternion(q.x, q.y, q.z, q.w), Scale = s });
        }
        return t;
    }
}

public sealed class NodePose
{
    public string Path;
    public Vector3 Pos;
    public Quaternion Rot;
    public Vector3 Scale;
}

public sealed class RendLook
{
    public string Path;
    public bool Skinned;
    public int Mesh;
    public int[] Mats;
    public string[] Bones;
    public string RootBone;
    public Vector3 Center, Extents;

    public void Write(BinaryWriter w)
    {
        w.Write(Path ?? "");
        w.Write(Skinned);
        w.Write(Mesh);
        w.Write(Mats.Length);
        foreach (int m in Mats) w.Write(m);
        w.Write(Bones.Length);
        foreach (string b in Bones) w.Write(b ?? "");
        w.Write(RootBone ?? "");
        LookBank.WriteV3(w, Center);
        LookBank.WriteV3(w, Extents);
    }

    public static RendLook Read(BinaryReader r)
    {
        RendLook l = new RendLook { Path = r.ReadString(), Skinned = r.ReadBoolean(), Mesh = r.ReadInt32() };
        int n = r.ReadInt32();
        l.Mats = new int[n];
        for (int i = 0; i < n; i++) l.Mats[i] = r.ReadInt32();
        n = r.ReadInt32();
        l.Bones = new string[n];
        for (int i = 0; i < n; i++) l.Bones[i] = r.ReadString();
        l.RootBone = r.ReadString();
        l.Center = LookBank.ReadV3(r);
        l.Extents = LookBank.ReadV3(r);
        return l;
    }
}

/// <summary>A mesh copied from the GPU buffers (raw vertex streams + indices + sub-meshes + bind poses).</summary>
public sealed class MeshBlob
{
    public string Name = "";
    public int VertexCount;
    public bool Stored;
    public int[] Attr = Array.Empty<int>();   // (attribute, format, dimension, stream) x N
    public byte[][] Streams = Array.Empty<byte[]>();
    public bool Index32;
    public byte[] Indices = Array.Empty<byte>();
    public int[] Subs = Array.Empty<int>();   // (indexStart, indexCount, topology, baseVertex) x N
    public Matrix4x4[] Bindposes = Array.Empty<Matrix4x4>();

    public static MeshBlob From(Mesh m, bool store)
    {
        MeshBlob b = new MeshBlob { Name = m.name ?? "", VertexCount = m.vertexCount };
        if (!store) return b;
        try
        {
            VertexAttributeDescriptor[] attrs = m.GetVertexAttributes();
            b.Attr = new int[attrs.Length * 4];
            for (int i = 0; i < attrs.Length; i++)
            {
                b.Attr[i * 4] = (int)attrs[i].attribute;
                b.Attr[i * 4 + 1] = (int)attrs[i].format;
                b.Attr[i * 4 + 2] = attrs[i].dimension;
                b.Attr[i * 4 + 3] = attrs[i].stream;
            }

            m.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            m.indexBufferTarget |= GraphicsBuffer.Target.Raw;

            int streams = m.vertexBufferCount;
            b.Streams = new byte[streams][];
            for (int s = 0; s < streams; s++)
            {
                using GraphicsBuffer gb = m.GetVertexBuffer(s);
                byte[] data = new byte[gb.count * gb.stride];
                gb.GetData(data);
                b.Streams[s] = data;
            }

            b.Index32 = m.indexFormat == IndexFormat.UInt32;
            using (GraphicsBuffer ib = m.GetIndexBuffer())
            {
                byte[] data = new byte[ib.count * ib.stride];
                ib.GetData(data);
                b.Indices = data;
            }

            b.Subs = new int[m.subMeshCount * 4];
            for (int i = 0; i < m.subMeshCount; i++)
            {
                SubMeshDescriptor d = m.GetSubMesh(i);
                b.Subs[i * 4] = d.indexStart;
                b.Subs[i * 4 + 1] = d.indexCount;
                b.Subs[i * 4 + 2] = (int)d.topology;
                b.Subs[i * 4 + 3] = d.baseVertex;
            }

            try { b.Bindposes = m.bindposes ?? Array.Empty<Matrix4x4>(); } catch { b.Bindposes = Array.Empty<Matrix4x4>(); }
            b.Stored = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::Replay] Mesh {m.name} stored by name only: {ex.Message}");
            b.Stored = false;
            b.Streams = Array.Empty<byte[]>();
            b.Indices = Array.Empty<byte>();
        }
        return b;
    }

    public Mesh ToMesh()
    {
        Mesh m = new Mesh { name = Name };
        int n = Attr.Length / 4;
        var attrs = new VertexAttributeDescriptor[n];
        for (int i = 0; i < n; i++)
            attrs[i] = new VertexAttributeDescriptor((VertexAttribute)Attr[i * 4], (VertexAttributeFormat)Attr[i * 4 + 1], Attr[i * 4 + 2], Attr[i * 4 + 3]);

        const MeshUpdateFlags quiet = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;
        m.SetVertexBufferParams(VertexCount, attrs);
        for (int s = 0; s < Streams.Length; s++)
        {
            byte[] bytes = Streams[s];
            uint[] words = new uint[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, words, 0, words.Length * 4);
            m.SetVertexBufferData(words, 0, 0, words.Length, s, quiet);
        }

        if (Index32)
        {
            uint[] idx = new uint[Indices.Length / 4];
            Buffer.BlockCopy(Indices, 0, idx, 0, idx.Length * 4);
            m.SetIndexBufferParams(idx.Length, IndexFormat.UInt32);
            m.SetIndexBufferData(idx, 0, 0, idx.Length, quiet);
        }
        else
        {
            ushort[] idx = new ushort[Indices.Length / 2];
            Buffer.BlockCopy(Indices, 0, idx, 0, idx.Length * 2);
            m.SetIndexBufferParams(idx.Length, IndexFormat.UInt16);
            m.SetIndexBufferData(idx, 0, 0, idx.Length, quiet);
        }

        int subs = Subs.Length / 4;
        m.subMeshCount = subs;
        for (int i = 0; i < subs; i++)
            m.SetSubMesh(i, new SubMeshDescriptor(Subs[i * 4], Subs[i * 4 + 1], (MeshTopology)Subs[i * 4 + 2]) { baseVertex = Subs[i * 4 + 3] }, quiet);

        if (Bindposes.Length > 0) m.bindposes = Bindposes;
        m.RecalculateBounds();
        return m;
    }

    public void Write(BinaryWriter w)
    {
        w.Write(Name ?? "");
        w.Write(VertexCount);
        w.Write(Stored);
        if (!Stored) return;
        w.Write(Attr.Length);
        foreach (int a in Attr) w.Write(a);
        w.Write(Streams.Length);
        foreach (byte[] s in Streams) LookBank.WriteBytes(w, s);
        w.Write(Index32);
        LookBank.WriteBytes(w, Indices);
        w.Write(Subs.Length);
        foreach (int s in Subs) w.Write(s);
        w.Write(Bindposes.Length);
        foreach (Matrix4x4 m in Bindposes)
            for (int i = 0; i < 16; i++) w.Write(m[i]);
    }

    public static MeshBlob Read(BinaryReader r)
    {
        MeshBlob b = new MeshBlob { Name = r.ReadString(), VertexCount = r.ReadInt32(), Stored = r.ReadBoolean() };
        if (!b.Stored) return b;
        int n = r.ReadInt32();
        b.Attr = new int[n];
        for (int i = 0; i < n; i++) b.Attr[i] = r.ReadInt32();
        n = r.ReadInt32();
        b.Streams = new byte[n][];
        for (int i = 0; i < n; i++) b.Streams[i] = LookBank.ReadBytes(r);
        b.Index32 = r.ReadBoolean();
        b.Indices = LookBank.ReadBytes(r);
        n = r.ReadInt32();
        b.Subs = new int[n];
        for (int i = 0; i < n; i++) b.Subs[i] = r.ReadInt32();
        n = r.ReadInt32();
        b.Bindposes = new Matrix4x4[n];
        for (int k = 0; k < n; k++)
        {
            Matrix4x4 m = new Matrix4x4();
            for (int i = 0; i < 16; i++) m[i] = r.ReadSingle();
            b.Bindposes[k] = m;
        }
        return b;
    }
}

/// <summary>A material as shader name + keywords + property values + texture names.</summary>
public sealed class MatBlob
{
    public string Name = "", Shader = "";
    public int Queue = -1;
    public bool Baked;   // a per-gorilla copy (colours baked in), so the game's own material isn't the same
    public string[] Keywords = Array.Empty<string>();
    public readonly List<(string name, float v)> Floats = new();
    public readonly List<(string name, int v)> Ints = new();
    public readonly List<(string name, Vector4 v)> Vectors = new();
    public readonly List<(string name, string tex, Vector4 st)> Textures = new();

    public static MatBlob From(Material m)
    {
        MatBlob b = new MatBlob
        {
            Name = m.name ?? "",
            Shader = m.shader != null ? m.shader.name : "",
            Queue = m.renderQueue,
            Baked = (m.name ?? "").Contains("(Clone)") || (m.name ?? "").Contains("(Instance)") || (m.name ?? "").Contains("MonkeFrames"),
            Keywords = m.shaderKeywords ?? Array.Empty<string>(),
        };
        try
        {
            foreach (string p in m.GetPropertyNames(MaterialPropertyType.Float)) b.Floats.Add((p, m.GetFloat(p)));
            foreach (string p in m.GetPropertyNames(MaterialPropertyType.Int)) b.Ints.Add((p, m.GetInteger(p)));
            foreach (string p in m.GetPropertyNames(MaterialPropertyType.Vector)) b.Vectors.Add((p, m.GetVector(p)));
            foreach (string p in m.GetPropertyNames(MaterialPropertyType.Texture))
            {
                Texture t = m.GetTexture(p);
                Vector2 sc = m.GetTextureScale(p), of = m.GetTextureOffset(p);
                b.Textures.Add((p, t != null ? t.name : "", new Vector4(sc.x, sc.y, of.x, of.y)));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::Replay] Material {m.name}: {ex.Message}");
        }
        return b;
    }

    public Material ToMaterial(Dictionary<string, Texture> textures)
    {
        Shader sh = UnityEngine.Shader.Find(Shader);
        bool fallback = sh == null;
        if (fallback) sh = UnityEngine.Shader.Find("Universal Render Pipeline/Lit") ?? UnityEngine.Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) return null;

        Material m = new Material(sh) { name = Name + " (MonkeFrames replay)" };
        if (!fallback)
        {
            m.shaderKeywords = Keywords;
            if (Queue >= 0) m.renderQueue = Queue;
        }
        foreach (var (n, v) in Floats) if (m.HasProperty(n)) m.SetFloat(n, v);
        foreach (var (n, v) in Ints) if (m.HasProperty(n)) m.SetInteger(n, v);
        foreach (var (n, v) in Vectors) if (m.HasProperty(n)) m.SetVector(n, v);
        foreach (var (n, tex, st) in Textures)
        {
            if (!m.HasProperty(n)) continue;
            if (!string.IsNullOrEmpty(tex) && textures.TryGetValue(tex, out Texture t))
                m.SetTexture(n, t);
            m.SetTextureScale(n, new Vector2(st.x, st.y));
            m.SetTextureOffset(n, new Vector2(st.z, st.w));
        }
        return m;
    }

    public void Write(BinaryWriter w)
    {
        w.Write(Name ?? ""); w.Write(Shader ?? ""); w.Write(Queue); w.Write(Baked);
        w.Write(Keywords.Length);
        foreach (string k in Keywords) w.Write(k ?? "");
        w.Write(Floats.Count);
        foreach (var (n, v) in Floats) { w.Write(n); w.Write(v); }
        w.Write(Ints.Count);
        foreach (var (n, v) in Ints) { w.Write(n); w.Write(v); }
        w.Write(Vectors.Count);
        foreach (var (n, v) in Vectors) { w.Write(n); LookBank.WriteV4(w, v); }
        w.Write(Textures.Count);
        foreach (var (n, t, st) in Textures) { w.Write(n); w.Write(t ?? ""); LookBank.WriteV4(w, st); }
    }

    public static MatBlob Read(BinaryReader r)
    {
        MatBlob b = new MatBlob { Name = r.ReadString(), Shader = r.ReadString(), Queue = r.ReadInt32(), Baked = r.ReadBoolean() };
        int n = r.ReadInt32();
        b.Keywords = new string[n];
        for (int i = 0; i < n; i++) b.Keywords[i] = r.ReadString();
        n = r.ReadInt32();
        for (int i = 0; i < n; i++) b.Floats.Add((r.ReadString(), r.ReadSingle()));
        n = r.ReadInt32();
        for (int i = 0; i < n; i++) b.Ints.Add((r.ReadString(), r.ReadInt32()));
        n = r.ReadInt32();
        for (int i = 0; i < n; i++) b.Vectors.Add((r.ReadString(), LookBank.ReadV4(r)));
        n = r.ReadInt32();
        for (int i = 0; i < n; i++) b.Textures.Add((r.ReadString(), r.ReadString(), LookBank.ReadV4(r)));
        return b;
    }
}
