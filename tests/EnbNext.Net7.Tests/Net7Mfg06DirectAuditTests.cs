using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Mfg06DirectAuditTests
{
    private const uint FirstLabId = 0x11223344;
    private const uint SecondLabId = 0x55667788;

    [Fact]
    public void ActiveManufacturingObjectRetainsInitializedBytesAndIgnoresTail()
    {
        Assert.Equal(FirstLabId,
            Net7GameplayWire.DecodeActiveManufacturingObject(Convert.FromHexString("11223344")));

        for (int length = 0; length < 4; length++)
            Assert.Equal(0u, Net7GameplayWire.DecodeActiveManufacturingObject(new byte[length]));
        Assert.Equal(0u, Net7GameplayWire.DecodeActiveManufacturingObject(new byte[5]));
    }

    [Fact]
    public void StateStartsAtZeroAndEvery007FOnlyOverwritesTheActiveObjectId()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(FirstLabId, 1f, 0, 0x27, 0, 0, 0));
        world.MaterializeCreate(new Net7CreateUpdate(SecondLabId, 1f, 0, 0x27, 0, 0, 0));
        var domain = new Net7ManufacturingDomain(id => world.Objects.FindFirst(id));

        Assert.Equal(0u, domain.ActiveObjectId);
        Assert.Null(domain.ActiveObject);

        domain.SetActiveObject(FirstLabId);
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        domain.RecordManufactureItemSent(500);
        domain.RecordActionSent(2);
        domain.RecordFilterSent(false, 1);

        Assert.Equal(Net7ManufacturingMode.Manufacture, domain.RequestedMode);
        Assert.Equal(500u, domain.RememberedManufactureItemId);
        Assert.True(domain.ManufactureSelectionPending);
        Assert.Equal(Net7ManufacturingActionContext.Manufacture, domain.ActionContext);
        Assert.Equal(1000u, domain.ActionCountdownMilliseconds);
        Assert.False(domain.ShouldSendFilter(false, 1));

        domain.SetActiveObject(SecondLabId);
        Assert.Equal(SecondLabId, domain.ActiveObjectId);
        Assert.Equal(Net7ManufacturingMode.Manufacture, domain.RequestedMode);
        Assert.Equal(500u, domain.RememberedManufactureItemId);
        Assert.True(domain.ManufactureSelectionPending);
        Assert.Equal(Net7ManufacturingActionContext.Manufacture, domain.ActionContext);
        Assert.Equal(1000u, domain.ActionCountdownMilliseconds);
        Assert.False(domain.ShouldSendFilter(false, 1));

        domain.SetActiveObject(0);
        Assert.Equal(0u, domain.ActiveObjectId);
        Assert.Null(domain.ActiveObject);
        Assert.Equal(Net7ManufacturingMode.Manufacture, domain.RequestedMode);
        Assert.Equal(500u, domain.RememberedManufactureItemId);
        Assert.True(domain.ManufactureSelectionPending);
        Assert.Equal(Net7ManufacturingActionContext.Manufacture, domain.ActionContext);
        Assert.Equal(1000u, domain.ActionCountdownMilliseconds);
    }

    [Fact]
    public void Gameplay007FOverwritesAndZeroClearsOnlyTheExplicitActiveObjectState()
    {
        var state = new Net7GameplayState();
        Assert.Equal(0u, state.Manufacturing.ActiveObjectId);

        Assert.True(state.Apply(Packet.ForOpcode(0x007F, Convert.FromHexString("11223344"))));
        Assert.Equal(FirstLabId, state.Manufacturing.ActiveObjectId);

        Assert.True(state.Apply(Packet.ForOpcode(0x007F, Convert.FromHexString("55667788"))));
        Assert.Equal(SecondLabId, state.Manufacturing.ActiveObjectId);

        Assert.True(state.Apply(Packet.ForOpcode(0x007F, new byte[4])));
        Assert.Equal(0u, state.Manufacturing.ActiveObjectId);
    }

    [Fact]
    public void MissingRemovedAndLaterMaterializedObjectsResolveReactivelyWithoutChangingWireState()
    {
        var world = new Net7WorldState();
        var domain = new Net7ManufacturingDomain(id => world.Objects.FindFirst(id));
        domain.SetActiveObject(FirstLabId);

        Assert.Equal(FirstLabId, domain.ActiveObjectId);
        Assert.Null(domain.ActiveObject);

        world.MaterializeCreate(new Net7CreateUpdate(FirstLabId, 1f, 0, 0x27, 0, 0, 0));
        Assert.NotNull(domain.ActiveObject);
        Assert.NotNull(domain.CurrentLab);
        Assert.Equal(FirstLabId, domain.ActiveObjectId);

        world.MaterializeRemove(FirstLabId);
        Assert.Null(domain.ActiveObject);
        Assert.Null(domain.CurrentLab);
        Assert.Equal(FirstLabId, domain.ActiveObjectId);

        world.MaterializeCreate(new Net7CreateUpdate(FirstLabId, 1f, 0, 0x27, 0, 0, 0));
        Assert.NotNull(domain.CurrentLab);
        Assert.Equal(FirstLabId, domain.ActiveObjectId);
    }

    [Fact]
    public void SectorBoundaryResetRemainsAnExplicitLifecycleOwnerRatherThanA007FSideEffect()
    {
        var domain = new Net7ManufacturingDomain();
        domain.SetActiveObject(FirstLabId);
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        domain.RecordManufactureItemSent(500);

        domain.ResetForSectorBoundary();

        Assert.Equal(0u, domain.ActiveObjectId);
        Assert.Equal(Net7ManufacturingMode.None, domain.RequestedMode);
        Assert.Null(domain.RememberedManufactureItemId);
        Assert.False(domain.ManufactureSelectionPending);
    }
}
