using Cardboard;
using System;

namespace MonkeFrames.Extensions;

/// <summary>Serializes editor and extension log writes, including background exports.</summary>
public static class FramesLog
{
    private static readonly object Sync = new();
    private static CardboardLog _log;

    public static void Log(string message)
    {
        lock (Sync)
        {
            _log ??= new CardboardLog("MonkeFrames");
            _log.Log(message);
        }
    }

    public static void Log(Exception exception) => Log(exception.ToString());
}
