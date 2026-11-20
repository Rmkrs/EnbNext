using System.Buffers.Binary;

namespace EnbNext.Assets;

/// <summary>009A0870/009A0B80's DDS source and payload state. This describes
/// metadata and read ownership; it does not invent pixels for absent storage.</summary>
public sealed record EnbCurrentRetailDdsSourceState(uint Width,uint Height,uint ReducedWidth,
    uint ReducedHeight,uint MipLevels,uint Reduction,uint Format,uint BaseLevelBytes,
    uint PixelOffset,uint RemainingBytes,bool LoadReturnedSuccess)
{
    public bool HasPixelAllocation=>LoadReturnedSuccess && RemainingBytes!=0 && RemainingBytes<0x80000000;

    public static bool TryRead(ReadOnlySpan<byte> file,uint requestedReduction,
        out EnbCurrentRetailDdsSourceState? state)
    {
        state=null;
        //The native constructor reads four bytes without testing DDS magic,
        //then requires the complete 124-byte header. Contain incomplete reads.
        if(file.Length<128 || BinaryPrimitives.ReadUInt32LittleEndian(file[4..])!=124)return false;
        uint width=BinaryPrimitives.ReadUInt32LittleEndian(file[16..]);
        uint height=BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        var (levels,reduction)=EnbCurrentRetailTextureSurfaceRequirements.SelectDdsSourceMips(
            BinaryPrimitives.ReadUInt32LittleEndian(file[28..]),requestedReduction);
        uint sdkFormat=BinaryPrimitives.ReadUInt32LittleEndian(file[84..]);
        if((int)sdkFormat<0)return false; //Native negative index reads outside its table.
        uint format=MapFormat(sdkFormat);
        uint blockBytes=unchecked((width>>2)*(height>>2));
        if(format==20)blockBytes=unchecked(blockBytes*8);
        else if(format is >=21 and <=24)blockBytes=unchecked(blockBytes*16);
        uint skipped=0,remainingReduction=reduction,baseBytes=blockBytes;
        //Once at <=16 bytes, retail repeats the same size for every remaining
        //iteration. Multiplication preserves the exact 32-bit sum and avoids
        //a caller-controlled multi-billion-iteration loop.
        while(remainingReduction!=0 && baseBytes>16)
        {
            skipped=unchecked(skipped+baseBytes);
            baseBytes>>=2;
            remainingReduction--;
        }
        skipped=unchecked(skipped+baseBytes*remainingReduction);
        uint available=unchecked((uint)file.Length-128);
        if(available==0)skipped=0; //009A0BF5 returns before the skip loop.
        state=new(width,height,width>>(int)(reduction&31),height>>(int)(reduction&31),
            levels,reduction,format,baseBytes,unchecked(128+skipped),unchecked(available-skipped),available!=0);
        return true;
    }

    /// <summary>00995980 and initialized00C0B948 from00995A20.</summary>
    public static uint MapFormat(uint format)
    {
        if((int)format<0)throw new ArgumentOutOfRangeException(nameof(format));
        return format switch
        {
        0=>5,
        >=20 and <=30=>format-19,
        40=>12,41=>13,50=>14,51=>15,52=>16,60=>17,61=>18,62=>19,
        0x31545844=>20,0x32545844=>21,0x33545844=>22,0x34545844=>23,0x35545844=>24,
            _=>0
        };
    }
}
