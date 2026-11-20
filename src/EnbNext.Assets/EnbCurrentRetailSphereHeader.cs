using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace EnbNext.Assets;

// Inspection only: child 1 of 0x741, 0xA4 bytes read to definition +8
// by 0090DFE0. Neutral parameter names remain until their draw use is traced.
public sealed record EnbCurrentRetailSphereHeader(uint RawVersion,uint Flags,string Name,
    Vector3 Center,Vector3 AuthoredExtent,float Duration,Vector3 Color,float Opacity,
    Vector3 Parameter54,float[] Parameters60,string Texture,byte[] ShaderBytes)
{
    public static EnbCurrentRetailSphereHeader Read(ReadOnlySpan<byte> payload)
    {
        if(payload.Length<0xa4)throw new InvalidDataException("Sphere header is shorter than native 0xA4-byte read");
        return new(U(payload,0),U(payload,4),Text(payload.Slice(8,32)),V3(payload,0x28),V3(payload,0x34),
            F(payload,0x40),V3(payload,0x44),F(payload,0x50),V3(payload,0x54),
            [F(payload,0x60),F(payload,0x64),F(payload,0x68),F(payload,0x6c),F(payload,0x70)],
            Text(payload.Slice(0x74,32)),payload.Slice(0x94,16).ToArray());
    }
    private static uint U(ReadOnlySpan<byte> p,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(at,4));
    private static float F(ReadOnlySpan<byte> p,int at)=>BitConverter.Int32BitsToSingle(unchecked((int)U(p,at)));
    private static Vector3 V3(ReadOnlySpan<byte> p,int at)=>new(F(p,at),F(p,at+4),F(p,at+8));
    private static string Text(ReadOnlySpan<byte> p)
    {
        int end=p.IndexOf((byte)0);return Encoding.ASCII.GetString(end<0?p:p[..end]);
    }
}
