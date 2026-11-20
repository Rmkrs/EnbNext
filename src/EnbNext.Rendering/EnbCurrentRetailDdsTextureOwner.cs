using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Retained texture metadata, device bounds and source ownership for
/// the implemented compressed and scalar publication consumers.</summary>
public static class EnbCurrentRetailDdsTextureOwner
{
    public static EnbTextureImage? Load(ReadOnlySpan<byte> file,
        EnbCurrentRetailTextureObjectState state,EnbCurrentRetailTextureDeviceCapabilities device,
        int textureDepth,int displayDepth,int textureReduction,Vector3 shift)
    {
        if(!EnbCurrentRetailDdsSourceState.TryRead(file,0,out var original) || !original!.HasPixelAllocation)
            return null;
        uint width=state.CurrentRetailThumbnail?.Width??original.Width;
        uint height=state.CurrentRetailThumbnail?.Height??original.Height;
        uint reduction=unchecked((uint)EnbCurrentRetailTextureSurfaceRequirements.SelectReduction(
            checked((int)width),checked((int)height),unchecked((int)state.RetailCreationMipLevels),textureReduction,false));
        uint authoredLevels=Math.Max(1,BinaryPrimitives.ReadUInt32LittleEndian(file[28..]));
        // Authorized partial-chain correction: allocate from the last authored
        // level instead of native request/source divergence and table overread.
        bool corrected=reduction>=authoredLevels;
        if(corrected)reduction=authoredLevels-1;
        //94A3F0 can select the ordinary allocation from retained noncompressed
        // metadata even though94A480 subsequently succeeds with the DDS file.
        if(!state.TrySelectAllocation(file,[],reduction,device,textureDepth,displayDepth,out var request))return null;
        bool compressed=request.Format is 20 or 21 or 22 or 23 or 24;
        if(!compressed && request.Format is not (1 or 2 or 3 or 4 or 6 or 7 or 9 or 14))return null;
        if(request.Reduction>=authoredLevels)
            throw new InvalidDataException("DDS device bounds require a mip beyond its authored chain.");
        EnbCurrentRetailDdsSourceState.TryRead(file,request.Reduction,out var source);
        uint allocatedWidth=request.Width,allocatedHeight=request.Height;
        //00925980: square requests pass008E3754's block alignment. The
        // task dimensions remain unchanged;94AB50 refreshes only level count.
        if(compressed && allocatedWidth==allocatedHeight)
            allocatedWidth=allocatedHeight=(allocatedWidth+3)&~3u;
        else if(compressed && ((allocatedWidth&3)!=0 || (allocatedHeight&3)!=0))
            throw new InvalidDataException("Direct rectangular DDS allocation requires block-aligned dimensions.");
        uint count=request.RequestedMipLevels;
        uint maximum=(uint)BitOperations.Log2(Math.Max(allocatedWidth,allocatedHeight))+1;
        count=count==0?maximum:Math.Min(count,maximum);
        if(corrected)count=Math.Min(count,source!.MipLevels);
        if(compressed)return EnbCurrentRetailAllocatedDdsImage.Create(file,request.Reduction,
            allocatedWidth,allocatedHeight,request.Format,count,shift,request.Width,request.Height);
        int bytesPerPixel=checked((int)EnbCurrentRetailTextureFormatStorage.BytesPerPixel(request.Format));
        var levels=new Memory<byte>[count];var pitches=new int[count];
        var surfaces=new (int Width,int Height,ReadOnlyMemory<byte> Pixels,int Pitch)[count];
        for(int index=0,w=checked((int)allocatedWidth),h=checked((int)allocatedHeight);index<count;index++,w=Math.Max(1,w>>1),h=Math.Max(1,h>>1))
        {
            if((long)w*h>16_777_216)throw new InvalidDataException("DDS scalar image exceeds bounded decoded storage.");
            pitches[index]=checked((w*bytesPerPixel+3)&~3);
            levels[index]=new byte[checked(h*pitches[index])];
            surfaces[index]=(w,h,levels[index],pitches[index]);
        }
        // Keep the native task's whole-success/no-write result, containing an
        // exhausted source table in initialized modern allocation storage.
        int copyCount=checked((int)Math.Min(count,source!.MipLevels));
        EnbCurrentRetailDdsSurfaceLoadRuntime.TryLoad(file,request.Reduction,request.Width,request.Height,
            request.Format,levels[..copyCount],pitches[..copyCount],shift);
        return request.Format switch
        {
            1 or 2 or 3=>EnbTextureImage.CreateCurrentRetailColorSurface(request.Format,surfaces),
            9=>EnbTextureImage.CreateCurrentRetailAlphaSurface(surfaces),
            14=>EnbTextureImage.CreateCurrentRetailLuminanceSurface(surfaces),
            _=>EnbTextureImage.CreateCurrentRetailPackedSurface(request.Format,surfaces),
        };
    }
}
