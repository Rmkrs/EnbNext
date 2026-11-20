using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbUnprojectedPassTests
{
    [InstalledRetailTheory]
    [InlineData(338)]
    [InlineData(340)]
    [InlineData(342)]
    [InlineData(10060)]
    [InlineData(10085)]
    public void InstalledGeneratedSwatchNeedsNoInventedProjector(int id)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        var root=new EnbSceneEffectSnapshot(1,1,42,0,(ushort)id,0,0,"",0,false,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var node=new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],assets.PresentationCatalog!,0).VisualNodes.Single(n=>n.Definition.DefinitionId==id);
        Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(node,out var definition,out var error),error);
        Assert.False(definition.ProjectStage0);Assert.Null(definition.Stage1BoxScale);
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,definition.Swatch,out var source,out error),error);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out error),error);
        var mesh=Assert.Single(resource.Meshes);var pass=Assert.Single(mesh.MaterialPasses);
        definition.ValidateSwatch(pass);
        var semantics=pass.SingleBindingSemantics!.Value;
        Assert.True(EnbCurrentRetailPassEffectDefinition.IsProjectedLinearParent(semantics.Mapper0Program));
        Assert.True(EnbCurrentRetailPassEffectDefinition.IsDisabledSecondStage(pass));
        var bound=EnbCurrentRetailPassEffectDefinition.BindToMesh(pass,mesh);
        Assert.Equal(semantics,bound.SingleBindingSemantics);
        (definition with {Stage1BoxScale=Vector2.One}).ValidateSwatch(pass);
        var requiresUv=(pass).Copy().SetSingleBindingSemantics(semantics with {Mapper0Program=semantics.Mapper0Program with {
            Kind=EnbW3dMapperRuntimeProgramKind.Linear,CoordinateSource=EnbW3dMapperCoordinateSource.PassThrough,TransformShape=EnbW3dMapperTransformShape.Count2}});
        // Admission accepts the mapper. Target UV availability is checked by
        // the binding consumer, not by the swatch definition validator.
        definition.ValidateSwatch(requiresUv);
        Assert.Throws<InvalidDataException>(()=>definition.BindToTargetMesh(requiresUv,(mesh).Copy().SetRetailTextureCoordinateSets([])));
    }
}
