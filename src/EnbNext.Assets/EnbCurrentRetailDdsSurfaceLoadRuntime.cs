using System.Numerics;

namespace EnbNext.Assets;

/// <summary>0094AC00's DDS task after its source file has been selected.
/// The device supplies actual allocated levels, pitches and incoming storage.
/// Task success is independent of whether009A0D30 wrote any pixels.</summary>
public static class EnbCurrentRetailDdsSurfaceLoadRuntime
{
    public static bool TryLoad(ReadOnlySpan<byte> file,uint reduction,uint width,uint height,
        uint destinationFormat,IReadOnlyList<Memory<byte>> levels,IReadOnlyList<int> pitches,Vector3 shift)
    {
        ArgumentNullException.ThrowIfNull(levels);ArgumentNullException.ThrowIfNull(pitches);
        if(levels.Count!=pitches.Count)throw new ArgumentException("DDS allocated levels and pitches must agree.");
        if(!EnbCurrentRetailDdsSourceState.TryRead(file,reduction,out var source) || !source!.LoadReturnedSuccess)return false;
        //0094AC7E ignores each LoadMip return. Even an empty allocation or a
        //loaded source without pixel allocation returns success at0094ACA1.
        for(int index=0;index<levels.Count;index++,width>>=1,height>>=1)
        {
            if(!source.HasPixelAllocation)continue;
            uint level=(uint)index;
            if(level>=source.MipLevels)throw new InvalidDataException("DDS task exceeds its native source tables.");
            if(width!=Math.Max(4u,source.ReducedWidth>>(index&31)) ||
                height!=Math.Max(4u,source.ReducedHeight>>(index&31)))continue;
            if(destinationFormat is >=20 and <=24)
                EnbCurrentRetailCompressedSurfaceLoadRuntime.LoadMip(file,source,level,destinationFormat,width,height,levels[index].Span,shift);
            else if(destinationFormat==source.Format && !EnbCurrentRetailHsvRuntime.ChangesDdsColor(shift))
            {
                //009A142A's same-format byte copy applies to every native
                //format, not only compressed formats; pitch is ignored.
                ulong offset=source.PixelOffset;
                uint bytes=source.BaseLevelBytes,remaining=level;
                while(remaining!=0 && bytes>16){offset+=bytes;bytes>>=2;remaining--;}
                offset+=(ulong)bytes*remaining;
                if(offset>(ulong)file.Length || bytes>(ulong)file.Length-offset || bytes>levels[index].Length)
                    throw new InvalidDataException("DDS task copy exceeds its source or destination storage.");
                file.Slice((int)offset,(int)bytes).CopyTo(levels[index].Span);
            }
            else
                EnbCurrentRetailDdsArgbSurfaceLoadRuntime.LoadMip(file,source,level,destinationFormat,width,height,levels[index].Span,pitches[index],shift);
            //0094AC69 supplies the same HSV words to every DDS level.
        }
        return true;
    }
}
