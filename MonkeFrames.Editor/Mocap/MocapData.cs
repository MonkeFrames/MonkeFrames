using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace MonkeFrames.Editor.Mocap;

public struct MocapVector3
{
    [JsonProperty("x")]
    public float X;

    [JsonProperty("y")]
    public float Y;

    [JsonProperty("z")]
    public float Z;

    public MocapVector3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public MocapVector3(Vector3 v)
    {
        X = v.x;
        Y = v.y;
        Z = v.z;
    }

    public Vector3 ToVector3() => new Vector3(X, Y, Z);

    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:F3}, {1:F3}, {2:F3})", X, Y, Z);
}

public struct MocapQuaternion
{
    [JsonProperty("x")]
    public float X;

    [JsonProperty("y")]
    public float Y;

    [JsonProperty("z")]
    public float Z;

    [JsonProperty("w")]
    public float W;

    public MocapQuaternion(float x, float y, float z, float w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    public MocapQuaternion(Quaternion q)
    {
        X = q.x;
        Y = q.y;
        Z = q.z;
        W = q.w;
    }

    public Quaternion ToQuaternion() => new Quaternion(X, Y, Z, W);

    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:F3}, {1:F3}, {2:F3}, {3:F3})", X, Y, Z, W);
}

public class MocapTransform
{
    [JsonProperty("position")]
    public MocapVector3 Position { get; set; }

    [JsonProperty("rotation")]
    public MocapQuaternion Rotation { get; set; }

    public MocapTransform() { }

    public MocapTransform(Vector3 pos, Quaternion rot)
    {
        Position = new MocapVector3(pos);
        Rotation = new MocapQuaternion(rot);
    }
}

public class MocapFrame
{
    [JsonProperty("timestamp")]
    public float Timestamp { get; set; }

    [JsonProperty("head")]
    public MocapTransform Head { get; set; } = new();

    [JsonProperty("body")]
    public MocapTransform Body { get; set; } = new();

    [JsonProperty("leftHand")]
    public MocapTransform LeftHand { get; set; } = new();

    [JsonProperty("rightHand")]
    public MocapTransform RightHand { get; set; } = new();

    [JsonProperty("leftIndex")]
    public float LeftIndex { get; set; }

    [JsonProperty("leftMiddle")]
    public float LeftMiddle { get; set; }

    [JsonProperty("leftThumb")]
    public float LeftThumb { get; set; }

    [JsonProperty("rightIndex")]
    public float RightIndex { get; set; }

    [JsonProperty("rightMiddle")]
    public float RightMiddle { get; set; }

    [JsonProperty("rightThumb")]
    public float RightThumb { get; set; }
}

public class MocapMetadata
{
    [JsonProperty("recordedAt")]
    public string RecordedAt { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonProperty("playerName")]
    public string PlayerName { get; set; } = "Gorilla";

    [JsonProperty("playerColorHex")]
    public string PlayerColorHex { get; set; } = "#FFFFFF";

    [JsonProperty("recorderVersion")]
    public string RecorderVersion { get; set; } = "1.0.0";

    [JsonProperty("coordinateSystem")]
    public string CoordinateSystem { get; set; } = "Unity_LeftHanded_YUp";
}

public class MocapRecording
{
    public const string CurrentFormat = "MonkeFrames Mocap";
    public const int CurrentFormatVersion = 1;
    public const string FileExtension = ".mfmc";

    [JsonProperty("format")]
    public string Format { get; set; } = CurrentFormat;

    [JsonProperty("formatVersion")]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    [JsonProperty("sampleRate")]
    public float SampleRate { get; set; } = 24f;

    [JsonProperty("frameCount")]
    public int FrameCount { get; set; }

    [JsonProperty("duration")]
    public float Duration { get; set; }

    [JsonProperty("metadata")]
    public MocapMetadata Metadata { get; set; } = new();

    [JsonProperty("frames")]
    public List<MocapFrame> Frames { get; set; } = new();

    public void SaveToFile(string filePath)
    {
        string directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        string tempPath = filePath + ".tmp";
        JsonSerializerSettings settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Culture = CultureInfo.InvariantCulture
        };

        string json = JsonConvert.SerializeObject(this, settings);
        File.WriteAllText(tempPath, json);

        if (File.Exists(filePath))
            File.Delete(filePath);

        File.Move(tempPath, filePath);
    }

    public static MocapRecording LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Mocap file not found: {filePath}");

        string json = File.ReadAllText(filePath);
        MocapRecording recording = JsonConvert.DeserializeObject<MocapRecording>(json);

        if (recording == null)
            throw new InvalidDataException("Failed to deserialize mocap file.");

        if (recording.Format != CurrentFormat)
            throw new InvalidDataException($"Unsupported format '{recording.Format}'. Expected '{CurrentFormat}'.");

        if (recording.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException($"Mocap file version {recording.FormatVersion} is newer than supported version {CurrentFormatVersion}.");

        return recording;
    }
}
