namespace MmoProfiler.Core;

/// <summary>
/// Recent message text per character, kept in memory so summaries work when raw storage is off.
/// Contents are lost on unload by design.
/// </summary>
public sealed class PendingSummaryBuffer
{
    public const int DefaultMaxMessagesPerCharacter = 30;
    public const int DefaultMaxCharacters = 500;

    private sealed class Entry
    {
        public readonly List<ChatMessage> Messages = new();
        public long Stamp;
    }

    private readonly int _maxPerCharacter;
    private readonly int _maxCharacters;
    private readonly Dictionary<CharacterKey, Entry> _entries = new();
    private readonly object _gate = new();
    private long _clock;

    public PendingSummaryBuffer(
        int maxMessagesPerCharacter = DefaultMaxMessagesPerCharacter,
        int maxCharacters = DefaultMaxCharacters)
    {
        _maxPerCharacter = Math.Max(1, maxMessagesPerCharacter);
        _maxCharacters = Math.Max(1, maxCharacters);
    }

    /// <summary>Number of characters currently buffered.</summary>
    public int CharacterCount
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    public void Add(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            if (!_entries.TryGetValue(message.Sender, out var entry))
            {
                if (_entries.Count >= _maxCharacters) EvictLeastRecentlyUpdated();
                entry = new Entry();
                _entries[message.Sender] = entry;
            }

            entry.Messages.Add(message);
            if (entry.Messages.Count > _maxPerCharacter) entry.Messages.RemoveAt(0);
            entry.Stamp = ++_clock;
        }
    }

    /// <summary>Returns a copy of the buffered messages, oldest first, and keeps them.</summary>
    public IReadOnlyList<ChatMessage> Take(CharacterKey key)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) ? entry.Messages.ToArray() : Array.Empty<ChatMessage>();
        }
    }

    /// <summary>Forgets this character's buffered messages (called after a summary is written).</summary>
    public void Clear(CharacterKey key)
    {
        lock (_gate) _entries.Remove(key);
    }

    /// <summary>Forgets this character entirely (called when the profile is deleted).</summary>
    public void Remove(CharacterKey key)
    {
        lock (_gate) _entries.Remove(key);
    }

    public void ClearAll()
    {
        lock (_gate) _entries.Clear();
    }

    private void EvictLeastRecentlyUpdated()
    {
        CharacterKey? victim = null;
        var oldest = long.MaxValue;
        foreach (var (key, entry) in _entries)
        {
            if (entry.Stamp < oldest)
            {
                oldest = entry.Stamp;
                victim = key;
            }
        }
        if (victim is not null) _entries.Remove(victim);
    }
}
