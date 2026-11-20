using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Full02DirectAuditTests
{
    [Fact]
    public void ConfirmedActionResponseConsumesOnlyAnExistingPendingOffer()
    {
        var state = new Net7GameplayState();
        state.Apply(Packet.ForOpcode(
            0x00BE,
            Net7UiPresentationWireTests.Build(w =>
            {
                w.NetworkU32(0);
                w.NetworkU32(0x11223344);
                w.SharedString("Confirm");
            })));

        Assert.False(state.TryConsumeConfirmedAction(0, 0x55667788, out _));
        Assert.Contains((0u, 0x11223344u), state.ConfirmedActions.Keys);

        Assert.True(state.TryConsumeConfirmedAction(0, 0x11223344, out Net7ConfirmedActionPending? pending));
        Assert.Equal(0x11223344u, pending!.Id);
        Assert.DoesNotContain((0u, 0x11223344u), state.ConfirmedActions.Keys);
    }

    [Fact]
    public void ConfirmedActionTimeoutRemovesTheOnlyAuthorityForThatResponseId()
    {
        var state = new Net7GameplayState();
        state.Apply(Packet.ForOpcode(
            0x00BE,
            Net7UiPresentationWireTests.Build(w =>
            {
                w.NetworkU32(1);
                w.NetworkU32(77);
                w.SharedString("Route");
            })));
        state.Apply(Packet.ForOpcode(
            0x00BF,
            Net7UiPresentationWireTests.Build(w =>
            {
                w.NetworkU32(1);
                w.NetworkU32(77);
            })));

        Assert.False(state.TryConsumeConfirmedAction(1, 77, out _));
    }

    [Fact]
    public void OutboundProducerApisBindRetailOwnedIdentityAndPermissionState()
    {
        string session = File.ReadAllText(FindRepoFile("src", "EnbNext.Net7", "Net7Session.cs"));

        Assert.Contains("public Task<bool> SendUiTriggerAsync(uint action", session, StringComparison.Ordinal);
        Assert.Contains("if (!World.ClientSendUiTriggersEnabled)", session, StringComparison.Ordinal);
        Assert.Contains("Net7GameplayWire.BuildUiTrigger(RequireLocalAvatarObjectId(), action)", session, StringComparison.Ordinal);
        Assert.DoesNotContain("SendUiTriggerAsync(uint id, uint action", session, StringComparison.Ordinal);

        Assert.Contains("Gameplay.TryConsumeConfirmedAction(type, id", session, StringComparison.Ordinal);
        Assert.Contains("pending!.Id, accepted", session, StringComparison.Ordinal);

        Assert.Contains("public Task<bool> SendGmDestroyItemAsync(CancellationToken", session, StringComparison.Ordinal);
        Assert.Contains("Gameplay.GmInventoryEditor", session, StringComparison.Ordinal);
        Assert.Contains("editor.PlayerPackTargetValue", session, StringComparison.Ordinal);
        Assert.DoesNotContain("SendGmDestroyItemAsync(uint playerPackTargetValue", session, StringComparison.Ordinal);

        Assert.Contains("public Task<bool> SendGmResetMissionAsync(CancellationToken", session, StringComparison.Ordinal);
        Assert.Contains("Gameplay.GmMissionLog", session, StringComparison.Ordinal);
        Assert.Contains("editor.Field0", session, StringComparison.Ordinal);
        Assert.DoesNotContain("SendGmResetMissionAsync(uint missionEditorContextValue", session, StringComparison.Ordinal);
    }

    private static string FindRepoFile(params string[] parts)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine([current.FullName, .. parts]);
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Unable to find repository file {Path.Combine(parts)}.");
    }
}
