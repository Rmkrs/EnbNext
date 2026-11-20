using System.Text;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.Social;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbTimedMessageLineTests
{
    [Fact]
    public void EffectDrivenLineWaitsForCompletionAndCannotBeSkippedOrInterrupted()
    {
        var q = new EnbTimedMessageLineQueue();
        var effect = Line("effect", 0) with { EphemeralType = "Simple" };
        q.Enqueue(effect); q.Advance(0);
        q.Enqueue(Line("urgent", 10, Net7MessageLineKind.Push));
        q.NavigateHistory(false); q.Advance(100000);
        Assert.Same(effect, q.Current);
        q.Advance(100001, effectsComplete: true);
        Assert.Equal("urgent", q.Current!.Text);
        // Retail keeps the displaced effect entry and starts it again later.
        q.Advance(100011); Assert.Same(effect, q.Current);
        q.Advance(100012, effectsComplete: true); Assert.Null(q.Current);
        q.NavigateHistory(true); q.Advance(100012);
        Assert.Equal("(1) effect", q.DisplayText);
        Assert.True(q.Replaying);
        q.Advance(105012); Assert.Null(q.Current);
    }

    [Fact]
    public void EffectDrivenLineDefersHistoryUntilItCompletes()
    {
        var q = new EnbTimedMessageLineQueue();
        q.Enqueue(Line("history", 1)); q.Advance(0); q.Advance(1);
        q.Enqueue(Line("effect", 0)); q.Advance(1);
        q.NavigateHistory(true); q.Advance(2000);
        Assert.Equal("effect", q.DisplayText); Assert.False(q.Replaying);
        q.Advance(2001, effectsComplete: true);
        Assert.Equal("(2) history", q.DisplayText);
    }

    private static Net7MessageLineUpdate Line(string text, uint time = 5000,
        Net7MessageLineKind kind = Net7MessageLineKind.Queue, uint priority = 0)
        => new(kind, text, "MessageLine", time, priority);

    [Fact]
    public void QueueDisplaysEachLineForItsOwnTimeIncludingZeroXpAndDuplicates()
    {
        var q = new EnbTimedMessageLineQueue();
        var first = Line("Prospected Oxygen: 0 Explore experience earned.");
        q.Enqueue(first); q.Enqueue(first); q.Enqueue(Line("Emptied Asteroid: 65 Explore experience earned.", 1000));
        Assert.True(q.Advance(100)); Assert.Same(first, q.Current);
        Assert.False(q.Advance(5099)); Assert.True(q.Advance(5100)); Assert.Same(first, q.Current);
        Assert.True(q.Advance(10100)); Assert.StartsWith("Emptied", q.Current!.Text);
        Assert.False(q.Advance(11099)); Assert.True(q.Advance(11100)); Assert.Null(q.Current);
    }

    [Fact]
    public void PushAndPriorityInterruptWithRemainingTimeAndStableEqualOrdering()
    {
        var q = new EnbTimedMessageLineQueue();
        q.Enqueue(Line("ordinary", 5000)); q.Advance(0); q.Advance(1000);
        q.Enqueue(Line("push", 1000, Net7MessageLineKind.Push, 999)); q.Advance(1000);
        Assert.Equal("push", q.Current!.Text);
        q.Enqueue(Line("equal", 1000, Net7MessageLineKind.Priority, 3)); q.Advance(1500);
        Assert.Equal("push", q.Current!.Text);
        q.Enqueue(Line("priority", 500, Net7MessageLineKind.Priority, 1)); q.Advance(1500);
        Assert.Equal("priority", q.Current!.Text);
        q.Advance(2000); Assert.Equal("push", q.Current!.Text);
        q.Advance(2500); Assert.Equal("equal", q.Current!.Text);
        q.Advance(3500); Assert.Equal("ordinary", q.Current!.Text);
        q.Advance(7499); Assert.NotNull(q.Current);
        q.Advance(7500); Assert.Null(q.Current);
    }

    [Fact]
    public void HistoryWalksBothWaysAndReturnsToInterruptedQueue()
    {
        var q = new EnbTimedMessageLineQueue();
        q.Enqueue(Line("first", 10)); q.Enqueue(Line("second", 10)); q.Enqueue(Line("waiting", 100));
        q.Advance(0); q.Advance(10); q.Advance(20); q.Advance(30);
        q.NavigateHistory(true); q.Advance(30);
        Assert.Equal("(1) second", q.DisplayText);
        q.NavigateHistory(true); q.Advance(40);
        Assert.Equal("(2) first", q.DisplayText);
        q.NavigateHistory(false); q.Advance(50);
        Assert.Equal("(1) second", q.DisplayText);
        q.NavigateHistory(false); q.Advance(60);
        Assert.Equal("waiting", q.DisplayText);
        q.Advance(149); Assert.Equal("waiting", q.DisplayText);
        q.Advance(150); Assert.Null(q.Current);
        q.NavigateHistory(true); q.Advance(150);
        Assert.Equal("(1) waiting", q.DisplayText);
        q.Advance(5149); Assert.NotNull(q.Current);
        q.Advance(5150); Assert.Null(q.Current);
    }

    [Fact]
    public void HistoryRetainsThirtyCompletionsAndPreviousPastOldestExits()
    {
        var q = new EnbTimedMessageLineQueue();
        for (int i = 0; i < 32; i++) q.Enqueue(Line(i.ToString(), 1));
        for (int i = 0; i <= 32; i++) q.Advance(i);
        for (int i = 0; i < 30; i++) { q.NavigateHistory(true); q.Advance(32); }
        Assert.Equal("(30) 2", q.DisplayText);
        q.NavigateHistory(true); q.Advance(32); Assert.Null(q.Current);
        q.NavigateHistory(true); q.Advance(32); Assert.Equal("(1) 31", q.DisplayText);
    }

    [Fact]
    public void NextSkipsCurrentAndClearArchivesPendingButResetDropsHistory()
    {
        var q = new EnbTimedMessageLineQueue();
        q.Enqueue(Line("one")); q.Enqueue(Line("two")); q.Advance(0);
        q.NavigateHistory(false); q.Advance(1);
        Assert.Equal("two", q.DisplayText);
        q.ClearPending(); q.Advance(2); Assert.Null(q.Current);
        q.NavigateHistory(true); q.Advance(2); Assert.Equal("(1) two", q.DisplayText);
        q.NavigateHistory(true); q.Advance(2); Assert.Equal("(2) one", q.DisplayText);
        q.Clear(); q.NavigateHistory(true); q.Advance(3); Assert.Null(q.Current);
    }

    [Fact]
    public void IncomingPriorityWaitsUntilRecallEnds()
    {
        var q = new EnbTimedMessageLineQueue();
        q.Enqueue(Line("history", 1)); q.Advance(0); q.Advance(1);
        q.NavigateHistory(true); q.Advance(1);
        q.Enqueue(Line("urgent", 1, Net7MessageLineKind.Push)); q.Advance(4000);
        Assert.Equal("(1) history", q.DisplayText);
        q.Advance(5001); Assert.Equal("urgent", q.DisplayText);
        q.Advance(5002); Assert.Null(q.Current);
    }

    [Fact]
    public void DecodedLinesAndClearReachPresentationOnceWithoutEnteringChatHistory()
    {
        var game = new Net7GameplayState();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("Prospected Oxygen: 0 Explore experience earned.\0MessageLine\0"));
            writer.Write(1234u); writer.Write(9u);
        }
        game.Apply(Packet.ForOpcode(0x0021, stream.ToArray()));
        var snapshot = EnbSceneChatSnapshot.From(game.Social);
        Assert.Same(snapshot, EnbSceneChatSnapshot.From(game.Social));
        var e = Assert.Single(snapshot.Events);
        Assert.Equal(Net7ChatPresentationKind.ScreenLine, e.Kind);
        Assert.Equal(1234u, e.ScreenLine!.TimeMilliseconds);
        Assert.Empty(snapshot.Lines);
        game.Apply(Packet.ForOpcode(0x0024, Array.Empty<byte>()));
        var cleared = EnbSceneChatSnapshot.From(game.Social);
        Assert.Equal(Net7ChatPresentationKind.ClearScreenLines, cleared.Events.Last().Kind);
        Assert.True(cleared.Events.Last().Sequence > e.Sequence);
        Assert.Empty(game.MessageLines);
        var q = new EnbTimedMessageLineQueue(); q.Enqueue(e.ScreenLine); q.Advance(0); q.Clear();
        Assert.Null(q.Current); Assert.False(q.Advance(100));
    }
}
