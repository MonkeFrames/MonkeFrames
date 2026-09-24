using System.Diagnostics;
using System.Linq;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;
using UnityEngine;

namespace MonkeFrames.Editor.Windows;

public class About : IEditorWindow
{
    public string Name => "About MonkeFrames";
    public Rect Rect => new Rect(Screen.width / 2 - 250, Screen.height / 2 - 220, 500, 440);

    private Vector2 ScrollPosition;

    public void OnOpen() => Anim.Set("about.intro", 0f);

    public void OnDraw()
    {
        float w = Rect.width;
        float intro = Anim.OutCubic(Anim.To("about.intro", 1f, 6f, 0f));

        // Hero: icon + title slide in
        Color prev = GUI.color;
        GUI.color = new Color(1, 1, 1, prev.a * intro);

        float ox = (1f - intro) * -16f;
        Theme.Dot(new Vector2(46 + ox, 72), 26f, Theme.Accent.WithAlpha(0.18f));
        GUI.DrawTexture(new Rect(28 + ox, 54, 36, 36), UIManager.Instance.Icon);

        GUI.Label(new Rect(84 + ox, 46, w - 100, 32), "MonkeFrames", Theme.Big);
        GUI.Label(new Rect(86 + ox, 76, w - 100, 20), $"Version {Constants.VersionID}  ·  Build {Constants.BuildDate}", Theme.Muted);
        GUI.Label(new Rect(86 + ox, 94, w - 100, 20), "(C) Copyright 2026 SirKingBinx", Theme.MutedSmall);

        GUI.color = prev;

        // Credits list
        Rect box = new Rect(14, 124, w - 28, Rect.height - 124 - 54);
        Theme.Fill(box, Theme.Field, 10);
        GUI.Label(new Rect(box.x + 12, box.y + 8, 200, 18), "CREDITS", Theme.Header);

        var credits = Constants.Contributors.Where(c => !string.IsNullOrEmpty(c.Key)).ToList();
        const float rowH = 24f;
        Rect view = new Rect(box.x + 4, box.y + 30, box.width - 8, box.height - 36);
        Rect content = new Rect(0, 0, view.width - 16, credits.Count * rowH);

        ScrollPosition = GUI.BeginScrollView(view, ScrollPosition, content);
        for (int i = 0; i < credits.Count; i++)
        {
            // Stagger each name in slightly after the previous one when the window opens.
            float e = Anim.OutCubic(Mathf.Clamp01(intro * 3f - i * 0.08f));
            Color c = GUI.color;
            GUI.color = new Color(1, 1, 1, c.a * e);

            Rect row = new Rect(8 + (1f - e) * 10f, i * rowH, content.width - 16, rowH);
            if (i % 2 == 0)
                Theme.Fill(row, new Color(1, 1, 1, 0.025f), 5);

            bool dev = credits[i].Value == "Developer";
            GUI.Label(new Rect(row.x + 8, row.y, row.width * 0.6f, rowH), credits[i].Key);

            Rect tag = new Rect(row.xMax - 84, row.y + 3, 78, rowH - 6);
            Theme.Fill(tag, dev ? Theme.Accent.WithAlpha(0.22f) : new Color(1, 1, 1, 0.06f), 9);
            Theme.DrawText(tag, credits[i].Value, Theme.MutedCenter, dev ? Theme.Accent : Theme.TextMuted);

            GUI.color = c;
        }
        GUI.EndScrollView();

        if (GUI.Button(new Rect(w - 110, Rect.height - 42, 96, 28), "OK", Theme.AccentButton))
            UIManager.Instance.CloseWindow("About MonkeFrames");
    }
}
