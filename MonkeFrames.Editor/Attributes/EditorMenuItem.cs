using System;

namespace MonkeFrames.Editor.Attributes;

public class EditorMenuItem : Attribute
{
    public string Name;
    public Action Action;

    /// <summary>Optional keyboard hint shown right-aligned in the dropdown (display only).</summary>
    public string Shortcut;

    /// <summary>Draw a separator line above this item.</summary>
    public bool Separator;

    public EditorMenuItem(string name) =>
        Name = name;
}
