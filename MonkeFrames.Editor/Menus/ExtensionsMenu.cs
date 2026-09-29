using GorillaTagScripts;
using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.Utilities;
using MonkeFrames.Extensions;
using System.Diagnostics;
using System.IO;

namespace MonkeFrames.Editor.Menus;

public class ExtensionsMenu : IEditorMenu
{
    public string Name => "Extensions";
    public int Index => 8;

    [EditorMenuItem("View Extensions")]
    public void ViewExtensionsButton()
    {
        FramesExtension.ShowMessageBox("Error", "Not implemented", icon: FramesExtension.MessageBoxIcon.Error);
    }

    [EditorMenuItem("Install", Separator = true)]
    public void InstallButton()
    {
        string filePath = Win32Utilities.OpenFile(
            "Select extension file",
            "MonkeFrames extension (*.mfextension)\0*.mfextension\0All Files\0*.*",
            SystemUtilities.GetFolder(System.Environment.SpecialFolder.UserProfile));

        if (string.IsNullOrEmpty(filePath))
            return;

        string extensionPath = Path.Combine(Constants.DataFolder, "extensions", Path.GetFileName(filePath));
        File.Copy(filePath, extensionPath);

        FramesExtension.ShowMessageBox(
            $"[MonkeFrames {Constants.VersionID}] Restart to apply changes",
            "You must restart MonkeFrames to reload extensions.",
            icon: FramesExtension.MessageBoxIcon.Information);
    }

    [EditorMenuItem("Uninstall")]
    public void UninstallButton()
    {
        string filePath = Win32Utilities.OpenFile(
            "Select extension file",
            "MonkeFrames extension (*.mfextension)\0*.mfextension\0All Files\0*.*",
            Path.Combine(Constants.DataFolder, "extensions"));

        if (string.IsNullOrEmpty(filePath))
            return;

        File.Delete(filePath);

        FramesExtension.ShowMessageBox(
            $"[MonkeFrames {Constants.VersionID}] Restart to apply changes",
            "You must restart MonkeFrames to reload extensions.",
            icon: FramesExtension.MessageBoxIcon.Information);
    }

    [EditorMenuItem("Open Extensions Folder", Separator = true)]
    public void OpenExtensionsFolderButton()
    {
        Process.Start("explorer.exe", $"\"{Path.Combine(Constants.DataFolder, "extensions")}\"");
    }
}