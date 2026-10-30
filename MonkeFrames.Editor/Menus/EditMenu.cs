using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;

namespace MonkeFrames.Editor.Menus;

public sealed class EditMenu : IEditorMenu
{
    public string Name => "Edit";
    public int Index => 9;
    [EditorMenuItem("Undo", Shortcut = "^Z")]
    public void Undo() => KeyframeManager.Instance.Undo();
    [EditorMenuItem("Redo", Shortcut = "^Y")]
    public void Redo() => KeyframeManager.Instance.Redo();
    [EditorMenuItem("Cut", Shortcut = "^X", Separator = true)]
    public void Cut() => KeyframeManager.Instance.CutSelection();
    [EditorMenuItem("Copy", Shortcut = "^C")]
    public void Copy() => KeyframeManager.Instance.CopySelection();
    [EditorMenuItem("Paste", Shortcut = "^V")]
    public void Paste() => KeyframeManager.Instance.Paste();
}
