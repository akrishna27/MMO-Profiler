using System.Net;
using System.Text;

namespace MmoProfiler.Tests.Fakes;

public sealed record CapturedRequest(HttpMethod Method, Uri Uri, string Body);

/// <summary>Scripted HTTP handler. Never touches the network. Records every request it receives.</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    public delegate Task<HttpResponseMessage> Responder(CapturedRequest request, CancellationToken ct);

    private readonly object _gate = new();

    public Responder Respond { get; set; } = (_, _) => throw new InvalidOperationException("No response scripted.");

    public List<CapturedRequest> Requests { get; } = new();

    /// <summary>Completed when the first request arrives.</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var captured = new CapturedRequest(request.Method, request.RequestUri!, body);
        lock (_gate) Requests.Add(captured);
        Entered.TrySetResult();
        return await Respond(captured, ct).ConfigureAwait(false);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A chat reply whose <c>message.content</c> is the given text.</summary>
    public static HttpResponseMessage ChatReply(string content) =>
        Json(System.Text.Json.JsonSerializer.Serialize(new
        {
            model = "llama3.2:3b",
            created_at = "2026-11-09T00:00:00Z",
            message = new { role = "assistant", content },
            done = true,
        }));
}
