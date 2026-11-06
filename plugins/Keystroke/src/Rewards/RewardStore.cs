using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Keystroke.Rewards;

/// <summary>One finished Keybound Brawler game.</summary>
public sealed record GameRecord
{
    public DateTime At { get; init; }
    public double DurationSec { get; init; }
    public int Score { get; init; }
    public long MgpGained { get; init; }
    public long MgcGained { get; init; }
    public int Keys { get; init; }
    public int Words { get; init; }
    public int Mistakes { get; init; }
    public double Wpm { get; init; }
    public string Profile { get; init; } = "";
    public bool Instant { get; init; }
}

/// <summary>Aggregates for one calendar day.</summary>
public sealed record DayTotals(DateOnly Day, int Games, long Mgp, long Mgc);

/// <summary>
/// Append-only JSON-lines history in the plugin config folder. Loaded once at start; appends are
/// written off the framework thread.
/// </summary>
internal sealed class RewardStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string path;
    private readonly List<GameRecord> records = new();
    private readonly object gate = new();

    public IReadOnlyList<GameRecord> Records { get { lock (gate) return records.ToList(); } }
    public int TotalGames { get; private set; }
    public long TotalMgp { get; private set; }
    public long TotalMgc { get; private set; }
    public double BestWpm { get; private set; }
    public int BestScore { get; private set; }

    public RewardStore()
    {
        path = Path.Combine(Services.PluginInterface.GetPluginConfigDirectory(), "history.jsonl");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var r = JsonSerializer.Deserialize<GameRecord>(line, Json);
                    if (r != null) Add(r);
                }
                catch (Exception e) { Services.Log.Warning(e, "Skipping bad history line"); }
            }
            Services.Log.Information("Loaded {Count} games from history", records.Count);
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Could not load history");
        }
    }

    private void Add(GameRecord r)
    {
        lock (gate)
        {
            records.Add(r);
            TotalGames++;
            TotalMgp += r.MgpGained;
            TotalMgc += r.MgcGained;
            if (r.Wpm > BestWpm) BestWpm = r.Wpm;
            if (r.Score > BestScore) BestScore = r.Score;
        }
    }

    public void Append(GameRecord r)
    {
        Add(r);
        var line = JsonSerializer.Serialize(r, Json) + "\n";
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line);
            }
            catch (Exception e) { Services.Log.Error(e, "Could not append history"); }
        });
    }

    /// <summary>Per-day totals for the last <paramref name="days"/> days, oldest first, zero-filled.</summary>
    public List<DayTotals> LastDays(int days)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = today.AddDays(-(days - 1));
        Dictionary<DateOnly, (int g, long mgp, long mgc)> byDay;
        lock (gate)
        {
            byDay = records
                .Where(r => DateOnly.FromDateTime(r.At) >= start)
                .GroupBy(r => DateOnly.FromDateTime(r.At))
                .ToDictionary(g => g.Key, g => (g.Count(), g.Sum(r => r.MgpGained), g.Sum(r => r.MgcGained)));
        }
        var result = new List<DayTotals>(days);
        for (var i = 0; i < days; i++)
        {
            var d = start.AddDays(i);
            result.Add(byDay.TryGetValue(d, out var t) ? new DayTotals(d, t.g, t.mgp, t.mgc) : new DayTotals(d, 0, 0, 0));
        }
        return result;
    }
}
