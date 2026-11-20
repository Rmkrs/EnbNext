using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSneakyPassTests
{
    [Fact]
    public void InstalledSneakyDelayAndFloatDurationBoundaries()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=new EnbSceneEffectSnapshot(1,1,42,0,625,0,0,"",0,false,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        foreach(var (tick,id) in new (uint,int)[]{(0,0),(499,0),(500,625),(1448,625),(1449,626),(3448,626),(3449,627),(4397,627),(4398,0)})
        {
            var nodes=scheduler.Update([root],catalog,tick).VisualNodes.Where(n=>n.Active).ToArray();
            if(id==0)Assert.Empty(nodes);
            else
            {
                var node=Assert.Single(nodes);Assert.Equal(id,node.Definition.DefinitionId);
                Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(node,out var pass,out var error),error);
                Assert.Equal(id==626,pass.HideBaseMesh);
            }
        }
        scheduler.Update([],catalog,4400);Assert.Equal(0,scheduler.TrackedRootCount);
    }

    [Theory]
    [InlineData(625)]
    [InlineData(626)]
    [InlineData(627)]
    public void InstalledSneakyUsesUnprojectedReflectionAndOptionalSignedStageOne(int id)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        var root=new EnbSceneEffectSnapshot(1,1,42,0,(ushort)id,0,0,"",0,false,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var authored=assets.PresentationCatalog!.FindEffect(id)!;
        var node=new EnbCurrentRetailEffectOwner(1,authored,root,42,true,0);
        Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(node,out var definition,out var error),error);
        Assert.False(definition.ProjectStage0);Assert.True(definition.ResetMappers);
        Assert.Equal(id==626,definition.Stage1BoxScale.HasValue);
        if(id==626)Assert.Equal(new System.Numerics.Vector2(4,-.5f),definition.Stage1BoxScale);
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,definition.Swatch,out var source,out error),error);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out error),error);
        var pass=Assert.Single(Assert.Single(resource.Meshes).MaterialPasses);
        Assert.True(RetailTestShims.IsReflectionMapper(pass.SingleBindingSemantics!.Value.Mapper0Program));
        definition.ValidateSwatch(pass);
        (definition with {ProjectStage0=true}).ValidateSwatch(pass);
        (definition with {ResetMappers=false}).ValidateSwatch(pass);
        if(id==626)(definition with {Stage1BoxScale=null}).ValidateSwatch(pass);
    }
}
