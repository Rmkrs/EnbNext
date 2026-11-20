using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRingFactoryScaleTests
{
    [Fact]
    public void FactoryAndSourceRadiusScaleStoreEachKeySeparately()
    {
        var definition=Definition();
        var binding=new EnbRetailRingAttachment(0,0,"",default,EnbRetailRingAsset.FromDefinition(definition),null);
        var runtime=new EnbRetailRingAssemblyRuntime(new(null,null,null,[binding],null),1.7f);
        var item=runtime.Prepare(1,0,0,Matrix4x4.Identity,1.1f,true,new HashSet<int>{0}).Rings.Single();
        var expected=EnbRetailRingAsset.FromDefinition(new(definition.Header,definition.Tracks.ToDictionary(p=>p.Key,
            p=>(IReadOnlyList<EnbCurrentRetailEffectPropertyKey>)p.Value.Select(k=>new EnbCurrentRetailEffectPropertyKey(
                k.Value.Select(v=>(v*1.7f)*1.1f).ToArray(),k.Time)).ToArray()))).EvaluateValues(0);
        Assert.Equal(expected.FirstRadii,item.Values.FirstRadii);
        Assert.Equal(expected.SecondRadii,item.Values.SecondRadii);
        Assert.NotEqual(binding.Asset.ScaleRadiusMultipliers(1.7f*1.1f).EvaluateValues(0).FirstRadii,item.Values.FirstRadii);
    }

    [Fact]
    public void ZeroSourceRadiusRetainsInitialExtentUntilTheSecondTrackDraw()
    {
        var asset=EnbRetailRingAsset.FromDefinition(Definition());
        var binding=new EnbRetailRingAttachment(0,0,"",default,asset,null);
        var runtime=new EnbRetailRingAssemblyRuntime(new(null,null,null,[binding],null));
        var world=Matrix4x4.CreateRotationY(.7f)*Matrix4x4.CreateTranslation(3,4,5);
        var before=runtime.BeforeAllocation(1,0,world,0,true).Rings.Single();
        Assert.Equal(world,before.World);
        Assert.Equal(Vector2.Zero,before.Values.FirstRadii);
        Assert.Equal(Vector2.Zero,before.Values.SecondRadii);
        Assert.Equal(asset.Header.SecondRadii.Length(),before.Bounds.Radius);
        var drawn=runtime.Prepare(1,0,300,world,0,true,new HashSet<int>{0}).Rings.Single();
        Assert.Equal(0,drawn.Bounds.Radius);
        Assert.Equal(Vector2.Zero,drawn.Values.SecondRadii);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PrepareOnlyPrimitiveWrapperConstructsChildrenWithoutRetainingThem(bool sphere)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        string name=sphere?"sk_clx04p2":"sk_pd01p1";
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,assets.RetailMixResources,
            name,out var primitive,out var error),error);
        using var cache=new EnbW3dRenderResourceCache();
        Assert.True(cache.TryLoadFile(primitive,out _,out error),error);
        var bytes=EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("PREP744",name,[("MS03A","ROOT")],25,0);
        Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out error),error);
        var wrapper=new EnbResolvedW3dResource("prep744.w3d","PREP744",new("prep744.w3d","fixture",0,bytes.Length),file.Model,[]){PrototypeFile=file};
        Assert.True(cache.TryLoadFile(wrapper,out _,out error),error);
        Assert.True(cache.TryFindPrototypeSource("PREP744",out var selected));
        Assert.False(cache.TryFindPrototypeSource("MS03A",out _));
        var context=cache.CreatePublicationContext();
        var prepared=EnbCurrentRetailRObjEffectAsset.PreparePrototype(assets,selected,Vector3.Zero,cache.CapturePrototypeSources(out _),context);
        //No Build call is needed for009A3A50's discarded child factory effects.
        Assert.True(prepared.PublicationsApplied);
        Assert.True(cache.TryFindPrototypeSource("MS03A",out _));
        Assert.Empty(prepared.ModelChildren);
        Assert.Equal(1,context.PublicationCount);
        Assert.Equal(selected.PrototypeRegistration,prepared.Source!.PrototypeRegistration);
        Assert.Equal("PREP744",prepared.Source.CurrentRetailAggregate!.RenderDefinition.Name);
        if(sphere)Assert.Same(primitive.Model.Spheres[0],prepared.Source.Model.Spheres[0]);
        else Assert.Same(primitive.Model.Rings[0],prepared.Source.Model.Rings[0]);
    }

    private static EnbCurrentRetailRingDefinition Definition(bool secondTrack=true)=>new(
        new(1,2,"factory-ring",new(7,8,9),new(11,12,13),1,new(.2f,.4f,.6f),.75f,
            new(2,3),new(4,5),Vector2.One,new(2,3),"",new byte[16],3),
        secondTrack?new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>
        {
            [4]=[new([.1f,2],0),new([.7f,4],1)],
            [5]=[new([4,5],0),new([8,9],1)]
        }:new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>
        {
            [4]=[new([.1f,2],0),new([.7f,4],1)]
        });

    [Theory]
    [InlineData(0f)] [InlineData(-2f)]
    public void FiniteZeroAndNegativeScaleChangeMultipliersAndKeysWithoutChangingDefinition(float scale)
    {
        var definition=Definition();var original=EnbRetailRingAsset.FromDefinition(definition);
        var scaled=original.ScaleRadiusMultipliers(scale);
        Assert.Same(definition,scaled.Definition);
        Assert.Equal(original.Header.FirstRadii,scaled.Header.FirstRadii);
        Assert.Equal(original.Header.SecondRadii,scaled.Header.SecondRadii);
        Assert.Equal(original.Header.Center,scaled.Header.Center);
        Assert.Equal(original.Header.AuthoredExtent,scaled.Header.AuthoredExtent);
        Assert.Equal(original.InitialValues.FirstRadii*scale,scaled.InitialValues.FirstRadii);
        Assert.Equal(original.InitialValues.SecondRadii*scale,scaled.InitialValues.SecondRadii);
        Assert.Equal(original.EvaluateValues(1).FirstRadii*scale,scaled.EvaluateValues(1).FirstRadii);
        Assert.Equal(original.EvaluateValues(1).SecondRadii*scale,scaled.EvaluateValues(1).SecondRadii);
        Assert.Equal(new float[]{.1f,2},definition.Tracks[4][0].Value);
        Assert.Equal(new Vector2(2,3),definition.Header.FirstRadiusMultiplier);
        Assert.Same(original,original.ScaleRadiusMultipliers(1));
    }

    [Fact]
    public void ScaleRoundsKeysBeforeSamplingAndComposesWithoutMutatingItsSource()
    {
        var definition=Definition();var original=EnbRetailRingAsset.FromDefinition(definition);
        var scaled=original.ScaleRadiusMultipliers(1.7f);
        Assert.Equal(0x3ef3b647u,BitConverter.SingleToUInt32Bits(scaled.EvaluateValues(.3f).FirstRadii.X));
        Assert.NotEqual(scaled.EvaluateValues(.3f).FirstRadii.X,original.EvaluateValues(.3f).FirstRadii.X*1.7f);
        var composed=scaled.ScaleRadiusMultipliers(-2);
        Assert.Same(definition,composed.Definition);
        Assert.Equal(scaled.Tracks[5][1].Value[0]*-2,composed.Tracks[5][1].Value[0]);
        Assert.Equal(new float[]{8,9},definition.Tracks[5][1].Value);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void StoredExtentChangesOnlyAfterAnAdmittedSecondTrackSample(bool secondTrack)
    {
        var definition=Definition(secondTrack);var asset=EnbRetailRingAsset.FromDefinition(definition).ScaleRadiusMultipliers(-2);
        var binding=new EnbRetailRingAttachment(0,0,"",new("ring","fixture",0,0),asset,null);
        var runtime=new EnbRetailRingAssemblyRuntime(new(null,null,null,[binding],null));
        var world=Matrix4x4.CreateScale(4)*Matrix4x4.CreateTranslation(3,4,5);
        var before=Assert.Single(runtime.BeforeAllocation(1,0,world,1,true).Rings);
        Assert.Equal(definition.Header.SecondRadii.Length(),before.Bounds.Radius);
        Assert.Equal(Vector3.Transform(definition.Header.Center,world),before.Bounds.Center);
        var notDrawn=Assert.Single(runtime.Prepare(1,0,500,world,1,true,new HashSet<int>()).Rings);
        Assert.Equal(0,notDrawn.Progress);Assert.Equal(before.Values,notDrawn.Values);Assert.Equal(before.Bounds,notDrawn.Bounds);
        var drawn=Assert.Single(runtime.Prepare(2,0,500,world,1,true,new HashSet<int>{0}).Rings);
        Assert.Equal(.5f,drawn.Progress);
        Assert.Equal(asset.EvaluateValues(.5f),drawn.Values);
        Assert.Equal(secondTrack?drawn.Values.SecondRadii.Length():definition.Header.SecondRadii.Length(),drawn.Bounds.Radius);
        Assert.Empty(runtime.Prepare(3,0,500,world,1,false,new HashSet<int>{0}).Rings);
        var returned=Assert.Single(runtime.Prepare(4,0,500,world,1,true,new HashSet<int>()).Rings);
        Assert.Equal(0,returned.Progress);Assert.Equal(drawn.Values,returned.Values);Assert.Equal(drawn.Bounds,returned.Bounds);
        Assert.Same(definition,asset.Definition);
    }

    [Fact]
    public void SharedAttachmentHsvHelperPreservesDefinitionAndSourceGeometry()
    {
        var definition=Definition();var asset=EnbRetailRingAsset.FromDefinition(definition).ScaleRadiusMultipliers(2);
        var binding=new EnbRetailRingAttachment(7,2,"bone",new("ring","fixture",0,0),asset,null);
        Assert.Same(binding,binding.ApplyHsv(null!,Vector3.Zero));
        var shifted=binding.ApplyHsv(null!,new(120,0,0));
        Assert.Same(definition,shifted.Asset.Definition);
        Assert.Equal(EnbCurrentRetailHsvRuntime.ShiftRgb(asset.Header.Color,new(120,0,0)),shifted.Asset.Header.Color);
        Assert.Equal(asset.InitialValues.FirstRadii,shifted.Asset.InitialValues.FirstRadii);
        Assert.Equal(asset.InitialValues.SecondRadii,shifted.Asset.InitialValues.SecondRadii);
        Assert.Equal(asset.Header.Opacity,shifted.Asset.Header.Opacity);
        Assert.Equal(binding.Ordinal,shifted.Ordinal);Assert.Equal(binding.PivotIndex,shifted.PivotIndex);
        Assert.Equal(definition.Header.Color,asset.Header.Color);Assert.NotEqual(binding.Key,shifted.Key);
    }

    [Fact]
    public void NonfiniteScaleRemainsOutsideTheRepresentedAssetBoundary()
    {
        var asset=EnbRetailRingAsset.FromDefinition(Definition());
        foreach(float value in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity})
            Assert.Throws<ArgumentOutOfRangeException>(()=>asset.ScaleRadiusMultipliers(value));
    }
}
