using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7UiPresentationStateTests
{
    [Fact]
    public void ConfirmationWithMissingInitializedIdStillOpensAndTimesOut()
    {
        var state = new Net7GameplayState();
        state.Apply(Packet.ForOpcode(0x00BE, new byte[] {0, 0, 0, 1}));
        Assert.Equal(0u, state.IncomingMessage!.Id);
        Assert.Equal("", state.IncomingMessage.Message);
        state.Apply(Packet.ForOpcode(0x00BF, new byte[] {0, 0, 0, 1}));
        Assert.Null(state.IncomingMessage);
        state.Apply(Packet.ForOpcode(0x00BE, new byte[] {0, 0, 0, 1, 0x12}));
        Assert.Equal(0x12000000u, state.IncomingMessage!.Id);
        state.Apply(Packet.ForOpcode(0x00BF, new byte[] {0, 0, 0, 1, 0x12}));
        Assert.Null(state.IncomingMessage);
    }

    [Fact]
    public void I04FamiliesMaterializeTransientUiAndStarbaseState()
    {
        var world = new EnbNext.Net7.World.Net7WorldState();
        world.MaterializeCreate(new EnbNext.Net7.World.Net7CreateUpdate(0x11223344, 1, 0, 0, 0, 0, 0));
        var state = new Net7GameplayState(resolveWorldObject: world.Objects.FindFirst);

        foreach (ushort opcode in new ushort[] { 0x0020, 0x0021, 0x0022 })
            Assert.True(state.Apply(Packet.ForOpcode(opcode, Net7UiPresentationWireTests.Build(w => { w.CString($"M{opcode:X}"); w.CString("T"); w.U32(1); w.U32(2); }))));
        Assert.Equal(3, state.MessageLines.Count);
        state.Apply(Packet.ForOpcode(0x0023, Encoding.Latin1.GetBytes("SUPER\0")));
        Assert.Equal("SUPER", state.SuperLine!.Text);
        state.Apply(Packet.ForOpcode(0x0024, Array.Empty<byte>()));
        Assert.Empty(state.MessageLines);
        Assert.Equal("SUPER", state.SuperLine!.Text);

        state.Apply(Packet.ForOpcode(0x004F, Convert.FromHexString("443322110102")));
        Assert.Equal(0x11223344u, state.LastStarbaseSet!.StarbaseId);

        byte[] tow = new byte[24];
        for (int i = 0; i < 6; i++) Net7UiPresentationWireTests.WriteU32(tow, i * 4, (uint)(i + 1));
        state.Apply(Packet.ForOpcode(0x0050, tow));
        Assert.Equal(4u, state.StarbaseTow!.Options[0].TowCost);

        byte[] lounge = Net7UiPresentationWireTests.Build(w => { w.U32(9); w.U32(0); w.U32(0); w.U32(0); });
        state.Apply(Packet.ForOpcode(0x0052, lounge));
        Assert.Equal(9u, state.LoungeDefinition!.StarbaseDefinitionValue);

        state.Apply(Packet.ForOpcode(0x0054, Net7UiPresentationWireTests.Build(w => { w.CString("Hi"); w.CString(""); w.U8(1); w.U32(44); w.CString("Bye"); })));
        Assert.Equal(44u, state.TalkTree!.Responses.Single().ResponseActionOrId);
        state.Apply(Packet.ForOpcode(0x0056, Convert.FromHexString("78563412")));
        Assert.Equal(0x12345678u, state.TalkTreeAction!.Action);

        state.Apply(Packet.ForOpcode(0x005B, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(7); w.NetworkU32(3); w.U32(0x30000); w.U32(0x10000); w.U32(0x20000); })));
        Assert.Equal(new uint[] { 0x10000, 0x20000, 0x30000 }, state.VerbLists[7]);
        state.Apply(Packet.ForOpcode(0x005C, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(7); w.NetworkU32(1); w.U32(0x20000); w.NetworkU32(2); w.U32(0x40000); w.U32(0x10000); })));
        Assert.Equal(new uint[] { 0x10000, 0x2FFFF, 0x30000, 0x40000 }, state.VerbLists[7]);

        state.Apply(Packet.ForOpcode(0x0060, Convert.FromHexString("0544332211")));
        Assert.Equal(0x11223344u, state.MessageTime!.Time);
        state.Apply(Packet.ForOpcode(0x0062, Net7UiPresentationWireTests.Build(w => { w.U16(2); w.U32(3); w.Bytes(Convert.FromHexString("AABB")); })));
        Assert.Equal(Convert.FromHexString("AABB"), state.Dialog!.Data);
        state.Apply(Packet.ForOpcode(0x0066, Convert.FromHexString("0100000002000000")));
        Assert.Equal(2u, state.OpenInterface!.UiType);
        state.Apply(Packet.ForOpcode(0x0067, Net7UiPresentationWireTests.Build(w => { w.U16(2); w.U32(7); w.Bytes(Encoding.Latin1.GetBytes("X\0")); })));
        Assert.Equal("X", state.GadgetControls.Single().Name);
        state.Apply(Packet.ForOpcode(0x0069, Convert.FromHexString("0144332211")));
        Assert.True(state.ClickIndicators[0x11223344].IsOn);
        state.Apply(Packet.ForOpcode(0x00A0, Convert.FromHexString("010000000200000003000000")));
        Assert.Equal(2u, state.StarbaseRoomUpdate!.NewRoom);

        state.Apply(Packet.ForOpcode(0x00BE, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(0); w.NetworkU32(9); w.SharedString("Confirm"); })));
        Assert.True(state.ConfirmedActions[(0, 9)].UsesConfirmationDialog);
        state.Apply(Packet.ForOpcode(0x00BE, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(1); w.NetworkU32(10); w.SharedString("Chat route"); })));
        Assert.True(state.ConfirmedActions[(1, 10)].UsesChatUiRoute);
        state.Apply(Packet.ForOpcode(0x00BF, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(0); w.NetworkU32(9); })));
        Assert.DoesNotContain((0u, 9u), state.ConfirmedActions.Keys);
        Assert.Contains((1u, 10u), state.ConfirmedActions.Keys);
    }

    [Fact]
    public void UnknownConfirmationOfferDoesNotReplacePendingActionsOrStopFollowingUpdates()
    {
        var state = new Net7GameplayState();
        state.Apply(Packet.ForOpcode(0x00BE, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(1); w.NetworkU32(10); w.SharedString("Existing"); })));
        var before = state.IncomingMessage;
        Assert.True(state.Apply(Packet.ForOpcode(0x00BE, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(2); w.NetworkU32(11); w.SharedString("Unknown"); }))));
        Assert.Single(state.ConfirmedActions);
        Assert.Same(before, state.IncomingMessage);
        Assert.True(state.Apply(Packet.ForOpcode(0x0023, Encoding.Latin1.GetBytes("Following\0ignored"))));
        Assert.Equal("Following", state.SuperLine!.Text);
    }

    [Fact]
    public void I04TransientStateResetsForHandoffAndCharacterSelection()
    {
        var state = new Net7GameplayState();
        Populate(state);
        Assert.NotEmpty(state.MessageLines);
        Assert.NotEmpty(state.ConfirmedActions);
        Assert.NotNull(state.LoungeDefinition);

        state.ResetForServerHandoff();
        Assert.Empty(state.MessageLines);
        Assert.Empty(state.GadgetControls);
        Assert.Empty(state.VerbLists);
        Assert.Empty(state.ClickIndicators);
        Assert.Empty(state.ConfirmedActions);
        Assert.Null(state.SuperLine);
        Assert.Null(state.LoungeDefinition);
        Assert.Null(state.Dialog);

        Populate(state);
        state.ResetForCharacterSelection();
        Assert.Empty(state.MessageLines);
        Assert.Empty(state.ConfirmedActions);
        Assert.Null(state.TalkTree);
        Assert.Null(state.OpenInterface);
    }


    private static void Populate(Net7GameplayState state)
    {
        state.Apply(Packet.ForOpcode(0x0020, Net7UiPresentationWireTests.Build(w => { w.CString("M"); w.CString("T"); w.U32(1); w.U32(2); })));
        state.Apply(Packet.ForOpcode(0x0023, new byte[] { (byte)'S', 0 }));
        state.Apply(Packet.ForOpcode(0x0052, Net7UiPresentationWireTests.Build(w => { w.U32(1); w.U32(0); w.U32(0); w.U32(0); })));
        state.Apply(Packet.ForOpcode(0x0054, new byte[] { 0, 0, 0 }));
        state.Apply(Packet.ForOpcode(0x0066, new byte[8]));
        state.Apply(Packet.ForOpcode(0x0067, new byte[6]));
        state.Apply(Packet.ForOpcode(0x0069, new byte[5]));
        state.Apply(Packet.ForOpcode(0x00BE, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(0); w.NetworkU32(1); w.SharedString("M"); })));
    }
}
