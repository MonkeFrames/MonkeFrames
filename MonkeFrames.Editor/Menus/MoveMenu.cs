using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using UnityEngine;

namespace MonkeFrames.Editor.Menus;

using Keyframe = MonkeFrames.Compiler.Models.Keyframe;

public class MoveMenu : IEditorMenu
{
    public string Name => "Move";
    public int Index => 3;

    [EditorMenuItem("To Gorilla")]
    public void ToGorilla()
    {
        Vector3 headPos = GorillaTagger.Instance.headCollider.transform.position;
        Vector3 headRot = GorillaTagger.Instance.headCollider.transform.rotation.eulerAngles;

        CameraManager.Instance.Position = headPos;
        CameraManager.Instance.Rotation = Quaternion.Euler(headRot);
    }

    [EditorMenuItem("To Selected Keyframe", Shortcut = "F")]
    public void ToSelectedKeyframe()
    {
        if (UIManager.Instance.Selection == -1)
        {
            UIManager.Instance.Status = "Please select a keyframe in the keyframe editor before performing this action.";
            return;
        }

        Keyframe k = KeyframeManager.Instance.Project.Keyframes[UIManager.Instance.Selection];
        
        CameraManager.Instance.Position = k.Position;
        CameraManager.Instance.Rotation = k.QuatRotation;
        CameraManager.Instance.FieldOfView = k.FieldOfView;
    }
}