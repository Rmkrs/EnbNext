using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Assets;

/// <summary>
/// Current-retail W3D 0x0600 aggregate descriptor. The selected retail loader
/// reads 0x0601 as a 20-byte version/name header, 0x0602 as a 36-byte
/// base-model/count prefix followed by count 64-byte subobject/bone pairs, and
/// 0x0604 as a 20-byte class-info record. Current retail's 0x0603 branch reads
/// only the leading replacement-count dword, discards it, and closes/skips the
/// rest of that chunk without storing or applying texture-replacer state.
/// </summary>
public sealed record EnbCurrentRetailW3dAggregateDefinition(
    uint Version,
    string Name,
    string BaseModelName,
    IReadOnlyList<EnbCurrentRetailW3dAggregateAttachment> Attachments,
    uint OriginalClassId,
    uint Flags,
    bool HasTextureReplacerInfo)
{
    public bool ForceSubObjectLod => (Flags & 0x1u) != 0;
}

public readonly record struct EnbCurrentRetailW3dAggregateAttachment(
    string SubobjectName,
    string BoneName);

/// <summary>
/// Runtime resolution metadata retained when an authored 0x0600 aggregate
/// prototype resolves through its referenced base model. The immutable model
/// and texture lists on EnbResolvedW3dResource belong to BaseModelName; this
/// record preserves the wrapper identity and attachment list separately.
/// </summary>
public sealed record EnbCurrentRetailW3dAggregateResolution(
    EnbCurrentRetailW3dAggregateDefinition Definition,
    string BaseLogicalFileName,
    string BaseRequestedObjectName,
    EnbMixResourceLocation BaseResourceLocation,
    EnbCurrentRetailW3dAggregateResolution? InheritedAggregate = null)
{
    public EnbW3dPrototypeEntry? BaseRegisteredPrototype { get; init; }
    public long BasePrototypeRegistration { get; init; }
    public string? BaseLooseFilePath { get; init; }
    //009A4540 creates the base through the same manager before009A3A50
    //appends this wrapper's children. The outer prototype then overwrites
    //the object's flag bit, including clearing an inherited FORCE_SUB_OBJ_LOD.
    //Keep the authored definition separately from this composed render input.
    public EnbCurrentRetailW3dAggregateDefinition RenderDefinition { get; } =
        InheritedAggregate is null ? Definition : Definition with
        { Attachments = [..InheritedAggregate.RenderDefinition.Attachments,..Definition.Attachments] };
}

public static class EnbCurrentRetailW3dAggregateDecoder
{
    private const uint ContainerFlag = 0x8000_0000u;
    private const uint SizeMask = 0x7FFF_FFFFu;
    private const uint AggregateChunk = 0x0600u;
    private const uint HeaderChunk = 0x0601u;
    private const uint InfoChunk = 0x0602u;
    private const uint TextureReplacerChunk = 0x0603u;
    private const uint ClassInfoChunk = 0x0604u;
    private const int ChunkHeaderLength = 8;
    private const int HeaderPayloadLength = 0x14;
    private const int InfoPrefixLength = 0x24;
    private const int AttachmentLength = 0x40;
    private const int ClassInfoPayloadLength = 0x14;

    public static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out EnbCurrentRetailW3dAggregateDefinition aggregate,
        out string? diagnostic)
    {
        aggregate = default!;
        diagnostic = null;
        if (!TryReadChunk(bytes, 0, bytes.Length, out ChunkView root) ||
            root.Type != AggregateChunk ||
            root.PayloadEnd != bytes.Length)
        {
            diagnostic = "resource is not one exact top-level current-retail 0x0600 aggregate chunk.";
            return false;
        }

        // Retail 009A4010 opens children through 009D4310, which masks the
        // parent's length without requiring its container hint. Installed
        // SH_FLSH1.W3D (shield hit) is an aggregate with that hint clear.
        uint? version = null;
        string? name = null;
        string baseModelName = string.Empty;
        List<EnbCurrentRetailW3dAggregateAttachment>? attachments = null;
        uint originalClassId = 25;
        uint flags = 0;
        bool textureReplacerSeen = false;

        int offset = root.PayloadStart;
        while (offset < root.PayloadEnd)
        {
            //009D4310 ends on a short header.009D4370 skips unknown
            //chunks by their declared length, even past the parent's end.
            //Keep that publication behavior without reading beyond the span.
            if (root.PayloadEnd - offset < ChunkHeaderLength) break;
            uint childType = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            if (!TryReadChunk(bytes, offset, root.PayloadEnd, out ChunkView child))
            {
                if (childType is not (HeaderChunk or InfoChunk or ClassInfoChunk)) break;
                diagnostic = $"aggregate child at 0x{offset:X} is malformed.";
                return false;
            }

            ReadOnlySpan<byte> payload = bytes.Slice(child.PayloadStart, child.PayloadLength);
            switch (child.Type)
            {
                case HeaderChunk:
                    if (payload.Length < HeaderPayloadLength)
                    {
                        diagnostic = "aggregate requires a complete 20-byte 0x0601 header.";
                        return false;
                    }
                    version = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    name = ReadFixedAscii(payload.Slice(4, 16));
                    break;

                case InfoChunk:
                    if (payload.Length < InfoPrefixLength)
                    {
                        diagnostic = "aggregate requires a complete 0x0602 info prefix.";
                        return false;
                    }
                    baseModelName = ReadFixedAscii(payload.Slice(0, 32));
                    uint authoredCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(32, 4));
                    //009A4120 consumes the authored DWORD count, stopping on
                    //a failed64-byte read. Bound allocation by the actual
                    //chunk payload rather than an unsupported count ceiling.
                    int expectedLength;
                    try
                    {
                        expectedLength = checked(InfoPrefixLength + checked((int)authoredCount * AttachmentLength));
                    }
                    catch (OverflowException)
                    {
                        diagnostic = "aggregate subobject payload length overflows the bounded decoder.";
                        return false;
                    }
                    if (payload.Length < expectedLength)
                    {
                        diagnostic = $"aggregate 0x0602 payload has {payload.Length} bytes; current retail requires {expectedLength} for {authoredCount} subobject(s).";
                        return false;
                    }

                    int attachmentCount = checked((int)authoredCount);
                    //009A4120 overwrites its base/count prefix, while
                    //009A4180/009A41E0 appends each pair to the retained vector.
                    attachments ??= new List<EnbCurrentRetailW3dAggregateAttachment>(attachmentCount);
                    int attachmentOffset = InfoPrefixLength;
                    for (int index = 0; index < attachmentCount; index++)
                    {
                        string subobject = ReadFixedAscii(payload.Slice(attachmentOffset, 32));
                        string bone = ReadFixedAscii(payload.Slice(attachmentOffset + 32, 32));
                        if (string.IsNullOrWhiteSpace(subobject))
                        {
                            diagnostic = $"aggregate subobject {index} has an empty render-object name.";
                            return false;
                        }
                        //009A4180 retains all 64 bytes.00941F60 passes the
                        //bone string to0098A000, including empty/space names;
                        //the first match wins and a miss returns pivot zero.
                        attachments.Add(new EnbCurrentRetailW3dAggregateAttachment(subobject, bone));
                        attachmentOffset += AttachmentLength;
                    }
                    break;

                case TextureReplacerChunk:
                    // Current client FUN_009A4010 handles 0x0603 by attempting
                    // one 4-byte read into a stack temporary, ignoring both the
                    // value and read result, then closing the chunk. Chunk close
                    // skips the unread remainder. Preserve presence for evidence
                    // but intentionally apply no texture replacement semantics.
                    textureReplacerSeen = true;
                    break;

                case ClassInfoChunk:
                    if (payload.Length < ClassInfoPayloadLength)
                    {
                        diagnostic = "aggregate requires a complete 20-byte 0x0604 class-info record.";
                        return false;
                    }
                    originalClassId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    flags = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
                    break;

                default:
                    //009A403E ->009A4075 closes unknown children without
                    //reading them. Known callbacks also consume only their
                    //fixed prefix/count records;009D4370 skips the remainder.
                    break;
            }

            offset = child.PayloadEnd;
        }

        if (!version.HasValue || string.IsNullOrWhiteSpace(name))
        {
            diagnostic = "aggregate is missing its required 0x0601 header or 0x0602 base-model information.";
            return false;
        }

        aggregate = new EnbCurrentRetailW3dAggregateDefinition(
            version.Value,
            name,
            baseModelName,
            attachments?.ToArray() ?? [],
            originalClassId,
            flags,
            textureReplacerSeen);
        return true;
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end < 0)
            end = bytes.Length;
        return Encoding.ASCII.GetString(bytes[..end]);
    }

    private static bool TryReadChunk(ReadOnlySpan<byte> bytes, int offset, int end, out ChunkView chunk)
    {
        chunk = default;
        if (offset < 0 || end > bytes.Length || end - offset < ChunkHeaderLength)
            return false;

        uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
        uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
        int payloadLength;
        try
        {
            payloadLength = checked((int)(rawSize & SizeMask));
        }
        catch (OverflowException)
        {
            return false;
        }

        int payloadStart = offset + ChunkHeaderLength;
        int payloadEnd;
        try
        {
            payloadEnd = checked(payloadStart + payloadLength);
        }
        catch (OverflowException)
        {
            return false;
        }
        if (payloadEnd > end)
            return false;

        chunk = new ChunkView(type, (rawSize & ContainerFlag) != 0, payloadStart, payloadLength, payloadEnd);
        return true;
    }

    private readonly record struct ChunkView(
        uint Type,
        bool IsContainer,
        int PayloadStart,
        int PayloadLength,
        int PayloadEnd);
}
