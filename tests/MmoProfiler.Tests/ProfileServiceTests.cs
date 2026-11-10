using MmoProfiler.Core;
using MmoProfiler.Tests.Fakes;
using static MmoProfiler.Tests.TestData;

namespace MmoProfiler.Tests;

public class ProfileServiceTests
{
    private readonly InMemoryProfileStore _store = new();
    private readonly InMemoryMessageLog _log = new();
    private readonly PendingSummaryBuffer _pending = new();
    private readonly ProfileService _service;

    public ProfileServiceTests() => _service = new ProfileService(_store, _log, _pending);

    [Fact]
    public async Task Mutate_CreatesProfileWhenMissing()
    {
        var alice = Key();

        await _service.MutateAsync(alice, p => p.MessageCount = 7);

        var saved = _service.Get(alice);
        Assert.NotNull(saved);
        Assert.Equal(7, saved!.MessageCount);
        Assert.Equal(alice.Id, saved.Id);
        Assert.Equal(alice, saved.Key);
    }

    [Fact]
    public async Task ApplyBatch_SavesAllProfilesInOneCall()
    {
        var keys = new[] { Key("Alice Example"), Key("Bob Example"), Key("Cara Example") };
        var changes = keys.ToDictionary(k => k, k => (Action<Profile>)(p => p.MessageCount += 2));

        await _service.ApplyBatchAsync(changes);

        Assert.Equal(1, _store.UpsertManyCalls);
        Assert.Equal(new[] { 3 }, _store.UpsertManySizes);
        Assert.Equal(0, _store.UpsertCalls);
        Assert.All(keys, k => Assert.Equal(2, _service.Get(k)!.MessageCount));
    }

    [Fact]
    public async Task SetNotes_TruncatesToLimit()
    {
        var alice = Key();

        await _service.SetNotesAsync(alice, new string('n', 5000));

        Assert.Equal(ProfileService.MaxNotesLength, _service.Get(alice)!.UserNotes.Length);
        Assert.Equal(4000, ProfileService.MaxNotesLength);
    }

    [Fact]
    public async Task ConcurrentMutations_DoNotLoseUpdates()
    {
        var alice = Key();
        const int workers = 16, perWorker = 50;

        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < perWorker; i++)
                await _service.MutateAsync(alice, p => p.MessageCount++);
        })));

        Assert.Equal(workers * perWorker, _service.Get(alice)!.MessageCount);
    }

    [Fact]
    public async Task Delete_RemovesProfileMessagesAndPendingBuffer()
    {
        var alice = Key("Alice Example");
        var bob = Key("Bob Example");
        await _service.MutateAsync(alice, p => p.MessageCount = 1);
        await _service.MutateAsync(bob, p => p.MessageCount = 1);
        _log.Append(new[]
        {
            new LoggedMessage(Msg(alice), MessageAnalysis.Unknown),
            new LoggedMessage(Msg(bob), MessageAnalysis.Unknown),
        });
        _pending.Add(Msg(alice));
        _pending.Add(Msg(bob));

        var existed = await _service.DeleteAsync(alice);

        Assert.True(existed);
        Assert.Null(_service.Get(alice));
        Assert.Empty(_log.GetRecent(alice, 10));
        Assert.Empty(_pending.Take(alice));
        // Other characters are untouched.
        Assert.NotNull(_service.Get(bob));
        Assert.Single(_log.GetRecent(bob, 10));
        Assert.Single(_pending.Take(bob));
    }

    [Fact]
    public async Task DeleteAll_ClearsEverything()
    {
        var alice = Key();
        await _service.MutateAsync(alice, p => p.MessageCount = 1);
        _log.Append(new[] { new LoggedMessage(Msg(alice), MessageAnalysis.Unknown) });
        _pending.Add(Msg(alice));

        await _service.DeleteAllAsync();

        Assert.Equal(0, _service.Count());
        Assert.Empty(_log.All);
        Assert.Equal(0, _pending.CharacterCount);
    }
}
