using MmoProfiler.Core;

namespace MmoProfiler.Tests.Fakes;

/// <summary>Stores deep copies, like a real database, so unsaved mutations never leak into the store.</summary>
public sealed class InMemoryProfileStore : IProfileStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Profile> _items = new();

    public int UpsertCalls { get; private set; }
    public int UpsertManyCalls { get; private set; }
    public List<int> UpsertManySizes { get; } = new();
    public Exception? ThrowOnNextUpsertMany { get; set; }

    public Profile? Get(CharacterKey key)
    {
        lock (_gate) return _items.TryGetValue(key.Id, out var p) ? Clone(p) : null;
    }

    public void Upsert(Profile profile)
    {
        lock (_gate)
        {
            UpsertCalls++;
            _items[profile.Id] = Clone(profile);
        }
    }

    public void UpsertMany(IEnumerable<Profile> profiles)
    {
        lock (_gate)
        {
            if (ThrowOnNextUpsertMany is { } ex)
            {
                ThrowOnNextUpsertMany = null;
                throw ex;
            }

            UpsertManyCalls++;
            var list = profiles.ToList();
            UpsertManySizes.Add(list.Count);
            foreach (var p in list) _items[p.Id] = Clone(p);
        }
    }

    public IReadOnlyList<Profile> List(string? nameContains, int skip, int take)
    {
        lock (_gate)
        {
            return _items.Values
                .Where(p => string.IsNullOrEmpty(nameContains)
                            || p.Key.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.LastSeenUtc)
                .Skip(skip).Take(take)
                .Select(Clone).ToList();
        }
    }

    public long Count()
    {
        lock (_gate) return _items.Count;
    }

    public bool Delete(CharacterKey key)
    {
        lock (_gate) return _items.Remove(key.Id);
    }

    public void DeleteAll()
    {
        lock (_gate) _items.Clear();
    }

    public static Profile Clone(Profile p) => new()
    {
        SchemaVersion = p.SchemaVersion,
        Id = p.Id,
        Key = p.Key,
        FirstSeenUtc = p.FirstSeenUtc,
        LastSeenUtc = p.LastSeenUtc,
        MessageCount = p.MessageCount,
        AnalyzedCount = p.AnalyzedCount,
        ChannelCounts = new(p.ChannelCounts),
        ToneCounts = new(p.ToneCounts),
        IntentCounts = new(p.IntentCounts),
        LanguageCounts = new(p.LanguageCounts),
        TopicCounts = new(p.TopicCounts),
        ActivityByHourOfWeekUtc = (int[])p.ActivityByHourOfWeekUtc.Clone(),
        Summary = p.Summary,
        SummaryUpdatedUtc = p.SummaryUpdatedUtc,
        MessagesSinceSummary = p.MessagesSinceSummary,
        UserNotes = p.UserNotes,
    };
}

public sealed class InMemoryMessageLog : IMessageLog
{
    private readonly object _gate = new();
    private readonly List<LoggedMessage> _items = new();

    public int AppendCalls { get; private set; }
    public List<DateTimeOffset> PurgeCutoffs { get; } = new();

    public IReadOnlyList<LoggedMessage> All
    {
        get
        {
            lock (_gate) return _items.ToList();
        }
    }

    public void Append(IEnumerable<LoggedMessage> messages)
    {
        lock (_gate)
        {
            AppendCalls++;
            _items.AddRange(messages);
        }
    }

    public IReadOnlyList<LoggedMessage> GetRecent(CharacterKey key, int take)
    {
        lock (_gate)
        {
            return _items.Where(m => m.Message.Sender.Equals(key))
                .Reverse()
                .OrderByDescending(m => m.Message.TimestampUtc)
                .Take(take).ToList();
        }
    }

    public int DeleteFor(CharacterKey key)
    {
        lock (_gate) return _items.RemoveAll(m => m.Message.Sender.Equals(key));
    }

    public int DeleteOlderThan(DateTimeOffset cutoffUtc)
    {
        lock (_gate)
        {
            PurgeCutoffs.Add(cutoffUtc);
            return _items.RemoveAll(m => m.Message.TimestampUtc < cutoffUtc);
        }
    }

    public void DeleteAll()
    {
        lock (_gate) _items.Clear();
    }
}
