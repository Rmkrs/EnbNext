using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbInactivePassStageTests
{
    [Fact]
    public void InstalledRallySurfaceKeepsSharedClockWithDisabledSecondStage()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        var root=new EnbSceneEffectSnapshot(1,1,42,0,325,0,0,"",0,false,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var node=new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],assets.PresentationCatalog!,0).VisualNodes.Single();
        Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(node,out var definition,out var error),error);
        Assert.True(definition.ProjectStage0);Assert.False(definition.ResetMappers);Assert.Null(definition.Stage1BoxScale);
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,definition.Swatch,out var source,out error),error);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out error),error);
        var pass=Assert.Single(Assert.Single(resource.Meshes).MaterialPasses);
        Assert.Equal(2,pass.TextureStages.Count);
        Assert.True(EnbCurrentRetailPassEffectDefinition.IsDisabledSecondStage(pass));
        definition.ValidateSwatch(pass);
        (definition with {Stage1BoxScale=Vector2.One}).ValidateSwatch(pass);
    }
}
