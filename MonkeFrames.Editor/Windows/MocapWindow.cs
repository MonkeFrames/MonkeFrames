using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.Mocap;
using MonkeFrames.Editor.UI;
using UnityEngine;

namespace MonkeFrames.Editor.Windows;

public class MocapWindow : IEditorWindow
{
    public string Name => "Mocap";
    public Rect Rect => new Rect(100, 80, 480, 620);

    private Vector2 _recordingsScroll;
    private readonly string[] _fpsOptions = new[] { "24 FPS", "30 FPS", "60 FPS" };
    private readonly float[] _fpsValues = new[] { 24f, 30f, 60f };
    private int _selectedFpsIndex = 0;

    public void OnDraw()
    {
        float w = Rect.width;
        MocapManager mm = MocapManager.Instance;

        if (mm == null)
        {
            GUI.Label(new Rect(16, 40, 400, 24), "Mocap manager is initializing...", Theme.Muted);
            return;
        }

        float y = 38f;

        GUI.Label(new Rect(16, y, 220, 24), "MOCAP RECORDER", Theme.Header);

        MocapRecordingState state = mm.State;
        Color badgeColor;
        string badgeText;

        switch (state)
        {
            case MocapRecordingState.Recording:
                float pulse = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 5f);
                badgeColor = Color.Lerp(Theme.Danger, Color.red, pulse);
                badgeText = "RECORDING";
                break;
            case MocapRecordingState.ReadyToSave:
                badgeColor = Theme.Accent;
                badgeText = "READY TO SAVE";
                break;
            case MocapRecordingState.Saving:
                badgeColor = Color.yellow;
                badgeText = "SAVING...";
                break;
            default:
                badgeColor = Theme.TextMuted;
                badgeText = "NOT RECORDING";
                break;
        }

        Rect badgeRect = new Rect(w - 140, y - 2, 124, 24);
        Theme.Fill(badgeRect, badgeColor.WithAlpha(0.2f), 6);
        Theme.DrawText(badgeRect, badgeText, Theme.LabelCenter, badgeColor);
        y += 32f;

        GUI.Label(new Rect(16, y, w - 32, 20), $"Status: {mm.StatusMessage}", Theme.MutedSmall);
        y += 24f;

        float btnW = (w - 32f - 8f) / 2f;
        Rect startBtnRect = new Rect(16, y, btnW, 30);
        Rect stopBtnRect = new Rect(16 + btnW + 8f, y, btnW, 30);

        if (state == MocapRecordingState.Recording)
        {
            GUI.enabled = false;
            GUI.Button(startBtnRect, "Recording...", Theme.AccentButton);
            GUI.enabled = true;

            if (GUI.Button(stopBtnRect, "Stop Recording", Theme.DangerButton))
            {
                mm.StopRecording();
            }
        }
        else
        {
            GUI.enabled = state != MocapRecordingState.Saving;
            if (GUI.Button(startBtnRect, "Start Recording", Theme.AccentButton))
            {
                mm.StartRecording();
            }

            GUI.enabled = false;
            GUI.Button(stopBtnRect, "Stop Recording");
            GUI.enabled = true;
        }
        y += 36f;

        if (state == MocapRecordingState.ReadyToSave)
        {
            Rect saveBtnRect = new Rect(16, y, btnW, 30);
            Rect discardBtnRect = new Rect(16 + btnW + 8f, y, btnW, 30);

            if (GUI.Button(saveBtnRect, "Save Recording (.mfmc)", Theme.AccentButton))
            {
                _ = mm.SaveRecordingAsync();
            }

            if (GUI.Button(discardBtnRect, "Discard", Theme.DangerButton))
            {
                mm.DiscardRecording();
            }
            y += 36f;
        }

        Widgets.Divider(12, y + 2, w - 24);
        y += 14f;

        GUI.Label(new Rect(16, y, 160, 24), "Sample Rate:", Theme.Label);

        Rect segRect = new Rect(140, y, w - 156, 26);
        GUI.enabled = state != MocapRecordingState.Recording && state != MocapRecordingState.Saving;
        int newFpsIndex = Widgets.Segmented("mocap.fps", segRect, _selectedFpsIndex, _fpsOptions);
        if (newFpsIndex != _selectedFpsIndex && newFpsIndex >= 0 && newFpsIndex < _fpsValues.Length)
        {
            _selectedFpsIndex = newFpsIndex;
            mm.SampleRate = _fpsValues[newFpsIndex];
        }
        GUI.enabled = true;
        y += 34f;

        Rect metricsBox = new Rect(12, y, w - 24, 110);
        Theme.Fill(metricsBox, Theme.Surface, 8);

        float mx = metricsBox.x + 12;
        float my = metricsBox.y + 10;

        float duration = mm.ElapsedRecordingTime;
        int frames = mm.RecordedFrameCount;
        int minutes = Mathf.FloorToInt(duration / 60f);
        float seconds = duration % 60f;
        string timeStr = $"{minutes:00}:{seconds:00.00}";

        GUI.Label(new Rect(mx, my, 100, 20), "Duration:", Theme.MutedSmall);
        Theme.DrawText(new Rect(mx + 80, my, 120, 20), timeStr, Theme.Label, Theme.Text);

        GUI.Label(new Rect(mx + 210, my, 80, 20), "Frames:", Theme.MutedSmall);
        Theme.DrawText(new Rect(mx + 270, my, 100, 20), frames.ToString(), Theme.Label, Theme.Accent);
        my += 24f;

        string targetName = mm.CurrentRig != null ? CameraModes.PlayerName(mm.CurrentRig) : "Local Gorilla";
        GUI.Label(new Rect(mx, my, 100, 20), "Target:", Theme.MutedSmall);
        Theme.DrawText(new Rect(mx + 80, my, 250, 20), targetName, Theme.Label, Theme.Text);
        my += 24f;

        bool headOk = mm.HeadTarget != null;
        bool bodyOk = mm.BodyTarget != null;
        bool leftHandOk = mm.LeftHandTarget != null;
        bool rightHandOk = mm.RightHandTarget != null;

        GUI.Label(new Rect(mx, my, 100, 20), "Tracking:", Theme.MutedSmall);
        float tx = mx + 80;

        DrawStatusChip(ref tx, my, "Head", headOk);
        DrawStatusChip(ref tx, my, "Body", bodyOk);
        DrawStatusChip(ref tx, my, "L.Hand", leftHandOk);
        DrawStatusChip(ref tx, my, "R.Hand", rightHandOk);
        DrawStatusChip(ref tx, my, "Fingers", true);

        y += metricsBox.height + 14f;
        Widgets.Divider(12, y, w - 24);
        y += 10f;

        GUI.Label(new Rect(16, y, 220, 22), "SAVED RECORDINGS (.mfmc)", Theme.Header);

        if (GUI.Button(new Rect(w - 110, y - 2, 94, 24), "Open Folder"))
        {
            try
            {
                mm.EnsureDirectories();
                Process.Start(new ProcessStartInfo
                {
                    FileName = MocapManager.RecordingsFolder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MonkeFrames::Mocap] Could not open recordings folder: {ex.Message}");
            }
        }
        y += 26f;

        List<FileInfo> files = mm.GetSavedRecordings();
        Rect listRect = new Rect(12, y, w - 24, Rect.height - y - 16);
        Theme.Fill(listRect, Theme.Field, 8);

        float contentH = Mathf.Max(files.Count * 36f + 8f, listRect.height - 2f);
        Rect contentRect = new Rect(0, 0, listRect.width - 16f, contentH);

        _recordingsScroll = GUI.BeginScrollView(new Rect(listRect.x + 2, listRect.y + 2, listRect.width - 4, listRect.height - 4),
            _recordingsScroll, contentRect);

        if (files.Count == 0)
        {
            GUI.Label(new Rect(0, listRect.height / 2f - 14, contentRect.width, 20), "No recordings saved yet.", Theme.MutedCenter);
            GUI.Label(new Rect(0, listRect.height / 2f + 6, contentRect.width, 20), "Recorded .mfmc files will appear here.", Theme.MutedSmall);
        }
        else
        {
            for (int i = 0; i < files.Count; i++)
            {
                FileInfo fi = files[i];
                Rect row = new Rect(4, 4 + i * 36f, contentRect.width - 8, 32f);

                bool hover = row.Contains(Event.current.mousePosition);
                float h = Anim.To($"mocap.file.{i}", hover ? 1f : 0f, 18f);

                if (h > 0.01f)
                    Theme.Fill(row, Theme.Accent.WithAlpha(0.15f * h), 6);

                string nameWithoutExt = Path.GetFileNameWithoutExtension(fi.Name);
                float kb = fi.Length / 1024f;
                string sizeStr = kb >= 1024f ? $"{kb / 1024f:0.1} MB" : $"{kb:0} KB";
                string dateStr = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");

                Theme.DrawText(new Rect(row.x + 8, row.y + 2, row.width - 160, 18), nameWithoutExt, Theme.Label, Theme.Text);
                Theme.DrawText(new Rect(row.x + 8, row.y + 18, 160, 14), dateStr, Theme.MutedSmall, Theme.TextMuted);
                Theme.DrawText(new Rect(row.xMax - 140, row.y + 7, 60, 18), sizeStr, Theme.MutedSmall, Theme.TextMuted);

                if (GUI.Button(new Rect(row.xMax - 70, row.y + 4, 64, 24), "Locate"))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{fi.FullName}\"",
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
            }
        }

        GUI.EndScrollView();
    }

    private static void DrawStatusChip(ref float x, float y, string label, bool ok)
    {
        float chipW = 56f;
        Rect chipRect = new Rect(x, y, chipW, 18);
        Color col = ok ? new Color(0.2f, 0.8f, 0.3f) : Theme.Danger;

        Theme.Fill(chipRect, col.WithAlpha(0.18f), 4);
        Theme.DrawText(chipRect, label, Theme.LabelCenter, col);
        x += chipW + 4f;
    }
}
