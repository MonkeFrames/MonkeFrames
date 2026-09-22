using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MonkeFrames.Editor.Attributes;
using MonkeFrames.Editor.Interfaces;
using MonkeFrames.Editor.UI;
using UnityEngine;

namespace MonkeFrames.Editor.Classes;

public class IEditorMenuManager
{
    public const float ItemHeight = 26f;
    public const float SeparatorHeight = 9f;
    public const float DropdownPadding = 5f;
    public const float DropdownWidth = 230f;

    public IEditorMenu Menu;
    public List<EditorMenuItem> Items = [];

    /// <summary>Where this menu's button sits in the menu bar (set by UIManager each frame).</summary>
    public Rect BarRect;

    public IEditorMenuManager(IEditorMenu menu)
    {
        Menu = menu;

        List<MethodInfo> menuItemMethods = Menu.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.IsDefined(typeof(EditorMenuItem)))
            .ToList();

        foreach (MethodInfo method in menuItemMethods)
        {
            EditorMenuItem item = method.GetCustomAttribute<EditorMenuItem>();
            item.Action = () => method.Invoke(menu, []);

            Items.Add(item);
        }
    }

    public float DropdownHeight
    {
        get
        {
            float h = DropdownPadding * 2;
            for (int i = 0; i < Items.Count; i++)
                h += ItemHeight + (Items[i].Separator && i > 0 ? SeparatorHeight : 0);
            return h;
        }
    }

    /// <summary>
    /// Draw the dropdown contents inside the popup window. Items fade/slide in one after another.
    /// Returns true if an item was clicked (the menu should close).
    /// </summary>
    public bool DrawItems(float appear, bool interactive)
    {
        float y = DropdownPadding;
        bool clicked = false;
        Color baseColor = GUI.color;

        for (int i = 0; i < Items.Count; i++)
        {
            EditorMenuItem item = Items[i];

            if (item.Separator && i > 0)
            {
                Theme.Fill(new Rect(10, y + SeparatorHeight / 2f, DropdownWidth - 20, 1), Theme.Border, 0);
                y += SeparatorHeight;
            }

            // Stagger: each item starts its entrance slightly after the previous one.
            float local = Mathf.Clamp01(appear * 1.6f - i * 0.09f);
            float e = Anim.OutCubic(local);

            Rect row = new Rect(DropdownPadding + (1f - e) * -8f, y, DropdownWidth - DropdownPadding * 2, ItemHeight);
            GUI.color = new Color(1, 1, 1, baseColor.a * e);

            bool hover = interactive && row.Contains(Event.current.mousePosition);
            float h = Anim.To($"menu.{Menu.Name}.{i}", hover ? 1f : 0f, 22f);

            if (h > 0.01f)
            {
                Theme.Fill(row, Theme.Accent.WithAlpha(0.20f * h), 5);
                Theme.Fill(new Rect(row.x, row.y + 6, 3, row.height - 12), Theme.Accent.WithAlpha(h), 1);
            }

            Rect textRect = new Rect(row.x + 10 + h * 3f, row.y, row.width - 20, row.height);
            Theme.DrawText(textRect, item.Name, Theme.Label, Color.Lerp(Theme.Text, Color.white, h));

            if (!string.IsNullOrEmpty(item.Shortcut))
            {
                Rect keyRect = new Rect(row.xMax - 44, row.y + 5, 36, row.height - 10);
                Theme.Fill(keyRect, new Color(1, 1, 1, 0.06f + 0.04f * h), 4);
                Theme.DrawText(keyRect, item.Shortcut, Theme.MutedCenter, Theme.TextMuted);
            }

            if (interactive && GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                try
                {
                    item.Action();
                }
                catch (System.Exception ex)
                {
                    System.Console.WriteLine($"[MonkeFrames::Menu] \"{item.Name}\" failed: {ex.InnerException?.Message ?? ex.Message}");
                }

                clicked = true;
            }

            y += ItemHeight;
        }

        GUI.color = baseColor;
        return clicked;
    }
}
