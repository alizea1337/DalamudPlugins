using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Lumina.Excel.Sheets;

namespace Provenance.Integrations.Fishing;

/// <summary>What the fishing hole (or the game) said.</summary>
internal enum RodMessage
{
    /// <summary>"The fish have become wary of your presence…" (5517): move a little.</summary>
    Wary,
    /// <summary>"The fish sense something amiss…" (3516), "The fish here have grown wise to your presence…" (5518): another hole.</summary>
    Amiss,
    /// <summary>"You have no bait." and the like.</summary>
    NoBait,
    /// <summary>"You do not sense any fish here." (1126): this stand can't cast.</summary>
    NoFishHere,
    /// <summary>"Unable to fish at this time." (1109): the cast didn't go out.</summary>
    CastFailed,
    /// <summary>"Your level is too low to use that bait." (3521).</summary>
    BaitTooHigh,
    /// <summary>The lure's general success: "the next bite will land a large/small-jawed fish!" (5565, 5569).</summary>
    LureHit,
    /// <summary>The lure brought the lure fish (its own FishParameter message).</summary>
    LureFish,
}

/// <summary>
/// Watches the messages that matter while fishing. Log messages come by LogMessage id (the same in every
/// language); toasts and chat lines are compared with the LogMessage sheet's texts, read once in the
/// client's language (as GatherBuddy Reborn does for wary and amiss). A lure fish announces itself with
/// its own line from the FishParameter sheet (the text AutoHook reads as Unknown_70_1).
/// Ported from AutoHook (BSD-3, Puni.sh); wary/amiss follow GatherBuddy Reborn (Apache-2.0).
/// </summary>
internal sealed class RodMessages : IDisposable
{
    private static readonly Dictionary<uint, RodMessage> Ids = new()
    {
        [5517] = RodMessage.Wary,
        [3516] = RodMessage.Amiss,
        [5518] = RodMessage.Amiss,
        [3523] = RodMessage.NoBait,
        [10824] = RodMessage.NoBait,
        [1123] = RodMessage.NoBait,
        [5576] = RodMessage.NoBait,
        [10759] = RodMessage.NoBait,
        [1126] = RodMessage.NoFishHere,
        [1109] = RodMessage.CastFailed,
        [3521] = RodMessage.BaitTooHigh,
        [5565] = RodMessage.LureHit,
        [5569] = RodMessage.LureHit,
    };

    private Dictionary<string, RodMessage>? texts;
    private readonly HashSet<string> lureTexts = new();
    private bool attached;

    /// <summary>A message seen (framework thread).</summary>
    public event Action<RodMessage>? Seen;

    /// <summary>Starts listening (once; <see cref="Dispose"/> stops).</summary>
    public void Attach()
    {
        if (attached) return;
        attached = true;
        Services.Chat.LogMessage += OnLogMessage;
        Services.Chat.ChatMessage += OnChatMessage;
        Services.Toasts.Toast += OnToast;
        Services.Toasts.QuestToast += OnQuestToast;
        Services.Toasts.ErrorToast += OnErrorToast;
    }

    public void Dispose()
    {
        if (!attached) return;
        attached = false;
        Services.Chat.LogMessage -= OnLogMessage;
        Services.Chat.ChatMessage -= OnChatMessage;
        Services.Toasts.Toast -= OnToast;
        Services.Toasts.QuestToast -= OnQuestToast;
        Services.Toasts.ErrorToast -= OnErrorToast;
    }

    /// <summary>The lure fish whose arrival line counts as the lure's success (replaces the previous ones).</summary>
    public void WatchLureFish(IEnumerable<uint> fish)
    {
        lureTexts.Clear();
        var wanted = fish.ToHashSet();
        if (wanted.Count == 0) return;
        try
        {
            foreach (var row in Services.Data.GetExcelSheet<FishParameter>())
                if (wanted.Contains(row.Item.RowId) && Normal(row.Unknown_70_1.ExtractText()) is { Length: > 0 } text)
                    lureTexts.Add(text);
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Couldn't read the lure fish messages");
        }
    }

    /// <summary>Some lure fish has an arrival line we watch for (otherwise the lure's general success counts).</summary>
    public bool KnowsLureFish => lureTexts.Count > 0;

    private Dictionary<string, RodMessage> Texts()
    {
        if (texts != null) return texts;
        texts = new Dictionary<string, RodMessage>();
        try
        {
            var sheet = Services.Data.GetExcelSheet<LogMessage>();
            foreach (var (id, kind) in Ids)
                if (sheet.GetRowOrDefault(id) is { } row && Normal(row.Text.ExtractText()) is { Length: > 0 } text)
                    texts.TryAdd(text, kind);
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Couldn't read the fishing log messages");
        }
        return texts;
    }

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static string Normal(string text) => Spaces.Replace(text, " ").Trim();

    /// <summary>The same message often comes as a log line and as a toast: one second apart counts once.</summary>
    private readonly Dictionary<RodMessage, DateTime> lastSeen = new();

    private void Raise(RodMessage kind)
    {
        var now = DateTime.UtcNow;
        if (lastSeen.TryGetValue(kind, out var last) && now - last < TimeSpan.FromSeconds(1)) return;
        lastSeen[kind] = now;
        try
        {
            Seen?.Invoke(kind);
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Fishing message handler");
        }
    }

    private void OnLogMessage(ILogMessage message)
    {
        if (Ids.TryGetValue(message.LogMessageId, out var kind)) Raise(kind);
    }

    // Log messages come by id above; text catches the same lines shown as toasts or printed without an id
    // (the lure fish's arrival is such a line).
    private void OnChatMessage(IChatMessage message) => OnText(message.Message);

    private void OnText(SeString message)
    {
        var text = Normal(message.TextValue);
        if (text.Length < 10) return;
        if (lureTexts.Contains(text)) Raise(RodMessage.LureFish);
        else if (Texts().TryGetValue(text, out var kind)) Raise(kind);
    }

    private void OnToast(ref SeString message, ref ToastOptions options, ref bool isHandled) => OnText(message);
    private void OnQuestToast(ref SeString message, ref QuestToastOptions options, ref bool isHandled) => OnText(message);
    private void OnErrorToast(ref SeString message, ref bool isHandled) => OnText(message);
}
