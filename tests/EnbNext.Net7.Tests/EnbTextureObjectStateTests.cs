using System.Buffers.Binary;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbTextureObjectStateTests
{
    [Fact]
    public void CurrentSurfaceFormatControlsLaterAllocationAndIndependentRecolorCreation()
    {
        var source=new EnbCurrentRetailTextureObjectState(EnbW3dTextureSamplingSemantics.RetailDefault);
        var file=new byte[26];file[2]=2;file[12]=4;file[14]=4;file[16]=24;
        var device=new EnbCurrentRetailTextureDeviceCapabilities(4096,4096,(1u<<3)|(1u<<7),false);
        uint Selected(EnbCurrentRetailTextureObjectState state)
        {
            Assert.True(state.TrySelectOrdinaryAllocation(file,0,device,32,32,out var request));return request.Format;
        }
        Assert.Equal(3u,Selected(source));
        source.PublishSurfaceFormat(7,markReady:false);
        Assert.Equal(0u,source.CurrentRetailFormat);Assert.Equal(3u,Selected(source));
        source.PublishSurfaceFormat(7,markReady:true);
        Assert.Equal(0u,source.RetailCreationFormat);Assert.Equal(7u,source.CurrentRetailFormat);Assert.Equal(7u,Selected(source));
        source.PublishSurfaceFormat(3,markReady:false);
        var copy=source.CloneForRecolor();
        Assert.Equal(7u,copy.RetailCreationFormat);Assert.Equal(7u,Selected(copy));
        copy.PublishSurfaceFormat(3,markReady:true);
        Assert.Equal(3u,Selected(copy));Assert.Equal(7u,Selected(source));
    }



    [Theory]
    [InlineData(3,0,8,14)]
    [InlineData(1,1,8,2)]
    [InlineData(2,0,24,3)]
    [InlineData(2,0,32,2)]
    [InlineData(2,0,16,6)]
    [InlineData(2,0,0,2)]
    public void OrdinaryQueryUsesRawSourceFormatAndRetainsZeroCreationCount(byte type,byte map,byte depth,uint format)
    {
        var file=new byte[26];file[2]=type;file[1]=map;file[12]=13;file[14]=7;file[16]=depth;
        var state=new EnbCurrentRetailTextureObjectState(EnbW3dTextureSamplingSemantics.RetailDefault);
        var device=new EnbCurrentRetailTextureDeviceCapabilities(4096,4096,(1u<<2)|(1u<<3)|(1u<<6)|(1u<<14),true);
        Assert.True(state.TrySelectOrdinaryAllocation(file,1,device,32,32,out var request));
        Assert.Equal(new EnbCurrentRetailTextureAllocationRequest(8,4,format,0,1),request);
        Assert.True(state.TrySelectOrdinaryAllocation(file,33,device,32,32,out var masked));
        Assert.Equal(request with {Reduction=33},masked);
        Assert.False(state.TrySelectOrdinaryAllocation(file[..25],0,device,32,32,out _));
    }

    [Theory]
    [InlineData(0,20,0,3,0)]
    [InlineData(7,20,5,7,5)]
    [InlineData(0,14,1,14,1)]
    public void OrdinaryAllocationMetadataAndCreationFormatWinWithoutCompressedMipCaps(uint creation,uint source,uint count,uint format,uint expectedCount)
    {
        var state=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(
            EnbW3dTextureSamplingSemantics.RetailDefault with {RetailCreationSelector=(byte)count},creation)
        {CurrentRetailThumbnail=new("owner.tga",0,0,1,1,512,4,count,source)});
        var device=new EnbCurrentRetailTextureDeviceCapabilities(64,64,(1u<<2)|(1u<<3)|(1u<<7)|(1u<<14)|(1u<<20),true);
        Assert.True(state.TrySelectOrdinaryAllocation([],0,device,32,32,out var request));
        Assert.Equal(new EnbCurrentRetailTextureAllocationRequest(64,8,format,expectedCount,0),request);
        Assert.Equal(request,Select(state.CloneForRecolor()));
        EnbCurrentRetailTextureAllocationRequest Select(EnbCurrentRetailTextureObjectState copy)
        {
            Assert.True(copy.TrySelectOrdinaryAllocation([],0,device,32,32,out var result));return result;
        }
    }

    private static byte[] DdsHeader(uint width,uint height,uint levels)
    {
        var bytes=new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12),height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16),width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28),levels);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84),0x31545844);
        return bytes;
    }

    [Fact]
    public void AllocationUsesRetainedMetadataBeforeDdsAndFinalizedDeviceFormats()
    {
        var metadata=new EnbCurrentRetailTextureThumbnail("source.tga",1,0,32,32,256,256,4,20);
        var state=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(
            EnbW3dTextureSamplingSemantics.RetailDefault,0){CurrentRetailThumbnail=metadata});
        var device=new EnbCurrentRetailTextureDeviceCapabilities(64,64,(1u<<21)|(1u<<2)|(1u<<3),true);
        Assert.True(state.TrySelectCompressedAllocation(DdsHeader(512,16,9),0,device,32,32,out var request));
        Assert.Equal(new EnbCurrentRetailTextureAllocationRequest(64,64,21,4,2),request);
        Assert.Equal(request,Select(state.CloneForRecolor()));
        Assert.Equal(0,state.Sampling.RetailCreationSelector);
        EnbCurrentRetailTextureAllocationRequest Select(EnbCurrentRetailTextureObjectState copy)
        {
            Assert.True(copy.TrySelectCompressedAllocation([],0,device,32,32,out var selected));
            return selected;
        }
    }

    [Fact]
    public void NonCompressedMetadataRejectsDdsQueryWithoutFallingBackToItsHeader()
    {
        var state=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(
            EnbW3dTextureSamplingSemantics.RetailDefault,0)
        {CurrentRetailThumbnail=new("source.tga",1,0,32,32,256,256,4,3)});
        Assert.False(state.TrySelectCompressedAllocation(DdsHeader(64,64,7),0,
            new(4096,4096,1u<<20,true),32,32,out _));
    }

    [Theory]
    [InlineData(true,20)]
    [InlineData(false,3)]
    public void HeaderOnlyAllocationQueryDoesNotRequireLoadedPayload(bool compression,uint expectedFormat)
    {
        var state=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(
            EnbW3dTextureSamplingSemantics.RetailDefault,0){CurrentRetailCompressionAllowed=compression});
        var header=DdsHeader(64,64,7);
        Assert.True(EnbCurrentRetailDdsSourceState.TryRead(header,1,out var source));
        Assert.False(source!.LoadReturnedSuccess);
        Assert.True(state.TrySelectCompressedAllocation(header,1,new(4096,4096,(1u<<20)|(1u<<3),true),32,32,out var request));
        Assert.Equal(new EnbCurrentRetailTextureAllocationRequest(32,32,expectedFormat,4,1),request);
    }

    [Theory]
    [InlineData(0,false,false)]
    [InlineData(0,true,true)]
    [InlineData(16,false,false)]
    [InlineData(20,false,true)]
    [InlineData(21,false,true)]
    [InlineData(22,false,true)]
    [InlineData(23,false,true)]
    [InlineData(24,false,true)]
    [InlineData(25,false,false)]
    public void CompressionPermissionRetainsConstructorInputExceptCompressedFormats(uint format,bool input,bool expected)
    {
        var source=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(
            EnbW3dTextureSamplingSemantics.RetailDefault,format)
        {CurrentRetailCompressionAllowed=input});
        Assert.Equal(expected,source.RetailCompressionAllowed);
        source.ApplyW3dInfo(new(4|8,0,0));
        Assert.Equal(expected,source.RetailCompressionAllowed);
        Assert.Equal(expected,source.CloneForRecolor().RetailCompressionAllowed);
    }

    [Theory]
    [InlineData(0,4,4)]
    [InlineData(2,4,4)]
    [InlineData(0,1,1)]
    [InlineData(1,4,1)]
    public void ThumbnailAllocationCountDoesNotRewriteConstructorSampling(byte selector,uint metadataLevels,uint expectedLevels)
    {
        var thumbnail=new EnbCurrentRetailTextureThumbnail("SOURCE.DDS",0,0,32,32,256,256,metadataLevels,24);
        var source=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(
            EnbW3dTextureSamplingSemantics.RetailDefault with {RetailCreationSelector=selector},0)
        {CurrentRetailThumbnail=thumbnail});
        Assert.Equal(expectedLevels,source.RetailCreationMipLevels);
        Assert.Equal(selector,source.Sampling.RetailCreationSelector);
        source.ApplyW3dInfo(new(4,0,0));
        Assert.Equal(expectedLevels,source.RetailCreationMipLevels);
        var copy=source.CloneForRecolor();
        Assert.Equal(expectedLevels,copy.RetailCreationMipLevels);
        Assert.Same(thumbnail,copy.CurrentRetailThumbnail);
        Assert.Equal(source.Sampling,copy.Sampling);
    }

    [Fact]
    public void RecolorCopiesSamplingIntoAnIndependentTextureObject()
    {
        var source=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(
            EnbW3dTextureSamplingSemantics.RetailDefault with {RetailCreationSelector=2},0x13));
        source.ApplyW3dInfo(new(4|8,0,0));
        var copy=source.CloneForRecolor();
        Assert.NotSame(source,copy);
        Assert.Equal(source.RetailCreationFormat,copy.RetailCreationFormat);
        Assert.Equal(source.Sampling,copy.Sampling);
        source.ApplyRslEffectPassAddressing();
        Assert.Equal(EnbRenderTextureAddressMode.Wrap,copy.Sampling.AddressV);
        copy.ApplyW3dInfo(new(0,0,0));
        Assert.Equal(EnbRenderTextureAddressMode.Clamp,source.Sampling.AddressU);
        Assert.Equal(EnbRenderTextureAddressMode.Wrap,copy.Sampling.AddressU);
        Assert.Equal(EnbRenderTextureMipFilter.None,copy.Sampling.CurrentRetailMipOverride);
    }

    [Fact]
    public void SharedObjectSamplingDoesNotReplaceBindingAuthoredMetadata()
    {
        var state = new EnbCurrentRetailTextureObjectState(EnbW3dTextureSamplingSemantics.RetailDefault);
        state.ApplyW3dInfo(new(12, 7, 9));
        var authored = EnbW3dTextureSemanticCompiler.Compile(new(0x1000, 3, 5));
        var binding = new EnbW3dRenderTextureBinding(0, "BUMP.TGA", null)
        {
            Sampling = authored,
            CurrentRetailTextureState = state,
        };
        Assert.True(binding.Sampling.LegacyCapabilityFormatRequested);
        Assert.Equal(authored.AuthoredAttributes, binding.Sampling.AuthoredAttributes);
        Assert.Equal(3u, binding.Sampling.AuthoredWord1);
        Assert.Equal(5u, binding.Sampling.AuthoredWord2);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp, binding.Sampling.AddressU);
        Assert.Equal(EnbRenderTextureMipFilter.None, binding.Sampling.CurrentRetailMipOverride);
    }


    [Fact]
    public void RepeatedAcquisitionPreservesCreationAndStickyMipDisable()
    {
        var texture = new EnbCurrentRetailTextureObjectState(EnbW3dTextureSamplingSemantics.RetailDefault);
        texture.ApplyW3dInfo(new(4 | 8, 1, 0));
        Assert.Equal(0, texture.Sampling.RetailCreationSelector);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp, texture.Sampling.AddressU);
        Assert.Equal(EnbRenderTextureAddressMode.Wrap, texture.Sampling.AddressV);
        var retained = texture.Sampling;
        texture.ApplyW3dInfo(null);
        Assert.Equal(retained, texture.Sampling);
        texture.ApplyW3dInfo(new(16, 0, 0));
        Assert.Equal(EnbRenderTextureAddressMode.Wrap, texture.Sampling.AddressU);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp, texture.Sampling.AddressV);
        var binding = new EnbW3dRenderTextureBinding(0, "lights_ALL.tga", null) { Sampling = texture.Sampling };
        var sampler = EnbW3dTextureSamplerCompiler.Compile(binding,
            EnbRenderTextureRuntimeSettings.RetailDefaults, EnbRenderTextureBackendCapabilities.ModernDefault);
        Assert.Equal(EnbRenderTextureMipFilter.None, sampler.MipFilter);
    }

    [Fact]
    public void LaterInfoDoesNotEnableMipsOnSelectorOneTexture()
    {
        var creation = EnbW3dTextureSemanticCompiler.Compile(new EnbW3dTextureInfo(4, 1, 0));
        var texture = new EnbCurrentRetailTextureObjectState(creation);
        texture.ApplyW3dInfo(new(0, 0, 0));
        Assert.Equal(1, texture.Sampling.RetailCreationSelector);
        var binding = new EnbW3dRenderTextureBinding(0, "lights_ALL.tga", null) { Sampling = texture.Sampling };
        Assert.Equal(EnbRenderTextureMipFilter.None, EnbW3dTextureSamplerCompiler.Compile(binding,
            EnbRenderTextureRuntimeSettings.RetailDefaults, EnbRenderTextureBackendCapabilities.ModernDefault).MipFilter);
    }
}
