using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;
using UnityEngine;

namespace MonkeFrames.Editor.Windows.Project;

public class ProjectSettings : IEditorWindow
{
    public string Name => "Project Settings";
    public Rect Rect => new Rect(140, 70, 460, 250);

    public Compiler.Models.Project Project => KeyframeManager.Instance.Project;

    private static readonly int[] FpsOptions = [30, 60, 120];
    private static readonly string[] FpsLabels = ["30 fps", "60 fps", "120 fps"];

    public void OnDraw()
    {
        float w = Rect.width;
        float x = 18, y = 42;
        float labelW = 110;

        GUI.Label(new Rect(x, y, labelW, 26), new GUIContent("Name", "The display name of your project."));
        Project.Name = GUI.TextField(new Rect(x + labelW, y, w - x * 2 - labelW, 26), Project.Name ?? "");
        y += 36;

        // FPS only accepts 30/60/120, so use a segmented control instead of a free slider.
        GUI.Label(new Rect(x, y, labelW, 28), new GUIContent("Frame rate", "Frames per second of the compiled animation and MP4 export."));
        int fpsIdx = System.Array.IndexOf(FpsOptions, Project.FPS);
        int newIdx = Widgets.Segmented("proj.fps", new Rect(x + labelW, y, w - x * 2 - labelW, 28), fpsIdx, FpsLabels);
        if (newIdx != fpsIdx && newIdx >= 0)
            Project.FPS = FpsOptions[newIdx];
        y += 40;

        GUI.Label(new Rect(x, y, labelW, 24), new GUIContent("Smoothness",
            "How much momentum Smooth keyframes carry through each point. 0 = ease to a stop at every keyframe, 1 = fully flowing, above 1 = extra swoopy."));
        Project.Smoothness = Widgets.Slider("proj.smooth", new Rect(x + labelW, y, w - x * 2 - labelW - 50, 24), Project.Smoothness, 0f, 1.5f);
        GUI.Label(new Rect(w - x - 44, y, 44, 24), Project.Smoothness.ToString("0.00"), Theme.LabelRight);
        y += 26;
        GUI.Label(new Rect(x + labelW, y, w - x * 2 - labelW, 20), "Applies to keyframes using the Smooth transition.", Theme.MutedSmall);

        string hint = string.IsNullOrEmpty(GUI.tooltip) ? "Recompile or press Refresh in the Player to see changes." : GUI.tooltip;
        Theme.Fill(new Rect(10, Rect.height - 36, w - 20, 26), Theme.Surface, 6);
        GUI.Label(new Rect(20, Rect.height - 36, w - 40, 26), hint, Theme.Muted);
        GUI.tooltip = "";
    }
}
