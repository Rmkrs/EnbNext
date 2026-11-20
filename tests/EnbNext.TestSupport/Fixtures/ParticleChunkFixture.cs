using System.Buffers.Binary;

internal static class ParticleChunkFixture
{
    public static byte[] ExpandCurves(byte[] bytes)
    {
        var children=new List<byte[]>();bool properties=false;
        for(int offset=8;offset<bytes.Length;)
        {
            uint type=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            int size=(int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset+4))&0x7fffffff);
            byte[] payload=bytes.AsSpan(offset+8,size).ToArray();
            if(type==0x505)
            {
                var expanded=new List<byte>(payload.AsSpan(0,40).ToArray());int cursor=40;
                for(int curve=0;curve<3;curve++)
                {
                    int count=(int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(curve*4));
                    expanded.AddRange(payload.AsSpan(cursor,count*8).ToArray());
                    var last=payload.AsSpan(cursor+(count-1)*8,8).ToArray();
                    float time=BinaryPrimitives.ReadSingleLittleEndian(last);
                    for(int i=count;i<4097;i++)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(last,time+(i-count+1));expanded.AddRange(last);
                    }
                    cursor+=count*8;
                }
                payload=expanded.ToArray();
                for(int curve=0;curve<3;curve++)BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(curve*4),4097);
                properties=true;
            }
            // Replace the installed frame curve with an equivalent constant
            // only in fixtures whose frame property is the default zero.
            if(type!=0x50B)children.Add(Chunk(type,payload,false));
            offset+=8+size;
        }
        if(!properties)throw new Exception("Large curve fixture requires a V2 definition");
        byte[] frame=new byte[16+4098*8];BinaryPrimitives.WriteUInt32LittleEndian(frame,4097);
        for(int i=0;i<4098;i++)BinaryPrimitives.WriteSingleLittleEndian(frame.AsSpan(16+i*8),i);
        children.Add(Chunk(0x50B,frame,false));
        return Chunk(0x500,children.SelectMany(c=>c).ToArray(),true);
    }

    public static byte[] Extend(byte[] bytes)
    {
        var children=new List<byte[]>();
        for(int offset=8;offset<bytes.Length;)
        {
            uint type=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            int size=(int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset+4))&0x7fffffff);
            children.Add(Chunk(type,[..bytes.AsSpan(offset+8,size),0x91,0x92,0x93],true));
            offset+=8+size;
        }
        // Optional-loop unknown container and superseded mandatory types must
        // be skipped without parsing their deliberately incomplete payloads.
        children.Add(Chunk(0x7ffffffe,[1,2,3],true));
        children.Add(Chunk(0x501,[],true));
        children.Add(Chunk(0x503,[],false));
        return Chunk(0x500,children.SelectMany(c=>c).ToArray(),false);
    }
    private static byte[] Chunk(uint type,byte[] payload,bool hint)
    {
        byte[] bytes=new byte[payload.Length+8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes,type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),(uint)payload.Length|(hint?0x80000000u:0));
        payload.CopyTo(bytes,8);return bytes;
    }
}
