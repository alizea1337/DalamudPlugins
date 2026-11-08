using System;
using System.IO;
using Dalamud.Interface.ManagedFontAtlas;

namespace Keystroke.UI.Theme;

/// <summary>DejaVu Sans (Verdana-metric, free) for text, Lucide for icons.</summary>
internal sealed class Fonts : IDisposable
{
    public IFontHandle Body { get; }        // 12 px — rows, labels
    public IFontHandle Small { get; }       // 11 px — values, captions
    public IFontHandle Tiny { get; }        // 10 px — rail labels, footers
    public IFontHandle Bold { get; }        // 12 px bold — group titles, emphasis
    public IFontHandle Title { get; }       // 18 px bold — page title, compact header
    public IFontHandle Display { get; }     // 26 px bold — dashboard numbers
    public IFontHandle Icons { get; }       // 16 px
    public IFontHandle IconsLarge { get; }  // 22 px
    public IFontHandle Mono { get; }

    public const float BodySize = 12f;
    public const float SmallSize = 11f;
    public const float TinySize = 10f;
    public const float TitleSize = 18f;
    public const float DisplaySize = 26f;
    public const float IconSize = 16f;
    public const float IconLargeSize = 22f;

    public Fonts()
    {
        var atlas = Services.PluginInterface.UiBuilder.FontAtlas;
        var dir = Path.Combine(Services.PluginInterface.AssemblyLocation.DirectoryName!, "Resources", "Fonts");
        var regular = Path.Combine(dir, "DejaVuSans.ttf");
        var bold = Path.Combine(dir, "DejaVuSans-Bold.ttf");
        var lucide = Path.Combine(dir, "Lucide.ttf");

        Body = Text(atlas, regular, BodySize);
        Small = Text(atlas, regular, SmallSize);
        Tiny = Text(atlas, regular, TinySize);
        Bold = Text(atlas, bold, BodySize);
        Title = Text(atlas, bold, TitleSize);
        Display = Text(atlas, bold, DisplaySize);
        Icons = IconFont(atlas, lucide, IconSize);
        IconsLarge = IconFont(atlas, lucide, IconLargeSize);
        Mono = atlas.NewGameFontHandle(new Dalamud.Interface.GameFonts.GameFontStyle(Dalamud.Interface.GameFonts.GameFontFamilyAndSize.Axis12));
    }

    private static IFontHandle Text(IFontAtlas atlas, string path, float sizePx)
        => atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            var font = tk.AddFontFromFile(path, new SafeFontConfig { SizePx = sizePx });
            tk.AddFontAwesomeIconFont(new SafeFontConfig { SizePx = sizePx, MergeFont = font });
        }));

    private static IFontHandle IconFont(IFontAtlas atlas, string path, float sizePx)
        => atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
            tk.AddFontFromFile(path, new SafeFontConfig { SizePx = sizePx, GlyphRanges = Icon.GlyphRanges })));

    public void Dispose()
    {
        Body.Dispose(); Small.Dispose(); Tiny.Dispose(); Bold.Dispose(); Title.Dispose(); Display.Dispose();
        Icons.Dispose(); IconsLarge.Dispose(); Mono.Dispose();
    }
}
