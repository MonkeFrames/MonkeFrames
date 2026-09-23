using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;
using MonkeFrames.Editor.Utilities;
using System.Collections.Generic;
using UnityEngine;

namespace MonkeFrames.Editor.Windows;

public class SettingsWindow : IEditorWindow
{
    public string Name => "Settings";
    public Rect Rect => new Rect(120, 50, 480, 560);

    public List<Color> colors = [
        Color.red,
        Color.orange,
        Color.yellow,
        Color.green,
        Color.cyan,
        Color.blue,
        Color.purple,
        Color.magenta,
    ];

    public void OnDraw()
    {
        float w = Rect.width;
        float x = 18, y = 42;

        // ---- Appearance ----
        GUI.Label(new Rect(x, y, 200, 20), "APPEARANCE", Theme.Header);
        y += 24;

        GUI.Label(new Rect(x, y, 120, 28), new GUIContent("Accent colour", "Used for highlights, keyframe markers and the camera line."));
        int current = colors.FindIndex(c => c == Settings.current.AccentColor);
        int chosen = Widgets.Swatches("accent", new Rect(x + 120, y, w - x * 2 - 120, 28), current, colors, 24f);
        if (chosen != current && chosen >= 0)
        {
            Settings.current.AccentColor = colors[chosen];
            UIManager.Instance.Status = $"Accent colour set to {UnityUtilities.ColorToString(colors[chosen])}.";
        }
        y += 38;

        Settings.current.Animations = Widgets.Switch("anim", new Rect(x, y, 260, 26), Settings.current.Animations,
            "UI animations", "Smooth window, menu and hover transitions.");
        y += 32;

        Settings.current.ShowIntro = Widgets.Switch("intro", new Rect(x, y, w - x * 2, 26), Settings.current.ShowIntro,
            "Intro on startup", "Play the animated MonkeFrames logo when the mod loads.");
        y += 32;

        bool soundWas = Settings.current.NotificationSound;
        Settings.current.NotificationSound = Widgets.Switch("notifsound", new Rect(x, y, w - x * 2, 26), Settings.current.NotificationSound,
            "Notification sound", "Play a soft chime when a notification pops up.");
        if (Settings.current.NotificationSound && !soundWas)
            NotificationSound.Play(force: true);
        y += 32;

        GUI.enabled = Settings.current.NotificationSound;
        GUI.Label(new Rect(x, y, 120, 24), new GUIContent("Sound volume", "How loud the notification chime is."));
        Settings.current.NotificationVolume = Widgets.Slider("notifvol", new Rect(x + 120, y, w - x * 2 - 234, 24),
            Settings.current.NotificationVolume, 0f, 1f);
        GUI.Label(new Rect(w - x - 108, y, 40, 24), $"{Settings.current.NotificationVolume * 100f:0}%", Theme.LabelRight);
        if (GUI.Button(new Rect(w - x - 62, y, 62, 24), "Test"))
            NotificationSound.Play(force: true);
        GUI.enabled = true;
        y += 34;

        GUI.enabled = Settings.current.Animations;
        GUI.Label(new Rect(x, y, 120, 24), new GUIContent("Animation speed", "How quickly transitions play."));
        Settings.current.AnimationSpeed = Widgets.Slider("animspeed", new Rect(x + 120, y, w - x * 2 - 170, 24),
            Settings.current.AnimationSpeed, 0.5f, 2f);
        GUI.Label(new Rect(w - x - 44, y, 44, 24), $"{Settings.current.AnimationSpeed:0.0}x", Theme.LabelRight);
        GUI.enabled = true;
        y += 38;

        Widgets.Divider(x, y - 8, w - x * 2);

        // ---- Editing ----
        GUI.Label(new Rect(x, y, 200, 20), "EDITING", Theme.Header);
        y += 24;

        Settings.current.Autosave = Widgets.Switch("autosave", new Rect(x, y, w - x * 2, 26), Settings.current.Autosave,
            "Autosave projects", "Automatically save any named project when its keyframes are changed.");
        y += 32;

        Settings.current.SmoothByDefault = Widgets.Switch("smoothdefault", new Rect(x, y, w - x * 2, 26), Settings.current.SmoothByDefault,
            "Smooth keyframes by default", "New keyframes use the Smooth transition, so the camera flows through them without stopping.");
        y += 40;

        Widgets.Divider(x, y - 8, w - x * 2);

        // ---- Camera ----
        GUI.Label(new Rect(x, y, 200, 20), "CAMERA", Theme.Header);
        y += 24;

        Settings.current.SmoothMouseLook = Widgets.Switch("smoothlook", new Rect(x, y, w - x * 2 - 90, 26), Settings.current.SmoothMouseLook,
            "Smooth mouse look", "Cinematic, floaty mouse look (left-drag) and tilt (right-drag). Press Caps Lock at any time to toggle it.");
        Rect key = new Rect(w - x - 80, y + 3, 80, 20);
        Theme.Fill(key, new Color(1, 1, 1, 0.08f), 5);
        Theme.DrawText(key, "Caps Lock", Theme.LabelCenterSmall, Theme.Text);
        y += 34;

        GUI.enabled = Settings.current.SmoothMouseLook;
        GUI.Label(new Rect(x, y, 120, 24), new GUIContent("Look smoothing", "Light = responsive, Heavy = slow and floaty."));
        Settings.current.MouseSmoothing = Widgets.Slider("mousesmooth", new Rect(x + 120, y, w - x * 2 - 190, 24),
            Settings.current.MouseSmoothing, 0f, 1f);
        float ms = Settings.current.MouseSmoothing;
        GUI.Label(new Rect(w - x - 64, y, 64, 24), ms < 0.34f ? "Light" : ms < 0.67f ? "Medium" : "Heavy", Theme.LabelRight);
        GUI.enabled = true;

        // ---- Hint bar ----
        string hint = string.IsNullOrEmpty(GUI.tooltip) ? "Changes are saved when you close this window." : GUI.tooltip;
        Theme.Fill(new Rect(10, Rect.height - 36, w - 20, 26), Theme.Surface, 6);
        GUI.Label(new Rect(20, Rect.height - 36, w - 40, 26), hint, Theme.Muted);
        GUI.tooltip = "";
    }

    public void OnClose()
    {
        KeyframeManager.Instance.RefreshOrbs();
        Settings.Save();
    }
}
