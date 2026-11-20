using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStandaloneRingTests
{
    [Theory]
    [InlineData(1196)] [InlineData(1197)] [InlineData(1198)] [InlineData(1820)] [InlineData(1835)]
    [InlineData(1577)] [InlineData(1578)] [InlineData(1579)] [InlineData(1580)] [InlineData(1581)] [InlineData(1582)]
    [InlineData(1812)] [InlineData(1813)]
    public void StandaloneOwnerUsesSharedClockGateAndDirectTransform(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(assets,id,new Vector3(120,.1f,0));
        var assembly=Assert.IsType<EnbRetailRingAssembly>(loaded.Rings);
        Assert.Null(loaded.Resource);Assert.Null(assembly.Resource);Assert.Null(assembly.Emitters);
        Assert.Null(assembly.Animation);Assert.Null(assembly.AnimationFlags);
        var binding=Assert.Single(assembly.Rings);
        Assert.Equal(EnbCurrentRetailAssetVariantName.CreateTexture(binding.Asset.Header.Texture,new(120,.1f,0)),binding.Texture!.LogicalName);
        var world=Matrix4x4.CreateRotationZ(.6f)*Matrix4x4.CreateTranslation(10,20,30);
        var state=new EnbRetailRingAssemblyRuntime(assembly);
        var before=Assert.Single(state.BeforeAllocation(1,100,world,2,true).Rings);
        Assert.Equal(world,before.World);
        var paused=Assert.Single(state.Prepare(1,100,100,world,2,true,new HashSet<int>()).Rings);
        Assert.Equal(0,paused.Progress);Assert.Equal(before.Values,paused.Values);Assert.Equal(before.Bounds,paused.Bounds);
        var drawn=Assert.Single(state.Prepare(2,200,100,world,2,true,new HashSet<int>{0}).Rings);
        Assert.True(drawn.Progress>0);Assert.NotNull(drawn.Values.Sample(20));
        var again=Assert.Single(state.Prepare(3,300,100,world,2,true,new HashSet<int>()).Rings);
        Assert.Equal(drawn.Progress,again.Progress);Assert.Equal(drawn.Values,again.Values);
        Assert.Empty(state.BeforeAllocation(4,400,world,2,false).Rings);
        Assert.Equal(0,Assert.Single(state.BeforeAllocation(5,500,world,2,true).Rings).Progress);
    }
}
