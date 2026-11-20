using System.Buffers.Binary;
using EnbNext.Net7.Reliability;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7PacketOptimisationTrackerTests
{
    [Fact]
    public void FirstCreateIsNotDuplicate_SecondActiveCreateIs()
    {
        var tracker = new Net7PacketOptimisationTracker();
        Assert.False(tracker.ObserveCreate(42));
        Assert.True(tracker.IsActive(42));
        Assert.True(tracker.ObserveCreate(42));
    }

    [Fact]
    public void RemoveMakesNextCreateFreshAgain()
    {
        var tracker = new Net7PacketOptimisationTracker();
        tracker.ObserveCreate(42);
        tracker.ObserveRemove(42);
        Assert.False(tracker.IsActive(42));
        Assert.False(tracker.ObserveCreate(42));
    }

    [Fact]
    public void HandoffStyleClearDropsAllTrackedObjectState()
    {
        var tracker = new Net7PacketOptimisationTracker();
        tracker.ObserveCreate(1);
        tracker.ObserveCreate(2);
        tracker.Clear();
        Assert.False(tracker.IsActive(1));
        Assert.False(tracker.IsActive(2));
    }

    [Fact]
    public void DecodedInnerDuplicateCreate_SynthesizesRemoveBeforeOriginalCreate()
    {
        var tracker = new Net7PacketOptimisationTracker();
        Packet create = CreatePacket(0x11223344);
        Assert.Single(tracker.Apply(create, decodedInnerIngress: true));

        IReadOnlyList<Packet> second = tracker.Apply(create, decodedInnerIngress: true);
        Assert.Equal(2, second.Count);
        Assert.Equal((ushort)0x0007, second[0].Header.Opcode);
        Assert.Equal((ushort)0x0004, second[1].Header.Opcode);
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(second[0].Payload.Span));
    }

    [Fact]
    public void DirectOuterDuplicateCreate_QueuesOriginalBeforeSyntheticRemove()
    {
        var tracker = new Net7PacketOptimisationTracker();
        Packet create = CreatePacket(0x55667788);
        _ = tracker.Apply(create, decodedInnerIngress: false);

        IReadOnlyList<Packet> second = tracker.Apply(create, decodedInnerIngress: false);
        Assert.Equal(2, second.Count);
        Assert.Equal((ushort)0x0004, second[0].Header.Opcode);
        Assert.Equal((ushort)0x0007, second[1].Header.Opcode);
    }

    [Fact]
    public void RemoveIsAlwaysForwardedAndClearsActiveState()
    {
        var tracker = new Net7PacketOptimisationTracker();
        _ = tracker.Apply(CreatePacket(7), decodedInnerIngress: true);
        Packet remove = RemovePacket(7);

        IReadOnlyList<Packet> delivered = tracker.Apply(remove, decodedInnerIngress: true);
        Assert.Single(delivered);
        Assert.Same(remove, delivered[0]);
        Assert.False(tracker.IsActive(7));
    }

    [Theory]
    [InlineData(0x0004)]
    [InlineData(0x0007)]
    public void ShortCreateOrRemoveFailsClosedInsteadOfReproducingUncheckedRead(ushort opcode)
    {
        var tracker = new Net7PacketOptimisationTracker();
        Packet malformed = Packet.ForOpcode(opcode, new byte[3]);
        Assert.Throws<InvalidDataException>(() => tracker.Apply(malformed, decodedInnerIngress: false));
    }

    private static Packet CreatePacket(uint gameId)
        => Packet.ForOpcode(0x0004, U32(gameId));

    private static Packet RemovePacket(uint gameId)
        => Packet.ForOpcode(0x0007, U32(gameId));

    private static byte[] U32(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }
}
