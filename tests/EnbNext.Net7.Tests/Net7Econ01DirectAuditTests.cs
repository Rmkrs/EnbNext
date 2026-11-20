using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Net7;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Econ01DirectAuditTests
{
    private static readonly Net7InventoryTransferIdentity VendorTarget = new(
        0x11223344,
        Net7InventoryTransferIdSource.ClientShip);

    [Fact]
    public void TypedVendorPlannerPreservesAllEightCurrentTransferForms()
    {
        AssertVendor(Net7VendorTransactionPath.VendorWindowBuyOne, 7, 17, 4, 1, uint.MaxValue, 1);
        AssertVendor(Net7VendorTransactionPath.VendorWindowBuyStack, 7, 17, 4, 1, uint.MaxValue, 17);
        AssertVendor(Net7VendorTransactionPath.ShortcutBuyOne, 7, 17, 4, 1, uint.MaxValue, 1);
        AssertVendor(Net7VendorTransactionPath.ShortcutBuyStack, 7, 17, 4, 1, uint.MaxValue, 17);
        AssertVendor(Net7VendorTransactionPath.VendorWindowSellOne, 3, 9, 1, 4, uint.MaxValue, 1);
        AssertVendor(Net7VendorTransactionPath.VendorWindowSellStack, 3, 9, 1, 4, uint.MaxValue, 9);
        AssertVendor(Net7VendorTransactionPath.ShortcutSellOne, 3, 9, 1, 4, 0, 1);
        AssertVendor(Net7VendorTransactionPath.ShortcutSellStack, 3, 9, 1, 4, 0, 9);
    }

    [Fact]
    public void VendorPlannerPinsSelectedTargetAndStartsNothingWithoutSlotOrQuantity()
    {
        var localPlayer = new Net7InventoryTransferIdentity(
            0x11223344,
            Net7InventoryTransferIdSource.LocalPlayer);

        Assert.Throws<ArgumentException>(() => Net7InventoryDomain.CreateVendorTransfer(
            localPlayer, Net7VendorTransactionPath.VendorWindowBuyOne, 0));
        // 005A02E0/005A03B0 NO_SLOT and 00690AA0 Num=0 start no transaction.
        Assert.Null(Net7InventoryDomain.CreateVendorTransfer(
            VendorTarget, Net7VendorTransactionPath.VendorWindowBuyOne, uint.MaxValue));
        Assert.Null(Net7InventoryDomain.CreateVendorTransfer(
            VendorTarget, Net7VendorTransactionPath.VendorWindowBuyStack, 0, 0));

        Net7InventoryTransferPlan one = Net7InventoryDomain.CreateVendorTransfer(
            VendorTarget, Net7VendorTransactionPath.VendorWindowBuyOne, 0, 999)!.Value;
        Assert.Equal(1u, one.Count); // one-item path never serializes an external price/count substitute
    }

    [Fact]
    public void VendorConfirmationMetadataMatchesCurrentUiPathsWithoutInventingLossPredicate()
    {
        Assert.True(Net7InventoryDomain.VendorPathRequiresPrimaryConfirmation(Net7VendorTransactionPath.VendorWindowBuyStack));
        Assert.True(Net7InventoryDomain.VendorPathRequiresPrimaryConfirmation(Net7VendorTransactionPath.VendorWindowSellStack));
        Assert.False(Net7InventoryDomain.VendorPathRequiresPrimaryConfirmation(Net7VendorTransactionPath.ShortcutBuyStack));
        Assert.False(Net7InventoryDomain.VendorPathRequiresPrimaryConfirmation(Net7VendorTransactionPath.ShortcutSellStack));
        Assert.False(Net7InventoryDomain.VendorPathRequiresPrimaryConfirmation(Net7VendorTransactionPath.VendorWindowSellOne));

        Assert.True(Net7InventoryDomain.VendorPathMayRequireLossWarning(Net7VendorTransactionPath.VendorWindowSellOne));
        Assert.True(Net7InventoryDomain.VendorPathMayRequireLossWarning(Net7VendorTransactionPath.VendorWindowSellStack));
        Assert.False(Net7InventoryDomain.VendorPathMayRequireLossWarning(Net7VendorTransactionPath.ShortcutSellOne));
        Assert.False(Net7InventoryDomain.VendorPathMayRequireLossWarning(Net7VendorTransactionPath.ShortcutSellStack));
    }

    [Fact]
    public async Task CancelledPrimaryConfirmationSendsNothingBeforeTargetOrInventoryResolution()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });

        Assert.False(await session.ExecuteVendorTransactionAsync(
            Net7VendorTransactionPath.VendorWindowBuyStack,
            sourceSlot: 0,
            primaryConfirmationAccepted: false));
        Assert.False(await session.ExecuteVendorTransactionAsync(
            Net7VendorTransactionPath.VendorWindowSellStack,
            sourceSlot: 0,
            primaryConfirmationAccepted: false));
    }

    [Fact]
    public async Task SessionStackPathsReadAuthoritativeVendorAndCargoAuxDataWithoutOptimisticMutation()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        const uint vendorId = 0x11223344;
        const uint localPlayerId = 0x55667788;
        session.Gameplay.SetLocalSelectedTarget(vendorId);

        Apply(session.World.LocalPlayerAuxData,
            ("LocalPlayerAuxData.VendorInventory.0.ItemTemplateID", I32(1001)),
            ("LocalPlayerAuxData.VendorInventory.0.StackCount", I32(7)));

        Assert.False(await session.ExecuteVendorTransactionAsync(
            Net7VendorTransactionPath.ShortcutBuyStack, sourceSlot: 0));
        Assert.Equal(7, Assert.IsType<int>(Require(session.World.LocalPlayerAuxData,
            "LocalPlayerAuxData.VendorInventory.0.StackCount").Value));

        Net7WorldApplyResult create = session.World.Apply(Packet.ForOpcode(0x0004, BuildCreate(localPlayerId, 0)), 0);
        Assert.True(create.PacketMaterialized);
        session.World.SetLocalPlayerId(localPlayerId);
        Net7AuxDataSchema ship = session.World.Objects.FindFirst(localPlayerId)!.AuxData!;
        Apply(ship,
            ("ShipAuxData.Inventory.Cargo.0.ItemTemplateID", I32(2002)),
            ("ShipAuxData.Inventory.Cargo.0.StackCount", I32(9)));

        Assert.False(await session.ExecuteVendorTransactionAsync(
            Net7VendorTransactionPath.ShortcutSellStack, sourceSlot: 0));
        Assert.Equal(9, Assert.IsType<int>(Require(ship,
            "ShipAuxData.Inventory.Cargo.0.StackCount").Value));
    }

    [Fact]
    public void SessionVendorProducerUsesLocalSelectionAndAuthoritativeSourceInventoryOnly()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "EnbNext.Net7", "Net7Session.cs"));
        string sender = Method(source, "public Task<bool> ExecuteVendorTransactionAsync", "public Task<bool> OfferTradeItemAsync");
        Assert.Contains("Net7InventoryTransferIdSource.ClientShip", sender, StringComparison.Ordinal);
        Assert.Contains("ReadVendorWholeStackCount(path, sourceSlot)", sender, StringComparison.Ordinal);
        Assert.Contains("CreateVendorTransfer", sender, StringComparison.Ordinal);
        Assert.DoesNotContain("RequireCurrentTargetId", sender, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAuthoritativeMoney", sender, StringComparison.Ordinal);
        Assert.DoesNotContain("Price", sender, StringComparison.Ordinal);
        Assert.DoesNotContain("AveCost", sender, StringComparison.Ordinal);

        string reader = Method(source, "private uint ReadVendorWholeStackCount", "private Net7InventoryTransferIdentity ResolveInventoryTransferIdentity");
        Assert.Contains("LocalPlayerAuxData.VendorInventory", reader, StringComparison.Ordinal);
        Assert.Contains("ShipAuxData.Inventory.Cargo", reader, StringComparison.Ordinal);
        Assert.Contains(".ItemTemplateID", reader, StringComparison.Ordinal);
        Assert.Contains(".StackCount", reader, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSlotOrZeroCountInventoryTransactionsStartNothing()
    {
        // 00690AA0 reports NO_SLOT / Num=0 and returns without a transaction.
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        session.Gameplay.SetLocalSelectedTarget(0x11223344);
        Assert.False(await session.TransferInventoryAsync(VendorTarget, 1, 0, 2, 0, 0));
        Assert.False(await session.TransferInventoryAsync(VendorTarget, 1, uint.MaxValue, 2, 0, 1));
        Assert.False(await session.TransferInventoryFromUiDestinationAsync(VendorTarget, 1, 0, 0x0B, 0, 0));
        Assert.False(await session.OfferTradeItemAsync(1, 0, 0, 0));
        Assert.False(await session.RetractTradeItemAsync(0, 0));
        Assert.False(await session.ReorderTradeItemAsync(0, 1, 0));
    }

    [Fact]
    public async Task WholeStackCountsFollowRetailDefinitionMaxStackAndCargoStackCount()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        MethodInfo read = typeof(Net7Session).GetMethod("ReadVendorWholeStackCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        uint Count(Net7VendorTransactionPath path, uint slot) => (uint)read.Invoke(session, [path, slot])!;

        // 005A02E0 -> 0068AAA0: no materialized vendor item or no definition buys one.
        Assert.Equal(1u, Count(Net7VendorTransactionPath.VendorWindowBuyStack, 0));
        Apply(session.World.LocalPlayerAuxData,
            ("LocalPlayerAuxData.VendorInventory.0.ItemTemplateID", I32(1001)),
            ("LocalPlayerAuxData.VendorInventory.0.StackCount", I32(7)));
        Assert.Equal(1u, Count(Net7VendorTransactionPath.ShortcutBuyStack, 0));
        var empty = new Net7ItemTemplateVector([], null);
        session.ItemTemplates.Materialize(new Net7ItemTemplate(1001, 0, 0, 0, new Net7ItemTemplateInfo([], empty, empty),
            0, 0, 0, 25, 50, 0, 0, new Net7ItemTemplateSharedString([]), new Net7ItemTemplateSharedString([]), new Net7ItemTemplateSharedString([])));
        // The definition's MaxStack (+0x50), not the vendor slot StackCount.
        Assert.Equal(50u, Count(Net7VendorTransactionPath.VendorWindowBuyStack, 0));

        // 005A03B0/005A0740 -> 00689D20: cargo StackCount, 0 without a slot.
        Assert.Equal(0u, Count(Net7VendorTransactionPath.ShortcutSellStack, 0));
        const uint localPlayerId = 0x55667788;
        Assert.True(session.World.Apply(Packet.ForOpcode(0x0004, BuildCreate(localPlayerId, 0)), 0).PacketMaterialized);
        session.World.SetLocalPlayerId(localPlayerId);
        Apply(session.World.Objects.FindFirst(localPlayerId)!.AuxData!,
            ("ShipAuxData.Inventory.Cargo.0.StackCount", I32(0)),
            ("ShipAuxData.Inventory.Cargo.1.StackCount", I32(9)));
        Assert.Equal(0u, Count(Net7VendorTransactionPath.VendorWindowSellStack, 0));
        Assert.Equal(9u, Count(Net7VendorTransactionPath.ShortcutSellStack, 1));
    }

    [Fact]
    public async Task NumericInbound0027RemainsRequestOnlyAndCannotBecomeVendorCompletion()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x0027)
                delivered++;
        };

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DispatchGameplayPacketAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        byte[] request = Wire(Net7InventoryDomain.CreateVendorTransfer(
            VendorTarget, Net7VendorTransactionPath.VendorWindowBuyOne, 2)!.Value);
        Task task = (Task)dispatch.Invoke(session,
            [Packet.ForOpcode(0x0027, request), "decoded-inner", CancellationToken.None])!;
        await task;

        Assert.Equal(0, delivered);
        Assert.False(session.Gameplay.Apply(Packet.ForOpcode(0x0027, request)));
        Assert.False(session.World.Apply(Packet.ForOpcode(0x0027, request), 0).PacketMaterialized);
    }


    private static void AssertVendor(
        Net7VendorTransactionPath path,
        uint sourceSlot,
        uint stackCount,
        uint expectedFrom,
        uint expectedTo,
        uint expectedToSlot,
        uint expectedCount)
    {
        Net7InventoryTransferPlan plan = Net7InventoryDomain.CreateVendorTransfer(
            VendorTarget, path, sourceSlot, stackCount)!.Value;
        Net7InventoryTransferRequest decoded = Net7GameplayWire.DecodeInventoryTransferRequest(Wire(plan));
        Assert.Equal(VendorTarget.Id, decoded.Id);
        Assert.Equal(expectedFrom, decoded.FromInventory);
        Assert.Equal(sourceSlot, decoded.FromSlot);
        Assert.Equal(expectedTo, decoded.ToInventory);
        Assert.Equal(expectedToSlot, decoded.ToSlot);
        Assert.Equal(expectedCount, decoded.Count);
    }

    private static byte[] Wire(Net7InventoryTransferPlan plan)
        => Net7GameplayWire.BuildInventoryTransfer(
            plan.Identity.Id,
            plan.FromInventory,
            plan.FromSlot,
            plan.ToInventory,
            plan.ToSlot,
            plan.Count);

    private static void Apply(Net7AuxDataSchema schema, params (string Path, byte[] Wire)[] values)
    {
        using var delta = new MemoryStream();
        delta.WriteByte(0);
        WriteU32(delta, checked((uint)values.Length));
        foreach ((string path, byte[] wire) in values)
        {
            Net7AuxDataProperty property = Require(schema, path);
            WriteU32(delta, checked((uint)property.Id));
            delta.Write(wire);
        }
        WriteU32(delta, 0);
        WriteU32(delta, 0);
        new Net7AuxDataCodec().Apply(schema, delta.ToArray());
    }

    private static Net7AuxDataProperty Require(Net7AuxDataSchema schema, string path)
        => Assert.Single(schema.Properties, p => p.Path == path);

    private static byte[] I32(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] BuildCreate(uint gameId, byte objectType)
    {
        byte[] payload = new byte[23];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), gameId);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4, 4), 1f);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(8, 2), 0);
        payload[10] = objectType;
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(11, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(15, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(19, 4), 0f);
        return payload;
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static string Method(string source, string startText, string endText)
    {
        int start = source.IndexOf(startText, StringComparison.Ordinal);
        int end = source.IndexOf(endText, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not isolate method starting with {startText}.");
        return source[start..end];
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EnbNext.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate EnbNext repository root.");
    }
}
