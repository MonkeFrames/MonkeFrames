using MonkeFrames.Compiler.Models;
using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.Utilities;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEngine.ProBuilder;

namespace MonkeFrames.Editor.Menus;

public class ProjectMenu : IEditorMenu
{
    public string Name => "Project";
    public int Index => 3;

    [EditorMenuItem("Project Settings")]
    public void OpenProjectSettings()
    {
        UIManager.Instance.ToggleWindow("Project Settings");
    }

    [EditorMenuItem("Load Project", Separator = true)]
    public void LoadProject()
    {
        string path = Win32Utilities.OpenFile("Select your project", "MonkeFrames project\0*.frames", SaveUtilities.ProjectDirectory);

        if (string.IsNullOrEmpty(path))
            return;

        string projectContent = File.ReadAllText(path);

        if (!SaveUtilities.IsValidJson(projectContent)) {
            Task.Run(() =>
            {
                Win32Utilities.ShowMessageDialog($"Error: {path}", "The project file provided was not valid.");
            });

            return;
        }

        var project = Project.FromJson(projectContent);

        KeyframeManager.Instance.LoadProject(project);
    }

    [EditorMenuItem("Save Project")]
    public void SaveProject() 
    {
        SaveUtilities.Save();
    }

    [EditorMenuItem("Export to MP4", Separator = true)]
    public void ExportProject()
    {
        KeyframeManager.Instance.Project.Build().Wait();
        CameraManager.Instance.StartRecording();
    }

    [EditorMenuItem("Compile")]
    public void CompileProject()
    {
        KeyframeManager.Instance.StartBuild();
    }

    [EditorMenuItem("Play")]
    public void PlayProject()
    {
        KeyframeManager.Instance.StartBuildAndRun();
    }
}