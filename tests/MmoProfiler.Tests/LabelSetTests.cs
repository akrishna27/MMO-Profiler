using MmoProfiler.Core;

namespace MmoProfiler.Tests;

public class LabelSetTests
{
    // A small valid file to mutate in the rejection tests.
    private const string Minimal = """
        {
          "version": 1,
          "intents": [
            { "id": "Chat", "definition": "Talking.", "examples": ["hi"] },
            { "id": "Reaction", "definition": "Emoticon.", "examples": [] }
          ],
          "tones": [
            { "id": "Warm", "definition": "Kind.", "examples": [] },
            { "id": "Plain", "definition": "Flat.", "examples": [] }
          ],
          "aliases": { "intents": { "Socializing": "Chat" }, "tones": { "Friendly": "Warm" } },
          "reactionIntent": "Reaction",
          "reactions": [
            { "tones": ["Warm"], "patterns": [":3", ":)"] },
            { "tones": ["Plain"], "patterns": ["x.x"] }
          ]
        }
        """;

    private static string Json(string intents, string tones, string aliases = "{}", string reactionIntent = "Reaction", string reactions = "[]") =>
        $$"""
        {"version":1,"intents":{{intents}},"tones":{{tones}},"aliases":{{aliases}},"reactionIntent":"{{reactionIntent}}","reactions":{{reactions}}}
        """;

    private static string Label(string id) => $$"""{"id":"{{id}}","definition":"d","examples":[]}""";
    private static string Labels(params string[] ids) => "[" + string.Join(",", ids.Select(Label)) + "]";

    private static readonly string OneTone = Labels("Warm");

    private static string WithReactionPatterns(params string[] patterns) =>
        Json(Labels("Chat", "Reaction"), OneTone,
            reactions: "[{\"tones\":[\"Warm\"],\"patterns\":[" + string.Join(",", patterns.Select(p => System.Text.Json.JsonSerializer.Serialize(p))) + "]}]");

    // ---- the default file ----

    [Fact]
    public void Default_LoadsAndContainsEveryStartingLabelAndReaction()
    {
        var set = LabelSet.Default;

        Assert.Equal(
            new[]
            {
                "Socializing", "Roleplay", "Recruiting", "LookingForGroup", "Trading", "Advertising",
                "HelpRequest", "HelpOffer", "Coordination", "Spam", "Reaction", "Other",
            },
            set.Intents.Select(l => l.Id));
        Assert.Equal(
            new[] { "Neutral", "Friendly", "Humorous", "Excited", "Formal", "Sarcastic", "Frustrated", "Hostile", "Sad", "Flirtatious" },
            set.Tones.Select(l => l.Id));
        Assert.Equal("Reaction", set.ReactionIntent);
        Assert.Equal(["Adult"], set.Flags.Select(l => l.Id));
        Assert.Equal("Sexually explicit content.", set.Flags[0].Definition);
        Assert.Empty(set.Flags[0].Examples);
        Assert.All(set.Intents.Concat(set.Tones).Concat(set.Flags), l => Assert.False(string.IsNullOrWhiteSpace(l.Definition), l.Id));
        Assert.Empty(set.Intents.Single(l => l.Id == "Other").Examples);
        Assert.Equal(["lol how was your weekend"], set.Intents.Single(l => l.Id == "Socializing").Examples);

        // Every pattern of the starting table, with the tone it must give. No two patterns share a
        // normal form, so "!!" stands for any run of "!", "?" for any run of "?" and "..." for any run of dots.
        var expected = new Dictionary<string, string[]>
        {
            ["Friendly"] = [":)", ":-)", "(:", ":3", "^^", "^_^", "^-^", "o/", "\\o", "<3", "uwu", "owo", "o7"],
            ["Humorous"] = ["xD", ":P", ";P", "lol", "lmao", "haha", "kek"],
            ["Excited"] = [":D", "\\o/", "!!"],
            ["Sad"] = [":(", ":'(", ":c", "T_T", "T.T", ";_;", "D:"],
            ["Frustrated"] = ["x.x", "x_x", ">.<", ">_<", "-_-", "-.-", "ugh"],
            ["Flirtatious"] = [";)", ";-)", ":*"],
            ["Neutral"] = ["?", "...", "o.o", "o_o", "._."],
        };
        Assert.Equal(
            expected.Values.Sum(v => v.Length),
            expected.Values.SelectMany(v => v).Select(LabelSet.NormalForm).Distinct().Count());
        foreach (var (tone, patterns) in expected)
            foreach (var pattern in patterns)
                Assert.Equal([tone], set.MatchReaction(pattern));

        Assert.Equal(7 + 0, expected.Count);
        Assert.Equal(set.Tones.Count - 3, expected.Count); // Formal, Sarcastic and Hostile have no emoticons
    }

    // ---- validation ----

    [Fact]
    public void Parse_AcceptsAMinimalValidFile()
    {
        var set = LabelSet.Parse(Minimal);

        Assert.Equal(["Chat", "Reaction"], set.Intents.Select(l => l.Id));
        Assert.Equal("Reaction", set.ReactionIntent);
    }

    [Fact]
    public void Parse_RejectsDuplicateIds_IgnoringCase()
    {
        var ex1 = Assert.Throws<LabelFileException>(() => LabelSet.Parse(Json(Labels("Chat", "chat", "Reaction"), OneTone)));
        Assert.Contains("more than once", ex1.Message);
        Assert.Throws<LabelFileException>(() => LabelSet.Parse(Json(Labels("Chat", "Reaction"), Labels("Warm", "WARM"))));

        // The same id in the intent list and the tone list is allowed: they are separate lists.
        LabelSet.Parse(Json(Labels("Chat", "Reaction"), Labels("Chat")));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("unknown")]
    [InlineData("UNKNOWN")]
    public void Parse_RejectsReservedUnknownId(string id)
    {
        var inIntents = Assert.Throws<LabelFileException>(() => LabelSet.Parse(Json(Labels("Chat", "Reaction", id), OneTone)));
        Assert.Contains("reserved", inIntents.Message);
        Assert.Throws<LabelFileException>(() => LabelSet.Parse(Json(Labels("Chat", "Reaction"), Labels("Warm", id))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1Chat")]
    [InlineData("Chat Room")]
    [InlineData("Chat-Room")]
    [InlineData("Chat_Room")]
    [InlineData("Caf\u00e9")]
    [InlineData("A12345678901234567890123456789012")] // 33 characters
    public void Parse_RejectsInvalidIdCharacters(string id)
    {
        var ex = Assert.Throws<LabelFileException>(() => LabelSet.Parse(Json(Labels("Chat", "Reaction", id), OneTone)));
        Assert.Contains("not a valid id", ex.Message);
    }

    [Fact]
    public void Parse_AcceptsIdsAtTheLengthLimit()
    {
        var longest = "A" + new string('b', 31); // 32 characters
        LabelSet.Parse(Json(Labels("Chat", "Reaction", longest), OneTone));
    }

    [Theory]
    [InlineData("[]", "[{\"id\":\"Warm\"}]")]
    [InlineData("[{\"id\":\"Chat\"},{\"id\":\"Reaction\"}]", "[]")]
    public void Parse_RejectsEmptyIntentOrToneList(string intents, string tones)
    {
        var ex = Assert.Throws<LabelFileException>(() =>
            LabelSet.Parse(Json(intents, tones, reactionIntent: "Reaction")));
        Assert.Contains("at least one", ex.Message);
    }

    [Fact]
    public void Parse_RejectsAliasToMissingLabel_AndAliasThatIsACurrentId()
    {
        var toMissing = Assert.Throws<LabelFileException>(() => LabelSet.Parse(
            Json(Labels("Chat", "Reaction"), OneTone, aliases: "{\"intents\":{\"Old\":\"Nowhere\"},\"tones\":{}}")));
        Assert.Contains("not a current id", toMissing.Message);

        var toMissingTone = Assert.Throws<LabelFileException>(() => LabelSet.Parse(
            Json(Labels("Chat", "Reaction"), OneTone, aliases: "{\"intents\":{},\"tones\":{\"Old\":\"Nowhere\"}}")));
        Assert.Contains("not a current id", toMissingTone.Message);

        // The key "Chat" is itself a current id (case does not matter).
        var isCurrent = Assert.Throws<LabelFileException>(() => LabelSet.Parse(
            Json(Labels("Chat", "Reaction"), OneTone, aliases: "{\"intents\":{\"chat\":\"Reaction\"},\"tones\":{}}")));
        Assert.Contains("also a current id", isCurrent.Message);
        Assert.Throws<LabelFileException>(() => LabelSet.Parse(
            Json(Labels("Chat", "Reaction"), Labels("Warm", "Plain"), aliases: "{\"intents\":{},\"tones\":{\"Plain\":\"Warm\"}}")));

        // The target is checked against the right list: a tone id is not an intent.
        Assert.Throws<LabelFileException>(() => LabelSet.Parse(
            Json(Labels("Chat", "Reaction"), OneTone, aliases: "{\"intents\":{\"Old\":\"Warm\"},\"tones\":{}}")));
    }

    [Fact]
    public void Parse_RejectsReactionIntentNotInIntents()
    {
        var ex = Assert.Throws<LabelFileException>(() => LabelSet.Parse(
            Json(Labels("Chat", "Other"), OneTone, reactionIntent: "Reaction")));
        Assert.Contains("reactionIntent", ex.Message);

        Assert.Throws<LabelFileException>(() => LabelSet.Parse(
            """{"version":1,"intents":[{"id":"Chat"}],"tones":[{"id":"Warm"}]}"""));
    }

    [Fact]
    public void Parse_RejectsReactionWithUnknownTone()
    {
        var ex = Assert.Throws<LabelFileException>(() => LabelSet.Parse(
            Json(Labels("Chat", "Reaction"), OneTone, reactions: "[{\"tones\":[\"Grumpy\"],\"patterns\":[\":3\"]}]")));
        Assert.Contains("Grumpy", ex.Message);
    }

    [Theory]
    [InlineData(":3", ":3")]                                  // exact duplicate
    [InlineData("xD", "XD")]                                  // duplicate ignoring case
    [InlineData("", "ok")]                                    // empty
    [InlineData("a b", "ok")]                                 // contains whitespace
    [InlineData("a\tb", "ok")]
    [InlineData("12345678901234567", "ok")]                   // 17 characters
    public void Parse_RejectsDuplicateOrInvalidReactionPattern(string first, string second)
    {
        Assert.Throws<LabelFileException>(() => LabelSet.Parse(WithReactionPatterns(first, second)));
    }

    [Fact]
    public void Parse_RejectsAPatternRepeatedInAnotherReactionEntry()
    {
        var ex = Assert.Throws<LabelFileException>(() => LabelSet.Parse(Json(
            Labels("Chat", "Reaction"), Labels("Warm", "Plain"),
            reactions: "[{\"tones\":[\"Warm\"],\"patterns\":[\":3\"]},{\"tones\":[\"Plain\"],\"patterns\":[\":3\"]}]")));
        Assert.Contains("more than once", ex.Message);
    }

    [Theory]
    [InlineData(":)", ":))")]          // a repeated character
    [InlineData("xD", "XDD")]          // case and a repeat together
    [InlineData("!!", "!")]            // the shortest and a longer run
    [InlineData("lol", "LOOOL")]
    [InlineData("...", "..")]
    public void Parse_RejectsPatternsWithTheSameNormalForm(string first, string second)
    {
        var ex = Assert.Throws<LabelFileException>(() => LabelSet.Parse(WithReactionPatterns(first, second)));
        Assert.Contains("more than once", ex.Message);

        // The same across two reaction entries.
        Assert.Throws<LabelFileException>(() => LabelSet.Parse(Json(
            Labels("Chat", "Reaction"), Labels("Warm", "Plain"),
            reactions: "[{\"tones\":[\"Warm\"],\"patterns\":[" + System.Text.Json.JsonSerializer.Serialize(first)
                       + "]},{\"tones\":[\"Plain\"],\"patterns\":[" + System.Text.Json.JsonSerializer.Serialize(second) + "]}]")));

        // Patterns that differ in more than that are fine.
        LabelSet.Parse(WithReactionPatterns(first, "ok"));
    }

    [Fact]
    public void Parse_AcceptsAPatternAtTheLengthLimit()
    {
        LabelSet.Parse(WithReactionPatterns("1234567890123456")); // 16 characters
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"intents\":[],\"tones\":[]}")]
    public void Parse_RejectsMalformedOrUnversionedFiles(string json)
    {
        Assert.Throws<LabelFileException>(() => LabelSet.Parse(json));
    }

    // ---- flags ----

    private static readonly string MinimalWithFlags = Minimal
        .Replace("\"reactionIntent\": \"Reaction\",",
            "\"flags\": [{ \"id\": \"Adult\", \"definition\": \"Explicit.\", \"examples\": [\"x\"] }, { \"id\": \"Rude\", \"definition\": \"Rude.\" }],\n  \"reactionIntent\": \"Reaction\",")
        .Replace("\"tones\": { \"Friendly\": \"Warm\" } }", "\"tones\": { \"Friendly\": \"Warm\" }, \"flags\": { \"Explicit\": \"Adult\" } }");

    private static string JsonWithFlags(string flags, string aliases = "{}") =>
        $$"""
        {"version":1,"intents":{{Labels("Chat", "Reaction")}},"tones":{{OneTone}},"flags":{{flags}},"aliases":{{aliases}},"reactionIntent":"Reaction","reactions":[]}
        """;

    [Fact]
    public void Parse_FlagsAreOptional_AndValidatedLikeLabels()
    {
        // No "flags" at all is valid and means no flags.
        Assert.Empty(LabelSet.Parse(Minimal).Flags);
        Assert.Empty(LabelSet.Parse(JsonWithFlags("[]")).Flags);

        var set = LabelSet.Parse(MinimalWithFlags);
        Assert.Equal(["Adult", "Rude"], set.Flags.Select(l => l.Id));
        Assert.Equal("Explicit.", set.Flags[0].Definition);
        Assert.Equal(["x"], set.Flags[0].Examples);

        // The same rules as intents and tones.
        var dup = Assert.Throws<LabelFileException>(() => LabelSet.Parse(JsonWithFlags(Labels("Adult", "adult"))));
        Assert.Contains("more than once", dup.Message);
        var reserved = Assert.Throws<LabelFileException>(() => LabelSet.Parse(JsonWithFlags(Labels("Unknown"))));
        Assert.Contains("reserved", reserved.Message);
        var badId = Assert.Throws<LabelFileException>(() => LabelSet.Parse(JsonWithFlags(Labels("Not Valid"))));
        Assert.Contains("not a valid id", badId.Message);
        Assert.Throws<LabelFileException>(() => LabelSet.Parse(JsonWithFlags("\"Adult\"")));      // not a list
        Assert.Throws<LabelFileException>(() => LabelSet.Parse(JsonWithFlags("[\"Adult\"]")));    // entries must be objects

        // An id may repeat across lists: they are separate lists.
        LabelSet.Parse(JsonWithFlags(Labels("Chat")));

        // Flag aliases: the target must be a flag, and the key must not be a current flag id.
        var toMissing = Assert.Throws<LabelFileException>(() => LabelSet.Parse(
            JsonWithFlags(Labels("Adult"), "{\"flags\":{\"Old\":\"Nowhere\"}}")));
        Assert.Contains("not a current id", toMissing.Message);
        Assert.Throws<LabelFileException>(() => LabelSet.Parse(
            JsonWithFlags(Labels("Adult"), "{\"flags\":{\"Old\":\"Chat\"}}"))); // an intent id is not a flag
        var isCurrent = Assert.Throws<LabelFileException>(() => LabelSet.Parse(
            JsonWithFlags(Labels("Adult", "Rude"), "{\"flags\":{\"rude\":\"Adult\"}}")));
        Assert.Contains("also a current id", isCurrent.Message);
        Assert.Throws<LabelFileException>(() => LabelSet.Parse( // flag aliases need flags to point at
            Json(Labels("Chat", "Reaction"), OneTone, aliases: "{\"flags\":{\"Old\":\"Adult\"}}")));
    }

    [Fact]
    public void ResolveFlag_IsCaseInsensitive_AndAppliesAliases()
    {
        var set = LabelSet.Parse(MinimalWithFlags);

        Assert.Equal("Adult", set.ResolveFlag("adult"));
        Assert.Equal("Adult", set.ResolveFlag("  ADULT \n"));
        Assert.Equal("Adult", set.ResolveFlag("explicit"));      // alias, any case
        Assert.Equal("Rude", set.ResolveFlag("rUdE"));
        Assert.Null(set.ResolveFlag("Chat"));                    // an intent id is not a flag
        Assert.Null(set.ResolveFlag("Warm"));
        Assert.Null(set.ResolveFlag("Unknown"));
        Assert.Null(set.ResolveFlag(""));
        Assert.Null(set.ResolveFlag(null));
        Assert.Null(set.ResolveIntent("Adult"));                 // and a flag id is not an intent
        Assert.Null(LabelSet.Parse(Minimal).ResolveFlag("Adult"));
    }

    // ---- resolving ----

    [Fact]
    public void Resolve_IsCaseInsensitive_Trims_AndAppliesAliases()
    {
        var set = LabelSet.Parse(Minimal);

        Assert.Equal("Chat", set.ResolveIntent("chat"));
        Assert.Equal("Chat", set.ResolveIntent("  CHAT \n"));
        Assert.Equal("Chat", set.ResolveIntent("socializing"));   // alias, any case
        Assert.Equal("Reaction", set.ResolveIntent("REACTION"));
        Assert.Equal("Warm", set.ResolveTone("warm"));
        Assert.Equal("Warm", set.ResolveTone(" Friendly "));      // alias
        Assert.Equal("Plain", set.ResolveTone("pLaIn"));
    }

    [Fact]
    public void Resolve_UnknownLabel_ReturnsNull()
    {
        var set = LabelSet.Parse(Minimal);

        Assert.Null(set.ResolveIntent("Smuggling"));
        Assert.Null(set.ResolveIntent("Unknown"));
        Assert.Null(set.ResolveIntent(""));
        Assert.Null(set.ResolveIntent("   "));
        Assert.Null(set.ResolveIntent(null));
        Assert.Null(set.ResolveIntent("3"));
        Assert.Null(set.ResolveTone("Chat"));    // an intent id is not a tone
        Assert.Null(set.ResolveIntent("Warm"));  // and a tone id is not an intent
    }

    // ---- emote-only messages ----

    // 29 characters: ten ":3" and nine spaces.
    private static readonly string TenSmileys = string.Join(" ", Enumerable.Repeat(":3", 10));

    [Theory]
    [InlineData(":3", true)]
    [InlineData(":3 :3", true)]
    [InlineData(" X.X ", true)]
    [InlineData("\t:3\n:)  x.x\r\n", true)]
    [InlineData(":3 hello", false)]
    [InlineData("hello", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(":3:3", false)]   // no space between them: one unknown token
    public void MatchReaction_MatchesWholeMessageOnly(string text, bool expectedMatch)
    {
        var set = LabelSet.Parse(Minimal);

        Assert.Equal(expectedMatch, set.MatchReaction(text) is not null);
    }

    [Fact]
    public void MatchReaction_ThirtyThreeCharacterString_IsNotAMatch()
    {
        var set = LabelSet.Parse(Minimal);

        var fits = TenSmileys + " :)";                 // exactly 32 characters, every token a pattern
        var tooLong = TenSmileys + " x.x";             // 33 characters, every token a pattern
        Assert.Equal(32, fits.Length);
        Assert.Equal(33, tooLong.Length);

        Assert.NotNull(set.MatchReaction(fits));
        Assert.Null(set.MatchReaction(tooLong));
        Assert.NotNull(set.MatchReaction("   " + fits + "   ")); // the limit applies after trimming
        Assert.Null(set.MatchReaction(null));
    }

    [Fact]
    public void MatchReaction_SeveralPatterns_ReturnsDistinctTonesInOrder()
    {
        var set = LabelSet.Parse(Minimal);

        Assert.Equal(["Warm"], set.MatchReaction(":3"));
        Assert.Equal(["Warm"], set.MatchReaction(":3 :) :3"));          // repeated tone appears once
        Assert.Equal(["Warm", "Plain"], set.MatchReaction(":3 x.x"));
        Assert.Equal(["Plain", "Warm"], set.MatchReaction("x.x :) x.x :3")); // order of first appearance
    }

    [Theory]
    [InlineData(":)))", "Friendly")]
    [InlineData("xDDD", "Humorous")]
    [InlineData("LOOOOL", "Humorous")]
    [InlineData("ughhh", "Frustrated")]
    [InlineData("!!!!", "Excited")]
    [InlineData("???", "Neutral")]
    [InlineData("XD", "Humorous")]
    [InlineData(":DDDD", "Excited")]
    [InlineData("uwuuu", "Friendly")]
    public void MatchReaction_UsesNormalForm(string text, string expectedTone)
    {
        Assert.Equal([expectedTone], LabelSet.Default.MatchReaction(text));
    }

    [Fact]
    public void NormalForm_LowerCasesAndCollapsesRepeatedCharacters()
    {
        Assert.Equal(":)", LabelSet.NormalForm(":)))"));
        Assert.Equal("xd", LabelSet.NormalForm("XDdd"));
        Assert.Equal("lol", LabelSet.NormalForm("LOOOOL"));
        Assert.Equal("haha", LabelSet.NormalForm("haha"));      // only runs of the same character collapse
        Assert.Equal(".", LabelSet.NormalForm("..."));
        Assert.Equal("", LabelSet.NormalForm(""));
    }

    [Theory]
    [InlineData(">.>")]
    [InlineData("~")]
    [InlineData("<.<")]
    [InlineData("  >.>   ~ ")]
    [InlineData("♥")]            // a symbol that is neither a letter nor a digit
    [InlineData("\U0001F600")]        // an emoji (a surrogate pair)
    public void MatchReaction_SymbolOnlyTextNotInTable_ReturnsEmptyList(string text)
    {
        var tones = LabelSet.Default.MatchReaction(text);

        Assert.NotNull(tones);
        Assert.Empty(tones);
    }

    [Theory]
    [InlineData(">w<")]
    [InlineData("hello")]
    [InlineData("x3")]
    [InlineData("<3 hello")]
    [InlineData("o7 7")]               // a digit that is not part of a pattern
    [InlineData("/wave")]
    [InlineData("こんにちは")] // Japanese letters
    public void MatchReaction_TextWithLettersNotInTable_ReturnsNull(string text)
    {
        Assert.Null(LabelSet.Default.MatchReaction(text));
    }

    [Fact]
    public void MatchReaction_SymbolOnlyRule_StillObeysTheLengthLimit()
    {
        Assert.Null(LabelSet.Default.MatchReaction(new string('~', 33)));
        Assert.NotNull(LabelSet.Default.MatchReaction(new string('~', 32)));
    }

    [Fact]
    public void MatchReaction_DefaultTable_FollowsTheTypeOfEmote()
    {
        var set = LabelSet.Default;

        Assert.Equal(["Friendly", "Sad"], set.MatchReaction(":3 :("));
        Assert.Equal(["Frustrated", "Friendly"], set.MatchReaction("x.x ^^"));
        Assert.Null(set.MatchReaction(":3 hello"));
        Assert.Null(set.MatchReaction("/wave")); // game emotes are not typed emoticons
    }

    // ---- fingerprint ----

    [Fact]
    public void Fingerprint_SameContent_Same_DifferentContent_Different()
    {
        var a = LabelSet.Parse(Minimal);
        var same = LabelSet.Parse(Minimal);
        // Same meaning, different whitespace and key order in the file.
        var reformatted = LabelSet.Parse(Minimal.Replace("\n", "").Replace("  ", ""));

        Assert.Equal(a.Fingerprint, same.Fingerprint);
        Assert.Equal(a.Fingerprint, reformatted.Fingerprint);
        Assert.Matches("^[0-9A-F]{64}$", a.Fingerprint);

        string[] changes =
        [
            Minimal.Replace("\"definition\": \"Talking.\"", "\"definition\": \"Chatting.\""),    // a definition
            Minimal.Replace("\"examples\": [\"hi\"]", "\"examples\": [\"hello\"]"),               // an example
            Minimal.Replace("\"id\": \"Chat\"", "\"id\": \"Talk\"").Replace("\"Socializing\": \"Chat\"", "\"Socializing\": \"Talk\""), // an id
            Minimal.Replace("\"Socializing\": \"Chat\"", "\"Social\": \"Chat\""),                 // an alias
            Minimal.Replace("[\":3\", \":)\"]", "[\":3\", \":D\"]"),                              // a pattern
            Minimal.Replace("{ \"tones\": [\"Plain\"], \"patterns\": [\"x.x\"] }", "{ \"tones\": [\"Warm\"], \"patterns\": [\"x.x\"] }"), // a reaction tone
        ];
        foreach (var changed in changes)
        {
            Assert.NotEqual(Minimal, changed);
            Assert.NotEqual(a.Fingerprint, LabelSet.Parse(changed).Fingerprint);
        }
    }

    [Fact]
    public void Fingerprint_ChangesWhenFlagsChange()
    {
        var withFlags = LabelSet.Parse(MinimalWithFlags);

        Assert.Equal(withFlags.Fingerprint, LabelSet.Parse(MinimalWithFlags).Fingerprint);
        Assert.NotEqual(LabelSet.Parse(Minimal).Fingerprint, withFlags.Fingerprint);   // adding flags

        string[] changes =
        [
            MinimalWithFlags.Replace("\"definition\": \"Explicit.\"", "\"definition\": \"Graphic.\""),   // a flag definition
            MinimalWithFlags.Replace("\"examples\": [\"x\"]", "\"examples\": [\"y\"]"),                  // a flag example
            MinimalWithFlags.Replace("\"id\": \"Rude\"", "\"id\": \"Crude\""),                           // a flag id
            MinimalWithFlags.Replace("\"Explicit\": \"Adult\"", "\"Graphic\": \"Adult\""),               // a flag alias
            MinimalWithFlags.Replace("{ \"id\": \"Rude\", \"definition\": \"Rude.\" }", ""),             // a flag removed
        ];
        foreach (var changed in changes)
        {
            Assert.NotEqual(MinimalWithFlags, changed);
            Assert.NotEqual(withFlags.Fingerprint, LabelSet.Parse(changed).Fingerprint); // a trailing comma is tolerated
        }
    }

    // ---- loading ----

    private static string TempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mmoprofiler-labels-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void LoadOrDefault_NoPathOrMissingFile_ReturnsDefault_NoError()
    {
        Assert.Same(LabelSet.Default, LabelSetLoader.LoadOrDefault(null, out var e1));
        Assert.Null(e1);
        Assert.Same(LabelSet.Default, LabelSetLoader.LoadOrDefault("", out var e2));
        Assert.Null(e2);
        Assert.Same(LabelSet.Default, LabelSetLoader.LoadOrDefault(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"), out var e3));
        Assert.Null(e3);
    }

    [Fact]
    public void LoadOrDefault_InvalidFile_ReturnsDefault_AndSetsError()
    {
        var bad = TempFile(Json(Labels("Chat", "chat", "Reaction"), OneTone));
        var notJson = TempFile("this is not json");
        try
        {
            Assert.Same(LabelSet.Default, LabelSetLoader.LoadOrDefault(bad, out var error));
            Assert.NotNull(error);
            Assert.Contains("more than once", error);       // says what to fix
            Assert.Contains(Path.GetFileName(bad), error);  // and which file

            Assert.Same(LabelSet.Default, LabelSetLoader.LoadOrDefault(notJson, out var error2));
            Assert.NotNull(error2);
        }
        finally
        {
            File.Delete(bad);
            File.Delete(notJson);
        }
    }

    [Fact]
    public void LoadOrDefault_ValidFile_ReplacesDefault()
    {
        var path = TempFile(Minimal);
        try
        {
            var set = LabelSetLoader.LoadOrDefault(path, out var error);

            Assert.Null(error);
            Assert.NotSame(LabelSet.Default, set);
            Assert.Equal(["Chat", "Reaction"], set.Intents.Select(l => l.Id)); // replaced, not merged
            Assert.Equal(["Warm", "Plain"], set.Tones.Select(l => l.Id));
            Assert.NotEqual(LabelSet.Default.Fingerprint, set.Fingerprint);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
