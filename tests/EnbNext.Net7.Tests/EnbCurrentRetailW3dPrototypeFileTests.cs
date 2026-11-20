using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbCurrentRetailW3dPrototypeFileTests
{
    [Fact]
    public void EmptyPublicationDoesNotAbsorbUndecodedNonemptyFamilies()
    {
        Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode([],out var empty,out var error),error);
        Assert.Empty(empty.Entries);
        foreach(uint kind in new uint[]{0x741,0x742,0x800,0x7fffffff})
        {
            byte[] bytes=new byte[8];System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes,kind);
            Assert.False(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out _,out error));
            Assert.Contains("no decoded",error);
        }
    }
    [Fact]
    public void AggregatePublicationDoesNotAcquireItsUnavailableBase()
    {
        var bytes=EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("WRAPPED","UNAVAILABLE",[],25,0);
        Assert.False(EnbW3dModel.TryDecode(bytes,out _));
        Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out var error),error);
        var entry=Assert.Single(file.Entries);
        Assert.Null(entry.Ordinary);
        Assert.Equal("WRAPPED",entry.Aggregate!.Name);
        Assert.Equal("UNAVAILABLE",entry.Aggregate.BaseModelName);
        Assert.Empty(file.Model.Meshes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedFilePreservesOrdinaryAndAggregateDuplicatePublicationOrder(bool aggregateFirst)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(assets.FileResources.TryRead("MS03A.w3d",out var ordinary,out _,out _));
        var aggregate=EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("MS03A","UNAVAILABLE",[],25,0);
        byte[] bytes=aggregateFirst?[..aggregate,..ordinary]:[..ordinary,..aggregate];
        Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out var error),error);
        var sameName=file.Entries.Where(entry=>entry.Aggregate?.Name=="MS03A" ||
            entry.Ordinary is {Kind:EnbW3dPrototypeKind.HierarchyModel} model && file.Model.HierarchyModels[model.Ordinal].Name=="MS03A").ToArray();
        Assert.Equal(2,sameName.Length);
        Assert.Equal(aggregateFirst,sameName[0].Aggregate is not null);
        Assert.Equal(!aggregateFirst,sameName[1].Aggregate is not null);
        Assert.True(sameName[0].ChunkOffset<sameName[1].ChunkOffset);
        Assert.NotEmpty(file.Model.Meshes);
        using var cache=new EnbW3dRenderResourceCache();
        var source=new EnbResolvedW3dResource("mixed.w3d","MS03A",new("mixed.w3d","publication-control",0,bytes.Length),file.Model,[])
            {PrototypeFile=file};
        Assert.True(cache.TryLoadFile(source,out _,out error),error);
        Assert.True(cache.TryFindPrototypeSource("MS03A",out var registered));
        Assert.Equal(aggregateFirst,registered.AggregatePrototype is not null);
        Assert.Equal(!aggregateFirst,registered.RegisteredPrototype?.Kind==EnbW3dPrototypeKind.HierarchyModel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisteredAggregateCreatesTheCurrentBaseWithoutReopeningItsFile(bool cycle)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        using var cache=new EnbW3dRenderResourceCache();
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,assets.MixResources,"MS03A",out var source,out var error),error);
        Assert.True(cache.TryLoadFile(source,out _,out error),error);
        for(int index=0;index<12;index++)
        {
            string name="WRAP"+index;
            var bytes=EnbR6Z11CurrentRetailAggregateTests.BuildAggregate(name,index==11?(cycle?"WRAP0":"MS03A"):"WRAP"+(index+1),[],25,0);
            Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out error),error);
            Assert.True(cache.TryLoadFile(new("never-present-"+name+".w3d",name,new(name,"publication-control",0,bytes.Length),file.Model,[])
                {PrototypeFile=file},out _,out error),error);
        }
        Assert.True(cache.TryFindPrototypeSource("WRAP0",out var wrapper));
        bool Resolve(out EnbResolvedW3dResource resolved,out string? why)=>EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveRegisteredSource(
            assets.FileResources,assets.RetailMixResources,wrapper,cache.CapturePrototypeSources(out _),out resolved,out why);
        if(cycle)
        {
            Assert.False(Resolve(out _,out error));Assert.Contains("cycle",error);
            return;
        }
        Assert.True(Resolve(out var first,out error),error);
        Assert.Equal(wrapper.PrototypeRegistration,first.PrototypeRegistration);
        Assert.Equal(source.ResourceLocation,first.CurrentRetailAggregate!.BaseResourceLocation);
        Assert.Empty(first.AcquisitionPublications);
        Assert.True(cache.RemovePrototype("MS03A"));
        var replacement=source with {LogicalFileName="replacement-base.w3d",ResourceLocation=source.ResourceLocation with {Name="replacement-base.w3d"}};
        Assert.True(cache.TryLoadFile(replacement,out _,out error),error);
        Assert.True(Resolve(out var second,out error),error);
        Assert.Equal(wrapper.PrototypeRegistration,second.PrototypeRegistration);
        Assert.Equal(replacement.ResourceLocation,second.CurrentRetailAggregate!.BaseResourceLocation);
        Assert.NotEqual(first.CurrentRetailAggregate.BasePrototypeRegistration,second.CurrentRetailAggregate.BasePrototypeRegistration);
        Assert.Equal(source.ResourceLocation,first.CurrentRetailAggregate.BaseResourceLocation);
        var effect=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,wrapper,System.Numerics.Vector3.Zero,cache.CapturePrototypeSources(out _));
        Assert.Equal(wrapper.PrototypeRegistration,effect.FactoryPrototypeRegistration);
        Assert.Equal("MS03A",Assert.Single(effect.Resource!.HierarchyModels).Name);
    }
}
