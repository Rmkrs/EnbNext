using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbTextureTaskLoadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthoredAcquisitionPreservesNoMipAndClampOnColdAndNamedCacheHits(bool warm)
    {
        using var registry=new EnbW3dRenderResourceCache(definition=>(definition.LogicalName.ToLowerInvariant(),
            new(EnbW3dTextureSemanticCompiler.Compile(definition.Info),0)));
        using var earlier=warm?registry.AcquireNamedTexture("frame.tga"):null;
        using var authored=registry.AcquireW3dTexture(new("frame.tga",new(28,1,0)));
        if(earlier is not null)Assert.Same(earlier.State,authored.State);
        Assert.Equal(warm?0u:1u,authored.State.RetailCreationMipLevels);
        var binding=new EnbW3dRenderTextureBinding(0,"frame.tga",null){CurrentRetailTextureState=authored.State};
        var sampler=EnbW3dTextureSamplerCompiler.Compile(binding,EnbRenderTextureRuntimeSettings.RetailDefaults,
            EnbRenderTextureBackendCapabilities.ModernDefault);
        Assert.Equal(EnbRenderTextureMipFilter.None,sampler.MipFilter);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp,sampler.AddressU);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp,sampler.AddressV);
        using var named=registry.AcquireNamedTexture("FRAME.TGA");
        Assert.Same(authored.State,named.State);
        Assert.Equal(EnbRenderTextureMipFilter.None,named.State.Sampling.CurrentRetailMipOverride);
    }

    [Fact]
    public void UnshiftedSourcePublicationRetainsOwnerAndRefreshesChangedDeviceSettings()
    {
        if(!OperatingSystem.IsWindows())return;
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-source-owner-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);string path=Path.Combine(directory,"source.xyz");
        try
        {
            var bytes=new byte[18+64*64*3+26];bytes[2]=2;bytes[12]=64;bytes[14]=64;bytes[16]=24;bytes[17]=32;
            for(int i=0;i<64*64;i++)bytes[18+i*3+2]=255;
            File.WriteAllBytes(path,bytes);
            Assert.True(EnbCurrentRetailThumbnailFileSource.TryLoad(directory,directory,out var source));
            var files=new EnbRetailTextureResolver(null,source);
            using var registry=new EnbW3dRenderResourceCache(definition=>(definition.LogicalName.ToLowerInvariant(),
                new(EnbW3dTextureSamplingSemantics.RetailDefault,0)));
            using var retained=registry.AcquireNamedTexture("source.xyz");
            var owner=new EnbCurrentRetailOrdinaryTextureOwner(new(4096,4096,1u<<3,false),32,32,1);
            EnbCurrentRetailEffectMeshHsvRuntime.SetOrdinaryTextureDevice(files,owner,registry.AcquireNamedTexture);
            var first=EnbCurrentRetailEffectMeshHsvRuntime.ResolveSourceTexture(files,"source.xyz",retained.State);
            Assert.Equal(32,first.Image!.Width);
            Assert.Equal(0u,retained.State.RetailCreationFormat);
            Assert.Equal(first.Image.CurrentRetailSurfaceFormat,retained.State.CurrentRetailFormat);
            Assert.Equal(3u,retained.State.CurrentRetailFormat);
            Assert.Same(first,EnbCurrentRetailEffectMeshHsvRuntime.ResolveSourceTexture(files,"SOURCE.XYZ"));
            EnbCurrentRetailEffectMeshHsvRuntime.SetOrdinaryTextureDevice(files,owner with {TextureReduction=3},registry.AcquireNamedTexture);
            var next=EnbCurrentRetailEffectMeshHsvRuntime.ResolveSourceTexture(files,"source.xyz",retained.State);
            Assert.Equal(8,next.Image!.Width);Assert.Equal(32,first.Image.Width);
            File.Delete(path);
            Assert.Same(next,EnbCurrentRetailEffectMeshHsvRuntime.ResolveSourceTexture(files,"source.xyz"));
        }
        finally{File.Delete(path);Directory.Delete(directory);}
    }

    [Fact]
    public void NamedHsvReusesPriorRegistryCreationAndCopiesItsSampler()
    {
        if(!OperatingSystem.IsWindows())return;
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-named-owner-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);string path=Path.Combine(directory,"named.xyz");
        try
        {
            File.WriteAllBytes(path,Tga());
            Assert.True(EnbCurrentRetailThumbnailFileSource.TryLoad(directory,directory,out var source));
            var files=new EnbRetailTextureResolver(null,source);
            bool first=true;
            using var registry=new EnbW3dRenderResourceCache(definition=>
            {
                var sampling=EnbW3dTextureSamplingSemantics.RetailDefault with {RetailCreationSelector=first?(byte)1:(byte)0};
                first=false;return(definition.LogicalName.ToLowerInvariant(),new(sampling,0));
            });
            using var earlier=registry.AcquireRslEffectPassTexture("named.xyz");
            EnbCurrentRetailEffectMeshHsvRuntime.SetOrdinaryTextureDevice(files,
                new(new(4096,4096,(1u<<2)|(1u<<3),false),32,32,3),registry.AcquireNamedTexture);
            var changed=EnbCurrentRetailEffectMeshHsvRuntime.ResolveNamedTexture(files,"named.xyz",new(120,0,0));
            var state=EnbCurrentRetailEffectMeshHsvRuntime.GetRecoloredTextureState(changed)!;
            Assert.NotSame(earlier.State,state);
            Assert.Equal(0u,earlier.State.CurrentRetailFormat);
            Assert.Equal(changed.Image!.CurrentRetailSurfaceFormat,state.CurrentRetailFormat);
            Assert.Equal(3u,state.CurrentRetailFormat);
            Assert.Equal(earlier.State.Sampling,state.Sampling);
            Assert.Equal(1u,state.RetailCreationMipLevels);
            Assert.Single(changed.Image!.MipLevels);
            File.Delete(path);
            Assert.Same(changed,EnbCurrentRetailEffectMeshHsvRuntime.ResolveNamedTexture(files,"NAMED.XYZ",new(120,0,0)));
            Assert.Same(state,EnbCurrentRetailEffectMeshHsvRuntime.GetRecoloredTextureState(changed));
        }
        finally{File.Delete(path);Directory.Delete(directory);}
    }

    [Fact]
    public void RecolorCopiesPublishedSourceFormatAndPublishesOnlyItsOwnAllocation()
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-current-format-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);string path=Path.Combine(directory,"source.xyz");
        try
        {
            File.WriteAllBytes(path,Tga());
            Assert.True(EnbCurrentRetailThumbnailFileSource.TryLoad(directory,directory,out var source));
            var files=new EnbRetailTextureResolver(null,source);
            using var registry=new EnbW3dRenderResourceCache(definition=>(definition.LogicalName.ToLowerInvariant(),
                new(EnbW3dTextureSamplingSemantics.RetailDefault,0)));
            using var original=registry.AcquireNamedTexture("source.xyz");
            EnbCurrentRetailEffectMeshHsvRuntime.SetOrdinaryTextureDevice(files,
                new(new(4096,4096,1u<<3,false),32,32,0),registry.AcquireNamedTexture);
            var published=EnbCurrentRetailEffectMeshHsvRuntime.ResolveSourceTexture(files,"source.xyz",original.State);
            Assert.Equal(3u,published.Image!.CurrentRetailSurfaceFormat);Assert.Equal(3u,original.State.CurrentRetailFormat);
            var recolored=EnbCurrentRetailEffectMeshHsvRuntime.ResolveNamedTexture(files,"source.xyz",new(120,0,0));
            var copy=EnbCurrentRetailEffectMeshHsvRuntime.GetRecoloredTextureState(recolored)!;
            Assert.Equal(3u,copy.RetailCreationFormat);Assert.Equal(recolored.Image!.CurrentRetailSurfaceFormat,copy.CurrentRetailFormat);
            Assert.Equal(0u,original.State.RetailCreationFormat);Assert.Equal(3u,original.State.CurrentRetailFormat);
            Assert.NotSame(original.State,copy);
            //Publishing a later surface to one owner cannot rewrite the other.
            copy.PublishSurfaceFormat(7,markReady:true);
            Assert.Equal(3u,original.State.CurrentRetailFormat);
            Assert.Equal(7u,EnbCurrentRetailEffectMeshHsvRuntime.GetRecoloredTextureState(recolored)!.CurrentRetailFormat);
        }
        finally {File.Delete(path);Directory.Delete(directory);}
    }

    [Theory]
    [InlineData(64,64,0,1,32,6)]
    [InlineData(64,64,99,1,32,6)]
    [InlineData(64,64,1,3,64,1)]
    [InlineData(8,4,0,3,8,4)]
    public void OrdinaryAllocationAppliesReductionOnceAndRetainsDeviceLevelCount(
        int width,int height,uint creation,int reduction,int expectedWidth,int expectedLevels)
    {
        if(!OperatingSystem.IsWindows())return;
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-ordinary-shape-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);string path=Path.Combine(directory,"shape.xyz");
        try
        {
            var bytes=new byte[18+width*height*3+26];bytes[2]=2;bytes[16]=24;bytes[17]=32;
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(12),(short)width);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(14),(short)height);
            for(int pixel=0;pixel<width*height;pixel++)bytes[18+pixel*3+2]=255;
            File.WriteAllBytes(path,bytes);
            Assert.True(EnbCurrentRetailThumbnailFileSource.TryLoad(directory,directory,out var source));
            var files=new EnbRetailTextureResolver(null,source);
            var owner=new EnbCurrentRetailOrdinaryTextureOwner(new(4096,4096,(1u<<2)|(1u<<3),false),32,32,reduction);
            var state=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(
                EnbW3dTextureSamplingSemantics.RetailDefault,0){CurrentRetailCreationMipLevels=creation});
            var image=owner.Load(files,state,"shape.xyz",Vector3.Zero)!.Image!;
            Assert.Equal(expectedWidth,image.Width);Assert.Equal(expectedLevels,image.MipLevels.Count);
            Assert.Equal(new EnbColor32(255,0,0,255),image.GetPixel(0,0));
            Assert.Equal(expectedWidth,EnbTextureUploadMipPlanner.Compile(image,new(3,EnbRenderTextureFilterMode.Trilinear)).Width);
            if(width!=height)
            {
                //0094ACC0 stops at the short dimension; fresh modern storage
                //contains no stale bytes in the extra device-allocated tail.
                Assert.Equal(new EnbColor32(0,0,0,255),image.MipLevels[^1].GetPixel(0,0));
                var excessive=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(
                    EnbW3dTextureSamplingSemantics.RetailDefault,0){CurrentRetailCreationMipLevels=99});
                Assert.Throws<InvalidDataException>(()=>owner.Load(files,excessive,"shape.xyz",Vector3.Zero));
                EnbCurrentRetailEffectMeshHsvRuntime.SetOrdinaryTextureDevice(files,owner);
                var contained=EnbCurrentRetailEffectMeshHsvRuntime.ResolveSourceTexture(files,"shape.xyz",excessive);
                Assert.Equal(EnbTextureResolutionStatus.UnsupportedOrInvalidTexture,contained.Status);
                Assert.Null(contained.Image);
                Assert.Contains("Rectangular",contained.Diagnostic);
            }
        }
        finally{File.Delete(path);Directory.Delete(directory);}
    }

    [Fact]
    public void OrdinaryOwnerPublishesActualSourceThroughNamedHsvCache()
    {
        if(!OperatingSystem.IsWindows())return;
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-ordinary-owner-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string original=Path.Combine(directory,"original.xyz");
        try
        {
            File.WriteAllBytes(original,Tga());
            Assert.True(EnbCurrentRetailThumbnailFileSource.TryLoad(directory,directory,out var source));
            var files=new EnbRetailTextureResolver(null,source);
            var state=new EnbCurrentRetailTextureObjectState(EnbW3dTextureSamplingSemantics.RetailDefault);
            var owner=new EnbCurrentRetailOrdinaryTextureOwner(new(4096,4096,(1u<<2)|(1u<<3),false),32,32,3);
            var prepared=new EnbResolvedTextureResource("original.xyz",EnbTextureResolutionStatus.MissingResource);
            var shifted=files.ResolveCurrentRetailMeshHsv(prepared,new(120,0,0),(name,hsv)=>owner.Load(files,state,name,hsv));
            Assert.Equal(original,shifted.ResourceLocation!.ArchivePath);
            Assert.Equal(EnbTextureFormat.CurrentRetailPackedSurface,shifted.Image!.Format);
            Assert.Equal(3,shifted.Image.MipLevels.Count);
            var expected=EnbCurrentRetailHsvRuntime.ShiftRgb(new(1,0,0),new(120,0,0));
            Assert.Equal((byte)(expected.Y*255),shifted.Image.GetPixel(0,0).G);
            File.Delete(original);
            Assert.Same(shifted,files.ResolveCurrentRetailMeshHsv(prepared,new(120,0,0),
                (_,_)=>throw new InvalidOperationException("Cache hit must precede source opening.")));
            Assert.Equal(0,EnbTextureUploadMipPlanner.Compile(shifted.Image,new(3,EnbRenderTextureFilterMode.Trilinear)).FirstSourceMip);
        }
        finally{File.Delete(original);Directory.Delete(directory);}
    }

    [Fact]
    public void OwnedFileCopyFallsBackToOriginalNameAndRetainsSelectedLocation()
    {
        if(!OperatingSystem.IsWindows())return;
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-texture-owner-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string original=Path.Combine(directory,"original.xyz"),compressed=Path.Combine(directory,"original.dds");
        try
        {
            File.WriteAllBytes(original,Tga());File.WriteAllBytes(compressed,Dds(16));
            Assert.True(EnbCurrentRetailThumbnailFileSource.TryLoad(directory,directory,out var source));
            var files=new EnbRetailTextureResolver(null,source);
            var owner=new EnbCurrentRetailTextureObjectState(EnbW3dTextureSamplingSemantics.RetailDefault);
            var output=Enumerable.Repeat((byte)0xa5,64).ToArray();
            Assert.Equal(EnbCurrentRetailTextureTaskLoadedSource.Dds,owner.CopyAllocatedFiles(files,"original.xyz",
                new(4,4,2,1,0),[output],[16],new byte[48],new byte[1024],Vector3.Zero,out var selected));
            Assert.Equal(compressed,selected!.ArchivePath);Assert.All(output,value=>Assert.Equal(0xa5,value));
            File.Delete(compressed);
            Assert.Equal(EnbCurrentRetailTextureTaskLoadedSource.Tga,owner.CopyAllocatedFiles(files,"original.xyz",
                new(4,4,2,1,0),[output],[16],new byte[48],new byte[1024],Vector3.Zero,out selected));
            Assert.Equal(original,selected!.ArchivePath);
            var image=EnbTextureImage.CreateCurrentRetailColorSurface(2,[(4,4,output,16)]);
            Assert.All(image.Pixels.ToArray(),pixel=>Assert.Equal(new EnbColor32(255,0,0,255),pixel));
        }
        finally{File.Delete(original);File.Delete(compressed);Directory.Delete(directory);}
    }

    [Fact]
    public void AllocatedColorLevelsAreNotReducedAgainDuringGpuPublication()
    {
        var image=EnbTextureImage.CreateCurrentRetailColorSurface(2,
            [(64,64,new byte[64*64*4],64*4),(32,32,new byte[32*32*4],32*4)]);
        var plan=EnbTextureUploadMipPlanner.Compile(image,new(3,EnbRenderTextureFilterMode.Trilinear));
        Assert.Equal(0,plan.FirstSourceMip);
        Assert.Equal(2,plan.UploadMipCount);
        Assert.Equal(64,plan.Width);
    }

    [Theory]
    [InlineData(1,255)]
    [InlineData(2,17)]
    [InlineData(3,255)]
    public void ColorSurfacePublicationRetainsMipPixelsWithoutPaddingOrMutableAliases(uint format,byte alpha)
    {
        int stride=format==1?3:4;
        var top=Enumerable.Repeat((byte)0xa5,24).ToArray();
        var tail=Enumerable.Repeat((byte)0x7b,8).ToArray();
        for(int y=0;y<2;y++)for(int x=0;x<2;x++)
        {
            int p=y*12+x*stride;top[p]=30;top[p+1]=20;top[p+2]=10;
            if(stride==4)top[p+3]=17;
        }
        tail[0]=60;tail[1]=50;tail[2]=40;if(stride==4)tail[3]=17;
        var image=EnbTextureImage.CreateCurrentRetailColorSurface(format,[(2,2,top,12),(1,1,tail,8)]);
        Array.Fill(top,(byte)0);Array.Fill(tail,(byte)0);
        Assert.Equal(format,image.CurrentRetailSurfaceFormat);
        Assert.Equal(2,image.MipLevels.Count);
        Assert.All(image.MipLevels[0].Pixels.ToArray(),pixel=>Assert.Equal(new EnbColor32(10,20,30,alpha),pixel));
        Assert.Equal(new EnbColor32(40,50,60,alpha),image.MipLevels[1].GetPixel(0,0));
    }

    private static byte[] Dds(int payload)
    {
        var file=new byte[128+payload];
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4),124);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12),4);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16),4);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28),3);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(84),0x32545844); //DXT2 generic noWrite.
        return file;
    }

    private static byte[] Tga()
    {
        var file=new byte[66];file[2]=2;file[12]=4;file[14]=4;file[16]=24;file[17]=32;
        for(int pixel=0;pixel<16;pixel++)file[18+pixel*3+2]=255;
        return file;
    }



    [Fact]
    public void CompressedRequestFailureFallsThroughToOrdinaryButItsSuccessfulQueryWins()
    {
        var state=new EnbCurrentRetailTextureObjectState(EnbW3dTextureSamplingSemantics.RetailDefault);
        var caps=new EnbCurrentRetailTextureDeviceCapabilities(4096,4096,(1u<<2)|(1u<<3)|(1u<<21),true);
        Assert.True(state.TrySelectAllocation([],Tga(),0,caps,32,32,out var ordinary));
        Assert.Equal(new EnbCurrentRetailTextureAllocationRequest(4,4,3,0,0),ordinary);
        Assert.True(state.TrySelectAllocation(Dds(0),[],0,caps,32,32,out var compressed));
        Assert.Equal(new EnbCurrentRetailTextureAllocationRequest(4,4,21,1,0),compressed);
    }
}
