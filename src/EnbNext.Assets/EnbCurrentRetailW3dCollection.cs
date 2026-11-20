using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace EnbNext.Assets;

public sealed record EnbCurrentRetailW3dCollectionDefinition(uint Version, string Name,
    uint ChildCapacityHint, IReadOnlyList<string> ChildNames,
    IReadOnlyList<EnbCurrentRetailW3dCollectionNamedMatrix> NamedMatrices,
    IReadOnlyList<Vector3>? AuxiliaryVectors);

/// <summary>The copied twelve raw words at model entry+4..30.00991AB0
/// transposes source column groups into three native four-word rows. No float
/// arithmetic occurs in that copy; signaling NaNs and signed zeros survive.</summary>
public sealed record EnbCurrentRetailW3dCollectionNamedMatrix(string Name, IReadOnlyList<uint> RowWords);

/// <summary>Registered0420 loader00991AB0 for complete bounded source storage.
/// Header421's count reserves the child pointer array; actual422 records append
/// independently.423 named matrices and the last440 vector owner are retained
/// separately. Unknown children close/skip. Native partial reads into incoming
/// allocation/stack storage remain outside this complete-source decoder.</summary>
public static class EnbCurrentRetailW3dCollectionDecoder
{
    public static bool TryDecode(ReadOnlySpan<byte> bytes,
        out EnbCurrentRetailW3dCollectionDefinition definition, out string? diagnostic)
    {
        definition = default!; diagnostic = null;
        if (!ReadChild(bytes, 0, bytes.Length, out uint type, out int start, out int end) ||
            type != 0x420 || end != bytes.Length)
            return Fail("resource is not one complete root0420", out diagnostic);
        int limit = end;
        if (!ReadChild(bytes, start, limit, out type, out start, out end) || type != 0x421 || end - start < 32)
            return Fail("first child must supply the complete32-byte0421 prefix", out diagnostic);
        var header = bytes.Slice(start, 32);
        // strncpy copies16 bytes, independently of child capacity. A full
        // unterminated name would consume adjacent native object storage.
        if (!ReadName(header.Slice(4, 16), out string name))
            return Fail("0421 name extends beyond its bounded field", out diagnostic);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        List<string> children = [];
        List<EnbCurrentRetailW3dCollectionNamedMatrix> matrices = [];
        IReadOnlyList<Vector3>? vectors = null;
        int offset = end;
        while (limit - offset >= 8)
        {
            if (!ReadChild(bytes, offset, limit, out type, out start, out end))
                return Fail("child exceeds the bounded source storage", out diagnostic);
            var payload = bytes.Slice(start, end - start);
            switch (type)
            {
                case 0x422:
                    if (!ReadName(payload, out string child))
                        return Fail("0422 name extends beyond its allocation", out diagnostic);
                    children.Add(child);
                    break;
                case 0x423:
                    if (payload.Length < 56)
                        return Fail("0423 prefix requires incoming stack bytes absent from this complete source", out diagnostic);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(payload[52..]);
                    if (length < 0 || length > payload.Length - 56)
                        return Fail("0423 name requires storage absent from this complete source", out diagnostic);
                    var stringBytes = payload.Slice(56, length);
                    int nul = stringBytes.IndexOf((byte)0);
                    string matrixName = Encoding.Latin1.GetString(nul < 0 ? stringBytes : stringBytes[..nul]);
                    uint[] words = new uint[12];
                    for (int row = 0; row < 3; row++)
                        for (int column = 0; column < 4; column++)
                            words[row * 4 + column] = BinaryPrimitives.ReadUInt32LittleEndian(payload[(4 + column * 12 + row * 4)..]);
                    matrices.Add(new(matrixName, Array.AsReadOnly(words)));
                    break;
                case 0x440:
                    Vector3[] values = new Vector3[payload.Length / 12];
                    for (int index = 0; index < values.Length; index++)
                    {
                        int p = index * 12;
                        values[index] = new(Quiet(BinaryPrimitives.ReadUInt32LittleEndian(payload[p..])),
                            Quiet(BinaryPrimitives.ReadUInt32LittleEndian(payload[(p + 4)..])),
                            Quiet(BinaryPrimitives.ReadUInt32LittleEndian(payload[(p + 8)..])));
                    }
                    vectors = Array.AsReadOnly(values);
                    break;
            }
            offset = end;
        }
        definition = new(version, name, capacity, children.AsReadOnly(), matrices.AsReadOnly(), vectors);
        return true;
    }

    private static bool ReadChild(ReadOnlySpan<byte> bytes, int offset, int limit, out uint type, out int start, out int end)
    {
        type = 0; start = end = 0;
        if (offset < 0 || limit - offset < 8) return false;
        type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]) & 0x7fffffff;
        start = offset + 8;
        if (length > (uint)(limit - start)) return false;
        end = start + (int)length;
        return true;
    }

    private static bool ReadName(ReadOnlySpan<byte> field, out string name)
    {
        int nul = field.IndexOf((byte)0);
        name = nul < 0 ? string.Empty : Encoding.Latin1.GetString(field[..nul]);
        return nul >= 0;
    }

    private static float Quiet(uint bits)
    {
        if ((bits & 0x7f800000) == 0x7f800000 && (bits & 0x007fffff) != 0) bits |= 0x00400000;
        return BitConverter.UInt32BitsToSingle(bits);
    }

    private static bool Fail(string reason, out string? diagnostic) { diagnostic = reason; return false; }
}
