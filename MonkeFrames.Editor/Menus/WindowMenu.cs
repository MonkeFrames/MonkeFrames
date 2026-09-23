using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;

namespace MonkeFrames.Editor.Menus;

public class WindowMenu : IEditorMenu
{
    public string Name => "Window";
    public int Index => 4;

    [EditorMenuItem("Keyframe Editor")]
    public void KeyframeManager()
    {
        UIManager.Instance.ToggleWindow("Keyframe Editor");
    }

    [EditorMenuItem("Player")]
    public void Player()
    {
        UIManager.Instance.ToggleWindow("Player");
    }


    [EditorMenuItem("Room Manager")]
    public void RoomManager()
    {
        UIManager.Instance.ToggleWindow("Room Manager");
    }

    [EditorMenuItem("Environment Manager")]
    public void MapLoader()
    {
        UIManager.Instance.ToggleWindow("Environment Manager");
    }
}