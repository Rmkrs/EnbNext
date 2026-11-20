using System.Buffers.Binary;

internal static class AnimationCurveFixture
{
    public const int LargeCount = 1_000_001;

    public static byte[] Scalar(ushort pivot, float first, float second, int count)
    {
        var payload = new byte[8 + count * 8 + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)count);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), pivot);
        payload[6] = 1;
        for (int index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8 + index * 8), (uint)index);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(12 + index * 8), index == 0 ? first : second);
        }
        payload.AsSpan(payload.Length - 4).Fill(255);
        return payload;
    }

    public static byte[] Visibility(ushort pivot, int count)
    {
        var payload = new byte[8 + count * 4 + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)count);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), pivot);
        for (int index = 0; index < count; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8 + index * 4),
                (uint)index | (index + 1 < count ? 0x80000000u : 0));
        payload.AsSpan(payload.Length - 4).Fill(255);
        return payload;
    }

    public static byte[] ExpandClassicFixture(byte[] source)
    {
        using var result = new MemoryStream();
        using var output = new BinaryWriter(result);
        for (int root = 0; root < source.Length;)
        {
            int length = (int)(BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(root + 4)) & 0x7fffffff);
            if (BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(root)) != 0x200)
                throw new InvalidDataException("Expected the classic two-key fixture");
            using var body = new MemoryStream();
            using var writer = new BinaryWriter(body);
            Chunk(writer, 0x281, source.AsSpan(root + 16, 44).ToArray());
            for (int offset = root + 60; offset < root + 8 + length; offset += 28)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset)) != 0x202 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset + 4)) != 20 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset + 8)) != 0x10000 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset + 12)) != 1)
                    throw new InvalidDataException("Expected a two-key X translation");
                ushort pivot = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset + 16));
                Chunk(writer, 0x282, Scalar(pivot,
                    BinaryPrimitives.ReadSingleLittleEndian(source.AsSpan(offset + 20)),
                    BinaryPrimitives.ReadSingleLittleEndian(source.AsSpan(offset + 24)), LargeCount));
                Chunk(writer, 0x283, Visibility(pivot, LargeCount));
            }
            Chunk(output, 0x280, body.ToArray());
            root += 8 + length;
        }
        return result.ToArray();
    }

    private static void Chunk(BinaryWriter writer, uint type, byte[] payload)
    {
        writer.Write(type); writer.Write((uint)payload.Length); writer.Write(payload);
    }
}
