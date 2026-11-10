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

/// <summary>
/// What the model (or the emoticon table) made of one message. Intents and tones are canonical label
/// ids from the <see cref="LabelSet"/>, strongest first; either list may be empty. Flags are canonical
/// flag ids with no repeats (yes/no attributes such as adult content); they are independent of
/// intents and tones, and flags alone do not make a message "analysed" (<see cref="HasLabels"/>).
/// </summary>
public sealed record MessageAnalysis(
    IReadOnlyList<string> Intents,
    IReadOnlyList<string> Tones,
    string Language,
    IReadOnlyList<string> Topics)
{
    public static MessageAnalysis Unknown { get; } =
        new(Array.Empty<string>(), Array.Empty<string>(), "und", Array.Empty<string>());

    public IReadOnlyList<string> Flags { get; init; } = Array.Empty<string>();

    public bool HasLabels => Intents.Count > 0 || Tones.Count > 0;
}
