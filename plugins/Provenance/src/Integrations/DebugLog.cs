using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Provenance.Integrations;

/// <summary>
/// A detailed log for bug reports: every step and decision of the runs (orders and the Cosmic loop) and the crafter,
/// with the numbers they went by. Each run gets its own file (logs/session-…), so the bug report can carry the whole
/// run that's going and the one before it, even after a crash; a file per day keeps everything besides (a week's
/// worth). No character names go in plain, like the bug report: the one exception, the player who set the Security
/// tripwire off, goes in base64-encoded ("ref …"), which hides it at a glance but decodes for debugging.
/// </summary>
internal static class DebugLog
{
    private const int Kept = 600;
    /// <summary>Most lines of a run the bug report carries (the rest stay in its file).</summary>
    public const int ReportLines = 5000;

    private static readonly object Lock = new();
    private static readonly Queue<string> Lines = new();
    private static string? folder;
    private static bool prepared;
    private static string? current;
    private static string? previous;

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
                Prepare();
                if (folder == null) return;
                File.AppendAllText(Path.Combine(folder, $"provenance-{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
                if (current != null) File.AppendAllText(current, line + Environment.NewLine);
            }
            catch
            {
                // A log that can't be written mustn't stop anything.
            }
        }
    }

    /// <summary>A run starts: from here its lines go to a file of its own ("order", "cosmic").</summary>
    public static void BeginSession(string what)
    {
        lock (Lock)
        {
            try
            {
                Prepare();
                if (folder == null) return;
                if (current != null) previous = current;
                current = Path.Combine(folder, $"session-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{what}.log");
            }
            catch
            {
                current = null;
            }
        }
        Write("session", $"{what} started, Provenance {Plugin.Version}");
    }

    /// <summary>The last lines in memory, oldest first.</summary>
    public static IReadOnlyList<string> Recent(int count)
    {
        lock (Lock) return Lines.Skip(Math.Max(0, Lines.Count - count)).ToList();
    }

    /// <summary>
    /// The run going (or the last one this session) and the one before it, each with its name and lines (the last
    /// <see cref="ReportLines"/>). After a restart the newest file on disk is the one before, so a run that ended in
    /// a crash comes along too.
    /// </summary>
    public static List<(string Name, IReadOnlyList<string> Lines, int Total)> Sessions()
    {
        var result = new List<(string, IReadOnlyList<string>, int)>();
        lock (Lock)
        {
            try
            {
                Prepare();
                foreach (var file in new[] { current, previous }.Where(f => f != null && File.Exists(f)).Distinct())
                {
                    var all = File.ReadAllLines(file!);
                    result.Add((Path.GetFileNameWithoutExtension(file!), all.Skip(Math.Max(0, all.Length - ReportLines)).ToList(), all.Length));
                }
            }
            catch
            {
                // Without the files there's still the lines in memory.
            }
        }
        return result;
    }

    /// <summary>The log folder; old files cleared out, and the newest run's file from before (a restart, a crash) remembered.</summary>
    private static void Prepare()
    {
        if (prepared) return;
        prepared = true;
        var dir = Path.Combine(Services.PluginInterface.ConfigDirectory.FullName, "logs");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "*.log"))
            if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f);
        var sessions = Directory.GetFiles(dir, "session-*.log").OrderBy(f => f).ToList();
        // Keep the last 40 runs' files.
        foreach (var f in sessions.Take(Math.Max(0, sessions.Count - 40))) File.Delete(f);
        previous = sessions.LastOrDefault(File.Exists);
        folder = dir;
    }
}
