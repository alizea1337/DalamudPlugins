using System;
using Dalamud.Interface.ImGuiNotification;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Keystroke.Alerts;

public enum AlertKind { LoopFinished, MgcWarning, Error, GameResult }

/// <summary>Routes an event to the channels the user enabled: in-game toast, sound effect, chat line.</summary>
internal sealed class Notifier
{
    private readonly Func<Configuration> config;

    public Notifier(Func<Configuration> config) => this.config = config;

    public void Notify(AlertKind kind, string title, string message)
    {
        var a = config().Alerts;
        var enabled = kind switch
        {
            AlertKind.LoopFinished => a.NotifyLoopFinished,
            AlertKind.MgcWarning => a.NotifyMgcWarning,
            AlertKind.Error => a.NotifyErrors,
            AlertKind.GameResult => a.NotifyGameResult,
            _ => true,
        };
        if (!enabled) return;

        if (a.ToastEnabled)
        {
            try
            {
                Services.Notifications.AddNotification(new Notification
                {
                    Title = title,
                    Content = message,
                    Type = kind switch
                    {
                        AlertKind.Error => NotificationType.Error,
                        AlertKind.MgcWarning => NotificationType.Warning,
                        AlertKind.LoopFinished => NotificationType.Success,
                        _ => NotificationType.Info,
                    },
                    InitialDuration = TimeSpan.FromSeconds(kind == AlertKind.GameResult ? 5 : 10),
                });
            }
            catch (Exception e) { Services.Log.Warning(e, "Toast failed"); }
        }

        if (a.ChatEnabled)
        {
            try { Services.Chat.Print($"[Keystroke] {title}: {message}"); }
            catch (Exception e) { Services.Log.Warning(e, "Chat print failed"); }
        }

        if (a.SoundEnabled) PlaySound(a.SoundEffect);
    }

    /// <summary>Plays the game's chat sound effect N (as in &lt;se.N&gt;).</summary>
    public static unsafe void PlaySound(int se)
    {
        try
        {
            var id = (uint)(36 + Math.Clamp(se, 1, 16));
            UIGlobals.PlaySoundEffect(id);
        }
        catch (Exception e) { Services.Log.Warning(e, "Sound failed"); }
    }
}
