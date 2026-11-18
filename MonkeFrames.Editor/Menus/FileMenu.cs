using MonkeFrames.Compiler.Models;
using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.Utilities;
using System.IO;
using System.Threading.Tasks;

namespace MonkeFrames.Editor.Menus;

public class FileMenu : IEditorMenu
{
    public string Name => "File";
    public int Index => 1;

    [EditorMenuItem("Open", Shortcut = "^O")]
    public void OpenProject()
    {
        string path = Win32Utilities.OpenFile("Select your project", "MonkeFrames project\0*.frames", SaveUtilities.ProjectDirectory);

        if (string.IsNullOrEmpty(path))
            return;

        string projectContent = File.ReadAllText(path);

        if (!SaveUtilities.IsValidJson(projectContent))
        {
            Task.Run(() =>
            {
                Win32Utilities.ShowMessageDialog($"Error: {path}", "The project file provided was not valid.");
            });

            return;
        }

        var project = Project.FromJson(projectContent);

        KeyframeManager.Instance.LoadProject(project);
    }

    // No Ctrl+S here: Ctrl (slow) + S (back) flies the free camera, and a save dialog would pop up mid-shot.
    [EditorMenuItem("Save")]
    public void SaveProject()
    {
        SaveUtilities.Save();
    }

    [EditorMenuItem("Close Project", Separator = true)]
    public void CloseProject()
    {
        KeyframeManager.Instance.CloseProject();
    }

    [EditorMenuItem("Reopen Last Closed")]
    public void ReopenProject()
    {
        KeyframeManager.Instance.ReopenLastClosed();
    }
}
