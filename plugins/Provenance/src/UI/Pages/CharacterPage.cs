using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Character;
using Provenance.Data;
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

/// <summary>
/// What the character brings to a run: levels, the gearset each job would use with its stats, and the
/// master books and folklore tomes that unlock recipes and legendary nodes. Read on "Check character".
/// </summary>
internal sealed class CharacterPage : Page
{
    private const float TileHeight = 156f;
    private readonly HashSet<uint> expanded = new();
    private bool checking;
    private string? error;

    public CharacterPage(Plugin plugin) : base(plugin) { }

    public override string Title => "Character";

    public override (string Text, Vector4 Color)? Status
    {
        get
        {
            if (!Services.ClientState.IsLoggedIn) return ("Log in to check your character", Palette.TextFaint);
            var p = Plans.Profile;
            return p == null ? ("Not checked yet", Palette.Warn) : ("Checked " + Ago(p.CheckedAtUtc), Palette.Accent);
        }
    }

    public override IEnumerable<FooterAction> Actions
    {
        get
        {
            var ready = Services.ClientState.IsLoggedIn && Plans.Catalog != null && !checking;
            yield return new FooterAction(checking ? "Checking" : "Check character", Ui.Kind.Primary, FontAwesomeIcon.UserCheck, Check,
                Enabled: ready, Tooltip: !Services.ClientState.IsLoggedIn ? "Log in first" : Plans.Catalog == null ? "Still reading game data" : null);
        }
    }

    private void Check()
    {
        checking = true;
        error = null;
        // Game memory is read on the framework thread.
        Services.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                if (Plans.CheckCharacter() == null) error = "The character isn't fully loaded yet. Try again in a moment.";
            }
            catch (Exception e)
            {
                Services.Log.Error(e, "Character check failed");
                error = "The check failed: " + e.Message;
            }
            finally
            {
                checking = false;
            }
        });
    }

    public override void Draw()
    {
        var s = Ui.S;
        var p = Plans.Profile;
        var catalog = Plans.Catalog;

        if (p == null)
        {
            Ui.Heading("Your character", "Check your character to read its gearsets, crafting and gathering stats, master books and folklore tomes. The plan uses them to tell which recipes and legendary nodes are open to you.");
            ImGui.Dummy(new Vector2(0, 4 * s));
            if (error != null) Ui.Wrapped(error, Palette.Danger);
            if (Services.ClientState.IsLoggedIn && catalog != null && Ui.Button(checking ? "Checking" : "Check character", Ui.Kind.Primary, FontAwesomeIcon.UserCheck, enabled: !checking, id: "##checkinline")) Check();
            return;
        }

        Ui.Heading(p.World.Length > 0 ? $"{p.Name} of {p.World}" : p.Name, $"Checked {Ago(p.CheckedAtUtc)}. Each job shows its highest item level gearset, the one a run would equip.");
        if (error != null) Ui.Wrapped(error, Palette.Danger);
        if (p.Note != null)
        {
            Ui.Icon(FontAwesomeIcon.InfoCircle, Palette.Warn, small: true);
            ImGui.SameLine(0, 8 * s);
            Ui.Wrapped(p.Note, Palette.Warn);
        }
        ImGui.Dummy(new Vector2(0, 4 * s));

        SectionTitle("Crafters");
        ConsumableRow(Discipline.Hand);
        Grid(ClassJobs.Crafters, 4, p);
        SectionTitle("Gatherers");
        ConsumableRow(Discipline.Land);
        Grid(ClassJobs.Gatherers, 3, p);

        if (catalog == null) return;
        SectionTitle("Master books");
        Ui.Wrapped("Master recipes can only be crafted once their book is learned.", Palette.TextMuted);
        foreach (var job in ClassJobs.Crafters)
        {
            var books = catalog.MasterBooks.Where(b => b.Job.HasValue && (uint)b.Job.Value + 8 == job).ToList();
            BookRow(job, books.Count, books.Count(b => p.MasterBooks.Contains(b.Id)), books.Where(b => !p.MasterBooks.Contains(b.Id)).Select(b => b.Name).ToList());
        }
        ImGui.Dummy(new Vector2(0, 6 * s));
        SectionTitle("Folklore tomes");
        Ui.Wrapped("Legendary nodes and some fishing holes only appear once their tome is read.", Palette.TextMuted);
        foreach (var job in ClassJobs.Gatherers)
        {
            var tomes = catalog.FolkloreTomes.Where(t => t.ClassJobId == job).ToList();
            BookRow(job, tomes.Count, tomes.Count(t => p.FolkloreTomes.Contains(t.ItemId)), tomes.Where(t => !p.FolkloreTomes.Contains(t.ItemId)).Select(t => t.Name).ToList());
        }
    }

    private static void SectionTitle(string text)
    {
        ImGui.Dummy(new Vector2(0, 2 * Ui.S));
        using (Ui.F.Strong.Push()) Ui.Text(text);
    }

    private void Grid(uint[] jobs, int columns, CharacterProfile p)
    {
        var s = Ui.S;
        var gap = 12 * s;
        var avail = ImGui.GetContentRegionAvail().X;
        var w = (avail - gap * (columns - 1)) / columns;
        var h = TileHeight * s;
        var origin = ImGui.GetCursorScreenPos();
        for (var i = 0; i < jobs.Length; i++)
        {
            var pos = origin + new Vector2((i % columns) * (w + gap), (i / columns) * (h + gap));
            var js = p.Job(jobs[i]);
            Tile(jobs[i], js, pos, w, h, js == null ? null : ConsumableBonus(ClassJobs.Of(jobs[i]), js.Stats));
        }
        var rows = (jobs.Length + columns - 1) / columns;
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(avail, rows * h + (rows - 1) * gap + 10 * s));
    }

    private static void Tile(uint job, JobStats? js, Vector2 pos, float w, float h, int[]? bonus)
    {
        var s = Ui.S;
        var dl = ImGui.GetWindowDrawList();
        var color = Palette.ClassJobColor(job);
        dl.AddRectFilled(pos, pos + new Vector2(w, h), Palette.U32(Palette.Surface), Ui.CardRadius * s);
        // A short stripe in the job's colour marks the tile without tinting the whole card.
        dl.AddRectFilled(pos + new Vector2(14 * s, 0), pos + new Vector2(54 * s, 3 * s), Palette.U32(color), 2 * s);

        var pad = 14 * s;
        var x = pos.X + pad;
        var y = pos.Y + 14 * s;
        using (Ui.F.Strong.Push())
        {
            JobIcons.Draw(dl, job, new Vector2(x + 10 * s, y + ImGui.GetTextLineHeight() / 2), 20 * s);
            dl.AddText(new Vector2(x + 26 * s, y), Palette.U32(color), ClassJobs.Abbreviation(job));
        }
        var level = js?.Level ?? 0;
        var levelText = level > 0 ? "level " + level : "not unlocked";
        float lw;
        using (Ui.F.Small.Push())
        {
            lw = ImGui.CalcTextSize(levelText).X;
            dl.AddText(new Vector2(pos.X + w - pad - lw, y + 2 * s), Palette.U32(level > 0 ? Palette.TextMuted : Palette.TextFaint), levelText);
        }

        y += 24 * s;
        if (js == null || js.Source == StatSource.None)
        {
            using (Ui.F.Small.Push())
                dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(x, y), Palette.U32(Palette.TextFaint), level > 0 ? "No gearset saved for this job." : "", w - pad * 2);
            return;
        }

        using (Ui.F.Small.Push())
        {
            var set = js.GearsetName.Length > 0 ? js.GearsetName : "Gearset " + js.GearsetNumber;
            var label = $"{set}, item level {js.ItemLevel}";
            while (label.Length > 4 && ImGui.CalcTextSize(label).X > w - pad * 2) label = label[..^2] + "…";
            dl.AddText(new Vector2(x, y), Palette.U32(Palette.TextMuted), label);
        }

        y += 22 * s;
        var d = ClassJobs.Of(job);
        for (var i = 0; i < 3; i++)
        {
            var name = ClassJobs.StatName(d, i);
            var value = js.Stats.Length > i ? js.Stats[i].ToString("N0") : "–";
            dl.AddText(new Vector2(x, y), Palette.U32(Palette.TextMuted), name);
            // What the chosen food and medicine add, in brackets after the value.
            var extra = bonus != null && bonus[i] > 0 ? $" (+{bonus[i]:N0})" : "";
            var ew = extra.Length > 0 ? ImGui.CalcTextSize(extra).X : 0;
            if (extra.Length > 0)
                dl.AddText(new Vector2(pos.X + w - pad - ew, y), Palette.U32(Palette.Accent), extra);
            using (Ui.F.Strong.Push())
            {
                var vw = ImGui.CalcTextSize(value).X;
                dl.AddText(new Vector2(pos.X + w - pad - ew - vw, y), Palette.U32(Palette.Text), value);
            }
            y += 20 * s;
        }

        // Footer: how the numbers were found, plus specialist / Manipulation.
        var (srcText, srcColor, srcTip) = js.Source switch
        {
            StatSource.Measured => ("measured", Palette.Accent, "Read from the game while on this job in this gearset."),
            StatSource.GearAndBase => ("from gearset", Palette.TextMuted, "Gearset items and materia plus base stats measured on another job at the same level."),
            _ => ("gear only", Palette.Warn, "Gear and materia only. Check again while on any job of this kind at this level to add base stats."),
        };
        ImGui.SetCursorScreenPos(new Vector2(x, pos.Y + h - 30 * s));
        Ui.Chip(srcText, srcColor);
        Ui.Tooltip(srcTip);
        if (js.Specialist) { ImGui.SameLine(0, 6 * s); Ui.Chip("specialist", Palette.Accent); }
        if (ClassJobs.IsCrafter(job) && !js.Manipulation && level > 0)
        {
            ImGui.SameLine(0, 6 * s);
            Ui.Chip("no Manipulation", Palette.Warn);
            Ui.Tooltip("Manipulation isn't learned on this crafter yet; it comes from a class quest.");
        }
    }

    private void BookRow(uint job, int total, int have, List<string> missing)
    {
        if (total == 0) return;
        var s = Ui.S;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var h = 32 * s;
        var isOpen = expanded.Contains(job);
        var canOpen = missing.Count > 0;

        if (ImGui.InvisibleButton("##books" + job, new Vector2(width, h)) && canOpen)
        {
            if (!expanded.Remove(job)) expanded.Add(job);
        }
        var hovered = ImGui.IsItemHovered() && canOpen;
        var dl = ImGui.GetWindowDrawList();
        if (hovered) dl.AddRectFilled(start, start + new Vector2(width, h), Palette.U32(Palette.Surface), Ui.InputRadius * s);

        var color = Palette.ClassJobColor(job);
        var textY = start.Y + (h - ImGui.GetTextLineHeight()) / 2;
        using (Ui.F.Strong.Push())
        {
            JobIcons.Draw(dl, job, new Vector2(start.X + 19 * s, start.Y + h / 2), 18 * s);
            dl.AddText(new Vector2(start.X + 34 * s, textY), Palette.U32(color), ClassJobs.Abbreviation(job));
        }
        var count = have == total ? $"all {total}" : $"{have} of {total}";
        dl.AddText(new Vector2(start.X + 88 * s, textY), Palette.U32(have == total ? Palette.Accent : Palette.Text), count);
        ImGui.SetCursorScreenPos(new Vector2(start.X + 174 * s, start.Y + (h - 6 * s) / 2));
        Ui.Bar("##bookbar" + job, total == 0 ? 0 : have / (float)total, have == total ? Palette.Accent : color, MathF.Min(260 * s, width - 324 * s));
        if (canOpen)
        {
            var hint = isOpen ? "hide missing" : $"{missing.Count} missing";
            using (Ui.F.Small.Push())
            {
                var hw = ImGui.CalcTextSize(hint).X;
                dl.AddText(new Vector2(start.X + width - hw - 10 * s, start.Y + (h - ImGui.GetTextLineHeight()) / 2), Palette.U32(hovered ? Palette.Text : Palette.TextMuted), hint);
            }
        }
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h + 2 * s));
        if (isOpen && canOpen)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 88 * s);
            Ui.Wrapped(string.Join(", ", missing), Palette.TextMuted, width - 80 * s);
            ImGui.Dummy(new Vector2(0, 2 * s));
        }
    }

    // ------------------------------------------------------------------ food and medicine

    private int[]? ConsumableBonus(Discipline d, int[] stats)
    {
        var catalog = Plans.Catalog;
        if (catalog == null || stats.Length < 3) return null;
        var (food, medicine) = Config.Consumables.For(d);
        var total = new int[3];
        foreach (var choice in new[] { food, medicine })
        {
            if (catalog.Consumable(choice.ItemId) is not { } c) continue;
            var b = c.Bonus(stats, choice.Hq);
            for (var i = 0; i < 3; i++) total[i] += b[i];
        }
        return total.Any(v => v > 0) ? total : null;
    }

    private void ConsumableRow(Discipline d)
    {
        var s = Ui.S;
        var (food, medicine) = Config.Consumables.For(d);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 2 * s);
        Picker("##food" + d, "Food", ConsumableKind.Food, d, food);
        ImGui.SameLine(0, 16 * s);
        Picker("##med" + d, "Medicine", ConsumableKind.Medicine, d, medicine);
        ImGui.Dummy(new Vector2(0, 2 * s));
    }

    private readonly Dictionary<string, List<ConsumableInfo>> pickerOptions = new();
    private readonly Dictionary<string, List<DropdownItem>> pickerItems = new();

    /// <summary>A field showing the current choice; it opens a searchable list of matching consumables.</summary>
    private void Picker(string id, string label, ConsumableKind kind, Discipline d, ConsumableChoice choice)
    {
        var s = Ui.S;
        var catalog = Plans.Catalog;
        if (catalog == null) return;
        var current = catalog.Consumable(choice.ItemId);
        if (Ui.DropdownField(id, current?.Name, "None", prefix: label, gameIcon: current?.Icon ?? 0, hq: choice.Hq && current is { CanBeHq: true }))
        {
            var options = catalog.Consumables.Where(c => c.Kind == kind && c.Discipline == d).ToList();
            var items = new List<DropdownItem> { new("None", Sub: "No " + label.ToLowerInvariant()) };
            foreach (var c in options)
            {
                var owned = Plans.Owned.GetValueOrDefault(c.ItemId);
                items.Add(new DropdownItem(c.Name, Sub: Describe(c, choice.Hq, d), Badge: owned > 0 ? $"{owned} in your bags" : null,
                    GameIcon: c.Icon, Hq: choice.Hq && c.CanBeHq));
            }
            pickerOptions[id] = options;
            pickerItems[id] = items;
        }
        Ui.Tooltip(current == null
            ? $"No {label.ToLowerInvariant()} is used. Pick one to see what it adds to each job's stats."
            : Describe(current, choice.Hq, d));

        if (current is { CanBeHq: true })
        {
            ImGui.SameLine(0, 8 * s);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2 * s);
            if (Ui.ToggleChip(id + "hq", "HQ", Palette.Accent, choice.Hq)) { choice.Hq = !choice.Hq; Config.Save(); }
            Ui.Tooltip("Use the high-quality version, which adds more.");
        }

        var listed = pickerOptions.GetValueOrDefault(id);
        var at = listed?.FindIndex(c => c.ItemId == choice.ItemId) ?? -1;
        var selected = choice.ItemId == 0 ? 0 : at >= 0 ? at + 1 : -1;
        var pick = Ui.DropdownList(id, pickerItems.GetValueOrDefault(id), selected, "Nothing of this kind in the game data.", 460 * s);
        if (pick == 0) { choice.ItemId = 0; Config.Save(); }
        else if (pick > 0 && listed != null) { choice.ItemId = listed[pick - 1].ItemId; Config.Save(); }
    }

    private static string Describe(ConsumableInfo c, bool hq, Discipline d)
    {
        var useHq = hq && c.CanBeHq;
        var parts = c.Params.Select(p =>
        {
            var value = useHq ? p.ValueHq : p.Value;
            var max = useHq ? p.MaxHq : p.Max;
            var stat = ClassJobs.StatName(d, p.StatIndex);
            return p.Relative ? $"{stat} +{value}% (max {max})" : $"{stat} +{value}";
        });
        return string.Join(", ", parts) + (useHq ? ", HQ" : "");
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        return utc.ToLocalTime().ToString("d MMM, HH:mm");
    }
}
