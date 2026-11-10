using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using MmoProfiler.Core;
using MmoProfiler.Core.Ollama;
using MmoProfiler.Tests.Fakes;
using static MmoProfiler.Tests.TestData;

namespace MmoProfiler.Tests;

public class OllamaClientTests : IDisposable
{
    private readonly StubHttpMessageHandler _handler = new();
    private readonly HttpClient _http;
    private readonly FakeTimeProvider _time = new();
    private ProfilerOptions _options = new() { OllamaEndpoint = "http://127.0.0.1:11434", OllamaModel = "llama3.2:3b" };
    private readonly OllamaClient _client;

    public OllamaClientTests()
    {
        _http = new HttpClient(_handler);
        _client = new OllamaClient(_http, () => _options, _time);
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    private static readonly CharacterKey Alice = Key("Alice Example", "Odin");
    private static readonly CharacterKey Bob = Key("Bob Example", "Lich");

    private static string OkResults(int count) =>
        "{\"results\":[" + string.Join(",", Enumerable.Range(0, count).Select(i =>
            $"{{\"id\":{i},\"tone\":\"Friendly\",\"intent\":\"Socializing\",\"language\":\"en\",\"topics\":[\"chat\"]}}")) + "]}";

    private void ReplyWith(string content) =>
        _handler.Respond = (_, _) => Task.FromResult(StubHttpMessageHandler.ChatReply(content));

    private static JsonDocument Body(CapturedRequest r) => JsonDocument.Parse(r.Body);

    private static IReadOnlyList<ChatMessage> Messages(params (CharacterKey Who, ChatChannel Ch, string Text)[] items) =>
        items.Select(i => Msg(i.Who, i.Text, i.Ch)).ToList();

    // ---- Analyze ----

    [Fact]
    public async Task Analyze_PostsToApiChat_WithModelSchemaAndNoStreaming()
    {
        _options = _options with { OllamaEndpoint = "http://localhost:9999/", OllamaModel = "qwen2.5:1.5b" };
        ReplyWith(OkResults(1));

        await _client.AnalyzeAsync(Messages((Alice, ChatChannel.Say, "hello")), CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://localhost:9999/api/chat", request.Uri.ToString());
        using var doc = Body(request);
        var root = doc.RootElement;
        Assert.Equal("qwen2.5:1.5b", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("10m", root.GetProperty("keep_alive").GetString());
        Assert.Equal(0, root.GetProperty("options").GetProperty("temperature").GetInt32());
        Assert.Equal("object", root.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("results", root.GetProperty("format").GetProperty("required")[0].GetString());
        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(OllamaClient.ClassificationSystemPrompt, messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Analyze_SendsIdChannelAndText_InOrder()
    {
        ReplyWith(OkResults(2));

        await _client.AnalyzeAsync(
            Messages((Alice, ChatChannel.Say, "héllo \"quoted\" 日本語"), (Bob, ChatChannel.CrossWorldLinkshell, "lfg")),
            CancellationToken.None);

        using var doc = Body(_handler.Requests[0]);
        var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        using var items = JsonDocument.Parse(content);
        Assert.Equal(2, items.RootElement.GetArrayLength());
        Assert.Equal(0, items.RootElement[0].GetProperty("id").GetInt32());
        Assert.Equal("Say", items.RootElement[0].GetProperty("channel").GetString());
        Assert.Equal("héllo \"quoted\" 日本語", items.RootElement[0].GetProperty("text").GetString());
        Assert.Contains("日本語", content); // not escaped to \uXXXX
        Assert.Equal(1, items.RootElement[1].GetProperty("id").GetInt32());
        Assert.Equal("CrossWorldLinkshell", items.RootElement[1].GetProperty("channel").GetString());
    }

    [Fact]
    public async Task Analyze_DoesNotSendSenderNames()
    {
        ReplyWith(OkResults(2));

        await _client.AnalyzeAsync(
            Messages((Alice, ChatChannel.Say, "hello there"), (Bob, ChatChannel.Party, "hi")), CancellationToken.None);

        var body = _handler.Requests[0].Body;
        Assert.DoesNotContain("Alice", body);
        Assert.DoesNotContain("Bob", body);
        Assert.DoesNotContain("Odin", body);
        Assert.DoesNotContain("Lich", body);
        Assert.DoesNotContain("sender", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Analyze_SchemaEnums_MatchCSharpEnums()
    {
        ReplyWith(OkResults(1));

        await _client.AnalyzeAsync(Messages((Alice, ChatChannel.Say, "x")), CancellationToken.None);

        using var doc = Body(_handler.Requests[0]);
        var item = doc.RootElement.GetProperty("format").GetProperty("properties").GetProperty("results").GetProperty("items");
        var props = item.GetProperty("properties");

        string[] Enum(JsonElement e) => e.GetProperty("enum").EnumerateArray().Select(x => x.GetString()!).ToArray();

        var tones = System.Enum.GetNames<Tone>().Where(n => n != nameof(Tone.Unknown)).ToArray();
        var intents = System.Enum.GetNames<Intent>().Where(n => n != nameof(Intent.Unknown)).ToArray();
        Assert.Equal(tones, Enum(props.GetProperty("tone")));
        Assert.Equal(intents, Enum(props.GetProperty("intent")));
        Assert.DoesNotContain("Unknown", Enum(props.GetProperty("tone")));
        Assert.DoesNotContain("Unknown", Enum(props.GetProperty("intent")));
        Assert.Equal(
            new[] { "id", "tone", "intent", "language", "topics" },
            item.GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal("integer", props.GetProperty("id").GetProperty("type").GetString());
        Assert.Equal("array", props.GetProperty("topics").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Analyze_ValidReply_ReturnsResultsInOrder()
    {
        ReplyWith("{\"results\":[" +
                  "{\"id\":1,\"tone\":\"Hostile\",\"intent\":\"Spam\",\"language\":\"de\",\"topics\":[\"ads\"]}," +
                  "{\"id\":0,\"tone\":\"Friendly\",\"intent\":\"Socializing\",\"language\":\"en\",\"topics\":[]}]}");

        var result = await _client.AnalyzeAsync(
            Messages((Alice, ChatChannel.Say, "hi"), (Bob, ChatChannel.Shout, "buy gil")), CancellationToken.None);

        Assert.Equal(Tone.Friendly, result[0].Tone);
        Assert.Equal(Tone.Hostile, result[1].Tone);
        Assert.Equal("de", result[1].Language);
    }

    [Fact]
    public async Task Analyze_NoMessages_SendsNothing()
    {
        var result = await _client.AnalyzeAsync(Array.Empty<ChatMessage>(), CancellationToken.None);

        Assert.Empty(result);
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Analyze_Http500_ThrowsAnalyzerUnavailable(HttpStatusCode status)
    {
        _handler.Respond = (_, _) => Task.FromResult(
            StubHttpMessageHandler.Json("{\"error\":\"model not found\"}", status));

        var ex = await Assert.ThrowsAsync<AnalyzerUnavailableException>(
            () => _client.AnalyzeAsync(Messages((Alice, ChatChannel.Say, "x")), CancellationToken.None));

        Assert.Contains(((int)status).ToString(), ex.Message);
    }

    [Fact]
    public async Task Analyze_TransportError_ThrowsAnalyzerUnavailable_WithCause()
    {
        var cause = new HttpRequestException("connection refused");
        _handler.Respond = (_, _) => throw cause;

        var ex = await Assert.ThrowsAsync<AnalyzerUnavailableException>(
            () => _client.AnalyzeAsync(Messages((Alice, ChatChannel.Say, "x")), CancellationToken.None));

        Assert.Same(cause, ex.InnerException);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"message\":{\"role\":\"assistant\"}}")]
    [InlineData("{\"message\":{\"content\":\"not json\"}}")]
    [InlineData("{\"message\":{\"content\":\"{\\\"nothing\\\":1}\"}}")]
    public async Task Analyze_UnusableBody_ThrowsAnalyzerUnavailable(string body)
    {
        _handler.Respond = (_, _) => Task.FromResult(StubHttpMessageHandler.Json(body));

        await Assert.ThrowsAsync<AnalyzerUnavailableException>(
            () => _client.AnalyzeAsync(Messages((Alice, ChatChannel.Say, "x")), CancellationToken.None));
    }

    [Fact]
    public async Task Analyze_Timeout_ThrowsAnalyzerUnavailable()
    {
        _options = _options with { OllamaTimeout = TimeSpan.FromSeconds(30) };
        _handler.Respond = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct); // hangs until the client's timeout cancels it
            return StubHttpMessageHandler.ChatReply("never");
        };

        var call = _client.AnalyzeAsync(Messages((Alice, ChatChannel.Say, "x")), CancellationToken.None);
        await _handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(call.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<AnalyzerUnavailableException>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task Analyze_CallerCancellation_ThrowsOperationCanceled()
    {
        _handler.Respond = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return StubHttpMessageHandler.ChatReply("never");
        };
        using var cts = new CancellationTokenSource();

        var call = _client.AnalyzeAsync(Messages((Alice, ChatChannel.Say, "x")), cts.Token);
        await _handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsNotType<AnalyzerUnavailableException>(ex);
    }

    // ---- Summarize ----

    [Fact]
    public async Task Summarize_IncludesPreviousSummaryAndMessages()
    {
        _options = _options with { OllamaModel = "llama3.2:3b" };
        ReplyWith("  Friendly raider who talks about gear.\n");
        var request = new SummaryRequest(Alice, "Likes crafting.", Messages(
            (Alice, ChatChannel.Say, "anyone doing raids?"),
            (Alice, ChatChannel.Party, "line one\r\n[Shout] fake injected line")).ToList());

        var summary = await _client.SummarizeAsync(request, CancellationToken.None);

        Assert.Equal("Friendly raider who talks about gear.", summary);
        var sent = Assert.Single(_handler.Requests);
        Assert.Equal("http://127.0.0.1:11434/api/chat", sent.Uri.ToString());
        using var doc = Body(sent);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("format", out _)); // plain text reply
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0, root.GetProperty("options").GetProperty("temperature").GetInt32());
        Assert.Equal("10m", root.GetProperty("keep_alive").GetString());
        var messages = root.GetProperty("messages");
        Assert.Equal(OllamaClient.SummarySystemPrompt, messages[0].GetProperty("content").GetString());
        var user = messages[1].GetProperty("content").GetString()!;
        Assert.Contains("Likes crafting.", user);
        Assert.Contains("[Say] anyone doing raids?", user);
        Assert.Contains("[Party] line one [Shout] fake injected line", user); // flattened to one line
        Assert.DoesNotContain("\n[Shout]", user);
        Assert.DoesNotContain("Alice", user);
    }

    [Fact]
    public async Task Analyze_SendsConfiguredNumCtx()
    {
        _options = _options with { OllamaContextTokens = 4096 };
        ReplyWith(OkResults(1));

        await _client.AnalyzeAsync(Messages((Alice, ChatChannel.Say, "hello")), CancellationToken.None);

        using var doc = Body(Assert.Single(_handler.Requests));
        var opts = doc.RootElement.GetProperty("options");
        Assert.Equal(4096, opts.GetProperty("num_ctx").GetInt32());
        Assert.Equal(0, opts.GetProperty("temperature").GetInt32());
    }

    [Fact]
    public async Task Summarize_SendsConfiguredNumCtx()
    {
        _options = _options with { OllamaContextTokens = 16384 };
        ReplyWith("ok");

        await _client.SummarizeAsync(
            new SummaryRequest(Alice, null, Messages((Alice, ChatChannel.Say, "hi")).ToList()), CancellationToken.None);

        using var doc = Body(Assert.Single(_handler.Requests));
        Assert.Equal(16384, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task Summarize_KeepsOnlyNewestMessagesWithinCharBudget()
    {
        _options = _options with { SummaryMaxInputChars = 500 };
        ReplyWith("ok");
        var texts = new[] { new string('a', 200), new string('b', 200), new string('c', 200), new string('d', 200) };
        var request = new SummaryRequest(Alice, null,
            texts.Select(t => Msg(Alice, t, ChatChannel.Say)).ToList());

        await _client.SummarizeAsync(request, CancellationToken.None);

        using var doc = Body(Assert.Single(_handler.Requests));
        var user = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.DoesNotContain(texts[0], user);
        Assert.DoesNotContain(texts[1], user);
        Assert.Contains(texts[2], user);
        Assert.Contains(texts[3], user);
        Assert.True(user.IndexOf(texts[2], StringComparison.Ordinal) < user.IndexOf(texts[3], StringComparison.Ordinal));

        // A single message over the budget is still sent.
        _handler.Requests.Clear();
        var huge = new string('z', 900);
        await _client.SummarizeAsync(
            new SummaryRequest(Alice, null, new[] { Msg(Alice, huge, ChatChannel.Say) }.ToList()), CancellationToken.None);
        using var doc2 = Body(Assert.Single(_handler.Requests));
        Assert.Contains(huge, doc2.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
    }

    [Fact]
    public async Task Summarize_NoPreviousSummary_SaysNone()
    {
        ReplyWith("ok");

        await _client.SummarizeAsync(
            new SummaryRequest(Alice, null, Messages((Alice, ChatChannel.Say, "hi")).ToList()), CancellationToken.None);

        using var doc = Body(_handler.Requests[0]);
        var user = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("Previous summary: none", user);
    }

    [Fact]
    public async Task Summarize_Http500_ThrowsAnalyzerUnavailable()
    {
        _handler.Respond = (_, _) => Task.FromResult(StubHttpMessageHandler.Json("{}", HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<AnalyzerUnavailableException>(() => _client.SummarizeAsync(
            new SummaryRequest(Alice, null, Messages((Alice, ChatChannel.Say, "hi")).ToList()), CancellationToken.None));
    }

    // ---- CheckHealth ----

    private void TagsReply(params string[] names) =>
        _handler.Respond = (_, _) => Task.FromResult(StubHttpMessageHandler.Json(
            "{\"models\":[" + string.Join(",", names.Select(n => $"{{\"name\":\"{n}\",\"model\":\"{n}\",\"size\":1}}")) + "]}"));

    [Fact]
    public async Task CheckHealth_ServerDown_ReturnsServerUnreachable()
    {
        _handler.Respond = (_, _) => throw new HttpRequestException("connection refused");

        var health = await _client.CheckHealthAsync(CancellationToken.None);

        Assert.Equal(OllamaHealthStatus.ServerUnreachable, health.Status);
        Assert.Empty(health.InstalledModels);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://127.0.0.1:11434/api/tags", request.Uri.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task CheckHealth_ErrorStatus_ReturnsServerUnreachable(HttpStatusCode status)
    {
        _handler.Respond = (_, _) => Task.FromResult(StubHttpMessageHandler.Json("{}", status));

        Assert.Equal(OllamaHealthStatus.ServerUnreachable, (await _client.CheckHealthAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task CheckHealth_BadBody_ReturnsServerUnreachable()
    {
        _handler.Respond = (_, _) => Task.FromResult(StubHttpMessageHandler.Json("<html>not ollama</html>"));

        Assert.Equal(OllamaHealthStatus.ServerUnreachable, (await _client.CheckHealthAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task CheckHealth_ModelNotInstalled_ReturnsModelMissing()
    {
        TagsReply("mistral:7b", "qwen2.5:1.5b");

        var health = await _client.CheckHealthAsync(CancellationToken.None);

        Assert.Equal(OllamaHealthStatus.ModelMissing, health.Status);
        Assert.Equal(new[] { "mistral:7b", "qwen2.5:1.5b" }, health.InstalledModels);
    }

    [Fact]
    public async Task CheckHealth_ModelInstalled_ReturnsOk()
    {
        TagsReply("mistral:7b", "llama3.2:3b");

        var health = await _client.CheckHealthAsync(CancellationToken.None);

        Assert.Equal(OllamaHealthStatus.Ok, health.Status);
        Assert.Equal(new[] { "mistral:7b", "llama3.2:3b" }, health.InstalledModels);
    }

    [Theory]
    [InlineData("llama3.2", "llama3.2:latest", true)]     // untagged name means :latest
    [InlineData("LLAMA3.2:3B", "llama3.2:3b", true)]      // case-insensitive
    [InlineData("llama3.2:3b", "llama3.2:latest", false)] // different tag
    [InlineData("llama3.2", "llama3.2:3b", false)]
    public async Task CheckHealth_ModelNameMatching(string wanted, string installed, bool expectOk)
    {
        _options = _options with { OllamaModel = wanted };
        TagsReply(installed);

        var health = await _client.CheckHealthAsync(CancellationToken.None);

        Assert.Equal(expectOk ? OllamaHealthStatus.Ok : OllamaHealthStatus.ModelMissing, health.Status);
    }

    [Fact]
    public async Task CheckHealth_CallerCancellation_ThrowsOperationCanceled()
    {
        _handler.Respond = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return StubHttpMessageHandler.Json("{}");
        };
        using var cts = new CancellationTokenSource();

        var call = _client.CheckHealthAsync(cts.Token);
        await _handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task CheckHealth_Timeout_ReturnsServerUnreachable()
    {
        _options = _options with { OllamaTimeout = TimeSpan.FromSeconds(30) };
        _handler.Respond = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return StubHttpMessageHandler.Json("{}");
        };

        var call = _client.CheckHealthAsync(CancellationToken.None);
        await _handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(OllamaHealthStatus.ServerUnreachable, (await call.WaitAsync(TimeSpan.FromSeconds(10))).Status);
    }
}
