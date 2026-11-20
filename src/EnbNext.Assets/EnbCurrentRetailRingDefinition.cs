using System.Buffers.Binary;

namespace EnbNext.Assets;

// Validated retained 0742 definition; sampling and geometry remain renderer-owned.
public sealed record EnbCurrentRetailRingDefinition(EnbCurrentRetailRingHeader Header,
    IReadOnlyDictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>> Tracks)
{
    public static bool TryDecode(ReadOnlySpan<byte> bytes,out EnbCurrentRetailRingDefinition definition)
    {
        try {definition=Decode(bytes);return true;}
        catch(InvalidDataException) {definition=null!;return false;}
    }
    public static EnbCurrentRetailRingDefinition Decode(ReadOnlySpan<byte> bytes)
    {
        var root=Chunk(bytes,0,bytes.Length);
        if(root.Id!=0x742 || root.End!=bytes.Length)
            throw new InvalidDataException("Expected one ring resource");
        EnbCurrentRetailRingHeader? header=null;
        var tracks=new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>();
        for(int at=8;at<root.End;)
        {
            var child=Chunk(bytes,at,root.End);
            var payload=bytes.Slice(at+8,child.End-at-8);
            if(child.Id==1)header=EnbCurrentRetailRingHeader.Read(payload);
            else if(child.Id is >=2 and <=5)
                tracks[child.Id]=EnbCurrentRetailEffectPropertyKeys.Read(payload,child.Id==2?3:child.Id==3?1:2);
            at=child.End;
        }
        if(header is null)throw new InvalidDataException("Ring header missing");
        float[] values=[header.Duration,header.Color.X,header.Color.Y,header.Color.Z,header.Opacity,
            header.FirstRadii.X,header.FirstRadii.Y,header.SecondRadii.X,header.SecondRadii.Y,
            header.FirstRadiusMultiplier.X,header.FirstRadiusMultiplier.Y,header.SecondRadiusMultiplier.X,header.SecondRadiusMultiplier.Y];
        if(values.Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Nonfinite ring parameters");
        foreach(var track in tracks.Values)
        {
            // Supported playback subset: finite ordered tracks beginning at zero.
            // This avoids the native one-key-before-first division and leaves unusual
            // authored data available through the unrestricted inspection decoder.
            if(track.Count>4096 || (track.Count>0 && track[0].Time!=0))
                throw new InvalidDataException("Unsupported ring track start or key budget");
            for(int i=0;i<track.Count;i++)
                if(!float.IsFinite(track[i].Time) || track[i].Value.Any(v=>!float.IsFinite(v)) ||
                    (i>0 && track[i].Time<=track[i-1].Time))
                    throw new InvalidDataException("Unsupported ring key ordering or value");
        }
        return new(header,tracks);
    }

    private static (uint Id,int End) Chunk(ReadOnlySpan<byte> bytes,int at,int limit)
    {
        if(limit-at<8)throw new InvalidDataException("Truncated ring chunk header");
        uint id=BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
        uint size=BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at+4)..])&0x7fffffffu;
        long end=at+8L+size;
        if(end>limit)throw new InvalidDataException("Truncated ring chunk");
        return(id,(int)end);
    }
}
