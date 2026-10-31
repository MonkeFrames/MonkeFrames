using System.IO;

namespace MonkeFrames.Editor.Utilities;

internal static class ExportPaths
{
    public static string Next(string directory, string projectName)
    {
        string name = projectName ?? "new project";
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = name.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(name)) name = "new project";
        string path = Path.Combine(directory, name + ".mp4");
        for (int suffix = 1; File.Exists(path) || Directory.Exists(path); suffix++)
            path = Path.Combine(directory, $"{name} {suffix}.mp4");
        return path;
    }
}
