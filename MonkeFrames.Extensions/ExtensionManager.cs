using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;

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
    private static int _loadGeneration;

    public static void Load(string filePath)
    {
        if (!IsAssembly(filePath))
            return;

        Assembly assembly;

        try
        {
            // Load a private copy so Windows does not lock the installed extension file.
            // A unique location also ensures the runtime doesn't return an already-loaded assembly.
            string shadowDirectory = Path.Combine(Path.GetTempPath(), "MonkeFrames", "extensions", (++_loadGeneration).ToString());
            Directory.CreateDirectory(shadowDirectory);
            string shadowPath = Path.Combine(shadowDirectory, Path.GetFileName(filePath));
            File.Copy(filePath, shadowPath, true);
            assembly = Assembly.LoadFile(shadowPath);
            Assemblies.Add(assembly);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            return;
        }

        List<(FramesExtension.Info, FramesExtension)> plugins =
            FramesReflection.GetInstancesOfTypeWithAttribute<FramesExtension.Info, FramesExtension>(assembly);

        foreach ((FramesExtension.Info, FramesExtension) pluginData in plugins)
        {
            FramesExtension.Info metadata = pluginData.Item1;
            FramesExtension plugin = pluginData.Item2;

            Plugins[metadata] = plugin;

            Console.WriteLine($"Loaded plugin [{metadata.Name} {metadata.Version}] ({metadata.GUID})");

            try { plugin.OnLoad(); }
            catch (Exception ex) { Console.WriteLine($"Extension [{metadata.Name}] failed during OnLoad: {ex}"); }
        }
    }

    public static void Init()
    {
        string pluginsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MonkeFrames", "extensions");

        if (!Directory.Exists(pluginsFolder))
            Directory.CreateDirectory(pluginsFolder);

        foreach (string pluginAssembly in Directory.EnumerateFiles(pluginsFolder, "*.mfextension"))
            Load(pluginAssembly);
    }

    /// <summary>Unload the current extension instances and load every installed extension again.</summary>
    public static void Reload()
    {
        foreach (var plugin in Plugins.Values.ToArray())
        {
            try { plugin.OnUnload(); }
            catch (Exception ex) { Console.WriteLine($"Extension failed during OnUnload: {ex}"); }
        }

        Plugins.Clear();
        Menus.Clear();
        Windows.Clear();
        Assemblies.Clear();
        Init();
    }
}
