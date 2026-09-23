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
    public int Index => 8;

    [EditorMenuItem("Project Settings")]
    public void OpenProjectSettings()
    {
        UIManager.Instance.ToggleWindow("Project Settings");
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