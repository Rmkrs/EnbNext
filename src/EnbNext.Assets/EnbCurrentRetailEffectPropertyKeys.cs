using System.Buffers.Binary;

namespace EnbNext.Assets;

public sealed record EnbCurrentRetailEffectPropertyKey(float[] Value, float Time);

// Native property readers 0090FF20 (scalar), 00913DE0 (float2), 0090F9A0 (float3), 0090F470 (five values).
// Inspection only: this does not validate an animation for production admission.
public static class EnbCurrentRetailEffectPropertyKeys
{
    public static IReadOnlyList<EnbCurrentRetailEffectPropertyKey> Read(ReadOnlySpan<byte> payload, int components)
    {
        if(components is not (1 or 2 or 3 or 5))throw new ArgumentOutOfRangeException(nameof(components));
        var keys=new List<EnbCurrentRetailEffectPropertyKey>();
        for(int at=0;at<payload.Length;)
        {
            if(payload.Length-at<8)throw new InvalidDataException("Truncated property chunk header");
            uint id=BinaryPrimitives.ReadUInt32LittleEndian(payload[at..]);
            uint size=BinaryPrimitives.ReadUInt32LittleEndian(payload[(at+4)..])&0x7fffffffu;
            long end=at+8L+size;
            if(end>payload.Length)throw new InvalidDataException("Truncated property chunk");
            if(id==0x03150809)
            {
                for(int micro=at+8;micro<end;)
                {
                    if(end-micro<2)throw new InvalidDataException("Truncated property microchunk header");
                    int type=payload[micro],length=payload[micro+1],start=micro+2;
                    if(start+length>end)throw new InvalidDataException("Truncated property microchunk");
                    if(type==1)
                    {
                        if(length<(components+1)*4)throw new InvalidDataException("Short property key");
                        var value=new float[components];
                        for(int i=0;i<components;i++)value[i]=BinaryPrimitives.ReadSingleLittleEndian(payload[(start+i*4)..]);
                        keys.Add(new(value,BinaryPrimitives.ReadSingleLittleEndian(payload[(start+components*4)..])));
                    }
                    micro=start+length;
                }
            }
            at=(int)end;
        }
        return keys;
    }
}
