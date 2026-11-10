using MmoProfiler.Core;

namespace MmoProfiler.Tests;

internal static class TestData
{
    public static CharacterKey Key(string name = "Alice Example", string world = "Odin") =>
        CharacterKey.Create("ffxiv", name, world);

    public static ChatMessage Msg(
        CharacterKey sender, string text = "hello", ChatChannel channel = ChatChannel.Say,
        DateTimeOffset? at = null, string? label = null) =>
        new(sender, channel, label, text, at ?? new DateTimeOffset(2026, 11, 7, 21, 30, 0, TimeSpan.Zero));

    public static Func<ProfilerOptions> Opts(ProfilerOptions options) => () => options;
}
