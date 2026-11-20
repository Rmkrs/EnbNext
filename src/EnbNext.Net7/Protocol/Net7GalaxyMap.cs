using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Net7.Protocol;

public sealed record Net7GalaxyMapData(
    byte[] AuxiliaryBlob,
    IReadOnlyList<Net7GalaxySystemRecord> Systems,
    IReadOnlyList<Net7GalaxySectorRecord> Sectors,
    IReadOnlyList<Net7GalaxyGateRecord> Gates)
{
    public uint? RecordCount { get; init; }
    internal IReadOnlyDictionary<uint, (byte Kind, uint Hash)>? AllData { get; init; }
    public Net7GalaxyMapOrder? SystemOrder { get; init; }
    public Net7GalaxyMapOrder? SectorOrder { get; init; }
    public Net7GalaxyMapOrder? GateOrder { get; init; }

    // 007A5400 -> 004A4AA0: command 3 uses the record index, LSB first.
    public bool IsKnown(Net7GalaxySectorRecord sector) =>
        sector.Field0 / 8 < AuxiliaryBlob.Length &&
        (AuxiliaryBlob[sector.Field0 / 8] & (1 << (int)(sector.Field0 & 7))) != 0;
}

public sealed record Net7GalaxySystemRecord(
    uint Field0,
    uint SystemHashId,
    string Name,
    float[] VectorA,
    float[] VectorB,
    float Scalar,
    uint TailValue);

public sealed record Net7GalaxySectorRecord(
    uint CommandType,
    uint Field0,
    uint SectorHashId,
    uint Field2,
    string Name,
    uint Subtype,
    float[] Vector);

public sealed record Net7GalaxyGateRecord(
    uint GateHashId,
    uint SectorReferenceA,
    uint SectorReferenceB);

/// <summary>
/// Native decoder for the pinned current GalaxyMap.dat semantics.
/// The retail 0x0097 envelopes are treated as a local compatibility encoding,
/// not as an internal EnbNext architecture boundary.
/// </summary>
public static class Net7GalaxyMapCodec
{
    public const int CurrentFileSize = 12647;
    public const int CurrentFrameCount = 305;
    public const string CurrentSha256 = "3F2441D7DE271ABF0F3E9D0732E0BE7BEBB4E1F35760A69233219D4F21E98B42";

    /// <summary>Compatibility name; admission validates the format, not a release fingerprint.</summary>
    public static Net7GalaxyMapData DecodePinnedCurrentAsset(ReadOnlySpan<byte> file) => DecodeCurrentGeometry(file);

    /// <summary>Structural decoder used by deterministic synthetic conformance fixtures.</summary>
    public static Net7GalaxyMapData DecodeCurrentGeometry(ReadOnlySpan<byte> file)
    {
        var model = new Net7GalaxyMapData([], [], [], []);
        int frameCount = 0;
        int offset = 0;

        while (offset < file.Length)
        {
            if (file.Length - offset < 12)
                throw new InvalidDataException($"GalaxyMap frame at {offset} is truncated before its 12-byte header.");

            ushort frameLength = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(offset, 2));
            ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(offset + 2, 2));
            uint command = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(offset + 4, 4));
            uint blobLength32 = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(offset + 8, 4));
            if (blobLength32 > int.MaxValue)
                throw new InvalidDataException("GalaxyMap blob length exceeds supported process bounds.");
            int blobLength = (int)blobLength32;

            if (opcode != 0x0097)
                throw new InvalidDataException($"GalaxyMap frame {frameCount} uses opcode 0x{opcode:X4}; expected 0x0097.");
            if (frameLength < 12)
                throw new InvalidDataException("GalaxyMap frame is shorter than its header.");
            if (offset + frameLength > file.Length)
                throw new InvalidDataException($"GalaxyMap frame {frameCount} extends beyond EOF.");

            // Proxy 0040F270 injects these frames; retail 0073AC60 uses the same mutations.
            model = Net7GalaxyMapUpdates.Apply(model, file.Slice(offset + 4, frameLength - 4));

            frameCount++;
            offset += frameLength;
        }

        return model;
    }

    internal static Net7GalaxySystemRecord ParseSystem(ReadOnlySpan<byte> blob)
    {
        var reader = new BlobReader(blob);
        uint field0 = reader.ReadUInt32();
        uint hash = reader.ReadUInt32();
        string name = reader.ReadCString();
        float[] vectorA = reader.ReadFloat3();
        float[] vectorB = reader.ReadFloat3();
        float scalar = BitConverter.UInt32BitsToSingle(reader.ReadPartialUInt32(0x3F800000));
        uint tail = reader.ReadPartialUInt32(3); // 00837C70 / 008381B0: initialized dword, partial copy.
        return new Net7GalaxySystemRecord(field0, hash, name, vectorA, vectorB, scalar, tail);
    }

    internal static Net7GalaxySectorRecord ParseSector(uint command, ReadOnlySpan<byte> blob)
    {
        var reader = new BlobReader(blob);
        uint field0 = reader.ReadUInt32();
        uint hash = reader.ReadUInt32();
        uint field2 = reader.ReadUInt32();
        string name = reader.ReadCString();
        uint subtype = reader.ReadUInt32();
        float[] vector = reader.ReadFloat3();
        return new Net7GalaxySectorRecord(command, field0, hash, field2, name, subtype, vector);
    }

    internal static Net7GalaxyGateRecord ParseGate(ReadOnlySpan<byte> blob)
    {
        var reader = new BlobReader(blob);
        // 00838970 initializes all three fields; 00838B80 keeps unread bytes.
        return new(reader.ReadPartialUInt32(uint.MaxValue), reader.ReadPartialUInt32(0), reader.ReadPartialUInt32(0));
    }

    private ref struct BlobReader
    {
        private ReadOnlySpan<byte> _data;
        private int _offset;

        public BlobReader(ReadOnlySpan<byte> data) => _data = data;

        public uint ReadUInt32()
        {
            Require(4);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_offset, 4));
            _offset += 4;
            return value;
        }

        public float ReadSingle() => BitConverter.Int32BitsToSingle(unchecked((int)ReadUInt32()));

        public float[] ReadFloat3() => [ReadSingle(), ReadSingle(), ReadSingle()];

        public uint ReadPartialUInt32(uint initial)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, initial);
            int count = Math.Min(4, _data.Length - _offset);
            _data.Slice(_offset, count).CopyTo(bytes);
            _offset += count;
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        public string ReadCString()
        {
            ReadOnlySpan<byte> remaining = _data[_offset..];
            int nul = remaining.IndexOf((byte)0);
            if (nul < 0)
                throw new InvalidDataException("GalaxyMap record string is missing its NUL terminator.");
            string value = Encoding.Latin1.GetString(remaining[..nul]);
            _offset += nul + 1;
            return value;
        }

        private void Require(int count)
        {
            if (_offset > _data.Length - count)
                throw new InvalidDataException("GalaxyMap record is truncated.");
        }
    }
}
