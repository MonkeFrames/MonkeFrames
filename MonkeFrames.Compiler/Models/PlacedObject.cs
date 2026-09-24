using UnityEngine;

namespace MonkeFrames.Compiler.Models;

/// <summary>
/// An object inside of the scene.
/// </summary>
public class PlacedObject
{
    /// <summary>
    /// Name of the object.
    /// </summary>
    public string ObjectName { get; set; } = "";

    /// <summary>
    /// Position of the object.
    /// </summary>
    public Vector3 Position { get; set; }

    /// <summary>
    /// Rotation of the object.
    /// </summary>
    public Vector3 Rotation { get; set; }

    /// <summary>
    /// Scale of the object.
    /// </summary>
    public Vector3 Scale { get; set; } = Vector3.one;
}
