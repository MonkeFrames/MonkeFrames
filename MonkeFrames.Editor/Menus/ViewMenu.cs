using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;

namespace MonkeFrames.Editor.Menus;

public sealed class ViewMenu : IEditorMenu
{
    public string Name => "View";
    public int Index => 10;
    [EditorMenuItem("Hide UI", Shortcut = "^H")]
    public void ToggleUI() => UIManager.Instance.ToggleUI();
}
