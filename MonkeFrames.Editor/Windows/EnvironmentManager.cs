using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MonkeFrames.Editor.Windows;

public class EnvironmentManager : IEditorWindow
{
    public string Name => "Environment Manager";
    public Rect Rect => new Rect(400, 200, 420, 420);

    public Dictionary<string, GorillaSetZoneTrigger> Maps = new();
    public Vector2 ScrollPos;

    public void OnDraw()
    {
        float w = Rect.width;

        GUI.Label(new Rect(16, 38, 200, 20), "MAPS", Theme.Header);

        Rect box = new Rect(12, 60, w - 24, Rect.height - 60 - 132);
        Theme.Fill(box, Theme.Field, 10);

        var maps = Maps.ToList();
        const float cellH = 30f, gap = 6f;
        float colW = (box.width - 16 - 8 - gap) / 2f;
        int rows = (maps.Count + 1) / 2;

        Rect view = new Rect(box.x + 4, box.y + 4, box.width - 8, box.height - 8);
        Rect content = new Rect(0, 0, view.width - 16, Mathf.Max(rows * (cellH + gap), view.height - 1));

        ScrollPos = GUI.BeginScrollView(view, ScrollPos, content);
        if (maps.Count == 0)
            GUI.Label(new Rect(0, view.height / 2f - 12, content.width, 24), "No maps found in this area.", Theme.MutedCenter);

        for (int i = 0; i < maps.Count; i++)
        {
            int col = i % 2, row = i / 2;
            float e = Anim.OutCubic(Anim.To("env.map." + maps[i].Key, 1f, 9f, 0f));
            Rect cell = new Rect(4 + col * (colW + gap), row * (cellH + gap) + 4 + (1f - e) * 10f, colW, cellH);

            Color c = GUI.color;
            GUI.color = new Color(1, 1, 1, c.a * e);
            if (GUI.Button(cell, maps[i].Key))
            {
                maps[i].Value.OnBoxTriggered();
                UIManager.Instance.Status = $"Loading {maps[i].Key}...";
            }
            GUI.color = c;
        }
        GUI.EndScrollView();

        // ---- Conditions ----
        float y = Rect.height - 124;
        GUI.Label(new Rect(16, y, 200, 20), "CONDITIONS", Theme.Header);
        y += 24;

        int maxTime = Mathf.Max(0, BetterDayNightManager.instance.timeOfDayRange.Length - 1);
        GUI.Label(new Rect(16, y, 70, 24), "Time");
        ConditionManager.Time = Mathf.RoundToInt(Widgets.Slider("env.time", new Rect(86, y, w - 150, 24), ConditionManager.Time, 0, maxTime));
        GUI.Label(new Rect(w - 60, y, 44, 24), $"{ConditionManager.Time}/{maxTime}", Theme.MutedRight);
        y += 32;

        bool rain = ConditionManager.Conditions == BetterDayNightManager.WeatherType.Raining;
        rain = Widgets.Switch("env.rain", new Rect(16, y, 200, 26), rain, "Rain / Snow");
        ConditionManager.Conditions = rain ? BetterDayNightManager.WeatherType.Raining : BetterDayNightManager.WeatherType.None;
        y += 30;

        GUI.Label(new Rect(16, y, w - 32, 20), "Note: switching maps may make you fall out of the world.", Theme.MutedSmall);
    }

    public void OnOpen()
    {
        Maps = new();
        var triggers = Object.FindObjectsByType<GorillaSetZoneTrigger>(FindObjectsSortMode.None);

        foreach (GorillaSetZoneTrigger trigger in triggers)
        {
            string name = trigger.gameObject.name;
            int mapNameStart = name.IndexOf("To") + 2;

            if (mapNameStart == 1)
                continue;

            Maps.TryAdd(name[mapNameStart ..], trigger);
        }
    }
}
