using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class Net7VerbStatusUpdateTests
{
    [Fact]
    public void GateChangesStatusInOneSlotAcrossRepeatedRangeUpdates()
    {
        var state = new Net7GameplayState();
        List(state, 7, [0xA0000, 0x10000]);
        for (int i = 0; i < 3; i++)
        {
            Update(state, 7, [], [0xA0002]);
            Assert.Equal(new uint[] { 0x10000, 0xA0002 }, state.VerbLists[7]);
            Assert.Equal(new uint[] { 0xA0002, 0x10000 },
                EnbCurrentRetailTargetingRuntime.OrderVerbButtonsLeftToRight(state.VerbLists[7]));
            Update(state, 7, [0xA0002], [0xA0000]);
            Assert.Equal(new uint[] { 0x10000, 0xA0000 }, state.VerbLists[7]);
        }
    }

    [Fact]
    public void DropDisablesExistingTypeWithReasonAndDoesNotCreateUnknownType()
    {
        var state = new Net7GameplayState();
        List(state, 7, [0xA0000]);
        Update(state, 7, [0xA0003, 0x80000], []);
        Assert.Equal(new uint[] { 0xA0003 }, state.VerbLists[7]);
        Update(state, 7, [0xA0000], []);
        Assert.Equal(new uint[] { 0xAFFFF }, state.VerbLists[7]);
        Update(state, 7, [], [0x10000]);
        Assert.Equal(new uint[] { 0xAFFFF, 0x10000 }, state.VerbLists[7]);
    }

    [Fact]
    public void InitialDuplicatesShareATypeAndDifferentTargetsRemainIndependent()
    {
        var state = new Net7GameplayState();
        List(state, 7, [0xA0002, 0xA0000, 0xA0002]);
        List(state, 8, [0xA0000]);
        Assert.Equal(new uint[] { 0xA0002 }, state.VerbLists[7]);
        Assert.Equal(new uint[] { 0xA0000 }, state.VerbLists[8]);
        List(state, 7, []);
        Assert.Empty(state.VerbLists[7]);
    }

    private static void List(Net7GameplayState state, uint target, uint[] verbs)
        => state.Apply(Packet.ForOpcode(0x005B, Net7UiPresentationWireTests.Build(w =>
        { w.NetworkU32(target); w.NetworkU32((uint)verbs.Length); foreach (var v in verbs) w.U32(v); })));
    private static void Update(Net7GameplayState state, uint target, uint[] drop, uint[] add)
        => state.Apply(Packet.ForOpcode(0x005C, Net7UiPresentationWireTests.Build(w =>
        {
            w.NetworkU32(target); w.NetworkU32((uint)drop.Length); foreach (var v in drop) w.U32(v);
            w.NetworkU32((uint)add.Length); foreach (var v in add) w.U32(v);
        })));
}
