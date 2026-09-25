using System;
using UnityEngine;

namespace MonkeFrames.Editor.Replays;

/// <summary>
/// Listens to a gorilla's voice (added next to the game's voice AudioSource) and hands the audio
/// to the replay recorder. The sound itself passes through untouched.
/// </summary>
public class VoiceTap : MonoBehaviour
{
    private readonly object _lock = new();
    private float[] _buf = new float[48000];
    private int _count;
    private double _startDsp = -1;

    public volatile bool Capturing;

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (!Capturing || channels <= 0)
            return;

        double dsp = AudioSettings.dspTime;
        int frames = data.Length / channels;

        lock (_lock)
        {
            if (_count == 0)
                _startDsp = dsp;

            if (_count + frames > _buf.Length)
            {
                // Main thread hasn't collected for a while (hitch); drop the oldest audio.
                if (frames > _buf.Length) return;
                _count = 0;
                _startDsp = dsp;
            }

            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                int o = f * channels;
                for (int c = 0; c < channels; c++)
                    sum += data[o + c];
                _buf[_count++] = sum / channels;
            }
        }
    }

    /// <summary>Take everything captured so far. Returns the number of samples copied into 'into'.</summary>
    public int Drain(ref float[] into, out double startDsp)
    {
        lock (_lock)
        {
            startDsp = _startDsp;
            int n = _count;
            if (n == 0) return 0;
            if (into == null || into.Length < n)
                into = new float[Math.Max(n, 4096)];
            Array.Copy(_buf, into, n);
            _count = 0;
            return n;
        }
    }

    public void Clear()
    {
        lock (_lock)
            _count = 0;
    }
}

/// <summary>
/// Plays one gorilla's recorded voice in sync with the replay. The replay clock is handed over
/// from the main thread; the audio thread follows it and resyncs if they drift apart
/// (e.g. after scrubbing).
/// </summary>
public class VoicePlayer : MonoBehaviour
{
    private ReplayTrack _track;
    private AudioSource _source;
    private int _outRate;

    private volatile bool _playing;
    private double _anchorTime, _anchorDsp;
    private float _speed = 1f;
    private double _cursor = -1;

    public AudioSource Source => _source;

    /// <summary>Volume (can go above 1 to boost quiet voices).</summary>
    public volatile float Gain = 1f;

    public static VoicePlayer Create(ReplayTrack track, Transform parent)
    {
        GameObject go = new GameObject("Replay Voice " + track.Name);
        go.transform.SetParent(parent, false);
        VoicePlayer vp = go.AddComponent<VoicePlayer>();
        vp.Init(track);
        return vp;
    }

    private void Init(ReplayTrack track)
    {
        _track = track;
        _outRate = AudioSettings.outputSampleRate;

        // A looping clip of 1.0s: our filter multiplies it by the recorded voice.
        AudioClip ones = AudioClip.Create("MonkeFrames Replay Voice", _outRate, 1, _outRate, false);
        float[] data = new float[_outRate];
        for (int i = 0; i < data.Length; i++) data[i] = 1f;
        ones.SetData(data, 0);

        _source = gameObject.AddComponent<AudioSource>();
        _source.clip = ones;
        _source.loop = true;
        _source.playOnAwake = false;
        _source.spatialBlend = 0f;
        _source.ignoreListenerVolume = true;   // still audible when "mute game" is on
        _source.ignoreListenerPause = true;
        _source.bypassReverbZones = true;
        _source.volume = 1f;
        _source.priority = 0;
        _source.Play();
    }

    /// <summary>Called every frame by the replay manager.</summary>
    public void SetClock(double time, float speed, bool playing)
    {
        _anchorTime = time;
        _anchorDsp = AudioSettings.dspTime;
        _speed = speed;
        _playing = playing;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        ReplayTrack track = _track;
        if (!_playing || track == null || track.Audio.Count == 0 || channels <= 0)
        {
            Array.Clear(data, 0, data.Length);
            _cursor = -1;
            return;
        }

        double expected = _anchorTime + (AudioSettings.dspTime - _anchorDsp) * _speed;
        if (_cursor < 0 || Math.Abs(_cursor - expected) > 0.08)
            _cursor = expected;

        double step = _speed / (double)_outRate;
        float gain = Gain;
        int frames = data.Length / channels;
        for (int f = 0; f < frames; f++)
        {
            float s = Mathf.Clamp(track.SampleAt(_cursor) * gain, -1f, 1f);
            int o = f * channels;
            for (int c = 0; c < channels; c++)
                data[o + c] *= s;
            _cursor += step;
        }
    }

    private void OnDestroy()
    {
        _playing = false;
        if (_source != null && _source.clip != null)
            Destroy(_source.clip);
    }
}
