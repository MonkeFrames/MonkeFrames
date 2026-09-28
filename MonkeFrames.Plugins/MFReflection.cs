using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MonkeFrames.Plugins;

// Vendored from cardboard.sirkingbinx.dev
public static class MFReflection
{
    /// <summary>
    /// Get all instances of classes that implement interface T.
    /// </summary>
    /// <typeparam name="T">The interface to search for.</typeparam>
    /// <returns>A list of activated instances of the interface.</returns>
    public static List<T> GetInstancesOfInterface<T>(Assembly assembly)
    {
        var types = assembly.GetTypes().Where(type => typeof(T).IsAssignableFrom(type) && !type.IsInterface);

        var typeInstances = new List<T>();

        foreach (var type in types) {
            if (Activator.CreateInstance(type) is not T typeInstance)
                continue;

            typeInstances.Add(typeInstance);
        }

        return typeInstances;
    }

    /// <summary>
    /// Get all instances of a type which has the specified attribute applied.
    /// </summary>
    /// <typeparam name="T1">The attribute to search for.</typeparam>
    /// <typeparam name="T2">The type to search for.</typeparam>
    /// <returns>A list of tuples (T1, T2) for each type and attribute.</returns>
    public static List<(T1, T2)> GetInstancesOfTypeWithAttribute<T1, T2>(Assembly assembly) where T1 : Attribute
    {
        var types = assembly.GetTypes()
            .Where(type => typeof(T2).IsAssignableFrom(type) && type.GetCustomAttributes<T1>(true).Any()
                        && !type.IsInterface);

        var typeInstances = new List<(T1, T2)>();

        foreach (var type in types)
        {
            if (Activator.CreateInstance(type) is not T2 typeInstance)
                continue;

            T1 attribute = typeInstance.GetType().GetCustomAttribute<T1>();

            typeInstances.Add((attribute, typeInstance));
        }

        return typeInstances;
    }
}