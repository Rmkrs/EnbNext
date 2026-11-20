using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace EnbNext.Assets;

// Inspection of child 1 of resource 0x742, not a complete resource decoder.
// 00912F50 reads 0xA8 bytes to definition +8; 00913180 creates the render object.
public sealed record EnbCurrentRetailRingHeader(
    uint RawVersion, uint Flags, string Name, Vector3 Center, Vector3 AuthoredExtent,
    float Duration, Vector3 Color, float Opacity, Vector2 FirstRadiusMultiplier,
    Vector2 SecondRadiusMultiplier, Vector2 FirstRadii, Vector2 SecondRadii,
    string Texture, byte[] ShaderBytes, int Repeats)
{
    public static EnbCurrentRetailRingHeader Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 0xa8)
            throw new InvalidDataException("Ring header is shorter than the native 0xA8-byte read");
        return new(U(payload,0), U(payload,4), Text(payload.Slice(8,32)),
            V3(payload,0x28), V3(payload,0x34), F(payload,0x40), V3(payload,0x44),
            F(payload,0x50), V2(payload,0x54), V2(payload,0x5c), V2(payload,0x64),
            V2(payload,0x6c), Text(payload.Slice(0x74,32)), payload.Slice(0x94,16).ToArray(),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(0xa4,4)));
    }

    private static uint U(ReadOnlySpan<byte> p,int at) => BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(at,4));
    private static float F(ReadOnlySpan<byte> p,int at) => BitConverter.Int32BitsToSingle(unchecked((int)U(p,at)));
    private static Vector2 V2(ReadOnlySpan<byte> p,int at) => new(F(p,at),F(p,at+4));
    private static Vector3 V3(ReadOnlySpan<byte> p,int at) => new(F(p,at),F(p,at+4),F(p,at+8));
    private static string Text(ReadOnlySpan<byte> p)
    {
        int end=p.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end<0?p:p[..end]);
    }
}
