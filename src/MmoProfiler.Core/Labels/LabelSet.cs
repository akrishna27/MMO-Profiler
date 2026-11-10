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
/// The parsed label file (spec section 6.7): the intents and tones, the intent used for emote-only
/// messages, aliases for retired ids, and the emoticon table. Immutable. Labels are data, so no label
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
    private readonly Dictionary<string, IReadOnlyList<string>> _patternTones; // pattern (ignoring case) -> canonical tone ids
    private readonly IReadOnlyDictionary<string, string> _intentAliases;
    private readonly IReadOnlyDictionary<string, string> _toneAliases;
    private readonly IReadOnlyList<ReactionDefinition> _reactions;

    private LabelSet(
        IReadOnlyList<LabelDefinition> intents, IReadOnlyList<LabelDefinition> tones,
        IReadOnlyDictionary<string, string> intentAliases, IReadOnlyDictionary<string, string> toneAliases,
        string reactionIntent, IReadOnlyList<ReactionDefinition> reactions)
    {
        Intents = intents;
        Tones = tones;
        ReactionIntent = reactionIntent;
        _intentAliases = intentAliases;
        _toneAliases = toneAliases;
        _reactions = reactions;
        _intentLookup = BuildLookup(intents, intentAliases);
        _toneLookup = BuildLookup(tones, toneAliases);

        _patternTones = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in reactions)
            foreach (var pattern in r.Patterns)
                _patternTones[pattern] = r.Tones;

        Fingerprint = ComputeFingerprint();
    }

    /// <summary>Ordered as in the file. Order breaks ties when counts are equal.</summary>
    public IReadOnlyList<LabelDefinition> Intents { get; }

    public IReadOnlyList<LabelDefinition> Tones { get; }

    /// <summary>The intent given to emote-only messages. One of <see cref="Intents"/>.</summary>
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

    /// <summary>
    /// If the whole message is made of known emoticons (typed text, not game emotes), returns their
    /// distinct tones in order of first appearance. Otherwise null.
    /// </summary>
    public IReadOnlyList<string>? MatchReaction(string? text)
    {
        if (text is null) return null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxReactionTextLength) return null;

        var tones = new List<string>();
        foreach (var token in trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!_patternTones.TryGetValue(token, out var matched)) return null;
            foreach (var tone in matched)
                if (!tones.Contains(tone, StringComparer.Ordinal)) tones.Add(tone);
        }
        return tones;
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

            var intents = ParseLabels(root, "intents");
            var tones = ParseLabels(root, "tones");

            var intentIds = new HashSet<string>(intents.Select(l => l.Id), StringComparer.OrdinalIgnoreCase);
            var toneIds = new HashSet<string>(tones.Select(l => l.Id), StringComparer.OrdinalIgnoreCase);

            var intentAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var toneAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("aliases", out var aliases))
            {
                if (aliases.ValueKind != JsonValueKind.Object)
                    throw new LabelFileException("\"aliases\" must be an object with \"intents\" and \"tones\".");
                ParseAliases(aliases, "intents", intents, intentAliases);
                ParseAliases(aliases, "tones", tones, toneAliases);
            }

            if (!root.TryGetProperty("reactionIntent", out var ri) || ri.ValueKind != JsonValueKind.String)
                throw new LabelFileException("\"reactionIntent\" is required and must name one of the intents.");
            var reactionIntent = intents.FirstOrDefault(
                l => string.Equals(l.Id, ri.GetString()?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
                ?? throw new LabelFileException($"\"reactionIntent\" is \"{ri.GetString()}\", which is not one of the intents.");

            var reactions = ParseReactions(root, tones, toneAliases);

            return new LabelSet(intents, tones, intentAliases, toneAliases, reactionIntent, reactions);
        }
    }

    private static List<LabelDefinition> ParseLabels(JsonElement root, string listName)
    {
        if (!root.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array)
            throw new LabelFileException($"The label file needs a \"{listName}\" list.");

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

        if (result.Count == 0)
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

        var seenPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    if (!seenPatterns.Add(pattern))
                        throw new LabelFileException($"The reaction pattern \"{pattern}\" appears more than once (patterns are not case sensitive).");
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
        AddAliases("intentAliases", _intentAliases);
        AddAliases("toneAliases", _toneAliases);
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
