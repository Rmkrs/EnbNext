using System.Buffers.Binary;
using System.Numerics;

namespace EnbNext.Assets;

/// <summary>0094AC00/009A0D30's compressed destination writes. Destination
/// storage belongs to the device; branches without writes retain its bytes.</summary>
public static class EnbCurrentRetailCompressedSurfaceLoadRuntime
{
    public static bool LoadMip(ReadOnlySpan<byte> file,EnbCurrentRetailDdsSourceState source,
        uint level,uint destinationFormat,uint width,uint height,Span<byte> destination,Vector3 shift)
    {
        if(destinationFormat is <20 or >24)throw new ArgumentOutOfRangeException(nameof(destinationFormat));
        if(!source.HasPixelAllocation)return false;
        if(level>=source.MipLevels)throw new InvalidDataException("DDS level exceeds its native source tables.");
        //009A1461/147B return outright when either dimension differs.
        if(width!=Math.Max(4u,source.ReducedWidth>>(int)(level&31)) ||
            height!=Math.Max(4u,source.ReducedHeight>>(int)(level&31)))return false;
        bool changes=EnbCurrentRetailHsvRuntime.ChangesDdsColor(shift);
        bool convert=source.Format==20 && destinationFormat==21;
        if(source.Format!=destinationFormat && !convert)
        {
            //009A1A13 enters009A2050. Every compressed destination reaches
            //009A33BF's no-op0099B190, with no destination store.
            return false;
        }
        if(!convert && changes && source.Format is not (20 or 24))return false; //009A1081.

        ulong offset=source.PixelOffset;
        uint bytes=source.BaseLevelBytes,remaining=level;
        while(remaining!=0 && bytes>16)
        {
            offset+=bytes;
            bytes>>=2;
            remaining--;
        }
        offset+=(ulong)bytes*remaining;
        if(offset>(ulong)file.Length)throw new InvalidDataException("DDS level begins outside its loaded payload.");
        if(!convert && !changes)
        {
            //009A142A copies the level-size table entry as a tight byte range;
            //the supplied destination pitch does not participate in this path.
            if(bytes>file.Length-(long)offset || bytes>destination.Length)
                throw new InvalidDataException("DDS level copy exceeds its source or destination storage.");
            file.Slice((int)offset,(int)bytes).CopyTo(destination);
            return true;
        }
        ulong blocks=((ulong)width+3)/4*(((ulong)height+3)/4);
        int inputBlock=source.Format==20?8:16,outputBlock=destinationFormat==20?8:16;
        if(blocks>(ulong)(file.Length-(long)offset)/(uint)inputBlock || blocks>(ulong)destination.Length/(uint)outputBlock)
            throw new InvalidDataException("DDS endpoint write exceeds its source or destination storage.");
        for(ulong block=0;block<blocks;block++)
        {
            var input=file.Slice((int)(offset+block*(uint)inputBlock),inputBlock);
            var output=destination.Slice((int)(block*(uint)outputBlock),outputBlock);
            if(convert)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(output,ulong.MaxValue);
                input.CopyTo(output[8..]);
            }
            else input.CopyTo(output);
            if(!changes)continue;
            int colors=outputBlock==8?0:8;
            for(int endpoint=0;endpoint<2;endpoint++)
            {
                var word=output.Slice(colors+endpoint*2,2);
                BinaryPrimitives.WriteUInt16LittleEndian(word,
                    EnbCurrentRetailHsvRuntime.ShiftRgb565(BinaryPrimitives.ReadUInt16LittleEndian(word),shift));
            }
        }
        return true;
    }
}
