using System.Collections;
using System.IO;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;
using MonkeFrames.Editor.Utilities;
using UnityEngine;
using UnityEngine.Networking;

namespace MonkeFrames.Editor.Windows;

public class Player : IEditorWindow
{
    public string Name => "Player";
    public Rect Rect => new Rect(Screen.width / 2f - 450, Screen.height - 195, 900, 155);

    public Compiler.Models.Project Project => KeyframeManager.Instance.Project;

    public bool IsPlaying = false;
    public int HeadPosition = 0;
    private int _LastHeadPosition = 0;
    private float _frameAccumulator;

    private static AudioSource _audioSource;
    private static AudioClip _loadedClip;
    private static string _loadedPath;
    private static float _volume = 1f;
    private string _startTimeStr;
    private bool _isLoadingAudio;

    public void OnDraw()
    {
        float w = Rect.width;

        if (CameraManager.Instance.InPlayback)
        {
            if (_audioSource != null && _audioSource.isPlaying)
                _audioSource.Pause();
            Widgets.Spinner(new Vector2(w / 2f - 70, 70), 7f, Theme.Accent);
            GUI.Label(new Rect(0, 58, w, 24), "Playing back / recording...", Theme.MutedCenter);
            return;
        }

        // Rebuild the preview automatically whenever keyframes, transitions or project settings change,
        // so picking a new transition in the Keyframe Editor shows up here straight away.
        if (Event.current.type == EventType.Layout)
        {
            int hash = ProjectHash();
            if (hash != _lastHash)
            {
                _lastHash = hash;
                _ = Project.Build();
                HeadPosition = Mathf.Clamp(HeadPosition, 0, Mathf.Max((Project.CompiledKeyframes?.Count ?? 1) - 1, 0));
                _LastHeadPosition = HeadPosition;
            }
        }

        if (Project != null && Project.AudioPath != _loadedPath && !_isLoadingAudio)
        {
            LoadAudio(Project.AudioPath);
            _startTimeStr = FormatTime(Project.AudioStartTime);
        }

        var frames = Project.CompiledKeyframes;
        int count = frames?.Count ?? 0;

        // ---- Timeline ----
        Rect timeline = new Rect(16, 42, w - 32, 24);

        // Keyframe markers along the timeline (where each keyframe's transition begins)
        if (count > 1)
        {
            float t = 0f;
            for (int i = 0; i < Project.Keyframes.Count; i++)
            {
                float x = timeline.x + 8 + (timeline.width - 16) * Mathf.Clamp01(t * Project.FPS / (count - 1));
                bool sel = i == UIManager.Instance.Selection;
                Theme.Fill(new Rect(x - 1, timeline.y - 4, 2, 6), (sel ? Theme.Accent : Theme.TextMuted).WithAlpha(sel ? 1f : 0.6f), 1);
                t += Project.Keyframes[i].Transition.Duration;
            }
        }

        int max = Mathf.Max(count - 1, 0);
        int newHead = Mathf.RoundToInt(Widgets.Slider("player.head", timeline, HeadPosition, 0, max));
        HeadPosition = Mathf.Clamp(newHead, 0, max);

        // ---- Controls ----
        float y = 74;
        if (GUI.Button(new Rect(16, y, 96, 28), IsPlaying ? "Pause" : "Play", Theme.AccentButton))
        {
            IsPlaying = !IsPlaying;
            SyncAudio(forceSeek: true);
        }

        if (GUI.Button(new Rect(118, y, 96, 28), "Refresh"))
        {
            _ = Project.Build();
            UIManager.Instance.Status = $"Rebuilt preview: {Project.CompiledKeyframes?.Count ?? 0} frames.";
        }

        float seconds = count == 0 ? 0 : HeadPosition / (float)Project.FPS;
        float total = count == 0 ? 0 : count / (float)Project.FPS;
        string label = count != 0
            ? $"Frame {HeadPosition + 1} / {count}     {seconds:0.00}s / {total:0.00}s     {Project.FPS} FPS"
            : $"No frames yet: add keyframes and press Refresh  ({Project.FPS} FPS)";
        GUI.Label(new Rect(228, y, w - 244, 28), label, Theme.MutedRight);

        // ---- Audio row ----
        Widgets.Divider(16, 108, w - 32);
        float ay = 116;

        GUI.Label(new Rect(16, ay + 2, 70, 24), "Audio file:", Theme.Label);

        bool hasAudio = !string.IsNullOrEmpty(Project.AudioPath) && File.Exists(Project.AudioPath);
        string audioName = _isLoadingAudio ? "Loading..." : (hasAudio ? Path.GetFileName(Project.AudioPath) : "None selected");
        GUI.Label(new Rect(90, ay + 2, 230, 24), audioName, hasAudio ? Theme.Label : Theme.MutedSmall);

        if (GUI.Button(new Rect(325, ay, 76, 26), "Browse..."))
        {
            string chosen = Win32Utilities.OpenFile("Select audio file", "Audio Files (*.mp3;*.wav;*.ogg)\0*.mp3;*.wav;*.ogg\0All Files\0*.*\0\0", SaveUtilities.ProjectDirectory);
            if (!string.IsNullOrEmpty(chosen))
            {
                Project.AudioPath = chosen;
                LoadAudio(chosen);
            }
        }

        if (hasAudio)
        {
            if (GUI.Button(new Rect(405, ay, 26, 26), "✕"))
            {
                Project.AudioPath = null;
                LoadAudio(null);
            }
        }

        GUI.Label(new Rect(445, ay + 2, 75, 24), "Start (m:s):", Theme.Label);
        if (_startTimeStr == null) _startTimeStr = FormatTime(Project.AudioStartTime);
        string newStartStr = GUI.TextField(new Rect(522, ay, 65, 24), _startTimeStr);
        if (newStartStr != _startTimeStr)
        {
            _startTimeStr = newStartStr;
            Project.AudioStartTime = ParseTime(newStartStr);
            SyncAudio(forceSeek: true);
        }

        GUI.Label(new Rect(605, ay + 2, 30, 24), "Vol:", Theme.Label);
        float newVol = Widgets.Slider("player.volume", new Rect(638, ay + 2, 95, 20), _volume, 0f, 1f);
        if (Mathf.Abs(newVol - _volume) > 0.001f)
        {
            _volume = newVol;
            if (_audioSource != null) _audioSource.volume = _volume;
        }
        GUI.Label(new Rect(738, ay + 2, 38, 24), $"{(int)(_volume * 100f)}%", Theme.MutedSmall);

        if (count == 0)
        {
            IsPlaying = false;
            SyncAudio(forceSeek: false);
            return;
        }

        if (HeadPosition != _LastHeadPosition)
        {
            IsPlaying = false;
            ApplyFrame();
            SyncAudio(forceSeek: true);
        }

        // Advance by real time, once per rendered frame, so playback runs at the project's FPS
        // no matter how many times OnGUI is called or what the game's framerate is.
        if (IsPlaying && Event.current.type == EventType.Repaint)
        {
            _frameAccumulator += Time.unscaledDeltaTime * Project.FPS;
            int step = Mathf.FloorToInt(_frameAccumulator);
            _frameAccumulator -= step;

            if (step > 0)
            {
                HeadPosition = (HeadPosition + step) % count;
                ApplyFrame();
                SyncAudio(forceSeek: false);
            }
        }
        else if (!IsPlaying)
        {
            _frameAccumulator = 0f;
            CameraManager.Instance.Blur.Set(false, 0f);
            SyncAudio(forceSeek: false);
        }

        _LastHeadPosition = HeadPosition;
    }

    public static string FormatTime(float seconds)
    {
        if (seconds < 0f) seconds = 0f;
        int m = (int)(seconds / 60f);
        float s = seconds % 60f;
        if (Mathf.Abs(s - Mathf.Round(s)) < 0.01f)
            return $"{m}:{Mathf.RoundToInt(s):00}";
        return $"{m}:{s:00.##}";
    }

    public static float ParseTime(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return 0f;
        str = str.Trim();
        if (str.Contains(":"))
        {
            string[] parts = str.Split(':');
            if (parts.Length == 2 && float.TryParse(parts[0], out float m) && float.TryParse(parts[1], out float s))
                return Mathf.Max(0f, m * 60f + s);
        }
        if (float.TryParse(str, out float sec))
            return Mathf.Max(0f, sec);
        return 0f;
    }

    private static void EnsureAudioSource()
    {
        if (_audioSource == null)
        {
            GameObject go = new GameObject("MonkeFrames_PlayerAudio");
            Object.DontDestroyOnLoad(go);
            _audioSource = go.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.loop = false;
            _audioSource.spatialBlend = 0f;
            _audioSource.volume = _volume;
        }
    }

    private void LoadAudio(string path)
    {
        _loadedPath = path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _loadedClip = null;
            if (_audioSource != null)
            {
                _audioSource.Stop();
                _audioSource.clip = null;
            }
            return;
        }

        EnsureAudioSource();
        _isLoadingAudio = true;
        CameraManager.Instance.StartCoroutine(LoadAudioCoroutine(path));
    }

    private IEnumerator LoadAudioCoroutine(string path)
    {
        AudioType type = AudioType.UNKNOWN;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".wav") type = AudioType.WAV;
        else if (ext == ".ogg") type = AudioType.OGGVORBIS;
        else if (ext == ".mp3") type = AudioType.MPEG;

        string uri = new System.Uri(Path.GetFullPath(path)).AbsoluteUri;
        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(uri, type))
        {
            yield return www.SendWebRequest();
            _isLoadingAudio = false;
            if (string.IsNullOrEmpty(www.error))
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                if (clip != null)
                {
                    clip.name = Path.GetFileName(path);
                    _loadedClip = clip;
                    if (_audioSource != null)
                        _audioSource.clip = clip;
                    UIManager.Instance.Status = $"Loaded audio file: {Path.GetFileName(path)}";
                    SyncAudio(forceSeek: true);
                }
            }
            else
            {
                UIManager.Instance.Status = $"Failed to load audio: {www.error}";
            }
        }
    }

    private void SyncAudio(bool forceSeek = false)
    {
        if (_audioSource == null || _loadedClip == null) return;

        _audioSource.volume = _volume;

        int count = Project?.CompiledKeyframes?.Count ?? 0;
        float animTime = count > 0 && Project != null ? (HeadPosition / (float)Project.FPS) : 0f;
        float targetTime = (Project?.AudioStartTime ?? 0f) + animTime;

        if (IsPlaying)
        {
            if (targetTime >= 0f && targetTime < _loadedClip.length)
            {
                if (!_audioSource.isPlaying || forceSeek || Mathf.Abs(_audioSource.time - targetTime) > 0.08f)
                {
                    _audioSource.time = targetTime;
                    if (!_audioSource.isPlaying)
                        _audioSource.Play();
                }
            }
            else
            {
                if (_audioSource.isPlaying)
                    _audioSource.Pause();
            }
        }
        else
        {
            if (_audioSource.isPlaying)
                _audioSource.Pause();

            if (forceSeek && targetTime >= 0f && targetTime < _loadedClip.length)
                _audioSource.time = targetTime;
        }
    }

    private int _lastHash;

    private int ProjectHash()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + Project.FPS;
            h = h * 31 + Project.Smoothness.GetHashCode();
            foreach (var k in Project.Keyframes)
            {
                h = h * 31 + k.Position.GetHashCode();
                h = h * 31 + k.Rotation.GetHashCode();
                h = h * 31 + k.FieldOfView.GetHashCode();
                h = h * 31 + (int)k.Transition.Effect;
                h = h * 31 + k.Transition.Duration.GetHashCode();
                h = h * 31 + (k.MotionBlur ? 1 : 0);
                h = h * 31 + k.MotionBlurStrength.GetHashCode();
                h = h * 31 + k.Transition.CurveX1.GetHashCode() ^ k.Transition.CurveY1.GetHashCode();
                h = h * 31 + k.Transition.CurveX2.GetHashCode() ^ k.Transition.CurveY2.GetHashCode();
                h = h * 31 + (k.Transition.CustomSpeed ? 1 : 0);
            }
            return h;
        }
    }

    private void ApplyFrame()
    {
        var f = Project.CompiledKeyframes[HeadPosition];

        // Motion blur only makes sense while the camera is actually moving (playing), not when scrubbing a still frame.
        CameraManager.Instance.Blur.Set(IsPlaying && f.MotionBlur, f.MotionBlurStrength);
        CameraManager.Instance.Position = f.Position;
        CameraManager.Instance.Rotation = f.QuatRotation;
        CameraManager.Instance.FieldOfView = f.FieldOfView;
    }

    public void OnClose()
    {
        IsPlaying = false;
        CameraManager.Instance.Blur.Set(false, 0f);
        if (_audioSource != null && _audioSource.isPlaying)
            _audioSource.Pause();
    }

    public void OnOpen()
    {
        _ = Project.Build();
        _lastHash = ProjectHash();
        HeadPosition = Mathf.Clamp(HeadPosition, 0, Mathf.Max((Project.CompiledKeyframes?.Count ?? 1) - 1, 0));
        _LastHeadPosition = HeadPosition;

        if (!string.IsNullOrEmpty(Project.AudioPath))
        {
            if (_loadedPath != Project.AudioPath)
                LoadAudio(Project.AudioPath);
            _startTimeStr = FormatTime(Project.AudioStartTime);
        }
        else
        {
            _startTimeStr = "0:00";
        }
    }
}
