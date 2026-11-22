using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Provenance.Core.Crafting;

namespace Provenance.Integrations;

/// <summary>
/// Runs raphael-cli (raphael-rs by KonaeAkira, Apache-2.0) to get a crafting rotation. The executable
/// ships next to Provenance; for local builds without it, the copy GatherBuddy Reborn ships is used.
/// Solutions are cached per recipe and stats, and the ones found are kept on disk (next to the settings), so a later
/// session has them at once until the stats change. A new raphael-cli starts the kept ones afresh.
/// </summary>
internal sealed class Raphael
{
    private const string ExeName = "raphael-cli.exe";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, Task<RaphaelSolution>> cache = new();
    /// <summary>Estimates and the Cosmic plan ask for many recipes at once; three solver processes at a time.</summary>
    private static readonly SemaphoreSlim Gate = new(3);

    /// <summary>Kept rotations at most (the least recently used go first).</summary>
    private const int KeptMost = 6000;

    private sealed class Kept
    {
        public uint[] Actions { get; set; } = Array.Empty<uint>();
        public int Final { get; set; }
        public int Max { get; set; }
        public DateTime Used { get; set; }
    }

    private readonly object keptLock = new();
    private Dictionary<string, Kept>? kept;
    private string keptVersion = "";
    private bool saveScheduled;

    private static string KeptFile => Path.Combine(Services.PluginInterface.ConfigDirectory.FullName, "raphael-rotations.json");

    /// <summary>Reads the kept rotations once (they're only good for the raphael-cli they came from).</summary>
    private Dictionary<string, Kept> KeptRotations()
    {
        lock (keptLock)
        {
            if (kept != null) return kept;
            kept = new Dictionary<string, Kept>();
            try
            {
                var exe = ExecutablePath();
                keptVersion = exe == null ? "" : $"{new FileInfo(exe).Length}-{File.GetLastWriteTimeUtc(exe).Ticks}";
                if (File.Exists(KeptFile))
                {
                    var file = System.Text.Json.JsonSerializer.Deserialize<KeptFileData>(File.ReadAllText(KeptFile));
                    if (file != null && file.Version == keptVersion && file.Rotations != null) kept = file.Rotations;
                }
            }
            catch (Exception e)
            {
                Services.Log.Warning("The kept rotations couldn't be read; starting afresh: {Message}", e.Message);
            }
            return kept;
        }
    }

    private sealed class KeptFileData
    {
        public string Version { get; set; } = "";
        public Dictionary<string, Kept>? Rotations { get; set; }
    }

    private void Keep(string key, RaphaelSolution solution)
    {
        lock (keptLock)
        {
            KeptRotations()[key] = new Kept { Actions = solution.Actions.ToArray(), Final = solution.FinalQuality, Max = solution.MaxQuality, Used = DateTime.UtcNow };
            if (saveScheduled) return;
            saveScheduled = true;
        }
        // A few seconds later, so a burst of solves is written once.
        Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => SaveKept());
    }

    private void SaveKept()
    {
        try
        {
            string json;
            lock (keptLock)
            {
                saveScheduled = false;
                var rotations = kept ?? new Dictionary<string, Kept>();
                if (rotations.Count > KeptMost)
                    foreach (var old in rotations.OrderBy(r => r.Value.Used).Take(rotations.Count - KeptMost).Select(r => r.Key).ToList())
                        rotations.Remove(old);
                json = System.Text.Json.JsonSerializer.Serialize(new KeptFileData { Version = keptVersion, Rotations = rotations });
            }
            var temp = KeptFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, KeptFile, true);
        }
        catch (Exception e)
        {
            Services.Log.Warning("The rotations couldn't be kept: {Message}", e.Message);
        }
    }

    /// <summary>A kept rotation for the request, or null.</summary>
    private RaphaelSolution? FromKept(string key)
    {
        lock (keptLock)
        {
            if (!KeptRotations().TryGetValue(key, out var k)) return null;
            k.Used = DateTime.UtcNow;
            return new RaphaelSolution(k.Actions, k.Final, k.Max);
        }
    }

    /// <summary>Where raphael-cli was found, or null.</summary>
    public static string? ExecutablePath()
    {
        var own = Path.Combine(Services.PluginInterface.AssemblyLocation.DirectoryName!, ExeName);
        if (File.Exists(own)) return own;
        try
        {
            // Dalamud keeps installed plugins in XIVLauncher\installedPlugins\<name>\<version>\
            // (plugin assemblies are loaded from memory, so their own Location is empty).
            var launcher = new DirectoryInfo(Services.PluginInterface.ConfigDirectory.FullName).Parent?.Parent;
            var gbrDir = launcher == null ? null : new DirectoryInfo(Path.Combine(launcher.FullName, "installedPlugins", "GatherBuddyReborn"));
            if (gbrDir is { Exists: true })
            {
                var loaded = Services.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName == "GatherBuddyReborn")?.Version?.ToString();
                var candidates = gbrDir.GetDirectories()
                    .OrderByDescending(d => d.Name == loaded)
                    .ThenByDescending(d => d.LastWriteTimeUtc)
                    .Select(d => Path.Combine(d.FullName, ExeName));
                var theirs = candidates.FirstOrDefault(File.Exists);
                if (theirs != null) return theirs;
            }
        }
        catch (Exception e)
        {
            Services.Log.Debug("Looking for GatherBuddy Reborn's raphael-cli failed: {Message}", e.Message);
        }
        return null;
    }

    /// <summary>
    /// Cached per request for the session (failures too, so a broken recipe isn't retried every second); rotations
    /// found are kept on disk for later sessions.
    /// </summary>
    public Task<RaphaelSolution> Solve(RaphaelRequest request)
        => cache.GetOrAdd(request.CacheKey, key => FromKept(key) is { } known
            ? Task.FromResult(known)
            : Task.Run(async () =>
            {
                var solution = await Run(request).ConfigureAwait(false);
                Keep(key, solution);
                return solution;
            }));

    /// <summary>The finished answer for a request, without starting one.</summary>
    public RaphaelSolution? Known(RaphaelRequest request)
        => cache.TryGetValue(request.CacheKey, out var t) ? t.IsCompletedSuccessfully ? t.Result : null : FromKept(request.CacheKey);

    /// <summary>Drops a cached failure so the next solve tries again (the crafter does this before a real craft).</summary>
    public void ForgetFailure(RaphaelRequest request)
    {
        if (cache.TryGetValue(request.CacheKey, out var t) && (t.IsFaulted || t.IsCanceled)) cache.TryRemove(request.CacheKey, out _);
    }

    private static async Task<RaphaelSolution> Run(RaphaelRequest request)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // raphael-cli sometimes fails with an internal error for some option sets: try again with
            // fewer options (no specialist actions, then no Manipulation) before giving up.
            try { return await RunOne(request).ConfigureAwait(false); }
            catch (RaphaelInternalError) when (request.Specialist)
            {
                request = request with { Specialist = false };
            }
            try { return await RunOne(request).ConfigureAwait(false); }
            catch (RaphaelInternalError) when (request.Manipulation)
            {
                return await RunOne(request with { Manipulation = false }).ConfigureAwait(false);
            }
        }
        finally { Gate.Release(); }
    }

    /// <summary>raphael-cli failed for a reason other than "no solution" (it crashed or rejected the options).</summary>
    public sealed class RaphaelInternalError(string message) : Exception(message);

    private static async Task<RaphaelSolution> RunOne(RaphaelRequest request)
    {
        var exe = ExecutablePath() ?? throw new FileNotFoundException("raphael-cli.exe wasn't found next to Provenance.");
        var args = RaphaelCli.Arguments(request);
        Services.Log.Debug("Raphael: {Args}", args);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        using var timeout = new CancellationTokenSource(Timeout);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* already gone */ }
            throw new TimeoutException("Raphael took longer than two minutes for this recipe.");
        }
        var output = await stdout.ConfigureAwait(false);
        var solution = RaphaelCli.ParseSolution(output);
        if (solution == null)
        {
            var error = (await stderr.ConfigureAwait(false)).Trim();
            // The useful line is "Failed to solve: <reason>"; the last line is only Rust's backtrace hint.
            var lines = error.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var reason = lines.FirstOrDefault(l => l.Contains("Failed to solve", StringComparison.OrdinalIgnoreCase))
                         ?? lines.FirstOrDefault(l => !l.StartsWith("note:", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("thread ", StringComparison.OrdinalIgnoreCase))
                         ?? "";
            Services.Log.Warning("Raphael failed for {Args}: {Error}", args, error);
            if (reason.Contains("NoSolution", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Raphael found no rotation.");
            throw new RaphaelInternalError("Raphael couldn't plan this craft" + (reason.Length > 0 ? " (" + reason + ")" : "") + ".");
        }
        Services.Log.Information("Raphael solved recipe {Recipe} in {Steps} steps, quality {Quality} of {Max}", request.RecipeId, solution.Actions.Count, solution.FinalQuality, solution.MaxQuality);
        return solution;
    }
}
