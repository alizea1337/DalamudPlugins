using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Interface.ManagedFontAtlas;

namespace Provenance.UI.Theme;

/// <summary>
/// Nunito in three weights (latin + latin-ext merged, plus the glyphs of Dalamud's UI language so item
/// names in other client languages still render). Icons come from Dalamud's bundled FontAwesome.
/// Built at the interface size (Settings, General): a new size builds a new set, and the old one stays in use
/// until the new one is ready, so the window never shows the default font in between.
/// </summary>
internal sealed class Fonts : IDisposable
{
    public const float BodySize = 14f;
    public const float SmallSize = 12f;
    public const float TitleSize = 20f;
    public const float DisplaySize = 26f;

    /// <summary>One size's fonts.</summary>
    private sealed class Set : IDisposable
    {
        public float Scale;
        public IFontHandle Body = null!, Strong = null!, Small = null!, Title = null!, Display = null!, Icon = null!, IconSmall = null!;
        public readonly Dictionary<int, IFontHandle> Regular = new();

        public IEnumerable<IFontHandle> All => new[] { Body, Strong, Small, Title, Display, Icon, IconSmall };
        public bool Ready => All.All(h => h.Available);

        public void Dispose()
        {
            foreach (var h in All) h.Dispose();
            foreach (var h in Regular.Values) h.Dispose();
            Regular.Clear();
        }
    }

    private readonly IFontAtlas atlas;
    private readonly string dir;
    private Set current;
    /// <summary>A new size being built; it takes over once every font of it is ready.</summary>
    private Set? pending;
    /// <summary>When the atlas was last asked to build (fonts made after the first build wait for one).</summary>
    private DateTime buildAskedAt;
    /// <summary>Fonts not built this long after asking: ask again, and log what's wrong.</summary>
    private static readonly TimeSpan BuildRetry = TimeSpan.FromSeconds(3);

    public Fonts(float scale = 1f)
    {
        atlas = Services.PluginInterface.UiBuilder.FontAtlas;
        dir = Path.Combine(Services.PluginInterface.AssemblyLocation.DirectoryName!, "Resources", "Fonts");
        current = Build(scale);
    }

    /// <summary>The interface size the fonts in use are built for (1 = 100%).</summary>
    public float Scale => current.Scale;

    public IFontHandle Body => current.Body;
    public IFontHandle Strong => current.Strong;
    public IFontHandle Small => current.Small;
    public IFontHandle Title => current.Title;
    public IFontHandle Display => current.Display;
    public IFontHandle Icon => current.Icon;
    public IFontHandle IconSmall => current.IconSmall;

    /// <summary>Asks for another interface size; the fonts switch once the new set is built (see <see cref="Update"/>).</summary>
    public void SetScale(float scale)
    {
        if (MathF.Abs((pending?.Scale ?? current.Scale) - scale) < 0.001f) return;
        pending?.Dispose();
        pending = MathF.Abs(current.Scale - scale) < 0.001f ? null : Build(scale);
        if (pending != null) AskForBuild();
    }

    /// <summary>Once a frame, before any font is pushed: switches to a new size once it's built.</summary>
    public void Update()
    {
        if (pending == null) return;
        if (!pending.Ready)
        {
            // Not built yet: the atlas may not rebuild by itself for fonts made after its first build. Ask again now and then.
            if (DateTime.UtcNow - buildAskedAt > BuildRetry)
            {
                foreach (var h in pending.All.Where(h => h.LoadException != null))
                    Services.Log.Warning(h.LoadException!, "A font for interface size {Scale} couldn't be built", pending.Scale);
                AskForBuild(always: true);
            }
            return;
        }
        var old = current;
        current = pending;
        pending = null;
        old.Dispose();
        Services.Log.Information("Interface size {Scale:P0}: fonts switched", current.Scale);
    }

    /// <summary>
    /// Asks the atlas to build the fonts made since its last build. One that rebuilds by itself (Async) is left to it
    /// at first; the others are told to build now, and every atlas once the fonts are still missing a while later.
    /// </summary>
    private void AskForBuild(bool always = false)
    {
        buildAskedAt = DateTime.UtcNow;
        try
        {
            if (always || atlas.AutoRebuildMode != FontAtlasAutoRebuildMode.Async)
                if (atlas.BuildTask is not { IsCompleted: false }) _ = atlas.BuildFontsAsync();
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "Couldn't ask the font atlas to build");
        }
    }

    /// <summary>What the fonts are doing, for a bug report.</summary>
    public string Describe()
        => $"in use {current.Scale:P0} (ready {current.Ready}), waiting {(pending == null ? "none" : $"{pending.Scale:P0} (ready {pending.Ready})")}, "
           + $"atlas {atlas.AutoRebuildMode}, built {atlas.HasBuiltAtlas}, building {(atlas.BuildTask is { IsCompleted: false } ? "yes" : "no")}, "
           + $"tooltip sizes {string.Join(",", current.Regular.Select(kv => $"{kv.Key}:{(kv.Value.Available ? "ready" : "waiting")}"))}";

    /// <summary>
    /// Regular text at this pixel size, before the interface size (for tooltips). Sizes other than Small and Body
    /// are built the first time they're asked for, and only then: until the atlas has built one,
    /// <see cref="IFontHandle.Available"/> is false.
    /// </summary>
    public IFontHandle Regular(int size)
    {
        if (size == (int)SmallSize) return Small;
        if (size == (int)BodySize) return Body;
        if (!current.Regular.TryGetValue(size, out var handle))
        {
            current.Regular[size] = handle = Text(atlas, dir, 400, size * current.Scale);
            AskForBuild();
        }
        return handle;
    }

    private Set Build(float scale) => new()
    {
        Scale = scale,
        Body = Text(atlas, dir, 400, BodySize * scale),
        Strong = Text(atlas, dir, 700, BodySize * scale),
        Small = Text(atlas, dir, 400, SmallSize * scale),
        Title = Text(atlas, dir, 800, TitleSize * scale),
        Display = Text(atlas, dir, 800, DisplaySize * scale),
        Icon = Icons(atlas, 14f * scale),
        IconSmall = Icons(atlas, 11f * scale),
    };

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
        pending?.Dispose();
        pending = null;
        current.Dispose();
    }
}
