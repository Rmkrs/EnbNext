using System.Buffers.Binary;
using System.Numerics;

namespace EnbNext.Assets;

/// <summary>009A0D30/009A2050's DDS source to native pixel destinations.
/// Allocation and incoming pitch/storage remain explicit device inputs.</summary>
public static class EnbCurrentRetailDdsArgbSurfaceLoadRuntime
{

    public static bool LoadMip(ReadOnlySpan<byte> file,EnbCurrentRetailDdsSourceState source,
        uint level,uint destinationFormat,uint width,uint height,Span<byte> destination,int pitch,Vector3 shift)
    {
        if(!source.HasPixelAllocation)return false;
        if(level>=source.MipLevels)throw new InvalidDataException("DDS level exceeds its native source tables.");
        if(width!=Math.Max(4u,source.ReducedWidth>>(int)(level&31)) ||
            height!=Math.Max(4u,source.ReducedHeight>>(int)(level&31)))return false;
        //009A342C/3474 default slots invoke the no-op then continue without
        //stores. Compressed and unsupported destination IDs preserve storage.
        int pixelBytes=destinationFormat switch {1=>3,2 or 3=>4,4 or 6 or 7=>2,8 or 9 or 14=>1,_=>0};
        if(pixelBytes==0 || source.Format is not (20 or 24))return false; //009A3408 source dispatch.
        ulong offset=source.PixelOffset;
        uint bytes=source.BaseLevelBytes,remaining=level;
        while(remaining!=0 && bytes>16){offset+=bytes;bytes>>=2;remaining--;}
        offset+=(ulong)bytes*remaining;
        ulong columns=((ulong)width+3)/4,rows=((ulong)height+3)/4;
        ulong stride=width>>2;
        uint blockBytes=source.Format==20?8u:16u;
        if(pitch<=0 || offset>(ulong)file.Length ||
            ((rows-1)*stride+columns)*blockBytes>(ulong)file.Length-offset ||
            (rows*4-1)*(uint)pitch+columns*4*(uint)pixelBytes+(destinationFormat==8?1u:0u)>(ulong)destination.Length)
            throw new InvalidDataException("DDS block conversion exceeds supplied storage.");
        bool changes=EnbCurrentRetailHsvRuntime.ChangesDdsColor(shift);
        Span<uint> palette=stackalloc uint[4];
        Span<uint> alpha=stackalloc uint[8];
        for(ulong row=0;row<rows;row++)
        for(ulong column=0;column<columns;column++)
        {
            var input=file.Slice((int)(offset+(row*stride+column)*blockBytes),(int)blockBytes);
            var colors=source.Format==20?input:input[8..];
            ushort first=BinaryPrimitives.ReadUInt16LittleEndian(colors),second=BinaryPrimitives.ReadUInt16LittleEndian(colors[2..]);
            uint a=Expand(first),b=Expand(second);
            bool four=source.Format==24 || a>b; //009A2150 compares before HSV; DXT5 always uses four colors.
            if(changes){a=EnbCurrentRetailHsvRuntime.ShiftArgb8(a,shift);b=EnbCurrentRetailHsvRuntime.ShiftArgb8(b,shift);}
            palette[0]=a|0xff000000;palette[1]=b|0xff000000;
            //009A240B..2499 implements85*(2*a+b)>>8, not division by3.
            //009A28E9..293F uses127*a+128*b for the three-color midpoint.
            palette[2]=Mix(a,b,four?170u:127u,four?85u:128u)|0xff000000;
            palette[3]=four?Mix(a,b,85,170)|0xff000000:0;
            uint selectors=BinaryPrimitives.ReadUInt32LittleEndian(colors[4..]);
            ulong alphaSelectors=0;
            if(source.Format==24)
            {
                alpha[0]=input[0];alpha[1]=input[1];
                if(alpha[0]>alpha[1])
                    for(uint index=2;index<8;index++)alpha[(int)index]=((8-index)*alpha[0]+(index-1)*alpha[1]+3)/7;
                else
                {
                    for(uint index=2;index<6;index++)alpha[(int)index]=((6-index)*alpha[0]+(index-1)*alpha[1]+2)/5;
                    alpha[6]=0;alpha[7]=255;
                }
                //009A2B33..2C3B rounds its integer7/5 division with+3/+2.
                alphaSelectors=BinaryPrimitives.ReadUInt64LittleEndian(input)>>16;
            }
            for(int y=0;y<4;y++)
            for(int x=0;x<4;x++,selectors>>=2,alphaSelectors>>=3)
            {
                uint color=palette[(int)(selectors&3)];
                if(source.Format==24)color=(color&0xffffff)|(alpha[(int)(alphaSelectors&7)]<<24);
                Store(destination[(int)((row*4+(uint)y)*(uint)pitch+(column*4+(uint)x)*(uint)pixelBytes)..],
                    color,destinationFormat);
            }
        }
        return true;
    }

    private static uint Expand(ushort word)
        => (uint)(((word&0xf800)<<8)|((word&0x7e0)<<5)|((word&31)<<3));

    private static uint Mix(uint a,uint b,uint wa,uint wb)
        => ((((a>>16&255)*wa+(b>>16&255)*wb)>>8)<<16) |
           ((((a>>8&255)*wa+(b>>8&255)*wb)>>8)<<8) |
           (((a&255)*wa+(b&255)*wb)>>8);

    private static void Store(Span<byte> output,uint color,uint format)
    {
        uint r=color>>16&255,g=color>>8&255,b=color&255,a=color>>24;
        switch(format)
        {
            case 1:output[0]=(byte)b;output[1]=(byte)g;output[2]=(byte)r;break;
            case 2:case 3:BinaryPrimitives.WriteUInt32LittleEndian(output,color);break;
            case 4:BinaryPrimitives.WriteUInt16LittleEndian(output,(ushort)((r&248)<<8|(g&252)<<3|b>>3));break;
            case 6:BinaryPrimitives.WriteUInt16LittleEndian(output,(ushort)((a==0?0u:0x8000u)|(r&248)<<7|(g&248)<<2|b>>3));break;
            case 7:BinaryPrimitives.WriteUInt16LittleEndian(output,(ushort)((a&240)<<8|(r&240)<<4|g&240|b>>4));break;
            //009A257B writes a WORD then advances one byte: retain the final
            //zero spill byte, including the overlap with the next pixel.
            case 8:BinaryPrimitives.WriteUInt16LittleEndian(output,(ushort)((r&224)|(g&224)>>3|b>>6));break;
            //009A25F6 stores BL for native A8; this is the selected blue byte.
            case 9:output[0]=(byte)b;break;
            //009A259A's exact integer weights sum to65536.
            case 14:output[0]=(byte)((b*4725+g*46885+r*13926)>>16);break;
        }
    }
}
