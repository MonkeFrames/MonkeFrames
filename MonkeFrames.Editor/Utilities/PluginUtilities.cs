using System.Linq;
using MonkeFrames.Editor.Classes;
using MonkeFrames.Editor.Components;
using MonkeFrames.Plugins;

namespace MonkeFrames.Editor.Utilities;

public static class PluginUtilities
{
    // create menus for plugins, should be initialized by the time we do all this
    public static void CreateMenus()
    {
        foreach (var (kvp, index) in MFPluginManager.Menus.Select((kvp, index) => (kvp, index)))
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
        
    }
}