using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Utilities;
using UnityEngine;

namespace MonkeFrames.Editor.Mocap;

public enum MocapRecordingState
{
    Idle,
    Recording,
    ReadyToSave,
    Saving
}

[DefaultExecutionOrder(99)]
public class MocapManager : MonoBehaviour
{
    public static MocapManager Instance { get; private set; }

    public static string RecordingsFolder
    {
        get
        {
            string root = !string.IsNullOrEmpty(Constants.MonkeFramesAssemblyFolder)
                ? Constants.MonkeFramesAssemblyFolder
                : Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";

            return Path.Combine(root, "Recordings");
        }
    }

    public float SampleRate = 24f;

    public MocapRecordingState State { get; private set; } = MocapRecordingState.Idle;
    public MocapRecording CurrentRecording { get; private set; }
    public string StatusMessage { get; private set; } = "Not Recording";
    public string LastSavedFilePath { get; private set; } = "";

    public Transform HeadTarget { get; private set; }
    public Transform BodyTarget { get; private set; }
    public Transform LeftHandTarget { get; private set; }
    public Transform RightHandTarget { get; private set; }
    public VRRig CurrentRig { get; private set; }

    public float ElapsedRecordingTime => _isRecording ? (float)(Time.realtimeSinceStartupAsDouble - _recordingStartTime) : (_recordedDuration);
    public int RecordedFrameCount => CurrentRecording?.Frames?.Count ?? 0;

    private bool _isRecording = false;
    private double _recordingStartTime;
    private float _recordedDuration;
    private float _sampleAccumulator;
    private float _nextRigScanTime;

    public MocapManager()
    {
        Instance = this;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        EnsureDirectories();
        ResolveTrackingTargets();
    }

    public void EnsureDirectories()
    {
        try
        {
            string folder = RecordingsFolder;
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                Console.WriteLine($"[MonkeFrames::Mocap] Created mocap directory: {folder}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::Mocap] Error creating mocap directory: {ex.Message}");
        }
    }

    private void Update()
    {
        if (Time.unscaledTime >= _nextRigScanTime)
        {
            _nextRigScanTime = Time.unscaledTime + 1.0f;
            if (CurrentRig == null || !CurrentRig.isActiveAndEnabled || HeadTarget == null)
            {
                ResolveTrackingTargets();
            }
        }
    }

    private void LateUpdate()
    {
        if (!_isRecording || State != MocapRecordingState.Recording)
            return;

        SampleMotion();
    }

    private static Transform FindChildRecursive(Transform parent, string childName)
    {
        if (parent == null) return null;
        Transform direct = parent.Find(childName);
        if (direct != null) return direct;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (string.Equals(child.name, childName, StringComparison.OrdinalIgnoreCase))
                return child;

            Transform result = FindChildRecursive(child, childName);
            if (result != null) return result;
        }
        return null;
    }

    public bool ResolveTrackingTargets()
    {
        try
        {
            VRRig rig = CameraModes.LocalRig();
            if (rig == null)
            {
                CurrentRig = null;
                HeadTarget = null;
                BodyTarget = null;
                LeftHandTarget = null;
                RightHandTarget = null;
                return false;
            }

            CurrentRig = rig;
            Transform root = rig.transform;

            HeadTarget = root.Find("rig/head")
                ?? FindChildRecursive(root, "head")
                ?? (rig.headMesh != null ? rig.headMesh.transform : null)
                ?? (GorillaTagger.Instance != null && GorillaTagger.Instance.headCollider != null ? GorillaTagger.Instance.headCollider.transform : null)
                ?? (rig.head != null ? rig.head.rigTarget : null)
                ?? rig.headConstraint
                ?? root;

            BodyTarget = root.Find("rig/body_pivot/body/body_new")
                ?? root.Find("rig/body_pivot/body")
                ?? root.Find("rig/body")
                ?? FindChildRecursive(root, "body_new")
                ?? FindChildRecursive(root, "body")
                ?? (rig.mainSkin != null ? rig.mainSkin.transform : null)
                ?? root;

            LeftHandTarget = root.Find("rig/hand.L")
                ?? FindChildRecursive(root, "hand.L")
                ?? (rig.leftHandTransform != null ? rig.leftHandTransform : null)
                ?? (rig.leftHand != null ? rig.leftHand.rigTarget : null)
                ?? (GorillaTagger.Instance != null ? GorillaTagger.Instance.leftHandTransform : null);

            RightHandTarget = root.Find("rig/hand.R")
                ?? FindChildRecursive(root, "hand.R")
                ?? (rig.rightHandTransform != null ? rig.rightHandTransform : null)
                ?? (rig.rightHand != null ? rig.rightHand.rigTarget : null)
                ?? (GorillaTagger.Instance != null ? GorillaTagger.Instance.rightHandTransform : null);

            Console.WriteLine($"[MonkeFrames::Mocap] Resolved tracking targets -> Head: '{HeadTarget?.name}', Body: '{BodyTarget?.name}', LH: '{LeftHandTarget?.name}', RH: '{RightHandTarget?.name}'");
            return HeadTarget != null && LeftHandTarget != null && RightHandTarget != null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::Mocap] Error resolving tracking targets: {ex.Message}");
            return false;
        }
    }

    public bool StartRecording()
    {
        if (State == MocapRecordingState.Recording || State == MocapRecordingState.Saving)
        {
            Console.WriteLine("[MonkeFrames::Mocap] Cannot start recording: already recording or saving.");
            return false;
        }

        if (!ResolveTrackingTargets() || CurrentRig == null)
        {
            StatusMessage = "Cannot record: Local Gorilla rig not found.";
            UIManager.Instance.Status = StatusMessage;
            Console.WriteLine($"[MonkeFrames::Mocap] {StatusMessage}");
            return false;
        }

        EnsureDirectories();

        string playerName = "Gorilla";
        Color playerColor = Color.white;
        try
        {
            playerName = CameraModes.PlayerName(CurrentRig);
            playerColor = CurrentRig.playerColor;
        }
        catch { }

        CurrentRecording = new MocapRecording
        {
            Format = MocapRecording.CurrentFormat,
            FormatVersion = MocapRecording.CurrentFormatVersion,
            SampleRate = Mathf.Clamp(SampleRate, 10f, 120f),
            Metadata = new MocapMetadata
            {
                RecordedAt = DateTime.UtcNow.ToString("o"),
                PlayerName = playerName,
                PlayerColorHex = $"#{ColorUtility.ToHtmlStringRGBA(playerColor)}",
                RecorderVersion = Constants.Version,
                CoordinateSystem = "Unity_LeftHanded_YUp"
            },
            Frames = new List<MocapFrame>()
        };

        _recordingStartTime = Time.realtimeSinceStartupAsDouble;
        _sampleAccumulator = 0f;
        _recordedDuration = 0f;
        _isRecording = true;
        State = MocapRecordingState.Recording;

        StatusMessage = "Recording...";
        UIManager.Instance.Status = "Mocap: Recording local player movement...";

        CaptureFrame(0f);

        return true;
    }

    public void StopRecording()
    {
        if (!_isRecording || State != MocapRecordingState.Recording)
        {
            Console.WriteLine("[MonkeFrames::Mocap] Cannot stop recording: not currently recording.");
            return;
        }

        _isRecording = false;
        _recordedDuration = (float)(Time.realtimeSinceStartupAsDouble - _recordingStartTime);

        if (CurrentRecording == null || CurrentRecording.Frames.Count == 0)
        {
            State = MocapRecordingState.Idle;
            StatusMessage = "No frames were recorded.";
            UIManager.Instance.Status = StatusMessage;
            return;
        }

        CurrentRecording.FrameCount = CurrentRecording.Frames.Count;
        CurrentRecording.Duration = _recordedDuration;

        State = MocapRecordingState.ReadyToSave;
        StatusMessage = $"Recording finished: {CurrentRecording.FrameCount} frames ({_recordedDuration:0.00}s). Ready to save.";
        UIManager.Instance.Status = StatusMessage;
    }

    public void DiscardRecording()
    {
        if (State == MocapRecordingState.Saving)
            return;

        _isRecording = false;
        CurrentRecording = null;
        _recordedDuration = 0f;
        State = MocapRecordingState.Idle;
        StatusMessage = "Not Recording";
        UIManager.Instance.Status = "Mocap recording discarded.";
    }

    private void SampleMotion()
    {
        float interval = 1f / CurrentRecording.SampleRate;
        _sampleAccumulator += Time.unscaledDeltaTime;

        if (_sampleAccumulator > 1.0f)
            _sampleAccumulator = 1.0f;

        while (_sampleAccumulator >= interval)
        {
            _sampleAccumulator -= interval;
            float timestamp = (float)(Time.realtimeSinceStartupAsDouble - _recordingStartTime);
            CaptureFrame(timestamp);
        }
    }

    private void CaptureFrame(float timestamp)
    {
        if (HeadTarget == null || CurrentRig == null)
        {
            if (!ResolveTrackingTargets())
                return;
        }

        Vector3 headPos = HeadTarget != null ? HeadTarget.position : Vector3.zero;
        Quaternion headRot = HeadTarget != null ? HeadTarget.rotation : Quaternion.identity;

        Vector3 bodyPos = BodyTarget != null ? BodyTarget.position : (CurrentRig != null ? CurrentRig.transform.position : Vector3.zero);
        Quaternion bodyRot = BodyTarget != null ? BodyTarget.rotation : (CurrentRig != null ? CurrentRig.transform.rotation : Quaternion.identity);

        Vector3 leftHandPos = LeftHandTarget != null ? LeftHandTarget.position : Vector3.zero;
        Quaternion leftHandRot = LeftHandTarget != null ? LeftHandTarget.rotation : Quaternion.identity;

        Vector3 rightHandPos = RightHandTarget != null ? RightHandTarget.position : Vector3.zero;
        Quaternion rightHandRot = RightHandTarget != null ? RightHandTarget.rotation : Quaternion.identity;

        float leftIndex = 0f;
        float leftMiddle = 0f;
        float leftThumb = 0f;
        float rightIndex = 0f;
        float rightMiddle = 0f;
        float rightThumb = 0f;

        if (ControllerInputPoller.instance != null)
        {
            leftIndex = Mathf.Clamp01(ControllerInputPoller.instance.leftControllerIndexFloat);
            leftMiddle = Mathf.Clamp01(ControllerInputPoller.instance.leftControllerGripFloat);
            leftThumb = (ControllerInputPoller.instance.leftControllerSecondaryButton || ControllerInputPoller.instance.leftControllerPrimaryButton) ? 1.0f : 0.0f;

            rightIndex = Mathf.Clamp01(ControllerInputPoller.instance.rightControllerIndexFloat);
            rightMiddle = Mathf.Clamp01(ControllerInputPoller.instance.rightControllerGripFloat);
            rightThumb = (ControllerInputPoller.instance.rightControllerSecondaryButton || ControllerInputPoller.instance.rightControllerPrimaryButton) ? 1.0f : 0.0f;
        }
        else if (CurrentRig != null)
        {
            if (CurrentRig.leftIndex != null) leftIndex = Mathf.Clamp01(CurrentRig.leftIndex.triggerValue);
            if (CurrentRig.leftMiddle != null) leftMiddle = Mathf.Clamp01(CurrentRig.leftMiddle.gripValue);
            if (CurrentRig.leftThumb != null) leftThumb = (CurrentRig.leftThumb.secondaryButtonPress || CurrentRig.leftThumb.primaryButtonPress) ? 1.0f : 0.0f;

            if (CurrentRig.rightIndex != null) rightIndex = Mathf.Clamp01(CurrentRig.rightIndex.triggerValue);
            if (CurrentRig.rightMiddle != null) rightMiddle = Mathf.Clamp01(CurrentRig.rightMiddle.gripValue);
            if (CurrentRig.rightThumb != null) rightThumb = (CurrentRig.rightThumb.secondaryButtonPress || CurrentRig.rightThumb.primaryButtonPress) ? 1.0f : 0.0f;
        }

        MocapFrame frame = new MocapFrame
        {
            Timestamp = timestamp,
            Head = new MocapTransform(headPos, headRot),
            Body = new MocapTransform(bodyPos, bodyRot),
            LeftHand = new MocapTransform(leftHandPos, leftHandRot),
            RightHand = new MocapTransform(rightHandPos, rightHandRot),
            LeftIndex = leftIndex,
            LeftMiddle = leftMiddle,
            LeftThumb = leftThumb,
            RightIndex = rightIndex,
            RightMiddle = rightMiddle,
            RightThumb = rightThumb
        };

        CurrentRecording.Frames.Add(frame);
    }

    public async Task<bool> SaveRecordingAsync(string customFileName = null)
    {
        if (State != MocapRecordingState.ReadyToSave || CurrentRecording == null)
        {
            Console.WriteLine("[MonkeFrames::Mocap] Cannot save: no recording ready to save.");
            return false;
        }

        EnsureDirectories();

        string folder = RecordingsFolder;
        string timestampStr = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string baseName = string.IsNullOrWhiteSpace(customFileName) ? $"Mocap_{timestampStr}" : customFileName.Trim();
        string filename = baseName + MocapRecording.FileExtension;
        string targetPath = Path.Combine(folder, filename);

        int counter = 1;
        while (File.Exists(targetPath))
        {
            targetPath = Path.Combine(folder, $"{baseName}_{counter}{MocapRecording.FileExtension}");
            counter++;
        }

        State = MocapRecordingState.Saving;
        StatusMessage = "Saving .mfmc mocap file...";
        UIManager.Instance.Status = StatusMessage;

        MocapRecording recordingToSave = CurrentRecording;
        recordingToSave.FrameCount = recordingToSave.Frames.Count;
        recordingToSave.Duration = _recordedDuration;

        bool success = false;
        string errorMsg = null;

        await Task.Run(() =>
        {
            try
            {
                recordingToSave.SaveToFile(targetPath);
                success = true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                success = false;
            }
        });

        if (success)
        {
            LastSavedFilePath = targetPath;
            State = MocapRecordingState.Idle;
            StatusMessage = $"Saved: {Path.GetFileName(targetPath)} ({recordingToSave.FrameCount} frames)";
            UIManager.Instance.Status = $"Mocap: Successfully saved {Path.GetFileName(targetPath)}";
            Console.WriteLine($"[MonkeFrames::Mocap] Successfully saved recording to: {targetPath}");
            CurrentRecording = null;
            return true;
        }
        else
        {
            State = MocapRecordingState.ReadyToSave;
            StatusMessage = $"Save failed: {errorMsg}";
            UIManager.Instance.Status = $"Mocap save error: {errorMsg}";
            Console.WriteLine($"[MonkeFrames::Mocap] Error saving mocap file: {errorMsg}");
            return false;
        }
    }

    public List<FileInfo> GetSavedRecordings()
    {
        EnsureDirectories();
        try
        {
            DirectoryInfo dir = new DirectoryInfo(RecordingsFolder);
            if (!dir.Exists) return new List<FileInfo>();

            return dir.GetFiles("*" + MocapRecording.FileExtension, SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonkeFrames::Mocap] Error listing recordings: {ex.Message}");
            return new List<FileInfo>();
        }
    }
}
