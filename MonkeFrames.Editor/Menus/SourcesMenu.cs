using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;

namespace MonkeFrames.Editor.Menus;

public class SourcesMenu : IEditorMenu
{
    public string Name => "Sources";
    public int Index => 5;

    [EditorMenuItem("Open Sources")]
    public void Open() => UIManager.Instance.OpenWindow("Sources");

    [EditorMenuItem("Free Camera", Separator = true)]
    public void Free() => CameraModes.Instance?.SetMode(CameraMode.Free);

    [EditorMenuItem("Orbit Gorilla")]
    public void Orbit() => Switch(CameraMode.Orbit);

    [EditorMenuItem("First Person")]
    public void FirstPerson() => Switch(CameraMode.FirstPerson);

    [EditorMenuItem("Follow Gorilla")]
    public void Follow() => Switch(CameraMode.Follow);

    [EditorMenuItem("Over the Shoulder")]
    public void Shoulder() => Switch(CameraMode.Shoulder);

    [EditorMenuItem("Front / Selfie")]
    public void Front() => Switch(CameraMode.Front);

    [EditorMenuItem("Top Down")]
    public void TopDown() => Switch(CameraMode.TopDown);

    [EditorMenuItem("Side View")]
    public void SideView() => Switch(CameraMode.SideView);

    [EditorMenuItem("Hand Cam")]
    public void HandCam() => Switch(CameraMode.HandCam);

    [EditorMenuItem("Tracking Shot")]
    public void Tracking() => Switch(CameraMode.Tracking);

    [EditorMenuItem("Director (Auto Shots)")]
    public void Director() => Switch(CameraMode.Director);

    private static void Switch(CameraMode mode)
    {
        CameraModes.Instance?.SetMode(mode);
        UIManager.Instance.OpenWindow("Sources");
    }
}
