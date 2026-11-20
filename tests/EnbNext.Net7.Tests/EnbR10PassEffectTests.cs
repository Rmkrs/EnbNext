using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10PassEffectTests
{
    [Fact]
    public void EmptySwatchUsesNativeStockDescriptorWithoutRejectingTheEffect()
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-1]\nEffectClass=PassEffect\nDuration=1\nHideBaseMesh=true");
        var root=new EnbSceneEffectSnapshot(1,1,42,0,1,0,0,"",0,false,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0).VisualNodes);
        Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(node,out var definition,out var why),why);
        Assert.Equal("",definition.Swatch);
        var texture=new EnbW3dRenderTextureBinding(0,"missingtexture.tga",null){ResolveLogicalNameAtRuntime=true};
        var pass=EnbCurrentRetailPassEffectDefinition.CreateMissingSwatchDescriptor(texture);
        definition.ValidateSwatch(pass);
        Assert.Equal(0x9441bu,pass.CurrentRetailUniformShader);
        Assert.Same(texture,Assert.Single(pass.TextureStages).CurrentRetailUniformTexture);
        var second=EnbCurrentRetailPassEffectDefinition.CreateMissingSwatchDescriptor(texture);
        Assert.Same(pass.CurrentRetailUniformMaterial!.Value.Material,second.CurrentRetailUniformMaterial!.Value.Material);
        var source=EnbR4W3dRenderResourceTests.BuildResolvedResource(0);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out why),why);
        var bound=definition.BindToTargetMesh(pass,resource.Meshes[0]);
        Assert.NotEmpty(bound.TriangleBindings);
        Assert.Equal(0x9441bu,bound.SingleBindingSemantics!.Value.FixedFunction.PackedRetailState);
    }
    [Theory]
    [InlineData(0f)] [InlineData(-2f)] [InlineData(.5f)] [InlineData(3f)]
    public void PassFactoryIgnoresCommandAndAuthoredRenderObjectScale(float scale)
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-1]\nEffectClass=PassEffect\nSwatch=fixture\nDuration=1\nRObjScale=7\nTrackSource=false\nProjection0Axis=Z\nProjection0BoxScale=2,3");
        var root=new EnbSceneEffectSnapshot(1,1,42,0,1,0,0,"",0,false,scale,default,0,0,false,default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0).VisualNodes);
        Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(node,out var actual,out var error),error);
        Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(EffectOwnerFixture.Copy(node,copy=>{copy.Root=root with {Scale=1};}),out var control,out error),error);
        Assert.Equal(control,actual);
        Assert.Equal(control.ProjectionFromWorld(Matrix4x4.Identity,new(10,20,30),256),
            actual.ProjectionFromWorld(Matrix4x4.Identity,new(10,20,30),256));
    }

    [Fact]
    public void SwatchCopiesPassZeroUniformSlotsInsteadOfExpandedDrawBindings()
    {
        var source = EnbR4W3dRenderResourceTests.BuildResolvedResource(0);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source, out var resource, out var error), error);
        var mesh = resource.Meshes[0];
        var original = mesh.MaterialPasses[0];
        var expected = EnbCurrentRetailPassEffectDefinition.CaptureSwatchPass(mesh);
        // These arrays control the source mesh's draw. The native constructor
        // reads +74/+78/+94/+A4 directly and never consults those arrays.
        var expanded = (original).Copy().SetVertexMaterialCardinality(EnbW3dRenderBindingCardinality.PerVertex).SetShaderCardinality(EnbW3dRenderBindingCardinality.PerTriangle).SetVertexMaterials([]).SetShaders([]).SetTriangleBindings([]).SetSingleBindingSemantics(null).SetTextureStages(original.TextureStages.Select(stage => (stage).Copy().SetBindingCardinality(EnbW3dRenderBindingCardinality.PerTriangle).SetTextures([])).ToArray());
        var actual = EnbCurrentRetailPassEffectDefinition.CaptureSwatchPass((mesh).Copy().SetMaterialPasses([expanded, (original).Copy().SetOrdinal(1).SetCurrentRetailUniformShader(0)]));
        Assert.Equal(expected.SingleBindingSemantics, actual.SingleBindingSemantics);
        Assert.Equal(expected.VertexMaterials, actual.VertexMaterials);
        Assert.Equal(expected.Shaders, actual.Shaders);
        Assert.Equal(expected.TextureStages.SelectMany(stage => stage.Textures), actual.TextureStages.SelectMany(stage => stage.Textures));
        Assert.All(actual.TriangleBindings, triangle => Assert.Equal(expected.SingleBindingSemantics, triangle.Semantics));
        Assert.NotEmpty(actual.TriangleBindings);
        var bound = EnbCurrentRetailPassEffectDefinition.BindToMesh(actual, mesh);
        Assert.Equal(mesh.Indices.Count / 3, bound.TriangleBindings.Count);
    }

    [Fact]
    public void WrappedBumpCallsLinearMatrixWithoutAdvancingApplyState()
    {
        Assert.True(EnbW3dMapperRuntimeProgramCompiler.TryCompile(new(EnbW3dMapperTypeSelector.Mode12,
            "BumpRotation=.2\nBumpScale=.7",1,1,.1f,-.2f,0,0,false),out var program,out _));
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program,1000,out var wrapped,out _));
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program,1000,out var direct,out _));
        var matrix=wrapped.EvaluateMatrix(1250);
        Assert.False(matrix.ConfiguresBumpEnvironment);
        var apply=direct.Evaluate(1250);
        Assert.Equal(apply.TextureOffset,matrix.TextureOffset);
        // A later ordinary apply still owns the complete elapsed bump rotation.
        Assert.Equal(direct.Evaluate(1500).BumpEnvironmentMatrix,wrapped.Evaluate(1500).BumpEnvironmentMatrix);
    }

    [Theory]
    [InlineData('X',-5,-6,20,30)]
    [InlineData('Y',4,-6,10,30)]
    [InlineData('Z',-5,-4,20,10)]
    public void AllNativeProjectionFormsUseSelectedExtentsAndWorldPrecedence(char axis,float u,float v,float ex,float ey)
    {
        var extent=new Vector3(10,20,30);var point=new Vector3(4,5,6);float bias=254f/512;
        foreach(var kind in Enum.GetValues<EnbPassProjectionKind>())
        {
            var definition=new EnbCurrentRetailPassEffectDefinition("fixture",new(2,3),false,ProjectionAxis:axis){ProjectionKind=kind};
            var actual=Vector3.Transform(point,definition.ProjectionFromWorld(Matrix4x4.Identity,extent,256));
            float dx=kind==EnbPassProjectionKind.World?1:kind==EnbPassProjectionKind.Box?ex/2:MathF.Sqrt(ex*ex+ey*ey)/2;
            float dy=kind==EnbPassProjectionKind.World?1:kind==EnbPassProjectionKind.Box?ey/3:dx;
            Assert.Equal(bias*(1+u/dx),actual.X,5);Assert.Equal(bias*(1+v/dy),actual.Y,5);
        }
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText($"[EFFECT-1]\nEffectClass=PassEffect\nSwatch=fixture\nDuration=1\nProjection0Axis={axis}\nProjection0WorldScale=80\nProjection0BoxScale=invalid\nProjection0CylinderScale=invalid\nProjection1Axis={axis}");
        var root=new EnbSceneEffectSnapshot(1,1,42,0,1,0,0,"",0,false,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0).VisualNodes);
        Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(node,out var parsed,out var error),error);
        Assert.Equal(EnbPassProjectionKind.World,parsed.ProjectionKind);Assert.Equal(new Vector2(80),parsed.BoxScale);
        Assert.Equal(EnbPassProjectionKind.Cylinder,parsed.Stage1ProjectionKind);Assert.Equal(Vector2.One,parsed.Stage1BoxScale);
    }

    [Theory]
    [InlineData(EnbW3dMapperTypeSelector.Mode0C)]
    [InlineData(EnbW3dMapperTypeSelector.Mode13)]
    [InlineData(EnbW3dMapperTypeSelector.Mode14)]
    public void WrappedWorldMapperUsesInverseViewBasisAndConstantThirdCoordinate(EnbW3dMapperTypeSelector selector)
    {
        Assert.True(EnbW3dMapperRuntimeProgramCompiler.TryCompile(new(selector,"Axis=X\nLog2Width=1\nFPS=0",1,1,0,0,0,0,false),out var program,out _));
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program,0,out var state,out _));var evaluated=state.Evaluate(0);
        var view=Matrix4x4.CreateRotationZ(.6f)*Matrix4x4.CreateTranslation(10,20,30);
        var actual=EnbCurrentRetailPassEffectDefinition.ComposeParent(Matrix4x4.Identity,Matrix4x4.Identity,view,program,evaluated);
        var p=new Vector3(.2f,.7f,123);
        var nativeInput=new Vector3(p.X,p.Y,1);
        var rotated=Vector3.TransformNormal(nativeInput,Matrix4x4.CreateRotationZ(-.6f));
        var uv=Vector3.Transform(p,actual);
        Assert.Equal(rotated.Y*evaluated.TextureScale.X+evaluated.TextureOffset.X,uv.X,5);
        Assert.Equal(rotated.Z*evaluated.TextureScale.Y+evaluated.TextureOffset.Y,uv.Y,5);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelfTargetedUnaryChainPreservesEndpointSideAndBone(bool target)
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText($"[EFFECT-1]\nEffectClass=LineEffect\nDuration=1\nStartLinkID=2\nUnaryStartLinkToTarget={target}\n[EFFECT-2]\nEffectClass=PassEffect\nDuration=1\nSwatch=authored\nStartLinkID=3\nUnaryStartLinkToTarget={!target}\n[EFFECT-3]\nEffectClass=RObjEffect\nBaseAssetID=1098\nDuration=1");
        var root=new EnbSceneEffectSnapshot(1,1,42,42,1,0,0,"TRACTOR",0,true,1,default,0,0,false,default,1,
            EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var update=new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0);
        Assert.Single(update.VisualNodes,n=>n.Definition.DefinitionId==3);
        foreach(var node in update.VisualNodes.Where(n=>n.Definition.DefinitionId is 2 or 3))
        {
            Assert.Equal(42u,node.BoundGameId);Assert.Equal(target,node.BoundToTarget);
            Assert.Equal(target?null:"TRACTOR",node.UnaryBoneName);
        }
        var pass=Assert.Single(update.VisualNodes,n=>n.Definition.DefinitionId==2);
        Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(pass,out _,out var error),error);
    }

    [Theory]
    [InlineData(EnbSceneEffectGeometryKind.ObjectToObject)]
    [InlineData(EnbSceneEffectGeometryKind.ObjectToObjectDurationLinked)]
    public void BinaryJumpstartChainBindsSurfaceToRecipientAndRetiresWithRoot(EnbSceneEffectGeometryKind geometry)
    {
        var catalog = new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-414]\nEffectClass=RObjLookAtEffect\nDuration=.5\nNextLinkID=415\n[EFFECT-415]\nEffectClass=RObjLookAtEffect\nDuration=INFINITE\nStartLinkID=417\nUnaryStartLinkToTarget=TRUE\n[EFFECT-417]\nEffectClass=PassEffect\nDuration=INFINITE\nCycleTime=.5\nSwatch=swatch_jstart01\nProjection0Axis=Z\nProjection0BoxScale=2,.5\nProjection1Axis=Z\nProjection1BoxScale=1,1\nResetMappers=TRUE");
        var root = new EnbSceneEffectSnapshot(1, 1, 42, 43, 414, 0, 0, "", 0, false, 1, default, 0, 0, false, default, 1, geometry, default, -1, default, 0, 0, null);
        var runtime = new EnbCurrentRetailEffectRslSchedulerRuntime();
        runtime.Update([root], catalog, 0);
        var pass = Assert.Single(runtime.Update([root], catalog, 500).VisualNodes, n => n.Definition.DefinitionId == 417);
        Assert.Equal(43u, pass.BoundGameId);
        Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(pass, out var definition, out var error), error);
        Assert.Equal(new Vector2(2, .5f), definition.BoxScale);
        Assert.Equal(Vector2.One, definition.Stage1BoxScale);
        // The native target-child setter passes the recipient object, without
        // the binary ancestor's source bone or target-coordinate offset.
        var offsetBeam=EffectOwnerFixture.Copy(pass,copy=>{copy.Root=root with {BoneName="WEAPON",SpatialOffset=new(25,10,5)};});
        Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(offsetBeam,out _,out error),error);
        Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(EffectOwnerFixture.Copy(offsetBeam,copy=>{copy.BoundGameId=root.SourceGameId;}),out _,out error),error);
        Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(EffectOwnerFixture.Copy(offsetBeam,copy=>{copy.BoundGameId=root.SourceGameId;copy.Root=offsetBeam.Root with {BoneName=""};}),out _,out error),error);
        Assert.False(EnbCurrentRetailPassEffectDefinition.TryRead(EffectOwnerFixture.Copy(pass,copy=>{copy.LinkKind = EnbCurrentRetailEffectLinkKind.Root;}), out _, out _));
        runtime.Update([], catalog, 601);
        Assert.Empty(runtime.Update([], catalog, 1000).VisualNodes);
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(0x4000u, true)] // Source-only blending is not excluded.
    [InlineData(0x8000u, true)]
    [InlineData(0x20u, false)]
    [InlineData(0xe0u, false)]
    [InlineData(0x40000u, false)]
    public void PassEligibilityUsesExactRetailShaderPredicate(uint shader, bool expected)
        => Assert.Equal(expected, EnbCurrentRetailPassEffectDefinition.CanApplyToShader(shader));

    [Fact]
    public void EligibilityUsesPassZeroAndLoaderSortLatchNotLaterPasses()
    {
        EnbW3dRenderMaterialPass Pass(uint bits, int ordinal)
        {
            Assert.True(EnbW3dMaterialCompiler.TryDecodePackedRetailState(bits, out var state, out _));
            return new(ordinal, [], EnbW3dRenderBindingCardinality.None, [], EnbW3dRenderBindingCardinality.Uniform,
                [], [], [], [], [], new(state, default, default, default, default, default));
        }
        var mesh = new EnbW3dRenderMesh(0, "fixture", "fixture", 1, [], [], [], [], [], [], [Pass(0, 0), Pass(0x20, 1)]);
        Assert.True(EnbCurrentRetailPassEffectDefinition.CanApplyToMesh(mesh));
        Assert.False(EnbCurrentRetailPassEffectDefinition.CanApplyToMesh((mesh).Copy().SetCurrentRetailGeometrySortRequestOverride(true)));
        Assert.False(EnbCurrentRetailPassEffectDefinition.CanApplyToMesh((mesh).Copy().SetMaterialPasses([Pass(0x40000, 0)])));
    }

    [Theory]
    [InlineData("2,2", true)]
    [InlineData("-1,-1", true)]
    [InlineData("-2,3", true)]
    [InlineData("2,-3", true)]
    [InlineData("NaN,2", false)]
    [InlineData("0,2", true)]
    [InlineData("2", false)]
    public void DefinitionRejectsUnsupportedOrInvalidProjection(string scale, bool valid)
    {
        var catalog = new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText($"[EFFECT-1]\nEffectClass=PassEffect\nDuration=INFINITE\nSwatch=authored\nProjection0Axis=Z\nProjection0BoxScale={scale}");
        var root = new EnbSceneEffectSnapshot(1, 1, 42, 0, 1, 0, 0, "", 0, false, 1, default, 0, 0, false, default, 1, EnbSceneEffectGeometryKind.Object, default, -1, default, 0, 0, null);
        var node = Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root], catalog, 0).VisualNodes);
        Assert.Equal(valid, EnbCurrentRetailPassEffectDefinition.TryRead(node, out _, out _));
        Assert.Equal(valid,EnbCurrentRetailPassEffectDefinition.TryRead(EffectOwnerFixture.Copy(node,copy=>{copy.Root = root with { BoneName = "bone" };}), out _, out _));
    }

    [Fact]
    public void NegativeBoxScaleReflectsAroundTheUnchangedTextureBias()
    {
        var positive=new EnbCurrentRetailPassEffectDefinition("fixture",new(2,3),false);
        var negative=positive with {BoxScale=new(-2,-3)};
        var point=new Vector3(4,5,6);
        var a=Vector3.Transform(point,positive.ProjectionFromWorld(Matrix4x4.Identity,new(10,20,30),256));
        var b=Vector3.Transform(point,negative.ProjectionFromWorld(Matrix4x4.Identity,new(10,20,30),256));
        Assert.Equal(254f/256,a.X+b.X,5);
        Assert.Equal(254f/256,a.Y+b.Y,5);
        Assert.Equal(a.Z,b.Z);
    }

    [Fact]
    public void VisualNodesFollowPhasesEvenWithoutSoundAndDisappearOnRemoval()
    {
        var catalog = new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-1]\nEffectClass=PassEffect\nDuration=.6\nNextLinkID=2\n[EFFECT-2]\nEffectClass=PassEffect\nDuration=INFINITE\nHideBaseMesh=TRUE");
        var root = new EnbSceneEffectSnapshot(1, 1, 42, 0, 1, 0, 0, "", 0, false, 1, default, 0, 0, false, default, 1, EnbSceneEffectGeometryKind.Object, default, -1, default, 0, 0, null);
        var runtime = new EnbCurrentRetailEffectRslSchedulerRuntime();
        var first = Assert.Single(runtime.Update([root], catalog, 0).VisualNodes);
        Assert.True(first.Active); Assert.Equal(42u, first.BoundGameId);
        var next = Assert.Single(runtime.Update([root], catalog, 600).VisualNodes);
        Assert.Equal(2, next.Definition.DefinitionId); Assert.NotEqual(first.Sequence, next.Sequence);
        Assert.Empty(runtime.Update([], catalog, 601).VisualNodes);
        Assert.NotEqual(next.Root.InstanceGeneration, Assert.Single(runtime.Update([root with { InstanceGeneration = 2 }], catalog, 602).VisualNodes).Root.InstanceGeneration);
        runtime.Clear(); Assert.Empty(runtime.Update([], catalog, 603).VisualNodes);
    }

    [Fact]
    public void ZBoxProjectionUsesOwnerSpaceAndRetailTextureGuardBand()
    {
        var definition = new EnbCurrentRetailPassEffectDefinition("authored", new(2, 2), true);
        var owner = Matrix4x4.CreateRotationZ(.4f) * Matrix4x4.CreateTranslation(700, 800, 900);
        Matrix4x4.Invert(owner, out var inverse);
        var projection = definition.ProjectionFromWorld(inverse, new(10, 20, 30), 64);
        float bias = 62f / 128;
        var center = Vector3.Transform(new(700, 800, 900), projection);
        Assert.InRange(Math.Abs(center.X - bias), 0, 1e-5f); Assert.InRange(Math.Abs(center.Y - bias), 0, 1e-5f);
        var sample = Vector3.Transform(Vector3.Transform(new Vector3(5, 10, 0), owner), projection);
        Assert.InRange(Math.Abs(sample.X), 0, 1e-5f); Assert.InRange(Math.Abs(sample.Y), 0, 1e-5f);
    }

    [Theory]
    [InlineData("",false,false,true)]
    [InlineData("Projection0Axis=Z\nProjection0BoxScale=2,2\nResetMappers=TRUE",true,false,true)]
    [InlineData("Projection0Axis=Z\nProjection0BoxScale=2,2\nProjection1Axis=Z\nProjection1BoxScale=1,1\nResetMappers=TRUE",true,true,true)]
    [InlineData("Projection1Axis=X\nProjection1BoxScale=1,1",false,true,true)]
    [InlineData("Projection1Axis=Z\nProjection1BoxScale=NaN,1",false,false,false)]
    [InlineData("Projection0BoxScale=2,2",false,false,true)]
    [InlineData("Projection0Axis=Z\nProjection0WorldScale=2",true,false,true)]
    public void FoldProjectionFamiliesAreExplicit(string properties,bool stage0,bool stage1,bool valid)
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-1]\nEffectClass=PassEffect\nSwatch=fixture\nDuration=1\n"+properties);
        var root=new EnbSceneEffectSnapshot(1,1,42,0,1,0,0,"",0,false,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0).VisualNodes);
        Assert.Equal(valid,EnbCurrentRetailPassEffectDefinition.TryRead(node,out var d,out _));
        if (!valid) return;
        Assert.Equal(stage0,d.ProjectStage0); Assert.Equal(stage1,d.Stage1BoxScale.HasValue);
        Assert.Equal(properties.Contains("ResetMappers=TRUE"),d.ResetMappers);
    }

    [Fact]
    public void InstalledFoldSwatchesBindBothStagesAndTexturelessInvisibility()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();
        if (paths is null) return;
        var assets=EnbRetailAssetBundle.Load(paths);
        foreach (var (id,stages) in new[]{(203,2),(204,2),(666,0),(268,0),(270,2),(271,2)})
        {
            var root=new EnbSceneEffectSnapshot(1,1,42,0,(ushort)id,0,0,"",0,false,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
            var node=new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],assets.PresentationCatalog!,0).VisualNodes.Single(n=>n.Definition.DefinitionId==id);
            Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(node,out var d,out var error),error);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,d.Swatch,out var source,out error),error);
            Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out error),error);
            var mesh=Assert.Single(resource.Meshes); var pass=Assert.Single(mesh.MaterialPasses);
            Assert.Equal(stages,pass.TextureStages.Count);
            var captured=EnbCurrentRetailPassEffectDefinition.CaptureSwatchPass(mesh);
            Assert.Equal(stages,captured.TextureStages.Count);
            d.ValidateSwatch(captured);
            d.ValidateSwatch(pass);
            var bound=EnbCurrentRetailPassEffectDefinition.BindToMesh(pass,mesh);
            Assert.Equal(stages,bound.TextureStages.Count);
            for(int i=0;i<stages;i++)
            {
                Assert.Equal(i,bound.TextureStages[i].EffectiveTextureCoordinateSetIndex);
                Assert.Equal(mesh.Positions.Count,bound.TextureStages[i].EffectiveRuntimeTexCoords.Count);
                Assert.Equal(pass.TextureStages[i].Textures,bound.TextureStages[i].Textures);
            }
            for(int i=0;i<bound.TriangleBindings.Count;i++) Assert.Equal((int)mesh.Indices[i*3],bound.TriangleBindings[i].MaterialVertexIndex);
            if (stages==2) (d with { ResetMappers=false }).ValidateSwatch(pass);
        }
    }

}
