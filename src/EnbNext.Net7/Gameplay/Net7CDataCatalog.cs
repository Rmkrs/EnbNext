using System.Buffers.Binary;

namespace EnbNext.Net7.Gameplay;

/// <summary>Safe native decoder for the cdata.dat format.</summary>
public sealed class Net7CDataCatalog
{
    public const int CurrentFileSize = 2_423_401;
    public const string CurrentSha256 = "1188b9e0e5e673fd4f82261a887528e62e2da7b5365b40807c253ae7f8bdeaa7";

    private readonly byte[] _encodedData;
    private readonly Dictionary<uint, (int Offset, int Length)> _records;

    private Net7CDataCatalog(byte[] encodedData, Dictionary<uint, (int Offset, int Length)> records)
    {
        _encodedData = encodedData;
        _records = records;
    }

    public int RecordCount => _records.Count;
    /// <summary>Every record ID in the index (the Next item catalogue reads them all).</summary>
    public IEnumerable<uint> RecordIds => _records.Keys;

    public static Net7CDataCatalog Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("CData path is required.", nameof(path));
        return Decode(File.ReadAllBytes(EnbNext.Installation.EnbPathCase.Resolve(path)));
    }

    /// <summary>Compatibility name; only file-format invariants are required.</summary>
    public static Net7CDataCatalog DecodePinnedCurrentAsset(ReadOnlySpan<byte> file) => Decode(file);

    public static Net7CDataCatalog Decode(ReadOnlySpan<byte> file)
    {
        if (file.Length < 8)
            throw new InvalidDataException("cdata.dat is shorter than its two encoded header fields.");

        Span<byte> decoded4 = stackalloc byte[4];
        DecodeBlock(file[..4], decoded4);
        uint dataLengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(decoded4);
        if (dataLengthRaw > int.MaxValue)
            throw new InvalidDataException("cdata.dat data region exceeds supported process memory size.");
        int dataLength = checked((int)dataLengthRaw);
        int countOffset = checked(4 + dataLength);
        if (countOffset + 4 > file.Length)
            throw new InvalidDataException("cdata.dat encoded data length runs past EOF.");

        DecodeBlock(file.Slice(countOffset, 4), decoded4);
        uint recordCountRaw = BinaryPrimitives.ReadUInt32LittleEndian(decoded4);
        if (recordCountRaw > int.MaxValue)
            throw new InvalidDataException("cdata.dat record count exceeds supported process memory size.");
        int recordCount = checked((int)recordCountRaw);
        int indexOffset = checked(countOffset + 4);
        long indexEnd = (long)indexOffset + (long)recordCount * 8;
        if (indexEnd > file.Length)
            throw new InvalidDataException(
                $"cdata.dat index runs past EOF: index end {indexEnd}, file {file.Length}.");
        if (recordCount == 0 && dataLength != 0)
            throw new InvalidDataException("cdata.dat has encoded record data but no index entries.");

        byte[] encodedData = file.Slice(4, dataLength).ToArray();
        var offsets = new List<(uint Id, int Offset)>(recordCount);
        Span<byte> decoded8 = stackalloc byte[8];
        for (int i = 0; i < recordCount; i++)
        {
            DecodeBlock(file.Slice(indexOffset + i * 8, 8), decoded8);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(decoded8[..4]);
            uint offsetRaw = BinaryPrimitives.ReadUInt32LittleEndian(decoded8[4..]);
            if (offsetRaw > dataLengthRaw)
                throw new InvalidDataException(
                    $"cdata.dat record {id} offset {offsetRaw} does not identify a byte inside data length {dataLengthRaw}.");

            offsets.Add((id, checked((int)offsetRaw)));
        }

        var records = new Dictionary<uint, (int Offset, int Length)>(recordCount);
        uint previousId = 0;
        int previousOffset = 0;
        foreach ((uint id, int offset) in offsets)
        {
            // Net7Proxy 00404ECC writes the pointer before 00404EE2 writes
            // the preceding positive ID's length. These are independent writes,
            // including when an ID repeats or ID zero appears in the index.
            records.TryGetValue(id, out var current);
            records[id] = (offset, current.Length);
            if (unchecked((int)previousId) > 0)
            {
                var previous = records[previousId];
                records[previousId] = (previous.Offset, offset - previousOffset);
                previousOffset = offset;
            }
            previousId = id;
        }
        if (offsets.Count != 0)
        {
            var last = records[previousId];
            records[previousId] = (last.Offset, dataLength - offsets[^1].Offset);
        }

        return new Net7CDataCatalog(encodedData, records);
    }

    public bool TryGetRecord(uint recordId, out Net7CDataRecord? record)
    {
        if (!_records.TryGetValue(recordId, out (int Offset, int Length) entry) || entry.Length <= 0)
        {
            record = null;
            return false;
        }

        byte[] payload = new byte[entry.Length];
        DecodeBlock(_encodedData.AsSpan(entry.Offset, entry.Length), payload);
        record = new Net7CDataRecord(recordId, payload);
        return true;
    }

    public static void DecodeBlock(ReadOnlySpan<byte> encoded, Span<byte> decoded)
    {
        if (decoded.Length != encoded.Length)
            throw new ArgumentException("CData decode destination length must equal encoded length.", nameof(decoded));
        for (int i = 0; i < encoded.Length; i++)
            decoded[i] = unchecked((byte)(encoded[encoded.Length - 1 - i] + i + 0x6A));
    }
}
