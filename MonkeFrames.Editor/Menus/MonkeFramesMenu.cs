using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;

namespace MonkeFrames.Editor.Menus;

public class MonkeFramesMenu : IEditorMenu
{
    public string Name => "MonkeFrames";
    public int Index => 0;

    [EditorMenuItem("Enter/Exit", Shortcut = "F1")]
    public void Disable()
    {
        CameraManager.Instance.SetModEnabled(false);
    }

    [EditorMenuItem("Settings")]
    public void Settings()
    {
        UIManager.Instance.ToggleWindow("Settings");
    }

    [EditorMenuItem("About MonkeFrames", Separator = true)]
    public void About()
    {
        UIManager.Instance.ToggleWindow("About MonkeFrames");
    }
}