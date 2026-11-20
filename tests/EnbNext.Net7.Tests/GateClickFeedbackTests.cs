using EnbNext.Engine;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class GateClickFeedbackTests
{
    [Theory]
    [InlineData(0x000A0000u)]
    [InlineData(0x00040000u)]
    [InlineData(0x00060000u)]
    public async Task SuccessfulActionPlaysExactlyOneCueAfterDispatch(uint verb)
    {
        var sent = new TaskCompletionSource<bool>();
        var events = new List<string>();
        var ui = new EnbScannerUi((_, _) => Task.FromResult(true), (target, value, _) =>
        {
            Assert.Equal(42u, target);
            Assert.Equal(verb, value);
            events.Add("sent");
            return sent.Task;
        });
        ui.InteractionSoundRequested += events.Add;
        Task action = ui.ExecuteVerbSafelyAsync(42, verb);
        Assert.Equal(new[] { "sent" }, events);
        sent.SetResult(true);
        await action;
        Assert.Equal(new[] { "sent", "SR_scifielectronic40.wav" }, events);
        Assert.Null(ui.VerbExecutionError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsentGateDoesNotPlaySuccessCue(bool throws)
    {
        var ui = new EnbScannerUi((_, _) => Task.FromResult(true), (_, _, _) => throws
            ? Task.FromException<bool>(new InvalidOperationException("fixture")) : Task.FromResult(false));
        var sounds = new List<string>();
        ui.InteractionSoundRequested += sounds.Add;
        await ui.ExecuteVerbSafelyAsync(42, 0x000A0000);
        Assert.Empty(sounds);
        Assert.NotNull(ui.VerbExecutionError);
    }
}
