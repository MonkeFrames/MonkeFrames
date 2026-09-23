using MonkeFrames.Editor.Classes;
using UnityEngine;

namespace MonkeFrames.Editor.UI;

/// <summary>
/// Soft two-note chime played when a notification (status toast) appears.
/// The sound is synthesised at startup, so no audio file needs to ship with the mod.
/// </summary>
public static class NotificationSound
{
    private const int SampleRate = 44100;
    private const float MinInterval = 0.12f;   // don't machine-gun the chime if many toasts arrive at once

    private static AudioSource _source;
    private static AudioClip _clip;
    private static float _lastPlayed = -10f;

    /// <summary>Play the chime (respects the Settings toggle and volume).</summary>
    public static void Play(bool force = false)
    {
        Settings s = Settings.current;
        if (!force && (s == null || !s.NotificationSound))
            return;

        if (Time.unscaledTime - _lastPlayed < MinInterval)
            return;
        _lastPlayed = Time.unscaledTime;

        try
        {
            Ensure();
            _source.PlayOneShot(_clip, Mathf.Clamp01(s?.NotificationVolume ?? 0.5f));
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"[MonkeFrames::Sound] Could not play notification sound: {ex.Message}");
        }
    }

    private static void Ensure()
    {
        if (_source == null)
        {
            GameObject go = new GameObject("MonkeFrames Notification Sound");
            Object.DontDestroyOnLoad(go);
            _source = go.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;          // 2D: same volume wherever the camera is
            _source.ignoreListenerPause = true;
            _source.bypassReverbZones = true;
            _source.priority = 0;
        }

        if (_clip == null)
            _clip = CreateChime();
    }

    /// <summary>Two soft bell-like notes (C6 then G6) with quick attack and gentle decay.</summary>
    private static AudioClip CreateChime()
    {
        const float length = 0.55f;
        int count = (int)(SampleRate * length);
        float[] data = new float[count];

        AddNote(data, 1046.50f, 0.00f, 0.13f, 0.9f);   // C6
        AddNote(data, 1567.98f, 0.085f, 0.16f, 0.8f);  // G6

        // Normalise to a comfortable peak.
        float peak = 0.0001f;
        foreach (float v in data)
            peak = Mathf.Max(peak, Mathf.Abs(v));
        float gain = 0.8f / peak;
        for (int i = 0; i < count; i++)
            data[i] *= gain;

        AudioClip clip = AudioClip.Create("MonkeFrames Chime", count, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    private static void AddNote(float[] data, float freq, float start, float decay, float amp)
    {
        int begin = (int)(start * SampleRate);
        for (int i = begin; i < data.Length; i++)
        {
            float t = (i - begin) / (float)SampleRate;
            float attack = Mathf.Clamp01(t / 0.004f);            // 4 ms fade-in, no click
            float env = attack * Mathf.Exp(-t / decay);
            float w = 2f * Mathf.PI * freq * t;

            // Fundamental plus a couple of soft overtones for a bell-ish tone.
            float v = Mathf.Sin(w) + 0.22f * Mathf.Sin(2f * w) + 0.06f * Mathf.Sin(3.01f * w);
            data[i] += v * env * amp;
        }
    }
}
