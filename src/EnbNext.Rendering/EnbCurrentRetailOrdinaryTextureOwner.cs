using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Owned ordinary texture allocation, source copy and immutable GPU
/// publication. The modern device supplies bounds, supported surface consumers
/// and display precision; implemented DDS consumers share this source dispatch.</summary>
public sealed record EnbCurrentRetailOrdinaryTextureOwner(
    EnbCurrentRetailTextureDeviceCapabilities Device,int TextureDepth,int DisplayDepth,
    int TextureReduction)
{
    public EnbResolvedTextureResource? Load(EnbRetailTextureResolver files,
        EnbCurrentRetailTextureObjectState state,string name,Vector3 shift)
    {
        byte[] dds=[];
        if(state.RetailCompressionAllowed && files.TryReadCurrentRetailSource(name,true,out dds,out var ddsLocation) &&
            EnbCurrentRetailDdsSourceState.TryRead(dds,0,out var compressed) && compressed!.LoadReturnedSuccess)
        {
            var allocated=EnbCurrentRetailDdsTextureOwner.Load(dds,state,Device,TextureDepth,DisplayDepth,TextureReduction,shift);
            return allocated is null?null:new(name,EnbTextureResolutionStatus.Decoded,ddsLocation,allocated);
        }
        if(!files.TryReadCurrentRetailSource(name,false,out var tga,out _) ||
            !EnbCurrentRetailTgaHeaderState.TryRead(tga,out var header))return null;
        int width=state.CurrentRetailThumbnail is {} metadata?checked((int)metadata.Width):header!.Width;
        int height=state.CurrentRetailThumbnail is {} dimensions?checked((int)dimensions.Height):header!.Height;
        uint reduction=unchecked((uint)EnbCurrentRetailTextureSurfaceRequirements.SelectReduction(
            width,height,unchecked((int)state.RetailCreationMipLevels),TextureReduction,false));
        if(!state.TrySelectAllocation(dds,tga,reduction,Device,TextureDepth,DisplayDepth,out var request))return null;
        // Unsupported surface consumers retain their established admission.
        if(request.Format is not (1 or 2 or 3 or 4 or 6 or 7 or 9 or 14))return null;
        width=checked((int)request.Width);height=checked((int)request.Height);
        int maximum=BitOperations.Log2(Math.Max(request.Width,request.Height))+1;
        uint requested=request.RequestedMipLevels;
        //00925980 uses D3DX for square textures;008E3754 caps their levels.
        //Rectangles reach CreateTexture directly, which rejects excess counts.
        if(width!=height && requested>(uint)maximum)
            throw new InvalidDataException("Rectangular texture creation mip count exceeds its dimensions.");
        int count=requested==0?maximum:checked((int)Math.Min(requested,(uint)maximum));
        var levels=new Memory<byte>[count];var pitches=new int[count];
        var surfaces=new (int Width,int Height,ReadOnlyMemory<byte> Pixels,int Pitch)[count];
        int pixelBytes=checked((int)EnbCurrentRetailTextureFormatStorage.BytesPerPixel(request.Format));
        for(int i=0,w=width,h=height;i<count;i++,w=Math.Max(1,w>>1),h=Math.Max(1,h>>1))
        {
            pitches[i]=checked((w*pixelBytes+3)&~3);
            // Modern allocations are initialized. Retail's unwritten rectangular
            // mip tails and palette entries must never expose stale allocation data.
            levels[i]=new byte[checked(pitches[i]*h)];
            surfaces[i]=(w,h,levels[i],pitches[i]);
        }
        if(header!.Width<=0 || header.Height<=0 || header.PixelDepth is <=0 or >32)
            throw new InvalidDataException("Ordinary texture source has invalid pixel storage dimensions.");
        var pixels=new byte[checked(header.Width*header.Height*((header.PixelDepth+7)>>3))];
        var palette=new byte[1024];
        if(state.CopyAllocatedFiles(files,name,request,levels,pitches,pixels,palette,shift,out var location)
            ==EnbCurrentRetailTextureTaskLoadedSource.None)return null;
        EnbTextureImage image=request.Format switch
        {
            1 or 2 or 3=>EnbTextureImage.CreateCurrentRetailColorSurface(request.Format,surfaces),
            9=>EnbTextureImage.CreateCurrentRetailAlphaSurface(surfaces),
            14=>EnbTextureImage.CreateCurrentRetailLuminanceSurface(surfaces),
            _=>EnbTextureImage.CreateCurrentRetailPackedSurface(request.Format,surfaces),
        };
        return new(name,EnbTextureResolutionStatus.Decoded,location,image);
    }
}
