namespace MmoProfiler.Core;

public interface IMessageAnalyzer
{
    /// <summary>Returns one result per input message, in the same order.</summary>
    Task<IReadOnlyList<MessageAnalysis>> AnalyzeAsync(
        IReadOnlyList<ChatMessage> messages, CancellationToken ct);
}

public sealed record SummaryRequest(
    CharacterKey Character, string? PreviousSummary, IReadOnlyList<ChatMessage> RecentMessages);

public interface IProfileSummarizer
{
    Task<string> SummarizeAsync(SummaryRequest request, CancellationToken ct);
}

/// <summary>Thrown when the analysis backend cannot be reached or returns unusable output.</summary>
public sealed class AnalyzerUnavailableException : Exception
{
    public AnalyzerUnavailableException() { }
    public AnalyzerUnavailableException(string message) : base(message) { }
    public AnalyzerUnavailableException(string message, Exception? innerException) : base(message, innerException) { }
}

public interface IProfileStore
{
    Profile? Get(CharacterKey key);
    void Upsert(Profile profile);
    void UpsertMany(IEnumerable<Profile> profiles);
    /// <summary>Ordered by LastSeenUtc descending.</summary>
    IReadOnlyList<Profile> List(string? nameContains, int skip, int take);
    long Count();
    bool Delete(CharacterKey key);
    void DeleteAll();
}

public sealed record LoggedMessage(ChatMessage Message, MessageAnalysis Analysis);

public interface IMessageLog
{
    void Append(IEnumerable<LoggedMessage> messages);
    /// <summary>Newest first.</summary>
    IReadOnlyList<LoggedMessage> GetRecent(CharacterKey key, int take);
    int DeleteFor(CharacterKey key);
    int DeleteOlderThan(DateTimeOffset cutoffUtc);
    void DeleteAll();
}
