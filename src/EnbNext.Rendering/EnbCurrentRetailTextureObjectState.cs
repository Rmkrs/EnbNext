using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// Sampling and current surface-format state shared by one native TextureClass.
/// The caller supplies creation semantics and completed surface publications.
/// 0094B3D0 initializes the mip selector;0094CCC0 changes address modes and can
/// disable mip filtering after a cache hit, without changing creation state.
/// This object does not perform name lookup or acquire a texture.
/// </summary>
public sealed class EnbCurrentRetailTextureObjectState
{
    private EnbW3dTextureSamplingSemantics _sampling;
    public uint RetailCreationFormat { get; }
    private uint _currentRetailFormat;
    //TextureClass+54 starts with the request and changes on completed surface
    //publication. Keep the original request separately as provenance.
    public uint CurrentRetailFormat => Volatile.Read(ref _currentRetailFormat);
    public uint RetailCreationMipLevels { get; }
    public bool RetailCompressionAllowed { get; }
    public EnbCurrentRetailTextureThumbnail? CurrentRetailThumbnail { get; }

    public EnbCurrentRetailTextureObjectState(EnbW3dTextureSamplingSemantics creation)
        : this(new EnbW3dTextureCreationRequest(creation, 0)) { }

    public EnbCurrentRetailTextureObjectState(EnbW3dTextureCreationRequest creation)
    {
        RetailCreationFormat = creation.RetailFormat;
        _currentRetailFormat = creation.RetailFormat;
        //0094B427's byte table routes20..24 to0094B434, forcing+0E=1.
        //All other formats retain the resolved constructor input.
        RetailCompressionAllowed = creation.RetailFormat is >=20 and <=24 || creation.CurrentRetailCompressionAllowed;
        CurrentRetailThumbnail=creation.CurrentRetailThumbnail;
        uint requestedLevels=creation.CurrentRetailCreationMipLevels??creation.Sampling.RetailCreationSelector;
        //0094B3D0 writes+60 from its input before0091AD40 can overwrite+8.
        //The resolved allocation count must not rewrite those sampling words.
        RetailCreationMipLevels=requestedLevels!=1 && CurrentRetailThumbnail is {} thumbnail
            ? thumbnail.MipLevels : requestedLevels;
        _sampling = creation.Sampling with
        {
            AddressU = EnbRenderTextureAddressMode.Wrap,
            AddressV = EnbRenderTextureAddressMode.Wrap,
        };
    }

    public EnbW3dTextureSamplingSemantics Sampling => _sampling;

    //0094C118..126 updates+54 from the allocated surface only when the caller
    //marks it ready. Thumbnail publication leaves the existing format intact.
    //Resource ownership/readiness and pressure scheduling remain caller-owned.
    public void PublishSurfaceFormat(uint format,bool markReady)
    {
        if(markReady)Volatile.Write(ref _currentRetailFormat,format);
    }

    /// <summary>The same copy dispatch with the actual selected file owner.
    /// Ordinary input is opened only after compressed loading fails; successful
    /// no-write DDS paths still suppress it. Allocations remain caller-owned.</summary>
    public EnbCurrentRetailTextureTaskLoadedSource CopyAllocatedFiles(EnbRetailTextureResolver files,
        string sourceName,EnbCurrentRetailTextureAllocationRequest allocation,
        IReadOnlyList<Memory<byte>> levels,IReadOnlyList<int> pitches,
        Memory<byte> ordinaryPixels,Memory<byte> ordinaryPalette,System.Numerics.Vector3 shift,
        out EnbMixResourceLocation? loadedLocation)
    {
        loadedLocation=null;
        if(RetailCompressionAllowed && files.TryReadCurrentRetailSource(sourceName,true,out var dds,out var ddsLocation) &&
            EnbCurrentRetailDdsSurfaceLoadRuntime.TryLoad(dds,allocation.Reduction,allocation.Width,allocation.Height,
                allocation.Format,levels,pitches,shift))
        {
            loadedLocation=ddsLocation;return EnbCurrentRetailTextureTaskLoadedSource.Dds;
        }
        if(!files.TryReadCurrentRetailSource(sourceName,false,out var tga,out var tgaLocation) ||
            !EnbCurrentRetailTrueColorSurfaceLoadRuntime.TryLoadTga(tga,ordinaryPixels,ordinaryPalette,
                checked((int)allocation.Width),checked((int)allocation.Height),allocation.Format,levels,pitches,shift))
            return EnbCurrentRetailTextureTaskLoadedSource.None;
        loadedLocation=tgaLocation;return EnbCurrentRetailTextureTaskLoadedSource.Tga;
    }

    /// <summary>0094A3F0's ordered request dispatch. The resulting request
    /// precedes device allocation and its actual level-count/lock queries.</summary>
    public bool TrySelectAllocation(ReadOnlySpan<byte> sourceDds,ReadOnlySpan<byte> sourceTga,uint reduction,
        EnbCurrentRetailTextureDeviceCapabilities device,int textureDepth,int displayDepth,
        out EnbCurrentRetailTextureAllocationRequest request)
    {
        if(RetailCompressionAllowed && TrySelectCompressedAllocation(sourceDds,reduction,device,textureDepth,displayDepth,out request))
            return true;
        return TrySelectOrdinaryAllocation(sourceTga,reduction,device,textureDepth,displayDepth,out request);
    }

    /// <summary>0094A890 followed by0094A6F0. Metadata wins over the DDS
    /// header, including its compressed-format rejection. This selects a
    /// request only; source loading and actual allocated level count are separate.</summary>
    public bool TrySelectCompressedAllocation(ReadOnlySpan<byte> sourceDds,uint reduction,
        EnbCurrentRetailTextureDeviceCapabilities device,int textureDepth,int displayDepth,
        out EnbCurrentRetailTextureAllocationRequest request)
    {
        request=default;
        uint width,height,sourceLevels,sourceFormat;
        if(CurrentRetailThumbnail is {} thumbnail)
        {
            if(thumbnail.Format is <20 or >24)return false;
            width=thumbnail.Width>>(int)(reduction&31);
            height=thumbnail.Height>>(int)(reduction&31);
            sourceLevels=thumbnail.MipLevels;
            sourceFormat=thumbnail.Format;
        }
        else
        {
            if(!EnbCurrentRetailDdsSourceState.TryRead(sourceDds,reduction,out var source))return false;
            //0094A8D2 tests the header-created level-size table, not loaded
            //payload+1C. Empty payload therefore does not fail this query.
            width=Math.Max(4u,source!.ReducedWidth);
            height=Math.Max(4u,source.ReducedHeight);
            sourceLevels=source.MipLevels;
            sourceFormat=source.Format;
        }
        var shape=EnbCurrentRetailTextureSurfaceRequirements.SelectCompressedAllocation(
            width,height,sourceLevels,RetailCreationMipLevels,reduction,device.MaximumWidth,device.MaximumHeight);
        int format=EnbCurrentRetailTextureSurfaceRequirements.SelectFormat((int)sourceFormat,
            RetailCompressionAllowed,device.SupportsCompressed,device.SupportedFormatBits,textureDepth,displayDepth);
        request=new(shape.Width,shape.Height,(uint)format,shape.MipLevels,shape.Reduction);
        return true;
    }

    /// <summary>0094A890's ordinary query joined to0094AA80. Retained
    /// metadata wins even for compressed formats; otherwise the complete TGA
    /// header reader controls admission. Creation count is passed unchanged
    /// to the allocator and is independent of source count or dimensions.</summary>
    public bool TrySelectOrdinaryAllocation(ReadOnlySpan<byte> sourceTga,uint reduction,
        EnbCurrentRetailTextureDeviceCapabilities device,int textureDepth,int displayDepth,
        out EnbCurrentRetailTextureAllocationRequest request)
    {
        request=default;
        uint width,height,sourceFormat;
        if(CurrentRetailThumbnail is {} thumbnail)
        {
            width=thumbnail.Width>>(int)(reduction&31);
            height=thumbnail.Height>>(int)(reduction&31);
            sourceFormat=thumbnail.Format;
        }
        else
        {
            if(!EnbCurrentRetailTgaHeaderState.TryRead(sourceTga,out var header))return false;
            //0094A9B6/9CF SAR the signed16-bit dimensions, then MOVSX.
            //A negative dimension cannot define bounded modern storage.
            if(header!.Width<0 || header.Height<0)throw new InvalidDataException("Negative TGA dimensions do not define bounded allocation.");
            width=(uint)(header.Width>>(int)(reduction&31));
            height=(uint)(header.Height>>(int)(reduction&31));
            sourceFormat=header.Format;
        }
        int preferred=EnbCurrentRetailTextureSurfaceRequirements.SelectFormat((int)sourceFormat,
            false,device.SupportsCompressed,device.SupportedFormatBits,textureDepth,displayDepth);
        var shape=EnbCurrentRetailTextureSurfaceRequirements.NormalizeDimensions(width,height,device.MaximumWidth,device.MaximumHeight);
        //0094A321 snapshots the owner's current+54 into task+14;0094AA80
        //uses that snapshot, which may differ from the original request.
        uint currentFormat=CurrentRetailFormat;
        int format=currentFormat==0?preferred:EnbCurrentRetailTextureSurfaceRequirements.SelectFormat((int)currentFormat,
            false,device.SupportsCompressed,device.SupportedFormatBits,textureDepth,displayDepth);
        request=new(shape.Width,shape.Height,(uint)format,RetailCreationMipLevels,reduction);
        return true;
    }

    /// <summary>0048CC6C copies the current format/selector and the mutable
    /// sampling words into its new TextureClass; it retains no sampling owner.</summary>
    public EnbCurrentRetailTextureObjectState CloneForRecolor()
    {
        var copy=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(_sampling,CurrentRetailFormat)
        {
            CurrentRetailCreationMipLevels=RetailCreationMipLevels,
            CurrentRetailThumbnail=CurrentRetailThumbnail,
            CurrentRetailCompressionAllowed=RetailCompressionAllowed,
        });
        copy._sampling=_sampling;
        return copy;
    }

    /// <summary>00458CE6..58CF4 writes both address words on the acquired,
    /// shared TextureClass. The Environment factory supplies one for each.</summary>
    public void ApplyRslEffectPassAddressing()
        => _sampling = _sampling with
        {
            AddressU = EnbRenderTextureAddressMode.Clamp,
            AddressV = EnbRenderTextureAddressMode.Clamp,
        };

    public void ApplyW3dInfo(EnbW3dTextureInfo? info)
    {
        // Absent0033 performs acquisition only. It must not reset a cached
        // object's flags installed by an earlier load.
        if (info is not {} authored) return;
        _sampling = _sampling with
        {
            AddressU = (authored.Attributes & 8) != 0 ? EnbRenderTextureAddressMode.Clamp : EnbRenderTextureAddressMode.Wrap,
            AddressV = (authored.Attributes & 16) != 0 ? EnbRenderTextureAddressMode.Clamp : EnbRenderTextureAddressMode.Wrap,
            CurrentRetailMipOverride = (authored.Attributes & 4) != 0
                ? EnbRenderTextureMipFilter.None : _sampling.CurrentRetailMipOverride,
        };
    }
}

public readonly record struct EnbCurrentRetailTextureAllocationRequest(
    uint Width,uint Height,uint Format,uint RequestedMipLevels,uint Reduction);
