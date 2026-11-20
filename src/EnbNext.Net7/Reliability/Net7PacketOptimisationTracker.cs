using System.Buffers.Binary;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Reliability;

/// <summary>
/// Semantic Create/Remove tracker used when POPT is enabled. It produces
/// the protocol-visible packet ordering without reproducing Net7Proxy's unsafe
/// short-payload/null-retail memory hazards.
/// </summary>
public sealed class Net7PacketOptimisationTracker
{
    private readonly Dictionary<uint, bool> _active = new();

    public bool ObserveCreate(uint gameId)
    {
        bool duplicate = _active.TryGetValue(gameId, out bool active) && active;
        _active[gameId] = true;
        return duplicate;
    }

    public void ObserveRemove(uint gameId)
        => _active[gameId] = false;

    public bool IsActive(uint gameId)
        => _active.TryGetValue(gameId, out bool active) && active;

    public void Clear()
        => _active.Clear();

    /// <summary>
    /// Applies the closed POPT Create/Remove compatibility rule and returns the
    /// exact observable delivery order. The original packet is never suppressed.
    /// Decoded-inner duplicates synthesize Remove before Create; TCP/direct-outer
    /// duplicates deliver Create before the synthetic Remove.
    /// </summary>
    public IReadOnlyList<Packet> Apply(Packet packet, bool decodedInnerIngress)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.Header.Opcode == 0x0004)
        {
            if (packet.Payload.Length < 4)
                throw new InvalidDataException("POPT Create 0x0004 payload is shorter than the required GameID u32.");

            uint gameId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Span[..4]);
            bool duplicate = ObserveCreate(gameId);
            if (!duplicate)
                return new[] { packet };

            byte[] removePayload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(removePayload, gameId);
            Packet syntheticRemove = Packet.ForOpcode(0x0007, removePayload);
            return decodedInnerIngress
                ? new[] { syntheticRemove, packet }
                : new[] { packet, syntheticRemove };
        }

        if (packet.Header.Opcode == 0x0007)
        {
            if (packet.Payload.Length < 4)
                throw new InvalidDataException("POPT Remove 0x0007 payload is shorter than the required GameID u32.");

            uint gameId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Span[..4]);
            ObserveRemove(gameId);
        }

        return new[] { packet };
    }
}
