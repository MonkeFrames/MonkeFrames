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
        // ...and the smooth path used by every other transition when Auto smooth is on
        AutoPath auto = project.AutoSmooth ? AutoPath.Create(keys, project.AutoSmoothStrength) : null;
        // ...and each property's own keyframes / timing (Advanced Keyframe Editor); null for normal projects
        LaneSet lanes = LaneSet.Create(project);

        for (int i = 0; i < n; i++)
        {
            Keyframe keyframe = keys[i];
            int frames = Timeline.FrameCount(keyframe.Transition.Duration, project.FPS);

            int nextIdx = (i + 1 == n ? i : i + 1);
            Keyframe next = keys[nextIdx];
            TransitionEffect effect = keyframe.Transition.Effect;

            for (int j = 0; j < frames; j++)
            {
                Vector3 newPosition;
                Quaternion newRotation;
                float newFOV;

                if (auto != null && effect != TransitionEffect.Smooth && effect != TransitionEffect.Cut && nextIdx != i)
                {
                    // Auto smooth: along the smooth path, as far as this transition's own timing says
                    float p = Easing.Evaluate(keyframe.Transition, Transitions.Progress(j, frames));
                    newPosition = auto.Position(i, p);
                    newRotation = auto.Rotation(i, p);
                    newFOV = auto.Fov(i, p);
                }
                else if (effect == TransitionEffect.Smooth && nextIdx != i)
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

                // time of day: follows the same speed curve as the camera, the short way round midnight
                float newTime = keyframe.TimeOfDay;
                if (keyframe.TimeOfDay >= 0f && next.TimeOfDay >= 0f && nextIdx != i && effect != TransitionEffect.Cut)
                {
                    float f;
                    if (effect == TransitionEffect.Smooth)
                    {
                        f = (float)j / frames;
                        if (keyframe.Transition.CustomSpeed)
                            f = Easing.Bezier(keyframe.Transition.CurveX1, keyframe.Transition.CurveY1,
                                keyframe.Transition.CurveX2, keyframe.Transition.CurveY2, f);
                        f = f * f * (3f - 2f * f);
                    }
                    else f = Transitions.Interpolate(keyframe.Transition, 0f, 1f, j, frames);
                    float delta = Mathf.Repeat(next.TimeOfDay - keyframe.TimeOfDay + 12f, 24f) - 12f;
                    newTime = Mathf.Repeat(keyframe.TimeOfDay + delta * f, 24f);
                }

                lanes?.Apply(i, j, frames, ref newPosition, ref newRotation, ref newFOV, ref newTime);

                Keyframe compiledKeyframe = new Keyframe {
                    Position = newPosition,
                    Rotation = newRotation.eulerAngles,
                    FieldOfView = newFOV,
                    MotionBlur = keyframe.MotionBlur,
                    MotionBlurStrength = keyframe.MotionBlurStrength,
                    TimeOfDay = newTime,
                    Compiled = true
                };

                compiledKeyframes.Add(compiledKeyframe);
            }
        }

        Wiggler.Apply(project.Wiggle, compiledKeyframes, project.FPS);

        project.CompiledKeyframes = compiledKeyframes;
        project.IsCompiled = true;
    }

    /// <summary>
    /// Auto smooth's path: a smooth curve through every keyframe (no corners), shaped like a Catmull-Rom spline but
    /// with each tangent scaled to the gap it spans, so a short hop next to a long one doesn't loop or overshoot.
    /// A transition's own timing (effect or custom speed graph) says how far along its stretch of the curve the
    /// camera is; that's measured along the curve's length, so bends don't make it speed up or slow down.
    /// Rotation and zoom follow the same kind of curve. The path starts / ends straight at the first and last
    /// keyframes and at cuts.
    /// </summary>
    internal class AutoPath
    {
        private const int Samples = 32;
        private Vector3[] _p, _dir;
        private bool[] _flows;
        private float _tension;
        private Vector4[] _q, _qTan;
        private float[] _fov, _fovTan;
        private float[][] _arc;          // per stretch: length along the curve at Samples+1 even steps of u

        public static AutoPath Create(List<Keyframe> keys, float strength)
        {
            int n = keys.Count;
            var a = new AutoPath
            {
                _p = new Vector3[n], _dir = new Vector3[n], _flows = new bool[n],
                _q = new Vector4[n], _qTan = new Vector4[n], _fov = new float[n], _fovTan = new float[n],
                _arc = new float[n][],
                _tension = 0.35f + 0.65f * Mathf.Clamp01(strength),
            };
            for (int k = 0; k < n; k++)
            {
                a._p[k] = keys[k].Position;
                a._fov[k] = keys[k].FieldOfView;
                Vector4 q = Transitions.ToVector4(keys[k].QuatRotation);
                if (k > 0 && Vector4.Dot(q, a._q[k - 1]) < 0f) q = -q;
                a._q[k] = q;
            }
            for (int k = 0; k < n; k++)
            {
                // the camera flows through this keyframe (the path bends through it) unless a cut, the start or
                // the end breaks it
                bool flow = k > 0 && k < n - 1
                    && keys[k - 1].Transition.Effect != TransitionEffect.Cut && keys[k - 1].Transition.Duration > 0f
                    && keys[k].Transition.Effect != TransitionEffect.Cut && keys[k].Transition.Duration > 0f;
                a._flows[k] = flow;
                if (flow)
                {
                    Vector3 d = a._p[k + 1] - a._p[k - 1];
                    a._dir[k] = d.sqrMagnitude > 1e-10f ? d.normalized : Vector3.zero;
                    a._qTan[k] = (a._q[k + 1] - a._q[k - 1]) * 0.5f * a._tension;
                    a._fovTan[k] = (a._fov[k + 1] - a._fov[k - 1]) * 0.5f * a._tension;
                }
            }
            for (int k = 0; k + 1 < n; k++)
            {
                float[] arc = new float[Samples + 1];
                Vector3 prev = a.Curve(k, 0f);
                for (int s = 1; s <= Samples; s++)
                {
                    Vector3 cur = a.Curve(k, (float)s / Samples);
                    arc[s] = arc[s - 1] + Vector3.Distance(prev, cur);
                    prev = cur;
                }
                a._arc[k] = arc;
            }
            return a;
        }

        // the direction the path leaves / arrives at a keyframe on the stretch k -> k+1
        private Vector3 Out(int k)
        {
            Vector3 chord = _p[k + 1] - _p[k];
            Vector3 d = _flows[k] ? _dir[k] : (chord.sqrMagnitude > 1e-10f ? chord.normalized : Vector3.zero);
            return d * chord.magnitude * _tension;
        }
        private Vector3 In(int k)
        {
            Vector3 chord = _p[k + 1] - _p[k];
            Vector3 d = _flows[k + 1] ? _dir[k + 1] : (chord.sqrMagnitude > 1e-10f ? chord.normalized : Vector3.zero);
            return d * chord.magnitude * _tension;
        }

        private Vector3 Curve(int k, float u) =>
            Easing.H00(u) * _p[k] + Easing.H10(u) * Out(k) + Easing.H01(u) * _p[k + 1] + Easing.H11(u) * In(k);

        /// <summary>Where the camera is on the stretch after keyframe k, progress p (0..1, may overshoot).</summary>
        public Vector3 Position(int k, float p)
        {
            float[] arc = _arc[k];
            float total = arc[Samples];
            if (total < 1e-5f) return Vector3.LerpUnclamped(_p[k], _p[k + 1], p);
            // a speed graph that overshoots carries on straight past the ends
            if (p < 0f) { Vector3 t0 = Out(k); return _p[k] + (t0.sqrMagnitude > 1e-10f ? t0.normalized : Vector3.zero) * (p * total); }
            if (p > 1f) { Vector3 t1 = In(k); return _p[k + 1] + (t1.sqrMagnitude > 1e-10f ? t1.normalized : Vector3.zero) * ((p - 1f) * total); }
            float target = p * total;
            int lo = 0, hi = Samples;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (arc[mid] < target) lo = mid; else hi = mid; }
            float seg = arc[hi] - arc[lo];
            float u = (lo + (seg > 1e-7f ? (target - arc[lo]) / seg : 0f)) / Samples;
            return Curve(k, u);
        }

        public Quaternion Rotation(int k, float p)
        {
            float s = Mathf.Clamp(p, -0.5f, 1.5f);
            return Transitions.ToQuaternion(Easing.H00(s) * _q[k] + Easing.H10(s) * _qTan[k] + Easing.H01(s) * _q[k + 1] + Easing.H11(s) * _qTan[k + 1]);
        }

        public float Fov(int k, float p)
        {
            float s = Mathf.Clamp(p, -0.5f, 1.5f);
            return Easing.H00(s) * _fov[k] + Easing.H10(s) * _fovTan[k] + Easing.H01(s) * _fov[k + 1] + Easing.H11(s) * _fovTan[k + 1];
        }
    }

    /// <summary>
    /// Per-keyframe values and tangents (units per second) for the smooth-keyframes spline.
    /// Tangents use a time-aware Catmull-Rom estimate, so speed stays continuous through a
    /// keyframe even when the transitions on either side have different durations.
    /// </summary>
    internal class SplineData
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