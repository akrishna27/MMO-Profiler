using MmoProfiler.Core;

namespace MmoProfiler.Tests;

public class CharacterKeyTests
{
    [Fact]
    public void Create_TrimsAndCollapsesWhitespace()
    {
        var key = CharacterKey.Create("  FFXIV ", "  Alisaie \t  Leveilleur ", "  Twintania ");

        Assert.Equal("ffxiv", key.GameId);
        Assert.Equal("Alisaie Leveilleur", key.Name);
        Assert.Equal("Twintania", key.Realm);
    }

    [Theory]
    [InlineData("", "Name", "Realm")]
    [InlineData("   ", "Name", "Realm")]
    [InlineData("ffxiv", "", "Realm")]
    [InlineData("ffxiv", "  ", "Realm")]
    [InlineData("ffxiv", "Name", "")]
    [InlineData("ffxiv", "Name", "\t ")]
    public void Create_EmptyPart_Throws(string game, string name, string realm)
    {
        Assert.Throws<ArgumentException>(() => CharacterKey.Create(game, name, realm));
    }

    [Fact]
    public void Equality_IsCaseInsensitive()
    {
        var a = CharacterKey.Create("ffxiv", "Alisaie Leveilleur", "Twintania");
        var b = new CharacterKey("FFXIV", "ALISAIE leveilleur", "TWINTANIA");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, CharacterKey.Create("ffxiv", "Alisaie Leveilleur", "Odin"));
        Assert.Equal(a.Id, CharacterKey.Create("FFXIV", "alisaie leveilleur", "twintania").Id);
    }

    [Fact]
    public void ToString_And_TryParse_RoundTrip()
    {
        var key = CharacterKey.Create("ffxiv", "Y'shtola Rhul", "Twintania");

        Assert.Equal("ffxiv:Y'shtola Rhul@Twintania", key.ToString());
        Assert.Equal("ffxiv:y'shtola rhul@twintania", key.Id);
        Assert.True(CharacterKey.TryParse(key.ToString(), out var parsed));
        Assert.Equal(key, parsed);
        Assert.Equal(key.Name, parsed!.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no separators")]
    [InlineData("ffxiv:NameOnly")]
    [InlineData("Name@Realm")]
    [InlineData(":Name@Realm")]
    [InlineData("ffxiv:@Realm")]
    [InlineData("ffxiv:Name@")]
    [InlineData("ffxiv:   @Realm")]
    [InlineData("Name@Realm:ffxiv")]
    public void TryParse_Malformed_ReturnsFalse(string? text)
    {
        Assert.False(CharacterKey.TryParse(text, out var key));
        Assert.Null(key);
    }
}
