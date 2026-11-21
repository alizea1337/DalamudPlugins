using Provenance.Core.Appearance;
using Xunit;

namespace Provenance.Tests;

public class ThemeColorsTests
{
    [Fact]
    public void FirstColoursComeOutAsTheyAlwaysWere()
    {
        var t = ThemeColors.Default;
        Assert.Equal(0x171A21u, t.Window);
        Assert.Equal(0x1F232Cu, t.Surface);
        Assert.Equal(0x282D38u, t.Raised);
        Assert.Equal(0x313744u, t.RaisedHover);
        Assert.Equal(0xE7EAF0u, t.Text);
        Assert.Equal(0x9AA2B1u, t.TextMuted);
        Assert.Equal(0x6B7383u, t.TextFaint);
        Assert.Equal(0x86D0C2u, t.Accent);
        Assert.Equal(0x9CDCD0u, t.AccentHover);
        Assert.True(ThemeColors.Contrast(t.OnAccent, t.Accent) >= 7);
    }

    [Fact]
    public void LightCardsWithDarkTextShadeTheOtherWay()
    {
        var t = ThemeColors.From(0x2F6FDB, 0xEEF0F3, 0xFFFFFF, 0x1E2128);
        Assert.True(ThemeColors.Luminance(t.Raised) < ThemeColors.Luminance(t.Surface), "fields are darker than light cards");
        Assert.True(ThemeColors.Luminance(t.RaisedHover) < ThemeColors.Luminance(t.Raised));
        Assert.True(ThemeColors.Luminance(t.TextMuted) > ThemeColors.Luminance(t.Text), "muted text is lighter than dark text");
        Assert.True(ThemeColors.Luminance(t.TextFaint) > ThemeColors.Luminance(t.TextMuted));
    }

    [Theory]
    [InlineData(0x86D0C2u)] // pale
    [InlineData(0x1D3D8Fu)] // deep blue
    [InlineData(0xFFFF00u)] // bright yellow
    [InlineData(0x000000u)]
    [InlineData(0xFFFFFFu)]
    public void TextOnTheAccentReads(uint accent)
        => Assert.True(ThemeColors.Contrast(ThemeColors.OnAccentFor(accent), accent) >= ThemeColors.ReadableContrast);

    [Fact]
    public void BrightAccentDarkensOnHover()
    {
        var t = ThemeColors.From(0xFFFFFF, ThemeColors.DefaultBackground, ThemeColors.DefaultCards, ThemeColors.DefaultText);
        Assert.NotEqual(t.Accent, t.AccentHover);
        Assert.True(ThemeColors.Luminance(t.AccentHover) < ThemeColors.Luminance(t.Accent));
    }

    [Fact]
    public void ScrimDarkensTheBackground()
        => Assert.True(ThemeColors.Luminance(ThemeColors.Default.Scrim) < ThemeColors.Luminance(ThemeColors.Default.Window));

    [Theory]
    [InlineData("#86D0C2", 0x86D0C2u)]
    [InlineData("86d0c2", 0x86D0C2u)]
    [InlineData("  #86D0C2 ", 0x86D0C2u)]
    [InlineData("8dc", 0x88DDCCu)]
    [InlineData("#FFF", 0xFFFFFFu)]
    public void ReadsHexCodes(string text, uint expected)
    {
        Assert.True(ThemeColors.TryParseHex(text, out var rgb));
        Assert.Equal(expected, rgb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("GG0000")]
    [InlineData("#12 456")]
    [InlineData(null)]
    public void RejectsWhatIsNoColour(string? text) => Assert.False(ThemeColors.TryParseHex(text, out _));

    [Fact]
    public void WritesHexCodes()
    {
        Assert.Equal("#86D0C2", ThemeColors.ToHex(0x86D0C2));
        Assert.Equal("#00000A", ThemeColors.ToHex(0x0A));
        Assert.Equal("#123456", ThemeColors.ToHex(0xFF123456));
    }

    [Fact]
    public void ContrastRunsFromOneToTwentyOne()
    {
        Assert.Equal(21, ThemeColors.Contrast(0x000000, 0xFFFFFF), 3);
        Assert.Equal(1, ThemeColors.Contrast(0x777777, 0x777777), 3);
        Assert.True(ThemeColors.Contrast(ThemeColors.DefaultText, ThemeColors.DefaultCards) >= ThemeColors.ReadableContrast);
        Assert.True(ThemeColors.Contrast(ThemeColors.DefaultAccent, ThemeColors.DefaultCards) >= ThemeColors.VisibleContrast);
    }

    [Fact]
    public void StepsStayWithinRange()
    {
        Assert.Equal(0xFFFFFFu, ThemeColors.Step(0xF8F8F8, 0x101010, 1));
        Assert.Equal(0x000000u, ThemeColors.Step(0x080808, 0x101010, -1));
    }
}
