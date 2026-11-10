using MmoProfiler.Core;

namespace MmoProfiler.Games.Ffxiv;

public static class FfxivKeys
{
    public const string GameId = "ffxiv";

    public static CharacterKey Create(string name, string homeWorld) =>
        CharacterKey.Create(GameId, name, homeWorld);
}
