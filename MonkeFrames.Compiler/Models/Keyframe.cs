using Newtonsoft.Json;
using System;
using UnityEngine;

namespace MonkeFrames.Compiler.Models;

/// <summary>
/// Represents many properties of a point on the animation, such as positional, rotational, and transitional data.
/// </summary>
public struct Keyframe
{
    /// <summary>
    /// The position of the keyframe.
    /// </summary>
    public Vector3 Position = new Vector3(0, 0, 0);

    /// <summary>
    /// The rotation of the keyframe.
    /// </summary>
    public Vector3 Rotation = new Vector3(0, 0, 0);

    /// <summary>
    /// The field of view (FOV) of the keyframe.
    /// </summary>
    public float FieldOfView = 60;

    /// <summary>
    /// Transition data for the keyframe.
    /// </summary>
    public Transition Transition = Transition.Linear;

    /// <summary>
    /// Apply camera motion blur while moving from this keyframe to the next.
    /// </summary>
    public bool MotionBlur = false;

    /// <summary>
    /// Motion blur strength (0 = subtle, 1 = heavy). Only used when <see cref="MotionBlur"/> is on.
    /// </summary>
    public float MotionBlurStrength = 0.5f;
    
    /// <summary>
    /// The keyframe's GUID, used for equating keyframes to each other efficiently.
    /// </summary>
    [JsonIgnore]
    public string GUID = Guid.NewGuid().ToString();

    /// <summary>
    /// `true` if the project has been compiled yet; otherwise `false`.
    /// </summary>
    [JsonIgnore]
    public bool Compiled = false;

    /// <summary>
    /// The keyframe's rotation converted to a Quaternion.
    /// </summary>
    [JsonIgnore]
    public readonly Quaternion QuatRotation => Quaternion.Euler(Rotation);

    /// <summary>
    /// Determine if two keyframes are equal.
    /// </summary>
    /// <param name="left">Keyframe 1</param>
    /// <param name="right">Keyframe 2</param>
    /// <returns>`true` if keyframes are the same; otherwise `false`.</returns>
    public static bool operator ==(Keyframe? left, Keyframe? right)
    {
        if (left is null) return right is null;
        return left.Value.GUID == right.Value.GUID;
    }

    /// <summary>
    /// Determine if two keyframes are not equal.
    /// </summary>
    /// <param name="left">Keyframe 1</param>
    /// <param name="right">Keyframe 2</param>
    /// <returns>`true` if keyframes are not the same; otherwise `false`.</returns>
    public static bool operator !=(Keyframe? left, Keyframe? right) => !(left == right);

    /// <summary>
    /// Determine if two keyframes are equal.
    /// </summary>
    /// <param name="obj">Other keyframe to compare.</param>
    /// <returns>`true` if keyframes are the same; otherwise `false`.</returns>
    public override bool Equals(object obj)
    {
        if (obj is not Keyframe other)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        return this.GUID == other.GUID;
    }
    
    /// <summary>
    /// Returns the hash code for this instance.
    /// </summary>
    /// <returns>A 32-bit signed integer that is the hash code of this instance.</returns>
    public override readonly int GetHashCode()
    {
        return base.GetHashCode();
    }

    /// <summary>
    /// Create a new Keyframe.
    /// </summary>
    public Keyframe() { }
}
