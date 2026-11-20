using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Presentation;
using EnbNext.Net7.Timing;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbPresentationEffectPublicationTests
{
    [Fact]
    public async Task EffectOnlyGameplayMutationPublishesACompleteSnapshotWithoutPretendingWorldChanged()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        var publisher = new EnbPresentationSnapshotPublisher();
        int presentationChanges = 0;
        int worldChanges = 0;

        session.PresentationStateChanged += clientTime =>
        {
            presentationChanges++;
            publisher.Publish(
                session.World,
                session.Gameplay.Effects,
                assets: null,
                clientTime: clientTime);
        };
        session.WorldStateChanged += _ => worldChanges++;

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DispatchGameplayPacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Net7Session.DispatchGameplayPacketAsync was not found.");

        byte[] point = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(point.AsSpan(0, 4), 0x11223344);
        BinaryPrimitives.WriteUInt32LittleEndian(point.AsSpan(4, 4), 123);
        BinaryPrimitives.WriteUInt16LittleEndian(point.AsSpan(22, 2), 7);
        await Dispatch(dispatch, session, Packet.ForOpcode(0x000A, point));

        Assert.Equal(1, presentationChanges);
        Assert.Equal(0, worldChanges);
        EnbSceneSnapshot first = Assert.IsType<EnbSceneSnapshot>(publisher.Latest);
        EnbSceneEffectSnapshot effect = Assert.Single(first.Effects);
        Assert.Equal(0x11223344u, effect.EffectId);
        Assert.Equal((ushort)7, effect.DefinitionId);
        Assert.Equal(1UL, first.Version);

        byte[] remove = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(remove, 0x11223344);
        await Dispatch(dispatch, session, Packet.ForOpcode(0x000F, remove));

        Assert.Equal(2, presentationChanges);
        Assert.Equal(0, worldChanges);
        EnbSceneSnapshot second = Assert.IsType<EnbSceneSnapshot>(publisher.Latest);
        Assert.Empty(second.Effects);
        Assert.Equal(2UL, second.Version);
    }

    [Fact]
    public async Task CharacterSelectionResetDoesNotSampleClockWithoutPresentationSubscribers()
    {
        var clock = new CountingClock(0x12345678);
        await using var session = new Net7Session(
            new Net7ServerOptions { Host = "127.0.0.1" },
            clock: clock);

        MethodInfo reset = typeof(Net7Session).GetMethod(
            "ResetGameplayAndWorldForCharacterSelection",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Net7Session.ResetGameplayAndWorldForCharacterSelection was not found.");

        reset.Invoke(session, null);

        Assert.Equal(0, clock.ReadCount);
    }

    [Fact]
    public async Task VerbListMutationPublishesPresentationWithoutPretendingWorldChanged()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int presentationChanges = 0;
        int worldChanges = 0;
        session.PresentationStateChanged += _ => presentationChanges++;
        session.WorldStateChanged += _ => worldChanges++;

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DispatchGameplayPacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Net7Session.DispatchGameplayPacketAsync was not found.");
        byte[] verbList = Net7UiPresentationWireTests.Build(writer =>
        {
            writer.NetworkU32(7);
            writer.NetworkU32(1);
            writer.U32(0x00080000);
        });

        await Dispatch(dispatch, session, Packet.ForOpcode(0x005B, verbList));

        Assert.Equal(1, presentationChanges);
        Assert.Equal(0, worldChanges);
        Assert.Equal(new uint[] { 0x00080000 }, session.Gameplay.VerbLists[7]);
    }

    [Fact]
    public async Task LocalTargetSelectionPublishesBeforeTargetRequestCanReachTransport()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int presentationChanges = 0;
        session.PresentationStateChanged += _ => presentationChanges++;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RequestTargetAsync(0x10203040));

        Assert.Equal(1, presentationChanges);
        Assert.True(session.Gameplay.HasLocalTargetSelection);
        Assert.Equal(0x10203040u, session.Gameplay.LocalSelectedTargetId);
    }

    [Fact]
    public void ClientPublishesWorldAndTransientEffectsFromThePresentationChangeBoundary()
    {
        string client = Source("src/EnbNext.Client/EnbClient.cs");
        string session = Source("src/EnbNext.Net7/Net7Session.cs");

        Assert.Contains("PresentationStateChanged += OnNet7PresentationStateChanged", client, StringComparison.Ordinal);
        Assert.Contains("session.Gameplay.Effects", client, StringComparison.Ordinal);
        Assert.DoesNotContain("WorldStateChanged += OnNet7WorldStateChanged", client, StringComparison.Ordinal);
        Assert.Contains("opcode is >= 0x0009 and <= 0x000F", session, StringComparison.Ordinal);
        Assert.Contains("NotifyPresentationStateChanged(clientTime)", session, StringComparison.Ordinal);
        Assert.Contains("Gameplay.RemoveEffect(effectId)", session, StringComparison.Ordinal);
        Assert.Contains(
            "BuildVerbRequest(RequireLocalShipObjectId(), objectId, 1)",
            session,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "BuildVerbRequest(RequireLocalPlayerId(), objectId, 1)",
            session,
            StringComparison.Ordinal);
        Assert.Contains(
            "BuildVerbRequest(RequireLocalShipObjectId(), previous, 2)",
            session,
            StringComparison.Ordinal);
    }

    private sealed class CountingClock(uint now) : INet7RuntimeClock
    {
        public int ReadCount { get; private set; }

        public uint GetMilliseconds()
        {
            ReadCount++;
            return now;
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static async Task Dispatch(MethodInfo dispatch, Net7Session session, Packet packet)
    {
        Task task = (Task)(dispatch.Invoke(
            session,
            [packet, "decoded-inner", CancellationToken.None])
            ?? throw new InvalidOperationException("Gameplay dispatch did not return a Task."));
        await task;
    }

    private static string Source(string relativePath)
    {
        string repo = FindRepoRoot();
        return File.ReadAllText(Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
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

        throw new DirectoryNotFoundException("Could not locate the EnbNext repository root.");
    }
}
