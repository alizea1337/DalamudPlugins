using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Interface.ManagedFontAtlas;

namespace Provenance.UI.Theme;

/// <summary>
/// Nunito in three weights (latin + latin-ext merged, plus the glyphs of Dalamud's UI language so item
/// names in other client languages still render). Icons come from Dalamud's bundled FontAwesome.
/// Text is built at the text size and icons at the interface scale (Settings, Interface): a new size builds a new
/// set, and the old one stays in use until the new one is ready (the window stretches it to the size meanwhile).
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
        /// <summary>Text size the text fonts are built at (1 = 100%).</summary>
        public float Scale;
        /// <summary>Interface scale the icon fonts are built at.</summary>
        public float IconScale;
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

    public Fonts(float textScale = 1f, float iconScale = 1f)
    {
        atlas = Services.PluginInterface.UiBuilder.FontAtlas;
        dir = Path.Combine(Services.PluginInterface.AssemblyLocation.DirectoryName!, "Resources", "Fonts");
        current = Build(textScale, iconScale);
    }

    /// <summary>The text size the text fonts in use are built at (1 = 100%).</summary>
    public float Scale => current.Scale;

    /// <summary>New sizes are being built (the ones in use stay until they're ready).</summary>
    public bool Building => pending != null;

    public IFontHandle Body => current.Body;
    public IFontHandle Strong => current.Strong;
    public IFontHandle Small => current.Small;
    public IFontHandle Title => current.Title;
    public IFontHandle Display => current.Display;
    public IFontHandle Icon => current.Icon;
    public IFontHandle IconSmall => current.IconSmall;

    /// <summary>Asks for other sizes; the fonts switch once the new set is built (see <see cref="Update"/>).</summary>
    public void SetScale(float textScale, float iconScale)
    {
        static bool Same(Set s, float t, float i) => MathF.Abs(s.Scale - t) < 0.001f && MathF.Abs(s.IconScale - i) < 0.001f;
        if (Same(pending ?? current, textScale, iconScale)) return;
        pending?.Dispose();
        pending = Same(current, textScale, iconScale) ? null : Build(textScale, iconScale);
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
        Services.Log.Information("Fonts switched: text {Text:P0}, icons {Icons:P0}", current.Scale, current.IconScale);
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
        => $"in use text {current.Scale:P0}, icons {current.IconScale:P0} (ready {current.Ready}), waiting {(pending == null ? "none" : $"text {pending.Scale:P0}, icons {pending.IconScale:P0} (ready {pending.Ready})")}, "
           + $"atlas {atlas.AutoRebuildMode}, built {atlas.HasBuiltAtlas}, building {(atlas.BuildTask is { IsCompleted: false } ? "yes" : "no")}, "
           + $"tooltip sizes {string.Join(",", current.Regular.Select(kv => $"{kv.Key}:{(kv.Value.Available ? "ready" : "waiting")}"))}";

    /// <summary>
    /// Regular text at exactly this pixel size, whatever the interface size (tooltips have a size of their own). At
    /// 100% the Small and Body fonts are that already; other sizes are built the first time they're asked for, and
    /// only then: until the atlas has built one, <see cref="IFontHandle.Available"/> is false.
    /// </summary>
    public IFontHandle Regular(int size)
    {
        if (MathF.Abs(current.Scale - 1f) < 0.001f)
        {
            if (size == (int)SmallSize) return Small;
            if (size == (int)BodySize) return Body;
        }
        if (!current.Regular.TryGetValue(size, out var handle))
        {
            current.Regular[size] = handle = Text(atlas, dir, 400, size);
            AskForBuild();
        }
        return handle;
    }

    private Set Build(float textScale, float iconScale) => new()
    {
        Scale = textScale,
        IconScale = iconScale,
        Body = Text(atlas, dir, 400, BodySize * textScale),
        Strong = Text(atlas, dir, 700, BodySize * textScale),
        Small = Text(atlas, dir, 400, SmallSize * textScale),
        Title = Text(atlas, dir, 800, TitleSize * textScale),
        Display = Text(atlas, dir, 800, DisplaySize * textScale),
        Icon = Icons(atlas, 14f * iconScale),
        IconSmall = Icons(atlas, 11f * iconScale),
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
