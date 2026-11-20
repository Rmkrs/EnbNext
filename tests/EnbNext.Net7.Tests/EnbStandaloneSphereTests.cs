using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStandaloneSphereTests
{
    [Theory]
    [InlineData("sk_clx01p2")]
    [InlineData("BST62_shockp2")]
    public void DirectFileRetainsSphereScaleClockAndVisibility(string name)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        assets.PresentationCatalog!.LoadBaseAssetsText($"[BASE-90000]\nFILE={name}.w3d\nNAME={name}");
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(assets,90000,new Vector3(120,.1f,0));
        var assembly=Assert.IsType<EnbRetailSphereAssembly>(loaded.Spheres);
        Assert.Null(loaded.Resource);Assert.Null(assembly.Resource);Assert.Null(assembly.Emitters);
        Assert.Empty(assembly.Rings);Assert.Null(loaded.Rings);
        var binding=Assert.Single(assembly.Spheres);
        Assert.Equal(0,binding.PivotIndex);Assert.Equal(string.Empty,binding.BoneName);
        var world=Matrix4x4.CreateRotationZ(.6f)*Matrix4x4.CreateTranslation(10,20,30);
        var runtime=new EnbRetailSphereAssemblyRuntime(assembly);
        var before=Assert.Single(runtime.BeforeAllocation(1,100,world,2,true).Spheres);
        Assert.Equal(world,before.World);Assert.Equal(1,before.Scale);
        //90D340 scales the retained properties before sampling, not DrawWorld.
        Assert.Equal(binding.Asset.Properties.InitialValues.Scale*2,before.Values.Scale);
        var paused=Assert.Single(runtime.Prepare(1,100,100,world,2,true,new HashSet<int>()).Spheres);
        Assert.Equal(0,paused.Progress);Assert.Equal(before.Values,paused.Values);
        var drawn=Assert.Single(runtime.Prepare(2,200,100,world,2,true,new HashSet<int>{0}).Spheres);
        Assert.True(drawn.Progress>0);
        var again=Assert.Single(runtime.Prepare(3,300,100,world,2,true,new HashSet<int>()).Spheres);
        Assert.Equal(drawn.Progress,again.Progress);Assert.Equal(drawn.Values,again.Values);
        Assert.Empty(runtime.BeforeAllocation(4,400,world,2,false).Spheres);
        Assert.Equal(0,Assert.Single(runtime.BeforeAllocation(5,500,world,2,true).Spheres).Progress);
    }
}
