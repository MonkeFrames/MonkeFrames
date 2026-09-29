using System.Linq;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Extensions;

namespace MonkeFrames.Editor.Utilities;

public static class ExtensionUtilities
{
    // create menus for plugins, should be initialized by the time we do all this
    public static void CreateMenus()
    {
        foreach (var (kvp, index) in ExtensionManager.Menus.Select((kvp, index) => (kvp, index)))
        {
            string menuName = kvp.Key;

            // searching for menu or creating one
            IEditorMenuManager menu = UIManager.Instance.Menus.FirstOrDefault(m => m.Menu.Name == menuName);
            
            if (menu == null)
            {
                menu = new IEditorMenuManager(menuName, kvp.Value);
                return; // auto-init above ^^^
            }
                
            foreach (var action in kvp.Value)
                menu.AddMenuItem(action.Key, action.Value);
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
        }
    }
}