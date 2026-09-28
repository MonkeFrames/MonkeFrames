using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace MonkeFrames.Plugins;

public static class MFPluginManager
{
    public static Dictionary<MFPlugin.Info, MFPlugin> Plugins = new();

    public static Dictionary<string, Dictionary<string, Action>> Menus = new();
    public static List<MFWindow> Windows = new();

    private static bool IsAssembly(string assemblyPath)
    {
        try
        {
            AssemblyName.GetAssemblyName(assemblyPath);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public static List<Assembly> Assemblies = new();

    public static void Init()
    {
        string pluginsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MonkeFrames");
        List<Task> loadingTasks = new();

        foreach (string pluginAssembly in Directory.EnumerateFiles(pluginsFolder))
        {
            if (!IsAssembly(pluginAssembly))
                continue;
            
            Assembly assembly;

            try { assembly = Assembly.LoadFile(pluginAssembly); }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                continue;
            }

            List<(MFPlugin.Info, MFPlugin)> plugins =
                MFReflection.GetInstancesOfTypeWithAttribute<MFPlugin.Info, MFPlugin>(assembly);
            
            foreach ((MFPlugin.Info, MFPlugin) pluginData in plugins)
            {
                MFPlugin.Info metadata = pluginData.Item1;
                MFPlugin plugin = pluginData.Item2;

                Plugins.Add(metadata, plugin);

                Console.WriteLine($"Loaded plugin [{metadata.Name} {metadata.Version}] ({metadata.GUID})");

                loadingTasks.Add(Task.Run(plugin.OnLoad));
            }
        }

        Task.WaitAll([.. loadingTasks]);
    }
}