using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MmoProfiler.Core;

/// <summary>One intent or tone: its id, what it means, and example messages.</summary>
public sealed record LabelDefinition(string Id, string Definition, IReadOnlyList<string> Examples);

/// <summary>The label file is invalid. The message says what to fix.</summary>
public sealed class LabelFileException : Exception
{
    public LabelFileException(string message) : base(message) { }
    public LabelFileException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The parsed label file (spec section 5.5): the intents, tones and flags, the intent used for
/// reactions, aliases for retired ids, and the emoticon table. Immutable. Labels are data, so no label
/// id appears in code; the reaction intent is named by the file itself.
/// </summary>
public sealed class LabelSet
{
    public const int SupportedVersion = 1;
    public const int MaxReactionTextLength = 32;
    public const int MaxPatternLength = 16;

    private static readonly Regex IdPattern = new("^[A-Za-z][A-Za-z0-9]{0,31}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Lazy<LabelSet> DefaultSet = new(LoadDefault);

    private readonly Dictionary<string, string> _intentLookup;   // lower-case id or alias -> canonical id
    private readonly Dictionary<string, string> _toneLookup;
    private readonly Dictionary<string, string> _flagLookup;
    private readonly Dictionary<string, IReadOnlyList<string>> _patternTones; // normal form of a pattern -> canonical tone ids
    private readonly IReadOnlyDictionary<string, string> _intentAliases;
    private readonly IReadOnlyDictionary<string, string> _toneAliases;
    private readonly IReadOnlyDictionary<string, string> _flagAliases;
    private readonly IReadOnlyList<ReactionDefinition> _reactions;

    private LabelSet(
        IReadOnlyList<LabelDefinition> intents, IReadOnlyList<LabelDefinition> tones, IReadOnlyList<LabelDefinition> flags,
        IReadOnlyDictionary<string, string> intentAliases, IReadOnlyDictionary<string, string> toneAliases,
        IReadOnlyDictionary<string, string> flagAliases,
        string reactionIntent, IReadOnlyList<ReactionDefinition> reactions)
    {
        Intents = intents;
        Tones = tones;
        Flags = flags;
        ReactionIntent = reactionIntent;
        _intentAliases = intentAliases;
        _toneAliases = toneAliases;
        _flagAliases = flagAliases;
        _reactions = reactions;
        _intentLookup = BuildLookup(intents, intentAliases);
        _toneLookup = BuildLookup(tones, toneAliases);
        _flagLookup = BuildLookup(flags, flagAliases);

        _patternTones = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var r in reactions)
            foreach (var pattern in r.Patterns)
                _patternTones[NormalForm(pattern)] = r.Tones;

        Fingerprint = ComputeFingerprint();
    }

    /// <summary>Ordered as in the file. Order breaks ties when counts are equal.</summary>
    public IReadOnlyList<LabelDefinition> Intents { get; }

    public IReadOnlyList<LabelDefinition> Tones { get; }

    /// <summary>
    /// Yes/no attributes of a message, independent of intent and tone (for example adult content).
    /// Ordered as in the file; empty when the file has no <c>flags</c>.
    /// </summary>
    public IReadOnlyList<LabelDefinition> Flags { get; }

    /// <summary>
    /// The intent given to reactions. One of <see cref="Intents"/>. Only code assigns it: it is never
    /// offered to the model.
    /// </summary>
    public string ReactionIntent { get; }

    /// <summary>
    /// A stable hash of the file's meaning (ids, definitions, examples, aliases, reactions). Used to tie
    /// evaluation results to the labels they were measured with.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>The built-in label set, from the embedded <c>Labels/default-labels.json</c>.</summary>
    public static LabelSet Default => DefaultSet.Value;

    /// <summary>Trims, matches ignoring case, applies aliases. Returns the canonical id or null.</summary>
    public string? ResolveIntent(string? label) => Resolve(_intentLookup, label);

    /// <summary>Trims, matches ignoring case, applies aliases. Returns the canonical id or null.</summary>
    public string? ResolveTone(string? label) => Resolve(_toneLookup, label);

    /// <summary>Trims, matches ignoring case, applies aliases. Returns the canonical id or null.</summary>
    public string? ResolveFlag(string? label) => Resolve(_flagLookup, label);

    /// <summary>
    /// The normal form used to compare emoticons: lower-case, with every run of the same character
    /// collapsed to one. So <c>:)))</c> and <c>:)</c> are the same, as are <c>xDDD</c> and <c>xD</c>.
    /// </summary>
    public static string NormalForm(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lower = text.ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        foreach (var c in lower)
            if (sb.Length == 0 || sb[^1] != c) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>
    /// Decides whether a message is a reaction (typed emoticons, not game emotes). Null means it is not.
    /// Otherwise the result is the tones to give it, possibly an empty list:
    /// trim (empty or over 32 characters: null); if every whitespace-separated token's normal form is in
    /// the table, the distinct tones in order of first appearance; otherwise, if the text has no letter
    /// and no digit at all, an empty list (an emoticon the table does not know); otherwise null.
    /// </summary>
    public IReadOnlyList<string>? MatchReaction(string? text)
    {
        if (text is null) return null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxReactionTextLength) return null;

        var tones = new List<string>();
        var allKnown = true;
        foreach (var token in trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!_patternTones.TryGetValue(NormalForm(token), out var matched)) { allKnown = false; break; }
            foreach (var tone in matched)
                if (!tones.Contains(tone, StringComparer.Ordinal)) tones.Add(tone);
        }
        if (allKnown) return tones;

        return trimmed.Any(char.IsLetterOrDigit) ? null : Array.Empty<string>();
    }

    private static string? Resolve(Dictionary<string, string> lookup, string? label)
    {
        if (label is null) return null;
        var key = label.Trim();
        return key.Length > 0 && lookup.TryGetValue(key, out var id) ? id : null;
    }

    private static Dictionary<string, string> BuildLookup(
        IReadOnlyList<LabelDefinition> labels, IReadOnlyDictionary<string, string> aliases)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in labels) lookup[l.Id] = l.Id;
        foreach (var (from, to) in aliases) lookup[from] = to;
        return lookup;
    }

    // ---- parsing ----

    /// <exception cref="LabelFileException">The file is not valid; the message says what to fix.</exception>
    public static LabelSet Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json ?? "", new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new LabelFileException("The label file is not valid JSON: " + ex.Message, ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new LabelFileException("The label file must be a JSON object.");

            if (!root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v) || v != SupportedVersion)
                throw new LabelFileException($"The label file must have \"version\": {SupportedVersion}.");

            var intents = ParseLabels(root, "intents", required: true);
            var tones = ParseLabels(root, "tones", required: true);
            var flags = ParseLabels(root, "flags", required: false);

            var intentIds = new HashSet<string>(intents.Select(l => l.Id), StringComparer.OrdinalIgnoreCase);
            var toneIds = new HashSet<string>(tones.Select(l => l.Id), StringComparer.OrdinalIgnoreCase);

            var intentAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var toneAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var flagAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("aliases", out var aliases))
            {
                if (aliases.ValueKind != JsonValueKind.Object)
                    throw new LabelFileException("\"aliases\" must be an object with \"intents\", \"tones\" and \"flags\".");
                ParseAliases(aliases, "intents", intents, intentAliases);
                ParseAliases(aliases, "tones", tones, toneAliases);
                ParseAliases(aliases, "flags", flags, flagAliases);
            }

            if (!root.TryGetProperty("reactionIntent", out var ri) || ri.ValueKind != JsonValueKind.String)
                throw new LabelFileException("\"reactionIntent\" is required and must name one of the intents.");
            var reactionIntent = intents.FirstOrDefault(
                l => string.Equals(l.Id, ri.GetString()?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
                ?? throw new LabelFileException($"\"reactionIntent\" is \"{ri.GetString()}\", which is not one of the intents.");

            var reactions = ParseReactions(root, tones, toneAliases);

            return new LabelSet(intents, tones, flags, intentAliases, toneAliases, flagAliases, reactionIntent, reactions);
        }
    }

    private static List<LabelDefinition> ParseLabels(JsonElement root, string listName, bool required)
    {
        if (!root.TryGetProperty(listName, out var list))
        {
            if (!required) return new List<LabelDefinition>();
            throw new LabelFileException($"The label file needs a \"{listName}\" list.");
        }
        if (list.ValueKind != JsonValueKind.Array)
            throw new LabelFileException($"\"{listName}\" must be a list.");

        var result = new List<LabelDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new LabelFileException($"Every entry in \"{listName}\" must be an object with an \"id\".");
            var id = Str(item, "id");
            if (id is null || !IdPattern.IsMatch(id))
                throw new LabelFileException(
                    $"\"{id}\" in \"{listName}\" is not a valid id. Use a letter followed by up to 31 letters or digits, no spaces.");
            if (id.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                throw new LabelFileException("\"Unknown\" is reserved and cannot be used as a label id.");
            if (!seen.Add(id))
                throw new LabelFileException($"The id \"{id}\" appears more than once in \"{listName}\" (ids are not case sensitive).");

            var examples = new List<string>();
            if (item.TryGetProperty("examples", out var ex))
            {
                if (ex.ValueKind != JsonValueKind.Array)
                    throw new LabelFileException($"\"examples\" of \"{id}\" must be a list of strings.");
                foreach (var e in ex.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.String)
                        throw new LabelFileException($"\"examples\" of \"{id}\" must be a list of strings.");
                    examples.Add(e.GetString()!);
                }
            }
            result.Add(new LabelDefinition(id, Str(item, "definition")?.Trim() ?? "", examples));
        }

        if (required && result.Count == 0)
            throw new LabelFileException($"The label file needs at least one entry in \"{listName}\".");
        return result;
    }

    private static void ParseAliases(
        JsonElement aliases, string listName, IReadOnlyList<LabelDefinition> labels, Dictionary<string, string> into)
    {
        if (!aliases.TryGetProperty(listName, out var map)) return;
        if (map.ValueKind != JsonValueKind.Object)
            throw new LabelFileException($"\"aliases.{listName}\" must be an object mapping old ids to current ids.");

        foreach (var prop in map.EnumerateObject())
        {
            var from = prop.Name.Trim();
            if (from.Length == 0)
                throw new LabelFileException($"An alias in \"aliases.{listName}\" has an empty name.");
            if (labels.Any(l => l.Id.Equals(from, StringComparison.OrdinalIgnoreCase)))
                throw new LabelFileException(
                    $"The alias \"{from}\" in \"aliases.{listName}\" is also a current id. Remove the alias or the label.");

            var target = prop.Value.ValueKind == JsonValueKind.String
                ? labels.FirstOrDefault(l => l.Id.Equals(prop.Value.GetString()?.Trim(), StringComparison.OrdinalIgnoreCase))
                : null;
            if (target is null)
                throw new LabelFileException(
                    $"The alias \"{from}\" in \"aliases.{listName}\" points to \"{prop.Value}\", which is not a current id.");
            if (!into.TryAdd(from, target.Id))
                throw new LabelFileException($"The alias \"{from}\" appears more than once in \"aliases.{listName}\".");
        }
    }

    private static List<ReactionDefinition> ParseReactions(
        JsonElement root, IReadOnlyList<LabelDefinition> tones, Dictionary<string, string> toneAliases)
    {
        var result = new List<ReactionDefinition>();
        if (!root.TryGetProperty("reactions", out var list)) return result;
        if (list.ValueKind != JsonValueKind.Array)
            throw new LabelFileException("\"reactions\" must be a list.");

        // Patterns are compared by normal form, so two that differ only in case or in repeated
        // characters are the same pattern.
        var seenPatterns = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new LabelFileException("Every entry in \"reactions\" must be an object with \"tones\" and \"patterns\".");

            var reactionTones = new List<string>();
            if (item.TryGetProperty("tones", out var toneList) && toneList.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in toneList.EnumerateArray())
                {
                    var name = t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : "";
                    var tone = tones.FirstOrDefault(l => l.Id.Equals(name, StringComparison.OrdinalIgnoreCase))?.Id
                               ?? (toneAliases.TryGetValue(name, out var aliased) ? aliased : null);
                    if (tone is null)
                        throw new LabelFileException($"A reaction uses the tone \"{name}\", which is not in \"tones\".");
                    if (!reactionTones.Contains(tone, StringComparer.Ordinal)) reactionTones.Add(tone);
                }
            }
            else
            {
                throw new LabelFileException("Every entry in \"reactions\" needs a \"tones\" list.");
            }

            var patterns = new List<string>();
            if (item.TryGetProperty("patterns", out var patternList) && patternList.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in patternList.EnumerateArray())
                {
                    var pattern = p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
                    if (pattern.Length == 0)
                        throw new LabelFileException("A reaction pattern is empty.");
                    if (pattern.Length > MaxPatternLength)
                        throw new LabelFileException($"The reaction pattern \"{pattern}\" is longer than {MaxPatternLength} characters.");
                    if (pattern.Any(char.IsWhiteSpace))
                        throw new LabelFileException($"The reaction pattern \"{pattern}\" contains whitespace.");
                    if (!seenPatterns.TryAdd(NormalForm(pattern), pattern))
                        throw new LabelFileException(
                            $"The reaction pattern \"{pattern}\" appears more than once: it is the same as " +
                            $"\"{seenPatterns[NormalForm(pattern)]}\", because patterns ignore case and repeated characters.");
                    patterns.Add(pattern);
                }
            }
            else
            {
                throw new LabelFileException("Every entry in \"reactions\" needs a \"patterns\" list.");
            }

            result.Add(new ReactionDefinition(reactionTones, patterns));
        }
        return result;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static LabelSet LoadDefault()
    {
        const string resource = "MmoProfiler.Core.Labels.default-labels.json";
        using var stream = typeof(LabelSet).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("The embedded default label file is missing: " + resource);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    // ---- fingerprint ----

    private string ComputeFingerprint()
    {
        var sb = new StringBuilder();
        // Length-prefixed fields so no choice of text can make two different files look alike.
        void Add(string s) => sb.Append(s.Length).Append(':').Append(s).Append('|');
        void AddLabels(string name, IReadOnlyList<LabelDefinition> labels)
        {
            Add(name);
            foreach (var l in labels)
            {
                Add(l.Id);
                Add(l.Definition);
                Add(l.Examples.Count.ToString());
                foreach (var e in l.Examples) Add(e);
            }
        }
        void AddAliases(string name, IReadOnlyDictionary<string, string> aliases)
        {
            Add(name);
            foreach (var (from, to) in aliases.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                Add(from.ToLowerInvariant());
                Add(to);
            }
        }

        AddLabels("intents", Intents);
        AddLabels("tones", Tones);
        AddLabels("flags", Flags);
        AddAliases("intentAliases", _intentAliases);
        AddAliases("toneAliases", _toneAliases);
        AddAliases("flagAliases", _flagAliases);
        Add("reactionIntent");
        Add(ReactionIntent);
        Add("reactions");
        foreach (var r in _reactions)
        {
            Add("tones");
            foreach (var t in r.Tones) Add(t);
            Add("patterns");
            foreach (var p in r.Patterns) Add(p);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private sealed record ReactionDefinition(IReadOnlyList<string> Tones, IReadOnlyList<string> Patterns);
}

public static class LabelSetLoader
{
    /// <summary>
    /// With no path or a missing file, returns the default. With an unreadable or invalid file, returns
    /// the default and sets <paramref name="error"/> for the host to show. With a valid file, that file
    /// replaces the default entirely.
    /// </summary>
    public static LabelSet LoadOrDefault(string? overridePath, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(overridePath) || !File.Exists(overridePath))
            return LabelSet.Default;

        try
        {
            return LabelSet.Parse(File.ReadAllText(overridePath, Encoding.UTF8));
        }
        catch (LabelFileException ex)
        {
            error = $"{Path.GetFileName(overridePath)}: {ex.Message} The built-in labels are being used instead.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"{Path.GetFileName(overridePath)} could not be read: {ex.Message} The built-in labels are being used instead.";
        }
        return LabelSet.Default;
    }
}
