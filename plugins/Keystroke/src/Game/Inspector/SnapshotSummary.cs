using System.Linq;
using System.Text;

namespace Keystroke.Game.Inspector;

/// <summary>Compact, paste-friendly digest of a <see cref="GameSnapshot"/> for bug reports and verification.</summary>
internal static class SnapshotSummary
{
    private const int MaxTextNodes = 80;
    private const int MaxArrayEntries = 260;

    public static string Build(GameSnapshot s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Keystroke {s.PluginVersion} snapshot {s.CapturedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"territory {s.TerritoryId} {s.TerritoryName} \"{s.TerritoryPlaceName}\"  duty={s.InKeyboundDuty} lobby={s.InKeyboundLobby}  lang={s.ClientLanguage}");
        sb.AppendLine($"conditions: {s.Conditions}");
        sb.AppendLine($"currency: MGP {s.Currency.Mgp}  MGC {s.Currency.Mgc}/{s.Currency.MgcCap}  coffers {s.Currency.KeyboundCoffers}");
        sb.AppendLine($"queue: {s.Queue.State} pos={s.Queue.Position} popped={s.Queue.PoppedContentId} queued=[{string.Join(",", s.Queue.QueuedContentIds)}]");

        sb.AppendLine("agents: " + string.Join("  ", s.Agents.Select(a => $"{a.Name}={(a.Active ? "active/addon" + a.AddonId : "off")}")));

        sb.AppendLine("addons:");
        foreach (var a in s.Addons)
        {
            if (!a.Present) { sb.AppendLine($"  {a.Name}: not loaded"); continue; }
            sb.AppendLine($"  {a.Name}: {(a.Visible ? "visible" : "hidden")} {a.Width:0}x{a.Height:0}@{a.X:0},{a.Y:0} nodes={a.NodeCount}");

            // Visible text with content, plus component roots that are on-screen.
            var texts = a.Nodes.Where(n => n.Text is { Length: > 0 }).Take(MaxTextNodes).ToList();
            foreach (var n in texts)
                sb.AppendLine($"    [{n.Depth}] text#{n.NodeId} {(n.Visible ? "v" : "h")} @{n.X:0},{n.Y:0} \"{n.Text}\"");
            var comps = a.Nodes.Where(n => n.Depth == 0 && n.ComponentType != null && n.X > -5000).Take(40).ToList();
            foreach (var n in comps)
                sb.AppendLine($"    comp#{n.NodeId} {n.ComponentType} {(n.Visible ? "v" : "h")} @{n.X:0},{n.Y:0} {n.Width}x{n.Height}");
            if (a.Nodes.Count > 0 && texts.Count == 0 && comps.Count == 0)
                sb.AppendLine("    (no text content, no on-screen components)");
        }

        Array("numbers", s.NumberArray, sb, v => v != "0");
        Array("strings", s.StringArray, sb, v => v.Length > 0);

        if (s.Casts.Count > 0)
        {
            sb.AppendLine("casts:");
            foreach (var c in s.Casts)
                sb.AppendLine($"  {c.Name} (0x{c.EntityId:X}) action {c.ActionId} \"{c.ActionName}\" {c.CurrentTime:0.0}/{c.TotalTime:0.0}s");
        }
        else sb.AppendLine("casts: none");

        if (s.Errors.Count > 0)
        {
            sb.AppendLine("errors:");
            foreach (var e in s.Errors) sb.AppendLine("  " + e);
        }
        return sb.ToString();
    }

    private static void Array(string label, ArraySnapshot? arr, StringBuilder sb, System.Func<string, bool> keep)
    {
        if (arr == null) { sb.AppendLine($"{label}: not captured"); return; }
        if (!arr.Present) { sb.AppendLine($"{label}[{arr.Index}]: not present"); return; }
        var kept = arr.Values.Select((v, i) => (i, v)).Where(p => keep(p.v)).Take(MaxArrayEntries).ToList();
        sb.AppendLine($"{label}[{arr.Index}] size={arr.Size} nonzero={kept.Count}:");
        var line = new StringBuilder("  ");
        foreach (var (i, v) in kept)
        {
            var piece = label == "strings" ? $"{i}=\"{v}\" " : $"{i}={v} ";
            if (line.Length + piece.Length > 110) { sb.AppendLine(line.ToString()); line.Clear().Append("  "); }
            line.Append(piece);
        }
        if (line.Length > 2) sb.AppendLine(line.ToString());
    }
}
