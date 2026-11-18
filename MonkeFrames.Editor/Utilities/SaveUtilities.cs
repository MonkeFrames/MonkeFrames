using MonkeFrames.Compiler;
using MonkeFrames.Compiler.Models;
using MonkeFrames.Editor.Components;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MonkeFrames.Editor.Utilities;

public static class SaveUtilities
{
    public static string ProjectDirectory => SystemUtilities.Combine(Constants.DataFolder, "projects");

    public static bool IsValidJson(string json) {
        try
        {
            JToken.Parse(json);
            return true;
        }
        catch (JsonReaderException)
        {
            return false;
        }
    }

    public static void Save()
    {
        Win32Utilities.SaveFile("Save project", KeyframeManager.Instance.Project.ToJson(), "frames", "MonkeFrames project\0*.frames", ProjectDirectory);
    }
}