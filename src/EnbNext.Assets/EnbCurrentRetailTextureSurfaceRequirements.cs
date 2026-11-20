using System.Numerics;

namespace EnbNext.Assets;

/// <summary>Current retail's ordinary surface dimension and format selectors.
/// Capabilities and display state belong to the caller's device.</summary>
public static class EnbCurrentRetailTextureSurfaceRequirements
{
    /// <summary>0094A742..878, after0094A890 supplies reduced source dimensions
    /// and its source count. Device bounds are explicit; format selection and
    /// actual allocation remain owned by the caller's device.</summary>
    public static EnbCurrentRetailCompressedTextureAllocation SelectCompressedAllocation(
        uint reducedSourceWidth,uint reducedSourceHeight,uint sourceMipLevels,uint creationMipLevels,
        uint reduction,uint maximumWidth,uint maximumHeight)
    {
        var shape=NormalizeDimensions(reducedSourceWidth,reducedSourceHeight,maximumWidth,maximumHeight);
        if((shape.Width!=reducedSourceWidth || shape.Height!=reducedSourceHeight) && sourceMipLevels>1)
        {
            //Raw shifts mask to31. After32 candidates their dimensions repeat,
            //so a failed complete cycle has no later successful candidate.
            for(uint index=1;index<sourceMipLevels && index<=32;index++)
            {
                uint width=Math.Max(4u,reducedSourceWidth>>(int)(index&31));
                uint height=Math.Max(4u,reducedSourceHeight>>(int)(index&31));
                var candidate=NormalizeDimensions(width,height,maximumWidth,maximumHeight);
                if(candidate.Width==width && candidate.Height==height)
                {
                    shape=candidate;
                    reduction=unchecked(reduction+index);
                    break;
                }
            }
        }
        return new(shape.Width,shape.Height,
            SelectCompressedMipCount(shape.Width,shape.Height,sourceMipLevels,creationMipLevels),reduction);
    }

    /// <summary>0094A833..866 caps compressed levels by both dimensions,
    /// starting at a four-pixel block; a zero creation count means source count.</summary>
    public static uint SelectCompressedMipCount(uint width,uint height,uint sourceMipLevels,uint creationMipLevels)
    {
        //The native doubling loop has no terminating power for larger shapes.
        //Do not turn that unsupported storage size into a guessed level count.
        if(Math.Min(width,height)>0x80000000)throw new ArgumentOutOfRangeException(nameof(width));
        uint levels=creationMipLevels==0?sourceMipLevels:Math.Min(sourceMipLevels,creationMipLevels);
        uint power=4,shapeLevels=1;
        if(width>4)
            while(height>power)
            {
                power*=2;
                shapeLevels++;
                if(power>=width)break;
            }
        return Math.Min(levels,shapeLevels);
    }

    /// <summary>009A0870's DDS source state, before the allocation shape cap.
    /// Its over-reduction branch retains request-1 as the actual shift.</summary>
    public static (uint MipLevels,uint Reduction) SelectDdsSourceMips(uint headerMipLevels,uint requestedReduction)
    {
        uint levels=headerMipLevels==0?1:headerMipLevels;
        uint reduction=requestedReduction;
        if(reduction<levels)levels-=reduction;
        else{levels=1;reduction=unchecked(reduction-1);}
        levels=levels<3?1:levels-2;
        return (levels,reduction);
    }

    /// <summary>0094BC20: TextureClass dimensions and its creation mip count,
    /// rather than mutable sampling flags, control the reduction request.</summary>
    public static int SelectReduction(int width,int height,int creationMipLevels,
        int textureReduction,bool largeTextureCapabilityFallback)
    {
        if(creationMipLevels==1 || width<=32 || height<=32)return 0;
        int reduction=textureReduction;
        if(largeTextureCapabilityFallback && (width>256 || height>256))reduction=unchecked(reduction+1);
        if(creationMipLevels!=0 && creationMipLevels<reduction)reduction=creationMipLevels;
        return reduction;
    }

    /// <summary>00948D30: power-of-two rounding, device bounds, then the
    /// integer aspect-ratio limit. The aspect step follows the bounds step.</summary>
    public static (uint Width,uint Height) NormalizeDimensions(uint width,uint height,uint maximumWidth,uint maximumHeight)
    {
        if(maximumWidth==0 || maximumHeight==0)throw new ArgumentException("Texture device bounds must be positive.");
        uint roundedWidth=BitOperations.RoundUpToPowerOf2(Math.Max(1u,width));
        uint roundedHeight=BitOperations.RoundUpToPowerOf2(Math.Max(1u,height));
        if(roundedWidth==0 || roundedHeight==0)throw new ArgumentOutOfRangeException(nameof(width),"Texture dimensions exceed representable power-of-two storage.");
        roundedWidth=Math.Min(roundedWidth,maximumWidth);roundedHeight=Math.Min(roundedHeight,maximumHeight);
        if(roundedHeight<roundedWidth)
            while(roundedWidth/roundedHeight>8)roundedHeight*=2;
        else
            while(roundedHeight/roundedWidth>8)roundedWidth*=2;
        return (roundedWidth,roundedHeight);
    }

    /// <summary>009689A0. Each support bit corresponds to its native format
    /// number in device+E2; compressed permission is device+DD.</summary>
    public static int SelectFormat(int requestedFormat,bool allowCompressed,bool supportsCompressed,
        uint supportedFormatBits,int displayBits,int currentDisplayBits)
    {
        if(requestedFormat is <0 or >24)throw new ArgumentOutOfRangeException(nameof(requestedFormat));
        bool Supported(int format)=>(supportedFormatBits & (1u<<format))!=0;
        int selected=requestedFormat;
        if(!supportsCompressed || !allowCompressed)
        {
            if(requestedFormat==20 || requestedFormat==1)selected=3;
            else if(requestedFormat is >=21 and <=24)selected=2;
        }
        else if(requestedFormat==20)
        {
            if(!Supported(20) && Supported(21))selected=21;
        }
        else if(requestedFormat is >=21 and <=24 && !Supported(requestedFormat))selected=2;
        else if(requestedFormat==1)selected=3;
        if(currentDisplayBits==16 || displayBits<=16)
        {
            if(selected==2)return 7;
            if(selected is 1 or 3)return 4;
        }
        if(Supported(selected))return selected;
        if(Supported(2))return 2;
        if(Supported(7))return 7;
        if(Supported(3))return 3;
        return 4;
    }
}

public readonly record struct EnbCurrentRetailCompressedTextureAllocation(
    uint Width,uint Height,uint MipLevels,uint Reduction);
