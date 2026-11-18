using System.IO;

namespace MonkeFrames.Editor.Utilities;

/// <summary>Picks export file names that never overwrite an earlier export.</summary>
internal static class ExportPaths
{
    /// <summary>"name.mp4", or "name 1.mp4", "name 2.mp4"... if that already exists in <paramref name="directory"/>.</summary>
    public static string Next(string directory, string projectName)
    {
        // Windows drops trailing dots and spaces from file names, so strip them up front.
        string name = HEncodeUtilities.SafeFileName(projectName).TrimEnd('.', ' ');
        if (name.Length == 0) name = "export";

        string path = Path.Combine(directory, name + ".mp4");
        for (int i = 1; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(directory, $"{name} {i}.mp4");
        return path;
    }
}
