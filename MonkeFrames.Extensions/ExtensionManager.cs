using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using System.Text.RegularExpressions;

namespace MonkeFrames.Extensions;

public static class ExtensionManager
{
    public static Dictionary<FramesExtension.Info, FramesExtension> Plugins = new();

    public static Dictionary<string, Dictionary<string, Action>> Menus = new();
    public static List<FramesWindow> Windows = new();

    public static Dictionary<string, Harmony> HarmonyInstances = new();

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

    public static T CallMethod<T>(string methodName, T returnIfNone = default, params object[] args)
    {
        if (Plugins.Count == 0)
            return returnIfNone;

        foreach (FramesExtension extension in Plugins.Values)
        {
            MethodInfo method = extension.GetType().GetMethod(methodName);
            if (method == null)
                continue;

            object result = null;

            try
            {
                result = method.Invoke(extension, [.. args]);
            } catch { } // i don't care buddy

            if (result is T expected)
                return expected;
        }

        return returnIfNone;
    }

    public static void CallMethod(string methodName, params object[] args)
    {
        if (Plugins.Count == 0)
            return;

        foreach (FramesExtension extension in Plugins.Values)
        {
            MethodInfo method = extension.GetType().GetMethod(methodName);
            if (method == null)
                continue;

            try
            {
                method.Invoke(extension, [.. args]);
            } catch { } // i don't care buddy
        }
    }

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

    private static readonly Regex HarmonyMethodFormat = new Regex(
        @"^([a-zA-Z_][a-zA-Z0-9_\.]*):([a-zA-Z_][a-zA-Z0-9_]*)$", 
        RegexOptions.Compiled
    );

    public static bool IsValidFormat(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        return HarmonyMethodFormat.IsMatch(input);
    }

    internal static void ManagerApplyPatch(Type callingType, string fqdn, Delegate patchMethod, FramesExtension.PatchType patchType)
    {
        if (!IsValidFormat(fqdn))
            throw new ArgumentException("Method name has bad format.", nameof(fqdn));

        if (callingType.GetCustomAttribute<FramesExtension.Info>() is not FramesExtension.Info info)
            throw new ArgumentException("Missing FramesExtension.Info");

        if (!HarmonyInstances.ContainsKey(info.GUID))
            HarmonyInstances.Add(info.GUID, new Harmony(info.GUID));
        
        Harmony harmony = HarmonyInstances[info.GUID];
        MethodInfo method = AccessTools.Method(fqdn);
        HarmonyMethod patch = new(patchMethod.GetMethodInfo());
        
        if (patchType == FramesExtension.PatchType.Prefix)
            harmony.Patch(method, prefix: patch);
        if (patchType == FramesExtension.PatchType.Postfix)
            harmony.Patch(method, postfix: patch);
    }
}
