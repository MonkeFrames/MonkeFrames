using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.Replays;
using MonkeFrames.Editor.UI;
using UnityEngine;

namespace MonkeFrames.Editor.Windows;

public class ReplaysWindow : IEditorWindow
{
    public string Name => "Replays";
    public Rect Rect => new Rect(Screen.width - 580, 44, 540, 800);

    private static readonly int[] Rates = [30, 60, 90];
    private static readonly string[] RateLabels = ["30 fps", "60 fps", "90 fps"];
    private static readonly float[] Lengths = [5f, 10f, 20f, 30f];
    private static readonly string[] LengthLabels = ["5 min", "10 min", "20 min", "30 min"];
    private static readonly float[] Speeds = [0.25f, 0.5f, 1f, 2f];
    private static readonly string[] SpeedLabels = ["0.25x", "0.5x", "1x", "2x"];

    private Vector2 _scroll, _libScroll;
    private float _contentH = 900f;
    private float _innerW = 500f;
    private int _dragMode = -1;        // 0 = playhead, 1 = in, 2 = out
    private string _confirmDelete;
    private float _confirmUntil;

    public void OnOpen()
    {
        ReplayManager.Instance?.RefreshLibrary();
    }

    public void OnDraw()
    {
        ReplayManager rm = ReplayManager.Instance;
        if (rm == null)
        {
            GUI.Label(new Rect(16, 40, 400, 24), "Replays are still loading...", Theme.Muted);
            return;
        }

        Rect viewRect = new Rect(0, 34, Rect.width - 2, Rect.height - 38);
        bool needScroll = _contentH > viewRect.height;
        float contentW = viewRect.width - (needScroll ? 14f : 0f);
        _innerW = contentW - 32;

        _scroll = GUI.BeginScrollView(viewRect, _scroll, new Rect(0, 0, contentW, _contentH));
        float end = DrawBody(rm, 16, _innerW, 6);
        GUI.enabled = true;
        GUI.EndScrollView();

        if (Event.current.type == EventType.Repaint)
            _contentH = end + 12;
    }

    private float DrawBody(ReplayManager rm, float x, float w, float y)
    {
        // ================= RECORD =================
        GUI.Label(new Rect(x, y, 200, 20), "RECORD", Theme.Header);
        y += 26;

        if (rm.Recording)
        {
            float pulse = 0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * 5f);
            Theme.Fill(new Rect(x, y, w, 64), Theme.Danger.WithAlpha(0.12f), 8);
            Theme.Dot(new Vector2(x + 22, y + 22), 7f, Theme.Danger.WithAlpha(0.4f + 0.6f * pulse));
            Theme.DrawText(new Rect(x + 38, y + 8, 200, 28), "REC  " + ReplayManager.FormatTime(rm.RecordedSeconds), Theme.Big, Theme.Text);
            string info = $"{rm.RecordingPlayers} gorilla{(rm.RecordingPlayers == 1 ? "" : "s")} · {ReplayManager.FormatBytes(rm.Clip?.ApproxBytes ?? 0)}";
            if (rm.RecordMyMic)
                info += rm.MicFound ? " · your mic: on" : " · looking for your mic...";
            GUI.Label(new Rect(x + 38, y + 36, w - 50, 22), info, Theme.Muted);
            GUI.Label(new Rect(x, y + 8, w - 12, 20), $"stops at {rm.MaxMinutes:0} min", Theme.MutedRight);
            y += 72;

            if (GUI.Button(new Rect(x, y, w, 34), "Stop recording  (F9)", Theme.DangerButton))
                rm.StopRecording();
            y += 44;

            Hint(ref y, "Keep playing! Everyone in the lobby is being recorded. Press Stop when you're done and the replay opens straight away.");
            return y;
        }

        GUI.enabled = !rm.Busy;
        if (GUI.Button(new Rect(x, y, w, 34), "Start recording  (F9)", Theme.AccentButton))
            rm.StartRecording();
        Theme.Dot(new Vector2(x + 18, y + 17), 5f, Theme.Danger);
        GUI.enabled = true;
        y += 42;

        GUI.Label(new Rect(x, y, 100, 26), "Quality");
        int rate = Widgets.Segmented("rp.rate", new Rect(x + 100, y, w - 100, 26), System.Array.IndexOf(Rates, rm.RecordRate), RateLabels);
        if (rate >= 0) rm.RecordRate = Rates[rate];
        y += 32;

        GUI.Label(new Rect(x, y, 100, 26), "Max length");
        int len = Widgets.Segmented("rp.len", new Rect(x + 100, y, w - 100, 26), System.Array.IndexOf(Lengths, rm.MaxMinutes), LengthLabels);
        if (len >= 0) rm.MaxMinutes = Lengths[len];
        y += 34;

        rm.RecordVoices = Widgets.Switch("rp.voices", new Rect(x, y, w / 2f, 26), rm.RecordVoices, "Record voices",
            "Records what everyone says in voice chat.");
        rm.RecordMyMic = Widgets.Switch("rp.mymic", new Rect(x + w / 2f, y, w / 2f, 26), rm.RecordMyMic, "Record my mic",
            "Also records your own microphone (whenever you talk).");
        y += 34;

        // ================= CURRENT REPLAY =================
        ReplayClip clip = rm.Clip;
        if (clip != null)
        {
            Widgets.Divider(x, y, w);
            y += 10;
            GUI.Label(new Rect(x, y, 200, 20), "REPLAY", Theme.Header);
            GUI.Label(new Rect(x, y, w, 20),
                $"{ReplayManager.FormatTime(clip.Length)} · {clip.Tracks.Count} gorilla{(clip.Tracks.Count == 1 ? "" : "s")} · {clip.Rate} fps", Theme.MutedRight);
            y += 26;

            // Name + save
            string newName = GUI.TextField(new Rect(x, y, w - 170, 28), clip.Name ?? "");
            if (newName != clip.Name) { clip.Name = newName; clip.Dirty = true; }
            GUI.enabled = !rm.Busy;
            if (GUI.Button(new Rect(x + w - 162, y, 80, 28), clip.Dirty || clip.FilePath == null ? "Save*" : "Save", clip.Dirty ? Theme.AccentButton : GUI.skin.button))
                rm.Save();
            GUI.enabled = true;
            if (GUI.Button(new Rect(x + w - 76, y, 76, 28), "Close"))
                rm.Unload();
            y += 36;

            if (rm.Clip == null)
                return DrawLibrary(rm, x, w, y);

            // Timeline
            y = DrawTimeline(rm, clip, x, w, y);

            // Transport
            float bw = 44;
            if (GUI.Button(new Rect(x, y, bw, 30), "|<"))
                rm.Seek(clip.In);
            if (GUI.Button(new Rect(x + bw + 6, y, 96, 30), rm.Playing ? "Pause" : "Play", Theme.AccentButton))
                rm.TogglePlay();
            if (GUI.Button(new Rect(x + bw + 108, y, bw, 30), ">|"))
                rm.Seek(clip.Out);

            float sx = x + bw * 2 + 118;
            if (GUI.Button(new Rect(sx, y, 70, 30), "Set In"))
            {
                clip.InPoint = Mathf.Min((float)rm.Time, clip.Out - 0.1f);
                clip.Dirty = true;
            }
            if (GUI.Button(new Rect(sx + 76, y, 70, 30), "Set Out"))
            {
                clip.OutPoint = Mathf.Max((float)rm.Time, clip.In + 0.1f);
                clip.Dirty = true;
            }
            if (GUI.Button(new Rect(sx + 152, y, w - (sx - x) - 152, 30), "Reset trim"))
            {
                clip.InPoint = 0f;
                clip.OutPoint = 0f;
                clip.Dirty = true;
            }
            y += 38;

            GUI.Label(new Rect(x, y, 100, 26), "Speed");
            int sp = Widgets.Segmented("rp.speed", new Rect(x + 100, y, w - 100 - 110, 26), System.Array.IndexOf(Speeds, rm.Speed), SpeedLabels);
            if (sp >= 0) rm.Speed = Speeds[sp];
            rm.Loop = Widgets.Switch("rp.loop", new Rect(x + w - 96, y, 96, 26), rm.Loop, "Loop");
            y += 34;

            bool viewing = Widgets.Switch("rp.view", new Rect(x, y, w / 2f, 26), rm.Viewing, "Show replay gorillas",
                "Show the recorded gorillas in the world. Turn off to go back to the live game.");
            ReplayStudio studio = ReplayStudio.Instance;
            if (studio != null)
                studio.Enabled = Widgets.Switch("rp.studio", new Rect(x + w / 2f, y, w / 2f, 26), studio.Enabled, "Replay Studio layout",
                    "Edit keyframes on a timeline in replay time, with a camera preview (only while a replay is open).");
            if (viewing != rm.Viewing)
            {
                if (viewing) rm.StartViewing();
                else rm.StopViewing();
            }
            y += 34;

            // ================= GORILLAS =================
            Widgets.Divider(x, y, w);
            y += 10;
            GUI.Label(new Rect(x, y, 200, 20), "GORILLAS", Theme.Header);
            GUI.Label(new Rect(x, y, w, 20), "Film = point Other Cameras at them", Theme.MutedRight);
            y += 26;
            y = DrawTracks(rm, clip, x, w, y);

            // ================= PLAYBACK OPTIONS =================
            Widgets.Divider(x, y, w);
            y += 10;
            GUI.Label(new Rect(x, y, 200, 20), "PLAYBACK", Theme.Header);
            y += 26;

            rm.HideLive = Widgets.Switch("rp.hidelive", new Rect(x, y, w / 2f, 26), rm.HideLive, "Hide live gorillas",
                "Hide the real players (and you) while the replay is showing.");
            rm.MuteGame = Widgets.Switch("rp.mutegame", new Rect(x + w / 2f, y, w / 2f, 26), rm.MuteGame, "Mute live game",
                "Silence the game so you only hear the replay.");
            y += 32;
            rm.Voice3D = Widgets.Switch("rp.3d", new Rect(x, y, w / 2f, 26), rm.Voice3D, "3D voices",
                "Voices get quieter and pan left/right depending on where the camera is.");
            rm.SyncWithKeyframes = Widgets.Switch("rp.sync", new Rect(x + w / 2f, y, w / 2f, 26), rm.SyncWithKeyframes, "Sync with keyframes",
                "Project > Play, the Player and MP4 export play the replay in time with your keyframes.");
            y += 32;
            if (Classes.Settings.current != null)
            {
                Classes.Settings.current.ShowCamerasInReplays = Widgets.Switch("rp.cams", new Rect(x, y, w, 26),
                    Classes.Settings.current.ShowCamerasInReplays, "Show spectator cameras",
                    "Show the MonkeFrames camera models (yours and other mod users') recorded in this replay.");
                y += 32;
            }

            GUI.Label(new Rect(x, y, 100, 24), "Voice volume");
            rm.VoiceVolume = Widgets.Slider("rp.vol", new Rect(x + 100, y, w - 100 - 60, 24), rm.VoiceVolume, 0f, 2f);
            GUI.Label(new Rect(x + w - 56, y, 56, 24), $"{rm.VoiceVolume * 100f:0}%", Theme.LabelRight);
            y += 30;

            if (rm.SyncWithKeyframes)
                Hint(ref y, rm.SyncDriving
                    ? "Following your keyframes right now."
                    : "Keyframe time 0 = the replay's In point. Scrub the Player or press Project > Play to film the replay with your keyframes.");
        }

        return DrawLibrary(rm, x, w, y);
    }

    private float DrawTimeline(ReplayManager rm, ReplayClip clip, float x, float w, float y)
    {
        float len = Mathf.Max(0.01f, clip.Length);
        Rect bar = new Rect(x + 8, y + 4, w - 16, 22);

        float ToX(double t) => bar.x + bar.width * Mathf.Clamp01((float)(t / len));
        double ToTime(float px) => Mathf.Clamp01((px - bar.x) / bar.width) * len;

        // Background, trimmed region, played part
        Theme.Fill(new Rect(bar.x - 8, bar.y, bar.width + 16, bar.height), Theme.Field, 6);
        float ix = ToX(clip.In), ox = ToX(clip.Out), px = ToX(rm.Time);
        Theme.Fill(new Rect(ix, bar.y + 3, Mathf.Max(0, ox - ix), bar.height - 6), Theme.Raised, 4);
        Theme.Fill(new Rect(ix, bar.y + 3, Mathf.Clamp(px - ix, 0, ox - ix), bar.height - 6), Theme.Accent.WithAlpha(0.35f), 4);

        // Voice activity marks
        if (Event.current.type == EventType.Repaint)
        foreach (ReplayTrack t in clip.Tracks)
        {
            if (t.Hidden || t.VoiceMuted) continue;
            float lastX = -10;
            foreach (AudioBlock b in t.Audio)
            {
                float bx = ToX(b.Time);
                if (bx - lastX < 2f) continue;
                lastX = bx;
                Theme.Fill(new Rect(bx, bar.yMax - 6, 2, 3), Theme.Text.WithAlpha(0.35f), 0);
            }
        }

        // In / out handles
        Theme.Fill(new Rect(ix - 2, bar.y - 2, 4, bar.height + 4), Theme.Text, 2);
        Theme.Fill(new Rect(ox - 2, bar.y - 2, 4, bar.height + 4), Theme.Text, 2);

        // Playhead
        Theme.Fill(new Rect(px - 1, bar.y - 5, 2, bar.height + 10), Theme.Accent, 1);
        Theme.Dot(new Vector2(px, bar.y - 5), 4f, Theme.Accent);

        // Mouse: drag playhead, or the in/out handles
        Event e = Event.current;
        int id = GUIUtility.GetControlID(FocusType.Passive);
        Rect hit = new Rect(bar.x - 8, bar.y - 8, bar.width + 16, bar.height + 16);
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown when e.button == 0 && hit.Contains(e.mousePosition):
                GUIUtility.hotControl = id;
                if (Mathf.Abs(e.mousePosition.x - ix) < 7) _dragMode = 1;
                else if (Mathf.Abs(e.mousePosition.x - ox) < 7) _dragMode = 2;
                else _dragMode = 0;
                Drag(rm, clip, ToTime(e.mousePosition.x));
                e.Use();
                break;
            case EventType.MouseDrag when GUIUtility.hotControl == id:
                Drag(rm, clip, ToTime(e.mousePosition.x));
                e.Use();
                break;
            case EventType.MouseUp when GUIUtility.hotControl == id:
                GUIUtility.hotControl = 0;
                _dragMode = -1;
                e.Use();
                break;
        }

        y += 32;

        // Gorilla lanes: when each gorilla is in the replay
        int lanes = Mathf.Min(clip.Tracks.Count, 12);
        for (int i = 0; i < lanes; i++)
        {
            ReplayTrack t = clip.Tracks[i];
            float a = ToX(t.StartFrame / (float)clip.Rate), b = ToX(t.EndFrame / (float)clip.Rate);
            Color c = t.Color; c.a = t.Hidden ? 0.2f : 0.85f;
            Theme.Fill(new Rect(a, y + i * 4, Mathf.Max(2, b - a), 3), c, 1);
        }
        y += lanes * 4 + 6;

        GUI.Label(new Rect(x, y, w / 2f, 20), $"{ReplayManager.FormatTime(rm.Time)} / {ReplayManager.FormatTime(clip.Length)}", Theme.Label);
        GUI.Label(new Rect(x, y, w, 20), $"trim {ReplayManager.FormatTime(clip.In)} – {ReplayManager.FormatTime(clip.Out)}", Theme.MutedRight);
        return y + 28;
    }

    private void Drag(ReplayManager rm, ReplayClip clip, double t)
    {
        switch (_dragMode)
        {
            case 1:
                clip.InPoint = Mathf.Clamp((float)t, 0f, clip.Out - 0.1f);
                clip.Dirty = true;
                rm.Seek(clip.InPoint);
                break;
            case 2:
                clip.OutPoint = Mathf.Clamp((float)t, clip.In + 0.1f, clip.Length);
                clip.Dirty = true;
                rm.Seek(clip.OutPoint);
                break;
            default:
                if (!rm.Viewing) rm.StartViewing();
                rm.Seek(t);
                break;
        }
    }

    private float DrawTracks(ReplayManager rm, ReplayClip clip, float x, float w, float y)
    {
        CameraModes cm = CameraModes.Instance;
        const float rowH = 32f;

        foreach (ReplayTrack t in clip.Tracks)
        {
            Rect row = new Rect(x, y, w, rowH);
            bool filming = cm != null && cm.Target == t.Puppet && t.Puppet != null && cm.Mode != CameraMode.Free;
            Theme.Fill(row, filming ? Theme.Accent.WithAlpha(0.18f) : Theme.Surface, 6);
            if (filming)
                Theme.Fill(new Rect(row.x, row.y + 8, 3, row.height - 16), Theme.Accent, 1);

            Theme.Dot(new Vector2(row.x + 16, row.center.y), 6f, t.Hidden ? t.Color.WithAlpha(0.3f) : t.Color.WithAlpha(1f));
            string name = t.Name + (t.IsLocal ? "  (you)" : "");
            Theme.DrawText(new Rect(row.x + 30, row.y, w - 290, rowH), name, Theme.Label, t.Hidden ? Theme.TextMuted : Theme.Text);

            string when = $"{ReplayManager.FormatTime(t.StartFrame / (float)clip.Rate)}–{ReplayManager.FormatTime(t.EndFrame / (float)clip.Rate)}";
            Theme.DrawText(new Rect(row.xMax - 290, row.y, 80, rowH), when, Theme.MutedRight, Theme.TextMuted);

            float bx = row.xMax - 202;
            if (t.HasVoice)
            {
                if (Widgets.Ghost("rp.mute." + t.Key + t.StartFrame, new Rect(bx, row.y + 4, 62, rowH - 8), t.VoiceMuted ? "Muted" : "Voice", Theme.LabelCenterSmall, !t.VoiceMuted))
                {
                    t.VoiceMuted = !t.VoiceMuted;
                    clip.Dirty = true;
                }
            }
            if (Widgets.Ghost("rp.hide." + t.Key + t.StartFrame, new Rect(bx + 66, row.y + 4, 58, rowH - 8), t.Hidden ? "Show" : "Hide", Theme.LabelCenterSmall))
            {
                t.Hidden = !t.Hidden;
                clip.Dirty = true;
            }

            GUI.enabled = !t.Hidden && t.Puppet != null && cm != null;
            if (GUI.Button(new Rect(row.xMax - 74, row.y + 3, 70, rowH - 6), "Film", filming ? Theme.AccentButton : GUI.skin.button))
                Film(rm, cm, t);
            GUI.enabled = true;

            y += rowH + 4;
        }

        return y + 6;
    }

    private static void Film(ReplayManager rm, CameraModes cm, ReplayTrack t)
    {
        if (!rm.Viewing) rm.StartViewing();

        // Jump to when they're in the replay, if they aren't right now.
        double start = t.StartFrame / (double)rm.Clip.Rate, end = t.EndFrame / (double)rm.Clip.Rate;
        if (rm.Time < start || rm.Time > end)
            rm.Seek(start);
        rm.ApplyPose();

        cm.SetTarget(t.Puppet);
        if (cm.Mode == CameraMode.Free)
            cm.SetMode(CameraMode.Orbit);
        UIManager.Instance.OpenWindow("Other Cameras");
    }

    private float DrawLibrary(ReplayManager rm, float x, float w, float y)
    {
        Widgets.Divider(x, y, w);
        y += 10;
        GUI.Label(new Rect(x, y, 200, 20), "SAVED REPLAYS", Theme.Header);
        if (GUI.Button(new Rect(x + w - 190, y - 3, 96, 24), "Open folder"))
            rm.OpenFolder();
        if (GUI.Button(new Rect(x + w - 88, y - 3, 88, 24), "Refresh"))
            rm.RefreshLibrary();
        y += 28;

        if (rm.Busy)
        {
            Widgets.Spinner(new Vector2(x + 14, y + 12), 6f, Theme.Accent);
            GUI.Label(new Rect(x + 30, y, w - 30, 24), rm.BusyText, Theme.Muted);
            y += 30;
        }

        if (rm.Library.Count == 0)
        {
            Theme.Fill(new Rect(x, y, w, 50), Theme.Surface, 8);
            GUI.Label(new Rect(x + 12, y + 6, w - 24, 40), "No saved replays yet. Recordings save automatically when you press Stop.", Theme.MutedWrap);
            return y + 60;
        }

        const float rowH = 44f;
        float viewH = Mathf.Min(rm.Library.Count * (rowH + 4) + 4, 5 * (rowH + 4) + 4);
        Rect view = new Rect(x, y, w, viewH);
        Theme.Fill(view, Theme.Field, 8);
        Rect content = new Rect(0, 0, w - 16, rm.Library.Count * (rowH + 4) + 4);
        _libScroll = GUI.BeginScrollView(new Rect(view.x + 2, view.y + 1, view.width - 3, view.height - 2), _libScroll, content);

        if (Time.unscaledTime > _confirmUntil) _confirmDelete = null;

        for (int i = 0; i < rm.Library.Count; i++)
        {
            ReplayClip c = rm.Library[i];
            Rect row = new Rect(2, 2 + i * (rowH + 4), content.width - 4, rowH);
            bool current = rm.Clip != null && rm.Clip.FilePath != null && rm.Clip.FilePath == c.FilePath;
            Theme.Fill(row, current ? Theme.Accent.WithAlpha(0.16f) : Theme.Surface, 6);

            Theme.DrawText(new Rect(row.x + 12, row.y + 3, row.width - 180, 22), c.Name, Theme.Label, Theme.Text);
            string sub = $"{ReplayManager.FormatTime(c.Length)} · {c.TrackCount} gorilla{(c.TrackCount == 1 ? "" : "s")} · {c.Created:d MMM HH:mm}";
            Theme.DrawText(new Rect(row.x + 12, row.y + 22, row.width - 180, 18), sub, Theme.MutedSmall, Theme.TextMuted);

            GUI.enabled = !rm.Busy && !rm.Recording;
            if (GUI.Button(new Rect(row.xMax - 164, row.y + 8, 76, rowH - 16), current ? "Reload" : "Load", Theme.AccentButton))
                rm.Load(c.FilePath);
            bool confirming = _confirmDelete == c.FilePath;
            if (GUI.Button(new Rect(row.xMax - 82, row.y + 8, 76, rowH - 16), confirming ? "Sure?" : "Delete", confirming ? Theme.DangerButton : GUI.skin.button))
            {
                if (confirming)
                {
                    _confirmDelete = null;
                    rm.Delete(c);
                    GUI.enabled = true;
                    break;
                }
                _confirmDelete = c.FilePath;
                _confirmUntil = Time.unscaledTime + 3f;
            }
            GUI.enabled = true;
        }

        GUI.EndScrollView();
        return y + viewH + 8;
    }

    private void Hint(ref float y, string text)
    {
        GUI.Label(new Rect(16, y, _innerW, 36), text, Theme.MutedWrap);
        y += 40;
    }
}
