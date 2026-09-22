using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using MonkeFrames.Compiler.Models;
using UnityEngine;

using Keyframe = MonkeFrames.Compiler.Models.Keyframe;
using System.Threading.Tasks;

namespace MonkeFrames.Compiler;

/// <summary>
/// Manages all MonkeFrames.Compiler-related actions.
/// </summary>
public static class Compiler
{
    /// <summary>
    /// Asynchronously build a project.
    /// </summary>
    /// <param name="project">The project to build.</param>
    public static async Task Build(Project project)
    {
        List<Keyframe> compiledKeyframes = [];
        List<Keyframe> keys = project.Keyframes;
        int n = keys.Count;

        // Precompute spline data used by "Smooth" transitions.
        SplineData spline = SplineData.Create(keys, project.Smoothness);

        for (int i = 0; i < n; i++)
        {
            Keyframe keyframe = keys[i];
            int frames = (int)Math.Ceiling(keyframe.Transition.Duration * project.FPS);

            int nextIdx = (i + 1 == n ? i : i + 1);
            Keyframe next = keys[nextIdx];
            TransitionEffect effect = keyframe.Transition.Effect;

            for (int j = 0; j < frames; j++)
            {
                Vector3 newPosition;
                Quaternion newRotation;
                float newFOV;

                if (effect == TransitionEffect.Smooth && nextIdx != i)
                {
                    float s = (float)j / frames;
                    if (keyframe.Transition.CustomSpeed)
                        s = Easing.Bezier(keyframe.Transition.CurveX1, keyframe.Transition.CurveY1,
                            keyframe.Transition.CurveX2, keyframe.Transition.CurveY2, s);
                    float d = keyframe.Transition.Duration;

                    newPosition = Transitions.Hermite(spline.Positions[i], spline.PositionTangents[i],
                        spline.Positions[nextIdx], spline.PositionTangents[nextIdx], d, s);

                    newRotation = Transitions.ToQuaternion(Transitions.Hermite(spline.Rotations[i], spline.RotationTangents[i],
                        spline.Rotations[nextIdx], spline.RotationTangents[nextIdx], d, s));

                    newFOV = Transitions.Hermite(new Vector4(keyframe.FieldOfView, 0, 0, 0), new Vector4(spline.FovTangents[i], 0, 0, 0),
                        new Vector4(next.FieldOfView, 0, 0, 0), new Vector4(spline.FovTangents[nextIdx], 0, 0, 0), d, s).x;
                }
                else
                {
                    Transition tr = keyframe.Transition;
                    newPosition = Transitions.Interpolate(tr, keyframe.Position, next.Position, j, frames);
                    newRotation = Transitions.Interpolate(tr, keyframe.QuatRotation, next.QuatRotation, j, frames);
                    newFOV = Transitions.Interpolate(tr, keyframe.FieldOfView, next.FieldOfView, j, frames);
                }

                Keyframe compiledKeyframe = new Keyframe {
                    Position = newPosition,
                    Rotation = newRotation.eulerAngles,
                    FieldOfView = newFOV,
                    Compiled = true
                };

                compiledKeyframes.Add(compiledKeyframe);
            }
        }

        project.CompiledKeyframes = compiledKeyframes;
        project.IsCompiled = true;
    }

    /// <summary>
    /// Per-keyframe values and tangents (units per second) for the smooth-keyframes spline.
    /// Tangents use a time-aware Catmull-Rom estimate, so speed stays continuous through a
    /// keyframe even when the transitions on either side have different durations.
    /// </summary>
    private class SplineData
    {
        public Vector4[] Positions, PositionTangents, Rotations, RotationTangents;
        public float[] FovTangents;

        public static SplineData Create(List<Keyframe> keys, float smoothness)
        {
            int n = keys.Count;
            SplineData data = new SplineData
            {
                Positions = new Vector4[n],
                PositionTangents = new Vector4[n],
                Rotations = new Vector4[n],
                RotationTangents = new Vector4[n],
                FovTangents = new float[n],
            };

            float[] fov = new float[n];

            for (int k = 0; k < n; k++)
            {
                data.Positions[k] = keys[k].Position;
                fov[k] = keys[k].FieldOfView;

                // Keep consecutive quaternions in the same hemisphere so the spline takes the short way round.
                Vector4 q = Transitions.ToVector4(keys[k].QuatRotation);
                if (k > 0 && Vector4.Dot(q, data.Rotations[k - 1]) < 0f)
                    q = -q;
                data.Rotations[k] = q;
            }

            for (int k = 0; k < n; k++)
            {
                // First and last keyframes ease in/out from rest. Cuts on either side also stop the flow.
                if (k == 0 || k == n - 1)
                    continue;

                float dPrev = keys[k - 1].Transition.Duration;
                float dNext = keys[k].Transition.Duration;

                bool flowIn = dPrev > 0 && keys[k - 1].Transition.Effect != TransitionEffect.Cut;
                bool flowOut = dNext > 0 && keys[k].Transition.Effect != TransitionEffect.Cut;

                if (!flowIn || !flowOut)
                    continue;

                float span = dPrev + dNext;
                data.PositionTangents[k] = (data.Positions[k + 1] - data.Positions[k - 1]) / span * smoothness;
                data.RotationTangents[k] = (data.Rotations[k + 1] - data.Rotations[k - 1]) / span * smoothness;
                data.FovTangents[k] = (fov[k + 1] - fov[k - 1]) / span * smoothness;
            }

            return data;
        }
    }

    /// <summary>
    /// Convert the project into savable JSON data.
    /// </summary>
    public static string ConvertToJson(Project project)
    {
        var settings = new JsonSerializerSettings {
            Converters = new[] { new Vector3Converter() }
        };

        string json = JsonConvert.SerializeObject(project, settings);

        return json;
    }

    /// <summary>
    /// Loads a project from JSON data.
    /// </summary>
    /// <param name="json">The JSON to turn into a project.</param>
    /// <returns>The project embedded in the JSON.</returns>
    public static Project ConvertFromJson(string json)
    {
        var settings = new JsonSerializerSettings {
            Converters = new[] { new Vector3Converter() }
        };

        Project project = JsonConvert.DeserializeObject<Project>(json, settings);

        return project;
    }

    /// <summary>
    /// Convert a project name into a properly formatted file name.
    /// </summary>
    /// <param name="projectName">The project name to convert.</param>
    /// <returns>A string formatted in UpperCamelCase and ends with the .frames extension.</returns>
    public static string ProjectNameToFilename(string projectName)
    {
        TextInfo textInfo = CultureInfo.InvariantCulture.TextInfo;
        return textInfo.ToTitleCase(projectName.ToLower()).Replace(" ", "") + ".frames";
    }
}