using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace EnbNext.Net7.Gameplay;

public readonly record struct Net7InventoryTransferRequest(
    uint Id,
    uint FromInventory,
    uint FromSlot,
    uint ToInventory,
    uint ToSlot,
    uint Count);

public readonly record struct Net7ItemStateRequest(
    uint Id,
    uint State,
    byte BoolValue,
    byte Inventory,
    byte Slot);

public readonly record struct Net7InventorySortRequest(
    uint Id,
    uint TargetInventory,
    uint Sort1,
    uint Sort2,
    uint Sort3,
    bool Reverse);

public readonly record struct Net7HulkOpenRequest(
    uint PlayerId,
    uint Request);

public readonly record struct Net7UiTriggerRequest(
    uint Id,
    uint Action);

public readonly record struct Net7MissionForfeitRequest(
    uint PlayerId,
    uint InternalMissionId);

public readonly record struct Net7MissionDismissRequest(
    uint PlayerId,
    uint InternalMissionId);

public readonly record struct Net7ManufactureItemRequest(
    uint PlayerId,
    uint ItemId);

public readonly record struct Net7RefineryItemRequest(
    uint PlayerId,
    uint ItemId);


public readonly record struct Net7ManufacturingActionRequest(
    uint PlayerId,
    uint ActionId);

public readonly record struct Net7ManufacturingFilterRequest(
    uint PlayerId,
    bool Enable,
    uint BitField);

/// <summary>
/// Codecs for current retail-range gameplay payloads and current Net7
/// gameplay extensions. Every method is backed by a research dossier; no
/// historical Freya serializer is active in this layer.
/// </summary>
public static class Net7GameplayWire
{
    private static readonly Encoding WireText = Encoding.Latin1;

    // The integrated client sends Warp directly in the 12-byte
    // server UDP envelope. The u16 outer length is therefore the tightest
    // representability bound, not an invented planner-size limit.
    public const int MaxWarpRouteEntries = (ServerUdpFrame.MaxPayloadSize - 6) / 4;

    public static byte[] BuildSteering(uint gameId, float intensity)
    {
        byte[] payload = new byte[8];
        WriteU32LE(payload, 0, gameId);
        WriteF32LE(payload, 4, intensity);
        return payload;
    }

    public static byte[] BuildMove(uint gameId, byte moveType)
    {
        byte[] payload = new byte[5];
        WriteU32LE(payload, 0, gameId);
        payload[4] = moveType;
        return payload;
    }

    public static byte[] BuildFire(uint gameId, byte slot)
    {
        byte[] payload = new byte[5];
        WriteU32LE(payload, 0, gameId);
        payload[4] = slot;
        return payload;
    }

    public static (uint GameId, byte Slot) DecodeFire(ReadOnlySpan<byte> payload)
    {
        RequireExact(payload, 5, "0x0015 Fire");
        return (ReadU32LE(payload, 0), payload[4]);
    }

    public static byte[] BuildTargetRequest(uint gameId, uint targetId)
        => BuildTwoU32LE(gameId, targetId);

    public static byte[] BuildAction(uint gameId, Net7PlayerAction action, uint targetId, uint optionalVar)
    {
        byte[] payload = new byte[16];
        WriteU32LE(payload, 0, gameId);
        WriteI32LE(payload, 4, checked((int)action));
        WriteU32LE(payload, 8, targetId);
        WriteU32LE(payload, 12, optionalVar);
        return payload;
    }

    public static Net7ActionCommand DecodeAction(ReadOnlySpan<byte> payload)
    {
        // Retail's current Action object is exactly four raw LE dwords.
        // The legacy proxy read Action at +0x04 without an Action-local length
        // guard, but the clean integrated client must not reproduce that short-
        // buffer hazard or accept an invented trailing extension.
        RequireExact(payload, 16, "0x002C Action");
        return new Net7ActionCommand(
            ReadU32LE(payload, 0),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4)),
            ReadU32LE(payload, 8),
            ReadU32LE(payload, 12));
    }

    public static Net7CameraControl DecodeCameraControl(ReadOnlySpan<byte> payload)
    {
        // 00878390 initializes Message=-1 and GameID=0, then makes two
        // bounded network-order reads. 00739FF0 ignores unknown messages.
        return new Net7CameraControl(
            ReadInitializedU32BE(payload, uint.MaxValue),
            ReadInitializedU32BE(payload[Math.Min(4, payload.Length)..], 0));
    }

    public static byte[] BuildInventoryTransfer(
        uint id,
        uint fromInventory,
        uint fromSlot,
        uint toInventory,
        uint toSlot,
        uint count)
    {
        Net7InventoryDomain.RequireServerTransferDestination(toInventory);
        return EncodeInventoryTransfer(id, fromInventory, fromSlot, toInventory, toSlot, count);
    }

    // Current retail 00690E51 -> 00690E9E bypasses the C/E manufacturing return
    // branch after Yes. Destination B remains B: it is confirmed destruction.
    public static byte[] BuildConfirmedInventoryDestruction(uint id, uint inventory, uint slot, uint count)
    {
        // 005A0EB0 starts the dump from any non-vendor inventory (equipment included).
        if (slot == uint.MaxValue || count == 0)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return EncodeInventoryTransfer(id, inventory, slot, 0x0B, uint.MaxValue, count);
    }

    private static byte[] EncodeInventoryTransfer(uint id,uint fromInventory,uint fromSlot,uint toInventory,uint toSlot,uint count)
    {
        byte[] payload = new byte[24];
        WriteU32BE(payload, 0, id);
        WriteU32BE(payload, 4, fromInventory);
        WriteU32BE(payload, 8, fromSlot);
        WriteU32BE(payload, 12, toInventory);
        WriteU32BE(payload, 16, toSlot);
        WriteU32BE(payload, 20, count);
        return payload;
    }

    public static Net7InventoryTransferRequest DecodeInventoryTransferRequest(ReadOnlySpan<byte> payload)
    {
        // Retail has a structural inbound decoder for 0x0027,
        // but its process virtual is an assertion stub. Decode only to bound the
        // exact packet-object grammar; this must not become inventory state.
        RequireExact(payload, 24, "0x0027 Inventory");
        return new Net7InventoryTransferRequest(
            BinaryPrimitives.ReadUInt32BigEndian(payload[..4]),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(12, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(16, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(20, 4)));
    }

    public static byte[] BuildInventorySort(
        uint gameId,
        uint targetInventory,
        uint sort1,
        uint sort2,
        uint sort3,
        bool reverse)
    {
        byte[] payload = new byte[21];
        WriteU32BE(payload, 0, gameId);
        WriteU32BE(payload, 4, targetInventory);
        WriteU32BE(payload, 8, sort1);
        WriteU32BE(payload, 12, sort2);
        WriteU32BE(payload, 16, sort3);
        payload[20] = reverse ? (byte)1 : (byte)0;
        return payload;
    }

    public static Net7InventorySortRequest DecodeInventorySortRequest(ReadOnlySpan<byte> payload)
    {
        // Retail has a structural inbound decoder for InventorySort, but
        // the packet process virtual is an unconditional assertion stub. Decode
        // only the exact request-object grammar; this is not inventory state or
        // a same-opcode acknowledgement. The final byte is a normalized bool.
        RequireExact(payload, 21, "0x0028 InventorySort");
        return new Net7InventorySortRequest(
            BinaryPrimitives.ReadUInt32BigEndian(payload[..4]),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(12, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(16, 4)),
            payload[20] != 0);
    }

    public static byte[] BuildItemStateRequest(uint requestId, uint state, bool enabled, byte inventory, byte slot)
    {
        byte[] payload = new byte[11];
        WriteU32LE(payload, 0, requestId);
        WriteU32LE(payload, 4, state);
        payload[8] = enabled ? (byte)1 : (byte)0;
        payload[9] = inventory;
        payload[10] = slot;
        return payload;
    }

    public static Net7ItemStateRequest DecodeItemStateRequest(ReadOnlySpan<byte> payload)
    {
        // Unlike 0x0027, the two DWORDs in 0x0029 are raw little-endian
        // x86 values. Retail's inbound processor still asserts, so this is only
        // a structural request decoder, never the persistent ItemState owner.
        RequireExact(payload, 11, "0x0029 ItemStates");
        return new Net7ItemStateRequest(
            ReadU32LE(payload, 0),
            ReadU32LE(payload, 4),
            payload[8],
            payload[9],
            payload[10]);
    }

    public static byte[] BuildAction2(uint gameId, uint action, string name, uint optionalVar)
    {
        byte[] text = GetWireBytes(name, nameof(name));
        if (text.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(name), "Action2 name exceeds the u16 SharedString limit.");
        byte[] payload = new byte[14 + text.Length];
        WriteU32BE(payload, 0, gameId);
        WriteU32BE(payload, 4, action);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), checked((ushort)text.Length));
        text.CopyTo(payload, 10);
        WriteU32BE(payload, 10 + text.Length, optionalVar);
        return payload;
    }

    public static byte[] BuildGroupFinderOption(uint gameId, uint optionType, bool enabled)
    {
        byte[] payload = new byte[9];
        WriteU32LE(payload, 0, gameId);
        WriteU32LE(payload, 4, optionType);
        payload[8] = enabled ? (byte)1 : (byte)0;
        return payload;
    }

    public static byte[] BuildPlayerCommand(uint gameId, Net7ChatCommandType type, string text, string? target = null)
    {
        byte[] textBytes = GetCStringBytes(text, nameof(text));
        byte[] targetBytes = target is null ? [] : GetCStringBytes(target, nameof(target));
        if (textBytes.Length > ushort.MaxValue || targetBytes.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(text), "Player-command strings exceed the u16 wire limit.");

        byte[] payload = new byte[9 + textBytes.Length + targetBytes.Length];
        WriteU32LE(payload, 0, gameId);
        payload[4] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5, 2), checked((ushort)textBytes.Length));
        textBytes.CopyTo(payload, 7);
        int offset = 7 + textBytes.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset, 2), checked((ushort)targetBytes.Length));
        targetBytes.CopyTo(payload, offset + 2);
        return payload;
    }

    public static byte[] BuildStarbaseRequest(uint playerId, uint starbaseId, byte action)
    {
        byte[] payload = new byte[9];
        WriteU32LE(payload, 0, playerId);
        WriteU32LE(payload, 4, starbaseId);
        payload[8] = action;
        return payload;
    }

    public static Net7StarbaseRequest DecodeStarbaseRequest(ReadOnlySpan<byte> payload)
    {
        // The current retail Starbase Request object is exactly two raw
        // little-endian dwords followed by one Action byte. Net7Proxy's legacy
        // preprocessor reads Action at +0x08 without a local bounds guard; the
        // clean integrated client instead rejects both truncated and trailing
        // forms before exposing the packet.
        RequireExact(payload, 9, "0x004E Starbase Request");
        return new Net7StarbaseRequest(
            ReadU32LE(payload, 0),
            ReadU32LE(payload, 4),
            payload[8]);
    }

    public static byte[] BuildHulkOpenRequest(uint startSessionValue)
        => BuildTwoU32LE(startSessionValue, 0);

    public static Net7HulkOpenRequest DecodeHulkOpenRequest(ReadOnlySpan<byte> payload)
    {
        RequireExact(payload, 8, "0x0051 Request_Skill_Strings");
        return new Net7HulkOpenRequest(ReadU32LE(payload, 0), ReadU32LE(payload, 4));
    }

    public static byte[] BuildSkillAction(uint gameId, uint skillId)
    {
        byte[] payload = new byte[12];
        WriteU32LE(payload, 0, gameId);
        WriteU32LE(payload, 4, 1);
        WriteU32LE(payload, 8, skillId);
        return payload;
    }

    public static Net7SkillActionCommand DecodeSkillAction(ReadOnlySpan<byte> payload)
    {
        // The retail 0x0057 object is exactly three raw little-endian
        // dwords. Numeric server 0x0057 is not a completion packet, but the clean
        // integrated decoder still bounds the object before rejecting its process
        // semantics rather than inheriting retail's unsafe packet-object reads.
        RequireExact(payload, 12, "0x0057 SkillAction");
        return new Net7SkillActionCommand(
            ReadU32LE(payload, 0),
            ReadU32LE(payload, 4),
            ReadU32LE(payload, 8));
    }

    public static byte[] BuildSkillAbility(uint gameId, uint abilityIndex)
    {
        byte[] payload = new byte[12];
        WriteU32LE(payload, 0, gameId);
        WriteU32LE(payload, 4, 0);
        WriteU32LE(payload, 8, abilityIndex);
        return payload;
    }

    public static Net7SkillAbilityCommand DecodeSkillAbility(ReadOnlySpan<byte> payload)
    {
        // Numeric inbound 0x0058 has a real packet-object decoder,
        // but its retail process virtual is an unconditional assertion stub.
        // Bound the exact three-dword object grammar before the integrated path
        // rejects its process semantics instead of letting it fall through as
        // ordinary materialized gameplay state.
        RequireExact(payload, 12, "0x0058 SkillAbility");
        return new Net7SkillAbilityCommand(
            ReadU32LE(payload, 0),
            ReadU32LE(payload, 4),
            ReadU32LE(payload, 8));
    }

    public static byte[] BuildUseInventoryItem(uint targetGameId, byte inventory, byte slot)
    {
        // GameID is the client-owned current selected-target identity,
        // not the local player ObjectId and not authoritative ShipAuxData.TargetGameID.
        // EnbNext receives the correct equivalent explicitly from the owning UI/input
        // state and preserves zero when zero is supplied.
        byte[] payload = new byte[6];
        WriteU32LE(payload, 0, targetGameId);
        payload[4] = inventory;
        payload[5] = slot;
        return payload;
    }

    public static Net7UseInventoryItemRequest DecodeUseInventoryItem(ReadOnlySpan<byte> payload)
    {
        // Retail can structurally instantiate an inbound 0x005D object,
        // but its process virtual asserts. Bound the exact six-byte request grammar
        // before the integrated path rejects same-opcode completion semantics.
        RequireExact(payload, 6, "0x005D UseInventoryItem");
        return new Net7UseInventoryItemRequest(
            ReadU32LE(payload, 0),
            payload[4],
            payload[5]);
    }

    public static byte[] BuildUiTrigger(uint id, uint action)
        => BuildTwoU32LE(id, action);

    public static Net7UiTriggerRequest DecodeUiTrigger(ReadOnlySpan<byte> payload)
    {
        RequireExact(payload, 8, "0x0065 UITrigger");
        return new Net7UiTriggerRequest(ReadU32LE(payload, 0), ReadU32LE(payload, 4));
    }

    public static byte[] BuildManufacturingMode(uint playerId, Net7ManufacturingMode mode)
        => BuildTwoU32BE(playerId, (uint)mode);

    public static Net7ManufacturingModeRequest DecodeManufacturingModeRequest(ReadOnlySpan<byte> payload)
    {
        // Retail can structurally decode numeric inbound 0x0079, but
        // its process virtual unconditionally asserts. Decode only to bound the
        // exact two-u32be request grammar; current sender reachability is a
        // separate concern from structural decoding.
        RequireExact(payload, 8, "0x0079 manufacturing mode request");
        return new Net7ManufacturingModeRequest(
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)));
    }

    public static Net7ManufacturingCategoryRequest DecodeManufacturingCategoryRequest(ReadOnlySpan<byte> payload)
    {
        // Retail can structurally decode numeric inbound 0x007A, but
        // its process virtual unconditionally asserts. Decode only to bound the
        // exact final-leaf request grammar; it is not an ACK/materializer.
        RequireExact(payload, 8, "0x007A manufacturing category request");
        return new Net7ManufacturingCategoryRequest(
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)));
    }

    public static byte[] BuildManufacturingCategory(uint playerId, uint selectedSubcategoryId)
        => BuildTwoU32BE(playerId, selectedSubcategoryId);

    public static Net7ManufactureItemRequest DecodeManufactureItemRequest(ReadOnlySpan<byte> payload)
    {
        // Retail can structurally decode numeric inbound 0x007B, but
        // its process virtual unconditionally asserts. Decode only to bound the
        // exact manufacture-selection request grammar; it is not an ACK or
        // manufacturing-state materializer.
        RequireExact(payload, 8, "0x007B manufacture item request");
        return new Net7ManufactureItemRequest(
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)));
    }

    public static byte[] BuildManufactureItem(uint playerId, uint itemId)
        => BuildTwoU32BE(playerId, itemId);

    public static Net7RefineryItemRequest DecodeRefineryItemRequest(ReadOnlySpan<byte> payload)
    {
        // 0x007C deliberately differs from sibling 0x007B. Retail
        // decodes two raw host-order DWORDs; on the current x86 client that is
        // little-endian. Its inbound process virtual asserts, so this decoder
        // only bounds the request object and must never be treated as an ACK.
        RequireExact(payload, 8, "0x007C refinery item request");
        return new Net7RefineryItemRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)));
    }

    public static byte[] BuildRefineryItem(uint playerId, uint itemId)
        => BuildTwoU32LE(playerId, itemId);


    public static Net7ManufacturingActionRequest DecodeManufacturingActionRequest(ReadOnlySpan<byte> payload)
    {
        // 0x007E is exactly Player + ActionID as two network-order
        // DWORDs. Retail's packet factory can decode the object inbound, but
        // its process virtual is an unconditional assertion, so this decoder
        // only bounds request grammar and must never be treated as an ACK.
        RequireExact(payload, 8, "0x007E manufacturing action request");
        return new Net7ManufacturingActionRequest(
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)));
    }

    public static byte[] BuildManufacturingAction(uint playerId, uint actionId)
        => BuildTwoU32BE(playerId, actionId);

    public static Net7ManufacturingFilterRequest DecodeManufacturingFilterRequest(ReadOnlySpan<byte> payload)
    {
        // 0x0080 is exactly Player(u32be) + normalized one-byte Mode
        // + BitField(u32be). Retail's decoder canonicalizes any nonzero Mode to
        // true, while its process virtual asserts, so this only bounds the
        // request object and must never be interpreted as an ACK/materializer.
        RequireExact(payload, 9, "0x0080 manufacturing tech-level filter request");
        return new Net7ManufacturingFilterRequest(
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4)),
            payload[4] != 0,
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(5, 4)));
    }

    public static byte[] BuildManufacturingFilter(uint playerId, bool enable, uint bitField)
    {
        byte[] payload = new byte[9];
        WriteU32BE(payload, 0, playerId);
        payload[4] = enable ? (byte)1 : (byte)0;
        WriteU32BE(payload, 5, bitField);
        return payload;
    }

    public static byte[] BuildMissionRequest(uint playerId, uint internalMissionId)
        => BuildTwoU32BE(playerId, internalMissionId);

    public static Net7MissionForfeitRequest DecodeMissionForfeitRequest(ReadOnlySpan<byte> payload)
    {
        // Retail can structurally decode numeric inbound 0x0086,
        // but its process virtual unconditionally asserts. Decode only to bound
        // the exact request-object grammar; this is not mission result state.
        RequireExact(payload, 8, "0x0086 MissionForfeitRequest");
        return new Net7MissionForfeitRequest(
            BinaryPrimitives.ReadUInt32BigEndian(payload[..4]),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)));
    }

    public static Net7MissionDismissRequest DecodeMissionDismissRequest(ReadOnlySpan<byte> payload)
    {
        // Retail can structurally decode numeric inbound 0x0087,
        // but its process virtual unconditionally asserts. Decode only to bound
        // the exact request-object grammar; this is not a Clear result/ACK.
        RequireExact(payload, 8, "0x0087 MissionDismissRequest");
        return new Net7MissionDismissRequest(
            BinaryPrimitives.ReadUInt32BigEndian(payload[..4]),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)));
    }

    public static byte[] BuildIncapacitationRequest(uint gameId)
    {
        byte[] payload = new byte[4];
        WriteU32BE(payload, 0, gameId);
        return payload;
    }

    public static uint DecodeIncapacitationRequest(ReadOnlySpan<byte> payload)
    {
        RequireExact(payload, 4, "0x008D/0x008F incapacitation request");
        return BinaryPrimitives.ReadUInt32BigEndian(payload);
    }

    public static byte[] BuildDistressBeacon(uint gameId, bool on)
    {
        byte[] payload = new byte[5];
        WriteU32BE(payload, 0, gameId);
        payload[4] = on ? (byte)1 : (byte)0;
        return payload;
    }

    public static (uint GameId, byte On) DecodeDistressBeacon(ReadOnlySpan<byte> payload)
    {
        RequireExact(payload, 5, "0x008E IncapacitanceDistressBeacon");
        return (BinaryPrimitives.ReadUInt32BigEndian(payload[..4]), payload[4]);
    }

    public static byte[] BuildWarp(uint gameId, IReadOnlyList<uint> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Count > MaxWarpRouteEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(route),
                $"Warp route has {route.Count} entries; the current server UDP frame can represent at most {MaxWarpRouteEntries}.");
        }

        int payloadLength = checked(6 + route.Count * 4);
        byte[] payload = new byte[payloadLength];
        WriteU32LE(payload, 0, gameId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), checked((ushort)route.Count));
        for (int i = 0; i < route.Count; i++)
            WriteU32LE(payload, 6 + i * 4, route[i]);
        return payload;
    }

    public static byte[] BuildPlayerNlpStream(IReadOnlyList<Net7PlayerNlpRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        checked
        {
            int total = 4; // terminal 08 00 00 00
            var encoded = new byte[records.Count][];
            for (int i = 0; i < records.Count; i++)
            {
                Net7PlayerNlpRecord record = records[i] ?? throw new ArgumentException("PlayerNLP record cannot be null.", nameof(records));
                byte[] payload = record.RecordType switch
                {
                    0 or 2 when record.TextPayload is null => Array.Empty<byte>(),
                    1 when record.TextPayload is not null => GetCStringBytes(record.TextPayload, nameof(records)),
                    0 or 2 => throw new ArgumentException("Submit-generated PlayerNLP mood/manual-gesture records carry no payload.", nameof(records)),
                    1 => throw new ArgumentException("Submit-generated PlayerNLP text records require a text payload.", nameof(records)),
                    _ => throw new ArgumentOutOfRangeException(nameof(records), $"Submit-generated PlayerNLP record type {record.RecordType} is outside the proven 0,1,2 family."),
                };
                if (payload.Length > ushort.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(records), "PlayerNLP record payload exceeds the u16 stored-length field.");
                encoded[i] = payload;
                total += 4 + payload.Length;
            }

            byte[] stream = new byte[total];
            int offset = 0;
            for (int i = 0; i < records.Count; i++)
            {
                Net7PlayerNlpRecord record = records[i];
                byte[] payload = encoded[i];
                stream[offset] = record.RecordType;
                stream[offset + 1] = record.RecordArg;
                BinaryPrimitives.WriteUInt16LittleEndian(stream.AsSpan(offset + 2, 2), checked((ushort)payload.Length));
                payload.CopyTo(stream, offset + 4);
                offset += 4 + payload.Length;
            }
            stream[offset] = 8;
            return stream;
        }
    }

    public static byte[] BuildChatstream(uint gameId, ReadOnlySpan<byte> finalizedPlayerNlpStream)
    {
        int length = finalizedPlayerNlpStream.Length;
        if (length < 4 || finalizedPlayerNlpStream[length - 4] != 8 || finalizedPlayerNlpStream[length - 3] != 0 ||
            finalizedPlayerNlpStream[length - 2] != 0 || finalizedPlayerNlpStream[length - 1] != 0)
        {
            throw new ArgumentException("SOCIAL-02 Chatstream requires a finalized PlayerNLP stream ending in 08 00 00 00.", nameof(finalizedPlayerNlpStream));
        }
        if (finalizedPlayerNlpStream.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(finalizedPlayerNlpStream), "Chatstream exceeds the u16 ChatSize field.");
        byte[] payload = new byte[9 + finalizedPlayerNlpStream.Length];
        WriteU32LE(payload, 0, gameId);
        payload[4] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5, 2), checked((ushort)finalizedPlayerNlpStream.Length));
        finalizedPlayerNlpStream.CopyTo(payload.AsSpan(7));
        // Submit-generated path supplies null trailing C string, therefore TrailingSize=0.
        return payload;
    }

    public static byte[] BuildClientChatRequest(
        uint startSessionValue,
        uint requestCode,
        string arg1,
        string arg2,
        string arg3,
        ReadOnlySpan<byte> blob)
    {
        byte[] a1 = GetWireBytes(arg1, nameof(arg1));
        byte[] a2 = GetWireBytes(arg2, nameof(arg2));
        byte[] a3 = GetWireBytes(arg3, nameof(arg3));
        ValidateSharedString(a1, nameof(arg1));
        ValidateSharedString(a2, nameof(arg2));
        ValidateSharedString(a3, nameof(arg3));

        checked
        {
            byte[] payload = new byte[18 + a1.Length + a2.Length + a3.Length + blob.Length];
            WriteU32LE(payload, 0, startSessionValue);
            WriteU32LE(payload, 4, requestCode);
            int offset = 8;
            offset = WriteSharedString(payload, offset, a1);
            offset = WriteSharedString(payload, offset, a2);
            offset = WriteSharedString(payload, offset, a3);
            WriteU32LE(payload, offset, checked((uint)blob.Length));
            blob.CopyTo(payload.AsSpan(offset + 4));
            return payload;
        }
    }

    public static byte[] BuildCtaRequest(uint avatarId, uint targetId, uint requestType)
    {
        byte[] payload = new byte[12];
        WriteU32LE(payload, 0, avatarId);
        WriteU32LE(payload, 4, targetId);
        WriteU32LE(payload, 8, requestType);
        return payload;
    }

    public static byte[] BuildGuildLeaderAccept(uint playerId, string guildName, bool accept)
    {
        byte[] name = GetWireBytes(guildName, nameof(guildName));
        ValidateSharedString(name, nameof(guildName));
        byte[] payload = new byte[7 + name.Length];
        WriteU32BE(payload, 0, playerId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), checked((ushort)name.Length));
        name.CopyTo(payload, 6);
        payload[^1] = accept ? (byte)1 : (byte)0;
        return payload;
    }

    public static byte[] BuildGuildRecruitAccept(uint playerId, bool accept)
    {
        byte[] payload = new byte[5];
        WriteU32BE(payload, 0, playerId);
        payload[4] = accept ? (byte)1 : (byte)0;
        return payload;
    }

    public static byte[] BuildGuildSimpleReply(uint type, uint playerId, string optionalParam)
    {
        byte[] text = GetWireBytes(optionalParam, nameof(optionalParam));
        ValidateSharedString(text, nameof(optionalParam));
        byte[] payload = new byte[10 + text.Length];
        WriteU32LE(payload, 0, type);
        WriteU32BE(payload, 4, playerId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), checked((ushort)text.Length));
        text.CopyTo(payload, 10);
        return payload;
    }

    public static byte[] BuildGuildRankNamesRequest(uint playerId)
    {
        byte[] payload = new byte[4];
        WriteU32BE(payload, 0, playerId);
        return payload;
    }

    public static Net7ProspectExtension DecodeProspectExtension(ReadOnlySpan<byte> payload)
    {
        Require(payload, 20, "0x2012 Prospect");
        return new Net7ProspectExtension(
            ReadU32LE(payload, 0), ReadU32LE(payload, 4), ReadU32LE(payload, 8),
            ReadU32LE(payload, 12), ReadU32LE(payload, 16));
    }

    public static Net7TractorExtension DecodeTractorExtension(ushort opcode, ReadOnlySpan<byte> payload)
    {
        if (opcode is not 0x2013 and not 0x2014)
            throw new ArgumentOutOfRangeException(nameof(opcode));
        Require(payload, 24, $"0x{opcode:X4} Tractor");
        uint source = ReadU32LE(payload, 0);
        uint basset = ReadU32LE(payload, 4);
        uint effect = ReadU32LE(payload, 8);
        ushort baseAsset = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(12, 2));
        uint timestamp = ReadU32LE(payload, 14);
        ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(18, 2));
        // Safety rule: proxy 004100C0 copies the signed-16 length through
        // 0040F1A0 into a 128-byte stack buffer and NUL-terminates at [length];
        // a longer name overwrites the proxy stack (undefined).
        if (nameLength > 127)
            throw new InvalidDataException($"0x{opcode:X4} Tractor Name length {nameLength} overruns the proxy's 128-byte name buffer.");
        int tailOffset = checked(20 + nameLength);
        int expectedLength = checked(tailOffset + 20);
        Require(payload, expectedLength, $"0x{opcode:X4} Tractor");
        // 004020D0 forwards that buffer as a C string (the first NUL ends it)
        // and strncpy's it into a 118-byte field of a 128-byte stack frame;
        // a longer string overwrites the stack cookie (safety rule).
        ReadOnlySpan<byte> nameBytes = payload.Slice(20, nameLength);
        int nameEnd = nameBytes.IndexOf((byte)0);
        if (nameEnd < 0) nameEnd = nameBytes.Length;
        if (nameEnd > 118)
            throw new InvalidDataException($"0x{opcode:X4} Tractor Name ({nameEnd} bytes) overruns the proxy's 118-byte name field.");
        string name = WireText.GetString(nameBytes[..nameEnd]);
        uint duration = ReadU32LE(payload, tailOffset);
        float tractorSpeed = ReadF32LE(payload, tailOffset + 4);
        Vector3 position = new(
            ReadF32LE(payload, tailOffset + 8),
            ReadF32LE(payload, tailOffset + 12),
            ReadF32LE(payload, tailOffset + 16));
        return new Net7TractorExtension(opcode, source, basset, effect, baseAsset, timestamp, name, duration, tractorSpeed, position);
    }

    public static Net7WarpCompletionExtension DecodeWarpCompletion(ReadOnlySpan<byte> payload)
    {
        Require(payload, 36, "0x202A warp completion");
        return new Net7WarpCompletionExtension(
            new Vector3(
                ReadF32LE(payload, 0),
                ReadF32LE(payload, 4),
                ReadF32LE(payload, 8)),
            new Quaternion(
                ReadF32LE(payload, 12),
                ReadF32LE(payload, 16),
                ReadF32LE(payload, 20),
                ReadF32LE(payload, 24)),
            ReadU32LE(payload, 28),
            ReadU32LE(payload, 32));
    }

    public static uint DecodeCDataRequest(ReadOnlySpan<byte> payload)
    {
        Require(payload, 4, "0x2025 CData request");
        return ReadU32LE(payload, 0);
    }

    public static uint DecodeHulkPermission(ReadOnlySpan<byte> payload)
    {
        // 00873830 initializes the id to -1 before the bounded network-order read.
        return ReadInitializedU32BE(payload, uint.MaxValue);
    }

    public static Net7HullUpgradeNotice DecodeHullUpgrade(ReadOnlySpan<byte> payload)
    {
        Require(payload, 8, "0x0059 HullUpgrade");
        return new Net7HullUpgradeNotice(ReadU32LE(payload, 0), ReadU32LE(payload, 4));
    }

    public static Net7DamagePresentation DecodeClientDamage(ReadOnlySpan<byte> payload)
    {
        // 00880030 reads six fields; 00738970 does not require exhaustion.
        Require(payload, 24, "0x0064 ClientDamage");
        return new Net7DamagePresentation(
            ReadF32LE(payload, 0),
            ReadF32LE(payload, 4),
            ReadU32LE(payload, 8), ReadU32LE(payload, 12), ReadU32LE(payload, 16), ReadU32LE(payload, 20));
    }

    public static Net7IncapacitationDetails DecodeIncapacitationDetails(ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        string first = ReadCString(payload, ref offset, "0x0090 outer string 0");
        const int skeletonOffset = 0xC8;
        const int avatarBaseSize = 0xF1;
        Require(payload[offset..], avatarBaseSize, "0x0090 AvatarData");
        int skeletonLength = payload[(offset + skeletonOffset)..].IndexOf((byte)0);
        if (skeletonLength < 0)
            throw new InvalidDataException("0x0090 AvatarData Skeleton is not NUL terminated.");
        int avatarLength = checked(avatarBaseSize + skeletonLength);
        Require(payload[offset..], avatarLength, "0x0090 AvatarData");
        byte[] avatar = payload.Slice(offset, avatarLength).ToArray();
        offset += avatarLength;
        List<string> remaining = new(7);
        for (int i = 1; i < 8; i++)
            remaining.Add(ReadCString(payload, ref offset, $"0x0090 outer string {i}"));
        return new Net7IncapacitationDetails(first, avatar, remaining);
    }

    public static string DecodeRescueConfirmation(ReadOnlySpan<byte> payload)
    {
        // 008734E0 -> 004AAE90 stops at NUL or the end of the available bytes.
        int end = payload.IndexOf((byte)0);
        return WireText.GetString(end < 0 ? payload : payload[..end]);
    }

    public static Net7TradeState DecodeTradeState(ReadOnlySpan<byte> payload)
    {
        Require(payload, 5, "0x001F trade state");
        return new Net7TradeState(ReadU32LE(payload, 0), payload[4]);
    }

    public static uint DecodeActiveManufacturingObject(ReadOnlySpan<byte> payload)
    {
        // 0089C1C0 initializes the id to zero before the bounded network-order read.
        return ReadInitializedU32BE(payload, 0);
    }

    public static Net7MessageState DecodeMessageString(ReadOnlySpan<byte> payload)
    {
        Require(payload, 3, "0x001D Message String");
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
        // 0089E310 ignores the short-copy count; 00736760/0074D990 only
        // consume through the first NUL. An available terminator is sufficient.
        ReadOnlySpan<byte> textBytes = payload.Slice(3, Math.Min(length, payload.Length - 3));
        int terminator = textBytes.IndexOf((byte)0);
        if (terminator < 0)
            throw new InvalidDataException("0x001D message text is not NUL terminated.");
        // 0089E310 reads the declared text only; 0040E089 passes it as a
        // C string to AddChatMessage, so the first NUL ends visible text.
        string text = WireText.GetString(textBytes[..terminator]);
        return new Net7MessageState(payload[2], text);
    }

    public static Net7GuildPrompt DecodeGuildRecruitConfirm(ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        string inviter = ReadSharedString(payload, ref offset, "0x00C8 inviter");
        string guild = ReadSharedString(payload, ref offset, "0x00C8 guild");
        return new Net7GuildPrompt(0x00C8, 0, inviter, guild);
    }

    public static Net7GuildPrompt DecodeGuildSimplePrompt(ReadOnlySpan<byte> payload)
    {
        Require(payload, 4, "0x00CC GuildSimpleSectorClient");
        uint type = ReadU32LE(payload, 0);
        int offset = 4;
        string param = ReadSharedString(payload, ref offset, "0x00CC optional parameter");
        return new Net7GuildPrompt(0x00CC, type, param, string.Empty);
    }

    public static Net7GuildMessage DecodeGuildMessage(ReadOnlySpan<byte> payload)
    {
        Require(payload, 4, "0x00D0 GuildMessageSector");
        uint type = ReadU32LE(payload, 0);
        if (type > 131)
            throw new InvalidDataException($"0x00D0 GuildMessageSector MessageType {type} exceeds the current 0..131 domain.");
        int offset = 4;
        string other = ReadSharedString(payload, ref offset, "0x00D0 OtherName");
        string guild = ReadSharedString(payload, ref offset, "0x00D0 GuildName");
        return new Net7GuildMessage(type, other, guild);
    }

    public static Net7GuildPermissions DecodeGuildPermissions(ReadOnlySpan<byte> payload)
    {
        Require(payload, 16, "0x00D2 GuildPlayerPermissions");
        return new Net7GuildPermissions(
            BinaryPrimitives.ReadUInt32BigEndian(payload[..4]),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(12, 4)));
    }

    public static IReadOnlyList<Net7GuildRankName> DecodeGuildRankNames(ReadOnlySpan<byte> payload)
    {
        // 0088DE10 reads a signed count into a local initialized to zero, so an
        // absent count, like a nonpositive one, yields an empty vector.
        if (payload.Length < 4) return Array.Empty<Net7GuildRankName>();
        int count = BinaryPrimitives.ReadInt32BigEndian(payload[..4]);
        if (count <= 0) return Array.Empty<Net7GuildRankName>();
        int offset = 4;
        var result = new List<Net7GuildRankName>(Math.Min(count, (payload.Length - 4) / 6));
        // Net7 sends SharedString names (u16le byte length, no terminator).
        // The unpatched retail constructor's CString reader is not this
        // server wire format; see the 2026-09-16 captured D3 regression.
        // Safety rule: a record past the packet end is an undefined native
        // read, so complete records are kept and decoding stops there.
        for (int i = 0; i < count && payload.Length - offset >= 6; i++)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset, 2));
            if (length + 4 > payload.Length - offset - 2) break;
            string name = WireText.GetString(payload.Slice(offset + 2, length));
            offset += 2 + length;
            uint rank = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));
            offset += 4;
            result.Add(new Net7GuildRankName(name, rank));
        }
        // 0088DE10/0073EE20 consume the declared vector without an exhaustion gate.
        return result;
    }

    public static Net7GuildPrompt DecodeGuildGmRenameConfirm(ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        string oldName = ReadSharedString(payload, ref offset, "0x00D8 old guild name");
        string newName = ReadSharedString(payload, ref offset, "0x00D8 new guild name");
        return new Net7GuildPrompt(0x00D8, 15, oldName, newName);
    }

    public static byte[] BuildLogoffRequest(uint startSessionValue)
    {
        byte[] payload = new byte[4];
        WriteU32LE(payload, 0, startSessionValue);
        return payload;
    }

    public static byte[] BuildDebug(uint gameId, uint command, uint userData)
    {
        byte[] payload = new byte[12];
        WriteU32LE(payload, 0, gameId);
        WriteU32LE(payload, 4, command);
        WriteU32LE(payload, 8, userData);
        return payload;
    }

    public static byte[] BuildMessageString(byte color, string text)
    {
        byte[] textBytes = GetCStringBytes(text, nameof(text));
        if (textBytes.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(text), "0x001D Message String exceeds the u16 Size field.");
        byte[] payload = new byte[3 + textBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), checked((ushort)textBytes.Length));
        payload[2] = color;
        textBytes.CopyTo(payload, 3);
        return payload;
    }

    public static byte[] BuildSelectTalkTree(uint playerId, byte selection)
    {
        byte[] payload = new byte[5];
        WriteU32LE(payload, 0, playerId);
        payload[4] = selection;
        return payload;
    }

    public static byte[] BuildVerbRequest(uint subjectId, uint objectId, uint action)
    {
        byte[] payload = new byte[12];
        WriteU32BE(payload, 0, subjectId);
        WriteU32BE(payload, 4, objectId);
        WriteU32LE(payload, 8, action);
        return payload;
    }

    public static byte[] BuildGadgetControl(string name, uint function, uint gameId)
    {
        byte[] nameBytes = GetCStringBytes(name, nameof(name));
        if (nameBytes.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(name), "0x0068 Gadget Control name exceeds the u16 NameSize field.");
        byte[] payload = new byte[10 + nameBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), checked((ushort)nameBytes.Length));
        WriteU32LE(payload, 2, function);
        nameBytes.CopyTo(payload, 6);
        WriteU32LE(payload, 6 + nameBytes.Length, gameId);
        return payload;
    }

    public static byte[] BuildRecustomizeShipDone(ReadOnlySpan<byte> serializedShipCustomization, uint sessionValue)
    {
        if (serializedShipCustomization.Length != 194)
            throw new ArgumentException("0x0082 requires the exact 194-byte ShipCustomization record.", nameof(serializedShipCustomization));
        byte[] payload = new byte[198];
        serializedShipCustomization.CopyTo(payload);
        WriteU32BE(payload, 194, sessionValue);
        return payload;
    }

    public static byte[] BuildRecustomizeAvatarDone(ReadOnlySpan<byte> serializedAvatarData, uint sessionValue)
    {
        const int avatarBaseSize = 0xF1;
        const int skeletonOffset = 0xC8;
        if (serializedAvatarData.Length < avatarBaseSize)
            throw new ArgumentException("0x0084 AvatarData is shorter than the current 0xF1-byte base geometry.", nameof(serializedAvatarData));
        int skeletonLength = serializedAvatarData[skeletonOffset..].IndexOf((byte)0);
        if (skeletonLength < 0)
            throw new ArgumentException("0x0084 AvatarData Skeleton is not NUL terminated.", nameof(serializedAvatarData));
        int expectedLength = checked(avatarBaseSize + skeletonLength);
        if (serializedAvatarData.Length != expectedLength)
            throw new ArgumentException($"0x0084 AvatarData length {serializedAvatarData.Length} does not match current dynamic geometry {expectedLength}.", nameof(serializedAvatarData));
        byte[] payload = new byte[expectedLength + 4];
        serializedAvatarData.CopyTo(payload);
        WriteU32BE(payload, expectedLength, sessionValue);
        return payload;
    }

    public static byte[] BuildPetitionStuck(uint gameId1, uint gameId2, string subject, string complaint, string playerList)
    {
        byte[] subjectBytes = GetCStringBytes(subject, nameof(subject));
        byte[] complaintBytes = GetCStringBytes(complaint, nameof(complaint));
        byte[] playerListBytes = GetCStringBytes(playerList, nameof(playerList));
        checked
        {
            byte[] payload = new byte[8 + subjectBytes.Length + complaintBytes.Length + playerListBytes.Length];
            WriteU32LE(payload, 0, gameId1);
            WriteU32LE(payload, 4, gameId2);
            int offset = 8;
            subjectBytes.CopyTo(payload, offset);
            offset += subjectBytes.Length;
            complaintBytes.CopyTo(payload, offset);
            offset += complaintBytes.Length;
            playerListBytes.CopyTo(payload, offset);
            return payload;
        }
    }

    public static byte[] BuildGalaxyMap2(uint type, ReadOnlySpan<byte> data, uint channel, bool flag)
    {
        checked
        {
            byte[] payload = new byte[13 + data.Length];
            WriteU32LE(payload, 0, type);
            WriteU32LE(payload, 4, checked((uint)data.Length));
            data.CopyTo(payload.AsSpan(8));
            WriteU32LE(payload, 8 + data.Length, channel);
            payload[12 + data.Length] = flag ? (byte)1 : (byte)0;
            return payload;
        }
    }

    public static byte[] BuildStarbaseAvatarChange(uint avatarId, uint room, float orientation, Vector3 position, uint actionFlag)
    {
        byte[] payload = new byte[28];
        WriteU32LE(payload, 0, avatarId);
        WriteU32LE(payload, 4, room);
        WriteF32LE(payload, 8, orientation);
        WriteF32LE(payload, 12, position.X);
        WriteF32LE(payload, 16, position.Y);
        WriteF32LE(payload, 20, position.Z);
        WriteU32LE(payload, 24, actionFlag);
        return payload;
    }

    public static byte[] BuildStarbaseRoomChange(uint avatarId, uint newRoom, uint oldRoom)
    {
        byte[] payload = new byte[12];
        WriteU32LE(payload, 0, avatarId);
        WriteU32LE(payload, 4, newRoom);
        WriteU32LE(payload, 8, oldRoom);
        return payload;
    }

    public static byte[] BuildTriggerEmote(uint gameId, uint emote)
    {
        byte[] payload = new byte[8];
        WriteU32BE(payload, 0, gameId);
        WriteU32LE(payload, 4, emote);
        return payload;
    }

    public static byte[] BuildConfirmedActionResponse(uint avatarId, uint id, bool accepted)
    {
        byte[] payload = new byte[12];
        WriteU32BE(payload, 0, avatarId);
        WriteU32BE(payload, 4, id);
        WriteU32BE(payload, 8, accepted ? 1u : 0u);
        return payload;
    }

    public static byte[] BuildSpendReferralPoint(uint referralActionValue)
    {
        byte[] payload = new byte[4];
        WriteU32BE(payload, 0, referralActionValue);
        return payload;
    }

    public static byte[] BuildGmEditorCommand(uint contextValue, uint flag, string name)
    {
        byte[] nameBytes = GetCStringBytes(name, nameof(name));
        byte[] payload = new byte[8 + nameBytes.Length];
        WriteU32BE(payload, 0, contextValue);
        WriteU32BE(payload, 4, flag);
        nameBytes.CopyTo(payload, 8);
        return payload;
    }

    public static byte[] BuildPositionFeedback(Net7PositionFeedbackSample sample, bool full)
    {
        byte[] payload = new byte[full ? 28 : 16];
        WriteF32LE(payload, 0, sample.Position.X);
        WriteF32LE(payload, 4, sample.Position.Y);
        WriteF32LE(payload, 8, sample.Position.Z);
        if (full)
        {
            WriteF32LE(payload, 12, sample.OrientationColumn0.X);
            WriteF32LE(payload, 16, sample.OrientationColumn0.Y);
            WriteF32LE(payload, 20, sample.OrientationColumn0.Z);
            WriteI32LE(payload, 24, sample.LegacyPositionFeedbackSpeed);
        }
        else
        {
            WriteI32LE(payload, 12, sample.LegacyPositionFeedbackSpeed);
        }
        return payload;
    }

    private static byte[] BuildTwoU32LE(uint a, uint b)
    {
        byte[] payload = new byte[8];
        WriteU32LE(payload, 0, a);
        WriteU32LE(payload, 4, b);
        return payload;
    }

    private static byte[] BuildTwoU32BE(uint a, uint b)
    {
        byte[] payload = new byte[8];
        WriteU32BE(payload, 0, a);
        WriteU32BE(payload, 4, b);
        return payload;
    }

    private static byte[] GetWireBytes(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        return WireText.GetBytes(value);
    }

    private static byte[] GetCStringBytes(string value, string parameterName)
    {
        byte[] bytes = GetWireBytes(value, parameterName);
        if (Array.IndexOf(bytes, (byte)0) >= 0)
            throw new ArgumentException("Wire C strings cannot contain embedded NUL bytes.", parameterName);
        byte[] result = new byte[bytes.Length + 1];
        bytes.CopyTo(result, 0);
        return result;
    }

    private static void ValidateSharedString(byte[] bytes, string parameterName)
    {
        if (bytes.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, "SharedString exceeds the u16 wire length.");
    }

    private static int WriteSharedString(byte[] destination, int offset, byte[] bytes)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset, 2), checked((ushort)bytes.Length));
        bytes.CopyTo(destination, offset + 2);
        return offset + 2 + bytes.Length;
    }

    private static string ReadSharedString(ReadOnlySpan<byte> payload, ref int offset, string name)
    {
        // 0086EB50 initializes the length before 004AACE0's bounded read.
        Span<byte> lengthBytes = stackalloc byte[2];
        lengthBytes.Clear();
        int count = Math.Min(2, payload.Length - offset);
        payload.Slice(offset, count).CopyTo(lengthBytes);
        offset += count;
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
        // Safety rule: 0086EB50's buffer past the end of the packet is undefined;
        // decode only the available bytes and stop reading at the end.
        ReadOnlySpan<byte> bytes = payload.Slice(offset, Math.Min((int)length, payload.Length - offset));
        int terminator = bytes.IndexOf((byte)0);
        string value = WireText.GetString(terminator < 0 ? bytes : bytes[..terminator]);
        offset += bytes.Length;
        return value;
    }

    private static string ReadCString(ReadOnlySpan<byte> payload, ref int offset, string name)
    {
        // 00872E30 -> 004AAE90: available bytes become the string, including
        // an empty string at end-of-buffer; a terminator is consumed if present.
        int nul = payload[offset..].IndexOf((byte)0);
        int length = nul < 0 ? payload.Length - offset : nul;
        string value = WireText.GetString(payload.Slice(offset, length));
        offset += length + (nul >= 0 ? 1 : 0);
        return value;
    }

    private static uint ReadU32LE(ReadOnlySpan<byte> source, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));

    private static float ReadF32LE(ReadOnlySpan<byte> source, int offset)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset, 4)));



    private static void WriteU32LE(Span<byte> destination, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset, 4), value);

    private static void WriteI32LE(Span<byte> destination, int offset, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, 4), value);

    private static void WriteU32BE(Span<byte> destination, int offset, uint value)
        => BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(offset, 4), value);

    private static void WriteF32LE(Span<byte> destination, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, 4), BitConverter.SingleToInt32Bits(value));

    private static void Require(ReadOnlySpan<byte> payload, int minimum, string name)
    {
        if (payload.Length < minimum)
            throw new InvalidDataException($"{name} payload is {payload.Length} bytes; expected at least {minimum}.");
    }

    private static uint ReadInitializedU32BE(ReadOnlySpan<byte> payload, uint initial)
    {
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(field, initial);
        payload[..Math.Min(payload.Length, 4)].CopyTo(field);
        return BinaryPrimitives.ReadUInt32BigEndian(field);
    }

    private static void RequireExact(ReadOnlySpan<byte> payload, int expected, string name)
    {
        if (payload.Length != expected)
            throw new InvalidDataException($"{name} payload is {payload.Length} bytes; expected exactly {expected}.");
    }
}
