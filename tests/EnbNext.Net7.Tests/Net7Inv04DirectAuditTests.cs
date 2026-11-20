using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class Net7Inv04DirectAuditTests
{
    [Fact]
    public void NormalTransferPlannerPinsRetailLocalSelectedTargetProvenanceAndInv04Shapes()
    {
        var selected = new Net7InventoryTransferIdentity(
            0x10203040,
            Net7InventoryTransferIdSource.ClientShip);

        AssertTransfer(selected, 1, 2, 2, 4, 1);
        AssertTransfer(selected, 2, 4, 1, 3, 1);
        AssertTransfer(selected, 1, 2, 3, uint.MaxValue, 1);
        AssertTransfer(selected, 3, 5, 1, uint.MaxValue, 1);
        AssertTransfer(selected, 1, 2, 1, 9, 1);
        AssertTransfer(selected, 1, 2, 3, uint.MaxValue, uint.MaxValue);
    }

    [Fact]
    public void NormalTransferPlannerRejectsShapesTheCurrentInv04ControllerDoesNotEmit()
    {
        var selected = new Net7InventoryTransferIdentity(
            0x11223344,
            Net7InventoryTransferIdSource.ClientShip);
        var localPlayer = new Net7InventoryTransferIdentity(
            0x55667788,
            Net7InventoryTransferIdSource.LocalPlayer);

        Assert.Throws<ArgumentException>(() =>
            Net7InventoryDomain.CreateInv04Transfer(localPlayer, 1, 0, 2, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Net7InventoryDomain.CreateInv04Transfer(selected, 0, 0, 1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Net7InventoryDomain.CreateInv04Transfer(selected, 1, 0, 4, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Net7InventoryDomain.CreateInv04Transfer(selected, 1, uint.MaxValue, 2, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Net7InventoryDomain.CreateInv04Transfer(selected, 1, 0, 2, 0, 0));
    }

    [Fact]
    public void RetailUiDestination0BIsResolvedBeforeTheServerSerializerBoundary()
    {
        var selected = new Net7InventoryTransferIdentity(
            0x11223344,
            Net7InventoryTransferIdSource.ClientShip);

        Net7InventoryTransferPlan plan = Net7InventoryDomain.CreateInv04UiTransfer(
            selected, 2, 4, Net7InventoryDomain.RetailUiDestinationSentinel, uint.MaxValue, 1);

        Assert.Equal(Net7InventoryDomain.CargoInventory, plan.ToInventory);
        Assert.Equal(uint.MaxValue, plan.ToSlot);
        Assert.Equal(
            Convert.FromHexString("11223344000000020000000400000001FFFFFFFF00000001"),
            Wire(plan));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Net7GameplayWire.BuildInventoryTransfer(0x11223344, 2, 4, 0x0B, uint.MaxValue, 1));
    }

    [Fact]
    public void AuxiliaryDirectProducersRemainRepresentableWithLocalPlayerIdWithoutChangingIdMeaning()
    {
        var localPlayer = new Net7InventoryTransferIdentity(
            0x55667788,
            Net7InventoryTransferIdSource.LocalPlayer);

        Net7InventoryTransferPlan cargoToEquipped = Net7InventoryDomain.CreateTransfer(
            localPlayer, 1, 2, 2, uint.MaxValue, uint.MaxValue);
        Net7InventoryTransferPlan cargoReorder = Net7InventoryDomain.CreateTransfer(
            localPlayer, 1, 4, 1, 5, uint.MaxValue);

        Assert.Equal(
            Convert.FromHexString("55667788000000010000000200000002FFFFFFFFFFFFFFFF"),
            Wire(cargoToEquipped));
        Assert.Equal(
            Convert.FromHexString("5566778800000001000000040000000100000005FFFFFFFF"),
            Wire(cargoReorder));
    }

    [Fact]
    public void SessionHasNoImplicitInventoryTransferOverloadThatCanInventTheSelectedTargetId()
    {
        var overloads = typeof(Net7Session)
            .GetMethods()
            .Where(method => method.Name == nameof(Net7Session.TransferInventoryAsync))
            .ToArray();

        Assert.NotEmpty(overloads);
        Assert.All(overloads, method =>
        {
            var parameters = method.GetParameters();
            Assert.NotEmpty(parameters);
            Assert.Equal(typeof(Net7InventoryTransferIdentity), parameters[0].ParameterType);
        });
    }

    private static void AssertTransfer(
        Net7InventoryTransferIdentity identity,
        uint fromInventory,
        uint fromSlot,
        uint toInventory,
        uint toSlot,
        uint count)
    {
        Net7InventoryTransferPlan plan = Net7InventoryDomain.CreateInv04Transfer(
            identity, fromInventory, fromSlot, toInventory, toSlot, count);
        Net7InventoryTransferRequest decoded = Net7GameplayWire.DecodeInventoryTransferRequest(Wire(plan));

        Assert.Equal(identity.Id, decoded.Id);
        Assert.Equal(fromInventory, decoded.FromInventory);
        Assert.Equal(fromSlot, decoded.FromSlot);
        Assert.Equal(toInventory, decoded.ToInventory);
        Assert.Equal(toSlot, decoded.ToSlot);
        Assert.Equal(count, decoded.Count);
    }

    private static byte[] Wire(Net7InventoryTransferPlan plan)
        => Net7GameplayWire.BuildInventoryTransfer(
            plan.Identity.Id,
            plan.FromInventory,
            plan.FromSlot,
            plan.ToInventory,
            plan.ToSlot,
            plan.Count);
}
