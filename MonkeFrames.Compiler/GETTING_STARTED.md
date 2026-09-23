# Getting Started with MonkeFrames.Compiler
By viewing this file, I'm sure that you have heard somewhere that "developers can add MonkeFrames to their own mods" and you want to do that. This quick start guide will explain how to add keyframing into your own camera mods without much hassle.

## Add the Compiler
### Method 1: Simple Reference
The easiest method to include the compiler is [add the DLL file to your references](https://learn.microsoft.com/en-us/visualstudio/ide/how-to-add-or-remove-references-by-using-the-reference-manager?view=visualstudio#add-a-reference) and then copy the compiler DLL in the same folder as your plugin, simular to this:
```
Assume your plugin has a folder named MyCoolCameraMod that contains it's files. Include the compiler like this:

BepInEx/plugins/MyCoolCameraMod/MyCoolCameraMod.dll
BepInEx/plugins/MyCoolCameraMod/MonkeFrames.Compiler.dll
```

### Method 2: Extract & Load
You can include the compiler as a resource in your `csproj` and then load it during initialization. This is a janky solution but leads to one single DLL, so would be more preferred if you want an all-in-one mod.

Add the DLL as an embedded resource, reference a copy of it in your source code, and then add this code snippet for loading the compiler:
```cs
using System.Reflection;

/// <summary>
/// The path to the MonkeFrames.Compiler library in your embedded resources.
/// </summary>
public const string CompilerPath = "MyNamespaceName.Resources.MonkeFrames Compiler.dll";

/// <summary>
/// Loads the MonkeFrames compiler into the app domain from the Assembly's resources, and returns `true` if successful.
/// </summary>
public static bool LoadCompiler()
{
    Assembly assembly = Assembly.GetExecutingAssembly();
    using (Stream compilerFs = a.GetManifestResourceStream(CompilerPath))
    {
        if (compilerFs == null) return false;

        try
        {
            byte[] compilerBytes = new byte[compilerFs.Length];
            compilerFs.Read(compilerBytes, 0, compilerBytes.Length);
    
            AppDomain.CurrentDomain.Load(compilerBytes);
            return true;
        } catch (FileLoadException ex)
        {
            Debug.LogError("The compiler is already loaded. Please remove this invocation from your code.");
            Debug.LogError(ex.Message);
            Debug.LogError(ex.StackTrace);
            return true;
        } catch (Exception ex)
        {
            Debug.LogError(ex.Message);
            Debug.LogError(ex.StackTrace);
            return false;
        }
    }

    return false;
}
```

## Proper Integration
MonkeFrames.Compiler is designed so projects made with other camera mods can be loaded into your mod with no conflicts. We hope you uphold this promise in your camera mod, and not restrict projects from other cameras from loading.

When creating a project, we add a stamp showing where it originated from with the `Exporter` model.
```cs
using MonkeFrames.Compiler.Models;

// As an example, here is the exporter for MonkeFrames itself:
public static readonly MyExporter = new Exporter("bingus.monkeframes", "MonkeFrames");
```

## Projects
Sets of keyframes, both compiled and original, are stored in projects. Projects are savable and loadable, so work can be restored at any time.

To create a project, use this small snippet:
```cs
// See above for our exporter snippet.
Project project = new Project("project name", MyExporter);

// FPS accepts the following values: [30, 60, 120]
project.FPS = 120;

// Compile the keyframes so you can play them:
await project.Build();

// Getting/setting JSON
string projectJson = project.ToJson();
project = Project.FromJson(projectJson); // nothing will change here since it is the same project
```

## Keyframes
Keyframes are what you added the compiler for. Create one like this:
```cs
using UnityEngine; // Vector3

Keyframe k = new Keyframe();

k.Position = new Vector3(0, 10, 0); // basic position
k.Rotation = new Vector3(0, 25, 0); // basic rotation (stored as euler angles for simple editing)
k.FieldOfView = 40; // field of view
k.Transition = Transition.Linear; // Transition data

Quaternion realRotation = k.QuatRotation; // Get the real rotation to use on a transform

project.Add(k); // add the keyframe to the project (List<Keyframe>)
```

## Build & Playback
Keyframes are processed beforehand to create smooth playback. Build the project asyncronously:
```cs
using System.Collections.Generic; // List<T>

project.Build();
List<Keyframe> compiledKeyframes = project.CompiledKeyframes;
```

Once keyframes are built, the playback job is up to you to implement, since it is different per-person. Move the camera `project.FPS` times per second to the next keyframe in `project.CompiledKeyframes`. You can do this with Unity coroutines.

MonkeFrames' solution for playback is viewable [here](https://github.com/sirkingbinx/MonkeFrames/blob/master/MonkeFrames.Editor/Components/CameraManager.cs#L181).
