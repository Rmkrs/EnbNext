using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Net7;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Inv05DirectAuditTests
{
    [Fact]
    public void HulkWireContractsAreExactAndRequestOnlyObjectsAreBounded()
    {
        byte[] open = Net7GameplayWire.BuildHulkOpenRequest(0x11223344);
        Assert.Equal(Convert.FromHexString("4433221100000000"), open);
        Assert.Equal(new Net7HulkOpenRequest(0x11223344, 0), Net7GameplayWire.DecodeHulkOpenRequest(open));
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeHulkOpenRequest(open.AsSpan(0, 7)));
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeHulkOpenRequest(new byte[9]));

        Assert.Equal(0x11223344u, Net7GameplayWire.DecodeHulkPermission(Convert.FromHexString("11223344")));
        Assert.Equal(0xFFu, Net7GameplayWire.DecodeHulkPermission(new byte[3]));
        Assert.Equal(0u, Net7GameplayWire.DecodeHulkPermission(new byte[5]));

        byte[] trigger = Net7GameplayWire.BuildUiTrigger(0x11223344, 0x0B);
        Assert.Equal(Convert.FromHexString("443322110B000000"), trigger);
        Assert.Equal(new Net7UiTriggerRequest(0x11223344, 0x0B), Net7GameplayWire.DecodeUiTrigger(trigger));
        Assert.Equal(
            new Net7UiTriggerRequest(0x11223344, 0x0C),
            Net7GameplayWire.DecodeUiTrigger(Net7GameplayWire.BuildUiTrigger(0x11223344, 0x0C)));
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeUiTrigger(trigger.AsSpan(0, 7)));
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeUiTrigger(new byte[9]));

        Assert.Equal(
            Convert.FromHexString("11223344000000050000000200000000FFFFFFFFFFFFFFFF"),
            Net7GameplayWire.BuildInventoryTransfer(0x11223344, 5, 2, 0, uint.MaxValue, uint.MaxValue));
        Assert.Equal(
            Convert.FromHexString("11223344000000060000000200000000FFFFFFFFFFFFFFFF"),
            Net7GameplayWire.BuildInventoryTransfer(0x11223344, 6, 2, 0, uint.MaxValue, uint.MaxValue));
    }

    [Fact]
    public void LocalSelectedTargetStateIsExplicitAndIndependentFromServerAuthoritativeTarget()
    {
        var gameplay = new Net7GameplayState();
        var world = new EnbNext.Net7.World.Net7WorldState();

        Assert.Null(gameplay.LocalSelectedTargetId);
        Assert.False(gameplay.HasLocalTargetSelection);
        Assert.Null(world.CurrentTargetId);

        gameplay.SetLocalSelectedTarget(0x11223344);
        Assert.Equal(0x11223344u, gameplay.LocalSelectedTargetId);
        Assert.True(gameplay.HasLocalTargetSelection);
        Assert.Null(world.CurrentTargetId);

        gameplay.SetLocalSelectedTarget(0);
        Assert.Null(gameplay.LocalSelectedTargetId);
        Assert.True(gameplay.HasLocalTargetSelection);
        Assert.Null(world.CurrentTargetId);
    }

    [Fact]
    public async Task HulkPermissionUsesLocalSelectedTargetAndRejectsStaleTarget()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        const uint hulkId = 0x11223344;

        Net7WorldApplyResult result = session.World.Apply(Packet.ForOpcode(0x0004, BuildCreate(hulkId, 0x19)), 0);
        Assert.True(result.PacketMaterialized);
        Assert.NotNull(session.World.Objects.FindFirst(hulkId));
        Assert.Null(session.World.CurrentTargetId);

        var published = new List<uint?>();
        session.PresentationStateChanged += _ => published.Add(session.Gameplay.CurrentHulkId);
        session.Gameplay.SetLocalSelectedTarget(hulkId);
        await Dispatch(session, Packet.ForOpcode(0x008C, U32Be(hulkId)));
        Assert.Equal(hulkId, session.Gameplay.CurrentHulkId);
        Assert.Equal(hulkId, Assert.Single(published));

        await session.CloseHulkAsync();
        Assert.Null(published[^1]);
        Assert.Equal(2, published.Count);
        session.Gameplay.SetLocalSelectedTarget(0x55667788);
        await Dispatch(session, Packet.ForOpcode(0x008C, U32Be(hulkId)));
        Assert.Null(session.Gameplay.CurrentHulkId);

        await Dispatch(session, Packet.ForOpcode(0x008C, new byte[3]));
        Assert.Null(session.Gameplay.CurrentHulkId);
        Assert.Equal(2, published.Count);
    }

    [Fact]
    public async Task CockpitTickClosesAnOpenHulkOnlyWhenTheCurrentTargetChanges()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        const uint hulkId = 0x11223344;
        Assert.True(session.World.Apply(Packet.ForOpcode(0x0004, BuildCreate(hulkId, 0x19)), 0).PacketMaterialized);
        session.Gameplay.SetLocalSelectedTarget(hulkId);
        await Dispatch(session, Packet.ForOpcode(0x008C, U32Be(hulkId)));
        Assert.Equal(hulkId, session.Gameplay.CurrentHulkId);
        var reconcile = typeof(Net7Session).GetMethod("ReconcileHulkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)reconcile.Invoke(session, [CancellationToken.None])!;
        Assert.Equal(hulkId, session.Gameplay.CurrentHulkId);
        session.Gameplay.SetLocalSelectedTarget(0x55667788);
        await (Task)reconcile.Invoke(session, [CancellationToken.None])!;
        Assert.Null(session.Gameplay.CurrentHulkId);
    }

    [Fact]
    public async Task AuxDataForAnObjectTypeWithoutAuxDataIsDroppedAndLaterPacketsApply()
    {
        // 007364C0 passes ClientObject+88 to SelectAndDecodeAuxDataDelta without a
        // NULL check; 006F6000 creates none for this type (undefined native use).
        // Safety rule: the packet is dropped and the session keeps processing.
        var trace = new List<string>();
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" }, trace.Add);
        Assert.True(session.World.Apply(Packet.ForOpcode(0x0004, BuildCreate(0x5000, 0x0A)), 0).PacketMaterialized);
        Assert.Null(session.World.Objects.FindFirst(0x5000)!.AuxData);
        byte[] aux = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(aux, 0x5000);
        BinaryPrimitives.WriteUInt16LittleEndian(aux.AsSpan(4), 2);
        await Dispatch(session, Packet.ForOpcode(0x001B, aux));
        Assert.True(session.World.Apply(Packet.ForOpcode(0x0004, BuildCreate(0x19000, 0x19)), 0).PacketMaterialized);
        Assert.NotNull(session.World.Objects.FindFirst(0x19000)!.AuxData);
    }

    [Fact]
    public async Task LootControlWithoutACurrentTargetObjectQueuesNoHulkOpenRequest()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        Assert.False(await session.RequestHulkOpenAsync());
        session.Gameplay.SetLocalSelectedTarget(0x11223344);
        Assert.Null(session.World.Objects.FindFirst(0x11223344));
        Assert.False(await session.RequestHulkOpenAsync());
        Assert.Null(session.Gameplay.CurrentHulkId);
    }

    [Fact]
    public async Task SameOpcodeHulkRequestAndUiTriggerAreConsumedWithoutFalseCompletion()
    {
        var trace = new List<string>();
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" }, trace.Add);
        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode is 0x0051 or 0x0065)
                delivered++;
        };

        byte[] open = Net7GameplayWire.BuildHulkOpenRequest(0x11223344);
        byte[] trigger = Net7GameplayWire.BuildUiTrigger(0x11223344, 0x0B);

        await Dispatch(session, Packet.ForOpcode(0x0051, open));
        await Dispatch(session, Packet.ForOpcode(0x0051, open.AsSpan(0, 7).ToArray()));
        await Dispatch(session, Packet.ForOpcode(0x0065, trigger));
        await Dispatch(session, Packet.ForOpcode(0x0065, trigger.AsSpan(0, 7).ToArray()));

        await Dispatch(session, Packet.ForOpcode(0x0051, Array.Empty<byte>()));
        await Dispatch(session, Packet.ForOpcode(0x0065, Array.Empty<byte>()));
        Assert.DoesNotContain(trace, line => line.Contains("rejected", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, delivered);
        Assert.False(session.Gameplay.Apply(Packet.ForOpcode(0x0051, open)));
        Assert.False(session.World.Apply(Packet.ForOpcode(0x0051, open), 0).PacketMaterialized);
        Assert.False(session.Gameplay.Apply(Packet.ForOpcode(0x0065, trigger)));
        Assert.False(session.World.Apply(Packet.ForOpcode(0x0065, trigger), 0).PacketMaterialized);
    }

    [Fact]
    public void SessionHulkPathsUseLocalInputTargetAndToggleOpenControl()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "EnbNext.Net7", "Net7Session.cs"));

        string target = Method(source, "public async Task<bool> RequestTargetAsync", "public Task<bool> RequestTargetsTargetAsync");
        Assert.Contains("Gameplay.SetLocalSelectedTarget(targetId)", target, StringComparison.Ordinal);
        Assert.Contains("ReconcileHulkAsync", target, StringComparison.Ordinal);
        Assert.Contains("bool selected = await TrySendGameplayAsync", target, StringComparison.Ordinal);
        Assert.Contains("_ = await RequestVerbAsync(targetId, cancellationToken)", target, StringComparison.Ordinal);
        Assert.Contains("catch (InvalidOperationException exception)", target, StringComparison.Ordinal);
        Assert.Contains("target verb attach skipped after selection send", target, StringComparison.Ordinal);
        Assert.Contains("return true;", target, StringComparison.Ordinal);
        Assert.DoesNotContain("World.CurrentTargetId =", target, StringComparison.Ordinal);

        string open = Method(source, "public async Task<bool> RequestHulkOpenAsync", "public async Task<bool> TransferHulkItemAsync");
        Assert.Contains("Gameplay.CurrentHulkId is not null", open, StringComparison.Ordinal);
        Assert.Contains("CloseHulkAsync", open, StringComparison.Ordinal);
        Assert.Contains("Gameplay.LocalSelectedTargetId is not uint targetId", open, StringComparison.Ordinal);
        Assert.DoesNotContain("RequireCurrentTargetId()", open, StringComparison.Ordinal);

        string transfer = Method(source, "public async Task<bool> TransferHulkItemAsync", "public async Task CloseHulkAsync");
        Assert.Contains("RequireLocalSelectedTargetId()", transfer, StringComparison.Ordinal);
        Assert.Contains("identity.Id, inventoryType, slot, 0, uint.MaxValue, uint.MaxValue", transfer, StringComparison.Ordinal);
        Assert.DoesNotContain("RequireCurrentTargetId()", transfer, StringComparison.Ordinal);

        string permission = Method(source, "private async Task HandleHulkPermissionAsync", "private async Task ReconcileHulkAsync");
        Assert.Contains("Gameplay.LocalSelectedTargetId != hulkId", permission, StringComparison.Ordinal);
        Assert.DoesNotContain("World.CurrentTargetId", permission, StringComparison.Ordinal);
    }


    private static async Task Dispatch(Net7Session session, Packet packet)
    {
        MethodInfo method = typeof(Net7Session).GetMethod(
            "DispatchGameplayPacketAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task task = (Task)method.Invoke(session, [packet, "direct outer UDP", CancellationToken.None])!;
        await task;
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

    private static byte[] U32Be(uint value)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, value);
        return payload;
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
