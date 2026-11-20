using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Assets;

public sealed record EnbW3dTimeMappedSource(string Name, IReadOnlyList<EnbW3dTimeKey> Keys);
public readonly record struct EnbW3dTimeKey(uint Time, int SourceFrame);

/// <summary>00987CC0: first-child header, ordered source children and bytewise
/// pivot-map writes. Child IDs inside a source are deliberately not gates.</summary>
public sealed record EnbW3dTimeMappedAnimation(float FrameRate, uint SourceCount,
    IReadOnlyList<EnbW3dTimeMappedSource> Sources, IReadOnlyList<byte[]> PivotWrites)
{
    internal static bool TryDecode(ReadOnlySpan<byte> bytes, out EnbW3dHierarchyAnimation animation)
    {
        animation = default!;
        int offset = 0;
        if (!ReadChild(bytes, ref offset, out _, out var header) || header.Length < 48) return false;
        var sources = new List<EnbW3dTimeMappedSource>();
        var maps = new List<byte[]>();
        while (offset < bytes.Length)
        {
            if (!ReadChild(bytes, ref offset, out uint type, out var child)) return false;
            if (type == 0x2c5) maps.Add(child.ToArray());
            if (type != 0x2c2) continue;
            int inner = 0;
            if (!ReadChild(child, ref inner, out _, out var name) || name.IndexOf((byte)0) < 0 ||
                !ReadChild(child, ref inner, out _, out var keys)) return false;
            var track = new EnbW3dTimeKey[keys.Length / 8];
            for (int i = 0; i < track.Length; i++)
                track[i] = new(BinaryPrimitives.ReadUInt32LittleEndian(keys[(i * 8)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(keys[(i * 8 + 4)..]));
            sources.Add(new(Name(name), track));
        }
        var definition = new EnbW3dTimeMappedAnimation(BinaryPrimitives.ReadSingleLittleEndian(header[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[44..]), sources, maps);
        animation = new(BinaryPrimitives.ReadUInt32LittleEndian(header), Name(header.Slice(4,16)),
            Name(header.Slice(20,16)), BinaryPrimitives.ReadUInt32LittleEndian(header[36..]), 0, [], [])
            { Family = EnbW3dHAnimFamily.Adaptive02C0, TimeMapped = definition };
        return true;
    }

    private static string Name(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private static bool ReadChild(ReadOnlySpan<byte> bytes, ref int offset, out uint type, out ReadOnlySpan<byte> payload)
    {
        type = 0; payload = default;
        if (bytes.Length - offset < 8) return false;
        type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]) & 0x7fffffff;
        if (length > bytes.Length - offset - 8) return false;
        payload = bytes.Slice(offset + 8, (int)length);
        offset += 8 + (int)length;
        return true;
    }
}
