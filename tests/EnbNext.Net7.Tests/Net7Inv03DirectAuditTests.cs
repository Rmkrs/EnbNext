using System.Reflection;
using EnbNext.Net7;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Transport;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Inv03DirectAuditTests
{
    [Fact]
    public void UseInventoryItemWireIsExactAndKeepsTargetIdentityExplicit()
    {
        byte[] payload = Net7GameplayWire.BuildUseInventoryItem(0x12345678, 1, 2);

        Assert.Equal(Convert.FromHexString("785634120102"), payload);
        Assert.Equal(
            new Net7UseInventoryItemRequest(0x12345678, 1, 2),
            Net7GameplayWire.DecodeUseInventoryItem(payload));
        Assert.Equal(Convert.FromHexString("000000000102"), Net7GameplayWire.BuildUseInventoryItem(0, 1, 2));
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeUseInventoryItem(payload.AsSpan(0, 5)));
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeUseInventoryItem(new byte[7]));
    }

    [Fact]
    public void SessionUseItemSenderDoesNotSubstitutePlayerOrAuthoritativeTargetState()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "EnbNext.Net7", "Net7Session.cs"));
        int start = source.IndexOf("public Task<bool> UseInventoryItemAsync", StringComparison.Ordinal);
        int end = source.IndexOf("public Task<bool> SendUiTriggerAsync", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string method = source[start..end];

        Assert.Contains("targetGameId", method, StringComparison.Ordinal);
        Assert.Contains("BuildUseInventoryItem(targetGameId, inventory, slot)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildUseInventoryItem(RequireLocalPlayerId", method, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildUseInventoryItem(World.CurrentTargetId", method, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildUseInventoryItem(RequireCurrentTargetId", method, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameOpcodeInboundUseItemIsConsumedWithoutFalseCompletionOrMaterialization()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x005D)
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

        async Task Dispatch(byte[] bytes)
        {
            Task task = (Task)dispatch.Invoke(session,
                [Packet.ForOpcode(0x005D, bytes), "direct outer UDP", CancellationToken.None])!;
            await task;
        }

        byte[] valid = Net7GameplayWire.BuildUseInventoryItem(0x12345678, 1, 2);
        await Dispatch(valid);
        await Dispatch(valid.AsSpan(0, 5).ToArray());
        await Dispatch(new byte[7]);

        Assert.Equal(0, delivered);
        Assert.False(session.Gameplay.Apply(Packet.ForOpcode(0x005D, valid)));
        Assert.False(session.World.Apply(Packet.ForOpcode(0x005D, valid), 0).PacketMaterialized);
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
