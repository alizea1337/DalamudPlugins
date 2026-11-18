using System;
using System.IO;
using Dalamud.Interface.ManagedFontAtlas;

namespace Provenance.UI.Theme;

/// <summary>
/// Nunito in three weights (latin + latin-ext merged, plus the glyphs of Dalamud's UI language so item
/// names in other client languages still render). Icons come from Dalamud's bundled FontAwesome.
/// </summary>
internal sealed class Fonts : IDisposable
{
    public const float BodySize = 14f;
    public const float SmallSize = 12f;
    public const float TitleSize = 20f;
    public const float DisplaySize = 26f;

    public IFontHandle Body { get; }
    public IFontHandle Strong { get; }
    public IFontHandle Small { get; }
    public IFontHandle Title { get; }
    public IFontHandle Display { get; }
    public IFontHandle Icon { get; }
    public IFontHandle IconSmall { get; }

    public Fonts()
    {
        var atlas = Services.PluginInterface.UiBuilder.FontAtlas;
        var dir = Path.Combine(Services.PluginInterface.AssemblyLocation.DirectoryName!, "Resources", "Fonts");

        Body = Text(atlas, dir, 400, BodySize);
        Strong = Text(atlas, dir, 700, BodySize);
        Small = Text(atlas, dir, 400, SmallSize);
        Title = Text(atlas, dir, 800, TitleSize);
        Display = Text(atlas, dir, 800, DisplaySize);
        Icon = Icons(atlas, 14f);
        IconSmall = Icons(atlas, 11f);
    }

    private static IFontHandle Text(IFontAtlas atlas, string dir, int weight, float size)
        => atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            var font = tk.AddFontFromFile(Path.Combine(dir, $"Nunito-{weight}.ttf"), new SafeFontConfig { SizePx = size });
            tk.AddFontFromFile(Path.Combine(dir, $"Nunito-{weight}-ext.ttf"), new SafeFontConfig { SizePx = size, MergeFont = font });
            tk.AttachExtraGlyphsForDalamudLanguage(new SafeFontConfig { SizePx = size, MergeFont = font });
            // Item names follow the client language, which can differ from Dalamud's UI language.
            if (Services.ClientState.ClientLanguage == Dalamud.Game.ClientLanguage.Japanese)
                tk.AddDalamudAssetFont(Dalamud.DalamudAsset.NotoSansCjkRegular, new SafeFontConfig { SizePx = size, MergeFont = font, GlyphRanges = CjkRanges });
        }));

    private static readonly ushort[] CjkRanges = Dalamud.Interface.Utility.ImGuiHelpers.CreateImGuiRangesFrom(new[]
    {
        System.Text.Unicode.UnicodeRanges.CjkSymbolsandPunctuation, System.Text.Unicode.UnicodeRanges.Hiragana,
        System.Text.Unicode.UnicodeRanges.Katakana, System.Text.Unicode.UnicodeRanges.CjkUnifiedIdeographs,
        System.Text.Unicode.UnicodeRanges.HalfwidthandFullwidthForms,
    });

    private static IFontHandle Icons(IFontAtlas atlas, float size)
        => atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddFontAwesomeIconFont(new SafeFontConfig { SizePx = size })));

    public void Dispose()
    {
        Body.Dispose(); Strong.Dispose(); Small.Dispose(); Title.Dispose(); Display.Dispose(); Icon.Dispose(); IconSmall.Dispose();
    }
}
