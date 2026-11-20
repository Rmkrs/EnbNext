using System.Buffers.Binary;
using EnbNext.Assets;
using EnbNext.Rendering;

internal static class HlodProxyMetadataFixture
{
    public static EnbBaseAssetResolution Resolve(EnbRetailAssetBundle assets,EnbBaseAssetResolution resolved)
    {
        var source=resolved.W3d??throw new InvalidDataException("Proxy fixture needs a decoded parent");
        if(!assets.FileResources.TryRead(source.CurrentRetailAggregate?.BaseLogicalFileName??source.LogicalFileName,out var bytes,out _,out var error))
            throw new InvalidDataException(error);
        if(!EnbW3dModel.TryDecode(Append(bytes),out var model))throw new InvalidDataException("Proxy fixture failed to decode");
        return resolved with {W3d=source with {Model=model}};
    }

    public static byte[] Append(byte[] bytes)
    {
        byte[] Chunk(uint type,byte[] payload,bool container=false)
        {
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
            writer.Write(type);writer.Write((uint)payload.Length|(container?0x80000000u:0));writer.Write(payload);return stream.ToArray();
        }
        var header=new byte[8];BinaryPrimitives.WriteUInt32LittleEndian(header,1);
        var member=new byte[36];System.Text.Encoding.ASCII.GetBytes("NO_SUCH_RENDER_OBJECT").CopyTo(member,4);
        byte[] proxy=Chunk(0x706,[..Chunk(0x703,header),..Chunk(0x704,member)],true);
        using var output=new MemoryStream();int parents=0;
        for(int offset=0;offset<bytes.Length;)
        {
            uint kind=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            int length=checked(8+(int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset+4))&0x7fffffffu));
            if(kind==0x700){output.Write(Chunk(kind,[..bytes.AsSpan(offset+8,length-8).ToArray(),..proxy],true));parents++;}
            else output.Write(bytes,offset,length);
            offset+=length;
        }
        if(parents==0)throw new InvalidDataException("Proxy fixture has no HLOD parent");
        return output.ToArray();
    }
}
