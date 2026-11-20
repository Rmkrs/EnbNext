using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Net7.World;

public sealed record Net7AuxDataString(byte[] Bytes)
{
    public string Text => Encoding.Latin1.GetString(Bytes);
}

/// <summary>
/// Context supplied by the owning 0x001B/0x001C materialization path.
/// Time AuxData values are four-byte signed wire timestamps whose nonzero value
/// is converted relative to this client-owned server/local correction origin.
/// </summary>
public readonly record struct Net7AuxDataCodecContext(long TimeBase)
{
    public static Net7AuxDataCodecContext Empty => default;
}

/// <summary>
/// Clean internal/domain representation of the retail Time AuxData property's
/// two-DWORD signed relative value. The wire representation remains exactly
/// four little-endian bytes.
/// </summary>
public readonly record struct Net7AuxDataTime(long RelativeValue);

public enum Net7AuxDataOperationKind
{
    ValueUpdate,
    StateTwo,
    StateZero,
}

public readonly record struct Net7AuxDataOperation(int PropertyId, Net7AuxDataOperationKind Kind);

public sealed class Net7AuxDataCodec
{
    public void Apply(Net7AuxDataSchema schema, ReadOnlySpan<byte> delta)
        => Apply(schema, delta, Net7AuxDataCodecContext.Empty);

    public void Apply(Net7AuxDataSchema schema, ReadOnlySpan<byte> delta, Net7AuxDataCodecContext context)
        => ApplyCore(schema, delta, context, retainCompletedPrefix: false, default, out _);

    // Retail 004A4E80 commits each successful value immediately. 004A9390 /
    // 004A9400 report a later read failure without undoing those commits.
    // Incoming short reads retain every completed field, including nested
    // records. Never commit incomplete bytes or extend the declared view.
    // Recognized trailing forms below only classify informational diagnostics.
    internal string? ApplyObjectDelta(Net7AuxDataSchema schema, ReadOnlySpan<byte> delta,
        Net7AuxDataCodecContext context)
        => ApplyObjectDelta(schema, delta, context, default, out _);

    internal string? ApplyObjectDelta(Net7AuxDataSchema schema, ReadOnlySpan<byte> delta,
        Net7AuxDataCodecContext context, ReadOnlySpan<byte> trailingBytes, out bool knownIgnoredTail)
        => ApplyCore(schema, delta, context, retainCompletedPrefix: true,
            trailingBytes, out knownIgnoredTail);

    // Incoming packet visitors commit completed fields, including before a
    // short nested selection mask (004A9400). The packet handler 007364C0
    // ignores the visitor failure; it neither rolls back nor reads past the view.
    internal void ApplyIncomingDelta(Net7AuxDataSchema schema, ReadOnlySpan<byte> delta,
        Net7AuxDataCodecContext context)
        => ApplyCore(schema, delta, context, true, default, out _);

    private static string? ApplyCore(Net7AuxDataSchema schema, ReadOnlySpan<byte> delta,
        Net7AuxDataCodecContext context, bool retainCompletedPrefix, ReadOnlySpan<byte> trailingBytes,
        out bool knownIgnoredTail)
    {
        knownIgnoredTail = false;
        ArgumentNullException.ThrowIfNull(schema);
        // Empty incoming views cannot decode a value. 004A3FA0 still invokes
        // a visitor; 004A8FD0 / 004A9400 stop on the first unavailable read.
        if (retainCompletedPrefix && delta.IsEmpty)
            return null;
        if (delta.Length == 0)
            throw new InvalidDataException("AuxData delta is empty; selector byte is required.");

        var cursor = new Cursor(delta.ToArray());
        var journal = new MutationJournal();
        try
        {
            byte selector = cursor.ReadByte();
            if (selector == 0)
                ApplyDefault(schema, cursor, context, journal);
            else
                ApplyCompressed(schema.Root, cursor, context, journal);

            // Current terminal reset packets can carry two unused zero bytes.
            // Retail 007364C0 / 004A3FA0 returns after the aggregate visitor;
            // it does not reject this unused tail. Keep this observed lab form
            // from rolling back the filter and cleared component publication.
            bool terminalPadding = schema.Name == "ManufacturingLab" && cursor.Remaining == 2
                && delta[^2] == 0 && delta[^1] == 0;
            // Incoming 007364C0 -> 004A3FA0 never requires full consumption.
            // Keep exact-consumption validation only for transactional callers.
            if (!retainCompletedPrefix && !cursor.End && !terminalPadding)
                throw new InvalidDataException($"AuxData delta has {cursor.Remaining} unconsumed byte(s).");
            return null;
        }
        catch (InvalidDataException ex) when (retainCompletedPrefix && delta[0] != 0 &&
            (schema.Name is "ShipAuxData" or "Harvestable" ? cursor.ExhaustedRootScalar : cursor.TruncatedValue) && cursor.CompletedValueCount > 0)
        {
            // Current retail commits this prefix and returns normally. Recognize
            // the repeated tail forms for informational diagnostics only;
            // the speculative inspection rolls back ALL of its mutations.
            if (schema.Name == "ShipAuxData" && cursor.FailedPropertyId == 1921 &&
                cursor.ExhaustedRootScalar && trailingBytes.Length == 8 &&
                IsKnownIgnoredShipEngineTail(schema, delta, trailingBytes, context))
            {
                knownIgnoredTail = true;
            }
            else if (schema.Name == "Harvestable" && cursor.FailedPropertyId == 444 &&
                cursor.ExhaustedRootScalar && trailingBytes.Length == 4 &&
                IsKnownIgnoredHarvestableTail(schema, delta, trailingBytes, context))
            {
                knownIgnoredTail = true;
            }
            else if (schema.Name == "HulkAuxData" && cursor.FailedPropertyId == 693 && cursor.Remaining == 2 &&
                delta[^2] == 0xFE && delta[^1] == 0xFF && trailingBytes.Length == 2 &&
                trailingBytes[0] == 0xFF && trailingBytes[1] == 0xFF &&
                IsKnownIgnoredHulkEmptySlotTail(schema, delta, trailingBytes, context))
            {
                knownIgnoredTail = true;
            }
            return $"Retained completed {schema.Name} prefix; unread suffix was not applied: {ex.Message}";
        }
        catch (InvalidDataException ex) when (retainCompletedPrefix && (cursor.ReadTruncated || cursor.UndefinedPropertyRead))
        {
            // 004A9042 dereferences the unchecked lookup result. Undefined in
            // retail; project safety rule: stop here, retain the prefix and
            // continue later packets without a session error or issue report.
            knownIgnoredTail = cursor.ReadTruncated;
            return $"Retained completed {schema.Name} prefix; unread suffix was not applied: {ex.Message}";
        }
        catch
        {
            // Explicit transactional callers and non-read failures still roll back.
            journal.Rollback();
            throw;
        }
    }

    // Four complete September 15 receipts end halfway through Cargo39's -2
    // ItemTemplateID. Retail excludes the two physical trailing bytes and
    // preserves completed fields. Inspect only to classify that exact shape.
    private static bool IsKnownIgnoredHulkEmptySlotTail(Net7AuxDataSchema schema, ReadOnlySpan<byte> delta,
        ReadOnlySpan<byte> tail, Net7AuxDataCodecContext context)
    {
        var last=schema.Resolve(693);
        if(last.Path!="HulkAuxData.Cargo.39.ItemTemplateID" || last.Kind!=Net7AuxDataValueKind.Int32LittleEndian)
            return false;
        byte[] complete=new byte[delta.Length+tail.Length];
        delta.CopyTo(complete);tail.CopyTo(complete.AsSpan(delta.Length));
        var cursor=new Cursor(complete);
        var journal=new MutationJournal();
        try
        {
            cursor.ReadByte();
            ApplyCompressed(schema.Root,cursor,context,journal);
            return cursor.End && last.Value is int value && value==-2;
        }
        catch(InvalidDataException){return false;}
        finally {journal.Rollback();}
    }

    // September 11 capture: TechLevel is selected but lies outside the declared
    // view. Inspect the complete suffix only for diagnostics; never apply it.
    private static bool IsKnownIgnoredHarvestableTail(Net7AuxDataSchema schema, ReadOnlySpan<byte> delta,
        ReadOnlySpan<byte> tail, Net7AuxDataCodecContext context)
    {
        var last = schema.Resolve(444);
        if (last.Path != "Harvestable.TechLevel" || last.Kind != Net7AuxDataValueKind.Int32LittleEndian)
            return false;
        byte[] complete = new byte[delta.Length + tail.Length];
        delta.CopyTo(complete); tail.CopyTo(complete.AsSpan(delta.Length));
        var cursor = new Cursor(complete);
        var journal = new MutationJournal();
        try
        {
            cursor.ReadByte();
            ApplyCompressed(schema.Root, cursor, context, journal);
            return cursor.End;
        }
        catch (InvalidDataException) { return false; }
        finally { journal.Rollback(); }
    }

    private static bool IsKnownIgnoredShipEngineTail(Net7AuxDataSchema schema, ReadOnlySpan<byte> delta,
        ReadOnlySpan<byte> tail, Net7AuxDataCodecContext context)
    {
        byte[] complete = new byte[delta.Length + tail.Length];
        delta.CopyTo(complete); tail.CopyTo(complete.AsSpan(delta.Length));
        var cursor = new Cursor(complete) { EngineSuffixOffset = delta.Length };
        var journal = new MutationJournal();
        try
        {
            cursor.ReadByte(); // Caller already established compressed selector.
            ApplyCompressed(schema.Root, cursor, context, journal);
            if (!cursor.End || cursor.EngineSuffixValues != 2)
                throw new InvalidDataException("Engine suffix does not complete the Ship delta exactly.");
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        finally
        {
            // Inspection only: even a complete candidate must leave the exact
            // native declared-prefix result, including old engine values/state.
            journal.Rollback();
        }
    }

    /// <summary>
    /// Canonical encoder. The caller supplies the three logical
    /// property operations; value-update operations serialize the property's
    /// currently materialized value. The encoder emits selector 1 only when the
    /// compressed structural framing is strictly smaller than the default
    /// three-list framing, otherwise selector 0.
    /// </summary>
    public byte[] Encode(Net7AuxDataSchema schema, IEnumerable<Net7AuxDataOperation> operations)
        => Encode(schema, operations, Net7AuxDataCodecContext.Empty);

    public byte[] Encode(
        Net7AuxDataSchema schema,
        IEnumerable<Net7AuxDataOperation> operations,
        Net7AuxDataCodecContext context)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(operations);

        List<ResolvedOperation> resolved = ResolveCanonicalOperations(schema, operations);
        if (resolved.Count == 0)
            return EncodeDefault(resolved, context);

        CompressedRecord compressed = BuildCompressedRecord(schema.Root, resolved);
        int defaultStructuralBytes = checked(12 + 4 * resolved.Count);
        int compressedStructuralBytes = compressed.GetStructuralSize();

        return compressedStructuralBytes < defaultStructuralBytes
            ? EncodeCompressed(compressed, context)
            : EncodeDefault(resolved, context);
    }

    private static List<ResolvedOperation> ResolveCanonicalOperations(
        Net7AuxDataSchema schema,
        IEnumerable<Net7AuxDataOperation> operations)
    {
        List<ResolvedOperation> resolved = [];
        Dictionary<int, Net7AuxDataOperationKind> byId = new();

        foreach (Net7AuxDataOperation operation in operations)
        {
            Net7AuxDataProperty property = schema.Resolve(operation.PropertyId);
            if (byId.TryGetValue(property.Id, out Net7AuxDataOperationKind prior))
            {
                throw new InvalidDataException(
                    prior == operation.Kind
                        ? $"AuxData canonical encoder received duplicate operation {operation.Kind} for PropertyId {property.Id}."
                        : $"AuxData canonical encoder received conflicting operations {prior} and {operation.Kind} for PropertyId {property.Id}.");
            }

            byId.Add(property.Id, operation.Kind);
            resolved.Add(new ResolvedOperation(property, operation.Kind));
        }

        // A direct aggregate operation and a descendant operation cannot share a
        // canonical compressed child-map without selecting the same route twice.
        // Retail's canonical encoder guards such overlap, so fail closed rather
        // than manufacture legacy decoder-precedence traffic.
        foreach (ResolvedOperation operation in resolved)
        {
            for (Net7AuxDataProperty? ancestor = operation.Property.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (byId.ContainsKey(ancestor.Id))
                {
                    throw new InvalidDataException(
                        $"AuxData canonical encoder cannot combine aggregate PropertyId {ancestor.Id} with descendant PropertyId {operation.Property.Id}.");
                }
            }
        }

        return resolved;
    }

    private static byte[] EncodeDefault(IReadOnlyList<ResolvedOperation> operations, Net7AuxDataCodecContext context)
    {
        var writer = new Writer();
        writer.WriteByte(0);
        WriteDefaultList(writer, operations, Net7AuxDataOperationKind.ValueUpdate, context);
        WriteDefaultList(writer, operations, Net7AuxDataOperationKind.StateTwo, context);
        WriteDefaultList(writer, operations, Net7AuxDataOperationKind.StateZero, context);
        return writer.ToArray();
    }

    private static void WriteDefaultList(
        Writer writer,
        IReadOnlyList<ResolvedOperation> operations,
        Net7AuxDataOperationKind kind,
        Net7AuxDataCodecContext context)
    {
        int count = 0;
        for (int i = 0; i < operations.Count; i++)
        {
            if (operations[i].Kind == kind)
                count++;
        }

        writer.WriteUInt32(checked((uint)count));
        for (int i = 0; i < operations.Count; i++)
        {
            ResolvedOperation operation = operations[i];
            if (operation.Kind != kind)
                continue;

            writer.WriteUInt32(checked((uint)operation.Property.Id));
            if (kind == Net7AuxDataOperationKind.ValueUpdate)
                EncodeValue(operation.Property, writer, context);
        }
    }

    private static byte[] EncodeCompressed(CompressedRecord compressed, Net7AuxDataCodecContext context)
    {
        var writer = new Writer();
        writer.WriteByte(1);
        compressed.Encode(writer, context);
        return writer.ToArray();
    }

    private static CompressedRecord BuildCompressedRecord(
        Net7AuxDataProperty root,
        IReadOnlyList<ResolvedOperation> operations)
    {
        var rootRecord = new CompressedRecord(root);

        foreach (ResolvedOperation operation in operations)
        {
            if (ReferenceEquals(operation.Property, root))
            {
                rootRecord.SetDirect(operation.Kind);
                continue;
            }

            Stack<Net7AuxDataProperty> path = new();
            for (Net7AuxDataProperty? current = operation.Property; current is not null && !ReferenceEquals(current, root); current = current.Parent)
                path.Push(current);

            if (path.Count == 0)
                throw new InvalidDataException($"AuxData PropertyId {operation.Property.Id} is not descended from schema root PropertyId {root.Id}.");

            CompressedRecord record = rootRecord;
            while (path.Count > 0)
            {
                Net7AuxDataProperty child = path.Pop();
                bool isTarget = path.Count == 0;

                if (!isTarget)
                {
                    // Retail FUN_004A9C30 walks the ancestor chain once for
                    // each of the three operation sets and marks every route
                    // edge in that same operation map. Different descendant
                    // operation kinds may therefore select the same aggregate
                    // route child in more than one map; the nested aggregate
                    // record carries the actual descendant operations.
                    record = record.GetOrCreateNestedRoute(child, operation.Kind);
                    continue;
                }

                if (child.Kind == Net7AuxDataValueKind.Aggregate)
                {
                    // The target aggregate owns a record of its own. Its parent
                    // map only routes the visitor to it; the target operation is
                    // represented by the aggregate's direct 0x03/0x05/0x09
                    // record for value/state-two/state-zero respectively.
                    CompressedRecord nested = record.GetOrCreateNestedRoute(child, operation.Kind);
                    nested.SetDirect(operation.Kind);
                }
                else
                {
                    record.SetChildOperation(child, operation.Kind);
                }
            }
        }

        return rootRecord;
    }

    private static void ApplyDefault(
        Net7AuxDataSchema schema,
        Cursor cursor,
        Net7AuxDataCodecContext context,
        MutationJournal journal)
    {
        int valueCount = unchecked((int)cursor.ReadUInt32());
        for (int i = 0; i < valueCount; i++)
        {
            Net7AuxDataProperty property = cursor.ReadProperty(schema);
            ApplyValueUpdate(property, cursor, context, journal);
        }

        int stateTwoCount = unchecked((int)cursor.ReadUInt32());
        for (int i = 0; i < stateTwoCount; i++)
            ApplyStateTwo(cursor.ReadProperty(schema), journal);

        int stateZeroCount = unchecked((int)cursor.ReadUInt32());
        for (int i = 0; i < stateZeroCount; i++)
            ApplyStateZero(cursor.ReadProperty(schema), journal);
    }

    private static void ApplyCompressed(
        Net7AuxDataProperty aggregate,
        Cursor cursor,
        Net7AuxDataCodecContext context,
        MutationJournal journal)
    {
        if (aggregate.Kind != Net7AuxDataValueKind.Aggregate)
            throw new InvalidDataException($"Compressed AuxData routing requires aggregate property {aggregate.Id}.");

        // Current-server LocalPlayer full-state packets select the terminal
        // fixed GroupInfo.Members collection in the parent value map but omit
        // an empty child record entirely. Current retail accepts that shape and
        // the packet ends at this exact boundary. Preserve only that observed
        // compatibility case; every other absent aggregate record remains a
        // bounded truncation failure.
        if (cursor.End &&
            string.Equals(
                aggregate.Path,
                "LocalPlayerAuxData.GroupInfo.Members",
                StringComparison.Ordinal))
        {
            return;
        }

        byte first = cursor.PeekByte();
        int header = first & 0x0F;
        if ((header & 0x01) != 0)
        {
            cursor.ReadByte();
            if ((header & 0x0E) == 0)
            {
                // Current retail tolerates direct header 0x01 as a one-byte
                // no-op. AuxDataDeltaCodecMethod2Body reaches its assertion
                // path when no operation bit is present, but the release
                // ReportAssertionFailure helper only logs and returns; the
                // aggregate visitor then completes without setting failure.
                // Normal current-server Ship AuxData reaches this form, so a
                // clean client must preserve the tolerated wire behavior rather
                // than turn the retail diagnostic assertion into a hard reject.
                return;
            }

            // Current retail also resolves overlapping direct-operation bits by
            // precedence. AuxDataDeltaCodecMethod2Body @ 0x004A9400 tests the
            // value flag first, then state-two, then state-zero and invokes only
            // the first selected aggregate operation. Normal current-server Ship
            // AuxData reaches this shape, so it is part of the compatibility
            // boundary rather than malformed input that a clean decoder may reject.
            if ((header & 0x02) != 0)
                ApplyValueUpdate(aggregate, cursor, context, journal);
            else if ((header & 0x04) != 0)
                ApplyStateTwo(aggregate, journal);
            else
                ApplyStateZero(aggregate, journal);
            return;
        }

        int mapCount = CountOperationBits(header);

        // Current retail's aggregate decoder accepts a child-mask record with
        // no operation maps. 0x004A9710 reads the four logical header bits,
        // rewinds when direct bit 0 is clear, and 0x004A9400 then asks the
        // bitset reader for exactly four bits when bits 1..3 are all zero.
        // That consumes one byte and performs no child operations. Normal
        // current-server ship AuxData reaches this form, so treating header
        // 0x00 as malformed incorrectly aborts otherwise-valid materialization.
        int bitCount = checked(4 + aggregate.Children.Count * mapCount);
        int byteCount = (bitCount + 7) / 8;
        byte[] maps = cursor.ReadBytes(byteCount);
        if ((maps[0] & 0x0F) != header)
            throw new InvalidDataException("Compressed AuxData header changed while reading map bytes.");

        int mapBase = 4;
        int valueMapBase = -1;
        int stateTwoMapBase = -1;
        int stateZeroMapBase = -1;
        if ((header & 0x02) != 0)
        {
            valueMapBase = mapBase;
            mapBase += aggregate.Children.Count;
        }
        if ((header & 0x04) != 0)
        {
            stateTwoMapBase = mapBase;
            mapBase += aggregate.Children.Count;
        }
        if ((header & 0x08) != 0)
            stateZeroMapBase = mapBase;

        // Current retail does not reject a child selected by more than one
        // operation map. AuxDataDeltaCodecMethod2Body @ 0x004A9400 checks the
        // value map first, then state-two, then state-zero and invokes exactly
        // one child visitor. Normal current-server Ship AuxData reaches this
        // overlap, so decoding must preserve retail precedence rather than
        // promote the noncanonical encoder shape into a hard wire rejection.

        for (int childIndex = 0; childIndex < aggregate.Children.Count; childIndex++)
        {
            bool valueSelected = valueMapBase >= 0 && GetBit(maps, valueMapBase + childIndex);
            bool stateTwoSelected = stateTwoMapBase >= 0 && GetBit(maps, stateTwoMapBase + childIndex);
            bool stateZeroSelected = stateZeroMapBase >= 0 && GetBit(maps, stateZeroMapBase + childIndex);
            if (!valueSelected && !stateTwoSelected && !stateZeroSelected)
                continue;

            Net7AuxDataProperty child = aggregate.Children[childIndex];

            // The alternate aggregate visitor does not directly apply the
            // parent map's operation to an aggregate child. It records the map
            // selector, then invokes the selected child's visitor slot (+0x08).
            // Scalar children consume that selector directly; aggregate children
            // enter the aggregate visitor again and consume their own nested
            // compressed record. This is required for value/state-two/state-zero
            // parent maps alike.
            if (child.Kind == Net7AuxDataValueKind.Aggregate)
            {
                int nestedOffset = cursor.Offset;
                try
                {
                    ApplyCompressed(child, cursor, context, journal);
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidDataException(
                        $"AuxData aggregate decode failed at +0x{nestedOffset:X4} PropertyId {child.Id} ({child.Path}): {ex.Message}",
                        ex);
                }
                continue;
            }

            // Retail precedence for overlapping child maps is exact here:
            // value update > state-two > state-zero.
            if (valueSelected)
                ApplyValueUpdate(child, cursor, context, journal);
            else if (stateTwoSelected)
                ApplyStateTwo(child, journal);
            else
                ApplyStateZero(child, journal);
        }
    }

    private static int CountOperationBits(int header)
        => ((header & 0x02) != 0 ? 1 : 0)
            + ((header & 0x04) != 0 ? 1 : 0)
            + ((header & 0x08) != 0 ? 1 : 0);

    private static bool GetBit(ReadOnlySpan<byte> bytes, int bitIndex)
        => (bytes[bitIndex >> 3] & (1 << (bitIndex & 7))) != 0;

    private static void SetBit(Span<byte> bytes, int bitIndex)
        => bytes[bitIndex >> 3] |= checked((byte)(1 << (bitIndex & 7)));

    private static void ApplyValueUpdate(
        Net7AuxDataProperty property,
        Cursor cursor,
        Net7AuxDataCodecContext context,
        MutationJournal journal)
    {
        if (property.Kind == Net7AuxDataValueKind.Aggregate)
        {
            MarkStateTwoAndPropagate(property, journal);
            foreach (Net7AuxDataProperty child in property.Children)
                ApplyValueUpdate(child, cursor, context, journal);
            return;
        }

        int valueOffset = cursor.Offset;
        if (cursor.EngineSuffixOffset is int boundary && valueOffset >= boundary)
        {
            int index = cursor.EngineSuffixValues;
            if (index >= 2 || property.Id != 1921 + index ||
                property.Parent is null || property.Parent.Parent is not null || property.Kind != Net7AuxDataValueKind.Int32LittleEndian ||
                valueOffset != boundary + index * 4)
                throw new InvalidDataException("Unexpected property in candidate engine suffix.");
            cursor.EngineSuffixValues++;
        }
        object value;
        try
        {
            value = DecodeValue(property.Kind, cursor, context);
        }
        catch (InvalidDataException ex)
        {
            cursor.TruncatedValue = cursor.ReadTruncated;
            cursor.FailedPropertyId = property.Id;
            cursor.ExhaustedRootScalar = valueOffset == cursor.Length &&
                property.Parent?.Parent is null && property.Parent is not null;
            throw new InvalidDataException(
                $"AuxData value decode failed at +0x{valueOffset:X4} PropertyId {property.Id} ({property.Path}) kind {property.Kind} next[{cursor.HexWindow(valueOffset, 12)}]: {ex.Message}",
                ex);
        }

        journal.Capture(property);
        property.Value = value;
        MarkStateTwoAndPropagate(property, journal);
        cursor.CompletedValueCount++;
    }

    private static object DecodeValue(
        Net7AuxDataValueKind kind,
        Cursor cursor,
        Net7AuxDataCodecContext context)
        => kind switch
        {
            Net7AuxDataValueKind.UInt64LittleEndian => cursor.ReadUInt64(),
            Net7AuxDataValueKind.Int32LittleEndian => cursor.ReadInt32(),
            Net7AuxDataValueKind.Float32LittleEndian => cursor.ReadSingle(),
            Net7AuxDataValueKind.LengthPrefixedString => new Net7AuxDataString(cursor.ReadLengthPrefixedBytes()),
            Net7AuxDataValueKind.BooleanByte => cursor.ReadByte(),
            Net7AuxDataValueKind.Raw4 => cursor.ReadBytes(4),
            Net7AuxDataValueKind.Raw8TwoUInt32LittleEndian => cursor.ReadBytes(8),
            Net7AuxDataValueKind.Time32LittleEndianRelative => DecodeTime(cursor, context),
            Net7AuxDataValueKind.Raw12 => cursor.ReadBytes(12),
            Net7AuxDataValueKind.Raw16 => cursor.ReadBytes(16),
            Net7AuxDataValueKind.Raw20 => cursor.ReadBytes(20),
            Net7AuxDataValueKind.Aggregate => throw new InvalidOperationException("Aggregate values are decoded recursively."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static Net7AuxDataTime DecodeTime(Cursor cursor, Net7AuxDataCodecContext context)
    {
        int wireValue = cursor.ReadInt32();
        if (wireValue == 0)
            return new Net7AuxDataTime(0);

        // 004A81D0 subtracts the two DWORD context with SUB/SBB. Overflow
        // wraps; it does not fail the visitor or undo earlier values.
        long relative = unchecked((long)wireValue - context.TimeBase);

        return new Net7AuxDataTime(relative);
    }

    private static void EncodeValue(
        Net7AuxDataProperty property,
        Writer writer,
        Net7AuxDataCodecContext context)
    {
        if (property.Kind == Net7AuxDataValueKind.Aggregate)
        {
            foreach (Net7AuxDataProperty child in property.Children)
                EncodeValue(child, writer, context);
            return;
        }

        switch (property.Kind)
        {
            case Net7AuxDataValueKind.UInt64LittleEndian:
                writer.WriteUInt64(RequireValue<ulong>(property));
                return;
            case Net7AuxDataValueKind.Int32LittleEndian:
                writer.WriteInt32(RequireValue<int>(property));
                return;
            case Net7AuxDataValueKind.Float32LittleEndian:
                writer.WriteInt32(BitConverter.SingleToInt32Bits(RequireValue<float>(property)));
                return;
            case Net7AuxDataValueKind.LengthPrefixedString:
                writer.WriteLengthPrefixedBytes(RequireValue<Net7AuxDataString>(property).Bytes);
                return;
            case Net7AuxDataValueKind.BooleanByte:
                writer.WriteByte(RequireValue<byte>(property));
                return;
            case Net7AuxDataValueKind.Raw4:
                writer.WriteFixedBytes(RequireValue<byte[]>(property), 4, property.Id);
                return;
            case Net7AuxDataValueKind.Raw8TwoUInt32LittleEndian:
                writer.WriteFixedBytes(RequireValue<byte[]>(property), 8, property.Id);
                return;
            case Net7AuxDataValueKind.Time32LittleEndianRelative:
                EncodeTime(RequireValue<Net7AuxDataTime>(property), writer, context);
                return;
            case Net7AuxDataValueKind.Raw12:
                writer.WriteFixedBytes(RequireValue<byte[]>(property), 12, property.Id);
                return;
            case Net7AuxDataValueKind.Raw16:
                writer.WriteFixedBytes(RequireValue<byte[]>(property), 16, property.Id);
                return;
            case Net7AuxDataValueKind.Raw20:
                writer.WriteFixedBytes(RequireValue<byte[]>(property), 20, property.Id);
                return;
            case Net7AuxDataValueKind.Aggregate:
                throw new InvalidOperationException("Aggregate values are encoded recursively.");
            default:
                throw new ArgumentOutOfRangeException(nameof(property.Kind));
        }
    }

    private static void EncodeTime(
        Net7AuxDataTime value,
        Writer writer,
        Net7AuxDataCodecContext context)
    {
        if (value.RelativeValue == 0)
        {
            writer.WriteInt32(0);
            return;
        }

        long absolute = SaturatingAdd(value.RelativeValue, context.TimeBase);
        long bounded = Math.Clamp(absolute, int.MinValue, int.MaxValue);
        writer.WriteInt32(checked((int)bounded));
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
            return long.MaxValue;
        if (right < 0 && left < long.MinValue - right)
            return long.MinValue;
        return left + right;
    }

    private static T RequireValue<T>(Net7AuxDataProperty property)
    {
        if (property.Value is T value)
            return value;
        throw new InvalidDataException(
            $"AuxData PropertyId {property.Id} ({property.Path}) has no materialized {typeof(T).Name} value for canonical encoding.");
    }

    private static void ApplyStateTwo(Net7AuxDataProperty property, MutationJournal journal)
    {
        if (property.Kind == Net7AuxDataValueKind.Aggregate && property.State == 0)
        {
            foreach (Net7AuxDataProperty child in property.Children)
                ApplyStateTwo(child, journal);
        }
        MarkStateTwoAndPropagate(property, journal);
    }

    private static void MarkStateTwoAndPropagate(Net7AuxDataProperty property, MutationJournal journal)
    {
        // AuxDataPropertyFinalizeIncomingUpdate / StateTwoCallback set state 2
        // on the addressed property and recursively invoke the same callback on
        // each parent aggregate. State-zero deliberately does not do this.
        for (Net7AuxDataProperty? current = property; current is not null; current = current.Parent)
        {
            journal.Capture(current);
            current.State = 2;
            current.UpdateRevision++;
        }
    }

    private static void ApplyStateZero(Net7AuxDataProperty property, MutationJournal journal)
    {
        if (property.Kind == Net7AuxDataValueKind.Aggregate && property.State != 0)
        {
            foreach (Net7AuxDataProperty child in property.Children)
                ApplyStateZero(child, journal);
        }
        journal.Capture(property);
        property.State = 0;
    }

    public static void Normalize(Net7AuxDataProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        foreach (Net7AuxDataProperty child in property.Children)
            Normalize(child);
        if (property.State > 1)
            property.State = 1;
    }

    private sealed class MutationJournal
    {
        private readonly Dictionary<Net7AuxDataProperty, MaterializedSnapshot> _snapshots = new();

        public void Capture(Net7AuxDataProperty property)
        {
            if (!_snapshots.ContainsKey(property))
                _snapshots.Add(property, new MaterializedSnapshot(property.State, property.Value, property.UpdateRevision));
        }

        public void Rollback()
        {
            foreach (KeyValuePair<Net7AuxDataProperty, MaterializedSnapshot> pair in _snapshots)
            {
                pair.Key.State = pair.Value.State;
                pair.Key.Value = pair.Value.Value;
                pair.Key.UpdateRevision = pair.Value.UpdateRevision;
            }
        }
    }

    private sealed record MaterializedSnapshot(int State, object? Value, ulong UpdateRevision);

    private sealed record ResolvedOperation(Net7AuxDataProperty Property, Net7AuxDataOperationKind Kind);

    private sealed class CompressedChild
    {
        public CompressedChild(Net7AuxDataProperty property)
            => Property = property;

        public Net7AuxDataProperty Property { get; }
        public bool ValueSelected { get; private set; }
        public bool StateTwoSelected { get; private set; }
        public bool StateZeroSelected { get; private set; }
        public CompressedRecord? Nested { get; set; }

        public void Select(Net7AuxDataOperationKind operation)
        {
            switch (operation)
            {
                case Net7AuxDataOperationKind.ValueUpdate:
                    ValueSelected = true;
                    return;
                case Net7AuxDataOperationKind.StateTwo:
                    StateTwoSelected = true;
                    return;
                case Net7AuxDataOperationKind.StateZero:
                    StateZeroSelected = true;
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }
    }

    private sealed class CompressedRecord
    {
        private readonly Dictionary<int, CompressedChild> _children = new();

        public CompressedRecord(Net7AuxDataProperty aggregate)
        {
            if (aggregate.Kind != Net7AuxDataValueKind.Aggregate)
                throw new InvalidDataException($"Compressed record requires aggregate PropertyId {aggregate.Id}.");
            Aggregate = aggregate;
        }

        public Net7AuxDataProperty Aggregate { get; }
        public Net7AuxDataOperationKind? DirectOperation { get; private set; }

        public void SetDirect(Net7AuxDataOperationKind operation)
        {
            if (_children.Count != 0 || DirectOperation is not null)
                throw new InvalidDataException($"AuxData aggregate PropertyId {Aggregate.Id} has conflicting compressed operations.");
            DirectOperation = operation;
        }

        public CompressedRecord GetOrCreateNestedRoute(
            Net7AuxDataProperty child,
            Net7AuxDataOperationKind routeOperation)
        {
            if (DirectOperation is not null)
                throw new InvalidDataException($"AuxData aggregate PropertyId {Aggregate.Id} cannot mix a direct operation with child routing.");

            if (child.Kind != Net7AuxDataValueKind.Aggregate)
                throw new InvalidDataException($"AuxData routing child PropertyId {child.Id} is not aggregate.");

            if (_children.TryGetValue(child.ChildIndex, out CompressedChild? existing))
            {
                existing.Select(routeOperation);
                existing.Nested ??= new CompressedRecord(child);
                return existing.Nested;
            }

            var entry = new CompressedChild(child)
            {
                Nested = new CompressedRecord(child),
            };
            entry.Select(routeOperation);
            _children.Add(child.ChildIndex, entry);
            return entry.Nested;
        }

        public void SetChildOperation(Net7AuxDataProperty child, Net7AuxDataOperationKind operation)
        {
            if (DirectOperation is not null)
                throw new InvalidDataException($"AuxData aggregate PropertyId {Aggregate.Id} cannot mix a direct operation with child operations.");
            if (_children.ContainsKey(child.ChildIndex))
                throw new InvalidDataException($"AuxData child {child.ChildIndex} would require overlapping compressed operation maps.");
            var entry = new CompressedChild(child);
            entry.Select(operation);
            _children.Add(child.ChildIndex, entry);
        }

        public int GetStructuralSize()
        {
            if (DirectOperation is not null)
                return 1;
            if (_children.Count == 0)
                throw new InvalidDataException($"AuxData compressed aggregate PropertyId {Aggregate.Id} contains no operation.");

            bool hasValue = _children.Values.Any(child => child.ValueSelected);
            bool hasStateTwo = _children.Values.Any(child => child.StateTwoSelected);
            bool hasStateZero = _children.Values.Any(child => child.StateZeroSelected);
            int mapCount = (hasValue ? 1 : 0) + (hasStateTwo ? 1 : 0) + (hasStateZero ? 1 : 0);
            int bitCount = checked(4 + Aggregate.Children.Count * mapCount);
            int size = (bitCount + 7) / 8;
            foreach (CompressedChild child in _children.Values)
            {
                if (child.Nested is not null)
                    size = checked(size + child.Nested.GetStructuralSize());
            }
            return size;
        }

        public void Encode(Writer writer, Net7AuxDataCodecContext context)
        {
            if (DirectOperation is Net7AuxDataOperationKind direct)
            {
                writer.WriteByte(direct switch
                {
                    Net7AuxDataOperationKind.ValueUpdate => 0x03,
                    Net7AuxDataOperationKind.StateTwo => 0x05,
                    Net7AuxDataOperationKind.StateZero => 0x09,
                    _ => throw new ArgumentOutOfRangeException(nameof(direct)),
                });

                if (direct == Net7AuxDataOperationKind.ValueUpdate)
                    EncodeValue(Aggregate, writer, context);
                return;
            }

            if (_children.Count == 0)
                throw new InvalidDataException($"AuxData compressed aggregate PropertyId {Aggregate.Id} contains no operation.");

            bool hasValue = _children.Values.Any(child => child.ValueSelected);
            bool hasStateTwo = _children.Values.Any(child => child.StateTwoSelected);
            bool hasStateZero = _children.Values.Any(child => child.StateZeroSelected);
            int header = (hasValue ? 0x02 : 0) | (hasStateTwo ? 0x04 : 0) | (hasStateZero ? 0x08 : 0);
            int mapCount = (hasValue ? 1 : 0) + (hasStateTwo ? 1 : 0) + (hasStateZero ? 1 : 0);
            int bitCount = checked(4 + Aggregate.Children.Count * mapCount);
            byte[] maps = new byte[(bitCount + 7) / 8];
            maps[0] = checked((byte)header);

            int mapBase = 4;
            int valueBase = -1;
            int stateTwoBase = -1;
            int stateZeroBase = -1;
            if (hasValue)
            {
                valueBase = mapBase;
                mapBase += Aggregate.Children.Count;
            }
            if (hasStateTwo)
            {
                stateTwoBase = mapBase;
                mapBase += Aggregate.Children.Count;
            }
            if (hasStateZero)
                stateZeroBase = mapBase;

            foreach (KeyValuePair<int, CompressedChild> pair in _children)
            {
                int childIndex = pair.Key;
                CompressedChild child = pair.Value;
                if (child.ValueSelected)
                {
                    if (valueBase < 0)
                        throw new InvalidDataException("AuxData compressed value-map base was not initialized.");
                    SetBit(maps, valueBase + childIndex);
                }
                if (child.StateTwoSelected)
                {
                    if (stateTwoBase < 0)
                        throw new InvalidDataException("AuxData compressed state-two-map base was not initialized.");
                    SetBit(maps, stateTwoBase + childIndex);
                }
                if (child.StateZeroSelected)
                {
                    if (stateZeroBase < 0)
                        throw new InvalidDataException("AuxData compressed state-zero-map base was not initialized.");
                    SetBit(maps, stateZeroBase + childIndex);
                }
            }

            writer.WriteBytes(maps);
            for (int childIndex = 0; childIndex < Aggregate.Children.Count; childIndex++)
            {
                if (!_children.TryGetValue(childIndex, out CompressedChild? child))
                    continue;

                if (child.Nested is not null)
                    child.Nested.Encode(writer, context);
                else if (child.ValueSelected)
                    EncodeValue(child.Property, writer, context);
            }
        }
    }

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();

        public void WriteByte(byte value) => _stream.WriteByte(value);

        public void WriteBytes(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

        public void WriteFixedBytes(byte[] bytes, int expectedLength, int propertyId)
        {
            if (bytes.Length != expectedLength)
            {
                throw new InvalidDataException(
                    $"AuxData PropertyId {propertyId} requires exactly {expectedLength} encoded byte(s), got {bytes.Length}.");
            }
            _stream.Write(bytes);
        }

        public void WriteLengthPrefixedBytes(byte[] bytes)
        {
            if (bytes.Length > ushort.MaxValue)
                throw new InvalidDataException($"AuxData string length {bytes.Length} exceeds u16 wire capacity.");
            WriteUInt16(checked((ushort)bytes.Length));
            _stream.Write(bytes);
        }

        public void WriteUInt16(ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void WriteUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void WriteInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void WriteUInt64(ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        public byte[] ToArray() => _stream.ToArray();
    }

    private sealed class Cursor
    {
        private readonly byte[] _bytes;
        private int _offset;

        public Cursor(byte[] bytes) => _bytes = bytes;
        public bool End => _offset == _bytes.Length;
        public int Length => _bytes.Length;
        public int Offset => _offset;
        public int Remaining => _bytes.Length - _offset;
        public bool ExhaustedRootScalar { get; set; }
        public int? FailedPropertyId { get; set; }
        public int? EngineSuffixOffset { get; init; }
        public int EngineSuffixValues { get; set; }
        public bool TruncatedValue { get; set; }
        public bool ReadTruncated { get; private set; }
        public bool UndefinedPropertyRead { get; private set; }

        public Net7AuxDataProperty ReadProperty(Net7AuxDataSchema schema)
        {
            int id = unchecked((int)ReadUInt32());
            UndefinedPropertyRead = (uint)id >= (uint)schema.Properties.Count;
            return schema.Resolve(id);
        }
        public int CompletedValueCount { get; set; }

        public void Seek(int offset)
        {
            if ((uint)offset > (uint)_bytes.Length)
                throw new InvalidDataException($"AuxData cursor seek offset {offset} is outside delta length {_bytes.Length}.");
            _offset = offset;
        }

        public string HexWindow(int offset, int count)
        {
            if (offset < 0 || offset > _bytes.Length)
                return "<outside>";
            int length = Math.Min(Math.Max(count, 0), _bytes.Length - offset);
            if (length == 0)
                return "<eof>";
            return Convert.ToHexString(_bytes.AsSpan(offset, length));
        }

        public byte PeekByte()
        {
            Require(1);
            return _bytes[_offset];
        }

        public byte ReadByte()
        {
            Require(1);
            return _bytes[_offset++];
        }

        public byte[] ReadBytes(int count)
        {
            Require(count);
            byte[] result = _bytes.AsSpan(_offset, count).ToArray();
            _offset += count;
            return result;
        }

        public byte[] ReadLengthPrefixedBytes()
        {
            ushort length = ReadUInt16();
            return ReadBytes(length);
        }

        public ushort ReadUInt16()
        {
            Require(2);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(_offset, 2));
            _offset += 2;
            return value;
        }

        public uint ReadUInt32()
        {
            Require(4);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(_offset, 4));
            _offset += 4;
            return value;
        }

        public int ReadInt32()
        {
            Require(4);
            int value = BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(_offset, 4));
            _offset += 4;
            return value;
        }

        public ulong ReadUInt64()
        {
            Require(8);
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(_offset, 8));
            _offset += 8;
            return value;
        }

        public float ReadSingle()
            => BitConverter.Int32BitsToSingle(ReadInt32());

        private void Require(int count)
        {
            if (count < 0 || count > Remaining)
            {
                ReadTruncated = count >= 0;
                throw new InvalidDataException($"AuxData delta is truncated; need {count} byte(s), have {Remaining}.");
            }
        }
    }
}
