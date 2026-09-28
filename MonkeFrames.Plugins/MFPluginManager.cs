using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MonkeFrames.Plugins;

public static class MFPluginManager
{
    public static Dictionary<string, Dictionary<string, Action>> Menus = new();
    public static List<MFWindow> Windows = new();

    public static async Task Init()
    {
        
    }
}