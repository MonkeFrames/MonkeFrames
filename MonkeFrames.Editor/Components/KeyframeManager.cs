using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MonkeFrames.Compiler.Models;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Utilities;
using UnityEngine;
using UnityEngine.InputSystem;
using Keyframe = MonkeFrames.Compiler.Models.Keyframe;

namespace MonkeFrames.Editor.Components;

public class KeyframeManager : MonoBehaviour
{
    public static KeyframeManager Instance;
    public Project Project;

    public Dictionary<Keyframe, GameObject> Objects = new Dictionary<Keyframe, GameObject>();

    public KeyframeManager()
    {
        Instance = this;
    }

    public GameObject CreateOrb(string name)
    {
        GameObject mainOrb = new GameObject(name);

        LineRenderer line = mainOrb.AddComponent<LineRenderer>();

        line.startColor = Settings.current.AccentColor;
        line.endColor = Settings.current.AccentColor;
        line.startWidth = 0.05f;
        line.endWidth = 0.15f;

        line.SetPosition(0, mainOrb.transform.position);
        line.SetPosition(1, mainOrb.transform.position + (mainOrb.transform.forward * 0.25f));

        line.material.shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");

        return mainOrb;
    }

    public void CreateOrb(Keyframe keyframe)
    {
        GameObject mainOrb = CreateOrb($"MonkeFrames keyframe visual");

        mainOrb.transform.position = keyframe.Position;
        mainOrb.transform.rotation = keyframe.QuatRotation;

        LineRenderer line = mainOrb.GetComponent<LineRenderer>();

        line.SetPosition(0, mainOrb.transform.position);
        line.SetPosition(1, mainOrb.transform.position + (mainOrb.transform.forward * 0.25f));

        Objects.TryAdd(keyframe, mainOrb);
    }

    public void Start()
    {
        Instance = this;
        Console.WriteLine("[MonkeFrames::KeyframeManager] creating new project \"new project\"");

        Project = new Project("new project", Constants.Exporter);
        Project.FPS = 60;

        Console.WriteLine("[MonkeFrames::KeyframeManager] Keyframe manager is running");
    }

    public void Update()
    {
        if (CameraManager.Instance.InPlayback)
            return;

        // Don't treat typing in a text field (keyframe values, room codes...) as shortcuts.
        if (GUIUtility.keyboardControl != 0)
            return;

        if (Keyboard.current.vKey.wasPressedThisFrame)
            CreateKeyframe();

        if (Keyboard.current.fKey.wasPressedThisFrame && UIManager.Instance.Selection != -1)
            GoToKeyframe(UIManager.Instance.Selection);

        if (Keyboard.current.tKey.wasPressedThisFrame)
            CreateKeyframe(lookAtPlayer: true);

        if (Keyboard.current.xKey.wasPressedThisFrame && UIManager.Instance.Selection != -1)
            CreateKeyframe(replaceKeyframeIdx: UIManager.Instance.Selection);

        if (Keyboard.current.deleteKey.wasPressedThisFrame && UIManager.Instance.Selection != -1)
            DeleteKeyframe(UIManager.Instance.Selection);
    }

    /// <summary>Move the camera to a keyframe's position, rotation and FOV.</summary>
    public void GoToKeyframe(int index)
    {
        if (index < 0 || index >= Project.Keyframes.Count)
            return;

        Keyframe k = Project.Keyframes[index];

        CameraManager.Instance.CancelLookSmoothing();
        CameraManager.Instance.Position = k.Position;
        CameraManager.Instance.Rotation = k.QuatRotation;
        CameraManager.Instance.FieldOfView = k.FieldOfView;
    }

    public void LoadProject(Project p)
    {
        UIManager.Instance.Selection = -1;
        Project = p;
        RefreshOrbs();
    }

    public bool IsCompiling;

    public void StartBuild()
    {
        Task.Run(async () => {
            IsCompiling = true;
            await Task.Delay(100); // give frame time to process
            await Project.Build();
            IsCompiling = false;
        }); 
    }

    public void StartBuildAndRun()
    {
        Task.Run(async () => {
            IsCompiling = true;
            await Task.Delay(100); // give frame time to process
            await Project.Build();
            IsCompiling = false;
            CameraManager.Instance.StartPlayback();
        });
    }

    public Keyframe CreateKeyframe(int replaceKeyframeIdx = -1, bool lookAtPlayer = false)
    {
        Keyframe k = new Keyframe();

        k.Position = CameraManager.Instance.Position;
        k.Rotation = CameraManager.Instance.Rotation.eulerAngles;
        k.FieldOfView = CameraManager.Instance.FieldOfView;

        if (lookAtPlayer)
            k.Rotation = Quaternion.LookRotation(GorillaTagger.Instance.headCollider.transform.position - k.Position).eulerAngles;

        k.Position = new Vector3(MathF.Round(k.Position.x, 2), MathF.Round(k.Position.y, 2), MathF.Round(k.Position.z, 2));
        k.Rotation = new Vector3(MathF.Round(k.Rotation.x, 2), MathF.Round(k.Rotation.y, 2), MathF.Round(k.Rotation.z, 2));

        k.Transition.Duration = 5f;

        if (Settings.current?.SmoothByDefault == true)
            k.Transition.Effect = TransitionEffect.Smooth;

        if (replaceKeyframeIdx != -1)
        {
            Project.Keyframes.RemoveAt(replaceKeyframeIdx);
            Project.Keyframes.Insert(replaceKeyframeIdx, k);
        } else
        {
            Project.Keyframes.Add(k);
            UIManager.Instance.Selection = Project.Keyframes.IndexOf(k);
        }

        CreateOrb(k);
        
        string posStr = $"Position: {UnityUtilities.Vector3ToString(k.Position)}";
        string rotStr = $"Rotation: {UnityUtilities.Vector3ToString(k.Rotation)}";

        UIManager.Instance.Status = $"Created keyframe {Project.Keyframes.IndexOf(k)} with properties {{ {posStr}, {rotStr}, FOV: {k.FieldOfView} }} ";

        return k;
    }

    public void DeleteKeyframe(int index)
    {
        try {
            Objects[Project.Keyframes[index]].Destroy();
            Objects.Remove(Project.Keyframes[index]);
            Project.Keyframes.RemoveAt(index);
        } catch { };
        // UIManager.Instance.SelectedKeyframeIndex = -1;
    }

    public void DeleteOrbs()
    {
        Objects.Values.ForEach(g => g.Destroy());
        Objects.Clear();
    }

    public void RefreshOrbs()
    {
        DeleteOrbs();
        Project?.Keyframes.ForEach(CreateOrb);
    }
}