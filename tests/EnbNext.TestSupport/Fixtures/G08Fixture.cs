using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.TestFixtures;

public static class G08Fixture
{
    public static byte[] File(string name="MAP", string second="RIG.B", float rate=2.5f)
        => Join(Hierarchy(), Classic("A",0,1,[0,20,4]),Classic("B",1,2,[0,40,8]),Mapped(name,["RIG.A",second],rate));
    public static byte[] Hierarchy()
    {
        byte[] header=new byte[36]; U(header,0,0x40000); Name(header,4,"RIG"); U(header,20,3);
        byte[] pivots=new byte[180];
        for(int i=0;i<3;i++){Name(pivots,i*60,"P"+i);U(pivots,i*60+16,i==0?uint.MaxValue:0);F(pivots,i*60+56,1);}
        return Chunk(0x100,Join(Chunk(0x101,header),Chunk(0x102,pivots)));
    }
    public static byte[] Classic(string name,ushort kind,ushort pivot,float[] values)
    {
        byte[] header=new byte[44];U(header,0,0x40000);Name(header,4,name);Name(header,20,"RIG");U(header,36,(uint)values.Length);U(header,40,30);
        byte[] channel=new byte[12+values.Length*4];
        BinaryPrimitives.WriteUInt16LittleEndian(channel.AsSpan(2),(ushort)(values.Length-1));
        BinaryPrimitives.WriteUInt16LittleEndian(channel.AsSpan(4),1);
        BinaryPrimitives.WriteUInt16LittleEndian(channel.AsSpan(6),kind);
        BinaryPrimitives.WriteUInt16LittleEndian(channel.AsSpan(8),pivot);
        for(int i=0;i<values.Length;i++)F(channel,12+i*4,values[i]);
        return Chunk(0x200,Join(Chunk(0x201,header),Chunk(0x202,channel)));
    }
    public static byte[] Mapped(string name,string[] sources,float rate=2.5f,byte[]? pivotMap=null,uint? count=null)
    {
        byte[] header=new byte[48];U(header,0,0x40000);Name(header,4,name);Name(header,20,"RIG");U(header,36,9);F(header,40,rate);U(header,44,count??(uint)sources.Length);
        var children=new List<byte[]>{Chunk(0x777,header)};
        foreach(string source in sources)
        {
            byte[] keys=new byte[24];U(keys,0,0);U(keys,4,0);U(keys,8,4);U(keys,12,2);U(keys,16,8);U(keys,20,1);
            children.Add(Chunk(0x2c2,Join(Chunk(0x888,Encoding.ASCII.GetBytes(source+"\0")),Chunk(0x999,keys))));
        }
        byte[] map=pivotMap??new byte[12];if(pivotMap is null && sources.Length>1)U(map,8,1);
        children.Add(Chunk(0x2c5,map));return Chunk(0x2c0,Join(children.ToArray()));
    }
    public static EnbResolvedRslFileDependency Decode(byte[] bytes)
    {
        if(!EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out var error))throw new InvalidDataException(error);
        return new("mapped.w3d",EnbRslFileDependencyStatus.ResolvedW3d,Model:file.Model){PrototypeFile=file};
    }
    public static EnbW3dRenderResource Empty()=>new(new("fixture","fixture",0,0),"fixture",[],[],[],[],[]);
    public static byte[] Join(params byte[][] parts)=>parts.SelectMany(x=>x).ToArray();
    public static EnbMixResourceCatalog Mix(string directory,params (string Name,byte[] Bytes)[] files)
    {
        Directory.CreateDirectory(directory);
        using(var writer=new BinaryWriter(System.IO.File.Create(Path.Combine(directory,"mixfile_art_01.mix"))))
        {
            uint directoryOffset=(uint)(16+files.Sum(f=>f.Bytes.Length));
            writer.Write(0x3158494dU);writer.Write(directoryOffset);writer.Write(directoryOffset+4+(uint)files.Length*12);writer.Write(0U);
            foreach(var f in files)writer.Write(f.Bytes);
            writer.Write((uint)files.Length);uint offset=16;
            for(int i=0;i<files.Length;i++){writer.Write((uint)i+0x10000000);writer.Write(offset);writer.Write((uint)files[i].Bytes.Length);offset+=(uint)files[i].Bytes.Length;}
            writer.Write((uint)files.Length);
            foreach(var f in files){var name=Encoding.ASCII.GetBytes(f.Name+"\0");writer.Write((byte)name.Length);writer.Write(name);}
        }
        return EnbMixResourceCatalog.Load(directory)??throw new InvalidDataException("Fixture MIX failed");
    }
    public static byte[] Chunk(uint type,byte[] payload){byte[] bytes=new byte[payload.Length+8];U(bytes,0,type);U(bytes,4,(uint)payload.Length);payload.CopyTo(bytes,8);return bytes;}
    private static void U(byte[] b,int i,uint v)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i),v);
    private static void F(byte[] b,int i,float v)=>BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i),v);
    private static void Name(byte[] b,int i,string s)=>Encoding.ASCII.GetBytes(s).CopyTo(b,i);
}
