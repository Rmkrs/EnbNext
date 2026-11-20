using System.Buffers.Binary;
using System.Collections.Immutable;
using EnbNext.Assets;

namespace EnbNext.Rendering;

// A0AD80 -> 8E54DA -> 8EDDF1: CreateFileA, never MIX/ordinary missing texture.
public sealed record EnbRetailCubeTexture(ImmutableArray<EnbTextureImage> Faces)
{
    public static bool TryLoad(string workingDirectory,string name,out EnbRetailCubeTexture texture)
    {
        texture=null!;
        try {return TryDecode(File.ReadAllBytes(EnbNext.Installation.EnbPathCase.Resolve(Path.GetFullPath(name,workingDirectory))),out texture);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){return false;}
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes,out EnbRetailCubeTexture texture)
    {
        texture=null!;
        // 8ED942 tests the six face bits, not DDSCAPS2_CUBEMAP or header sizes.
        if(bytes.Length<128 || U(bytes,0)!=0x20534444 || (U(bytes,112)&0xfc00)!=0xfc00)return false;
        uint width=U(bytes,16),height=U(bytes,12),mips=Math.Max(1,U(bytes,28)),format=U(bytes,84);
        if(width==0 || height==0 || width>16384 || height>16384 || mips>32)return false;
        bool compressed=format is 0x31545844 or 0x32545844 or 0x33545844 or 0x34545844 or 0x35545844;
        if(!compressed)
        {
            uint flags=U(bytes,80),bits=U(bytes,88),alpha=U(bytes,104),green=U(bytes,96);
            format=(flags,bits) switch
            {
                (0x41,32) when alpha==0xff000000=>21,
                (0x40,32)=>22,
                (0x40,16) when green==0x7e0=>23,
                (0x40,16) when green==0x3e0=>24,
                (0x41,16) when alpha==0x8000=>25,
                (0x41,16) when alpha==0xf000=>26,
                _=>0
            };
            if(format==0)return false;
        }
        // 8E4D59 chooses the larger dimension. 8E3754 aligns compressed
        // extents and disables mip chains for non-power-of-two extents even
        // on a device supporting such textures. All these formats are admitted
        // by the modern backend; no legacy format substitution is required.
        int edge=checked((int)Math.Max(width,height));
        if(compressed)edge=(edge+3)&~3;
        int levels=(edge&(edge-1))==0?System.Numerics.BitOperations.Log2((uint)edge)+1:1;
        if((long)edge*edge>16_777_216)return false; // Same bounded decoded allocation as EnbDdsTexture.
        int cursor=128;
        var faces=ImmutableArray.CreateBuilder<EnbTextureImage>(6);
        try
        {
            for(int face=0;face<6;face++)
            {
                var source=new List<Surface>();int w=(int)width,h=(int)height;
                for(int level=0;level<mips;level++)
                {
                    int length=Size(format,w,h);
                    if(length>bytes.Length-cursor)return false;
                    source.Add(new(format,w,h,bytes.Slice(cursor,length).ToArray()));cursor+=length;
                    w=Math.Max(1,w>>1);h=Math.Max(1,h>>1);
                }
                var output=new List<Surface>();w=edge;
                for(int level=0;level<levels;level++,w=Math.Max(1,w>>1))
                {
                    // 8E4566 would D3DX-filter faces that are not square or lack
                    // mip levels. No installed file is a cube DDS at all, so that
                    // resize is not carried (archived with the retail translations);
                    // such a file is treated as missing cube storage, retail's NULL.
                    if(level>=source.Count||source[level].Width!=w||source[level].Height!=w)return false;
                    output.Add(source[level]);
                }
                faces.Add(Image(output));
            }
        }
        catch(Exception ex) when(ex is InvalidDataException or OverflowException or ArgumentException){return false;}
        texture=new(faces.MoveToImmutable());return true;
    }

    private sealed record Surface(uint Format,int Width,int Height,byte[] Bytes);
    private static int Pitch(uint format,int width)=>format>=0x31545844?checked(((width+3)/4)*(format==0x31545844?8:16)):checked(width*(format<=22?4:2));
    private static int Size(uint format,int width,int height)=>checked(Pitch(format,width)*(format>=0x31545844?(height+3)/4:height));
    private static EnbTextureImage Image(List<Surface> levels)
    {
        var first=levels[0];
        if(first.Format>=0x31545844)
        {
            byte[] file=new byte[128+levels.Sum(s=>s.Bytes.Length)];
            W(file,0,0x20534444);W(file,4,124);W(file,12,(uint)first.Height);W(file,16,(uint)first.Width);
            W(file,28,(uint)levels.Count);W(file,76,32);W(file,80,4);W(file,84,first.Format);
            int at=128;foreach(var level in levels){level.Bytes.CopyTo(file,at);at+=level.Bytes.Length;}
            if(!EnbRetailTextureDecoder.TryDecode(file,"cube-face.dds",out var image))throw new InvalidDataException("Cube DDS surface could not be sampled.");
            return image;
        }
        // X1R5G5B5 has no alpha; retain native 5-bit color for GPU upload.
        if(first.Format==24)
            levels=levels.Select(s=>
            {
                byte[] pixels=(byte[])s.Bytes.Clone();
                for(int i=1;i<pixels.Length;i+=2)pixels[i]|=0x80;
                return s with {Format=25,Bytes=pixels};
            }).ToList();
        first=levels[0];
        var surfaces=levels.Select(s=>(s.Width,s.Height,(ReadOnlyMemory<byte>)s.Bytes,Pitch(s.Format,s.Width))).ToArray();
        return first.Format<=22?EnbTextureImage.CreateCurrentRetailColorSurface(first.Format==21?2u:3u,surfaces):
            EnbTextureImage.CreateCurrentRetailPackedSurface(first.Format-19,surfaces);
    }
    private static uint U(ReadOnlySpan<byte> data,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
    private static void W(Span<byte> data,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(data[at..],value);
}
