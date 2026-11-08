using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Keystroke.Game.Inspector;
using Keystroke.UI.Theme;

namespace Keystroke.UI;

/// <summary>
/// Live view of the Keybound Brawler game state. Sampling happens on the framework thread at a
/// fixed rate; drawing only reads the latest immutable snapshot.
/// </summary>
internal sealed class InspectorWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly string[] Tabs = { "overview", "addons", "arrays", "casts", "raw json" };

    private GameSnapshot? snapshot;
    private DateTime lastSample = DateTime.MinValue;
    private int tab;
    private bool paused;
    private bool includeNodes = true;
    private bool includeArrays = true;
    private bool onlyVisibleNodes;
    private string nodeFilter = "";
    private string feedback = "";
    private DateTime feedbackAt;

    public int SampleIntervalMs { get; set; } = 100;

    public InspectorWindow(Configuration config) : base("Keystroke Inspector###KeystrokeInspector")
    {
        Size = new Vector2(780, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(540, 380) };
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoTitleBar;
        RespectCloseHotkey = true;
    }

    public void Tick()
    {
        if (!IsOpen || paused) return;
        var now = DateTime.UtcNow;
        if ((now - lastSample).TotalMilliseconds < SampleIntervalMs) return;
        lastSample = now;
        try { snapshot = GameInspector.Capture(includeNodes, includeArrays); }
        catch (Exception e) { Services.Log.Error(e, "Inspector capture failed"); }
    }

    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Theme.Frame);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
    }

    public override void Draw()
    {
        using var theme = Theme.Theme.Push();
        using var body = Widgets.F.Body.Push();
        var s = Widgets.S;
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var dl = ImGui.GetWindowDrawList();
        Theme.Theme.DrawFrame(dl, pos, pos + size, s);
        var inset = Theme.Theme.FrameInset * s;
        Theme.Theme.DrawStripe(dl, pos + new Vector2(inset, inset), size.X - inset * 2, Theme.Theme.StripeHeight * s);

        var x0 = inset + 12 * s;
        ImGui.SetCursorPos(new Vector2(x0, inset + Theme.Theme.StripeHeight * s + 8 * s));
        using (Widgets.F.Title.Push()) Widgets.Text("inspector", Theme.Theme.TextBright);
        ImGui.SameLine(0, 14 * s);
        if (Widgets.Button(paused ? "resume##insp" : "pause##insp", Widgets.ButtonKind.Normal, null, paused ? Theme.Icon.Play : Theme.Icon.Pause)) paused = !paused;
        ImGui.SameLine(0, 4 * s);
        if (Widgets.Button("copy summary##insp", Widgets.ButtonKind.Accent, null, Theme.Icon.Copy, "Compact text digest that fits in a chat message.")) CopySummary();
        ImGui.SameLine(0, 4 * s);
        if (Widgets.Button("copy json##insp", Widgets.ButtonKind.Normal, null, Theme.Icon.Copy)) CopySnapshot();
        ImGui.SameLine(0, 4 * s);
        if (Widgets.Button("save json##insp", Widgets.ButtonKind.Normal, null, Theme.Icon.FolderOpen, "Writes the full snapshot to the snapshots folder.")) SaveSnapshot();
        ImGui.SameLine(0, 4 * s);
        if (Widgets.Button("now##insp", Widgets.ButtonKind.Normal, null, Theme.Icon.RefreshCw)) snapshot = GameInspector.Capture(includeNodes, includeArrays);
        if (feedback.Length > 0 && (DateTime.UtcNow - feedbackAt).TotalSeconds < 3)
        {
            ImGui.SameLine(0, 8 * s);
            Widgets.Small(feedback, Theme.Theme.AccentText);
        }
        ImGui.SetCursorPos(new Vector2(size.X - inset - 26 * s, inset + Theme.Theme.StripeHeight * s + 8 * s));
        if (Widgets.Button("##close", Widgets.ButtonKind.Normal, new Vector2(18 * s), Theme.Icon.X, "close")) IsOpen = false;

        ImGui.SetCursorPos(new Vector2(x0, ImGui.GetCursorPosY() + 6 * s));
        var innerW = size.X - x0 * 2;
        Widgets.Segmented("##insptabs", Tabs, ref tab, innerW);
        ImGui.SetCursorPosX(x0);
        var ms = SampleIntervalMs;
        if (Widgets.Slider("sample rate##rate", ref ms, 50, 1000, "{0} ms", null, 160 * s)) SampleIntervalMs = ms;

        var snap = snapshot;
        ImGui.SetCursorPosX(x0);
        if (ImGui.BeginChild("##inspbody", new Vector2(innerW, size.Y - ImGui.GetCursorPosY() - inset - 8 * s)))
        {
            if (snap == null) Widgets.Small("waiting for first sample…");
            else
            {
                switch (tab)
                {
                    case 0: DrawOverview(snap); break;
                    case 1: DrawAddons(snap); break;
                    case 2: DrawArrays(snap); break;
                    case 3: DrawCasts(snap); break;
                    case 4: DrawJson(snap); break;
                }
            }
        }
        ImGui.EndChild();
    }

    private void CopySnapshot()
    {
        var snap = snapshot ?? GameInspector.Capture(true, true);
        var json = JsonSerializer.Serialize(snap, JsonOptions);
        ImGui.SetClipboardText(json);
        Feedback($"copied {json.Length / 1024} kb of json");
    }

    private void CopySummary()
    {
        var snap = snapshot ?? GameInspector.Capture(true, true);
        var text = SnapshotSummary.Build(snap);
        ImGui.SetClipboardText(text);
        Feedback($"copied summary ({text.Length / 1024} kb)");
    }

    private void SaveSnapshot()
    {
        try
        {
            var snap = snapshot ?? GameInspector.Capture(true, true);
            var dir = System.IO.Path.Combine(Services.PluginInterface.GetPluginConfigDirectory(), "snapshots");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"snapshot-{snap.CapturedAt:yyyyMMdd-HHmmss}.json");
            System.IO.File.WriteAllText(path, JsonSerializer.Serialize(snap, JsonOptions));
            Feedback($"saved {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Saving snapshot failed");
            Feedback("save failed: " + e.Message);
        }
    }

    private void Feedback(string text)
    {
        feedback = text;
        feedbackAt = DateTime.UtcNow;
    }

    private static void DrawOverview(GameSnapshot snap)
    {
        Widgets.BeginColumns(2);
        Widgets.BeginGroup("location");
        Widgets.ValueRow("logged in", snap.IsLoggedIn ? "yes" : "no", snap.IsLoggedIn ? Theme.Theme.AccentText : Theme.Theme.Danger);
        Widgets.ValueRow("territory", $"{snap.TerritoryId} {snap.TerritoryName} {snap.TerritoryPlaceName}".Trim());
        var area = snap.InKeyboundDuty ? "keybound duty" : snap.InKeyboundLobby ? "keybound square" : "elsewhere";
        Widgets.ValueRow("area", area, snap.InKeyboundDuty || snap.InKeyboundLobby ? Theme.Theme.AccentText : null);
        Widgets.ValueRow("client language", snap.ClientLanguage);
        Widgets.ValueRow("target", snap.Target == null ? "—" : $"{snap.Target.Name} #{snap.Target.DataId}");
        Widgets.EndGroup();
        Widgets.BeginGroup("conditions");
        Widgets.Wrapped(snap.Conditions.Length > 0 ? snap.Conditions : "—");
        Widgets.EndGroup();

        Widgets.Column(1);
        Widgets.BeginGroup("currency");
        Widgets.ValueRow("mgp", snap.Currency.Mgp.ToString("N0"));
        Widgets.ValueRow("mgc", $"{snap.Currency.Mgc:N0} / {snap.Currency.MgcCap:N0}");
        Widgets.ValueRow("keybound coffers", snap.Currency.KeyboundCoffers.ToString());
        Widgets.EndGroup();
        Widgets.BeginGroup("duty finder");
        Widgets.ValueRow("queue state", snap.Queue.State);
        Widgets.ValueRow("queued", snap.Queue.QueuedContentIds.Count > 0 ? string.Join(", ", snap.Queue.QueuedContentIds) : "—");
        Widgets.ValueRow("popped", snap.Queue.PoppedContentId != 0 ? snap.Queue.PoppedContentId.ToString() : "—");
        Widgets.ValueRow("position", snap.Queue.Position.ToString());
        Widgets.EndGroup();
        Widgets.BeginGroup("agents");
        foreach (var a in snap.Agents)
            Widgets.ValueRow($"{a.Name} ({a.Id})", a.Active ? $"active · addon {a.AddonId}" : "inactive", a.Active ? Theme.Theme.AccentText : null);
        Widgets.EndGroup();
        Widgets.EndColumns();

        foreach (var e in snap.Errors) Widgets.Notice(Theme.Icon.TriangleAlert, Theme.Theme.Warn, e);
    }

    private void DrawAddons(GameSnapshot snap)
    {
        var s = Widgets.S;
        Widgets.Check("capture nodes", ref includeNodes);
        Widgets.Check("visible only", ref onlyVisibleNodes);
        ImGui.SetNextItemWidth(220 * s);
        ImGui.InputTextWithHint("##nodefilter", "filter text / id / type", ref nodeFilter, 64);
        ImGui.Dummy(new Vector2(0, 6 * s));

        foreach (var addon in snap.Addons)
        {
            var color = !addon.Present ? Theme.Theme.TextFaint : addon.Visible ? Theme.Theme.AccentText : Theme.Theme.Warn;
            var header = addon.Present
                ? $"{addon.Name}   {(addon.Visible ? "visible" : "hidden")}   {addon.Width:0}×{addon.Height:0} @ {addon.X:0},{addon.Y:0}   {addon.NodeCount} nodes   {addon.Address}"
                : $"{addon.Name}   not loaded";
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            var open = addon.Present && ImGui.TreeNodeEx(header + "##" + addon.Name, ImGuiTreeNodeFlags.SpanAvailWidth);
            ImGui.PopStyleColor();
            if (!addon.Present) { Widgets.Text(header, color); continue; }
            if (!open) continue;

            if (ImGui.BeginTable("nodes" + addon.Name, 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerH))
            {
                ImGui.TableSetupColumn("id", ImGuiTableColumnFlags.WidthFixed, 56 * s);
                ImGui.TableSetupColumn("type", ImGuiTableColumnFlags.WidthFixed, 130 * s);
                ImGui.TableSetupColumn("vis", ImGuiTableColumnFlags.WidthFixed, 32 * s);
                ImGui.TableSetupColumn("pos", ImGuiTableColumnFlags.WidthFixed, 90 * s);
                ImGui.TableSetupColumn("size", ImGuiTableColumnFlags.WidthFixed, 80 * s);
                ImGui.TableSetupColumn("component", ImGuiTableColumnFlags.WidthFixed, 110 * s);
                ImGui.TableSetupColumn("text", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();
                foreach (var n in addon.Nodes)
                {
                    if (onlyVisibleNodes && !n.Visible) continue;
                    if (nodeFilter.Length > 0 &&
                        !(n.NodeId.ToString().Contains(nodeFilter) ||
                          n.Type.Contains(nodeFilter, StringComparison.OrdinalIgnoreCase) ||
                          (n.ComponentType?.Contains(nodeFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                          (n.Text?.Contains(nodeFilter, StringComparison.OrdinalIgnoreCase) ?? false)))
                        continue;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(new string(' ', n.Depth * 2) + n.NodeId);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(n.Type);
                    ImGui.TableNextColumn(); ImGui.TextColored(n.Visible ? Theme.Theme.AccentText : Theme.Theme.TextFaint, n.Visible ? "●" : "○");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{n.X:0},{n.Y:0}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{n.Width}×{n.Height}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(n.ComponentType ?? "");
                    ImGui.TableNextColumn(); if (n.Text != null) ImGui.TextColored(Theme.Theme.Warn, n.Text);
                }
                ImGui.EndTable();
            }
            ImGui.TreePop();
        }
    }

    private void DrawArrays(GameSnapshot snap)
    {
        Widgets.Check("capture arrays", ref includeArrays);
        ImGui.Dummy(new Vector2(0, 6 * Widgets.S));
        DrawArray("number array", snap.NumberArray, 13);
        ImGui.Dummy(new Vector2(0, 10 * Widgets.S));
        DrawArray("string array", snap.StringArray, 3);
    }

    private static void DrawArray(string title, ArraySnapshot? arr, int stride)
    {
        if (arr == null) { Widgets.Small($"{title}: capture disabled"); return; }
        using (Widgets.F.Bold.Push()) Widgets.Text($"{title} [{arr.Index}]", Theme.Theme.TextBright);
        if (!arr.Present) { Widgets.Small("not present"); return; }
        Widgets.Small($"size {arr.Size}, showing {arr.Values.Count}; {stride} per row");
        var s = Widgets.S;
        if (ImGui.BeginTable("arr" + arr.Index, stride + 1, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollX))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 44 * s);
            for (var c = 0; c < stride; c++) ImGui.TableSetupColumn("+" + c, ImGuiTableColumnFlags.WidthFixed, stride == 3 ? 180 * s : 64 * s);
            ImGui.TableHeadersRow();
            for (var i = 0; i < arr.Values.Count; i += stride)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.Theme.TextDim, i.ToString());
                for (var c = 0; c < stride; c++)
                {
                    ImGui.TableNextColumn();
                    if (i + c < arr.Values.Count)
                    {
                        var v = arr.Values[i + c];
                        ImGui.TextColored(v is "0" or "" ? Theme.Theme.TextFaint : Theme.Theme.Text, v);
                    }
                }
            }
            ImGui.EndTable();
        }
    }

    private static void DrawCasts(GameSnapshot snap)
    {
        if (snap.Casts.Count == 0) { Widgets.Small("nothing is casting"); return; }
        var s = Widgets.S;
        if (ImGui.BeginTable("casts", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("entity", ImGuiTableColumnFlags.WidthFixed, 90 * s);
            ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthFixed, 180 * s);
            ImGui.TableSetupColumn("action id", ImGuiTableColumnFlags.WidthFixed, 80 * s);
            ImGui.TableSetupColumn("action", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("progress", ImGuiTableColumnFlags.WidthFixed, 110 * s);
            ImGui.TableHeadersRow();
            foreach (var c in snap.Casts)
            {
                var blocking = c.ActionId == Game.Keybound.CursedGazeActionId || Game.Keybound.SensoryJammerActionIds.Contains(c.ActionId) || Game.Keybound.IsBlockingCastName(c.ActionName);
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"0x{c.EntityId:X}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(c.Name);
                ImGui.TableNextColumn(); ImGui.TextColored(blocking ? Theme.Theme.Danger : Theme.Theme.Text, c.ActionId.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(c.ActionName + (blocking ? "  (input-blocking)" : ""));
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{c.CurrentTime:0.0} / {c.TotalTime:0.0}s");
            }
            ImGui.EndTable();
        }
    }

    private static void DrawJson(GameSnapshot snap)
    {
        var json = JsonSerializer.Serialize(snap, JsonOptions);
        using var mono = Widgets.F.Mono.Push();
        if (ImGui.BeginChild("##jsonview", ImGui.GetContentRegionAvail(), false, ImGuiWindowFlags.HorizontalScrollbar))
            ImGui.TextUnformatted(json);
        ImGui.EndChild();
    }
}
