using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace MonkeFrames.Extensions;

public static class ExtensionManager
{
    public static Dictionary<FramesExtension.Info, FramesExtension> Plugins = new();

    public static Dictionary<string, Dictionary<string, Action>> Menus = new();
    public static List<FramesWindow> Windows = new();

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

    public static void Load(string filePath)
    {
        if (!IsAssembly(filePath))
            return;

        Assembly assembly;

        try { assembly = Assembly.LoadFile(filePath); }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            return;
        }

        List<(FramesExtension.Info, FramesExtension)> plugins =
            FramesReflection.GetInstancesOfTypeWithAttribute<FramesExtension.Info, FramesExtension>(assembly);

        List<Task> loadingTasks = new();

        foreach ((FramesExtension.Info, FramesExtension) pluginData in plugins)
        {
            FramesExtension.Info metadata = pluginData.Item1;
            FramesExtension plugin = pluginData.Item2;

            Plugins.Add(metadata, plugin);

            Console.WriteLine($"Loaded plugin [{metadata.Name} {metadata.Version}] ({metadata.GUID})");

            loadingTasks.Add(Task.Run(plugin.OnLoad));
        }

        Task.WaitAll([.. loadingTasks]);
    }

    public static void Init()
    {
        string pluginsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MonkeFrames", "extensions");

        List<Task> loadingTasks = new();

        foreach (string pluginAssembly in Directory.EnumerateFiles(pluginsFolder, "*.*"))
            Load(pluginAssembly);

        Task.WaitAll([.. loadingTasks]);
    }
}