using System.Text;

namespace MmoProfiler.Games.Ffxiv;

public static class FfxivNames
{
    private const int MaxTotalLength = 20;
    private const int MinWordLength = 2;
    private const int MaxWordLength = 15;

    /// <summary>
    /// Strips leading non-letters (party-number, friend and cross-world glyphs live in the Unicode
    /// private use area), trims and collapses whitespace. Returns null unless the result looks like
    /// a character name: two words of 2 to 15 characters, letters plus apostrophe and hyphen only,
    /// 20 characters in total at most.
    /// </summary>
    public static string? CleanSenderName(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;

        int start = 0;
        while (start < raw.Length && !char.IsLetter(raw[start]))
            start++;

        var sb = new StringBuilder(raw.Length - start);
        bool pendingSpace = false;
        for (int i = start; i < raw.Length; i++)
        {
            char c = raw[i];
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
        }

        string cleaned = sb.ToString();
        return IsPlausibleName(cleaned) ? cleaned : null;
    }

    private static bool IsPlausibleName(string name)
    {
        if (name.Length == 0 || name.Length > MaxTotalLength)
            return false;

        string[] words = name.Split(' ');
        if (words.Length != 2)
            return false;

        foreach (string word in words)
        {
            if (word.Length < MinWordLength || word.Length > MaxWordLength)
                return false;
            foreach (char c in word)
            {
                if (!char.IsLetter(c) && c != '\'' && c != '-')
                    return false;
            }
        }
        return true;
    }
}
