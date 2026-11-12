using System;
using System.IO;

namespace SpacePeek;

internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "spacepeek.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }

    public static void Exception(string where, Exception ex) => Write($"{where}: {ex.GetType().Name}: {ex.Message}");
}
