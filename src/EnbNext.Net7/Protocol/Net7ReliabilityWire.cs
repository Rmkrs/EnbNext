using System.Buffers.Binary;

namespace EnbNext.Net7.Protocol;

/// <summary>Exact small reliability and master-control payloads.</summary>
public static class Net7ReliabilityWire
{
    public static byte[] BuildMissingPacketRequest(uint missingIncomingSequence)
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), missingIncomingSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), 1);
        return payload;
    }

    public static byte[] BuildLoginStageAcknowledgement(uint stage)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, stage);
        return payload;
    }

    public static byte[] BuildFastLoginRestartPayload()
        => new byte[4];

    public static byte[] BuildGateCacheAcknowledgementPayload()
        => new byte[4];
}
