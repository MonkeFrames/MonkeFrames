using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;

namespace MonkeFrames.Editor.Menus;

public class HelpMenu : IEditorMenu
{
    public string Name => "Help";
    public int Index => 99;

    [EditorMenuItem("Join Discord")]
    public void JoinDiscordButton()
    {
        
    }
}