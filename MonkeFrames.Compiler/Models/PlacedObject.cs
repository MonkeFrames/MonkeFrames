using UnityEngine;

namespace MonkeFrames.Compiler.Models;

public class PlacedObject
{
    public string ObjectName { get; set; } = "";
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; }
    public Vector3 Scale { get; set; } = Vector3.one;
}
