using System;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;
using UnityEngine;

namespace MonkeFrames.Editor.Classes;

public class IEditorWindowManager
{
    public static int WindowIDs = 0;
    public static GUIStyle WindowStyle => Theme.WindowStyle;

    /// <summary>Seconds a window takes to open/close at 1x animation speed.</summary>
    public const float TransitionTime = 0.24f;

    public IEditorWindow Window;
    public Rect WindowPosition;
    public int WindowID;

    public bool Visible = false;
    public bool LastVisible = false;

    /// <summary>Linear 0..1 open progress. Eased when drawn.</summary>
    public float Progress;

    public IEditorWindowManager(IEditorWindow window)
    {
        Window = window;
        WindowPosition = window.Rect;

        WindowIDs++;
        WindowID = WindowIDs;
    }

    /// <summary>Advance the open/close transition. Called once per frame from UIManager.Update.</summary>
    public void Tick(float dt)
    {
        float target = Visible ? 1f : 0f;
        Progress = Mathf.MoveTowards(Progress, target, dt * Anim.Speed / TransitionTime);
    }

    /// <summary>Eased visibility used for alpha.</summary>
    private float Alpha => Visible ? Anim.OutCubic(Progress) : Anim.InCubic(Progress) * 0.85f + Progress * 0.15f;

    /// <summary>Eased scale: pops in with a little overshoot, shrinks slightly when closing.</summary>
    private float Scale => Visible
        ? Mathf.LerpUnclamped(0.9f, 1f, Anim.OutBack(Progress))
        : Mathf.Lerp(0.95f, 1f, Anim.OutCubic(Progress));

    private void CreateWindow(int windowId)
    {
        // Window layout
        //  _________________________________
        // | [icon] Window Name          [×] | <-- title bar (drawn here)
        // |=====accent line (animates)======|
        // |                                 |
        // |     content (Window.OnDraw)     |
        // |_________________________________|

        GUI.color = new Color(1, 1, 1, Alpha);

        // Swallow input while the window is fading out so nothing is clicked by accident.
        if (!Visible && (Event.current.isMouse || Event.current.isKey))
            Event.current.Use();

        float w = WindowPosition.width;
        bool focused = UIManager.Instance.FocusedWindow == WindowID;

        // Title bar
        GUI.DrawTexture(new Rect(10, 7, 16, 16), UIManager.Instance.Icon);
        Theme.DrawText(new Rect(32, 5, w - 70, 20), Window.Name, Theme.Title, focused ? Theme.Text : Theme.TextMuted);

        // Accent line grows out from the centre as the window opens
        float line = Anim.OutCubic(Progress);
        Theme.Fill(new Rect(0, Theme.TitleHeight - 2, w, 1), Theme.Border, 0);
        float lw = (w - 24) * line * (focused ? 1f : 0.35f);
        Theme.Fill(new Rect(w / 2f - lw / 2f, Theme.TitleHeight - 2.5f, lw, 2), Theme.Accent.WithAlpha(focused ? 0.9f : 0.5f), 1);

        // Close button (red hover fade)
        Rect close = new Rect(w - 28, 5, 20, 20);
        float ch = Anim.To("win.close." + WindowID, close.Contains(Event.current.mousePosition) ? 1f : 0f, 20f);
        if (ch > 0.01f)
            Theme.Fill(close, Theme.Danger.WithAlpha(0.85f * ch), 10);
        Theme.DrawText(new Rect(close.x, close.y - 1, close.width, close.height), "×", Theme.LabelCenter,
            Color.Lerp(Theme.TextMuted, Color.white, ch));

        if (GUI.Button(close, GUIContent.none, GUIStyle.none))
            Visible = false;

        if (Event.current.type == EventType.MouseDown)
            UIManager.Instance.FocusedWindow = WindowID;

        try
        {
            Window.OnDraw();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error drawing window \"{Window.Name}\" ({WindowID}): {ex.Message}");
        }

        GUI.DragWindow(new Rect(0, 0, w - 32, Theme.TitleHeight));
    }

    public void Draw()
    {
        // Fire open/close callbacks *before* drawing so OnDraw never runs un-initialised.
        if (Visible != LastVisible)
        {
            if (Visible)
            {
                Window.OnOpen();
                UIManager.Instance.FocusedWindow = WindowID;
                GUI.BringWindowToFront(WindowID);
            }
            else
            {
                Window.OnClose();
            }

            LastVisible = Visible;
        }

        if (!Visible && Progress <= 0f)
            return;

        Color prevColor = GUI.color;
        Color prevBg = GUI.backgroundColor;
        Matrix4x4 prevMatrix = GUI.matrix;

        float alpha = Alpha;
        GUI.color = new Color(1, 1, 1, alpha);
        GUI.backgroundColor = Color.white;

        // Slide up + scale around the window centre while animating.
        Rect drawRect = WindowPosition;
        drawRect.y += (1f - Anim.OutCubic(Progress)) * (Visible ? 14f : 6f);

        bool animating = Progress < 1f;
        if (animating)
        {
            float s = Scale;
            GUIUtility.ScaleAroundPivot(new Vector2(s, s), drawRect.center);
        }

        Rect result = GUI.Window(WindowID, drawRect, CreateWindow, GUIContent.none, WindowStyle);

        // Only accept drag results once the window is settled (the draw rect is offset while animating).
        if (!animating && Visible)
            WindowPosition = result;

        GUI.matrix = prevMatrix;
        GUI.color = prevColor;
        GUI.backgroundColor = prevBg;

        // Keep windows reachable on screen.
        WindowPosition.x = Mathf.Clamp(WindowPosition.x, -WindowPosition.width + 80, Screen.width - 80);
        WindowPosition.y = Mathf.Clamp(WindowPosition.y, UIManager.MenuBarHeight, Screen.height - 40);
    }
}
