using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;
using UnityEngine;

namespace MonkeFrames.Editor.Windows;

public class Player : IEditorWindow
{
    public string Name => "Player";
    public Rect Rect => new Rect(Screen.width / 2f - 450, Screen.height - 160, 900, 118);

    public Compiler.Models.Project Project => KeyframeManager.Instance.Project;

    public bool IsPlaying = false;
    public int HeadPosition = 0;
    private int _LastHeadPosition = 0;
    private float _frameAccumulator;

    public void OnDraw()
    {
        float w = Rect.width;

        if (CameraManager.Instance.InPlayback)
        {
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
                Project.Build();
                HeadPosition = Mathf.Clamp(HeadPosition, 0, Mathf.Max((Project.CompiledKeyframes?.Count ?? 1) - 1, 0));
                _LastHeadPosition = HeadPosition;
            }
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
        float y = 76;
        if (GUI.Button(new Rect(16, y, 96, 28), IsPlaying ? "Pause" : "Play", Theme.AccentButton))
            IsPlaying = !IsPlaying;

        if (GUI.Button(new Rect(118, y, 96, 28), "Refresh"))
        {
            Project.Build();
            UIManager.Instance.Status = $"Rebuilt preview: {Project.CompiledKeyframes?.Count ?? 0} frames.";
        }

        float seconds = count == 0 ? 0 : HeadPosition / (float)Project.FPS;
        float total = count == 0 ? 0 : count / (float)Project.FPS;
        string label = count != 0
            ? $"Frame {HeadPosition + 1} / {count}     {seconds:0.00}s / {total:0.00}s     {Project.FPS} FPS"
            : $"No frames yet: add keyframes and press Refresh  ({Project.FPS} FPS)";
        GUI.Label(new Rect(228, y, w - 244, 28), label, Theme.MutedRight);

        if (count == 0)
        {
            IsPlaying = false;
            return;
        }

        if (HeadPosition != _LastHeadPosition)
        {
            IsPlaying = false;
            ApplyFrame();
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
            }
        }
        else if (!IsPlaying)
        {
            _frameAccumulator = 0f;
            CameraManager.Instance.Blur.Set(false, 0f);
        }

        _LastHeadPosition = HeadPosition;
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
    }

    public void OnOpen()
    {
        Project.Build().Wait();
        _lastHash = ProjectHash();
        HeadPosition = Mathf.Clamp(HeadPosition, 0, Mathf.Max((Project.CompiledKeyframes?.Count ?? 1) - 1, 0));
        _LastHeadPosition = HeadPosition;
    }
}
