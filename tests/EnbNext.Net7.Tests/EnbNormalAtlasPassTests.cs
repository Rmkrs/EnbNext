using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbNormalAtlasPassTests
{
    [Theory]
    [InlineData(212,400,6400)]
    [InlineData(10086,0,7000)]
    public void InstalledInfiniteNormalAtlasRetiresAtItsCycleBoundary(int id,uint start,uint end)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=new EnbSceneEffectSnapshot(1,1,42,0,(ushort)id,0,0,"",0,false,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var initial=scheduler.Update([root],catalog,0).VisualNodes.Where(n=>n.Active).ToArray();
        Assert.Equal(start==0?1:0,initial.Length);
        if(start>0)Assert.DoesNotContain(scheduler.Update([root],catalog,start-1).VisualNodes,n=>n.Active);
        Assert.Single(scheduler.Update([root],catalog,start).VisualNodes,n=>n.Active);
        Assert.Single(scheduler.Update([root],catalog,6000).VisualNodes,n=>n.Active);
        Assert.Single(scheduler.Update([],catalog,6050).VisualNodes,n=>n.Active);
        Assert.Single(scheduler.Update([],catalog,end-1).VisualNodes,n=>n.Active);
        Assert.DoesNotContain(scheduler.Update([],catalog,end).VisualNodes,n=>n.Active);
        Assert.Equal(0,scheduler.TrackedRootCount);
    }

    [Theory]
    [InlineData(212)]
    [InlineData(10056)]
    [InlineData(10086)]
    public void InstalledNormalAtlasUsesGeneratedCoordinates(int id)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        var root=new EnbSceneEffectSnapshot(1,1,42,0,(ushort)id,0,0,"",0,false,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var node=new EnbCurrentRetailEffectOwner(1,assets.PresentationCatalog!.FindEffect(id)!,root,42,true,0);
        Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(node,out var definition,out var error),error);
        Assert.False(definition.ProjectStage0);Assert.True(definition.ResetMappers);Assert.Null(definition.Stage1BoxScale);
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,definition.Swatch,out var source,out error),error);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out error),error);
        var pass=Assert.Single(Assert.Single(resource.Meshes).MaterialPasses);
        Assert.True(RetailTestShims.IsNormalAtlasMapper(pass.SingleBindingSemantics!.Value.Mapper0Program));
        Assert.True(EnbCurrentRetailPassEffectDefinition.IsDisabledSecondStage(pass));
        definition.ValidateSwatch(pass);
        // Native 006ED6C0/006EE260: projection and reset are independent
        // owner choices, not restrictions on an admitted parent mapper.
        (definition with {ProjectStage0=true}).ValidateSwatch(pass);
        (definition with {ResetMappers=false}).ValidateSwatch(pass);
        (definition with {Stage1BoxScale=Vector2.One}).ValidateSwatch(pass);
        Assert.Throws<InvalidDataException>(()=>definition.ValidateSwatch((pass).Copy().SetSingleBindingSemantics(null)));
        var semantics=pass.SingleBindingSemantics!.Value;
        Assert.Throws<InvalidDataException>(()=>definition.ValidateSwatch((pass).Copy().SetSingleBindingSemantics(semantics with {
            Mapper0Program=semantics.Mapper0Program with {Kind=(EnbW3dMapperRuntimeProgramKind)byte.MaxValue}})));
    }
}
