using System.Buffers.Binary;
using System.Numerics;

namespace EnbNext.Assets;

/// <summary>Current retail 0046EB60 terrain PLD cache. No installed data is modified.</summary>
public sealed record EnbRetailTerrainPrelight(byte[] NormalIndices, ushort[] NegativeHorizons,
    ushort[] PositiveHorizons, byte[] AmbientRed, byte[] AmbientGreen, byte[] AmbientBlue,
    Vector3[] Normals)
{
    /// <summary>0046ECF0's header and seven ordered fwrite payloads.</summary>
    public byte[] Encode()
    {
        int count=NormalIndices.Length;
        if(count<1||NegativeHorizons.Length!=count||PositiveHorizons.Length!=count||
            AmbientRed.Length!=count||AmbientGreen.Length!=count||AmbientBlue.Length!=count||Normals.Length!=256)
            throw new InvalidDataException("Terrain prelight planes have inconsistent dimensions.");
        var bytes=new byte[checked(4+count*8+256*12)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes,(uint)count);
        int offset=4;
        NormalIndices.CopyTo(bytes,offset);offset+=count;
        foreach(ushort value in NegativeHorizons){BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset),value);offset+=2;}
        foreach(ushort value in PositiveHorizons){BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset),value);offset+=2;}
        AmbientRed.CopyTo(bytes,offset);offset+=count;
        AmbientGreen.CopyTo(bytes,offset);offset+=count;
        AmbientBlue.CopyTo(bytes,offset);offset+=count;
        foreach(var normal in Normals)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset),normal.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset+4),normal.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset+8),normal.Z);offset+=12;
        }
        return bytes;
    }

    public static EnbRetailTerrainPrelight Decode(ReadOnlySpan<byte> bytes, int sampleCount)
    {
        //0046EB92..0046EC87 checks the header and each fread item count.
        //It neither requires EOF nor validates the loaded normal words.
        if (sampleCount < 1 || bytes.Length < checked(4 + sampleCount * 8 + 256 * 12) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != sampleCount)
            throw new InvalidDataException("Terrain prelight cache dimensions do not match the height field.");
        int offset = 4;
        byte[] indices = bytes.Slice(offset, sampleCount).ToArray(); offset += sampleCount;
        var negative = new ushort[sampleCount];
        var positive = new ushort[sampleCount];
        for (int i = 0; i < sampleCount; i++, offset += 2)
            negative[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
        for (int i = 0; i < sampleCount; i++, offset += 2)
            positive[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
        byte[] red = bytes.Slice(offset, sampleCount).ToArray(); offset += sampleCount;
        byte[] green = bytes.Slice(offset, sampleCount).ToArray(); offset += sampleCount;
        byte[] blue = bytes.Slice(offset, sampleCount).ToArray(); offset += sampleCount;
        var normals = new Vector3[256];
        for (int i = 0; i < normals.Length; i++, offset += 12)
        {
            normals[i] = new(BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 4)..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 8)..]));
        }
        return new(indices, negative, positive, red, green, blue, normals);
    }
}
