using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStealthTacticsPassTests
{
    [Theory]
    [InlineData(328)]
    [InlineData(329)]
    [InlineData(677)]
    public void InstalledStealthPassProjectsOnlyStageZero(int id)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        var root=Root(id);
        var node=new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],assets.PresentationCatalog!,0).VisualNodes.Single(n=>n.Definition.DefinitionId==id);
        Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(node,out var definition,out var error),error);
        Assert.True(definition.ProjectStage0);Assert.Null(definition.Stage1BoxScale);Assert.True(definition.ResetMappers);
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,definition.Swatch,out var source,out error),error);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out error),error);
        var pass=Assert.Single(Assert.Single(resource.Meshes).MaterialPasses);
        Assert.True(RetailTestShims.IsReflectionSecondStage(pass));
        definition.ValidateSwatch(pass);
        (definition with {Stage1BoxScale=Vector2.One}).ValidateSwatch(pass);
        (definition with {ResetMappers=false}).ValidateSwatch(pass);
    }

    [Fact]
    public void InstalledStealthCancellationEndsZeroCycleLinkAndKeepsRestorationCycle()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=Root(328);var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        foreach(var (tick,id) in new (uint,int)[]{(0,328),(999,328),(1000,329),(6000,329),(6050,678),(9949,678),(9950,0)})
        {
            var active=scheduler.Update(tick<6050?[root]:[],catalog,tick).VisualNodes.Where(n=>n.Active).ToArray();
            if(id==0)Assert.Empty(active);
            else
            {
                var node=Assert.Single(active);Assert.Equal(id,node.Definition.DefinitionId);Assert.Equal(1u,node.BoundGameId);
                Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(node,out var definition,out var error),error);
                Assert.Equal(id==677,definition.HideBaseMesh);
            }
        }
        Assert.Equal(0,scheduler.TrackedRootCount);
        Assert.Contains(" E677 NextLink duration 0ms cancel True",scheduler.LifecycleHistory);
    }

    [Fact]
    public void DirectExitKeepsAuthoredHiddenSecondBeforeRestoration()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=Root(677);var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        foreach(var (tick,id) in new (uint,int)[]{(0,677),(999,677),(1000,678),(4899,678),(4900,0)})
        {
            var active=scheduler.Update([root],catalog,tick).VisualNodes.Where(n=>n.Active).ToArray();
            if(id==0)Assert.Empty(active);
            else
            {
                var node=Assert.Single(active);Assert.Equal(id,node.Definition.DefinitionId);
                Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(node,out var d,out var error),error);
                Assert.Equal(id==677,d.HideBaseMesh);
            }
        }
        scheduler.Update([],catalog,4901);Assert.Equal(0,scheduler.TrackedRootCount);
    }

    private static EnbSceneEffectSnapshot Root(int id)=>new(10,1,1,0,(ushort)id,0,0,"",0,false,1,default,0,0,false,
        default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
}
