using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Provenance.Core.Crafting;

namespace Provenance.Integrations;

/// <summary>
/// Runs raphael-cli (raphael-rs by KonaeAkira, Apache-2.0) to get a crafting rotation. The executable
/// ships next to Provenance; for local builds without it, the copy GatherBuddy Reborn ships is used.
/// Solutions are cached per recipe and stats for the session.
/// </summary>
internal sealed class Raphael
{
    private const string ExeName = "raphael-cli.exe";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, Task<List<uint>>> cache = new();

    /// <summary>Where raphael-cli was found, or null.</summary>
    public static string? ExecutablePath()
    {
        var own = Path.Combine(Services.PluginInterface.AssemblyLocation.DirectoryName!, ExeName);
        if (File.Exists(own)) return own;
        try
        {
            var gbr = PluginLookup.Instance(GatherBuddyIpc.InternalName);
            var dir = gbr == null ? null : Path.GetDirectoryName(gbr.GetType().Assembly.Location);
            var theirs = dir == null ? null : Path.Combine(dir, ExeName);
            if (theirs != null && File.Exists(theirs)) return theirs;
        }
        catch (Exception e)
        {
            Services.Log.Debug("Looking for GatherBuddy Reborn's raphael-cli failed: {Message}", e.Message);
        }
        return null;
    }

    public Task<List<uint>> Solve(RaphaelRequest request)
    {
        var task = cache.GetOrAdd(request.CacheKey, _ => Task.Run(() => Run(request)));
        // Don't keep failures; a later try may work (for example after Raphael is installed).
        if (task.IsFaulted || task.IsCanceled) cache.TryRemove(request.CacheKey, out _);
        return task;
    }

    private static async Task<List<uint>> Run(RaphaelRequest request)
    {
        var exe = ExecutablePath() ?? throw new FileNotFoundException("raphael-cli.exe wasn't found next to Provenance or GatherBuddy Reborn.");
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
        var ids = RaphaelCli.ParseActionIds(output);
        if (ids.Count == 0)
        {
            var error = (await stderr.ConfigureAwait(false)).Trim();
            throw new InvalidOperationException("Raphael found no rotation" + (error.Length > 0 ? ": " + error.Split('\n')[^1].Trim() : "."));
        }
        Services.Log.Information("Raphael solved recipe {Recipe} in {Steps} steps", request.RecipeId, ids.Count);
        return ids;
    }
}
