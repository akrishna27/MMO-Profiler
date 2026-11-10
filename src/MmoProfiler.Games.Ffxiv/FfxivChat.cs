using MmoProfiler.Core;

namespace MmoProfiler.Games.Ffxiv;

/// <summary>
/// Maps FFXIV chat type codes to <see cref="ChatChannel"/>. Pure: it only knows the numeric codes
/// (checked against Dalamud's XivChatType enum, see decision D22), never the Dalamud type itself.
/// </summary>
public static class FfxivChat
{
    /// <summary>The upper bits of the raw value carry source and target flags.</summary>
    public const int TypeMask = 0x7F;

    public static bool TryMapChannel(int rawChatType, out ChatChannel channel, out string? label)
    {
        label = null;
        int code = rawChatType & TypeMask;

        switch (code)
        {
            case 10: channel = ChatChannel.Say; return true;
            case 11: channel = ChatChannel.Shout; return true;
            case 13: channel = ChatChannel.Tell; return true;      // TellIncoming; 12 (TellOutgoing) is not mapped
            case 14: channel = ChatChannel.Party; return true;
            case 15: channel = ChatChannel.Alliance; return true;
            case 24: channel = ChatChannel.Guild; return true;      // FreeCompany
            case 27: channel = ChatChannel.NoviceNetwork; return true;
            case 30: channel = ChatChannel.Yell; return true;
            case 32: channel = ChatChannel.Party; return true;      // CrossParty
            case 36: channel = ChatChannel.PvpTeam; return true;
            case 37:
                channel = ChatChannel.CrossWorldLinkshell;
                label = "CWLS1";
                return true;
            case >= 16 and <= 23:
                channel = ChatChannel.Linkshell;
                label = "LS" + (code - 15);
                return true;
            case >= 101 and <= 107:
                channel = ChatChannel.CrossWorldLinkshell;
                label = "CWLS" + (code - 99);
                return true;
            default:
                channel = default;
                return false;
        }
    }
}
