using System.Buffers.Binary;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4JTextureRuntimeSemanticsTests
{
    [Theory]
    [InlineData(-1, -1, 0, EnbRenderTextureFilterMode.Bilinear)]
    [InlineData(0, 0, 0, EnbRenderTextureFilterMode.Bilinear)]
    [InlineData(2, 1, 2, EnbRenderTextureFilterMode.Trilinear)]
    [InlineData(3, 2, 3, EnbRenderTextureFilterMode.Anisotropic)]
    [InlineData(99, 99, 3, EnbRenderTextureFilterMode.Anisotropic)]
    public void RetailTextureRuntimeSettingsClampCurrentRegistryDomain(
        int reduction,
        int filter,
        int expectedReduction,
        EnbRenderTextureFilterMode expectedFilter)
    {
        EnbRenderTextureRuntimeSettings settings =
            EnbRenderTextureRuntimeSettings.FromRetailValues(reduction, filter);

        Assert.Equal(expectedReduction, settings.TextureReduction);
        Assert.Equal(expectedFilter, settings.TextureFilter);
    }

    [Fact]
    public void RuntimeCompilersReapplyRetailClampToDirectlyConstructedSettings()
    {
        EnbRenderTextureRuntimeSettings invalid = new(99, (EnbRenderTextureFilterMode)99);
        EnbW3dTextureSamplerSemantics sampler = EnbW3dTextureSamplerCompiler.Compile(
            Binding("PAINT.TGA", EnbW3dTextureSamplingSemantics.RetailDefault),
            invalid,
            EnbRenderTextureBackendCapabilities.ModernDefault);

        Assert.Equal(EnbRenderTextureMinMagFilter.Anisotropic, sampler.MinFilter);
        Assert.Equal(EnbRenderTextureMipFilter.Linear, sampler.MipFilter);

        ushort[] colors = Enumerable.Repeat((ushort)0xFFFF, 7).ToArray();
        byte[] dds = BuildDxt1MipChain(64, 64, 7, colors);
        Assert.True(EnbRetailTextureDecoder.TryDecode(dds, "CHAIN.DDS", out EnbTextureImage image));

        EnbTextureUploadMipPlan plan = EnbTextureUploadMipPlanner.Compile(image, invalid);
        Assert.Equal(3, plan.FirstSourceMip);
    }

    [Theory]
    [InlineData(EnbRenderTextureFilterMode.Bilinear, EnbRenderTextureMinMagFilter.Linear, EnbRenderTextureMipFilter.Point, 1)]
    [InlineData(EnbRenderTextureFilterMode.Trilinear, EnbRenderTextureMinMagFilter.Linear, EnbRenderTextureMipFilter.Linear, 1)]
    [InlineData(EnbRenderTextureFilterMode.Anisotropic, EnbRenderTextureMinMagFilter.Anisotropic, EnbRenderTextureMipFilter.Linear, 2)]
    public void OrdinaryW3dTextureUsesCurrentCapableDeviceFilterTable(
        EnbRenderTextureFilterMode filterMode,
        EnbRenderTextureMinMagFilter expectedMinMag,
        EnbRenderTextureMipFilter expectedMip,
        int expectedAnisotropy)
    {
        EnbW3dRenderTextureBinding binding = Binding(
            "PAINT.TGA",
            EnbW3dTextureSamplingSemantics.RetailDefault);

        EnbW3dTextureSamplerSemantics compiled = EnbW3dTextureSamplerCompiler.Compile(
            binding,
            new EnbRenderTextureRuntimeSettings(0, filterMode),
            EnbRenderTextureBackendCapabilities.ModernDefault);

        Assert.Equal(expectedMinMag, compiled.MinFilter);
        Assert.Equal(expectedMinMag, compiled.MagFilter);
        Assert.Equal(expectedMip, compiled.MipFilter);
        Assert.Equal(expectedAnisotropy, compiled.MaximumAnisotropy);
        Assert.False(compiled.UsesPlusNameFilterOverride);
    }

    [Fact]
    public void AnisotropicRetailModeFallsBackToLinearWhenBackendCannotProvideIt()
    {
        EnbW3dTextureSamplerSemantics compiled = EnbW3dTextureSamplerCompiler.Compile(
            Binding("PAINT.TGA", EnbW3dTextureSamplingSemantics.RetailDefault),
            new EnbRenderTextureRuntimeSettings(0, EnbRenderTextureFilterMode.Anisotropic),
            new EnbRenderTextureBackendCapabilities(false));

        Assert.Equal(EnbRenderTextureMinMagFilter.Linear, compiled.MinFilter);
        Assert.Equal(EnbRenderTextureMinMagFilter.Linear, compiled.MagFilter);
        Assert.Equal(EnbRenderTextureMipFilter.Linear, compiled.MipFilter);
        Assert.Equal(1, compiled.MaximumAnisotropy);
    }

    [Fact]
    public void PlusTextureNameUsesFixedLinearPointSamplerIndependentOfGlobalMode()
    {
        EnbW3dTextureSamplingSemantics sampling = EnbW3dTextureSamplingSemantics.RetailDefault with
        {
            AddressU = EnbRenderTextureAddressMode.Clamp,
            AddressV = EnbRenderTextureAddressMode.Wrap,
        };

        EnbW3dTextureSamplerSemantics compiled = EnbW3dTextureSamplerCompiler.Compile(
            Binding("FX+MASK.TGA", sampling),
            new EnbRenderTextureRuntimeSettings(0, EnbRenderTextureFilterMode.Anisotropic),
            EnbRenderTextureBackendCapabilities.ModernDefault);

        Assert.True(compiled.UsesPlusNameFilterOverride);
        Assert.Equal(EnbRenderTextureMinMagFilter.Linear, compiled.MinFilter);
        Assert.Equal(EnbRenderTextureMinMagFilter.Linear, compiled.MagFilter);
        Assert.Equal(EnbRenderTextureMipFilter.Point, compiled.MipFilter);
        Assert.Equal(1, compiled.MaximumAnisotropy);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp, compiled.AddressU);
        Assert.Equal(EnbRenderTextureAddressMode.Wrap, compiled.AddressV);
    }

    [Fact]
    public void CreationSelectorOneDisablesMipFilterEvenForPlusName()
    {
        EnbW3dTextureSamplingSemantics sampling = EnbW3dTextureSamplingSemantics.RetailDefault with
        {
            RetailCreationSelector = 1,
        };

        EnbW3dTextureSamplerSemantics compiled = EnbW3dTextureSamplerCompiler.Compile(
            Binding("FX+MASK.TGA", sampling),
            new EnbRenderTextureRuntimeSettings(0, EnbRenderTextureFilterMode.Anisotropic),
            EnbRenderTextureBackendCapabilities.ModernDefault);

        Assert.Equal(EnbRenderTextureMipFilter.None, compiled.MipFilter);
        Assert.Equal(EnbRenderTextureMinMagFilter.Linear, compiled.MinFilter);
    }

    [Fact]
    public void Dxt1DecoderPreservesEveryAuthoredMipLevel()
    {
        byte[] dds = BuildDxt1MipChain(
            8,
            8,
            4,
            0xF800, // red
            0x07E0, // green
            0x001F, // blue
            0xFFFF); // white

        Assert.True(EnbRetailTextureDecoder.TryDecode(dds, "CHAIN.DDS", out EnbTextureImage image));
        Assert.Equal(4, image.MipLevels.Count);
        Assert.Equal((8, 8), (image.MipLevels[0].Width, image.MipLevels[0].Height));
        Assert.Equal((4, 4), (image.MipLevels[1].Width, image.MipLevels[1].Height));
        Assert.Equal((2, 2), (image.MipLevels[2].Width, image.MipLevels[2].Height));
        Assert.Equal((1, 1), (image.MipLevels[3].Width, image.MipLevels[3].Height));
        Assert.True(image.MipLevels[0].GetPixel(0, 0).R > 240);
        Assert.True(image.MipLevels[1].GetPixel(0, 0).G > 240);
        Assert.True(image.MipLevels[2].GetPixel(0, 0).B > 240);
        Assert.Equal(new EnbColor32(255, 255, 255, 255), image.MipLevels[3].GetPixel(0, 0));
    }

    [Fact]
    public void TextureReductionSelectsAuthoredBaseMipAndOmitsRetailDxtTailLevels()
    {
        byte[] dds = BuildDxt1MipChain(
            64,
            64,
            7,
            0xF800, 0x07E0, 0x001F, 0xFFFF, 0xFFE0, 0xF81F, 0x07FF);
        Assert.True(EnbRetailTextureDecoder.TryDecode(dds, "CHAIN.DDS", out EnbTextureImage image));

        EnbTextureUploadMipPlan plan = EnbTextureUploadMipPlanner.Compile(
            image,
            EnbRenderTextureRuntimeSettings.FromRetailValues(2, 0));

        Assert.Equal(2, plan.FirstSourceMip);
        Assert.Equal(16, plan.Width);
        Assert.Equal(16, plan.Height);
        Assert.Equal(3, plan.UploadMipCount); // 16, 8, 4; retail drops 2 and 1.
        Assert.True(plan.UsesReducedBase);
    }

    [Theory]
    [InlineData(64,64,7,2,3,2,2)]
    [InlineData(64,64,7,3,3,3,2)]
    [InlineData(64,64,7,4,0,0,4)]
    [InlineData(64,8,7,0,0,0,2)]
    [InlineData(8,4,4,0,0,0,1)]
    public void DdsUploadUsesGeneralCreationCountAndShorterBlockDimension(
        int width,int height,int levels,uint creation,int reduction,int expectedFirst,int expectedCount)
    {
        var bytes=BuildDxt1MipChain(width,height,levels,
            Enumerable.Repeat((ushort)0xf800,levels).ToArray());
        Assert.True(EnbRetailTextureDecoder.TryDecode(bytes,"counts.dds",out var image));
        var plan=EnbTextureUploadMipPlanner.Compile(image,new(reduction,EnbRenderTextureFilterMode.Trilinear),
            retailCreationMipLevels:creation);
        Assert.Equal(expectedFirst,plan.FirstSourceMip);
        Assert.Equal(expectedCount,plan.UploadMipCount);
    }

    [Fact]
    public void PartialDdsChainUsesLastAuthoredMipInsteadOfRetailMetadataOverReduction()
    {
        var bytes=BuildDxt1MipChain(64,64,2,0xf800,0x07e0);
        var metadata=new EnbCurrentRetailTextureThumbnail("partial.dds",0,0,8,8,64,64,2,20);
        var state=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(
            EnbW3dTextureSamplingSemantics.RetailDefault,0){CurrentRetailThumbnail=metadata});
        int reduction=EnbCurrentRetailTextureSurfaceRequirements.SelectReduction(64,64,
            (int)state.RetailCreationMipLevels,3,false);
        Assert.Equal(2,reduction);
        Assert.True(state.TrySelectCompressedAllocation(bytes,(uint)reduction,new(4096,4096,1u<<20,true),32,32,out var nativeRequest));
        Assert.Equal(16u,nativeRequest.Width);Assert.Equal(2u,nativeRequest.RequestedMipLevels);
        Assert.True(EnbCurrentRetailDdsSourceState.TryRead(bytes,(uint)reduction,out var nativeSource));
        Assert.Equal(32u,nativeSource!.ReducedWidth);Assert.Equal(1u,nativeSource.MipLevels);
        var incoming=Enumerable.Repeat((byte)0xa5,128).ToArray();
        Assert.False(EnbCurrentRetailCompressedSurfaceLoadRuntime.LoadMip(bytes,nativeSource,0,20,16,16,incoming,System.Numerics.Vector3.Zero));
        Assert.All(incoming,value=>Assert.Equal(0xa5,value));
        Assert.True(EnbRetailTextureDecoder.TryDecode(bytes,"partial.dds",out var image));
        var corrected=EnbTextureUploadMipPlanner.Compile(image,new(3,EnbRenderTextureFilterMode.Trilinear),
            retailCreationMipLevels:state.RetailCreationMipLevels);
        Assert.Equal(new EnbTextureUploadMipPlan(1,1,32,32),corrected);
        Assert.Equal(new EnbColor32(0,255,0,255),image.MipLevels[corrected.FirstSourceMip].GetPixel(0,0));
    }

    [Fact]
    public void AllocatedDdsOwnerUsesMetadataAndDeviceBoundsBeforePublication()
    {
        var bytes=BuildDxt1MipChain(64,64,7,0xf800,0x07e0,0x001f,0xffff,0xffe0,0xf81f,0x07ff);
        var metadata=new EnbCurrentRetailTextureThumbnail("owned.dds",0,0,8,8,64,64,4,20);
        var state=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(
            EnbW3dTextureSamplingSemantics.RetailDefault,0){CurrentRetailThumbnail=metadata});
        var image=EnbCurrentRetailDdsTextureOwner.Load(bytes,state,new(16,16,1u<<20,true),32,32,0,System.Numerics.Vector3.Zero)!;
        Assert.Equal(16,image.Width);Assert.Equal(3,image.MipLevels.Count);
        Assert.Equal(new EnbColor32(0,0,255,255),image.GetPixel(0,0));
        Assert.Equal(new EnbColor32(255,255,0,255),image.MipLevels[2].GetPixel(0,0));
        Assert.Equal(new EnbTextureUploadMipPlan(0,3,16,16),EnbTextureUploadMipPlanner.Compile(image,new(3,EnbRenderTextureFilterMode.Trilinear)));
        var mismatched=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(
            EnbW3dTextureSamplingSemantics.RetailDefault,0){CurrentRetailThumbnail=metadata with {Width=32,Height=32}});
        var untouched=EnbCurrentRetailDdsTextureOwner.Load(bytes,mismatched,new(4096,4096,1u<<20,true),32,32,0,System.Numerics.Vector3.Zero)!;
        Assert.Equal(32,untouched.Width);
        Assert.All(untouched.Pixels.ToArray(),pixel=>Assert.Equal(new EnbColor32(0,0,0,255),pixel));
    }

    [Fact]
    public void AllocatedDdsOwnerRetainsPartialChainAndAuthoredPaletteCorrections()
    {
        var bytes=BuildDxt1MipChain(64,64,2,0xf800,0x07e0);
        var metadata=new EnbCurrentRetailTextureThumbnail("partial.dds",0,0,8,8,64,64,2,20);
        var state=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(
            EnbW3dTextureSamplingSemantics.RetailDefault,0){CurrentRetailThumbnail=metadata});
        var image=EnbCurrentRetailDdsTextureOwner.Load(bytes,state,new(4096,4096,1u<<20,true),32,32,3,System.Numerics.Vector3.Zero)!;
        Assert.Equal(32,image.Width);Assert.Single(image.MipLevels);
        Assert.Equal(new EnbColor32(0,255,0,255),image.GetPixel(0,0));
        var opaque=BuildDxt1MipChain(4,4,1,0xf800);
        BinaryPrimitives.WriteUInt16LittleEndian(opaque.AsSpan(130),0x07e0);
        BinaryPrimitives.WriteUInt32LittleEndian(opaque.AsSpan(132),uint.MaxValue);
        var shift=new System.Numerics.Vector3(120,0,0);
        Assert.True(EnbDdsTexture.TryDecodeDxt1Hsv(opaque,shift,out var expected));
        var allocated=EnbCurrentRetailAllocatedDdsImage.Create(opaque,0,4,4,20,1,shift);
        Assert.Equal(expected.MipLevels[0].Pixels.ToArray(),allocated.Pixels.ToArray());
        Assert.All(allocated.Pixels.ToArray(),pixel=>Assert.Equal(255,pixel.A));
    }

    [Fact]
    public void DdsSquareDeviceAlignmentDoesNotRewriteTaskCopyDimensions()
    {
        var file=BuildDxt1MipChain(4,4,1,0xf800);
        var metadata=new EnbCurrentRetailTextureThumbnail("small.dds",0,0,2,2,2,2,1,20);
        EnbCurrentRetailTextureObjectState State(EnbCurrentRetailTextureThumbnail entry)=>new(
            new EnbW3dTextureCreationRequest(EnbW3dTextureSamplingSemantics.RetailDefault,0){CurrentRetailThumbnail=entry});
        var device=new EnbCurrentRetailTextureDeviceCapabilities(4096,4096,1u<<20,true);
        var image=EnbCurrentRetailDdsTextureOwner.Load(file,State(metadata),device,32,32,0,System.Numerics.Vector3.Zero)!;
        Assert.Equal(4,image.Width);Assert.Single(image.MipLevels);
        Assert.All(image.Pixels.ToArray(),pixel=>Assert.Equal(new EnbColor32(0,0,0,255),pixel));
        Assert.Throws<InvalidDataException>(()=>EnbCurrentRetailDdsTextureOwner.Load(file,
            State(metadata with {Width=8}),device,32,32,0,System.Numerics.Vector3.Zero));
    }

    [Fact]
    public void AllocatedDdsZeroByteCopyDoesNotReadOriginalBlocks()
    {
        var file=BuildDxt1MipChain(1,1,1,0xf800);
        var plain=EnbCurrentRetailAllocatedDdsImage.Create(file,0,4,4,20,1,System.Numerics.Vector3.Zero);
        Assert.All(plain.Pixels.ToArray(),pixel=>Assert.Equal(new EnbColor32(0,0,0,255),pixel));
        var shifted=EnbCurrentRetailAllocatedDdsImage.Create(file,0,4,4,20,1,new(120,0,0));
        // Native HSV expands red5 to248 before the six-bit green repack:
        // 248>>2=62, whose decoded six-bit value is251.
        Assert.All(shifted.Pixels.ToArray(),pixel=>Assert.Equal(new EnbColor32(0,251,0,255),pixel));
    }

    [Fact]
    public void AllocatedDxt5PreservesAlphaWhileShiftingEndpoints()
    {
        var file=new byte[144];BuildDxt1MipChain(4,4,1,0xf800).AsSpan(0,128).CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(84),0x35545844);
        file[128]=128;file[129]=32;
        for(int i=130;i<136;i++)file[i]=0xe4;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(136),0xf800);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(138),0x07e0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(140),0xe4e4e4e4);
        var shift=new System.Numerics.Vector3(120,0,0);
        Assert.True(EnbCurrentRetailHsvRuntime.TryRecolorDds(file,shift,out var shifted));
        Assert.True(EnbRetailTextureDecoder.TryDecode(shifted,"alpha.dds",out var expected));
        var image=EnbCurrentRetailAllocatedDdsImage.Create(file,0,4,4,24,1,shift);
        Assert.Equal(expected.Pixels.ToArray(),image.Pixels.ToArray());
        Assert.True(image.Pixels.ToArray().Select(pixel=>pixel.A).Distinct().Count()>2);
    }

    [Fact]
    public void DdsDeviceFormatSelectionPublishesScalarAndOpaqueConversionConsumers()
    {
        var file=BuildDxt1MipChain(4,4,1,0xf800);
        var state=new EnbCurrentRetailTextureObjectState(EnbW3dTextureSamplingSemantics.RetailDefault);
        var scalar=EnbCurrentRetailDdsTextureOwner.Load(file,state,new(4096,4096,1u<<3,false),32,32,0,new(120,0,0))!;
        Assert.Equal(3u,scalar.CurrentRetailSurfaceFormat);
        Assert.All(scalar.Pixels.ToArray(),pixel=>Assert.Equal(new EnbColor32(0,248,0,255),pixel));
        var ordinaryMetadata=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(
            EnbW3dTextureSamplingSemantics.RetailDefault,0)
            {CurrentRetailThumbnail=new("scalar.tga",0,0,4,4,4,4,1,2)});
        var ordinaryAllocation=EnbCurrentRetailDdsTextureOwner.Load(file,ordinaryMetadata,
            new(4096,4096,(1u<<2)|(1u<<20),true),32,32,0,new(120,0,0))!;
        Assert.Equal(2u,ordinaryAllocation.CurrentRetailSurfaceFormat);
        Assert.All(ordinaryAllocation.Pixels.ToArray(),pixel=>Assert.Equal(new EnbColor32(0,248,0,255),pixel));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(128),0);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(130),0xffff);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(132),uint.MaxValue);
        var opaque=EnbCurrentRetailDdsTextureOwner.Load(file,state,new(4096,4096,1u<<21,true),32,32,0,System.Numerics.Vector3.Zero)!;
        Assert.Equal(21u,opaque.CurrentRetailSurfaceFormat);
        Assert.All(opaque.Pixels.ToArray(),pixel=>Assert.Equal(new EnbColor32(170,170,170,255),pixel));
    }

    [Fact]
    public void AllocatedDxt3CopiesWithoutHsvAndContainsNativeNoWriteWithHsv()
    {
        var file=new byte[144];BuildDxt1MipChain(4,4,1,0xf800).AsSpan(0,128).CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(84),0x33545844);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(128),ulong.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(136),0xf800);
        var state=new EnbCurrentRetailTextureObjectState(EnbW3dTextureSamplingSemantics.RetailDefault);
        var device=new EnbCurrentRetailTextureDeviceCapabilities(4096,4096,1u<<22,true);
        var plain=EnbCurrentRetailDdsTextureOwner.Load(file,state,device,32,32,0,System.Numerics.Vector3.Zero)!;
        Assert.All(plain.Pixels.ToArray(),pixel=>Assert.Equal(new EnbColor32(255,0,0,255),pixel));
        var shifted=EnbCurrentRetailDdsTextureOwner.Load(file,state,device,32,32,0,new(120,0,0))!;
        Assert.All(shifted.Pixels.ToArray(),pixel=>Assert.Equal(new EnbColor32(0,0,0,0),pixel));
        var scalar=EnbCurrentRetailDdsTextureOwner.Load(file,state,new(4096,4096,1u<<2,false),32,32,0,System.Numerics.Vector3.Zero)!;
        Assert.All(scalar.Pixels.ToArray(),pixel=>Assert.Equal(new EnbColor32(0,0,0,0),pixel));
    }

    [Fact]
    public void TextureReductionDoesNotReduceSingleMipOrSmallTextures()
    {
        byte[] single = BuildDxt1MipChain(64, 64, 1, 0xF800);
        Assert.True(EnbRetailTextureDecoder.TryDecode(single, "SINGLE.DDS", out EnbTextureImage singleImage));
        EnbTextureUploadMipPlan singlePlan = EnbTextureUploadMipPlanner.Compile(
            singleImage,
            EnbRenderTextureRuntimeSettings.FromRetailValues(3, 0));
        Assert.Equal(0, singlePlan.FirstSourceMip);

        byte[] small = BuildDxt1MipChain(32, 32, 6, 0xF800, 0x07E0, 0x001F, 0xFFFF, 0xFFE0, 0xF81F);
        Assert.True(EnbRetailTextureDecoder.TryDecode(small, "SMALL.DDS", out EnbTextureImage smallImage));
        EnbTextureUploadMipPlan smallPlan = EnbTextureUploadMipPlanner.Compile(
            smallImage,
            EnbRenderTextureRuntimeSettings.FromRetailValues(3, 0));
        Assert.Equal(0, smallPlan.FirstSourceMip);
        Assert.Equal(4, smallPlan.UploadMipCount); // 32, 16, 8, 4.
    }

    [Fact]
    public void OneLevelCreationRetainsAuthoredBaseAndDisablesReducedMipAllocation()
    {
        byte[] dds=BuildDxt1MipChain(64,64,7,0xF800,0x07E0,0x001F,0xFFFF,0xFFE0,0xF81F,0x07FF);
        Assert.True(EnbRetailTextureDecoder.TryDecode(dds,"ONE.DDS",out var image));
        var settings=EnbRenderTextureRuntimeSettings.FromRetailValues(3,2);
        var plan=EnbTextureUploadMipPlanner.Compile(image,settings,
            applyLegacyLargeTextureCapabilityFallback:true,retailCreationSelector:1);
        Assert.Equal(new EnbTextureUploadMipPlan(0,1,64,64),plan);
        var metadataPlan=EnbTextureUploadMipPlanner.Compile(image,settings,
            applyLegacyLargeTextureCapabilityFallback:true,retailCreationSelector:0,retailCreationMipLevels:1);
        Assert.Equal(plan,metadataPlan);
        var sampling=EnbW3dTextureSamplerCompiler.Compile(Binding("metadata.dds",
            EnbW3dTextureSemanticCompiler.Compile(null)),settings,EnbRenderTextureBackendCapabilities.ModernDefault);
        Assert.Equal(EnbRenderTextureMipFilter.Linear,sampling.MipFilter);
        //A later NoMip sampling write is independent of creation allocation.
        Assert.Equal(3,EnbTextureUploadMipPlanner.Compile(image,settings).FirstSourceMip);
    }

    [Fact]
    public void LegacyLargeTextureCapabilityFallbackIsExplicitNotAUserSetting()
    {
        ushort[] colors = Enumerable.Repeat((ushort)0xFFFF, 10).ToArray();
        byte[] dds = BuildDxt1MipChain(512, 512, 10, colors);
        Assert.True(EnbRetailTextureDecoder.TryDecode(dds, "LARGE.DDS", out EnbTextureImage image));

        EnbRenderTextureRuntimeSettings settings = EnbRenderTextureRuntimeSettings.FromRetailValues(1, 0);
        EnbTextureUploadMipPlan modern = EnbTextureUploadMipPlanner.Compile(image, settings);
        EnbTextureUploadMipPlan legacyFallback = EnbTextureUploadMipPlanner.Compile(
            image,
            settings,
            applyLegacyLargeTextureCapabilityFallback: true);

        Assert.Equal(1, modern.FirstSourceMip);
        Assert.Equal(2, legacyFallback.FirstSourceMip);
    }

    [Theory]
    [InlineData(1,1)]
    [InlineData(4,4)]
    [InlineData(16,16)]
    [InlineData(32,16)]
    public void EnhancedSceneSamplerUsesAvailableAnisotropy(int limit,int expected)
    {
        var binding=Binding("hull.dds",EnbW3dTextureSamplingSemantics.RetailDefault);
        var baseline=EnbW3dTextureSamplerCompiler.Compile(binding,EnbRenderTextureRuntimeSettings.RetailDefaults,EnbRenderTextureBackendCapabilities.ModernDefault);
        var enhanced=EnbW3dTextureSamplerCompiler.ApplySceneAnisotropy(binding,baseline,6,false,limit);
        Assert.Equal(expected,enhanced.MaximumAnisotropy);
        Assert.Equal(limit>1?EnbRenderTextureMipFilter.Linear:baseline.MipFilter,enhanced.MipFilter);
        Assert.Equal(baseline.AddressU,enhanced.AddressU);Assert.Equal(baseline.AddressV,enhanced.AddressV);
    }
    [Theory]
    [InlineData("blend")]
    [InlineData("single-mip")]
    [InlineData("plus")]
    [InlineData("no-mip")]
    [InlineData("point")]
    [InlineData("override")]
    public void EnhancedSceneSamplerPreservesAuthoredExceptions(string kind)
    {
        var binding=Binding(kind=="plus"?"hull+.dds":"hull.dds",EnbW3dTextureSamplingSemantics.RetailDefault);
        if(kind=="override")binding=binding with {Sampling=binding.Sampling with {CurrentRetailMipOverride=EnbRenderTextureMipFilter.Point}};
        var sampler=EnbW3dTextureSamplerCompiler.Compile(binding,EnbRenderTextureRuntimeSettings.RetailDefaults,EnbRenderTextureBackendCapabilities.ModernDefault);
        if(kind=="no-mip")sampler=sampler with {MipFilter=EnbRenderTextureMipFilter.None};
        if(kind=="point")sampler=sampler with {MinFilter=EnbRenderTextureMinMagFilter.Point};
        Assert.Equal(sampler,EnbW3dTextureSamplerCompiler.ApplySceneAnisotropy(binding,sampler,kind=="single-mip"?1:6,kind=="blend",16));
    }
    private static EnbW3dRenderTextureBinding Binding(
        string logicalName,
        EnbW3dTextureSamplingSemantics sampling)
        => new(0, logicalName, null) { Sampling = sampling };

    private static byte[] BuildDxt1MipChain(
        int width,
        int height,
        int mipCount,
        params ushort[] colors)
    {
        Assert.Equal(mipCount, colors.Length);
        var payload = new List<byte>();
        int mipWidth = width;
        int mipHeight = height;
        Span<byte> encoded = stackalloc byte[8];
        for (int level = 0; level < mipCount; level++)
        {
            int blocks = ((mipWidth + 3) / 4) * ((mipHeight + 3) / 4);
            for (int block = 0; block < blocks; block++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(encoded[..2], colors[level]);
                BinaryPrimitives.WriteUInt16LittleEndian(encoded.Slice(2, 2), (ushort)0);
                BinaryPrimitives.WriteUInt32LittleEndian(encoded.Slice(4, 4), 0u);
                payload.AddRange(encoded.ToArray());
            }
            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        byte[] bytes = new byte[128 + payload.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x20534444u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0x000A_1007u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), checked((uint)height));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), checked((uint)(((width + 3) / 4) * 8)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), checked((uint)mipCount));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 4u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84, 4), 0x31545844u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), mipCount > 1 ? 0x0040_1008u : 0x1000u);
        payload.CopyTo(bytes, 128);
        return bytes;
    }
}
