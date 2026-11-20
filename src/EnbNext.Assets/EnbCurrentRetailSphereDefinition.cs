using System.Buffers.Binary;

namespace EnbNext.Assets;

// Validated retained 0741 definition. FILE publication does not create a RenderObj.
public sealed record EnbCurrentRetailSphereDefinition(EnbCurrentRetailSphereHeader Header,
    IReadOnlyDictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>> Tracks)
{
    public static bool TryDecode(ReadOnlySpan<byte> bytes,out EnbCurrentRetailSphereDefinition definition)
    {
        try {definition=Decode(bytes);return true;}
        catch(InvalidDataException) {definition=null!;return false;}
    }
    public static EnbCurrentRetailSphereDefinition Decode(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length>2*1024*1024)throw new InvalidDataException("Sphere resource exceeds decode budget");
        var root=Chunk(bytes,0,bytes.Length);
        if(root.Id!=0x741||root.End!=bytes.Length)throw new InvalidDataException("Expected one sphere resource");
        EnbCurrentRetailSphereHeader? header=null;
        var tracks=new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>();
        for(int at=8;at<root.End;)
        {
            var child=Chunk(bytes,at,root.End);
            var payload=bytes.Slice(at+8,child.End-at-8);
            if(child.Id==1)
            {
                header=EnbCurrentRetailSphereHeader.Read(payload);
            }
            else if(child.Id is >=2 and <=5)
            {
                //0090DFE0 dispatches each occurrence. The property readers
                //clear their existing key list before loading its replacement.
                tracks[child.Id]=EnbCurrentRetailEffectPropertyKeys.Read(payload,child.Id==3?1:child.Id==5?5:3);
            }
            //0090E099 routes unknown child IDs directly to CloseChunk.
            at=child.End;
        }
        if(header is null)throw new InvalidDataException("Sphere header missing");
        if((header.Flags&~15u)!=0)throw new InvalidDataException("Unsupported sphere flags");
        float[] bounds=[header.Duration,header.Center.X,header.Center.Y,header.Center.Z,
            header.AuthoredExtent.X,header.AuthoredExtent.Y,header.AuthoredExtent.Z];
        if(bounds.Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Nonfinite sphere bounds/duration");
        ValidateProperties(header,tracks);
        return new(header,tracks);
    }

    private static (uint Id,int End) Chunk(ReadOnlySpan<byte> bytes,int at,int limit)
    {
        if(limit-at<8)throw new InvalidDataException("Truncated sphere chunk header");
        uint id=BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
        uint size=BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at+4)..])&0x7fffffffu;
        long end=at+8L+size;
        if(end>limit)throw new InvalidDataException("Truncated sphere chunk");
        return(id,(int)end);
    }
    public static void ValidateProperties(EnbCurrentRetailSphereHeader header,
        IReadOnlyDictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>> tracks)
    {
        var p=header.Parameters60;
        if(p.Length!=5)throw new InvalidDataException("Sphere directional parameter width");
        float[] initial=[header.Color.X,header.Color.Y,header.Color.Z,header.Opacity,
            header.Parameter54.X,header.Parameter54.Y,header.Parameter54.Z,..p];
        if(initial.Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Nonfinite sphere property");
        foreach(var (id,keys) in tracks)
        {
            int width=id switch {2=>3,3=>1,4=>3,5=>5,_=>throw new InvalidDataException("Unknown sphere property")};
            if(keys.Count>4096||(keys.Count>0&&keys[0].Time!=0))throw new InvalidDataException("Unsupported sphere track start or size");
            for(int i=0;i<keys.Count;i++)
                if(keys[i].Value.Length!=width||!float.IsFinite(keys[i].Time)||keys[i].Value.Any(v=>!float.IsFinite(v))||
                    (i>0&&keys[i].Time<=keys[i-1].Time))throw new InvalidDataException("Unsupported sphere key ordering or value");
        }
    }
}
