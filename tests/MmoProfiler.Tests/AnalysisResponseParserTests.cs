using MmoProfiler.Core;
using MmoProfiler.Core.Ollama;

namespace MmoProfiler.Tests;

public class AnalysisResponseParserTests
{
    private static string Item(int id, string tone = "Friendly", string intent = "Socializing", string lang = "en", string topics = "\"raids\"") =>
        $$"""{"id":{{id}},"tone":"{{tone}}","intent":"{{intent}}","language":"{{lang}}","topics":[{{topics}}]}""";

    private static string Results(params string[] items) => "{\"results\":[" + string.Join(",", items) + "]}";

    [Fact]
    public void ValidResponse_MapsById()
    {
        // Returned out of order on purpose.
        var json = Results(
            Item(1, "Hostile", "Spam", "de", "\"ads\",\"gil\""),
            Item(0, "Excited", "LookingForGroup", "en", "\"raids\""));

        var r = AnalysisResponseParser.Parse(json, 2);

        Assert.Equal(2, r.Count);
        Assert.Equal(new MessageAnalysis(Tone.Excited, Intent.LookingForGroup, "en", new[] { "raids" }).Tone, r[0].Tone);
        Assert.Equal(Intent.LookingForGroup, r[0].Intent);
        Assert.Equal("en", r[0].Language);
        Assert.Equal(new[] { "raids" }, r[0].Topics);
        Assert.Equal(Tone.Hostile, r[1].Tone);
        Assert.Equal(Intent.Spam, r[1].Intent);
        Assert.Equal("de", r[1].Language);
        Assert.Equal(new[] { "ads", "gil" }, r[1].Topics);
    }

    [Fact]
    public void MissingIds_BecomeUnknown()
    {
        var r = AnalysisResponseParser.Parse(Results(Item(1)), 3);

        Assert.Equal(3, r.Count);
        Assert.Same(MessageAnalysis.Unknown, r[0]);
        Assert.Equal(Tone.Friendly, r[1].Tone);
        Assert.Same(MessageAnalysis.Unknown, r[2]);

        Assert.All(AnalysisResponseParser.Parse(Results(), 2), a => Assert.Same(MessageAnalysis.Unknown, a));
    }

    [Fact]
    public void DuplicateAndOutOfRangeIds_Ignored()
    {
        var json = Results(
            Item(0, "Friendly"),
            Item(0, "Hostile"),   // duplicate: the first result for an id is kept
            Item(5, "Sad"),       // out of range
            Item(-1, "Sad"),      // out of range
            "{\"id\":\"1\",\"tone\":\"Sad\"}",   // id is not a number
            "{\"tone\":\"Sad\"}",                // no id
            "42");                               // not an object

        var r = AnalysisResponseParser.Parse(json, 2);

        Assert.Equal(2, r.Count);
        Assert.Equal(Tone.Friendly, r[0].Tone);
        Assert.Same(MessageAnalysis.Unknown, r[1]);
    }

    [Fact]
    public void UnknownEnumValues_BecomeUnknown()
    {
        var json = Results(
            Item(0, "Grumpy", "Socializing"),
            Item(1, "Friendly", "Smuggling"),
            Item(2, "3", "4"),                       // numbers are not names
            "{\"id\":3,\"tone\":7,\"intent\":null,\"language\":\"en\",\"topics\":[]}");

        var r = AnalysisResponseParser.Parse(json, 4);

        Assert.Equal(Tone.Unknown, r[0].Tone);
        Assert.Equal(Intent.Socializing, r[0].Intent);
        Assert.Equal(Tone.Friendly, r[1].Tone);
        Assert.Equal(Intent.Unknown, r[1].Intent);
        Assert.Equal(Tone.Unknown, r[2].Tone);
        Assert.Equal(Intent.Unknown, r[2].Intent);
        Assert.Equal(Tone.Unknown, r[3].Tone);
        Assert.Equal(Intent.Unknown, r[3].Intent);
    }

    [Fact]
    public void EnumNames_MatchedCaseInsensitively()
    {
        var r = AnalysisResponseParser.Parse(
            Results(Item(0, "fRiEnDlY", "lookingforgroup"), Item(1, " SARCASTIC ", "HELPREQUEST")), 2);

        Assert.Equal(Tone.Friendly, r[0].Tone);
        Assert.Equal(Intent.LookingForGroup, r[0].Intent);
        Assert.Equal(Tone.Sarcastic, r[1].Tone);
        Assert.Equal(Intent.HelpRequest, r[1].Intent);
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
        var r = AnalysisResponseParser.Parse(Results(Item(0, lang: language)), 1);

        Assert.Equal(expected, r[0].Language);
    }

    [Fact]
    public void InvalidLanguage_NonStringOrMissing_BecomesUnd()
    {
        var json = Results(
            "{\"id\":0,\"tone\":\"Friendly\",\"intent\":\"Other\",\"language\":5,\"topics\":[]}",
            "{\"id\":1,\"tone\":\"Friendly\",\"intent\":\"Other\",\"topics\":[]}");

        var r = AnalysisResponseParser.Parse(json, 2);

        Assert.Equal("und", r[0].Language);
        Assert.Equal("und", r[1].Language);
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
        Assert.Throws<AnalyzerUnavailableException>(() => AnalysisResponseParser.Parse(json, 1));
    }

    [Fact]
    public void Topics_NonStringEntriesSkipped_AndMissingArrayIsEmpty()
    {
        var json = Results(
            "{\"id\":0,\"tone\":\"Friendly\",\"intent\":\"Other\",\"language\":\"en\",\"topics\":[\"a\",1,null,\"b\"]}",
            "{\"id\":1,\"tone\":\"Friendly\",\"intent\":\"Other\",\"language\":\"en\"}");

        var r = AnalysisResponseParser.Parse(json, 2);

        Assert.Equal(new[] { "a", "b" }, r[0].Topics);
        Assert.Empty(r[1].Topics);
    }
}
