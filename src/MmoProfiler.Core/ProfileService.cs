namespace MmoProfiler.Core;

/// <summary>
/// Owns the store, the optional message log and the mutation lock. Every profile write goes through
/// here so read-modify-write cycles are serialised.
/// </summary>
public sealed class ProfileService
{
    public const int MaxNotesLength = 4000;

    private readonly IProfileStore _store;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ProfileService(IProfileStore store, IMessageLog? messageLog = null, PendingSummaryBuffer? pending = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        MessageLog = messageLog;
        Pending = pending ?? new PendingSummaryBuffer();
    }

    public IMessageLog? MessageLog { get; }

    public PendingSummaryBuffer Pending { get; }

    public Profile? Get(CharacterKey key) => _store.Get(key);

    public IReadOnlyList<Profile> List(string? nameContains, int skip, int take) =>
        _store.List(nameContains, skip, take);

    public long Count() => _store.Count();

    /// <summary>Loads or creates the profile, applies the change and saves, under the lock.</summary>
    public async Task MutateAsync(CharacterKey key, Action<Profile> change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(change);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var profile = _store.Get(key) ?? Profile.Create(key);
            change(profile);
            _store.Upsert(profile);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Same as <see cref="MutateAsync"/> for many profiles, saved with one <c>UpsertMany</c>.</summary>
    public async Task ApplyBatchAsync(
        IReadOnlyDictionary<CharacterKey, Action<Profile>> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0) return;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var updated = new List<Profile>(changes.Count);
            foreach (var (key, change) in changes)
            {
                var profile = _store.Get(key) ?? Profile.Create(key);
                change(profile);
                updated.Add(profile);
            }
            _store.UpsertMany(updated);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Saves the user's notes, truncated to <see cref="MaxNotesLength"/>. Creates the profile if needed.</summary>
    public Task SetNotesAsync(CharacterKey key, string notes, CancellationToken ct = default)
    {
        var text = notes ?? "";
        if (text.Length > MaxNotesLength) text = text[..MaxNotesLength];
        return MutateAsync(key, p => p.UserNotes = text, ct);
    }

    /// <summary>Removes the profile, its logged messages and its pending-summary entries.</summary>
    public async Task<bool> DeleteAsync(CharacterKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existed = _store.Delete(key);
            MessageLog?.DeleteFor(key);
            Pending.Remove(key);
            return existed;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _store.DeleteAll();
            MessageLog?.DeleteAll();
            Pending.ClearAll();
        }
        finally
        {
            _lock.Release();
        }
    }
}
