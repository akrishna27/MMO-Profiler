using MmoProfiler.Core;
using MmoProfiler.Games.Ffxiv;

namespace MmoProfiler.Tests;

public class FfxivChatTests
{
    public static TheoryData<int, ChatChannel, string?> TableRows()
    {
        var rows = new TheoryData<int, ChatChannel, string?>
        {
            { 10, ChatChannel.Say, null },
            { 11, ChatChannel.Shout, null },
            { 13, ChatChannel.Tell, null },
            { 14, ChatChannel.Party, null },
            { 15, ChatChannel.Alliance, null },
            { 24, ChatChannel.Guild, null },
            { 27, ChatChannel.NoviceNetwork, null },
            { 30, ChatChannel.Yell, null },
            { 32, ChatChannel.Party, null },
            { 36, ChatChannel.PvpTeam, null },
            { 37, ChatChannel.CrossWorldLinkshell, "CWLS1" },
        };
        for (int i = 0; i < 8; i++)
            rows.Add(16 + i, ChatChannel.Linkshell, $"LS{i + 1}");
        for (int i = 0; i < 7; i++)
            rows.Add(101 + i, ChatChannel.CrossWorldLinkshell, $"CWLS{i + 2}");
        return rows;
    }

    [Theory]
    [MemberData(nameof(TableRows))]
    public void TryMapChannel_MapsEveryRowOfTheTable(int code, ChatChannel expected, string? expectedLabel)
    {
        Assert.True(FfxivChat.TryMapChannel(code, out var channel, out var label));
        Assert.Equal(expected, channel);
        Assert.Equal(expectedLabel, label);
    }

    [Fact]
    public void TryMapChannel_MasksUpperBits()
    {
        // Source and target flags live above the low seven bits.
        Assert.True(FfxivChat.TryMapChannel(10 | 0x0100, out var say, out _));
        Assert.Equal(ChatChannel.Say, say);

        Assert.True(FfxivChat.TryMapChannel(18 | 0x4000 | 0x0080, out var ls, out var label));
        Assert.Equal(ChatChannel.Linkshell, ls);
        Assert.Equal("LS3", label);

        Assert.True(FfxivChat.TryMapChannel(105 | 0x0200, out var cw, out var cwLabel));
        Assert.Equal(ChatChannel.CrossWorldLinkshell, cw);
        Assert.Equal("CWLS6", cwLabel);

        // Masking must not turn an unmapped code into a mapped one or the reverse.
        Assert.False(FfxivChat.TryMapChannel(12 | 0x0100, out _, out _));
    }

    [Theory]
    [InlineData(0)]    // None
    [InlineData(1)]    // Debug
    [InlineData(2)]    // Urgent
    [InlineData(3)]    // Notice
    [InlineData(12)]   // TellOutgoing
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(28)]   // CustomEmote
    [InlineData(29)]   // StandardEmote
    [InlineData(31)]
    [InlineData(41)]   // Damage
    [InlineData(43)]   // Action
    [InlineData(56)]   // Echo
    [InlineData(57)]   // SystemMessage
    [InlineData(61)]   // NPCDialogue
    [InlineData(62)]   // LootNotice
    [InlineData(69)]   // FreeCompanyAnnouncement
    [InlineData(80)]   // GmTell
    [InlineData(86)]   // GmLinkshell1
    [InlineData(94)]   // GmNoviceNetwork
    [InlineData(100)]
    [InlineData(108)]
    [InlineData(127)]
    [InlineData(-1)]   // all bits set masks to 127
    public void TryMapChannel_RejectsEmotesOutgoingTellsAndSystemTypes(int code)
    {
        Assert.False(FfxivChat.TryMapChannel(code, out var channel, out var label));
        Assert.Equal(default, channel);
        Assert.Null(label);
    }

    [Fact]
    public void Linkshells_GetNumberedLabels()
    {
        var labels = new List<string?>();
        for (int code = 16; code <= 23; code++)
        {
            Assert.True(FfxivChat.TryMapChannel(code, out var channel, out var label));
            Assert.Equal(ChatChannel.Linkshell, channel);
            labels.Add(label);
        }
        Assert.Equal(["LS1", "LS2", "LS3", "LS4", "LS5", "LS6", "LS7", "LS8"], labels);

        // Cross-world linkshell 1 is code 37; 2 to 8 are 101 to 107.
        var cw = new List<string?>();
        foreach (int code in new[] { 37, 101, 102, 103, 104, 105, 106, 107 })
        {
            Assert.True(FfxivChat.TryMapChannel(code, out var channel, out var label));
            Assert.Equal(ChatChannel.CrossWorldLinkshell, channel);
            cw.Add(label);
        }
        Assert.Equal(["CWLS1", "CWLS2", "CWLS3", "CWLS4", "CWLS5", "CWLS6", "CWLS7", "CWLS8"], cw);
    }
}
