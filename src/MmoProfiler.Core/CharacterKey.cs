using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MmoProfiler.Core;

/// <summary>Identifies a character by game, name and home realm. Never contains account or content IDs.</summary>
public sealed record CharacterKey(string GameId, string Name, string Realm)
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static CharacterKey Create(string gameId, string name, string realm)
    {
        var g = Clean(gameId, nameof(gameId)).ToLowerInvariant();
        var n = Whitespace.Replace(Clean(name, nameof(name)), " ");
        var r = Clean(realm, nameof(realm));
        return new CharacterKey(g, n, r);
    }

    private static string Clean(string? value, string paramName)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException("Value must not be empty or whitespace.", paramName);
        return trimmed;
    }

    /// <summary>Storage key: the lower-cased <see cref="ToString"/>.</summary>
    public string Id => ToString().ToLowerInvariant();

    public bool Equals(CharacterKey? other) =>
        other is not null
        && string.Equals(GameId, other.GameId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Realm, other.Realm, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(GameId, StringComparer.OrdinalIgnoreCase);
        h.Add(Name, StringComparer.OrdinalIgnoreCase);
        h.Add(Realm, StringComparer.OrdinalIgnoreCase);
        return h.ToHashCode();
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{GameId}:{Name}@{Realm}");

    /// <summary>Reverses <see cref="ToString"/>. Splits on the first ':' and the last '@'.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out CharacterKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var colon = text.IndexOf(':');
        var at = text.LastIndexOf('@');
        if (colon <= 0 || at <= colon + 1 || at >= text.Length - 1) return false;

        try
        {
            key = Create(text[..colon], text[(colon + 1)..at], text[(at + 1)..]);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
