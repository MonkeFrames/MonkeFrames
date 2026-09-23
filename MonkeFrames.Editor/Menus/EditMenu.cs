using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Interfaces;

namespace MonkeFrames.Editor.Menus;

public class EditMenu : IEditorMenu
{
    public string Name => "Edit";
    public int Index => 2;

    [EditorMenuItem("Cut", Shortcut = "^X")]
    public void Cut()
    {
        
    }

    [EditorMenuItem("Copy", Shortcut = "^C")]
    public void Copy()
    {

    }

    [EditorMenuItem("Paste", Shortcut = "^V")]
    public void Paste()
    {

    }

    [EditorMenuItem("Undo", Shortcut = "^Z", Separator = true)]
    public void Undo()
    {

    }

    [EditorMenuItem("Redo", Shortcut = "^Y")]
    public void Redo()
    {

    }
}