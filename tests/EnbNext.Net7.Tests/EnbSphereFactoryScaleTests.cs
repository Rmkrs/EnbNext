using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSphereFactoryScaleTests
{
    [Fact]
    public void FactoryAndSourceRadiusScaleStoreEachKeySeparately()
    {
        var header=Header() with {Duration=1};
        var keys=new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>
            {[4]=[new([.1f,2,3],0),new([.7f,4,9],1)]};
        var binding=new EnbRetailSphereAttachment(0,0,"",default,EnbRetailSphereAsset.FromDefinition(new(header,keys)),null);
        var runtime=new EnbRetailSphereAssemblyRuntime(new(null,null,null,[binding],[],null),1.7f);
        var item=runtime.Prepare(1,0,0,Matrix4x4.Identity,1.1f,true,new HashSet<int>{0}).Spheres.Single();
        var expected=new EnbRetailSphereProperties(header,new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>
            {[4]=keys[4].Select(k=>new EnbCurrentRetailEffectPropertyKey(k.Value.Select(v=>(v*1.7f)*1.1f).ToArray(),k.Time)).ToArray()}).Evaluate(0);
        Assert.Equal(expected.Scale,item.Values.Scale);
        Assert.NotEqual(binding.Asset.Properties.Scale(1.7f*1.1f).Evaluate(0).Scale,item.Values.Scale);
    }

    [Fact]
    public void ZeroSourceRadiusPreservesRigidPoseAndSamplesCollapsedProperties()
    {
        var asset=EnbRetailSphereAsset.FromDefinition(new(Header(),new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>
            {[4]=[new([1,2,3],0),new([3,4,5],1)]}));
        var binding=new EnbRetailSphereAttachment(0,0,"",default,asset,null);
        var runtime=new EnbRetailSphereAssemblyRuntime(new(null,null,null,[binding],[],null));
        var world=Matrix4x4.CreateRotationY(.7f)*Matrix4x4.CreateTranslation(3,4,5);
        var item=runtime.Prepare(1,0,300,world,0,true,new HashSet<int>{0}).Spheres.Single();
        Assert.Equal(world,item.World);
        Assert.Equal(Vector3.Zero,item.Values.Scale);
        Assert.Equal(0,item.Bounds.Radius);
        Assert.Equal(Header().Center+world.Translation,item.Bounds.Center);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssemblyScalesKeysBeforeSamplingAndRetainsScaledValuesWhenNotDrawn(bool faceCamera)
    {
        var header=Header() with {Duration=1,Flags=faceCamera?2u:0u};
        var asset=EnbRetailSphereAsset.FromDefinition(new(header,
            new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>
            {[4]=[new([.1f,2,3],0),new([.7f,4,9],1)]}));
        var binding=new EnbRetailSphereAttachment(0,0,"",default,asset,null);
        var runtime=new EnbRetailSphereAssemblyRuntime(new(null,null,null,[binding],[],null));
        var world=Matrix4x4.CreateTranslation(3,4,5);
        var drawn=new HashSet<int>{0};
        var initial=runtime.BeforeAllocation(1,0,world,1.7f,true).Spheres.Single();
        Assert.Equal(header.Parameter54*1.7f,initial.Values.Scale);
        var frame=runtime.Prepare(1,0,300,world,1.7f,true,drawn);
        var item=frame.Spheres.Single();
        Assert.Equal(.3f,item.Progress);
        Assert.Equal(0x3ef3b647u,BitConverter.SingleToUInt32Bits(item.Values.Scale.X));
        Assert.Equal(1,item.Scale);
        Assert.Equal(world.Translation,item.World.Translation);
        Assert.True(EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),
            Math.PI/3,new(.01f,1000),out var camera));
        Assert.Equal(EnbRetailSphereTransform.Build(world,header.AuthoredExtent,item.Values.Scale,faceCamera,camera.Basis),item.DrawWorld(camera));
        Assert.Same(frame,runtime.Prepare(1,0,900,world,9,true,drawn));
        Assert.Equal(item.Values,runtime.Prepare(2,0,900,world,1.7f,true,new HashSet<int>()).Spheres.Single().Values);
        Assert.Empty(runtime.Prepare(3,0,900,world,1.7f,false,drawn).Spheres);
        var resumed=runtime.BeforeAllocation(4,0,world,1.7f,true).Spheres.Single();
        Assert.Equal(0,resumed.Progress);
        Assert.Equal(item.Values,resumed.Values);
        Assert.Equal(header.Parameter54,asset.Properties.InitialValues.Scale);
        Assert.Equal(0x3e8f5c29u,BitConverter.SingleToUInt32Bits(asset.Properties.Evaluate(.3f).Scale.X));
    }

    private static EnbCurrentRetailSphereHeader Header() => new(1,9,"factory-sphere",
        new(7,8,9),new(3,4,5),2,new(.2f,.4f,.6f),.75f,new(2,3,4),
        [0,0,0,1,1],"sphere.dds",new byte[16]);

    [Fact]
    public void KeylessScaleChangesInitialScaleWithoutChangingOtherPropertiesOrSource()
    {
        var original=new EnbRetailSphereProperties(Header(),
            new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>());
        var before=original.InitialValues;
        var scaled=original.Scale(2.5f);

        Assert.Equal(before with {Scale=new(5,7.5f,10)},scaled.InitialValues);
        Assert.Equal(scaled.InitialValues,scaled.Evaluate(100));
        Assert.Equal(before,original.InitialValues);
        Assert.Equal(before,original.Evaluate(100));
    }

    [Fact]
    public void ScaleRoundsAuthoredKeysBeforeInterpolationRatherThanScalingTheSample()
    {
        var original=new EnbRetailSphereProperties(Header(),
            new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>
            {
                [4]=[new([.1f,2,3],0),new([.7f,4,9],1)],
                [2]=[new([1,0,0],0),new([0,1,0],1)],
                [3]=[new([.2f],0),new([.8f],1)],
                [5]=[new([0,0,0,1,1],0),new([0,0,1,0,3],1)]
            });
        var before=original.Evaluate(.3f);
        var scaled=original.Scale(1.7f);
        var after=scaled.Evaluate(.3f);

        // 0090D340 stores each float key before 0090D6E0 interpolates it.
        // These inputs distinguish that order by one binary32 ULP.
        Assert.Equal(0x3ef3b647u,BitConverter.SingleToUInt32Bits(after.Scale.X));
        Assert.Equal(0x3ef3b646u,BitConverter.SingleToUInt32Bits(before.Scale.X*1.7f));
        Assert.Equal(0x3e8f5c29u,BitConverter.SingleToUInt32Bits(before.Scale.X));
        Assert.Equal(0x3e2e147bu,BitConverter.SingleToUInt32Bits(scaled.Evaluate(0).Scale.X));
        Assert.Equal(0x3f9851ecu,BitConverter.SingleToUInt32Bits(scaled.Evaluate(1).Scale.X));
        Assert.Equal(scaled.Evaluate(1),scaled.Evaluate(10));
        Assert.Equal(before with {Scale=after.Scale},after);
        Assert.Equal(before,original.Evaluate(.3f));
        Assert.Equal(Header().Parameter54,original.InitialValues.Scale);
    }

    [Fact]
    public void AssetCopiesKeepIndependentScaleFactorsAndAuthoredBounds()
    {
        var definition=new EnbCurrentRetailSphereDefinition(Header(),
            new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>
            {
                [4]=[new([1,2,3],0),new([3,4,5],2)]
            });
        var original=EnbRetailSphereAsset.FromDefinition(definition);
        var twice=original.Scale(2);
        var thrice=original.Scale(3);
        var sixTimes=twice.Scale(3);

        Assert.Equal(new Vector3(2,3,4),original.Properties.Evaluate(1).Scale);
        Assert.Equal(new Vector3(4,6,8),twice.Properties.Evaluate(1).Scale);
        Assert.Equal(new Vector3(6,9,12),thrice.Properties.Evaluate(1).Scale);
        Assert.Equal(new Vector3(12,18,24),sixTimes.Properties.Evaluate(1).Scale);
        Assert.Equal(new Vector3(4,6,8),twice.Properties.InitialValues.Scale);
        foreach(var asset in new[]{original,twice,thrice,sixTimes})
        {
            Assert.Same(definition,asset.Definition);
            Assert.Equal(new Vector3(7,8,9),asset.Header.Center);
            Assert.Equal(new Vector3(3,4,5),asset.Header.AuthoredExtent);
            Assert.Equal(new Vector3(2,3,4),asset.Header.Parameter54);
            Assert.Equal(original.Header,asset.Header);
            Assert.True(asset.HasPropertyKeys);
            Assert.True(asset.HasScaleKeys);
        }
        Assert.Equal(new float[]{1,2,3},definition.Tracks[4][0].Value);
        Assert.Equal(new float[]{3,4,5},definition.Tracks[4][1].Value);
    }
}
