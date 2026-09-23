using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace MonkeFrames.Editor.Replays;

/// <summary>A short chunk of recorded voice (mono, 16-bit) starting at <see cref="Time"/> seconds into the replay.</summary>
public sealed class AudioBlock
{
    public double Time;
    public short[] Samples;
}

/// <summary>
/// One recorded replay: every gorilla's movement (and voice) over time.
/// Movement is sampled at <see cref="Rate"/> frames per second and interpolated on playback.
/// </summary>
public sealed class ReplayClip
{
    public const int Magic = 0x5052464D; // "MFRP"
    public const int FileVersion = 2;   // 2: spectator cameras

    public string Name = "Replay";
    public DateTime Created = DateTime.Now;
    public int Rate = 60;
    public int FrameCount;
    public string Where = "";

    // Trim (seconds). OutPoint <= 0 means "to the end".
    public float InPoint;
    public float OutPoint;

    public readonly List<ReplayTrack> Tracks = new();

    /// <summary>MonkeFrames spectator cameras (yours and other mod users') during the recording.</summary>
    public readonly List<CamTrack> Cameras = new();

    /// <summary>File this replay was loaded from / saved to (null if never saved).</summary>
    public string FilePath;
    public bool Dirty;

    public float Length => FrameCount / (float)Mathf.Max(1, Rate);
    public float In => Mathf.Clamp(InPoint, 0f, Length);
    public float Out => OutPoint <= 0f ? Length : Mathf.Clamp(OutPoint, In, Length);

    public long ApproxBytes
    {
        get
        {
            long b = 0;
            foreach (ReplayTrack t in Tracks)
                b += t.ApproxBytes;
            foreach (CamTrack c in Cameras)
                b += c.FrameCount * CamTrack.FrameFloats * 4L;
            return b;
        }
    }

    // ---------------- Save / load ----------------

    public void Save(string path)
    {
        string tmp = path + ".tmp";
        using (FileStream fs = File.Create(tmp))
        using (GZipStream gz = new GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
        using (BinaryWriter w = new BinaryWriter(gz))
        {
            WriteHeader(w);

            foreach (ReplayTrack t in Tracks)
                t.Write(w);

            w.Write(Cameras.Count);
            foreach (CamTrack c in Cameras)
                c.Write(w);
        }

        if (File.Exists(path))
            File.Delete(path);
        File.Move(tmp, path);
    }

    private void WriteHeader(BinaryWriter w)
    {
        w.Write(Magic);
        w.Write(FileVersion);
        w.Write(Name ?? "Replay");
        w.Write(Created.Ticks);
        w.Write(Rate);
        w.Write(FrameCount);
        w.Write(Where ?? "");
        w.Write(InPoint);
        w.Write(OutPoint);
        w.Write(Tracks.Count);
    }

    /// <summary>Read a replay file. With headerOnly, tracks are skipped (fast, for the library list).</summary>
    public static ReplayClip Load(string path, bool headerOnly = false)
    {
        using FileStream fs = File.OpenRead(path);
        using GZipStream gz = new GZipStream(fs, CompressionMode.Decompress);
        using BinaryReader r = new BinaryReader(gz);

        if (r.ReadInt32() != Magic)
            throw new InvalidDataException("Not a MonkeFrames replay");
        int version = r.ReadInt32();
        if (version > FileVersion)
            throw new InvalidDataException("This replay was made by a newer MonkeFrames");

        ReplayClip c = new ReplayClip
        {
            Name = r.ReadString(),
            Created = new DateTime(r.ReadInt64()),
            Rate = r.ReadInt32(),
            FrameCount = r.ReadInt32(),
            Where = r.ReadString(),
            InPoint = r.ReadSingle(),
            OutPoint = r.ReadSingle(),
            FilePath = path,
        };
        int tracks = r.ReadInt32();
        c._headerTrackCount = tracks;

        if (headerOnly)
            return c;

        for (int i = 0; i < tracks; i++)
            c.Tracks.Add(ReplayTrack.Read(r));

        if (version >= 2)
        {
            int cams = r.ReadInt32();
            for (int i = 0; i < cams; i++)
                c.Cameras.Add(CamTrack.Read(r));
        }

        return c;
    }

    private int _headerTrackCount;
    public int TrackCount => Tracks.Count > 0 ? Tracks.Count : _headerTrackCount;
}

/// <summary>One gorilla in a replay.</summary>
public sealed class ReplayTrack
{
    // Root: position (3 floats) + rotation (4 halves) + uniform scale (1 half)
    public const int RootBytes = 12 + 8 + 2;
    // Every other moving part: local rotation (4 halves) + local position (3 halves)
    public const int BoneBytes = 8 + 6;

    public string Key = "";
    public string Name = "Gorilla";
    public bool IsLocal;
    public Color Color = Color.white;

    /// <summary>Moving parts (relative to the gorilla's root), recorded every frame.</summary>
    public string[] Paths = Array.Empty<string>();
    /// <summary>Visible meshes at the time of recording (used to rebuild the gorilla after loading).</summary>
    public string[] RendererPaths = Array.Empty<string>();
    public string HeadPath = "", LeftHandPath = "", RightHandPath = "", MainSkinPath = "";

    public int StartFrame;
    public int FrameCount;

    private byte[] _data = new byte[4096];
    private int _len;

    // Voice
    public int AudioRate = 48000;
    public readonly List<AudioBlock> Audio = new();

    // Editing
    public bool Hidden;
    public bool VoiceMuted;
    public float VoiceVolume = 1f;

    // Runtime only
    [NonSerialized] public ReplayPuppet Puppet;
    [NonSerialized] public VoicePlayer Voice;

    public int FrameSize => RootBytes + Paths.Length * BoneBytes;
    public int EndFrame => StartFrame + FrameCount;   // exclusive
    public bool HasVoice => Audio.Count > 0;

    public long ApproxBytes
    {
        get
        {
            long b = _len;
            foreach (AudioBlock a in Audio)
                b += a.Samples.Length * 2L;
            return b;
        }
    }

    // ---------------- Recording ----------------

    public void AppendFrame(Transform root, Transform[] parts)
    {
        int size = FrameSize;
        if (_len + size > _data.Length)
            Array.Resize(ref _data, Math.Max(_data.Length * 2, _len + size));

        int o = _len;
        root.GetPositionAndRotation(out Vector3 p, out Quaternion q);
        PutF(o, p.x); PutF(o + 4, p.y); PutF(o + 8, p.z);
        PutQ(o + 12, q);
        PutH(o + 20, root.localScale.x);
        o += RootBytes;

        for (int i = 0; i < parts.Length; i++, o += BoneBytes)
        {
            Transform t = parts[i];
            if (t == null)
            {
                PutQ(o, Quaternion.identity);
                PutH(o + 8, 0f); PutH(o + 10, 0f); PutH(o + 12, 0f);
                continue;
            }

            t.GetLocalPositionAndRotation(out Vector3 lp, out Quaternion lq);
            PutQ(o, lq);
            PutH(o + 8, lp.x); PutH(o + 10, lp.y); PutH(o + 12, lp.z);
        }

        _len += size;
        FrameCount++;
    }

    public void TrimExcess()
    {
        if (_data.Length != _len)
            Array.Resize(ref _data, _len);
    }

    // ---------------- Playback ----------------

    public Vector3 RootPosition(int frame)
    {
        int o = Mathf.Clamp(frame, 0, FrameCount - 1) * FrameSize;
        return new Vector3(GetF(o), GetF(o + 4), GetF(o + 8));
    }

    /// <summary>Pose the puppet at local frame position f (0 .. FrameCount-1, fractional = interpolate).</summary>
    public void Apply(float f, Transform root, Transform[] parts)
    {
        if (FrameCount <= 0)
            return;

        f = Mathf.Clamp(f, 0f, FrameCount - 1);
        int i0 = Mathf.FloorToInt(f);
        int i1 = Mathf.Min(i0 + 1, FrameCount - 1);
        float a = f - i0;

        int size = FrameSize;
        int o0 = i0 * size, o1 = i1 * size;

        Vector3 p0 = new Vector3(GetF(o0), GetF(o0 + 4), GetF(o0 + 8));
        Vector3 p1 = new Vector3(GetF(o1), GetF(o1 + 4), GetF(o1 + 8));

        // Teleports (e.g. respawning) snap instead of sliding across the map.
        if ((p1 - p0).sqrMagnitude > 9f)
            a = a < 0.5f ? 0f : 1f;

        root.SetPositionAndRotation(Vector3.LerpUnclamped(p0, p1, a), Quaternion.SlerpUnclamped(GetQ(o0 + 12), GetQ(o1 + 12), a));
        float s = Mathf.LerpUnclamped(GetH(o0 + 20), GetH(o1 + 20), a);
        if (s > 0.0001f)
            root.localScale = new Vector3(s, s, s);

        o0 += RootBytes;
        o1 += RootBytes;
        int n = Mathf.Min(parts.Length, Paths.Length);
        for (int i = 0; i < n; i++, o0 += BoneBytes, o1 += BoneBytes)
        {
            Transform t = parts[i];
            if (t == null)
                continue;

            Quaternion q = Quaternion.SlerpUnclamped(GetQ(o0), GetQ(o1), a);
            Vector3 lp = Vector3.LerpUnclamped(
                new Vector3(GetH(o0 + 8), GetH(o0 + 10), GetH(o0 + 12)),
                new Vector3(GetH(o1 + 8), GetH(o1 + 10), GetH(o1 + 12)), a);
            t.SetLocalPositionAndRotation(lp, q);
        }
    }

    // ---------------- Voice ----------------

    private int _hint;

    /// <summary>Voice sample at replay time t (seconds). Safe to call from the audio thread once recording has finished.</summary>
    public float SampleAt(double t)
    {
        List<AudioBlock> blocks = Audio;
        int count = blocks.Count;
        if (count == 0)
            return 0f;

        int i = _hint;
        if (i < 0 || i >= count || blocks[i].Time > t)
        {
            // Binary search for the last block starting at or before t.
            int lo = 0, hi = count - 1;
            i = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (blocks[mid].Time <= t) { i = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (i < 0) return 0f;
        }
        else
        {
            while (i + 1 < count && blocks[i + 1].Time <= t)
                i++;
        }
        _hint = i;

        AudioBlock b = blocks[i];
        double pos = (t - b.Time) * AudioRate;
        int k = (int)pos;
        if (k < 0 || k >= b.Samples.Length)
            return 0f;

        float s0 = b.Samples[k] / 32767f;
        float s1 = k + 1 < b.Samples.Length ? b.Samples[k + 1] / 32767f : s0;
        return s0 + (s1 - s0) * (float)(pos - k);
    }

    /// <summary>Store captured mono audio, keeping only the parts where someone is actually talking.</summary>
    public void AddAudio(float[] samples, int count, double startTime, int rate)
    {
        const int Chunk = 1024;
        const float Gate = 0.004f;

        AudioRate = rate;
        for (int i = 0; i < count; i += Chunk)
        {
            int n = Math.Min(Chunk, count - i);
            float peak = 0f;
            for (int j = 0; j < n; j++)
            {
                float v = Math.Abs(samples[i + j]);
                if (v > peak) peak = v;
            }
            if (peak < Gate)
                continue;

            short[] s = new short[n];
            for (int j = 0; j < n; j++)
                s[j] = (short)Mathf.Clamp(samples[i + j] * 32767f, -32767f, 32767f);

            double time = startTime + i / (double)rate;
            if (Audio.Count > 0 && Audio[Audio.Count - 1].Time > time)
                continue; // never go backwards in time
            Audio.Add(new AudioBlock { Time = time, Samples = s });
        }
    }

    // ---------------- File IO ----------------

    public void Write(BinaryWriter w)
    {
        w.Write(Key ?? "");
        w.Write(Name ?? "");
        w.Write(IsLocal);
        w.Write(Color.r); w.Write(Color.g); w.Write(Color.b); w.Write(Color.a);
        WriteStrings(w, Paths);
        WriteStrings(w, RendererPaths);
        w.Write(HeadPath ?? ""); w.Write(LeftHandPath ?? ""); w.Write(RightHandPath ?? ""); w.Write(MainSkinPath ?? "");
        w.Write(StartFrame);
        w.Write(FrameCount);
        w.Write(_len);
        w.Write(_data, 0, _len);

        w.Write(AudioRate);
        w.Write(Audio.Count);
        byte[] tmp = Array.Empty<byte>();
        foreach (AudioBlock b in Audio)
        {
            w.Write(b.Time);
            w.Write(b.Samples.Length);
            int bytes = b.Samples.Length * 2;
            if (tmp.Length < bytes) tmp = new byte[bytes];
            Buffer.BlockCopy(b.Samples, 0, tmp, 0, bytes);
            w.Write(tmp, 0, bytes);
        }

        w.Write(Hidden);
        w.Write(VoiceMuted);
        w.Write(VoiceVolume);
    }

    public static ReplayTrack Read(BinaryReader r)
    {
        ReplayTrack t = new ReplayTrack
        {
            Key = r.ReadString(),
            Name = r.ReadString(),
            IsLocal = r.ReadBoolean(),
            Color = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            Paths = ReadStrings(r),
            RendererPaths = ReadStrings(r),
            HeadPath = r.ReadString(),
            LeftHandPath = r.ReadString(),
            RightHandPath = r.ReadString(),
            MainSkinPath = r.ReadString(),
            StartFrame = r.ReadInt32(),
            FrameCount = r.ReadInt32(),
        };

        t._len = r.ReadInt32();
        if (t._len < 0 || t.FrameCount < 0 || t._len != t.FrameCount * t.FrameSize)
            throw new InvalidDataException($"Replay data for {t.Name} doesn't match its size");
        t._data = r.ReadBytes(t._len);
        if (t._data.Length != t._len)
            throw new EndOfStreamException("Replay file is cut short");

        t.AudioRate = r.ReadInt32();
        int blocks = r.ReadInt32();
        for (int i = 0; i < blocks; i++)
        {
            double time = r.ReadDouble();
            int n = r.ReadInt32();
            byte[] bytes = r.ReadBytes(n * 2);
            short[] s = new short[n];
            Buffer.BlockCopy(bytes, 0, s, 0, bytes.Length);
            t.Audio.Add(new AudioBlock { Time = time, Samples = s });
        }

        t.Hidden = r.ReadBoolean();
        t.VoiceMuted = r.ReadBoolean();
        t.VoiceVolume = r.ReadSingle();
        return t;
    }

    private static void WriteStrings(BinaryWriter w, string[] s)
    {
        w.Write(s.Length);
        foreach (string x in s)
            w.Write(x ?? "");
    }

    private static string[] ReadStrings(BinaryReader r)
    {
        int n = r.ReadInt32();
        string[] s = new string[n];
        for (int i = 0; i < n; i++)
            s[i] = r.ReadString();
        return s;
    }

    // ---------------- Packing ----------------

    private void PutF(int o, float v)
    {
        int i = BitConverter.SingleToInt32Bits(v);
        _data[o] = (byte)i; _data[o + 1] = (byte)(i >> 8); _data[o + 2] = (byte)(i >> 16); _data[o + 3] = (byte)(i >> 24);
    }

    private float GetF(int o) =>
        BitConverter.Int32BitsToSingle(_data[o] | (_data[o + 1] << 8) | (_data[o + 2] << 16) | (_data[o + 3] << 24));

    private void PutH(int o, float v)
    {
        ushort h = Mathf.FloatToHalf(v);
        _data[o] = (byte)h; _data[o + 1] = (byte)(h >> 8);
    }

    private float GetH(int o) => Mathf.HalfToFloat((ushort)(_data[o] | (_data[o + 1] << 8)));

    private void PutQ(int o, Quaternion q)
    {
        PutH(o, q.x); PutH(o + 2, q.y); PutH(o + 4, q.z); PutH(o + 6, q.w);
    }

    private Quaternion GetQ(int o)
    {
        Quaternion q = new Quaternion(GetH(o), GetH(o + 2), GetH(o + 4), GetH(o + 6));
        float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        if (m < 0.0001f) return Quaternion.identity;
        return new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
    }
}

/// <summary>A recorded MonkeFrames spectator camera: position, rotation and FOV per frame.</summary>
public sealed class CamTrack
{
    public const int FrameFloats = 8;

    public string Key = "";
    public string Name = "Camera";
    public bool IsLocal;
    public Color Color = Color.gray;
    public int StartFrame;
    public int FrameCount;
    private float[] _data = new float[FrameFloats * 256];

    [NonSerialized] public GameObject Model;

    public int EndFrame => StartFrame + FrameCount;

    public void Append(Vector3 p, Quaternion q, float fov)
    {
        int o = FrameCount * FrameFloats;
        if (o + FrameFloats > _data.Length)
            Array.Resize(ref _data, _data.Length * 2);
        _data[o] = p.x; _data[o + 1] = p.y; _data[o + 2] = p.z;
        _data[o + 3] = q.x; _data[o + 4] = q.y; _data[o + 5] = q.z; _data[o + 6] = q.w;
        _data[o + 7] = fov;
        FrameCount++;
    }

    public void Sample(float f, out Vector3 pos, out Quaternion rot, out float fov)
    {
        f = Mathf.Clamp(f, 0f, Mathf.Max(0, FrameCount - 1));
        int i0 = Mathf.FloorToInt(f), i1 = Mathf.Min(i0 + 1, FrameCount - 1);
        float a = f - i0;
        int o0 = i0 * FrameFloats, o1 = i1 * FrameFloats;
        Vector3 p0 = new Vector3(_data[o0], _data[o0 + 1], _data[o0 + 2]);
        Vector3 p1 = new Vector3(_data[o1], _data[o1 + 1], _data[o1 + 2]);
        if ((p1 - p0).sqrMagnitude > 25f) a = a < 0.5f ? 0f : 1f;   // camera cut: don't slide
        pos = Vector3.Lerp(p0, p1, a);
        rot = Quaternion.Slerp(
            new Quaternion(_data[o0 + 3], _data[o0 + 4], _data[o0 + 5], _data[o0 + 6]),
            new Quaternion(_data[o1 + 3], _data[o1 + 4], _data[o1 + 5], _data[o1 + 6]), a);
        fov = Mathf.Lerp(_data[o0 + 7], _data[o1 + 7], a);
    }

    public void Write(System.IO.BinaryWriter w)
    {
        w.Write(Key ?? ""); w.Write(Name ?? ""); w.Write(IsLocal);
        w.Write(Color.r); w.Write(Color.g); w.Write(Color.b); w.Write(Color.a);
        w.Write(StartFrame); w.Write(FrameCount);
        for (int i = 0; i < FrameCount * FrameFloats; i++)
            w.Write(_data[i]);
    }

    public static CamTrack Read(System.IO.BinaryReader r)
    {
        CamTrack c = new CamTrack
        {
            Key = r.ReadString(), Name = r.ReadString(), IsLocal = r.ReadBoolean(),
            Color = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            StartFrame = r.ReadInt32(),
        };
        int n = r.ReadInt32();
        if (n < 0 || n > 50_000_000) throw new System.IO.InvalidDataException("Bad camera track");
        c._data = new float[Math.Max(FrameFloats, n * FrameFloats)];
        for (int i = 0; i < n * FrameFloats; i++)
            c._data[i] = r.ReadSingle();
        c.FrameCount = n;
        return c;
    }
}
