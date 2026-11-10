using MmoProfiler.Core;
using MmoProfiler.Core.Ollama;

namespace MmoProfiler.Tests;

public class AnalysisResponseParserTests
{
    private static readonly LabelSet Labels = LabelSet.Default;
    private static readonly ProfilerOptions Defaults = new();

    // A label entry as the model returns it, with a confidence.
    private static string L(string label, double confidence) =>
        $$"""{"label":"{{label}}","confidence":{{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""";

    // intents / tones are comma-separated label entries (use L).
    private static string Item(int id, string intents = "", string tones = "", string lang = "en", string topics = "\"raids\"") =>
        $$"""{"id":{{id}},"intents":[{{intents}}],"tones":[{{tones}}],"language":"{{lang}}","topics":[{{topics}}]}""";

    private static string Simple(int id, string tone = "Friendly", string intent = "Socializing", string lang = "en") =>
        Item(id, L(intent, 0.9), L(tone, 0.9), lang);

    private static string Results(params string[] items) => "{\"results\":[" + string.Join(",", items) + "]}";

    private static IReadOnlyList<MessageAnalysis> Parse(string json, int count, ProfilerOptions? options = null) =>
        AnalysisResponseParser.Parse(json, count, Labels, options ?? Defaults);

    [Fact]
    public void ValidResponse_MapsById()
    {
        // Returned out of order on purpose.
        var json = Results(
            Item(1, L("Spam", 0.9), L("Hostile", 0.9), "de", "\"ads\",\"gil\""),
            Item(0, L("LookingForGroup", 0.9), L("Excited", 0.9), "en", "\"raids\""));

        var r = Parse(json, 2);

        Assert.Equal(2, r.Count);
        Assert.Equal(new[] { "LookingForGroup" }, r[0].Intents);
        Assert.Equal(new[] { "Excited" }, r[0].Tones);
        Assert.Equal("en", r[0].Language);
        Assert.Equal(new[] { "raids" }, r[0].Topics);
        Assert.Equal(new[] { "Spam" }, r[1].Intents);
        Assert.Equal(new[] { "Hostile" }, r[1].Tones);
        Assert.Equal("de", r[1].Language);
        Assert.Equal(new[] { "ads", "gil" }, r[1].Topics);
    }

    [Fact]
    public void MissingIds_BecomeUnknown()
    {
        var r = Parse(Results(Simple(1)), 3);

        Assert.Equal(3, r.Count);
        Assert.Same(MessageAnalysis.Unknown, r[0]);
        Assert.Equal(new[] { "Friendly" }, r[1].Tones);
        Assert.Same(MessageAnalysis.Unknown, r[2]);

        Assert.All(Parse(Results(), 2), a => Assert.Same(MessageAnalysis.Unknown, a));
    }

    [Fact]
    public void DuplicateAndOutOfRangeIds_Ignored()
    {
        var json = Results(
            Simple(0, "Friendly"),
            Simple(0, "Hostile"),   // duplicate: the first result for an id is kept
            Simple(5, "Sad"),       // out of range
            Simple(-1, "Sad"),      // out of range
            "{\"id\":\"1\",\"tones\":[" + L("Sad", 0.9) + "]}",   // id is not a number
            "{\"tones\":[" + L("Sad", 0.9) + "]}",                // no id
            "42");                                                // not an object

        var r = Parse(json, 2);

        Assert.Equal(2, r.Count);
        Assert.Equal(new[] { "Friendly" }, r[0].Tones);
        Assert.Same(MessageAnalysis.Unknown, r[1]);
    }

    [Fact]
    public void UnknownLabelValues_AreDropped()
    {
        var json = Results(
            Item(0, L("Socializing", 0.9), L("Grumpy", 0.9)),
            Item(1, L("Smuggling", 0.9), L("Friendly", 0.9)),
            Item(2, L("4", 0.9), L("3", 0.9)),                       // numbers are not names
            "{\"id\":3,\"tones\":[7,null,\"Friendly\",{\"label\":5}],\"intents\":null,\"language\":\"en\",\"topics\":[]}");

        var r = Parse(json, 4);

        Assert.Empty(r[0].Tones);
        Assert.Equal(new[] { "Socializing" }, r[0].Intents);
        Assert.Equal(new[] { "Friendly" }, r[1].Tones);
        Assert.Empty(r[1].Intents);
        Assert.False(r[2].HasLabels);
        // Plain strings are labels since milestone 5c, so "Friendly" counts; the numbers, null and the
        // object whose label is not a string are still dropped.
        Assert.Equal(new[] { "Friendly" }, r[3].Tones);
        Assert.Empty(r[3].Intents);
    }

    [Fact]
    public void LabelNames_MatchedCaseInsensitively()
    {
        var r = Parse(Results(
            Item(0, L("lookingforgroup", 0.9), L("fRiEnDlY", 0.9)),
            Item(1, L("HELPREQUEST", 0.9), L(" SARCASTIC ", 0.9))), 2);

        Assert.Equal(new[] { "Friendly" }, r[0].Tones);
        Assert.Equal(new[] { "LookingForGroup" }, r[0].Intents);
        Assert.Equal(new[] { "Sarcastic" }, r[1].Tones);
        Assert.Equal(new[] { "HelpRequest" }, r[1].Intents);
    }

    [Fact]
    public void KeepsLabelsAtOrAboveThreshold_UpToMax_StrongestFirst()
    {
        // Threshold 0.6, maximum 2. Listed out of order; 0.6 is "at" the threshold and is kept.
        var json = Results(
            Item(0,
                intents: string.Join(",", L("Trading", 0.3), L("Advertising", 0.6), L("Spam", 0.95), L("Other", 0.7)),
                tones: string.Join(",", L("Neutral", 0.5), L("Friendly", 0.8))));

        var r = Parse(json, 1, new ProfilerOptions { MaxIntentsPerMessage = 2, MaxTonesPerMessage = 2, LabelConfidenceThreshold = 0.6 });

        Assert.Equal(new[] { "Spam", "Other" }, r[0].Intents);   // 0.95, 0.7; 0.6 would be third
        Assert.Equal(new[] { "Friendly" }, r[0].Tones);          // 0.5 is below the threshold

        var wide = Parse(json, 1, new ProfilerOptions { MaxIntentsPerMessage = 2, LabelConfidenceThreshold = 0.6 });
        Assert.Equal(2, wide[0].Intents.Count);
        var exact = Parse(
            Results(Item(0, L("Advertising", 0.6), L("Friendly", 0.6))), 1,
            new ProfilerOptions { LabelConfidenceThreshold = 0.6 });
        Assert.Equal(new[] { "Advertising" }, exact[0].Intents);
    }

    [Fact]
    public void NothingAboveThreshold_KeepsSingleStrongest()
    {
        var json = Results(Item(0,
            intents: string.Join(",", L("Trading", 0.2), L("Spam", 0.4), L("Other", 0.1)),
            tones: string.Join(",", L("Neutral", 0.3), L("Friendly", 0.35))));

        var r = Parse(json, 1);

        Assert.Equal(new[] { "Spam" }, r[0].Intents);
        Assert.Equal(new[] { "Friendly" }, r[0].Tones);
    }

    [Fact]
    public void UnresolvedLabels_AreDropped()
    {
        // The unresolved label has the highest confidence but must not take part in the ranking.
        var json = Results(Item(0,
            intents: string.Join(",", L("Smuggling", 0.99), L("Trading", 0.7)),
            tones: string.Join(",", L("Grumpy", 0.99), L("Sad", 0.2))));

        var r = Parse(json, 1);

        Assert.Equal(new[] { "Trading" }, r[0].Intents);
        Assert.Equal(new[] { "Sad" }, r[0].Tones);   // nothing reaches the threshold: the strongest resolved label stays
    }

    [Fact]
    public void Aliases_AreResolvedToCurrentIds()
    {
        var labels = LabelSet.Parse("""
            {"version":1,
             "intents":[{"id":"Chat","definition":"d","examples":[]},{"id":"Reaction","definition":"d","examples":[]}],
             "tones":[{"id":"Warm","definition":"d","examples":[]}],
             "aliases":{"intents":{"Socializing":"Chat"},"tones":{"Friendly":"Warm"}},
             "reactionIntent":"Reaction","reactions":[]}
            """);

        var r = AnalysisResponseParser.Parse(
            Results(Item(0, L("socializing", 0.9), L("Friendly", 0.9))), 1, labels, Defaults);

        Assert.Equal(new[] { "Chat" }, r[0].Intents);
        Assert.Equal(new[] { "Warm" }, r[0].Tones);
    }

    [Fact]
    public void RepeatedLabel_KeepsHighestConfidence()
    {
        // Friendly 0.3 then 0.9 must rank as 0.9, above Neutral at 0.7; the repeat is not listed twice.
        var json = Results(Item(0,
            tones: string.Join(",", L("Friendly", 0.3), L("Neutral", 0.7), L("Friendly", 0.9), L("friendly", 0.1))));

        var r = Parse(json, 1);

        Assert.Equal(new[] { "Friendly", "Neutral" }, r[0].Tones);
    }

    [Fact]
    public void MissingOrInvalidConfidence_CountsAsZero()
    {
        // Zero confidence is below the threshold, so only the label with a real confidence is kept.
        var json = Results(Item(0, tones:
            "{\"label\":\"Sad\"}," +
            "{\"label\":\"Hostile\",\"confidence\":\"high\"}," +
            "{\"label\":\"Formal\",\"confidence\":null}," +
            L("Neutral", 0.65)));

        var r = Parse(json, 1);
        Assert.Equal(new[] { "Neutral" }, r[0].Tones);

        // When no entry in the list has a numeric confidence at all, the confidence rules do not apply:
        // the first N labels are kept in the model's order (milestone 5c).
        var allZero = Parse(Results(Item(0, tones: "{\"label\":\"Sad\"},{\"label\":\"Hostile\",\"confidence\":\"x\"}")), 1);
        Assert.Equal(new[] { "Sad", "Hostile" }, allZero[0].Tones);

        // With a numeric confidence present but below the threshold, the single strongest is kept; the
        // entry without a confidence counts as 0, so it is not the strongest.
        var belowThreshold = Parse(Results(Item(0, tones: "{\"label\":\"Sad\"}," + L("Hostile", 0.2))), 1);
        Assert.Equal(new[] { "Hostile" }, belowThreshold[0].Tones);

        // With a threshold of 0 a missing confidence (0) still qualifies.
        var lenient = Parse(
            Results(Item(0, tones: "{\"label\":\"Sad\"},{\"label\":\"Hostile\"}")), 1,
            new ProfilerOptions { LabelConfidenceThreshold = 0 });
        Assert.Equal(new[] { "Sad", "Hostile" }, lenient[0].Tones);
    }

    [Fact]
    public void Confidence_IsClampedToZeroToOne()
    {
        // 7 clamps to 1 and outranks 0.9; -3 clamps to 0 and is below the threshold.
        var json = Results(Item(0, tones: string.Join(",", L("Friendly", 0.9), L("Excited", 7), L("Sad", -3))));

        var r = Parse(json, 1);

        Assert.Equal(new[] { "Excited", "Friendly" }, r[0].Tones);
    }

    [Fact]
    public void ModelOrder_BreaksConfidenceTies()
    {
        var json = Results(Item(0, tones: string.Join(",", L("Humorous", 0.8), L("Excited", 0.8), L("Sad", 0.8))));

        var r = Parse(json, 1);

        Assert.Equal(new[] { "Humorous", "Excited" }, r[0].Tones);
    }

    [Fact]
    public void MaxOfOne_ReturnsOnlyTheStrongest()
    {
        var json = Results(Item(0,
            intents: string.Join(",", L("Trading", 0.7), L("Spam", 0.9)),
            tones: string.Join(",", L("Friendly", 0.8), L("Neutral", 0.7))));

        var r = Parse(json, 1, new ProfilerOptions { MaxIntentsPerMessage = 1, MaxTonesPerMessage = 1 });

        Assert.Equal(new[] { "Spam" }, r[0].Intents);
        Assert.Equal(new[] { "Friendly" }, r[0].Tones);
    }

    [Fact]
    public void ReactionOnlyResult_KeepsItsLanguage()
    {
        // The reaction intent is dropped (see ReactionIntentFromModel_IsDropped), and the old rule that set
        // the language of a reaction-only result to "und" is gone: a full sentence the model wrongly
        // called a reaction keeps its language.
        var reaction = Labels.ReactionIntent;
        var json = Results(
            Item(0, L(reaction, 0.9), L("Sad", 0.9), lang: "en"),
            Item(1, Plain(reaction), Plain("Sad"), lang: "de"));

        var r = Parse(json, 2);

        Assert.Equal("en", r[0].Language);
        Assert.Equal("de", r[1].Language);
        Assert.Empty(r[0].Intents);
        Assert.Empty(r[1].Intents);
    }

    // ---- plain lists (milestone 5c) ----

    // A label as a plain string, the form the model returns by default.
    private static string Plain(string label) => "\"" + label + "\"";
    private static string PlainList(params string[] labels) => string.Join(",", labels.Select(Plain));

    [Fact]
    public void PlainLists_KeepFirstNResolved_InModelOrder()
    {
        var json = Results(Item(0,
            intents: PlainList("Trading", "Spam", "Other"),
            tones: PlainList("Sad", "Friendly", "Neutral")));

        var two = Parse(json, 1, new ProfilerOptions { MaxIntentsPerMessage = 2, MaxTonesPerMessage = 2 });
        Assert.Equal(new[] { "Trading", "Spam" }, two[0].Intents);
        Assert.Equal(new[] { "Sad", "Friendly" }, two[0].Tones);

        var one = Parse(json, 1, new ProfilerOptions { MaxIntentsPerMessage = 1, MaxTonesPerMessage = 1 });
        Assert.Equal(new[] { "Trading" }, one[0].Intents);
        Assert.Equal(new[] { "Sad" }, one[0].Tones);

        // The confidence threshold plays no part when there are no confidences.
        var strict = Parse(json, 1, new ProfilerOptions { LabelConfidenceThreshold = 1.0 });
        Assert.Equal(2, strict[0].Intents.Count);
    }

    [Fact]
    public void PlainLists_DropUnresolvedAndRepeats()
    {
        var json = Results(Item(0,
            intents: PlainList("Smuggling", "Trading", "trading", " TRADING ", "Spam"),
            tones: PlainList("Grumpy", "Sad", "Sad", "Neutral")));

        var r = Parse(json, 1);

        // Unresolved labels do not use up a slot; a repeat counts once and the first occurrence wins.
        Assert.Equal(new[] { "Trading", "Spam" }, r[0].Intents);
        Assert.Equal(new[] { "Sad", "Neutral" }, r[0].Tones);
    }

    [Fact]
    public void ObjectForm_StillUsesConfidenceRules()
    {
        // Out of order, with a repeat and one below the threshold: the 5b rules apply unchanged.
        var json = Results(Item(0,
            intents: string.Join(",", L("Trading", 0.3), L("Advertising", 0.6), L("Spam", 0.95), L("Spam", 0.1)),
            tones: string.Join(",", L("Neutral", 0.5), L("Friendly", 0.2))));

        var r = Parse(json, 1);

        Assert.Equal(new[] { "Spam", "Advertising" }, r[0].Intents);
        Assert.Equal(new[] { "Neutral" }, r[0].Tones); // nothing reaches 0.6: the strongest stays
    }

    [Fact]
    public void ObjectForm_AndPlainStringsMixed_PlainStringsCountAsZeroConfidence()
    {
        var json = Results(Item(0, tones: Plain("Sad") + "," + L("Friendly", 0.8)));

        var r = Parse(json, 1);

        Assert.Equal(new[] { "Friendly" }, r[0].Tones);
    }

    [Fact]
    public void ReactionIntentFromModel_IsDropped()
    {
        var reaction = Labels.ReactionIntent;
        var json = Results(
            Item(0, Plain(reaction), PlainList("Sad")),                              // only the reaction intent
            Item(1, PlainList(reaction, "Socializing"), PlainList("Sad")),           // reaction first, then a real one
            Item(2, L(reaction, 0.99) + "," + L("Trading", 0.7), L("Sad", 0.9)));    // object form, highest confidence

        var r = Parse(json, 3);

        Assert.Empty(r[0].Intents);
        Assert.Equal(new[] { "Socializing" }, r[1].Intents);
        Assert.Equal(new[] { "Trading" }, r[2].Intents);
        Assert.All(r, a => Assert.DoesNotContain(reaction, a.Intents));
    }

    [Fact]
    public void ReactionIntentFromModel_ThroughAnAlias_IsDropped()
    {
        var labels = LabelSet.Parse("""
            {"version":1,
             "intents":[{"id":"Chat","definition":"d"},{"id":"Emote","definition":"d"}],
             "tones":[{"id":"Warm","definition":"d"}],
             "aliases":{"intents":{"Reaction":"Emote"}},
             "reactionIntent":"Emote","reactions":[]}
            """);

        var r = AnalysisResponseParser.Parse(
            Results(Item(0, PlainList("Reaction", "Chat"), PlainList("Warm"))), 1, labels, Defaults);

        Assert.Equal(new[] { "Chat" }, r[0].Intents);
    }

    [Fact]
    public void Flags_ResolvedDeduplicated_UnresolvedDropped()
    {
        var labels = LabelSet.Parse("""
            {"version":1,
             "intents":[{"id":"Chat","definition":"d"}],
             "tones":[{"id":"Warm","definition":"d"}],
             "flags":[{"id":"Adult","definition":"d"},{"id":"Rude","definition":"d"}],
             "aliases":{"flags":{"Explicit":"Adult"}},
             "reactionIntent":"Chat","reactions":[]}
            """);
        var json = Results(
            "{\"id\":0,\"intents\":[\"Chat\"],\"tones\":[\"Warm\"],\"flags\":[\"adult\",\"Explicit\",\"Nonsense\",\" RUDE \",\"Adult\",7,null],\"language\":\"en\",\"topics\":[]}");

        var r = AnalysisResponseParser.Parse(json, 1, labels, Defaults);

        Assert.Equal(new[] { "Adult", "Rude" }, r[0].Flags);
    }

    [Fact]
    public void FlagsMissing_GivesEmptyList()
    {
        var json = Results(
            Item(0, PlainList("Other"), PlainList("Neutral")),                 // Item() writes no flags at all
            "{\"id\":1,\"intents\":[\"Other\"],\"tones\":[\"Neutral\"],\"flags\":null,\"language\":\"en\",\"topics\":[]}",
            "{\"id\":2,\"intents\":[\"Other\"],\"tones\":[\"Neutral\"],\"flags\":[],\"language\":\"en\",\"topics\":[]}");

        var r = Parse(json, 3);

        Assert.All(r, a => Assert.Empty(a.Flags));
        Assert.All(r, a => Assert.True(a.HasLabels));
        Assert.Empty(MessageAnalysis.Unknown.Flags);
    }

    [Fact]
    public void Flags_AloneDoNotMakeAnAnalysis()
    {
        var json = Results("{\"id\":0,\"intents\":[],\"tones\":[],\"flags\":[\"Adult\"],\"language\":\"en\",\"topics\":[]}");

        var r = Parse(json, 1);

        Assert.Equal(new[] { "Adult" }, r[0].Flags);
        Assert.False(r[0].HasLabels);
    }

    [Theory]
    [InlineData("EN", "en")]
    [InlineData(" Fr ", "fr")]
    [InlineData("fil", "fil")]
    [InlineData("english", "und")]
    [InlineData("e", "und")]
    [InlineData("", "und")]
    [InlineData("e1", "und")]
    [InlineData("日本", "und")]
    public void InvalidLanguage_BecomesUnd(string language, string expected)
    {
        var r = Parse(Results(Item(0, L("Other", 0.9), L("Neutral", 0.9), lang: language)), 1);

        Assert.Equal(expected, r[0].Language);
    }

    [Fact]
    public void InvalidLanguage_NonStringOrMissing_BecomesUnd()
    {
        var labels = "\"intents\":[" + L("Other", 0.9) + "],\"tones\":[" + L("Friendly", 0.9) + "]";
        var json = Results(
            "{\"id\":0," + labels + ",\"language\":5,\"topics\":[]}",
            "{\"id\":1," + labels + ",\"topics\":[]}");

        var r = Parse(json, 2);

        Assert.Equal("und", r[0].Language);
        Assert.Equal("und", r[1].Language);
    }

    [Fact]
    public void NoIntentsOrTones_GivesUnknown()
    {
        var json = Results(
            Item(0),                                                   // both lists empty
            "{\"id\":1,\"language\":\"en\",\"topics\":[\"a\"]}",       // both lists missing
            Item(2, L("Smuggling", 0.9), L("Grumpy", 0.9)));           // nothing resolves

        var r = Parse(json, 3);

        Assert.All(r, a =>
        {
            Assert.False(a.HasLabels);
            Assert.Empty(a.Intents);
            Assert.Empty(a.Tones);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"results\":")]
    [InlineData("{}")]
    [InlineData("{\"results\":{}}")]
    [InlineData("{\"results\":null}")]
    [InlineData("[]")]
    [InlineData("\"results\"")]
    public void MalformedJson_ThrowsAnalyzerUnavailable(string json)
    {
        Assert.Throws<AnalyzerUnavailableException>(() => Parse(json, 1));
    }

    [Fact]
    public void Topics_NonStringEntriesSkipped_AndMissingArrayIsEmpty()
    {
        var labels = "\"intents\":[" + L("Other", 0.9) + "],\"tones\":[" + L("Friendly", 0.9) + "]";
        var json = Results(
            "{\"id\":0," + labels + ",\"language\":\"en\",\"topics\":[\"a\",1,null,\"b\"]}",
            "{\"id\":1," + labels + ",\"language\":\"en\"}");

        var r = Parse(json, 2);

        Assert.Equal(new[] { "a", "b" }, r[0].Topics);
        Assert.Empty(r[1].Topics);
    }
}
