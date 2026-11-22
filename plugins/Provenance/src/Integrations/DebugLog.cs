using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Provenance.Integrations;

/// <summary>
/// A detailed log for bug reports: every step and decision of the Cosmic loop and the crafter, with the numbers they
/// went by. Kept in memory (the last lines) and in a file per day next to the settings (logs/, a week's worth). No
/// character names go in, like the bug report.
/// </summary>
internal static class DebugLog
{
    private const int Kept = 600;
    private static readonly object Lock = new();
    private static readonly Queue<string> Lines = new();
    private static string? folder;

    /// <summary>Notes a line: "23:21:35.120 [crafter] …".</summary>
    public static void Write(string area, string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{area}] {text}";
        lock (Lock)
        {
            Lines.Enqueue(line);
            while (Lines.Count > Kept) Lines.Dequeue();
            try
            {
                folder ??= Prepare();
                if (folder != null) File.AppendAllText(Path.Combine(folder, $"provenance-{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
            }
            catch
            {
                // A log that can't be written mustn't stop anything.
            }
        }
    }

    /// <summary>The last lines, oldest first.</summary>
    public static IReadOnlyList<string> Recent(int count)
    {
        lock (Lock) return Lines.Skip(Math.Max(0, Lines.Count - count)).ToList();
    }

    /// <summary>The log folder, with files older than a week cleared out.</summary>
    private static string? Prepare()
    {
        var dir = Path.Combine(Services.PluginInterface.ConfigDirectory.FullName, "logs");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "provenance-*.log"))
            if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f);
        return dir;
    }
}
