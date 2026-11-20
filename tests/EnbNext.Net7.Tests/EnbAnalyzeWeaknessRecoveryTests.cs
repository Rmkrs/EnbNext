using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbAnalyzeWeaknessRecoveryTests
{
    [Theory]
    [InlineData(211)]
    [InlineData(10247)]
    public void AbsentInactiveSecondTextureRecoversOnlyProvenFirstStage(int id)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        var root=new EnbSceneEffectSnapshot(1,1,42,0,(ushort)id,0,0,"",0,false,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var node=new EnbCurrentRetailEffectOwner(1,assets.PresentationCatalog!.FindEffect(id)!,root,42,true,0);
        Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(node,out var definition,out var error),error);
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,definition.Swatch,out var source,out error),error);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out error),error);
        var mesh=Assert.Single(resource.Meshes);var pass=Assert.Single(mesh.MaterialPasses);
        Assert.Single(pass.TextureStages);Assert.NotNull(definition.Stage1BoxScale);
        Assert.NotNull(definition.GetRecoveryDiagnostic(pass));definition.ValidateSwatch(pass);
        var s=pass.SingleBindingSemantics!.Value;
        var active=(pass).Copy().SetSingleBindingSemantics(s with {FixedFunction=s.FixedFunction with {
            TextureCombiners=s.FixedFunction.TextureCombiners with {Selector1=2}}});
        // 006EE260 skips an absent stage-one texture independently of the
        // shader selector, mapper reset and stage-zero projection settings.
        Assert.NotNull(definition.GetRecoveryDiagnostic(active));definition.ValidateSwatch(active);
        var reset=definition with {ResetMappers=true};
        Assert.NotNull(reset.GetRecoveryDiagnostic(pass));reset.ValidateSwatch(pass);
        var unprojected=definition with {ProjectStage0=false};
        Assert.NotNull(unprojected.GetRecoveryDiagnostic(pass));unprojected.ValidateSwatch(pass);
        foreach(var admitted in new[]{definition,reset})
        {
            var bound=admitted.BindToTargetMesh(active,(mesh).Copy().SetRetailTextureCoordinateSets([]));
            Assert.Single(bound.TextureStages);
            Assert.Equal(active.SingleBindingSemantics,bound.SingleBindingSemantics);
            Assert.All(bound.TextureStages[0].EffectiveRuntimeTexCoords,uv=>Assert.Equal(System.Numerics.Vector2.Zero,uv));
        }
        Assert.Null((definition with {Stage1BoxScale=null}).GetRecoveryDiagnostic(pass));
    }
}
