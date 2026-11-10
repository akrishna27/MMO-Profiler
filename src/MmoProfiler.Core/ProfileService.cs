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
    private readonly List<ProfileBatchScope> _scopes = new();
    private readonly object _scopeGate = new();

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

    /// <summary>Applies the change only when the profile already exists. Never creates a profile.</summary>
    public async Task<bool> UpdateIfExistsAsync(CharacterKey key, Action<Profile> change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(change);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var profile = _store.Get(key);
            if (profile is null) return false;
            change(profile);
            _store.Upsert(profile);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Opens a scope that records deletes made while it is open, so work already in flight cannot undo them.
    /// Dispose it when the work is finished.
    /// </summary>
    public ProfileBatchScope BeginBatch()
    {
        var scope = new ProfileBatchScope(this);
        lock (_scopeGate) _scopes.Add(scope);
        return scope;
    }

    internal void EndBatch(ProfileBatchScope scope)
    {
        lock (_scopeGate) _scopes.Remove(scope);
    }

    /// <summary>
    /// Like <see cref="ApplyBatchAsync(IReadOnlyDictionary{CharacterKey, Action{Profile}}, CancellationToken)"/> but
    /// skips every key deleted during <paramref name="scope"/>, and everything if the scope was invalidated.
    /// </summary>
    public async Task ApplyBatchAsync(
        IReadOnlyDictionary<CharacterKey, Action<Profile>> changes, ProfileBatchScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(scope);
        if (changes.Count == 0) return;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Deletes also take the lock, so the check and the save cannot interleave with one.
            if (scope.IsInvalidated) return;
            var updated = new List<Profile>(changes.Count);
            foreach (var (key, change) in changes)
            {
                if (scope.IsDeleted(key)) continue;
                var profile = _store.Get(key) ?? Profile.Create(key);
                change(profile);
                updated.Add(profile);
            }
            if (updated.Count > 0) _store.UpsertMany(updated);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Appends to the message log, skipping characters deleted during <paramref name="scope"/>.</summary>
    public async Task AppendToLogAsync(
        IReadOnlyList<LoggedMessage> messages, ProfileBatchScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(scope);
        if (MessageLog is null || messages.Count == 0) return;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (scope.IsInvalidated) return;
            var keep = messages.Where(m => !scope.IsDeleted(m.Message.Sender)).ToList();
            if (keep.Count > 0) MessageLog.Append(keep);
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
            lock (_scopeGate)
            {
                foreach (var scope in _scopes) scope.RecordDeleted(key);
            }
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
            lock (_scopeGate)
            {
                foreach (var scope in _scopes) scope.Invalidate();
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>Remembers deletes made while it is open. See <see cref="ProfileService.BeginBatch"/>.</summary>
public sealed class ProfileBatchScope : IDisposable
{
    private readonly ProfileService _owner;
    private readonly HashSet<CharacterKey> _deleted = new();
    private readonly object _gate = new();
    private volatile bool _invalidated;
    private bool _disposed;

    internal ProfileBatchScope(ProfileService owner) => _owner = owner;

    /// <summary>True once Delete All was used while the scope was open.</summary>
    public bool IsInvalidated => _invalidated;

    public bool IsDeleted(CharacterKey key)
    {
        if (_invalidated) return true;
        lock (_gate) return _deleted.Contains(key);
    }

    internal void RecordDeleted(CharacterKey key)
    {
        lock (_gate) _deleted.Add(key);
    }

    internal void Invalidate() => _invalidated = true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.EndBatch(this);
    }
}
