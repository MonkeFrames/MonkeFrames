using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.Replays;

namespace MonkeFrames.Editor.Menus;

public class ReplayMenu : IEditorMenu
{
    public string Name => "Replay";
    public int Index => 6;

    [EditorMenuItem("Open Replays")]
    public void Open() => UIManager.Instance.OpenWindow("Replays");

    [EditorMenuItem("Start / Stop Recording", Shortcut = "F9", Separator = true)]
    public void Record() => ReplayManager.Instance?.ToggleRecording();

    [EditorMenuItem("Play / Pause Replay")]
    public void Play()
    {
        ReplayManager rm = ReplayManager.Instance;
        if (rm?.Clip == null)
        {
            UIManager.Instance.Status = "No replay open. Record one or load one in the Replays window.";
            UIManager.Instance.OpenWindow("Replays");
            return;
        }
        rm.TogglePlay();
    }

    [EditorMenuItem("Show / Hide Replay Gorillas")]
    public void ToggleView()
    {
        ReplayManager rm = ReplayManager.Instance;
        if (rm?.Clip == null) return;
        if (rm.Viewing) rm.StopViewing();
        else rm.StartViewing();
    }

    [EditorMenuItem("Replay Studio Layout")]
    public void Studio()
    {
        ReplayStudio s = ReplayStudio.Instance;
        if (s == null) return;
        s.Enabled = !s.Enabled;
        ReplayManager rm = ReplayManager.Instance;
        if (s.Enabled && rm?.Clip != null && !rm.Viewing) rm.StartViewing();
        UIManager.Instance.Status = s.Enabled
            ? (rm?.Clip != null ? "Replay Studio on." : "Replay Studio will open when you record or load a replay.")
            : "Replay Studio off.";
    }

    [EditorMenuItem("Save Replay")]
    public void Save() => ReplayManager.Instance?.Save();

    [EditorMenuItem("Close Replay")]
    public void Close() => ReplayManager.Instance?.Unload();

    [EditorMenuItem("Open Replays Folder", Separator = true)]
    public void Folder() => ReplayManager.Instance?.OpenFolder();
}
