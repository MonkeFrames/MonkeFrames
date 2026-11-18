using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace MonkeFrames.Editor.Utilities;

/// <summary>
/// Finding ffmpeg and choosing how to encode the MP4 export.
/// </summary>
public static class HEncodeUtilities
{
    private static string _encoder, _encoderFor;

    /// <summary>
    /// The real ffmpeg.exe: next to the mod, in its "ffmpeg" folder, or anywhere on the PATH. A file that isn't
    /// the program (e.g. the 134-byte Git LFS pointer that a GitHub "Download ZIP" gives you instead of the real
    /// 100 MB file) is skipped. null + a reason when there's none.
    /// </summary>
    public static string FindFfmpeg(out string problem)
    {
        problem = null;
        var tried = new List<string>
        {
            Path.Combine(Constants.MonkeFramesAssemblyFolder ?? "", "ffmpeg.exe"),
            Path.Combine(Constants.MonkeFramesAssemblyFolder ?? "", "ffmpeg", "ffmpeg.exe"),
        };
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { if (dir.Trim().Length > 0) tried.Add(Path.Combine(dir.Trim(), "ffmpeg.exe")); } catch { }
        }

        bool placeholder = false;
        foreach (string p in tried)
        {
            try
            {
                if (!File.Exists(p)) continue;
                if (IsProgram(p)) return p;
                placeholder = true;
            }
            catch { }
        }

        problem = placeholder
            ? "ffmpeg.exe in the MonkeFrames folder is only a small placeholder file, not the real program. Put the real ffmpeg.exe (about 100 MB) in BepInEx\\plugins\\MonkeFrames."
            : "ffmpeg.exe wasn't found. Put it in BepInEx\\plugins\\MonkeFrames.";
        return null;
    }

    /// <summary>A Windows program (starts with "MZ") and big enough to be ffmpeg.</summary>
    private static bool IsProgram(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Length < 1_000_000) return false;
        using FileStream s = fi.OpenRead();
        return s.ReadByte() == 'M' && s.ReadByte() == 'Z';
    }

    /// <summary>
    /// The best H.264 encoder that really works here: the graphics card's own (NVIDIA NVENC, AMD AMF, Intel Quick
    /// Sync), each tried once with a tiny test encode; libx264 (CPU) when none of them does.
    /// </summary>
    public static string GetGoodEncoder(string ffmpeg)
    {
        if (_encoder != null && _encoderFor == ffmpeg) return _encoder;
        var candidates = new List<string>();
        string gpu = (SystemInfo.graphicsDeviceName ?? "").ToLowerInvariant();
        if (gpu.Contains("nvidia") || gpu.Contains("geforce") || gpu.Contains("rtx") || gpu.Contains("gtx")) candidates.Add("h264_nvenc");
        if (gpu.Contains("radeon") || gpu.Contains("amd")) candidates.Add("h264_amf");
        if (gpu.Contains("intel")) candidates.Add("h264_qsv");

        _encoder = "libx264";
        foreach (string c in candidates)
            if (Works(ffmpeg, c)) { _encoder = c; break; }
        _encoderFor = ffmpeg;
        return _encoder;
    }

    private static bool Works(string ffmpeg, string encoder)
    {
        try
        {
            using Process p = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-hide_banner -loglevel error -f lavfi -i color=black:s=256x144:r=30:d=0.2 -c:v {encoder} -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            });
            if (p == null) return false;
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>High quality settings for each encoder (the export is made offline, so quality over speed).</summary>
    public static string QualityArgs(string encoder) => encoder switch
    {
        "h264_nvenc" => "-preset p5 -tune hq -rc vbr -cq 19 -b:v 0",
        "h264_amf" => "-quality quality -rc cqp -qp_i 18 -qp_p 20",
        "h264_qsv" => "-preset slow -global_quality 20",
        _ => "-preset faster -crf 18",
    };

    /// <summary>A project name that's safe as a file name.</summary>
    public static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "export";
        string s = string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Trim();
        return s.Length > 0 ? s : "export";
    }
}
