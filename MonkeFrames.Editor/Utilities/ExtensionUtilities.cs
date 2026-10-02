using System.Collections.Generic;
using System.Linq;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Extensions;

namespace MonkeFrames.Editor.Utilities;

public static class ExtensionUtilities
{
    private static readonly List<(IEditorMenuManager Menu, MonkeFrames.Editor.Attributes.EditorMenuItem Item)> OwnedMenuItems = new();
    private static readonly List<IEditorMenuManager> ExtensionMenus = new();
    private static readonly List<IEditorWindowManager> ExtensionWindows = new();

    public static void ClearCreatedItems()
    {
        foreach (var owned in OwnedMenuItems)
            owned.Menu.Items.Remove(owned.Item);
        OwnedMenuItems.Clear();

        foreach (IEditorMenuManager menu in ExtensionMenus)
            UIManager.Instance.Menus.Remove(menu);
        ExtensionMenus.Clear();

        foreach (IEditorWindowManager window in ExtensionWindows)
            UIManager.Instance.Windows.Remove(window);
        ExtensionWindows.Clear();
    }

    // create menus for plugins, should be initialized by the time we do all this
    public static void CreateMenus()
    {
        foreach (var kvp in ExtensionManager.Menus)
        {
            string menuName = kvp.Key;

            // searching for menu or creating one
            IEditorMenuManager menu = UIManager.Instance.Menus.FirstOrDefault(m => m.Name == menuName);
            
            if (menu == null)
            {
                menu = new IEditorMenuManager(menuName, new Dictionary<string, System.Action>());
                UIManager.Instance.Menus.Add(menu);
                ExtensionMenus.Add(menu);
            }

            foreach (var action in kvp.Value)
            {
                var item = menu.AddExtensionMenuItem(action.Key, action.Value);
                OwnedMenuItems.Add((menu, item));
            }
        }
    }

    public static void CreateWindows()
    {
        foreach (FramesWindow framesWindow in ExtensionManager.Windows)
        {
            if (UIManager.Instance.Windows.Any(n => n.Name == framesWindow.Name)) {
                FramesExtension.ShowMessageBox(
                    "[MonkeFrames] Extension Error!",
                    $"Error in an extension:\n\nWindow already has name \"{framesWindow.Name}\"",
                    icon: FramesExtension.MessageBoxIcon.Error,
                    buttons: FramesExtension.MessageBoxButtons.OK);

                continue;
            }

            var winMgr = new IEditorWindowManager(framesWindow.Name, framesWindow.Size,
                framesWindow.OnOpen, framesWindow.OnClose, framesWindow.OnDraw);

            UIManager.Instance.Windows.Add(winMgr);
            ExtensionWindows.Add(winMgr);
        }
    }
}
