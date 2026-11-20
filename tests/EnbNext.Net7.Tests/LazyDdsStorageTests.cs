using System.Buffers.Binary;
using System.Runtime.InteropServices;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Rendering.OpenGL;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class LazyDdsStorageTests
{
    [Fact]
    public void CompressedUploadKeepsBlocksAndDecodesOnlyForCpuReaders()
    {
        var file = RedDds(256);
        var binding = new EnbW3dRenderTextureBinding(0,"fixture.dds",null);
        var sampler = EnbW3dTextureSamplerCompiler.Compile(binding,
            EnbRenderTextureRuntimeSettings.RetailDefaults,EnbRenderTextureBackendCapabilities.ModernDefault);
        long before=GC.GetAllocatedBytesForCurrentThread();
        Assert.True(EnbRetailTextureDecoder.TryDecode(file,"fixture.dds",out var image));
        Assert.True(EnbOpenGlStage0TextureUploadPlanner.TryPlanStage(0,binding,image,new(0,1,256,256),sampler,out var upload,out var error),error);
        var mip=Assert.Single(upload.Mips);
        Assert.Equal(256*256*4,mip.RgbaByteLength);
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.True(allocated<128*1024,$"Compressed path allocated {allocated} bytes before any CPU pixel read");
        Assert.True(MemoryMarshal.TryGetArray(image.MipLevels[0].CompressedBlocks,out var source));
        Assert.True(MemoryMarshal.TryGetArray(mip.CompressedBlocks,out var uploaded));
        Assert.Same(source.Array,uploaded.Array);
        Array.Fill(file,(byte)0);
        Assert.Equal(new EnbColor32(255,0,0,255),image.GetPixel(255,255));
        Assert.Equal(new byte[]{255,0,0,255},mip.RgbaBytes.Slice(0,4).ToArray());
        Assert.Equal(image.Pixels.Length*4,mip.RgbaByteLength);
    }

    [Fact]
    public void ConcurrentCpuReadersPublishOneImmutablePixelArray()
    {
        Assert.True(EnbRetailTextureDecoder.TryDecode(RedDds(64),"fixture.dds",out var image));
        var views=new ArraySegment<EnbColor32>[8];
        Parallel.For(0,views.Length,i=>Assert.True(MemoryMarshal.TryGetArray(image.Pixels,out views[i])));
        foreach(var view in views)Assert.Same(views[0].Array,view.Array);
        Assert.All(views[0],p=>Assert.Equal(new EnbColor32(255,0,0,255),p));
    }

    private static byte[] RedDds(int size)
    {
        var file=new byte[128+size*size/2];
        void Set(int offset,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(offset),value);
        Set(0,0x20534444);Set(4,124);Set(8,0x81007);Set(12,(uint)size);Set(16,(uint)size);
        Set(20,(uint)(file.Length-128));Set(28,1);Set(76,32);Set(80,4);Set(84,0x31545844);Set(108,0x1000);
        for(int i=128;i<file.Length;i+=8)BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(i),0xf800);
        return file;
    }
}
