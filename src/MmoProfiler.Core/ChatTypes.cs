namespace MmoProfiler.Core;

public enum ChatChannel
{
    Say, Yell, Shout, Tell, Party,
    Linkshell, CrossWorldLinkshell,
    // Supported by the model but off by default:
    Guild, Alliance, NoviceNetwork, PvpTeam
}

public sealed record ChatMessage(
    CharacterKey Sender,
    ChatChannel Channel,
    string? ChannelLabel,
    string Text,
    DateTimeOffset TimestampUtc);

public enum Tone
{
    Unknown, Neutral, Friendly, Humorous, Excited, Formal,
    Sarcastic, Frustrated, Hostile, Sad, Flirtatious
}

public enum Intent
{
    Unknown, Socializing, Roleplay, Recruiting, LookingForGroup, Trading,
    Advertising, HelpRequest, HelpOffer, Coordination, Spam, Other
}

public sealed record MessageAnalysis(
    Tone Tone, Intent Intent, string Language, IReadOnlyList<string> Topics)
{
    public static MessageAnalysis Unknown { get; } =
        new(Tone.Unknown, Intent.Unknown, "und", Array.Empty<string>());
}
