using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Assets;

public sealed record EnbCurrentRetailW3dLegacyLodDefinition(uint Version,string Name,
    IReadOnlyList<EnbCurrentRetailW3dLegacyLodChild> Children);
public sealed record EnbCurrentRetailW3dLegacyLodChild(string Name,uint SourceWord32,uint SourceWord36);

/// <summary>Complete-source009A4CA0 admission.0401 supplies24 bytes and
/// its low16 count controls exactly that many consecutive0402 prefixes.
///009A4990 subsequently discards the temporary distance words when converting
/// these registered children, in reverse order, into an HLOD.</summary>
public static class EnbCurrentRetailW3dLegacyLodDecoder
{
    public static bool TryDecode(ReadOnlySpan<byte> bytes,
        out EnbCurrentRetailW3dLegacyLodDefinition definition,out string? diagnostic)
    {
        definition=default!;diagnostic=null;
        if(!Read(bytes,0,bytes.Length,out uint type,out int start,out int end) || type!=0x400 || end!=bytes.Length)
            return Fail("resource is not one complete root0400",out diagnostic);
        int limit=end;
        if(!Read(bytes,start,limit,out type,out start,out end) || type!=0x401 || end-start<24)
            return Fail("first child must provide the complete24-byte0401 prefix",out diagnostic);
        var header=bytes.Slice(start,24);
        if(!Name(header[4..],out string name))return Fail("0401 name exceeds the copied source prefix",out diagnostic);
        uint version=BinaryPrimitives.ReadUInt32LittleEndian(header);
        int count=BinaryPrimitives.ReadUInt16LittleEndian(header[20..]);
        var children=new List<EnbCurrentRetailW3dLegacyLodChild>();
        int offset=end;
        for(int index=0;index<count;index++)
        {
            if(!Read(bytes,offset,limit,out type,out start,out end) || type!=0x402 || end-start<40)
                return Fail("0400 requires its counted consecutive40-byte0402 prefixes",out diagnostic);
            var child=bytes.Slice(start,40);
            if(!Name(child,out string childName))return Fail("0402 name exceeds the copied source prefix",out diagnostic);
            children.Add(new(childName,BinaryPrimitives.ReadUInt32LittleEndian(child[32..]),
                BinaryPrimitives.ReadUInt32LittleEndian(child[36..])));
            offset=end;
        }
        definition=new(version,name,children.AsReadOnly());return true;
    }
    private static bool Read(ReadOnlySpan<byte> bytes,int offset,int limit,out uint type,out int start,out int end)
    {
        type=0;start=end=0;if(offset<0 || limit-offset<8)return false;
        type=BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        uint size=BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset+4)..])&0x7fffffff;
        start=offset+8;if(size>(uint)(limit-start))return false;end=start+(int)size;return true;
    }
    private static bool Name(ReadOnlySpan<byte> bytes,out string name)
    {
        int end=bytes.IndexOf((byte)0);name=end<0?string.Empty:Encoding.Latin1.GetString(bytes[..end]);return end>=0;
    }
    private static bool Fail(string message,out string? diagnostic){diagnostic=message;return false;}
}
