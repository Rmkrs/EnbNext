using EnbNext.Client;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class EnbQuitMenuStateTests
{
    [Theory]
    [InlineData("quit/BTTN00", Net7LogoffDestination.Desktop)]
    [InlineData("quit/BTTN01", Net7LogoffDestination.CharacterSelection)]
    [InlineData("quit/BTTN02", Net7LogoffDestination.LoginScreen)]
    public async Task AcceptedDestinationUsesCapturedOwnerAndHandoff(string key, Net7LogoffDestination destination)
    {
        var state = new EnbQuitMenuState(() => 42);
        var sequence = new List<string>();
        state.Open(42, 123);
        state.SoundRequested = sound => sequence.Add(sound);
        state.LogoffRequested = (character, handoff, target) =>
        {
            Assert.Equal(42u, character); Assert.Equal(123ul, handoff); Assert.Equal(destination, target);
            sequence.Add("send"); return Task.FromResult(true);
        };
        state.CloseRequested = all => { Assert.True(all); sequence.Add("close"); state.Retire(); };
        state.Changed = () => throw new Exception("Retired request must not republish.");
        await state.ActivateAsync(key, true);
        Assert.Equal(new[] { "Roll_Select02b.wav", "send", "close" }, sequence);
        Assert.False(state.IsOpen); Assert.False(state.IsSending);
    }

    [Theory]
    [InlineData("quit/EXIT")]
    [InlineData("quit/BTTN03")]
    public async Task CancelRequestsOnlyParentClose(string key)
    {
        var state = new EnbQuitMenuState(() => 42);
        state.Open(42, 9);
        int sounds = 0;
        state.SoundRequested = _ => sounds++;
        state.LogoffRequested = (_, _, _) => throw new Exception("Cancel sent logoff.");
        state.CloseRequested = all => { Assert.False(all); state.Retire(); };
        await state.ActivateAsync(key, true);
        Assert.Equal(1, sounds); Assert.False(state.IsOpen);
    }

    [Fact]
    public async Task RejectionRemainsOpenAndPendingRequestSuppressesDuplicates()
    {
        var state = new EnbQuitMenuState(() => 42);
        var pending = new TaskCompletionSource<bool>();
        int sends = 0, changes = 0;
        state.Open(42, 9);
        state.LogoffRequested = (_, _, _) => { sends++; return pending.Task; };
        state.Changed = () => changes++;
        state.CloseRequested = _ => throw new Exception("Pending/rejected request closed.");
        Task first = state.ActivateAsync("quit/BTTN00", true);
        await state.ActivateAsync("quit/BTTN01", true);
        await state.ActivateAsync("quit/EXIT", true);
        Assert.True(state.IsSending); Assert.Equal(1, sends);
        pending.SetResult(false); await first;
        Assert.True(state.IsOpen); Assert.False(state.IsSending); Assert.Equal(1, changes);
    }

    [Fact]
    public async Task RetiredCompletionCannotCloseOrClearReplacementRequest()
    {
        var state = new EnbQuitMenuState(() => 42);
        var first = new TaskCompletionSource<bool>(); var second = new TaskCompletionSource<bool>();
        state.Open(42, 9); state.LogoffRequested = (_, _, _) => first.Task;
        state.CloseRequested = _ => throw new Exception("Retired completion closed replacement.");
        Task old = state.ActivateAsync("quit/BTTN00", true);
        state.Retire(); state.Open(42, 10); state.LogoffRequested = (_, _, _) => second.Task;
        Task current = state.ActivateAsync("quit/BTTN01", true);
        first.SetResult(true); await old;
        Assert.True(state.IsOpen); Assert.True(state.IsSending); Assert.Equal(10ul, state.Handoff);
        second.SetResult(false); await current;
        Assert.False(state.IsSending);
    }

    [Fact]
    public async Task SessionChangeRejectsOldAcceptanceAndNewActions()
    {
        uint? session = 42;
        var state = new EnbQuitMenuState(() => session);
        var pending = new TaskCompletionSource<bool>(); int sends = 0;
        state.Open(42, 9);
        state.LogoffRequested = (_, _, _) => { sends++; return pending.Task; };
        state.CloseRequested = _ => throw new Exception("Old character closed UI.");
        Task first = state.ActivateAsync("quit/BTTN00", true);
        session = 43; pending.SetResult(true); await first;
        await state.ActivateAsync("quit/BTTN01", true);
        Assert.Equal(1, sends); Assert.False(state.IsSending);
    }

    [Fact]
    public async Task InertBackgroundAndSuspensionDoNotDispatch()
    {
        var state = new EnbQuitMenuState(() => 42);
        state.LogoffRequested = (_, _, _) => throw new Exception("Inert action dispatched.");
        await state.ActivateAsync("quit/BTTN00", true);
        state.Open(42, 9);
        await state.ActivateAsync("quit/FRAME", true);
        await state.ActivateAsync("quit/BTTN00", false);
        Assert.True(state.IsOpen); Assert.False(state.IsSending);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task FailedRouteDoesNotCloseOrRetry(int kind)
    {
        var state = new EnbQuitMenuState(() => 42); int sends = 0;
        state.Open(42, 9);
        state.LogoffRequested = (_, _, _) =>
        {
            sends++;
            return Task.FromException<bool>(kind switch
            {
                0 => new IOException(), 1 => new InvalidOperationException(), _ => new OperationCanceledException()
            });
        };
        state.CloseRequested = _ => throw new Exception("Failure closed UI.");
        await state.ActivateAsync("quit/BTTN00", true);
        Assert.Equal(1, sends); Assert.True(state.IsOpen); Assert.False(state.IsSending);
    }
}
