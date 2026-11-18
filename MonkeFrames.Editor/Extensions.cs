using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MonkeFrames.Editor.Components;
using MonkeFrames.Editor.Utilities;
using UnityEngine;

namespace MonkeFrames.Editor;

public static class Extensions
{
    public static IEnumerable<Type> GetLoadableTypes(this Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t != null);
        }
    }
}