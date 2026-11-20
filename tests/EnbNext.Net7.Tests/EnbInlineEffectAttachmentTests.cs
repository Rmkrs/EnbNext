using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbInlineEffectAttachmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PermanentMeshAndParticleChildrenKeepSeparateConsumers(bool withProxy)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var original=EnbCurrentRetailRObjEffectAsset.Load(assets,2010).Resource!;
        var h=Assert.Single(original.HierarchicalLods);
        var particle=Assert.Single(h.Aggregate!.Members);
        var mesh=original.Meshes[0];string name=EnbW3dRenderObjectNameSemantics.RegisteredMeshName(mesh);
        var mixed=EnbCurrentRetailMixedEffectAsset.LoadInline(assets,original.Copy().SetHierarchicalLods([h with {Aggregate=h.Aggregate with {Members=[particle,new(0,name)]},
                ProxyArray=withProxy?new(0,[new(0,"NO_SUCH_RENDER_OBJECT")]):null}]));
        Assert.Equal(particle.Name,Assert.Single(mixed.Sprites).Definition.Name,true);
        Assert.Empty(mixed.Streaks);
        Assert.Equal(withProxy?1:0,EnbW3dHlodProxyResolver.GetCount(mixed.Resource.HierarchicalLods.Single()));
        Assert.Equal(2,Assert.Single(mixed.Resource.HierarchicalLods).Aggregate!.Members.Count);
        Assert.Contains(mixed.Resource.Meshes,m=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,name));
        Assert.Equal(EnbW3dHlodBudgetCompiler.CurrentRetailZeroCostFallback,mixed.EmitterFixedCost);
        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dAggregateFixedRenderCost(mixed.Resource,
            mixed.Resource.HierarchicalLods.Single(),_=>true,out var combined,out var error),error);
        Assert.Equal(mesh.TriangleMetadata.Count*mesh.CurrentRetailRenderPassCount+mixed.EmitterFixedCost,combined);
    }

    [Fact]
    public void EmptyInlineAggregateRetainsOrdinaryMeshRoot()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var source=assets.BaseAssetResolver!.Resolve(2010).W3d!;
        Assert.True(assets.FileResources.TryRead(source.LogicalFileName,out var bytes,out _,out _));
        byte[] EmptyAggregate(ReadOnlySpan<byte> chunks)
        {
            using var output=new MemoryStream();using var writer=new BinaryWriter(output);
            while(chunks.Length!=0)
            {
                uint id=System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(chunks);
                uint length=System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(chunks[4..]);
                int size=checked((int)(length&0x7fffffff));
                byte[] data=chunks.Slice(8,size).ToArray();
                if(id==0x700)data=EmptyAggregate(data);
                if(id==0x705)
                {
                    data=new byte[16];System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data,0x703);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4),8);
                }
                writer.Write(id);writer.Write((length&0x80000000)|(uint)data.Length);writer.Write(data);
                chunks=chunks[(8+size)..];
            }
            return output.ToArray();
        }
        Assert.True(EnbW3dModel.TryDecode(EmptyAggregate(bytes),out var model));
        var asset=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,source with {Model=model},Vector3.Zero);
        Assert.Null(asset.Mixed);Assert.NotEmpty(asset.Resource!.Meshes);
        Assert.Empty(asset.Resource.HierarchicalLods.Single().Aggregate!.Members);
        Assert.Empty(asset.ModelAttachments);Assert.Null(asset.AggregateParticles);
    }

    [Theory]
    [InlineData(1,1)]
    [InlineData(32,1)]
    [InlineData(33,1)]
    [InlineData(65,1)]
    [InlineData(65,2)]
    public void InlineOccurrencesRetainIndependentEmitterBuffers(int count,int levels)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var original=EnbCurrentRetailRObjEffectAsset.Load(assets,2010).Resource!;
        var hlod=original.HierarchicalLods.Single();var member=Assert.Single(hlod.Aggregate!.Members);
        var resource=original.Copy().SetHierarchicalLods([hlod with {Aggregate=hlod.Aggregate with {Members=Enumerable.Repeat(member,count).ToArray()}}]);
        if(levels==2)resource=resource.Copy().SetHierarchicalLods([resource.HierarchicalLods.Single() with {Levels=
            [hlod.Levels[0] with {Members=[hlod.Levels[0].Members[0]]},hlod.Levels[0] with {Ordinal=1}]}]);
        var mixed=EnbCurrentRetailMixedEffectAsset.LoadInline(assets,resource);
        Assert.Equal(levels,mixed.Resource.HierarchicalLods.Single().Levels.Count);
        Assert.Equal(count,mixed.Sprites.Count);Assert.Equal(count,mixed.Resource.HierarchicalLods.Single().Aggregate!.Members.Count);
        Assert.All(mixed.Sprites,s=>Assert.Equal((int)member.ReferenceIndex,s.PivotIndex));
        var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,1,123,randomStreams:new());
        Assert.Equal(count,runtime.Sprites.Select(s=>s.Continuous).Distinct(ReferenceEqualityComparer.Instance).Count());
        var history=new EnbCurrentRetailParticlePoseHistory();
        for(uint age=0;age<=550;age+=50)
        {history.Record(age,Matrix4x4.CreateTranslation(age*.1f,0,0));runtime.Advance(mixed,history,1);}
        Assert.All(runtime.Sprites,s=>{Assert.True(s.Continuous!.EmittedCount>0);Assert.NotEmpty(s.Continuous.Births);});
    }

    [Theory]
    [InlineData(2010,4,5,"SHLD_DRNFXP1")]
    [InlineData(1946,2,4,"TSG01P1_DECO")]
    public void InstalledInlineChildKeepsMeshesPivotAndContinuousBirths(int id,int meshes,int pivot,string name)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        var original=assets.BaseAssetResolver!.Resolve(id).W3d!;
        Assert.Null(original.CurrentRetailAggregate);
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(assets,id);
        var mixed=Assert.IsType<EnbCurrentRetailMixedEffectAsset>(loaded.Mixed);
        Assert.Equal(meshes,mixed.Resource.Meshes.Count);
        var child=Assert.Single(mixed.Sprites);
        Assert.Equal(name,child.Definition.Name,true);Assert.Equal(pivot,child.PivotIndex);
        var member=Assert.Single(Assert.Single(mixed.Resource.HierarchicalLods).Aggregate!.Members);
        Assert.Equal((uint)pivot,member.ReferenceIndex);Assert.Equal(name,member.Name,true);
        Assert.Empty(mixed.Streaks);Assert.Null(mixed.Animation);
        var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,1,123,randomStreams:new());
        var emitter=Assert.Single(runtime.Sprites);
        Assert.NotNull(emitter.Continuous);
        var history=new EnbCurrentRetailParticlePoseHistory();
        for(uint age=0;age<=550;age+=50)
        {
            history.Record(age,Matrix4x4.CreateRotationZ(age*.001f)*Matrix4x4.CreateTranslation(age*.1f,0,0));
            runtime.Advance(mixed,history,1);
        }
        Assert.True(emitter.Continuous!.EmittedCount>0);Assert.NotEmpty(emitter.Continuous.Births);
        Assert.True(emitter.CurrentWorld.Translation.X>1);
        Assert.All(emitter.Continuous.Births,b=>Assert.True(float.IsFinite(b.World.Translation.X)));
        emitter.Continuous.Advance(2000,Matrix4x4.Identity,false);
        Assert.Empty(emitter.Continuous.Births);
    }
}
