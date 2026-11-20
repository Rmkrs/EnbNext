using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Transport;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Inv02DirectAuditTests
{
    [Fact]
    public void InventorySortCodecPinsExactCurrentRequestGrammar()
    {
        byte[] cargo = Net7GameplayWire.BuildInventorySort(0x11223344, 1, 5, 4, 1, false);
        Assert.Equal(Convert.FromHexString("112233440000000100000005000000040000000100"), cargo);
        Assert.Equal(
            new Net7InventorySortRequest(0x11223344, 1, 5, 4, 1, false),
            Net7GameplayWire.DecodeInventorySortRequest(cargo));

        byte[] vault = Net7GameplayWire.BuildInventorySort(0x01020304, 3, 10, 1, 8, true);
        Assert.Equal(Convert.FromHexString("01020304000000030000000A000000010000000801"), vault);
        Assert.Equal(
            new Net7InventorySortRequest(0x01020304, 3, 10, 1, 8, true),
            Net7GameplayWire.DecodeInventorySortRequest(vault));

        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeInventorySortRequest(cargo[..^1]));
        byte[] trailing = [.. cargo, (byte)0];
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeInventorySortRequest(trailing));
    }

    [Fact]
    public void CurrentProducerPolicyPinsPlayerOwnedCargoVaultAndExactPresets()
    {
        Assert.Equal(Net7InventorySortTarget.Cargo, Net7InventoryDomain.RequireCurrentSortTarget(1));
        Assert.Equal(Net7InventorySortTarget.Vault, Net7InventoryDomain.RequireCurrentSortTarget(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Net7InventoryDomain.RequireCurrentSortTarget(2));

        Assert.Equal(new Net7InventorySortKeys(5, 4, 1), Net7InventoryDomain.ResolveSortPreset(Net7InventorySortPreset.Category));
        Assert.Equal(new Net7InventorySortKeys(1, 4, 8), Net7InventoryDomain.ResolveSortPreset(Net7InventorySortPreset.Name));
        Assert.Equal(new Net7InventorySortKeys(10, 1, 8), Net7InventoryDomain.ResolveSortPreset(Net7InventorySortPreset.Value));
        Assert.Throws<ArgumentOutOfRangeException>(() => Net7InventoryDomain.RequireCurrentSortPreset(5, 4, 8));

        string session = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "EnbNext.Net7", "Net7Session.cs"));
        int start = session.IndexOf("public Task<bool> SortInventoryAsync(", StringComparison.Ordinal);
        int end = session.IndexOf("public Task<bool> SortInventoryAsync(", start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string sender = session[start..end];
        // 005D0D20/00657DA0 pass SClient+112C to ConstructInventorySortCommand.
        Assert.Contains("RequireLocalAvatarObjectId()", sender, StringComparison.Ordinal);
        Assert.Contains("TrySendGameplayAsync(0x0028", sender, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentTargetId", sender, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NumericInboundInventorySortIsRequestOnlyAndNeverSurfacedAsCompletion()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x0028)
                delivered++;
        };

        FieldInfo transportField = typeof(Net7Session).GetField(
            "_transportState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var transport = Assert.IsType<Net7TransportState>(transportField.GetValue(session));
        transport.CommitValidatedFixedSessionId(0xAABBCCDD);
        transport.CommitGameplayPort(3501);
        transport.SetGameplayForwardingActive(true);
        typeof(Net7Session).GetField("_gameplayClientTargetActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(session, true);

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DispatchGameplayPacketAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        async Task Dispatch(byte[] payload)
        {
            Task task = (Task)dispatch.Invoke(session,
                [Packet.ForOpcode(0x0028, payload), "direct outer UDP", CancellationToken.None])!;
            await task;
        }

        byte[] valid = Net7GameplayWire.BuildInventorySort(0x11223344, 1, 5, 4, 1, false);
        await Dispatch(valid);
        await Dispatch(valid[..^1]);
        await Dispatch([.. valid, (byte)0]);

        Assert.Equal(0, delivered);
        Assert.False(session.Gameplay.Apply(Packet.ForOpcode(0x0028, valid)));
        Assert.False(session.World.Apply(Packet.ForOpcode(0x0028, valid), 0).PacketMaterialized);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src"))
                && Directory.Exists(Path.Combine(current.FullName, "tests")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate EnbNext repository root from test output directory.");
    }
}
