using System.Collections.Generic;
using System.Linq;
using MonkeFrames.Editor.Classes;
using UnityEngine;
using MonkeFrames.Compiler;
using MonkeFrames.Compiler.Models;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;

using Keyframe = MonkeFrames.Compiler.Models.Keyframe;

namespace MonkeFrames.Editor.Windows;

public class KeyframeEditor : IEditorWindow
{
    public string Name => "Keyframe Editor";
    public Rect Rect => new Rect(Screen.width - 620, 40, 600, Mathf.Min(842, Screen.height - 48));

    public Compiler.Models.Project Project => KeyframeManager.Instance.Project;
    public Vector2 KeyframeListScrollPos;

    private const float RowHeight = 40f;
    private const float RowGap = 4f;
    private const float ListTop = 70f;
    private const float ListHeight = 200f;

    private int _lastSelection = -2;
    private string _anchorGuid;   // Shift+click ranges start here

    private static readonly string[] EffectNames = ["Linear", "Sine", "Ease In", "Ease Out", "Smooth", "Custom", "Cut"];
    private static readonly TransitionEffect[] EffectOrder =
    [
        TransitionEffect.Linear, TransitionEffect.Sine, TransitionEffect.EaseIn,
        TransitionEffect.EaseOut, TransitionEffect.Smooth, TransitionEffect.Custom, TransitionEffect.Cut,
    ];

    private static string Clock(float h)
    {
        h = Mathf.Repeat(h, 24f);
        int hh = Mathf.FloorToInt(h), mm = Mathf.Clamp(Mathf.RoundToInt((h - hh) * 60f), 0, 59);
        return $"{hh:00}:{mm:00}";
    }

    /// <summary>The time Photoreal's sky shows by its own settings (ignoring keyframes); noon without Photoreal.</summary>
    private static float CurrentHour()
    {
        float h = PhotorealLink.ShownHour;
        return h >= 0f ? h : 12f;
    }

    private static string Describe(TransitionEffect e) => e switch
    {
        TransitionEffect.Linear => "Constant speed, straight to the next keyframe.",
        TransitionEffect.Sine => "Gently speeds up, then slows into the next keyframe.",
        TransitionEffect.EaseIn => "Starts slow and accelerates into the next keyframe.",
        TransitionEffect.EaseOut => "Starts fast and glides to a stop at the next keyframe.",
        TransitionEffect.Smooth => "Curved path that flows through keyframes without stopping. Tune the flow in Project Settings.",
        TransitionEffect.Cut => "Holds this shot, then cuts instantly to the next keyframe.",
        TransitionEffect.Custom => "Your own speed curve. Drag the two handles on the graph, or pick a preset below.",
        _ => "",
    };

    public void OnDraw()
    {
        float w = Rect.width;
        var keys = Project.Keyframes;
        int selection = UIManager.Instance.Selection;
        if (selection >= keys.Count) selection = UIManager.Instance.Selection = keys.Count - 1;

        // ---- Header ----
        GUI.Label(new Rect(16, 38, 200, 24), "KEYFRAMES", Theme.Header);
        Rect count = new Rect(96, 42, 30, 16);
        Theme.Fill(count, Theme.Accent.WithAlpha(0.2f), 8);
        Theme.DrawText(count, keys.Count.ToString(), Theme.LabelCenter, Theme.Accent);

        if (GUI.Button(new Rect(w - 196, 38, 90, 26), "+  Add", Theme.AccentButton))
            KeyframeManager.Instance.CreateKeyframe();

        // Auto smooth: a smooth curve through every keyframe, smoothed turning and zoom, softened speed changes -
        // every transition keeps its own timing and custom speed graph
        bool auto = Project.AutoSmooth;
        if (GUI.Button(new Rect(136, 38, w - 316 - 136 - 8, 26),
                new GUIContent(auto ? "Auto smooth: On" : "Auto smooth: Off",
                    "Smooths the camera's path, turning and zoom through all keyframes for a cinematic move (no corners at keyframes). Every keyframe is still hit exactly and every transition keeps its own timing and custom speed graph. Strength: Project Settings."),
                auto ? Theme.AccentButton : GUI.skin.button))
        {
            Project.AutoSmooth = !auto;
            UIManager.Instance.Status = Project.AutoSmooth
                ? $"Auto smooth on ({Project.AutoSmoothStrength * 100f:0}%): the camera glides through your keyframes, keeping your speed graphs."
                : "Auto smooth off: straight moves between keyframes again.";
        }

        if (GUI.Button(new Rect(w - 316, 38, 112, 26), new GUIContent("Close project", "Leave this keyframe project and start a fresh one (a backup is kept: Project > Reopen Last Closed).")))
        {
            KeyframeManager.Instance.CloseProject();
            return;
        }

        GUI.enabled = selection != -1;
        int selectedCount = KeyframeManager.Instance.SelectionCount;
        if (GUI.Button(new Rect(w - 100, 38, 84, 26), selectedCount > 1 ? $"Delete {selectedCount}" : "Delete", Theme.DangerButton))
        {
            KeyframeManager.Instance.DeleteSelection();
            if (UIManager.Instance.Selection >= keys.Count) UIManager.Instance.Selection = keys.Count - 1;
            selection = UIManager.Instance.Selection;
        }
        GUI.enabled = true;

        DrawList(w, selection);

        // ---- Properties ----
        float panelTop = ListTop + ListHeight + 12;
        Rect panel = new Rect(12, panelTop, w - 24, Rect.height - panelTop - 38);
        Theme.Fill(panel, Theme.Surface, 10);

        if (selection != _lastSelection)
        {
            Anim.Set("kf.props", 0f);
            _lastSelection = selection;
        }

        float appear = Anim.OutCubic(Anim.To("kf.props", 1f, 11f, 0f));
        Color prev = GUI.color;
        GUI.color = new Color(1, 1, 1, prev.a * appear);
        float slide = (1f - appear) * 12f;

        if (KeyframeManager.Instance.IsCompiling)
        {
            Widgets.Spinner(new Vector2(panel.center.x, panel.center.y - 12), 10f, Theme.Accent);
            GUI.Label(new Rect(panel.x, panel.center.y + 6, panel.width, 24), "Compiling, please wait...", Theme.MutedCenter);
        }
        else if (selection != -1)
        {
            DrawProperties(panel, selection, slide);
        }
        else
        {
            GUI.Label(new Rect(panel.x, panel.center.y - 20, panel.width, 20), "Select a keyframe to edit its properties.", Theme.MutedCenter);
            GUI.Label(new Rect(panel.x, panel.center.y + 2, panel.width, 20), "Press V to add one at the camera, T to add one facing your monke.", Theme.MutedCenter);
        }

        GUI.color = prev;

        // ---- Footer ----
        float total = 0f;
        foreach (Keyframe k in keys) total += k.Transition.Duration;
        GUI.Label(new Rect(16, Rect.height - 30, w - 32, 22), $"{keys.Count} keyframes", Theme.MutedSmall);
        GUI.Label(new Rect(16, Rect.height - 30, w - 32, 22), $"Total length  {total:F2}s", Theme.MutedRight);
    }

    private void DrawList(float w, int selection)
    {
        var keys = Project.Keyframes;
        Rect view = new Rect(12, ListTop, w - 24, ListHeight);
        Theme.Fill(view, Theme.Field, 10);

        float contentH = Mathf.Max(keys.Count * (RowHeight + RowGap) + 6, view.height - 2);
        Rect content = new Rect(0, 0, view.width - 16, contentH);

        KeyframeListScrollPos = GUI.BeginScrollView(new Rect(view.x + 4, view.y + 1, view.width - 6, view.height - 2),
            KeyframeListScrollPos, content);

        if (keys.Count == 0)
            GUI.Label(new Rect(0, view.height / 2f - 12, content.width, 24), "No keyframes yet", Theme.MutedCenter);

        // Sliding selection highlight
        float target = selection < 0 ? -1 : selection * (RowHeight + RowGap) + 4;
        float hy = Anim.To("kf.sel.y", target, 16f);
        float ha = Anim.To("kf.sel.a", selection < 0 ? 0f : 1f, 14f);
        if (ha > 0.01f && selection >= 0)
        {
            Rect hr = new Rect(2, hy, content.width - 4, RowHeight);
            Theme.Fill(hr, Theme.Accent.WithAlpha(0.18f * ha), 8);
            Theme.Fill(new Rect(hr.x, hr.y + 8, 3, hr.height - 16), Theme.Accent.WithAlpha(ha), 1);
        }

        KeyframeManager km = KeyframeManager.Instance;
        for (int i = 0; i < keys.Count; i++)
        {
            Keyframe k = keys[i];
            bool primary = i == selection;
            bool selected = primary || km.IsSelected(i);

            // Extra (Shift / Ctrl+click) selections get a softer, fading highlight; the primary has the sliding one.
            float extra = Anim.To("kf.row.x." + k.GUID, selected && !primary ? 1f : 0f, 16f);
            if (extra > 0.01f)
                Theme.Fill(new Rect(2, i * (RowHeight + RowGap) + 4, content.width - 4, RowHeight), Theme.Accent.WithAlpha(0.11f * extra), 8);

            // New rows slide in from the right and fade up.
            float enter = Anim.OutCubic(Anim.To("kf.row." + k.GUID, 1f, 10f, 0f));
            Rect row = new Rect(2 + (1f - enter) * 24f, i * (RowHeight + RowGap) + 4, content.width - 4, RowHeight);

            Color prev = GUI.color;
            GUI.color = new Color(1, 1, 1, prev.a * enter);

            float hover = Anim.To("kf.row.h." + k.GUID, row.Contains(Event.current.mousePosition) && !selected ? 1f : 0f, 18f);
            if (hover > 0.01f)
                Theme.Fill(row, new Color(1, 1, 1, 0.045f * hover), 8);

            // Number badge
            Vector2 badge = new Vector2(row.x + 22, row.center.y);
            Theme.Dot(badge, 12f, selected ? Theme.Accent : Theme.Raised);
            Theme.DrawText(new Rect(badge.x - 12, badge.y - 11, 24, 22), (i + 1).ToString(), Theme.LabelCenter,
                selected ? Theme.OnAccent : Theme.Text);

            Theme.DrawText(new Rect(row.x + 44, row.y + 3, 200, 20), $"Keyframe {i + 1}", Theme.Title, Theme.Text);
            Theme.DrawText(new Rect(row.x + 44, row.y + 19, row.width - 200, 18),
                $"pos {Vec(k.Position)}   rot {Vec(k.Rotation)}   fov {k.FieldOfView:0}{(k.MotionBlur ? "   blur" : "")}{(k.TimeOfDay >= 0 ? "   " + Clock(k.TimeOfDay) : "")}", Theme.MutedSmall, Theme.TextMuted);

            // Effect chip + duration
            string effect = EffectNames[System.Array.IndexOf(EffectOrder, k.Transition.Effect) is int ix and >= 0 ? ix : 0];
            Rect chip = new Rect(row.xMax - 140, row.y + 10, 72, 20);
            bool smooth = k.Transition.Effect == TransitionEffect.Smooth;
            Theme.Fill(chip, smooth ? Theme.Accent.WithAlpha(0.25f) : new Color(1, 1, 1, 0.07f), 10);
            Theme.DrawText(chip, effect, Theme.MutedCenter, smooth ? Theme.Accent : Theme.TextMuted);
            Theme.DrawText(new Rect(row.xMax - 64, row.y, 56, row.height), $"{k.Transition.Duration:0.0}s", Theme.LabelRight, Theme.Text);

            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                Event ev = Event.current;
                ClickRow(i, ev.shift, ev.control || ev.command);
                if (ev.clickCount > 1 && !ev.shift && !ev.control && !ev.command)
                    KeyframeManager.Instance.GoToKeyframe(i);
            }

            GUI.color = prev;
        }

        GUI.EndScrollView();
    }

    /// <summary>Plain click selects one keyframe, Shift+click a range, Ctrl+click adds / removes one.</summary>
    private void ClickRow(int i, bool shift, bool ctrl)
    {
        KeyframeManager km = KeyframeManager.Instance;
        var keys = Project.Keyframes;
        int primary = UIManager.Instance.Selection;

        if (shift && primary >= 0 && primary < keys.Count)
        {
            // Range from the last plain-clicked keyframe (or the current one) to this one.
            int anchor = _anchorGuid != null ? keys.FindIndex(k => k.GUID == _anchorGuid) : -1;
            if (anchor < 0) anchor = primary;

            HashSet<int> picked = ctrl ? new HashSet<int>(km.SelectedIndices()) : new HashSet<int>();
            for (int n = Mathf.Min(anchor, i); n <= Mathf.Max(anchor, i); n++) picked.Add(n);
            km.SetSelection(i, picked);
        }
        else if (ctrl && primary >= 0 && primary < keys.Count)
        {
            List<int> picked = km.SelectedIndices();
            if (picked.Contains(i))
            {
                if (picked.Count == 1) return;   // keep at least one selected
                picked.Remove(i);
                // Removing the primary hands it to the nearest remaining keyframe.
                int next = i == primary ? picked.OrderBy(n => Mathf.Abs(n - i)).First() : primary;
                km.SetSelection(next, picked);
            }
            else
            {
                picked.Add(i);
                km.SetSelection(i, picked);
            }
            _anchorGuid = keys[i].GUID;
        }
        else
        {
            km.SetSelection(i, null);
            _anchorGuid = keys[i].GUID;
        }
    }

    private void DrawProperties(Rect panel, int selection, float slide)
    {
        KeyframeManager km = KeyframeManager.Instance;

        // Everything edited below is one undo step (a drag or typing in one field stays one step).
        KeyframeHistory.State before = km.BeginPropertyEdit();

        List<int> selected = km.SelectedIndices();
        bool multi = selected.Count > 1;

        Keyframe k = Project.Keyframes[selection];
        Keyframe orig = k;

        float x = panel.x + 14;
        float y = panel.y + 12 + slide;
        float innerW = panel.width - 28;

        GUI.Label(new Rect(x, y, 200, 20), "TRANSFORM", Theme.Header);
        if (multi)
        {
            // Several keyframes: the settings below apply to all of them; the transform stays per keyframe.
            Rect chip = new Rect(x + 96, y - 1, 170, 22);
            Theme.Fill(chip, Theme.Accent.WithAlpha(0.2f), 11);
            Theme.DrawText(chip, $"{selected.Count} keyframes selected", Theme.LabelCenterSmall, Theme.Accent);
            GUI.Label(new Rect(chip.xMax + 8, y, panel.xMax - 128 - chip.xMax - 8, 20), "transform: one at a time", Theme.MutedSmall);
        }
        if (GUI.Button(new Rect(panel.xMax - 120, y - 2, 106, 24), "Go to (F)"))
            KeyframeManager.Instance.GoToKeyframe(selection);
        y += 26;

        float labelW = 70;
        float fieldW = (innerW - labelW - 16) / 3f;

        // Position, rotation and FOV are per keyframe: read-only while several are selected.
        GUI.enabled = !multi;
        GUI.Label(new Rect(x, y, labelW, 24), "Position");
        k.Position.x = Widgets.FloatField("px", new Rect(x + labelW, y, fieldW, 24), "X", Theme.AxisX, k.Position.x);
        k.Position.y = Widgets.FloatField("py", new Rect(x + labelW + fieldW + 8, y, fieldW, 24), "Y", Theme.AxisY, k.Position.y);
        k.Position.z = Widgets.FloatField("pz", new Rect(x + labelW + (fieldW + 8) * 2, y, fieldW, 24), "Z", Theme.AxisZ, k.Position.z);
        y += 30;

        GUI.Label(new Rect(x, y, labelW, 24), "Rotation");
        k.Rotation.x = Widgets.FloatField("rx", new Rect(x + labelW, y, fieldW, 24), "X", Theme.AxisX, k.Rotation.x);
        k.Rotation.y = Widgets.FloatField("ry", new Rect(x + labelW + fieldW + 8, y, fieldW, 24), "Y", Theme.AxisY, k.Rotation.y);
        k.Rotation.z = Widgets.FloatField("rz", new Rect(x + labelW + (fieldW + 8) * 2, y, fieldW, 24), "Z", Theme.AxisZ, k.Rotation.z);
        y += 30;

        GUI.Label(new Rect(x, y, labelW, 24), "FOV");
        k.FieldOfView = SliderValue("fov", new Rect(x + labelW, y, innerW - labelW - 90, 24), k.FieldOfView, 20f, 130f);
        k.FieldOfView = Widgets.FloatField("fov", new Rect(x + innerW - 80, y, 80, 24), "°", Theme.TextMuted, k.FieldOfView);
        GUI.enabled = true;
        y += 32;

        // Time of day (photoreal sky): switch + hour slider, blends to the next keyframe's time
        bool timeOn = k.TimeOfDay >= 0f;
        bool newOn = Widgets.Switch("kf.time", new Rect(x, y, 150, 26), timeOn, "Time of day",
            "Set the sky's time at this keyframe (needs the Photoreal graphics mod). It blends to the next keyframe's time during the transition (the short way round midnight). Off = use Photoreal's own time.");
        if (newOn != timeOn)
        {
            k.TimeOfDay = newOn ? CurrentHour() : -1f;
            PhotorealLink.KeyframeHour = k.TimeOfDay;
        }
        GUI.enabled = newOn;
        float shown = newOn ? k.TimeOfDay : CurrentHour();
        float hour = Widgets.Slider("kf.timeh", new Rect(x + 160, y, innerW - 160 - 150, 26), shown, 0f, 24f);
        GUI.Label(new Rect(x + innerW - 144, y, 50, 26), Clock(newOn ? hour : shown), Theme.LabelRight);
        if (newOn && Mathf.Abs(hour - k.TimeOfDay) > 1e-4f)
        {
            k.TimeOfDay = Mathf.Clamp(hour, 0f, 23.999f);
            PhotorealLink.KeyframeHour = k.TimeOfDay;   // preview the change straight away
        }
        GUI.enabled = true;
        if (GUI.Button(new Rect(x + innerW - 86, y, 86, 26), new GUIContent("Now", "Set this keyframe's time to the time Photoreal currently shows.")))
        {
            k.TimeOfDay = CurrentHour();
            PhotorealLink.KeyframeHour = k.TimeOfDay;
        }
        y += 34;

        // Motion blur: switch + strength, plus a quick "apply to all"
        k.MotionBlur = Widgets.Switch("kf.blur", new Rect(x, y, 150, 26), k.MotionBlur, "Motion blur",
            "Blur the camera while it moves from this keyframe to the next (preview, playback and MP4 export).");
        GUI.enabled = k.MotionBlur;
        k.MotionBlurStrength = SliderValue("kf.blurstr",new Rect(x + 160, y, innerW - 160 - 150, 26), k.MotionBlurStrength, 0f, 1f);
        GUI.Label(new Rect(x + innerW - 144, y, 50, 26), $"{k.MotionBlurStrength * 100f:0}%", Theme.LabelRight);
        GUI.enabled = true;
        if (GUI.Button(new Rect(x + innerW - 86, y, 86, 26), new GUIContent("All", "Copy this keyframe's motion blur setting to every keyframe.")))
        {
            for (int i = 0; i < Project.Keyframes.Count; i++)
            {
                Keyframe other = Project.Keyframes[i];
                other.MotionBlur = k.MotionBlur;
                other.MotionBlurStrength = k.MotionBlurStrength;
                Project.Keyframes[i] = other;
            }
            UIManager.Instance.Status = k.MotionBlur
                ? $"Motion blur ({k.MotionBlurStrength * 100f:0}%) turned on for all {Project.Keyframes.Count} keyframes."
                : "Motion blur turned off for all keyframes.";
        }
        y += 38;

        Widgets.Divider(x, y - 6, innerW);

        // ---- Transition ----
        GUI.Label(new Rect(x, y, 200, 20), "TRANSITION TO NEXT", Theme.Header);
        y += 24;

        int current = System.Array.IndexOf(EffectOrder, k.Transition.Effect);
        int chosen = Widgets.Segmented("kf.effect", new Rect(x, y, innerW, 28), current, EffectNames);
        if (chosen != current && chosen >= 0)
            k.Transition.Effect = EffectOrder[chosen];
        y += 36;

        // ---- Left column: description, duration, options, bulk actions ----
        const float graphW = 250f, graphH = 176f;
        float leftW = innerW - graphW - 14;
        Rect graph = new Rect(x + innerW - graphW, y, graphW, graphH);

        GUI.Label(new Rect(x, y, leftW, 40), Describe(k.Transition.Effect), Theme.MutedWrap);

        GUI.Label(new Rect(x, y + 46, 70, 24), "Duration");
        k.Transition.Duration = SliderValue("dur", new Rect(x + 70, y + 46, leftW - 136, 24), k.Transition.Duration, 0f, 30f);
        k.Transition.Duration = Mathf.Max(0f, Widgets.FloatField("dur", new Rect(x + leftW - 60, y + 46, 60, 24), "s", Theme.TextMuted, k.Transition.Duration));

        bool isSmooth = k.Transition.Effect == TransitionEffect.Smooth;
        if (isSmooth)
            k.Transition.CustomSpeed = Widgets.Switch("kf.customspeed", new Rect(x, y + 80, leftW, 26), k.Transition.CustomSpeed,
                "Custom speed curve", "Shape how fast the camera moves along the smooth path using the graph.");

        if (GUI.Button(new Rect(x, y + 116, leftW, 26), "Apply to all keyframes"))
        {
            for (int i = 0; i < Project.Keyframes.Count; i++)
            {
                Keyframe other = Project.Keyframes[i];
                other.Transition = k.Transition;
                Project.Keyframes[i] = other;
            }
            UIManager.Instance.Status = $"Applied \"{EffectNames[Mathf.Max(0, System.Array.IndexOf(EffectOrder, k.Transition.Effect))]}\" ({k.Transition.Duration:0.00}s) and its speed curve to all {Project.Keyframes.Count} keyframes.";
        }

        if (GUI.Button(new Rect(x, y + 148, leftW, 26), "Make all Smooth"))
        {
            for (int i = 0; i < Project.Keyframes.Count; i++)
            {
                Keyframe other = Project.Keyframes[i];
                other.Transition.Effect = TransitionEffect.Smooth;
                Project.Keyframes[i] = other;
            }
            k.Transition.Effect = TransitionEffect.Smooth;
            UIManager.Instance.Status = "All keyframes now use Smooth transitions.";
        }

        // ---- Right column: speed graph ----
        bool editable = k.Transition.UsesCurve;
        if (isSmooth && !editable)
        {
            DrawCurvePreview(graph, TransitionEffect.Smooth);
            Theme.DrawText(new Rect(graph.x, graph.yMax - 18, graph.width, 16), "Turn on Custom speed curve to edit", Theme.LabelCenterSmall, Theme.TextMuted);
        }
        else
        {
            Vector4 curve = new Vector4(k.Transition.CurveX1, k.Transition.CurveY1, k.Transition.CurveX2, k.Transition.CurveY2);
            TransitionEffect effect = k.Transition.Effect;
            if (CurveEditor.Draw("kf.curve", graph, ref curve, editable, t => effect == TransitionEffect.Cut ? (t < 0.999f ? 0f : 1f) : Easing.Evaluate(effect, t)))
            {
                k.Transition.CurveX1 = curve.x; k.Transition.CurveY1 = curve.y;
                k.Transition.CurveX2 = curve.z; k.Transition.CurveY2 = curve.w;
            }
        }
        y += graphH + 10;

        // ---- Speed curve presets (picking one switches to a custom curve) ----
        if (k.Transition.Effect != TransitionEffect.Cut)
        {
            GUI.Label(new Rect(x, y, 70, 26), "Speed", Theme.Label);
            Vector4 cur = new Vector4(k.Transition.CurveX1, k.Transition.CurveY1, k.Transition.CurveX2, k.Transition.CurveY2);
            int preset = CurveEditor.PresetRow("kf.presets", new Rect(x + 60, y, innerW - 60, 26), k.Transition.UsesCurve ? cur : new Vector4(-9, -9, -9, -9));
            if (preset >= 0)
            {
                Vector4 c = CurveEditor.Presets[preset].curve;
                k.Transition.CurveX1 = c.x; k.Transition.CurveY1 = c.y;
                k.Transition.CurveX2 = c.z; k.Transition.CurveY2 = c.w;
                if (isSmooth) k.Transition.CustomSpeed = true;
                else k.Transition.Effect = TransitionEffect.Custom;
            }
        }

        Project.Keyframes[selection] = k;

        if (multi)
            ApplyToSelected(selected, selection, orig, k);

        km.EndPropertyEdit(before, "keyframe:" + k.GUID);
    }

    /// <summary>Copies each setting changed on the primary keyframe (<paramref name="before"/> to
    /// <paramref name="after"/>) to the other selected keyframes. Unchanged settings stay their own.</summary>
    private void ApplyToSelected(List<int> selected, int primary, Keyframe before, Keyframe after)
    {
        Transition a = before.Transition, b = after.Transition;
        bool effect = a.Effect != b.Effect;
        bool duration = a.Duration != b.Duration;
        bool curve = a.CurveX1 != b.CurveX1 || a.CurveY1 != b.CurveY1 || a.CurveX2 != b.CurveX2 || a.CurveY2 != b.CurveY2;
        bool customSpeed = a.CustomSpeed != b.CustomSpeed;
        bool blur = before.MotionBlur != after.MotionBlur;
        bool blurStrength = before.MotionBlurStrength != after.MotionBlurStrength;
        bool time = before.TimeOfDay != after.TimeOfDay;

        if (!(effect || duration || curve || customSpeed || blur || blurStrength || time))
            return;

        var keys = Project.Keyframes;
        foreach (int i in selected)
        {
            if (i == primary || i < 0 || i >= keys.Count)
                continue;

            Keyframe o = keys[i];
            if (effect) o.Transition.Effect = b.Effect;
            if (duration) o.Transition.Duration = b.Duration;
            if (curve)
            {
                o.Transition.CurveX1 = b.CurveX1; o.Transition.CurveY1 = b.CurveY1;
                o.Transition.CurveX2 = b.CurveX2; o.Transition.CurveY2 = b.CurveY2;
            }
            if (customSpeed) o.Transition.CustomSpeed = b.CustomSpeed;
            if (blur) o.MotionBlur = after.MotionBlur;
            if (blurStrength) o.MotionBlurStrength = after.MotionBlurStrength;
            if (time) o.TimeOfDay = after.TimeOfDay;
            keys[i] = o;
        }
    }

    /// <summary>A slider that only changes the value when it's actually moved (values outside its range,
    /// e.g. a typed 45 s duration, aren't clamped just by being shown).</summary>
    private static float SliderValue(string id, Rect rect, float value, float min, float max)
    {
        float result = Widgets.Slider(id, rect, value, min, max);
        return Mathf.Approximately(result, Mathf.Clamp(value, min, max)) ? value : result;
    }

    /// <summary>Draws the easing curve with a dot that travels along it on a loop.</summary>
    private static void DrawCurvePreview(Rect r, TransitionEffect effect)
    {
        Theme.Fill(r, Theme.Field, 8);

        Rect g = new Rect(r.x + 10, r.y + 10, r.width - 20, r.height - 20);
        Color line = Theme.Accent;
        const int samples = 56;

        if (effect == TransitionEffect.Smooth)
        {
            // Four "keyframes" and a flowing spline through them.
            Vector2[] pts =
            [
                new(g.x, g.yMax), new(g.x + g.width * 0.33f, g.y + g.height * 0.2f),
                new(g.x + g.width * 0.66f, g.y + g.height * 0.75f), new(g.xMax, g.y),
            ];

            for (int s = 0; s <= samples; s++)
                Theme.Dot(CatmullRom(pts, s / (float)samples), 1.4f, line.WithAlpha(0.85f));

            foreach (Vector2 p in pts)
                Theme.Dot(p, 3.2f, Color.white);

            float tt = Mathf.Repeat(Time.unscaledTime * 0.45f, 1f);
            Theme.Dot(CatmullRom(pts, Easing.Evaluate(TransitionEffect.Smooth, tt)), 4.5f, line);
            return;
        }

        for (int s = 0; s <= samples; s++)
        {
            float t = s / (float)samples;
            float v = effect == TransitionEffect.Cut ? (t < 0.999f ? 0f : 1f) : Easing.Evaluate(effect, t);
            Theme.Dot(new Vector2(g.x + g.width * t, g.yMax - g.height * v), 1.4f, line.WithAlpha(0.85f));
        }

        if (effect == TransitionEffect.Cut)
            for (int s = 0; s <= 12; s++)
                Theme.Dot(new Vector2(g.xMax, g.yMax - g.height * s / 12f), 1.1f, line.WithAlpha(0.35f));

        float pt = Mathf.Repeat(Time.unscaledTime * 0.5f, 1.25f);
        pt = Mathf.Clamp01(pt);
        float pv = effect == TransitionEffect.Cut ? (pt < 1f ? 0f : 1f) : Easing.Evaluate(effect, pt);
        Theme.Dot(new Vector2(g.x + g.width * pt, g.yMax - g.height * pv), 4.5f, line);
    }

    private static Vector2 CatmullRom(Vector2[] p, float t)
    {
        int segs = p.Length - 1;
        float f = Mathf.Clamp01(t) * segs;
        int i = Mathf.Min(Mathf.FloorToInt(f), segs - 1);
        float u = f - i;

        Vector2 p0 = p[Mathf.Max(i - 1, 0)], p1 = p[i], p2 = p[i + 1], p3 = p[Mathf.Min(i + 2, segs)];
        return 0.5f * (2f * p1 + (-p0 + p2) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u * u + (-p0 + 3f * p1 - 3f * p2 + p3) * u * u * u);
    }

    private static string Vec(Vector3 v) => $"{v.x:0.#}, {v.y:0.#}, {v.z:0.#}";
}
