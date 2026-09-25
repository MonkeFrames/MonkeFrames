using System;
using System.IO;

namespace MonkeFrames.Editor.Replays;

/// <summary>Mixes replay voice tracks into a temporary PCM WAV for project video export.</summary>
internal static class ReplayVoiceExporter
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int BytesPerSample = 2;

    public static bool HasAudibleVoice(ReplayClip clip)
    {
        if (clip == null) return false;
        foreach (ReplayTrack track in clip.Tracks)
            if (!track.Hidden && !track.VoiceMuted && track.HasVoice && track.VoiceVolume > 0f)
                return true;
        return false;
    }

    /// <summary>
    /// Write audio at project time zero. Replay time advances from the trimmed in-point at the
    /// same speed used by ReplayManager while the keyframe project drives the replay.
    /// </summary>
    public static void WriteWav(string path, ReplayClip clip, int frameCount, int fps, float speed, float masterVolume)
    {
        int rate = Math.Max(1, fps);
        long sampleFrames = (long)Math.Ceiling(frameCount * (double)SampleRate / rate);
        long dataBytes = sampleFrames * Channels * BytesPerSample;
        if (dataBytes > uint.MaxValue - 36)
            throw new InvalidOperationException("The exported replay audio exceeds the WAV format size limit.");

        using FileStream file = File.Create(path);
        using BinaryWriter writer = new BinaryWriter(file);
        writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        writer.Write((uint)(36 + dataBytes));
        writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
        writer.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)Channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * Channels * BytesPerSample);
        writer.Write((short)(Channels * BytesPerSample));
        writer.Write((short)(BytesPerSample * 8));
        writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        writer.Write((uint)dataBytes);

        const int ChunkFrames = 4096;
        double replayIn = clip.In;
        for (long chunkStart = 0; chunkStart < sampleFrames; chunkStart += ChunkFrames)
        {
            int count = (int)Math.Min(ChunkFrames, sampleFrames - chunkStart);
            for (int i = 0; i < count; i++)
            {
                double projectTime = (chunkStart + i) / (double)SampleRate;
                double replayTime = replayIn + projectTime * speed;
                float mixed = 0f;
                foreach (ReplayTrack track in clip.Tracks)
                {
                    if (track.Hidden || track.VoiceMuted || !track.HasVoice)
                        continue;
                    mixed += track.SampleAt(replayTime) * track.VoiceVolume * masterVolume;
                }

                short sample = (short)(Math.Max(-1f, Math.Min(1f, mixed)) * 32767f);
                writer.Write(sample);
                writer.Write(sample);
            }
        }
    }
}
