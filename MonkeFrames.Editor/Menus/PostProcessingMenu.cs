using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;

namespace MonkeFrames.Editor.Menus;

public class PostProcessingMenu : IEditorMenu
{
    public string Name => "Post Processing";
    public int Index => 7;

    [EditorMenuItem("Open Post Processing")]
    public void Open() => UIManager.Instance.OpenWindow("Post Processing");

    [EditorMenuItem("Bloom / Emission", Separator = true)]
    public void Bloom() => Toggle(ref Settings.current.Bloom, "Bloom");

    [EditorMenuItem("Chromatic Aberration")]
    public void CA() => Toggle(ref Settings.current.ChromaticAberration, "Chromatic aberration");

    [EditorMenuItem("Motion Blur")]
    public void MotionBlur() => Toggle(ref Settings.current.GlobalMotionBlur, "Motion blur");

    [EditorMenuItem("Depth of Field")]
    public void DoF() => Toggle(ref Settings.current.DepthOfField, "Depth of field");

    [EditorMenuItem("Vignette")]
    public void Vignette() => Toggle(ref Settings.current.Vignette, "Vignette");

    private static void Toggle(ref bool value, string name)
    {
        value = !value;
        UIManager.Instance.Status = $"{name} {(value ? "on" : "off")}.";
        Settings.Save();
    }
}
