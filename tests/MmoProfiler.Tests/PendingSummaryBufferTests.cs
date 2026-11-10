using MmoProfiler.Core;
using static MmoProfiler.Tests.TestData;

namespace MmoProfiler.Tests;

public class PendingSummaryBufferTests
{
    [Fact]
    public void KeepsLastThirtyPerCharacter()
    {
        var buffer = new PendingSummaryBuffer();
        var alice = Key();
        for (var i = 1; i <= 45; i++) buffer.Add(Msg(alice, $"m{i}"));

        var taken = buffer.Take(alice);

        Assert.Equal(30, taken.Count);
        Assert.Equal("m16", taken[0].Text);
        Assert.Equal("m45", taken[^1].Text);
        // Take keeps the messages.
        Assert.Equal(30, buffer.Take(alice).Count);
    }

    [Fact]
    public void EvictsLeastRecentlyUpdatedCharacter_OverFiveHundred()
    {
        var buffer = new PendingSummaryBuffer();
        var people = Enumerable.Range(0, 501).Select(i => Key($"Person{i:000} Test")).ToList();
        for (var i = 0; i < 500; i++) buffer.Add(Msg(people[i]));

        buffer.Add(Msg(people[0], "refresh")); // person 0 is now the most recently updated
        buffer.Add(Msg(people[500]));          // forces an eviction

        Assert.Equal(500, buffer.CharacterCount);
        Assert.Empty(buffer.Take(people[1]));  // oldest untouched entry went
        Assert.Equal(2, buffer.Take(people[0]).Count);
        Assert.Single(buffer.Take(people[500]));
        Assert.Single(buffer.Take(people[2]));
    }

    [Fact]
    public void Clear_RemovesOnlyThatCharacter()
    {
        var buffer = new PendingSummaryBuffer();
        var alice = Key("Alice Example");
        var bob = Key("Bob Example");
        buffer.Add(Msg(alice));
        buffer.Add(Msg(bob));

        buffer.Clear(alice);

        Assert.Empty(buffer.Take(alice));
        Assert.Single(buffer.Take(bob));

        buffer.Remove(bob);
        Assert.Empty(buffer.Take(bob));
    }
}
