using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7ResidualInboundStateTests
{
    [Fact]
    public void ModeRequestRetainsIgnoredFieldAndOnlySelectsTheTwoProvenTransitions()
    {
        var state = new Net7GameplayState();

        state.Apply(Packet.ForOpcode(0x004C, Convert.FromHexString("DDCCBBAA00000000")));
        Assert.Equal(0xAABBCCDDu, state.LastModeRequest!.Field0);
        Assert.Equal(Net7ModeTransitionKind.Mode0, state.LastModeTransition);

        state.Apply(Packet.ForOpcode(0x004C, Convert.FromHexString("0403020101000000")));
        Assert.Equal(0x01020304u, state.LastModeRequest!.Field0);
        Assert.Equal(Net7ModeTransitionKind.Mode1, state.LastModeTransition);
    }

    [Fact]
    public void ReferrerWarpAndEmoteMaterializeTheirBoundedCurrentConsequences()
    {
        var world = new EnbNext.Net7.World.Net7WorldState();
        world.MaterializeCreate(new EnbNext.Net7.World.Net7CreateUpdate(0x01020304, 1, 0, 0, 0, 0, 0));
        var state = new Net7GameplayState(resolveWorldObject: world.Objects.FindFirst);

        state.Apply(Packet.ForOpcode(0x0078, Convert.FromHexString("FFFE")));
        Assert.Equal((short)-2, state.GlobalReferrerResult);
        Assert.False(state.GlobalReferrerReadinessCleared);

        state.Apply(Packet.ForOpcode(0x0078, Convert.FromHexString("0002")));
        Assert.Equal((short)2, state.GlobalReferrerResult);
        Assert.True(state.GlobalReferrerReadinessCleared);

        state.Apply(Packet.ForOpcode(0x009C, Convert.FromHexString("78563412")));
        Assert.Equal(0x12345678u, state.WarpIndex);

        state.Apply(Packet.ForOpcode(0x00A2, Convert.FromHexString("0102030444332211")));
        Assert.Equal(0x11223344u, state.Emotes[0x01020304]);
    }

    [Fact]
    public void SectorResidualStateClearsOnHandoffWhileGlobalReferrerStateSurvivesUntilCharacterSelection()
    {
        var state = new Net7GameplayState();
        state.Apply(Packet.ForOpcode(0x004C, new byte[8]));
        state.Apply(Packet.ForOpcode(0x0078, Convert.FromHexString("0001")));
        state.Apply(Packet.ForOpcode(0x009C, new byte[4]));
        state.Apply(Packet.ForOpcode(0x00A2, new byte[8]));

        state.ResetForServerHandoff();
        Assert.Null(state.LastModeRequest);
        Assert.Null(state.LastModeTransition);
        Assert.Null(state.WarpIndex);
        Assert.Empty(state.Emotes);
        Assert.Equal((short)1, state.GlobalReferrerResult);
        Assert.True(state.GlobalReferrerReadinessCleared);

        state.ResetForCharacterSelection();
        Assert.Null(state.GlobalReferrerResult);
        Assert.False(state.GlobalReferrerReadinessCleared);
    }

}
