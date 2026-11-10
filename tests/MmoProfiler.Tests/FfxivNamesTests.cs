using MmoProfiler.Games.Ffxiv;

namespace MmoProfiler.Tests;

public class FfxivNamesTests
{
    [Theory]
    [InlineData("Alice Example", "Alice Example")]            // party number glyph
    [InlineData("Alice Example", "Alice Example")]            // cross-world icon
    [InlineData("  Alice Example", "Alice Example")]    // several glyphs and spaces
    [InlineData("1 Alice Example", "Alice Example")]                // digit prefix
    public void CleanSenderName_StripsLeadingIconGlyphs(string raw, string expected)
    {
        Assert.Equal(expected, FfxivNames.CleanSenderName(raw));
    }

    [Theory]
    [InlineData("Alice Example")]
    [InlineData("Yshtola Rhul")]
    public void CleanSenderName_PlainName_Unchanged(string raw)
    {
        Assert.Equal(raw, FfxivNames.CleanSenderName(raw));
    }

    [Fact]
    public void CleanSenderName_TrimsAndCollapsesWhitespace()
    {
        Assert.Equal("Alice Example", FfxivNames.CleanSenderName("  Alice \t  Example  "));
    }

    [Theory]
    [InlineData("O'Neil Smith-Jones", "O'Neil Smith-Jones")]
    [InlineData("Y'shtola Rhul", "Y'shtola Rhul")]
    [InlineData("Mary-Ann Lee", "Mary-Ann Lee")]
    public void CleanSenderName_AllowsApostropheAndHyphen(string raw, string expected)
    {
        Assert.Equal(expected, FfxivNames.CleanSenderName(raw));
    }

    [Theory]
    [InlineData("Alice")]                         // one word
    [InlineData("Alice Example Third")]           // three words
    [InlineData("Alice Exam9ple")]                // digit inside
    [InlineData("Alice Ex@mple")]                 // other symbol
    [InlineData("A Example")]                     // word under 2 characters
    [InlineData("Alicealicealicealice Bob")]      // word over 15 characters and total over 20
    [InlineData("Abcdefghijkl Mnopqrstuvw")]      // each word fine, 23 combined is over 20
    [InlineData("Alice Example")]           // trailing glyph is not stripped
    [InlineData("")]                  // nothing left
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CleanSenderName_Implausible_ReturnsNull(string? raw)
    {
        Assert.Null(FfxivNames.CleanSenderName(raw));
    }

    [Fact]
    public void CleanSenderName_TotalLengthBoundary()
    {
        // The combined limit is 20 characters and does not count the space between the two names.
        Assert.Equal("Abcdefghij Klmnopqrs", FfxivNames.CleanSenderName("Abcdefghij Klmnopqrs"));   // 10 + 9
        Assert.Equal("Abcdefghij Klmnopqrst", FfxivNames.CleanSenderName("Abcdefghij Klmnopqrst")); // 10 + 10
        Assert.Equal("Abcdefghijklmno Pqrst", FfxivNames.CleanSenderName("Abcdefghijklmno Pqrst")); // 15 + 5
        Assert.Null(FfxivNames.CleanSenderName("Abcdefghij Klmnopqrstu"));                           // 10 + 11
    }

    [Fact]
    public void Keys_Create_UsesFfxivGameId()
    {
        var key = FfxivKeys.Create("Alice Example", "Odin");
        Assert.Equal("ffxiv:Alice Example@Odin", key.ToString());
    }
}
