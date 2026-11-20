using EnbNext.Net7.Transport;

namespace EnbNext.Net7.Tests;

public sealed class Net7TransportStateTests
{
    [Fact]
    public void ReceiveAdmission_UsesSingleAuthoritativeIdentity()
    {
        var state = new Net7TransportState();

        Assert.True(state.IsIncomingFixedSessionCompatible(0));
        Assert.False(state.IsIncomingFixedSessionCompatible(0x11111111));

        state.CommitValidatedFixedSessionId(0xAABBCCDD);

        Assert.True(state.IsIncomingFixedSessionCompatible(0));
        Assert.True(state.IsIncomingFixedSessionCompatible(0xAABBCCDD));
        Assert.False(state.IsIncomingFixedSessionCompatible(0x11111111));
    }

    [Fact]
    public void ResponseIdentityAssignmentReplacesPriorIdentityIncludingZero()
    {
        var state = new Net7TransportState();
        state.CommitValidatedFixedSessionId(0x01020304);
        state.CommitValidatedFixedSessionId(0x01020304);

        Assert.Equal(0x01020304u, state.CommitValidatedFixedSessionId(0xAABBCCDD));
        Assert.Equal(0xAABBCCDDu, state.FixedSessionId);
        Assert.Equal(0xAABBCCDDu, state.CommitValidatedFixedSessionId(0));
        Assert.Equal(0u, state.FixedSessionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixedControlUpdatesReceivingChannelAndControlWithoutClearingForwarding(bool controlChannel)
    {
        var state = new Net7TransportState();
        state.CommitValidatedFixedSessionId(0xAABBCCDD);
        state.CommitGameplayPort(3777);
        state.SetGameplayForwardingActive(true);
        state.ApplyFixedSessionControl(0, controlChannel);
        Assert.Equal(0u, state.ControlSessionId);
        Assert.Equal(controlChannel ? 0xAABBCCDDu : 0u, state.FixedSessionId);
        Assert.True(state.GameplayForwardingActive);
        Assert.Equal(controlChannel, state.TryGetActiveGameplayRoute(out _, out _));
        Assert.False(state.IsIncomingFixedSessionCompatible(0xAABBCCDD, controlChannel: true));
        Assert.Equal(controlChannel, state.IsIncomingFixedSessionCompatible(0xAABBCCDD));
        Assert.True(state.IsIncomingFixedSessionCompatible(0, controlChannel: true));
        state.CommitValidatedFixedSessionId(0x11223344);
        Assert.Equal(0x11223344u, state.ControlSessionId);
        Assert.True(state.TryGetActiveGameplayRoute(out uint id, out int port));
        Assert.Equal(0x11223344u, id);
        Assert.Equal(3777, port);
    }

    [Fact]
    public void GameplayRoute_RejectsPortsOutsideAuthoritativeCurrentRange()
    {
        var state = new Net7TransportState();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            state.CommitGameplayPort(Net7ServerOptions.CurrentMinGameplaySectorUdpPort - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            state.CommitGameplayPort(Net7ServerOptions.CurrentMaxGameplaySectorUdpPort + 1));

        state.CommitGameplayPort(Net7ServerOptions.CurrentMinGameplaySectorUdpPort);
        Assert.Equal(Net7ServerOptions.CurrentMinGameplaySectorUdpPort, state.ConfirmedGameplayPort);
        state.CommitGameplayPort(Net7ServerOptions.CurrentMaxGameplaySectorUdpPort);
        Assert.Equal(Net7ServerOptions.CurrentMaxGameplaySectorUdpPort, state.ConfirmedGameplayPort);
    }

    [Fact]
    public void GameplayRoute_RequiresIdentityPortAndExplicitActivation()
    {
        var state = new Net7TransportState();
        state.CommitGameplayPort(3777);

        Assert.False(state.TryGetActiveGameplayRoute(out _, out _));
        state.SetGameplayForwardingActive(true);
        Assert.True(state.GameplayForwardingActive);
        Assert.False(state.TryGetActiveGameplayRoute(out _, out _));
        state.SetGameplayForwardingActive(false);

        state.CommitValidatedFixedSessionId(0x01020304);
        Assert.False(state.TryGetActiveGameplayRoute(out _, out _));

        state.SetGameplayForwardingActive(true);
        Assert.True(state.TryGetActiveGameplayRoute(out uint fixedId, out int port));
        Assert.Equal(0x01020304u, fixedId);
        Assert.Equal(3777, port);
    }

    [Fact]
    public void DeactivationAndIdentityClear_KeepNumericRouteButMakeItInert()
    {
        var state = new Net7TransportState();
        state.CommitValidatedFixedSessionId(0x01020304);
        state.CommitGameplayPort(3777);
        state.SetGameplayForwardingActive(true);

        state.SetGameplayForwardingActive(false);
        Assert.Equal(3777, state.ConfirmedGameplayPort);
        Assert.False(state.TryGetActiveGameplayRoute(out _, out _));

        state.SetGameplayForwardingActive(true);
        state.ClearSessionIdentity();
        Assert.Equal(3777, state.ConfirmedGameplayPort);
        Assert.Equal(0u, state.FixedSessionId);
        Assert.False(state.GameplayForwardingActive);
        Assert.False(state.TryGetActiveGameplayRoute(out _, out _));
    }
    [Fact]
    public void JoinIdentityAssignmentRetainsRouteAndForwardingState()
    {
        var state = new Net7TransportState();
        state.CommitValidatedFixedSessionId(0x01020304);
        state.CommitGameplayPort(3777);
        state.SetGameplayForwardingActive(true);

        state.CommitValidatedFixedSessionId(0x11223344);

        Assert.Equal(0x11223344u, state.FixedSessionId);
        Assert.Equal(3777, state.ConfirmedGameplayPort);
        Assert.True(state.GameplayForwardingActive);
        Assert.True(state.TryGetActiveGameplayRoute(out uint id, out int port));
        Assert.Equal(0x11223344u, id);
        Assert.Equal(3777, port);
    }

}
