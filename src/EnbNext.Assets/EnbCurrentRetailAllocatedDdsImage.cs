using System.Buffers.Binary;
using System.Numerics;

namespace EnbNext.Assets;

/// <summary>Publishes allocated BC1/BC2/BC3 blocks through the GPU compressed lane.
/// Native source offsets and no-write branches precede decoding. BC1 retains
/// the established authored-palette-mode correction after endpoint HSV.</summary>
public static class EnbCurrentRetailAllocatedDdsImage
{
    public static EnbTextureImage Create(ReadOnlySpan<byte> file,uint reduction,
        uint width,uint height,uint format,uint mipLevels,Vector3 shift,
        uint? copyWidth=null,uint? copyHeight=null)
    {
        if(format is not (20 or 21 or 22 or 23 or 24) || mipLevels==0 ||
            !EnbCurrentRetailDdsSourceState.TryRead(file,reduction,out var source) || !source!.HasPixelAllocation)
            throw new InvalidDataException("DDS publication requires an implemented compressed source consumer.");
        var frozen=new EnbTextureMipLevel[checked((int)mipLevels)];
        uint taskWidth=copyWidth??width,taskHeight=copyHeight??height;
        for(uint level=0;level<mipLevels;level++,width=Math.Max(1,width>>1),height=Math.Max(1,height>>1),taskWidth>>=1,taskHeight>>=1)
        {
            // Same bounded image admission as the established DDS decoder.
            if(width==0 || height==0 || (ulong)width*height>16_777_216)
                throw new InvalidDataException("Allocated DDS image exceeds bounded decoded storage.");
            int blockBytes=format==20?8:16;
            var blocks=new byte[checked((int)(((ulong)width+3)/4*((height+3)/4)*(uint)blockBytes))];
            // Native allocation is uninitialized. Modern storage is zeroed;
            // never read beyond a shorter source table to fill extra levels.
            bool written=level<source.MipLevels &&
                EnbCurrentRetailCompressedSurfaceLoadRuntime.LoadMip(file,source,level,format,taskWidth,taskHeight,blocks,shift);
            var carrier=new byte[checked(128+blocks.Length)];
            file[..128].CopyTo(carrier);
            BinaryPrimitives.WriteUInt32LittleEndian(carrier,0x20534444);
            BinaryPrimitives.WriteUInt32LittleEndian(carrier.AsSpan(12),height);
            BinaryPrimitives.WriteUInt32LittleEndian(carrier.AsSpan(16),width);
            BinaryPrimitives.WriteUInt32LittleEndian(carrier.AsSpan(28),1);
            BinaryPrimitives.WriteUInt32LittleEndian(carrier.AsSpan(76),32);
            BinaryPrimitives.WriteUInt32LittleEndian(carrier.AsSpan(84),format==20?0x31545844u:format is 23 or 24?0x35545844u:0x33545844u);
            blocks.CopyTo(carrier,128);
            EnbDdsTexture decoded;
            bool valid;
            if(format==20 && written && EnbCurrentRetailHsvRuntime.ChangesDdsColor(shift))
            {
                // Decode the original selectors and endpoint ordering, then
                // shift endpoints. Re-reading shifted ordering causes holes.
                ulong offset=source.PixelOffset;uint bytes=source.BaseLevelBytes,remaining=level;
                while(remaining!=0 && bytes>16){offset+=bytes;bytes>>=2;remaining--;}
                offset+=(ulong)bytes*remaining;
                file.Slice(checked((int)offset),blocks.Length).CopyTo(carrier.AsSpan(128));
                valid=EnbDdsTexture.TryDecodeDxt1Hsv(carrier,shift,out decoded);
            }
            else valid=format==20?EnbDdsTexture.TryDecodeDxt1(carrier,out decoded):
                format is 23 or 24?EnbDdsTexture.TryDecodeDxt5(carrier,out decoded):EnbDdsTexture.TryDecodeDxt3(carrier,out decoded);
            if(!valid)throw new InvalidDataException("Allocated DDS level cannot be decoded.");
            frozen[level]=decoded.MipLevels[0];
        }
        return new(EnbTextureFormat.CurrentRetailPackedSurface,Array.AsReadOnly(frozen))
            {CurrentRetailSurfaceFormat=format};
    }
}
