using MonkeFrames.Compiler.Models;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace MonkeFrames.Editor.Windows;

public class ObjectManagerWindow : IEditorWindow
{
    public string Name => "Object Manager";
    public Rect Rect => new Rect(100, 80, 500, 720);

    private Vector2 _availableScroll;
    private Vector2 _spawnedScroll;

    public void OnDraw()
    {
        float w = Rect.width;
        ObjectManager om = ObjectManager.Instance;

        if (om == null)
        {
            GUI.Label(new Rect(16, 40, 400, 24), "Object manager is loading...", Theme.Muted);
            return;
        }

        float y = 38f;

        // ---- Title Header & Refresh Button ----
        GUI.Label(new Rect(16, y, 220, 24), "OBJECT SPAWNER", Theme.Header);

        if (GUI.Button(new Rect(w - 110, y - 2, 94, 26), "Refresh", Theme.AccentButton))
        {
            om.EnsureDirectoryExists();
            om.SyncWithProject();
            UIManager.Instance.Status = "Refreshed Objects folder.";
        }

        if (GUI.Button(new Rect(w - 214, y - 2, 94, 26), "Open Folder", Theme.AccentButton))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ObjectManager.ObjectsFolder,
                UseShellExecute = true
            });
        }
        y += 28f;

        // ---- Section 1: Available Models (.obj files) ----
        GUI.Label(new Rect(16, y, 250, 20), "AVAILABLE .OBJ MODELS", Theme.MutedSmall);
        y += 20f;

        List<string> availableFiles = om.GetAvailableObjectFiles();
        Rect availableBox = new Rect(12, y, w - 24, 140);
        Theme.Fill(availableBox, Theme.Field, 8);

        float availContentH = Mathf.Max(availableFiles.Count * 32f + 4f, availableBox.height - 2f);
        Rect availContent = new Rect(0, 0, availableBox.width - 16f, availContentH);

        _availableScroll = GUI.BeginScrollView(new Rect(availableBox.x + 3, availableBox.y + 2, availableBox.width - 4, availableBox.height - 4),
            _availableScroll, availContent);

        if (availableFiles.Count == 0)
        {
            GUI.Label(new Rect(0, availableBox.height / 2f - 18, availContent.width, 20), "No .obj files found in Objects/ folder.", Theme.MutedCenter);
            GUI.Label(new Rect(0, availableBox.height / 2f + 2, availContent.width, 20), "Add .obj files to the Objects/ directory to spawn them.", Theme.MutedSmall);
        }
        else
        {
            for (int i = 0; i < availableFiles.Count; i++)
            {
                string fileName = availableFiles[i];
                Rect row = new Rect(4, 4 + i * 32f, availContent.width - 8, 28f);

                bool hover = row.Contains(Event.current.mousePosition);
                float h = Anim.To($"obj.file.h.{i}", hover ? 1f : 0f, 18f);

                if (h > 0.01f)
                    Theme.Fill(row, Theme.Accent.WithAlpha(0.15f * h), 6);

                Theme.DrawText(new Rect(row.x + 10, row.y, row.width - 90, row.height), fileName, Theme.Label, Theme.Text);

                if (GUI.Button(new Rect(row.xMax - 74, row.y + 2, 70, 24), "+ Spawn", Theme.AccentButton))
                {
                    om.SpawnObject(fileName);
                }
            }
        }
        GUI.EndScrollView();

        y += availableBox.height + 14f;
        Widgets.Divider(12, y - 6, w - 24);

        // ---- Section 2: Spawned Objects & Selection ----
        GUI.Label(new Rect(16, y, 220, 20), "SPAWNED OBJECTS", Theme.Header);

        int count = om.SpawnedObjects.Count;
        Rect countBadge = new Rect(160, y + 2, 30, 16);
        Theme.Fill(countBadge, Theme.Accent.WithAlpha(0.2f), 8);
        Theme.DrawText(countBadge, count.ToString(), Theme.LabelCenter, Theme.Accent);

        GUI.enabled = om.SelectedIndex >= 0 && om.SelectedIndex < om.SpawnedObjects.Count;
        if (GUI.Button(new Rect(w - 100, y - 2, 88, 26), "Delete", Theme.DangerButton))
        {
            om.DeleteSelectedObject();
        }
        GUI.enabled = true;
        y += 26f;

        Rect spawnedBox = new Rect(12, y, w - 24, 130);
        Theme.Fill(spawnedBox, Theme.Field, 8);

        float spawnedContentH = Mathf.Max(om.SpawnedObjects.Count * 34f + 4f, spawnedBox.height - 2f);
        Rect spawnedContent = new Rect(0, 0, spawnedBox.width - 16f, spawnedContentH);

        _spawnedScroll = GUI.BeginScrollView(new Rect(spawnedBox.x + 3, spawnedBox.y + 2, spawnedBox.width - 4, spawnedBox.height - 4),
            _spawnedScroll, spawnedContent);

        if (om.SpawnedObjects.Count == 0)
        {
            GUI.Label(new Rect(0, spawnedBox.height / 2f - 10, spawnedContent.width, 20), "No objects spawned in scene.", Theme.MutedCenter);
        }
        else
        {
            for (int i = 0; i < om.SpawnedObjects.Count; i++)
            {
                SpawnedObjectInstance inst = om.SpawnedObjects[i];
                bool selected = i == om.SelectedIndex;

                Rect row = new Rect(4, 4 + i * 34f, spawnedContent.width - 8, 30f);

                float h = Anim.To($"obj.spawned.h.{i}", selected ? 1f : (row.Contains(Event.current.mousePosition) ? 0.4f : 0f), 16f);
                if (h > 0.01f)
                    Theme.Fill(row, Theme.Accent.WithAlpha(0.22f * h), 6);
                if (selected)
                    Theme.Fill(new Rect(row.x, row.y + 6, 3, row.height - 12), Theme.Accent, 1);

                string displayName = inst.Data != null ? inst.Data.ObjectName : "Object";
                Vector3 p = inst.GameObject != null ? inst.GameObject.transform.position : Vector3.zero;
                string detailStr = $"({p.x:0.#}, {p.y:0.#}, {p.z:0.#})";

                Theme.DrawText(new Rect(row.x + 14, row.y + 2, 180, 20), $"{i + 1}. {displayName}", Theme.Label, selected ? Theme.Text : Theme.TextMuted);
                Theme.DrawText(new Rect(row.x + 200, row.y + 3, row.width - 210, 18), detailStr, Theme.MutedSmall, Theme.TextMuted);

                if (GUI.Button(row, GUIContent.none, GUIStyle.none))
                {
                    om.SelectObject(i);
                }
            }
        }
        GUI.EndScrollView();

        y += spawnedBox.height + 14f;
        Widgets.Divider(12, y - 6, w - 24);

        // ---- Section 3: Transform Inspector ----
        GUI.Label(new Rect(16, y, 220, 20), "TRANSFORM INSPECTOR", Theme.Header);

        SpawnedObjectInstance sel = om.SelectedObject;
        GUI.enabled = sel != null && sel.GameObject != null;

        if (GUI.Button(new Rect(w - 200, y - 2, 188, 26), "Move to Camera (L)"))
        {
            om.MoveSelectedToCamera();
        }
        y += 28f;

        Rect transformPanel = new Rect(12, y, w - 24, 160);
        Theme.Fill(transformPanel, Theme.Surface, 8);

        float px = transformPanel.x + 12;
        float py = transformPanel.y + 12;
        float innerW = transformPanel.width - 24;

        if (sel != null && sel.GameObject != null)
        {
            Transform t = sel.GameObject.transform;
            Vector3 pos = t.position;
            Vector3 rot = t.rotation.eulerAngles;
            Vector3 scale = t.localScale;

            float labelW = 75f;
            float fieldW = (innerW - labelW - 16f) / 3f;

            // Position (X, Y, Z)
            GUI.Label(new Rect(px, py, labelW, 24), "Position");
            pos.x = Widgets.FloatField("obj.px", new Rect(px + labelW, py, fieldW, 24), "X", Theme.AxisX, pos.x);
            pos.y = Widgets.FloatField("obj.py", new Rect(px + labelW + fieldW + 8, py, fieldW, 24), "Y", Theme.AxisY, pos.y);
            pos.z = Widgets.FloatField("obj.pz", new Rect(px + labelW + (fieldW + 8) * 2, py, fieldW, 24), "Z", Theme.AxisZ, pos.z);
            py += 32f;

            // Rotation (X, Y, Z)
            GUI.Label(new Rect(px, py, labelW, 24), "Rotation");
            rot.x = Widgets.FloatField("obj.rx", new Rect(px + labelW, py, fieldW, 24), "X", Theme.AxisX, rot.x);
            rot.y = Widgets.FloatField("obj.ry", new Rect(px + labelW + fieldW + 8, py, fieldW, 24), "Y", Theme.AxisY, rot.y);
            rot.z = Widgets.FloatField("obj.rz", new Rect(px + labelW + (fieldW + 8) * 2, py, fieldW, 24), "Z", Theme.AxisZ, rot.z);
            py += 32f;

            // Scale (X, Y, Z)
            GUI.Label(new Rect(px, py, labelW, 24), "Scale");
            scale.x = Widgets.FloatField("obj.sx", new Rect(px + labelW, py, fieldW, 24), "X", Theme.AxisX, scale.x);
            scale.y = Widgets.FloatField("obj.sy", new Rect(px + labelW + fieldW + 8, py, fieldW, 24), "Y", Theme.AxisY, scale.y);
            scale.z = Widgets.FloatField("obj.sz", new Rect(px + labelW + (fieldW + 8) * 2, py, fieldW, 24), "Z", Theme.AxisZ, scale.z);
            py += 32f;

            om.ApplyTransformToSelected(pos, rot, scale);
        }
        else
        {
            GUI.Label(new Rect(transformPanel.x, transformPanel.center.y - 10, transformPanel.width, 20),
                "Select a spawned object above to edit transform.", Theme.MutedCenter);
        }

        GUI.enabled = true;

        // ---- Footer Note ----
        string footerText = "Shortcut: Press L while an object is selected to snap it to your camera position.";
        GUI.Label(new Rect(16, Rect.height - 30, w - 32, 22), footerText, Theme.MutedSmall);
    }
}
