using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;

namespace MonkeFrames.Editor.Menus;

public class EditMenu : IEditorMenu
{
    public string Name => "Edit";
    public int Index => 2;

    [EditorMenuItem("Cut", Shortcut = "^X")]
    public void Cut()
    {
        KeyframeEditHistory.CutSelection();
    }

    [EditorMenuItem("Copy", Shortcut = "^C")]
    public void Copy()
    {
        KeyframeEditHistory.CopySelection();
    }

    [EditorMenuItem("Paste", Shortcut = "^V")]
    public void Paste()
    {
        KeyframeEditHistory.PasteAfterSelection();
    }

    [EditorMenuItem("Undo", Shortcut = "^Z", Separator = true)]
    public void Undo()
    {
        KeyframeEditHistory.Undo();
    }

    [EditorMenuItem("Redo", Shortcut = "^Y")]
    public void Redo()
    {
        KeyframeEditHistory.Redo();
    }
}
