using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Planning;
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

/// <summary>
/// The run, stage by stage. In this release it previews what a run of the current order would do;
/// the engine behind it lands in the next milestones.
/// </summary>
internal sealed class RunPage : Page
{
    public RunPage(Plugin plugin) : base(plugin) { }

    public override string Title => "Run";

    public override (string Text, Vector4 Color)? Status => ("Not running", Palette.TextFaint);

    public override void Draw()
    {
        var s = Ui.S;
        Ui.Heading("Run", "A run takes the order from start to finish in these steps. It stops on its own when the order is done.");
        ImGui.Dummy(new Vector2(0, 4 * s));

        Ui.BeginCard(Palette.WithAlpha(Palette.Warn, 0.07f));
        Ui.Icon(FontAwesomeIcon.InfoCircle, Palette.Warn);
        ImGui.SameLine(0, 10 * s);
        Ui.Wrapped("Running isn't available in this version yet. Below is what a run of the current order would do.", Palette.Text);
        Ui.EndCard();

        var plan = Plans.Plan;
        var gather = plan.Count(Supply.Gather) + plan.Count(Supply.GatherTimed) + plan.Count(Supply.Fish);
        var crafts = plan.CraftSteps.Sum(l => l.Crafts);
        var steps = new (FontAwesomeIcon Icon, string Title, string Detail, bool Active)[]
        {
            (FontAwesomeIcon.Box, "Take items from retainers", "Opens your retainers at the nearest summoning bell and takes out what the order can use.", Config.Stock.Retainers),
            (FontAwesomeIcon.Store, "Buy from vendors", plan.Count(Supply.Vendor) > 0 ? $"{plan.Count(Supply.Vendor)} items for about {plan.VendorGil:N0} gil." : "Nothing to buy for this order.", plan.Count(Supply.Vendor) > 0),
            (FontAwesomeIcon.Paw, "Farm monster drops", plan.Count(Supply.Drop) > 0 ? $"{plan.Count(Supply.Drop)} items from monsters." : "No monster drops needed.", plan.Count(Supply.Drop) > 0),
            (FontAwesomeIcon.Leaf, "Gather and craft", gather + crafts > 0 ? $"{gather} materials to gather or fish and {crafts} crafts. Crafting fills the waits between timed nodes." : "Nothing to gather or craft.", gather + crafts > 0),
        };

        for (var i = 0; i < steps.Length; i++)
        {
            var st = steps[i];
            var pos = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            var c = pos + new Vector2(16 * s, 18 * s);
            if (i < steps.Length - 1)
                dl.AddLine(c + new Vector2(0, 16 * s), c + new Vector2(0, 58 * s), Palette.U32(Palette.Line), 1.5f * s);
            dl.AddCircleFilled(c, 16 * s, Palette.U32(st.Active ? Palette.Raised : Palette.Surface));
            using (Ui.F.Strong.Push())
            {
                var n = (i + 1).ToString();
                var ns = ImGui.CalcTextSize(n);
                dl.AddText(c - ns / 2, Palette.U32(st.Active ? Palette.Text : Palette.TextFaint), n);
            }
            ImGui.SetCursorScreenPos(pos + new Vector2(46 * s, 4 * s));
            ImGui.BeginGroup();
            using (Ui.F.Strong.Push()) Ui.Text(st.Title, st.Active ? Palette.Text : Palette.TextFaint);
            Ui.Wrapped(st.Detail, Palette.TextMuted, 520 * s);
            ImGui.EndGroup();
            ImGui.SetCursorScreenPos(new Vector2(pos.X, ImGui.GetItemRectMax().Y + 14 * s));
        }
    }
}
