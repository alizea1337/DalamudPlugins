using System;
using System.IO;
using Dalamud.Interface.ManagedFontAtlas;

namespace Keystroke.UI.Theme;

/// <summary>Inter (text) and Lucide (icons) font handles built on Dalamud's font atlas.</summary>
internal sealed class Fonts : IDisposable
{
    public IFontHandle Body { get; }
    public IFontHandle BodyMedium { get; }
    public IFontHandle BodySemibold { get; }
    public IFontHandle Caption { get; }
    public IFontHandle Title { get; }
    public IFontHandle LargeTitle { get; }
    public IFontHandle Icons { get; }
    public IFontHandle IconsLarge { get; }
    public IFontHandle Mono { get; }

    public const float BodySize = 14f;
    public const float CaptionSize = 12f;
    public const float TitleSize = 20f;
    public const float LargeTitleSize = 30f;
    public const float IconSize = 16f;
    public const float IconLargeSize = 22f;

    public Fonts()
    {
        var atlas = Services.PluginInterface.UiBuilder.FontAtlas;
        var dir = Path.Combine(Services.PluginInterface.AssemblyLocation.DirectoryName!, "Resources", "Fonts");
        var regular = Path.Combine(dir, "Inter-Regular.ttf");
        var medium = Path.Combine(dir, "Inter-Medium.ttf");
        var semibold = Path.Combine(dir, "Inter-SemiBold.ttf");
        var lucide = Path.Combine(dir, "Lucide.ttf");

        Body = Text(atlas, regular, BodySize);
        BodyMedium = Text(atlas, medium, BodySize);
        BodySemibold = Text(atlas, semibold, BodySize);
        Caption = Text(atlas, regular, CaptionSize);
        Title = Text(atlas, semibold, TitleSize);
        LargeTitle = Text(atlas, semibold, LargeTitleSize);
        Icons = IconFont(atlas, lucide, IconSize);
        IconsLarge = IconFont(atlas, lucide, IconLargeSize);
        Mono = atlas.NewGameFontHandle(new Dalamud.Interface.GameFonts.GameFontStyle(Dalamud.Interface.GameFonts.GameFontFamilyAndSize.Axis12));
    }

    private static IFontHandle Text(IFontAtlas atlas, string path, float sizePx)
        => atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            var font = tk.AddFontFromFile(path, new SafeFontConfig { SizePx = sizePx });
            // Dalamud's default font supplies any glyph Inter lacks (CJK names in chat, symbols).
            tk.AddFontAwesomeIconFont(new SafeFontConfig { SizePx = sizePx, MergeFont = font });
        }));

    private static IFontHandle IconFont(IFontAtlas atlas, string path, float sizePx)
        => atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
            tk.AddFontFromFile(path, new SafeFontConfig { SizePx = sizePx, GlyphRanges = Icon.GlyphRanges })));

    public void Dispose()
    {
        Body.Dispose();
        BodyMedium.Dispose();
        BodySemibold.Dispose();
        Caption.Dispose();
        Title.Dispose();
        LargeTitle.Dispose();
        Icons.Dispose();
        IconsLarge.Dispose();
        Mono.Dispose();
    }
}
