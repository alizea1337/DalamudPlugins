using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Lumina.Data.Files;
using Provenance.UI.Theme;

namespace Provenance.UI;

/// <summary>
/// Job glyphs from the game's transparent job icons (62000 + ClassJob row). The game draws them silver
/// or gold; here they are made white with their shading kept, so each one takes its job's colour.
/// Made once per job on first use, freed when the plugin unloads.
/// </summary>
internal static class JobIcons
{
    private static readonly Dictionary<uint, IDalamudTextureWrap?> Made = new();

    public static IDalamudTextureWrap? Get(uint classJob)
    {
        if (classJob == 0) return null;
        if (!Made.TryGetValue(classJob, out var wrap)) Made[classJob] = wrap = Create(classJob);
        return wrap;
    }

    private static IDalamudTextureWrap? Create(uint classJob)
    {
        try
        {
            var id = 62000 + classJob;
            var folder = $"ui/icon/{id / 1000 * 1000:000000}/{id:000000}";
            var tex = Services.Data.GetFile<TexFile>(folder + "_hr1.tex") ?? Services.Data.GetFile<TexFile>(folder + ".tex");
            if (tex == null) return null;
            var bgra = tex.ImageData;
            // The brightest pixel sets the scale, so gold and silver glyphs come out alike.
            var max = 1;
            for (var i = 0; i < bgra.Length; i += 4)
                if (bgra[i + 3] > 16) max = Math.Max(max, bgra[i] + bgra[i + 1] + bgra[i + 2]);
            var white = new byte[bgra.Length];
            for (var i = 0; i < bgra.Length; i += 4)
            {
                var shade = (byte)Math.Clamp((0.62f + 0.38f * (bgra[i] + bgra[i + 1] + bgra[i + 2]) / max) * 255f, 0f, 255f);
                white[i] = white[i + 1] = white[i + 2] = shade;
                white[i + 3] = bgra[i + 3];
            }
            return Services.Textures.CreateFromRaw(RawImageSpecification.Bgra32(tex.Header.Width, tex.Header.Height), white, "Provenance job icon " + classJob);
        }
        catch (Exception e)
        {
            Services.Log.Debug(e, "Couldn't make the icon for job {Job}", classJob);
            return null;
        }
    }

    /// <summary>A job's glyph centred on a point, in the job's colour (a colour dot until it's made).</summary>
    public static void Draw(ImDrawListPtr dl, uint classJob, Vector2 center, float size, float alpha = 1f)
    {
        var color = Palette.WithAlpha(Palette.ClassJobColor(classJob), alpha);
        if (Get(classJob) is { } wrap)
        {
            var half = new Vector2(size / 2);
            dl.AddImage(wrap.Handle, center - half, center + half, Vector2.Zero, Vector2.One, Palette.U32(color));
        }
        else dl.AddCircleFilled(center, MathF.Max(3f, size * 0.2f), Palette.U32(color));
    }

    public static void Dispose()
    {
        foreach (var wrap in Made.Values) wrap?.Dispose();
        Made.Clear();
    }
}
