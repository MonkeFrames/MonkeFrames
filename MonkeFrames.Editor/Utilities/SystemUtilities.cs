using System;
using System.Runtime.InteropServices;

namespace MonkeFrames.Editor.Utilities;

public static class SystemUtilities
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    public static bool IsLinux()
    {
        IntPtr ntdllModule = GetModuleHandle("ntdll.dll");
        if (ntdllModule == IntPtr.Zero) return false;
        
        // created by Wine, won't exist if running on Windows
        IntPtr wineVersionProc = GetProcAddress(ntdllModule, "wine_get_version");
        return wineVersionProc != IntPtr.Zero;
    }

    public static string Combine(params string[] path)
    {
        char pathSeperator = '\\';
        string final = path[0];

        foreach (string p in path[1..])
            final += pathSeperator + p;

        return final;
    }

    public static string GetFolder(Environment.SpecialFolder folder)
    {
        return Environment.GetFolderPath(folder);
    }
}