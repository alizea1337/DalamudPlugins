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

    private readonly Configuration config;
    private GameSnapshot? snapshot;
    private DateTime lastSample = DateTime.MinValue;
    private int tab;
    private bool paused;
    private bool includeNodes = true;
    private bool includeArrays = true;
    private bool onlyVisibleNodes;
    private string nodeFilter = "";
    private string copiedFeedback = "";
    private DateTime copiedAt;

    public int SampleIntervalMs { get; set; } = 100;

    public InspectorWindow(Configuration config) : base("Keystroke Inspector###KeystrokeInspector")
    {
        this.config = config;
        Size = new Vector2(760, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 360) };
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        RespectCloseHotkey = true;
    }

    /// <summary>Called from the framework update; cheap when the window is closed or paused.</summary>
    public void Tick()
    {
        if (!IsOpen || paused) return;
        var now = DateTime.UtcNow;
        if ((now - lastSample).TotalMilliseconds < SampleIntervalMs) return;
        lastSample = now;
        try
        {
            snapshot = GameInspector.Capture(includeNodes, includeArrays);
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Inspector capture failed");
        }
    }

    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
    }

    public override void Draw()
    {
        using var theme = Theme.Theme.Push();
        using var body = Widgets.F.Body.Push();
        var s = Widgets.Scale;

        ImGui.SetCursorPos(new Vector2(16 * s, 12 * s));
        ImGui.BeginGroup();

        // Toolbar
        if (Widgets.Button(paused ? "Resume##insp" : "Pause##insp", Widgets.ButtonStyle.Default, null, paused ? Icon.Play : Icon.Pause)) paused = !paused;
        ImGui.SameLine();
        if (Widgets.Button("Copy snapshot##insp", Widgets.ButtonStyle.Primary, null, Icon.Copy)) CopySnapshot();
        ImGui.SameLine();
        if (Widgets.Button("Snapshot now##insp", Widgets.ButtonStyle.Default, null, Icon.RefreshCw))
        {
            snapshot = GameInspector.Capture(includeNodes, includeArrays);
        }
        if (copiedFeedback.Length > 0 && (DateTime.UtcNow - copiedAt).TotalSeconds < 3)
        {
            ImGui.SameLine();
            Widgets.IconText(Icon.CircleCheck, copiedFeedback, Theme.Theme.Green, Theme.Theme.SecondaryLabel);
        }

        ImGui.SameLine(ImGui.GetWindowWidth() - 240 * s);
        var ms = SampleIntervalMs;
        ImGui.SetNextItemWidth(120 * s);
        if (ImGui.SliderInt("##rate", ref ms, 50, 1000, "%d ms")) SampleIntervalMs = ms;
        ImGui.SameLine();
        Widgets.Caption("sample rate");

        ImGui.Dummy(new Vector2(0, 6 * s));
        var tabs = new[] { "Overview", "Addons", "Arrays", "Casts", "Raw JSON" };
        Widgets.Segmented("##insptabs", tabs, ref tab, ImGui.GetWindowWidth() - 32 * s);
        ImGui.Dummy(new Vector2(0, 8 * s));
        ImGui.EndGroup();

        var snap = snapshot;
        ImGui.SetCursorPosX(16 * s);
        if (ImGui.BeginChild("##inspbody", new Vector2(ImGui.GetWindowWidth() - 32 * s, ImGui.GetContentRegionAvail().Y - 12 * s)))
        {
            if (snap == null)
            {
                Widgets.Secondary("Waiting for first sample…");
            }
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
        copiedFeedback = $"Copied {json.Length / 1024} KB to clipboard";
        copiedAt = DateTime.UtcNow;
    }

    private static void DrawOverview(GameSnapshot snap)
    {
        Widgets.SectionLabel("LOCATION");
        if (Widgets.BeginCard("ov1"))
        {
            Widgets.ValueRow("Logged in", snap.IsLoggedIn ? "Yes" : "No", snap.IsLoggedIn ? Theme.Theme.Green : Theme.Theme.Red);
            Widgets.RowSeparator();
            Widgets.ValueRow("Territory", $"{snap.TerritoryId}  {snap.TerritoryName}  {snap.TerritoryPlaceName}".Trim());
            Widgets.RowSeparator();
            var area = snap.InKeyboundDuty ? "Keybound Brawler duty" : snap.InKeyboundLobby ? "Keybound Brawler lobby" : "Elsewhere";
            Widgets.ValueRow("Keybound area", area, snap.InKeyboundDuty || snap.InKeyboundLobby ? Theme.Theme.Green : Theme.Theme.SecondaryLabel);
            Widgets.RowSeparator();
            Widgets.ValueRow("Client language", snap.ClientLanguage);
            Widgets.RowSeparator();
            Widgets.ValueRow("Conditions", snap.Conditions.Length > 0 ? snap.Conditions : "—");
            Widgets.EndCard();
        }

        Widgets.SectionLabel("CURRENCY");
        if (Widgets.BeginCard("ov2"))
        {
            Widgets.ValueRow("MGP", snap.Currency.Mgp.ToString("N0"));
            Widgets.RowSeparator();
            Widgets.ValueRow("MGC", $"{snap.Currency.Mgc:N0} / {snap.Currency.MgcCap:N0}");
            Widgets.RowSeparator();
            Widgets.ValueRow("Keybound Coffers", snap.Currency.KeyboundCoffers.ToString());
            Widgets.EndCard();
        }

        Widgets.SectionLabel("DUTY FINDER");
        if (Widgets.BeginCard("ov3"))
        {
            Widgets.ValueRow("Queue state", snap.Queue.State);
            Widgets.RowSeparator();
            Widgets.ValueRow("Queued content", snap.Queue.QueuedContentIds.Count > 0 ? string.Join(", ", snap.Queue.QueuedContentIds) : "—");
            Widgets.RowSeparator();
            Widgets.ValueRow("Popped content", snap.Queue.PoppedContentId != 0 ? snap.Queue.PoppedContentId.ToString() : "—");
            Widgets.RowSeparator();
            Widgets.ValueRow("Position", snap.Queue.Position.ToString());
            Widgets.EndCard();
        }

        Widgets.SectionLabel("AGENTS");
        if (Widgets.BeginCard("ov4"))
        {
            for (var i = 0; i < snap.Agents.Count; i++)
            {
                var a = snap.Agents[i];
                if (i > 0) Widgets.RowSeparator();
                Widgets.ValueRow($"{a.Name}  ({a.Id})", a.Active ? $"active · addon {a.AddonId} · {a.Address}" : $"inactive · {a.Address}",
                    a.Active ? Theme.Theme.Green : Theme.Theme.SecondaryLabel);
            }
            Widgets.EndCard();
        }

        if (snap.Errors.Count > 0)
        {
            Widgets.SectionLabel("ERRORS");
            foreach (var e in snap.Errors) Widgets.Banner(Icon.TriangleAlert, Theme.Theme.Orange, e);
        }
    }

    private void DrawAddons(GameSnapshot snap)
    {
        var s = Widgets.Scale;
        ImGui.Checkbox("Capture nodes", ref includeNodes);
        ImGui.SameLine();
        ImGui.Checkbox("Visible only", ref onlyVisibleNodes);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200 * s);
        ImGui.InputTextWithHint("##nodefilter", "filter text / id / type", ref nodeFilter, 64);
        ImGui.Dummy(new Vector2(0, 6 * s));

        foreach (var addon in snap.Addons)
        {
            var color = !addon.Present ? Theme.Theme.TertiaryLabel : addon.Visible ? Theme.Theme.Green : Theme.Theme.Orange;
            var header = addon.Present
                ? $"{addon.Name}   {(addon.Visible ? "visible" : "hidden")}   {addon.Width:0}×{addon.Height:0} @ {addon.X:0},{addon.Y:0}   {addon.NodeCount} nodes   {addon.Address}"
                : $"{addon.Name}   not loaded";

            ImGui.PushStyleColor(ImGuiCol.Text, color);
            var open = addon.Present && ImGui.TreeNodeEx(header + "##" + addon.Name, ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.Framed);
            ImGui.PopStyleColor();
            if (!addon.Present) { ImGui.TextColored(color, header); continue; }
            if (!open) continue;

            if (ImGui.BeginTable("nodes" + addon.Name, 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerH))
            {
                ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 56 * s);
                ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 130 * s);
                ImGui.TableSetupColumn("Vis", ImGuiTableColumnFlags.WidthFixed, 32 * s);
                ImGui.TableSetupColumn("Pos", ImGuiTableColumnFlags.WidthFixed, 90 * s);
                ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 80 * s);
                ImGui.TableSetupColumn("Component", ImGuiTableColumnFlags.WidthFixed, 110 * s);
                ImGui.TableSetupColumn("Text", ImGuiTableColumnFlags.WidthStretch);
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
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(new string(' ', n.Depth * 2) + n.NodeId);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(n.Type);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(n.Visible ? Theme.Theme.Green : Theme.Theme.TertiaryLabel, n.Visible ? "●" : "○");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{n.X:0},{n.Y:0}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{n.Width}×{n.Height}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(n.ComponentType ?? "");
                    ImGui.TableNextColumn();
                    if (n.Text != null) ImGui.TextColored(Theme.Theme.Yellow, n.Text);
                }
                ImGui.EndTable();
            }
            ImGui.TreePop();
        }
    }

    private void DrawArrays(GameSnapshot snap)
    {
        ImGui.Checkbox("Capture arrays", ref includeArrays);
        ImGui.Dummy(new Vector2(0, 6 * Widgets.Scale));
        DrawArray("Number array", snap.NumberArray, 13);
        ImGui.Dummy(new Vector2(0, 10 * Widgets.Scale));
        DrawArray("String array", snap.StringArray, 3);
    }

    private static void DrawArray(string title, ArraySnapshot? arr, int stride)
    {
        if (arr == null) { Widgets.Secondary($"{title}: capture disabled"); return; }
        using (Widgets.F.BodySemibold.Push()) ImGui.TextUnformatted($"{title} [{arr.Index}]");
        if (!arr.Present) { Widgets.Secondary("not present"); return; }
        Widgets.Caption($"size {arr.Size}, showing {arr.Values.Count}; grouped by {stride} per row (hint layout, unverified)");

        var s = Widgets.Scale;
        if (ImGui.BeginTable("arr" + arr.Index, stride + 1, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollX))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 44 * s);
            for (var c = 0; c < stride; c++) ImGui.TableSetupColumn("+" + c, ImGuiTableColumnFlags.WidthFixed, stride == 3 ? 180 * s : 64 * s);
            ImGui.TableHeadersRow();
            for (var i = 0; i < arr.Values.Count; i += stride)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.Theme.SecondaryLabel, i.ToString());
                for (var c = 0; c < stride; c++)
                {
                    ImGui.TableNextColumn();
                    if (i + c < arr.Values.Count)
                    {
                        var v = arr.Values[i + c];
                        ImGui.TextColored(v is "0" or "" ? Theme.Theme.TertiaryLabel : Theme.Theme.Label, v);
                    }
                }
            }
            ImGui.EndTable();
        }
    }

    private static void DrawCasts(GameSnapshot snap)
    {
        if (snap.Casts.Count == 0) { Widgets.Secondary("Nothing is casting."); return; }
        var s = Widgets.Scale;
        if (ImGui.BeginTable("casts", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("Entity", ImGuiTableColumnFlags.WidthFixed, 90 * s);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 180 * s);
            ImGui.TableSetupColumn("Action id", ImGuiTableColumnFlags.WidthFixed, 80 * s);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthFixed, 110 * s);
            ImGui.TableHeadersRow();
            foreach (var c in snap.Casts)
            {
                var blocking = c.ActionId == Game.Keybound.CursedGazeActionId || Game.Keybound.SensoryJammerActionIds.Contains(c.ActionId);
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"0x{c.EntityId:X}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(c.Name);
                ImGui.TableNextColumn(); ImGui.TextColored(blocking ? Theme.Theme.Red : Theme.Theme.Label, c.ActionId.ToString());
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
