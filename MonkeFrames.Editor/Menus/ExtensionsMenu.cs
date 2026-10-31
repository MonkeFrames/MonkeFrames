using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.Utilities;
using System.Diagnostics;
using System.IO;

namespace MonkeFrames.Editor.Menus;

public class ExtensionsMenu : IEditorMenu
{
    public string Name => "Extensions";
    public int Index => 8;

    [EditorMenuItem("Extensions Gallery")]
    public void OpenExtensionsGallery()
    {
        UIManager.Instance.OpenWindow("Extension Marketplace");
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
        File.Copy(filePath, extensionPath, true);

        UIManager.Instance.ReloadExtensions();
        UIManager.Instance.Status = "Extension installed.";
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

        UIManager.Instance.ReloadExtensions();
        UIManager.Instance.Status = "Extension uninstalled.";
    }

    [EditorMenuItem("Reload")]
    public void ReloadButton()
    {
        UIManager.Instance.ReloadExtensions();
    }

    [EditorMenuItem("Open Extensions Folder", Separator = true)]
    public void OpenExtensionsFolderButton()
    {
        Process.Start("explorer.exe", $"\"{Path.Combine(Constants.DataFolder, "extensions")}\"");
    }
}
