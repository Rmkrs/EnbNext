using EnbNext.Rendering;
using EnbNext.Assets;
using System.Numerics;

namespace EnbNext.Net7.Tests;

public sealed class EnbRingDrawPacketTests
{
    [Fact]
    public void InstalledTexturelessRingSelectsUntexturedGpuPermutation()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1952);
        var ring=Assert.Single(loaded.Rings!.Rings);
        Assert.Null(ring.Texture);
        var packet=EnbRetailRingDrawPacket.Build(ring.Asset,ring.Asset.Sample(0,20)!,ring.Key,null,Matrix4x4.Identity,true,0);
        Assert.True(EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var indexed,out var error),error);
        Assert.All(indexed.Runs,run=>Assert.Equal(0,run.Binding.Semantics.PermutationKey.TextureStageCount));
        Assert.Empty(packet.Material.Source.TextureStages);
    }

    [Fact]
    public void AdditiveDiffuseBakesOpacityIntoRgbWithOpaqueAlpha()
    {
        Assert.Equal(new EnbRenderColor32(63,31,95,255),EnbRetailRingDrawPacket.PackColor(new(.5f,.25f,.75f),.5f,0x14633));
        Assert.Equal(new EnbRenderColor32(0,0,0,255),EnbRetailRingDrawPacket.PackColor(new(.5f,.25f,.75f),0,0x14633));
    }

    [Fact]
    public void OtherDestinationSelectorsKeepSeparateOpacityAndTruncateBytes()
    {
        Assert.Equal(new EnbRenderColor32(127,63,191,127),EnbRetailRingDrawPacket.PackColor(new(.5f,.25f,.75f),.5f,0x14613));
        Assert.Throws<InvalidDataException>(()=>EnbRetailRingDrawPacket.PackColor(new(float.NaN,0,0),1,0x14633));
    }
}
