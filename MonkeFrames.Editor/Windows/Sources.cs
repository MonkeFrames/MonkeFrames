using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;
using UnityEngine;

namespace MonkeFrames.Editor.Windows;

public class Sources : IEditorWindow
{
    public string Name => "Sources";
    public Rect Rect => new Rect(40, 44, 520, 780);

    private static readonly CameraMode[] Modes =
    [
        CameraMode.Free, CameraMode.Orbit, CameraMode.FirstPerson, CameraMode.Follow,
        CameraMode.Shoulder, CameraMode.Front, CameraMode.TopDown, CameraMode.SideView,
        CameraMode.HandCam, CameraMode.Tracking, CameraMode.Director,
    ];
    private static readonly string[] ModeLabels = System.Array.ConvertAll(Modes, CameraModes.ModeName);

    private Vector2 _playerScroll;
    private Vector2 _bodyScroll;
    private float _contentH = 800f;
    private float _innerW = 480f;
    private CameraMode _lastMode = (CameraMode)(-1);

    public void OnDraw()
    {
        CameraModes cm = CameraModes.Instance;
        if (cm == null)
        {
            GUI.Label(new Rect(16, 40, 400, 24), "Camera modes are still loading...", Theme.Muted);
            return;
        }

        // Whole window body scrolls, so every setting fits on smaller screens.
        Rect viewRect = new Rect(0, 34, Rect.width - 2, Rect.height - 38);
        bool needScroll = _contentH > viewRect.height;
        float contentW = viewRect.width - (needScroll ? 14f : 0f);
        _innerW = contentW - 32;

        _bodyScroll = GUI.BeginScrollView(viewRect, _bodyScroll, new Rect(0, 0, contentW, _contentH));
        Color prev = GUI.color;
        float end = DrawBody(cm, 16, _innerW, 6);
        GUI.color = prev;
        GUI.enabled = true;
        GUI.EndScrollView();

        if (Event.current.type == EventType.Repaint)
            _contentH = end + 12;
    }

    private float DrawBody(CameraModes cm, float x, float innerW, float y)
    {

        // ---- Mode ----
        GUI.Label(new Rect(x, y, 200, 20), "CAMERA", Theme.Header);
        y += 24;

        int cur = System.Array.IndexOf(Modes, cm.Mode);
        int pick = Widgets.ChipGrid("oc.mode", new Rect(x, y, innerW, 92), cur, ModeLabels, 4, 28f, 4f);
        if (pick != cur && pick >= 0)
            cm.SetMode(Modes[pick]);
        y += 98;

        GUI.Label(new Rect(x, y, innerW, 36), CameraModes.Describe(cm.Mode), Theme.MutedWrap);
        y += 40;

        // Settings fade/slide in when the mode changes.
        if (cm.Mode != _lastMode)
        {
            Anim.Set("oc.body", 0f);
            _lastMode = cm.Mode;
        }
        float appear = Anim.OutCubic(Anim.To("oc.body", 1f, 11f, 0f));
        Color prev = GUI.color;
        GUI.color = new Color(1, 1, 1, prev.a * appear);
        y += (1f - appear) * 10f;

        if (cm.Mode == CameraMode.Free)
        {
            Theme.Fill(new Rect(x, y, innerW, 70), Theme.Surface, 8);
            GUI.Label(new Rect(x + 12, y + 8, innerW - 24, 54),
                "Pick a camera above to follow a gorilla: yourself or anyone in your lobby. " +
                "Press V in any mode to drop a keyframe from that view.", Theme.MutedWrap);
            GUI.color = prev;
            return y + 74;
        }

        // ---- Target ----
        Widgets.Divider(x, y - 4, innerW);
        GUI.Label(new Rect(x, y + 2, 200, 20), "WATCHING", Theme.Header);
        var subjects = cm.Players;
        int replayCount = 0;
        foreach (CamSubject sub in subjects) if (sub != null && sub.IsReplay) replayCount++;
        GUI.Label(new Rect(x, y + 2, innerW, 20),
            replayCount > 0 ? $"{replayCount} in replay · {subjects.Count - replayCount} live" : $"{subjects.Count} in lobby", Theme.MutedRight);
        y += 26;
        y = DrawPlayers(cm, x, y, innerW);

        // ---- Mode settings ----
        Widgets.Divider(x, y + 4, innerW);
        y += 12;
        GUI.Label(new Rect(x, y, 200, 20), "SETTINGS", Theme.Header);
        y += 24;

        switch (cm.Mode)
        {
            case CameraMode.Orbit:
                cm.OrbitAutoSpin = Widgets.Switch("oc.spin", new Rect(x, y, 150, 26), cm.OrbitAutoSpin, "Auto spin",
                    "Keep circling on its own. Turn off to place the camera by hand.");
                GUI.enabled = cm.OrbitAutoSpin;
                cm.OrbitSpeed = Widgets.Slider("oc.ospeed", new Rect(x + 160, y, innerW - 160 - 70, 26), cm.OrbitSpeed, -90f, 90f);
                GUI.Label(new Rect(x + innerW - 64, y, 64, 26), cm.OrbitSpeed.ToString("0") + "°/s", Theme.LabelRight);
                GUI.enabled = true;
                y += 32;
                cm.OrbitDistance = SliderRow("oc.odist", ref y, "Distance", cm.OrbitDistance, 0.8f, 20f, "0.0", "m");
                cm.OrbitAngle = SliderRow("oc.oangle", ref y, "Angle", cm.OrbitAngle, -80f, 85f, "0", "°");
                cm.OrbitHeight = SliderRow("oc.oheight", ref y, "Height", cm.OrbitHeight, -3f, 6f, "0.0", "m");
                cm.OrbitRelative = Widgets.Switch("oc.orel", new Rect(x, y, innerW, 26), cm.OrbitRelative,
                    "Turn with gorilla", "Keep the same angle relative to where the gorilla faces (e.g. always in front of them).");
                y += 30;
                Hint(ref y, "Manual: left-drag, or A/D around · W/S angle · Q/E height · scroll zoom · Shift = faster");
                break;

            case CameraMode.FirstPerson:
                cm.LevelHorizon = Widgets.Switch("oc.level", new Rect(x, y, innerW, 26), cm.LevelHorizon,
                    "Level horizon", "Removes head tilt so the view stays level.");
                y += 32;
                cm.ForwardOffset = SliderRow("oc.fwd", ref y, "Eye offset", cm.ForwardOffset, 0f, 0.4f, "0.00", "m");
                Hint(ref y, "Position is locked to the head. Smoothing only affects turning.");
                break;

            case CameraMode.Follow:
                cm.FollowDistance = SliderRow("oc.fdist", ref y, "Distance", cm.FollowDistance, 0.5f, 15f, "0.0", "m");
                cm.FollowHeight = SliderRow("oc.fheight", ref y, "Height", cm.FollowHeight, -1f, 5f, "0.0", "m");
                cm.FollowSide = SliderRow("oc.fside", ref y, "Side", cm.FollowSide, -3f, 3f, "0.0", "m");
                cm.FollowTurnLag = SliderRow("oc.flag", ref y, "Turn lag", cm.FollowTurnLag, 0f, 1f, "", "", percent: true);
                break;

            case CameraMode.Shoulder:
                cm.ShoulderBack = SliderRow("oc.sback", ref y, "Distance", cm.ShoulderBack, 0.3f, 4f, "0.00", "m");
                cm.ShoulderHeight = SliderRow("oc.sheight", ref y, "Height", cm.ShoulderHeight, -0.5f, 1.5f, "0.00", "m");
                cm.ShoulderSide = SliderRow("oc.side", ref y, "Side", cm.ShoulderSide, -1.5f, 1.5f, "0.00", "m");
                if (GUI.Button(new Rect(x, y, 150, 26), "Swap shoulder"))
                    cm.SwapShoulder();
                y += 32;
                break;

            case CameraMode.Front:
                cm.FrontDistance = SliderRow("oc.frdist", ref y, "Distance", cm.FrontDistance, 0.4f, 10f, "0.0", "m");
                cm.FrontHeight = SliderRow("oc.frheight", ref y, "Height", cm.FrontHeight, -1f, 3f, "0.00", "m");
                cm.FollowTurnLag = SliderRow("oc.frlag", ref y, "Turn lag", cm.FollowTurnLag, 0f, 1f, "", "", percent: true);
                Hint(ref y, "Stays in front of the gorilla's face as they turn. Scroll to zoom.");
                break;

            case CameraMode.TopDown:
                cm.TopHeight = SliderRow("oc.theight", ref y, "Height", cm.TopHeight, 1.5f, 40f, "0.0", "m");
                cm.TopRotate = Widgets.Switch("oc.trot", new Rect(x, y, innerW, 26), cm.TopRotate,
                    "Turn with gorilla", "Rotate the view so the gorilla always faces up the screen.");
                y += 32;
                if (cm.TopRotate)
                    cm.FollowTurnLag = SliderRow("oc.tlag", ref y, "Turn lag", cm.FollowTurnLag, 0f, 1f, "", "", percent: true);
                break;

            case CameraMode.SideView:
                cm.SideAngle = SliderRow("oc.sangle", ref y, "Direction", cm.SideAngle, 0f, 360f, "0", "°");
                cm.SideDistance = SliderRow("oc.sdist", ref y, "Distance", cm.SideDistance, 1f, 20f, "0.0", "m");
                cm.SideHeight = SliderRow("oc.svh", ref y, "Height", cm.SideHeight, -2f, 5f, "0.00", "m");
                Hint(ref y, "The camera never turns with the gorilla, only slides along with them.");
                break;

            case CameraMode.HandCam:
            {
                int hand = Widgets.Segmented("oc.hand", new Rect(x, y, 200, 26), cm.HandRight ? 1 : 0, ["Left hand", "Right hand"]);
                cm.HandRight = hand == 1;
                y += 32;
                cm.HandLookAtFace = Widgets.Switch("oc.hface", new Rect(x, y, innerW, 26), cm.HandLookAtFace,
                    "Look at face", "Selfie-stick: point back at the gorilla. Off = point the way the hand points.");
                y += 32;
                break;
            }

            case CameraMode.Director:
                GUI.Label(new Rect(x, y, innerW, 22), "Now showing: " + CameraModes.ModeName(cm.DirectorShot), Theme.Label);
                y += 26;
                cm.DirectorInterval = SliderRow("oc.dint", ref y, "Shot length", cm.DirectorInterval, 2f, 20f, "0.0", "s");
                cm.DirectorBlend = Widgets.Switch("oc.dblend", new Rect(x, y, innerW, 26), cm.DirectorBlend,
                    "Glide between shots", "Smoothly fly to the next shot instead of a hard cut.");
                y += 32;
                break;

            case CameraMode.Tracking:
                cm.TrackingAutoZoom = Widgets.Switch("oc.azoom", new Rect(x, y, innerW, 26), cm.TrackingAutoZoom,
                    "Auto zoom", "Zooms in and out so the gorilla stays the same size, however far away they are.");
                y += 32;
                if (cm.TrackingAutoZoom)
                    cm.TrackingFrame = SliderRow("oc.frame", ref y, "Framing", cm.TrackingFrame, 0.3f, 6f, "0.0", "m");
                Hint(ref y, "Tip: fly the free camera to a spot first, then switch to Tracking.");
                break;
        }

        // ---- Look (all modes) ----
        Widgets.Divider(x, y + 2, innerW);
        y += 10;
        GUI.Label(new Rect(x, y, 200, 20), "LOOK", Theme.Header);
        if (GUI.Button(new Rect(x + innerW - 70, y - 3, 70, 22), "Reset"))
            cm.ResetSettings();
        y += 24;

        GUI.enabled = !(cm.Mode == CameraMode.Tracking && cm.TrackingAutoZoom);
        cm.FieldOfView = SliderRow("oc.fov", ref y, "FOV", cm.FieldOfView, 30f, 130f, "0", "°");
        GUI.enabled = true;
        cm.Smoothing = SliderRow("oc.smooth", ref y, cm.Mode == CameraMode.FirstPerson ? "Turn smooth" : "Smoothing", cm.Smoothing, 0f, 1f, "", "", percent: true);
        if (cm.Mode != CameraMode.FirstPerson && cm.Mode != CameraMode.HandCam)
            cm.PositionLag = SliderRow("oc.plag", ref y, "Position lag", cm.PositionLag, 0f, 1f, "", "", percent: true);
        if (cm.Mode != CameraMode.FirstPerson)
            cm.LookHeight = SliderRow("oc.aim", ref y, "Aim height", cm.LookHeight, -1f, 1f, "0.00", "m");
        cm.Dutch = SliderRow("oc.dutch", ref y, "Tilt", cm.Dutch, -30f, 30f, "0", "°");
        cm.Shake = SliderRow("oc.shake", ref y, "Handheld", cm.Shake, 0f, 1f, "", "", percent: true);

        // ---- Motion blur ----
        cm.MotionBlur = Widgets.Switch("oc.blur", new Rect(x, y, 150, 26), cm.MotionBlur, "Motion blur",
            "Real-time camera motion blur for this view.");
        GUI.enabled = cm.MotionBlur;
        cm.MotionBlurStrength = Widgets.Slider("oc.blurstr", new Rect(x + 160, y, innerW - 160 - 52, 26), cm.MotionBlurStrength, 0f, 1f);
        GUI.Label(new Rect(x + innerW - 48, y, 48, 26), $"{cm.MotionBlurStrength * 100f:0}%", Theme.LabelRight);
        GUI.enabled = true;
        y += 38;

        // ---- Actions ----
        if (GUI.Button(new Rect(x, y, (innerW - 8) / 2f, 28), "Keyframe this view (V)"))
            KeyframeManager.Instance.CreateKeyframe();
        if (GUI.Button(new Rect(x + (innerW + 8) / 2f, y, (innerW - 8) / 2f, 28), "Back to free camera", Theme.AccentButton))
            cm.SetMode(CameraMode.Free);
        y += 34;

        GUI.color = prev;
        return y;
    }

    private float DrawPlayers(CameraModes cm, float x, float y, float innerW)
    {
        const float rowH = 30f, gap = 3f, viewH = 132f;
        var players = cm.Players;

        Rect view = new Rect(x, y, innerW, viewH);
        Theme.Fill(view, Theme.Field, 8);

        Rect content = new Rect(0, 0, innerW - 16, Mathf.Max(players.Count * (rowH + gap) + 4, viewH - 2));
        _playerScroll = GUI.BeginScrollView(new Rect(view.x + 3, view.y + 1, view.width - 4, view.height - 2), _playerScroll, content);

        if (players.Count == 0)
            GUI.Label(new Rect(0, viewH / 2f - 12, content.width, 24), "Looking for gorillas...", Theme.MutedCenter);

        for (int i = 0; i < players.Count; i++)
        {
            CamSubject rig = players[i];
            if (rig == null) continue;

            Rect row = new Rect(3, 3 + i * (rowH + gap), content.width - 6, rowH);
            bool selected = rig == cm.Target;

            float h = Anim.To($"oc.p.{rig.GetInstanceID()}", selected ? 1f : (row.Contains(Event.current.mousePosition) ? 0.4f : 0f), 16f);
            if (h > 0.01f)
                Theme.Fill(row, Theme.Accent.WithAlpha(0.22f * h), 6);
            if (selected)
                Theme.Fill(new Rect(row.x, row.y + 7, 3, row.height - 14), Theme.Accent, 1);

            Color dot = rig.IsReplay ? Theme.Danger : rig.IsLocal ? Theme.Accent : Theme.TextMuted;
            Theme.Dot(new Vector2(row.x + 18, row.center.y), 5f, rig.Available ? dot : dot.WithAlpha(0.35f));
            Theme.DrawText(new Rect(row.x + 32, row.y, row.width - 130, row.height), rig.DisplayName, Theme.Label, rig.Available ? Theme.Text : Theme.TextMuted);
            string tag = rig.IsReplay ? (rig.Available ? (rig.IsLocal ? "replay · you" : "replay") : "replay · not here yet")
                : rig.IsLocal ? "yourself" : "";
            if (tag.Length > 0)
                Theme.DrawText(new Rect(row.xMax - 150, row.y, 142, row.height), tag, Theme.MutedRight, Theme.TextMuted);

            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
                cm.SetTarget(rig);
        }

        GUI.EndScrollView();
        return y + viewH + 6;
    }

    private void Hint(ref float y, string text)
    {
        GUI.Label(new Rect(16, y, _innerW, 34), text, Theme.MutedWrap);
        y += 36;
    }

    private float SliderRow(string id, ref float y, string label, float value, float min, float max, string fmt, string unit, bool percent = false)
    {
        float x = 16, innerW = _innerW;
        GUI.Label(new Rect(x, y, 100, 24), label);
        value = Widgets.Slider(id, new Rect(x + 100, y, innerW - 100 - 70, 24), value, min, max);
        string shown = percent ? $"{value * 100f:0}%" : value.ToString(fmt) + unit;
        GUI.Label(new Rect(x + innerW - 64, y, 64, 24), shown, Theme.LabelRight);
        y += 30;
        return value;
    }
}
