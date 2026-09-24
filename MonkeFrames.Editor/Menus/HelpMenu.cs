using System.Diagnostics;
using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Interfaces;

namespace MonkeFrames.Editor.Menus;

public class HelpMenu : IEditorMenu
{
    public string Name => "Help";
    public int Index => 99;

    [EditorMenuItem("Issues / Support")]
    public void JoinDiscordButton()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://monkeframes.sirkingbinx.dev",
            UseShellExecute = true
        });
    }

    [EditorMenuItem("Donate")]
    public void DonateButton()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/sponsors/MonkeFrames",
            UseShellExecute = true
        });
    }

    [EditorMenuItem("Source Code", Separator = true)]
    public void SourceButton()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/MonkeFrames/MonkeFrames",
            UseShellExecute = true
        });
    }
}