using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR9YCurrentRetailShipDecalTests
{
    [Fact]
    public void DecalClampsUnorderedAndInfiniteComponentsWithoutRejectingTheUpdate()
    {
        var unusual=new Vector4(float.NaN,float.PositiveInfinity,float.NegativeInfinity,1);
        var target=CreatePacket("SHIP.DECAL020");
        var result=EnbCurrentRetailShipDecalRuntime.Apply(target,[new(2,1,EncodeColor(unusual))],CreateCatalog(),out var receipt);
        var control=EnbCurrentRetailShipDecalRuntime.Apply(target,[new(2,1,EncodeColor(new(0,1,0,1)))],CreateCatalog(),out var expected);
        Assert.True(receipt.Applied);
        Assert.Equal(expected,receipt);
        Assert.Equal(control.Material.Source.TriangleBindings[0].Semantics,result.Material.Source.TriangleBindings[0].Semantics);
    }

    [Fact]
    public void PositiveDecalRestoresDefaultDescriptorAndInstallsMissingTextureStage()
    {
        var target = CreatePacket("SHIP.DECAL020");
        var source = (target.Material.Source).Copy().SetTextureStages([]);
        target = (target).ForkTask().SetMaterial(new(source, source.SingleBindingSemantics));
        var updated = EnbCurrentRetailShipDecalRuntime.Apply(target,
            [new(2, 1, EncodeColor(Vector4.One))], CreateCatalog(), out var receipt);
        Assert.Equal(EnbCurrentRetailShipDecalOutcome.AppliedTexture, receipt.Outcome);
        var pass = updated.Material.Source;
        Assert.Equal(0x985B3u, pass.CurrentRetailUniformShader);
        var uniforms = pass.TriangleBindings[0].Semantics!.Value.Uniforms;
        Assert.Equal(Vector3.One, uniforms.Ambient);
        Assert.Equal(Vector3.One, uniforms.Diffuse);
        Assert.Equal(Vector3.Zero, uniforms.Specular);
        Assert.Equal(Vector3.Zero, uniforms.Emissive);
        Assert.Equal(0f, uniforms.Power);
        Assert.Equal("insignia.tga", pass.TextureStages[0].Textures[0].LogicalName);
        var extra = (target).ForkTask().SetMaterial(new((source).Copy().SetOrdinal(1), source.SingleBindingSemantics));
        EnbCurrentRetailShipDecalRuntime.Apply(extra,
            [new(2, 1, EncodeColor(Vector4.One))], CreateCatalog(), out var omitted);
        Assert.Equal(EnbCurrentRetailShipDecalOutcome.OmittedDescriptorPass, omitted.Outcome);
        Assert.True(omitted.Applied);
    }

    [Fact]
    public void PositiveTintPreservesExpandedTargetMaterialArray()
    {
        var target = CreatePacket("SHIP.DECAL020");
        var source = target.Material.Source;
        source = (source).Copy().SetVertexMaterialCardinality(EnbW3dRenderBindingCardinality.PerVertex).SetVertexMaterials(Enumerable.Repeat(source.VertexMaterials[0], 3).ToArray());
        target = (target).ForkTask().SetMaterial(new(source, source.SingleBindingSemantics));
        var updated = EnbCurrentRetailShipDecalRuntime.Apply(target,
            [new(2, 1, EncodeColor(new Vector4(0.2f, 0.4f, 0.6f, 0.8f)))],
            CreateCatalog(), out var receipt);
        Assert.True(receipt.Applied);
        Assert.Same(source.VertexMaterials, updated.Material.Source.VertexMaterials);
        Assert.Null(updated.Material.Source.VertexMaterials[0].Material.CurrentRetailUniformOverride);
        Assert.Equal(new Vector3(0.2f, 0.4f, 0.6f),
            updated.Material.Source.CurrentRetailUniformMaterial!.Value.Material.CurrentRetailUniformOverride!.Value.Diffuse);
    }
    [Fact]
    public void ExpandedTextureBindingSurvivesDecalDescriptorReplacement()
    {
        var source = CreatePacket("SHIP.DECAL020");
        var pass = source.Material.Source;
        pass = (pass).Copy().SetTextureStages([(pass.TextureStages[0]).Copy().SetBindingCardinality(EnbW3dRenderBindingCardinality.PerTriangle)]);
        source = (source).ForkTask().SetMaterial(new(pass, pass.SingleBindingSemantics));
        var updated = EnbCurrentRetailShipDecalRuntime.Apply(source,
            [new(2, 1, EncodeColor(Vector4.One))], CreateCatalog(), out var receipt);
        Assert.True(receipt.Applied);
        Assert.Same(pass.TextureStages[0].Textures, updated.Material.Source.TextureStages[0].Textures); Assert.Equal("insignia.tga", updated.Material.Source.TextureStages[0].CurrentRetailUniformTexture!.LogicalName);
        Assert.Same(pass.TriangleBindings[0].Textures[0], updated.Material.Source.TriangleBindings[0].Textures[0]);
    }
    [Fact]
    public void RepeatedDecalReplacesMaterialWithoutRestartingRenderObject()
    {
        var world = new EnbNext.Net7.World.Net7WorldState();
        world.MaterializeCreate(new EnbNext.Net7.World.Net7CreateUpdate(71, 1f, 10, 0, 0f, 0f, 0f));
        var obj = Assert.Single(new EnbPresentationSnapshotBuilder().Build(world, null, 1).Objects) with
        {
            Decals = [new(2, 1, EncodeColor(Vector4.One))], DecalApplyCount = 1
        };
        var source = CreatePacket("SHIP.DECAL020");
        var cache = new EnbShipMaterialCache();
        var catalog = CreateCatalog();
        EnbW3dDrawTask Apply(EnbW3dDrawTask packet, EnbSceneObjectSnapshot owner) =>
            cache.ApplySurface(packet, owner, "root", 0, null, false, null, catalog, out _);
        var first = Apply(source, obj);
        Assert.Equal(1, first.CurrentRetailDecalRevision);
        Assert.Same(first.Material, Apply(source, obj).Material);
        var second = Apply(source, obj with { DecalApplyCount = 2 });
        Assert.Equal(2, second.CurrentRetailDecalRevision);
        Assert.NotSame(first.Material, second.Material);
        Assert.Equal(first.VisualGeneration, second.VisualGeneration);
        Assert.Same(second.Material, Apply(source, obj with { DecalApplyCount = 2 }).Material);
        var unrelated = CreatePacket("SHIP.HULL");
        var untouched = Apply(unrelated, obj);
        Assert.Same(untouched.Material, Apply(unrelated, obj with { DecalApplyCount = 2 }).Material);
        Assert.Equal(0, untouched.CurrentRetailDecalRevision);
    }
    [Fact]
    public void NormalTextureDecalForcesManagerClampAndAppliesRetailTint()
    {
        EnbW3dDrawTask source = CreatePacket("SHIP.DECAL020");
        EnbDecalDefinitionCatalog catalog = CreateCatalog();
        Assert.True(catalog.NormalTextures[1].TextureWrap); // Parsed loader input; manager-final state still clamps.
        ImmutableArray<EnbSceneDecalSnapshot> entries =
        [
            new(2, 1, EncodeColor(new Vector4(0.2f, 0.4f, 0.6f, 0.8f))),
        ];

        EnbW3dDrawTask effective = EnbCurrentRetailShipDecalRuntime.Apply(
            source,
            entries,
            catalog,
            out EnbCurrentRetailShipDecalReceipt receipt);

        Assert.Equal(EnbCurrentRetailShipDecalOutcome.AppliedTextureAndTint, receipt.Outcome);
        Assert.Equal(2, receipt.Slot);
        Assert.Equal(1, receipt.DecalId);
        Assert.Equal("insignia.tga", receipt.TextureName);
        Assert.NotSame(source, effective);
        Assert.Equal("noseart.tga", source.Material.Source.TextureStages[0].Textures[0].LogicalName);

        EnbW3dRenderTextureBinding texture = effective.Material.Source.TextureStages[0].Textures[0];
        Assert.Equal("insignia.tga", texture.LogicalName);
        Assert.True(texture.ResolveLogicalNameAtRuntime);
        Assert.False(texture.IsMissing);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp, texture.Sampling.AddressU);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp, texture.Sampling.AddressV);
        Assert.Equal(EnbW3dRenderBindingSourceKind.PackedRetailState, texture.SourceKind);

        EnbW3dCompiledMaterialSemantics semantics = Assert.IsType<EnbW3dCompiledMaterialSemantics>(
            effective.Material.Source.TriangleBindings[0].Semantics);
        Assert.Equal(new Vector3(0.2f, 0.4f, 0.6f), semantics.Uniforms.Ambient);
        Assert.Equal(new Vector3(0.2f, 0.4f, 0.6f), semantics.Uniforms.Diffuse);
        Assert.Equal(0.8f, semantics.Uniforms.DiffuseAlpha);

        EnbW3dRenderMaterialUniformOverride material = Assert.IsType<EnbW3dRenderMaterialUniformOverride>(
            effective.Material.Source.VertexMaterials[0].Material.CurrentRetailUniformOverride);
        Assert.Equal(semantics.Uniforms.Ambient, material.Ambient);
        Assert.Equal(semantics.Uniforms.Diffuse, material.Diffuse);
        Assert.Equal(semantics.Uniforms.DiffuseAlpha, material.DiffuseAlpha);
    }

    [Fact]
    public void RetailSlotClampLastAssignmentAndSwatchBoundaryAreFailClosed()
    {
        EnbDecalDefinitionCatalog catalog = CreateCatalog();
        EnbW3dDrawTask target = CreatePacket("container.DECAL990");
        ImmutableArray<EnbSceneDecalSnapshot> lastWins =
        [
            new(15, 0, EncodeColor(Vector4.One)),
            new(81, 1000, EncodeColor(Vector4.One)),
        ];

        EnbW3dDrawTask effective = EnbCurrentRetailShipDecalRuntime.Apply(
            target,
            lastWins,
            catalog,
            out EnbCurrentRetailShipDecalReceipt applied);

        Assert.Equal(15, applied.Slot);
        Assert.Equal(1000, applied.DecalId);
        Assert.Equal(EnbCurrentRetailShipDecalOutcome.AppliedTexture, applied.Outcome);
        EnbW3dRenderTextureBinding secretTexture = effective.Material.Source.TextureStages[0].Textures[0];
        Assert.Equal("secret.tga", secretTexture.LogicalName);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp, secretTexture.Sampling.AddressU);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp, secretTexture.Sampling.AddressV);

        ImmutableArray<EnbSceneDecalSnapshot> swatch =
        [
            new(15, unchecked((uint)-1), EncodeColor(Vector4.One)),
        ];
        EnbW3dDrawTask unchanged = EnbCurrentRetailShipDecalRuntime.Apply(
            target,
            swatch,
            catalog,
            out EnbCurrentRetailShipDecalReceipt rejected);

        Assert.Same(target, unchanged);
        Assert.Equal(EnbCurrentRetailShipDecalOutcome.UnsupportedSwatch, rejected.Outcome);
        Assert.Contains("material-descriptor", rejected.Diagnostic, StringComparison.OrdinalIgnoreCase);

        EnbW3dDrawTask nonTarget = CreatePacket("SHIP.WING020");
        Assert.Same(
            nonTarget,
            EnbCurrentRetailShipDecalRuntime.Apply(nonTarget, lastWins, catalog, out EnbCurrentRetailShipDecalReceipt ignored));
        Assert.Equal(EnbCurrentRetailShipDecalOutcome.NotDecalRenderObject, ignored.Outcome);
    }

    [Fact]
    public void DecalPacketInitializesUnassignedSlotsToNone()
    {
        EnbSceneDecalSnapshot[] entries=[new(1,0x07FFFFFF,EncodeColor(Vector4.One))];
        EnbCurrentRetailShipDecalRuntime.Apply(CreatePacket("SHIP.DECAL010"),entries,CreateCatalog(),out var hidden);
        Assert.Equal(EnbCurrentRetailShipDecalOutcome.Hidden,hidden.Outcome);
        Assert.True(hidden.Applied);
        EnbCurrentRetailShipDecalRuntime.Apply(CreatePacket("SHIP.DECAL020"),entries,CreateCatalog(),out var other);
        Assert.Equal(EnbCurrentRetailShipDecalOutcome.Hidden,other.Outcome);
        EnbCurrentRetailShipDecalRuntime.Apply(CreatePacket("SHIP.DECAL020"),[],CreateCatalog(),out var absent);
        Assert.Equal(EnbCurrentRetailShipDecalOutcome.NoSlotAssignment,absent.Outcome);
        EnbCurrentRetailShipDecalRuntime.Apply(CreatePacket("SHIP.DECAL020"),[],CreateCatalog(),out var empty,hasDecalUpdate:true);
        Assert.Equal(EnbCurrentRetailShipDecalOutcome.Hidden,empty.Outcome);
    }

    [Fact]
    public void EmptyReceivedDecalPacketInvalidatesTheAuthoredSurfaceCache()
    {
        var obj=Assert.Single(new EnbPresentationSnapshotBuilder().Build(EnbR11FlightTests.FlightWorld(),null,1000).Objects)
            with {Decals=[],DecalApplyCount=0};
        var packet=CreatePacket("SHIP.DECAL020");
        var cache=new EnbShipMaterialCache();cache.BeginFrame();
        cache.ApplySurface(packet,obj,"root",0,null,false,null,CreateCatalog(),out var absent);
        Assert.Equal(EnbCurrentRetailShipDecalOutcome.NoSlotAssignment,absent.Decal.Outcome);
        cache.ApplySurface(packet,obj with {DecalApplyCount=1},"root",0,null,false,null,CreateCatalog(),out var empty);
        Assert.Equal(EnbCurrentRetailShipDecalOutcome.Hidden,empty.Decal.Outcome);
        cache.ApplySurface(packet,obj with {DecalApplyCount=1},"root",0,null,false,null,CreateCatalog(),out var repeated);
        Assert.Equal(empty,repeated);
    }

    [Fact]
    public void SignedSwatchSelectionDoesNotReadPositiveTintPayload()
    {
        var target = CreatePacket("SHIP.DECAL020");
        // Descriptor selection ignores RGBA even when no usable float payload
        // exists. The descriptor owner is still required to render the swatch.
        EnbCurrentRetailShipDecalRuntime.Apply(target,
            [new(2, unchecked((uint)-1), [])], CreateCatalog(), out var swatch);
        Assert.Equal(EnbCurrentRetailShipDecalOutcome.UnsupportedSwatch, swatch.Outcome);
        Assert.Equal(-1, swatch.DecalId);
        Assert.Equal(Vector4.One, swatch.Color);

        EnbCurrentRetailShipDecalRuntime.Apply(target,
            [new(2, 0, [])], CreateCatalog(), out var texture);
        Assert.Equal(EnbCurrentRetailShipDecalOutcome.InvalidColor, texture.Outcome);
    }
    [Fact]
    public void RetainedSwatchExpandsTargetBindingAndSharesMapperOwnership()
    {
        var target = CreatePacket("SHIP.DECAL020");
        var pass = EnbCurrentRetailShipDecalRuntime.CreateSwatchDefaultDescriptor();
        var textureState=new EnbCurrentRetailTextureObjectState(EnbW3dTextureSamplingSemantics.RetailDefault);
        var texture=new EnbW3dRenderTextureBinding(0,"swatch.tga",null) {CurrentRetailTextureState=textureState,ResolveLogicalNameAtRuntime=true};
        pass=(pass).Copy().SetTextureStages([new(0,[texture],[],EnbW3dRenderBindingCardinality.Uniform) {CurrentRetailUniformTexture=texture}]);
        var owners = new Dictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers>(
            ReferenceEqualityComparer.Instance);
        Assert.True(EnbCurrentRetailSwatchDescriptor.TryCreate([pass, (pass).Copy().SetOrdinal(1)],
            100, owners, out var descriptor, out var diagnostic), diagnostic);
        Assert.True(descriptor.TryApply([target], 7, out var packets, out diagnostic), diagnostic);
        Assert.Equal(2, packets.Count);
        var loaded=packets[0].CurrentRetailLoadedMaterials!;
        Assert.Equal(2,loaded.Count);
        Assert.Same(pass.CurrentRetailUniformMaterial!.Value.Material,loaded[0]);
        Assert.Same(loaded[0],loaded[1]);
        Assert.Same(packets[0].CurrentRetailLoadedMaterials,packets[1].CurrentRetailLoadedMaterials);
        Assert.Equal(2,packets[0].CurrentRetailLoadedTextures!.Count);
        Assert.All(packets[0].CurrentRetailLoadedTextures!,state=>Assert.Same(textureState,state));
        Assert.Same(packets[0].CurrentRetailLoadedTextures,packets[1].CurrentRetailLoadedTextures);
        Assert.Same(target.Mesh, packets[0].Mesh);
        Assert.Same(target.Mesh, packets[1].Mesh);
        Assert.Equal(target.WorldTransform, packets[1].WorldTransform);
        Assert.Equal(target.RenderIdentity, packets[1].RenderIdentity);
        Assert.Equal(target.VisualGeneration, packets[1].VisualGeneration);
        Assert.Equal(7, packets[1].CurrentRetailDecalRevision);
        Assert.Equal(packets[0].OrderKey.BindingOrdinal + 1, packets[1].OrderKey.BindingOrdinal);
        Assert.Equal(1, packets[1].OrderKey.MaterialPassOrdinal);
        Assert.NotNull(packets[0].CurrentRetailSharedMaterialMappers);
        Assert.Same(packets[0].CurrentRetailSharedMaterialMappers, packets[1].CurrentRetailSharedMaterialMappers);

        Assert.True(EnbCurrentRetailSwatchDescriptor.TryCreate([pass], 900,
            owners, out var secondOwner, out diagnostic), diagnostic);
        Assert.True(secondOwner.TryApply([target], 8, out var repeated, out diagnostic), diagnostic);
        Assert.Same(packets[0].CurrentRetailSharedMaterialMappers, repeated[0].CurrentRetailSharedMaterialMappers);
        Assert.Equal(8, repeated[0].CurrentRetailDecalRevision);
        Assert.Throws<ArgumentException>(() => descriptor.TryApply(
            [target, (target).ForkTask().SetRenderInstancePath("other")], 0, out _, out _));
    }
    [Fact]
    public void ExpandedTargetMaterialKeepsItsMapperOwnerAfterSwatchReplacement()
    {
        var target = CreatePacket("SHIP.DECAL020");
        var expanded = (target.Material.Source).Copy().SetVertexMaterialCardinality(EnbW3dRenderBindingCardinality.PerVertex).SetVertexMaterials(Enumerable.Repeat(target.Material.Source.VertexMaterials[0], 3).ToArray());
        Assert.True(EnbW3dSharedMaterialMappers.TryCreate(expanded.VertexMaterials[0].Material,
            10, out var originalMappers, out var diagnostic), diagnostic);
        target = (target).ForkTask().SetMaterial(new(expanded, expanded.SingleBindingSemantics)).SetCurrentRetailSharedMaterialMappers(originalMappers);
        Assert.True(EnbCurrentRetailSwatchDescriptor.TryCreate(
            [EnbCurrentRetailShipDecalRuntime.CreateSwatchDefaultDescriptor()], 20,
            new Dictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers>(ReferenceEqualityComparer.Instance),
            out var descriptor, out diagnostic), diagnostic);
        Assert.True(descriptor.TryApply([target], 1, out var packets, out diagnostic), diagnostic);
        Assert.Same(originalMappers, packets[0].CurrentRetailSharedMaterialMappers);
        Assert.Same(expanded.VertexMaterials, packets[0].Material.Source.VertexMaterials);
        Assert.Same(descriptor.Passes[0].CurrentRetailUniformMaterial!.Value.Material,Assert.Single(packets[0].CurrentRetailLoadedMaterials!));
        Assert.NotSame(expanded.VertexMaterials[0].Material,packets[0].CurrentRetailLoadedMaterials![0]);
    }
    [Fact]
    public void SwatchCacheRebasesFrameStateAndRetiresTargetMaterialsWithoutResettingPrototype()
    {
        var target = CreatePacket("SHIP.DECAL020");
        Assert.True(EnbCurrentRetailSwatchDescriptor.TryCreate(
            [EnbCurrentRetailShipDecalRuntime.CreateSwatchDefaultDescriptor()], 100,
            new Dictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers>(ReferenceEqualityComparer.Instance),
            out var descriptor, out var diagnostic), diagnostic);
        var cache = new EnbShipMaterialCache();
        cache.BeginFrame();
        Assert.True(cache.TryApplySwatch([target], descriptor, 1, out var first, out diagnostic), diagnostic);
        var moved = (target).ForkTask().SetWorldTransform(Matrix4x4.CreateTranslation(1, 2, 3)).SetFrameTimeMilliseconds(200);
        Assert.True(cache.TryApplySwatch([moved], descriptor, 1, out var redraw, out diagnostic), diagnostic);
        Assert.Same(first[0].Material, redraw[0].Material);
        Assert.Same(first[0].CurrentRetailLoadedMaterials,redraw[0].CurrentRetailLoadedMaterials);
        Assert.Equal(moved.WorldTransform, redraw[0].WorldTransform);
        Assert.Equal(200, redraw[0].FrameTimeMilliseconds);
        cache.EndFrame();
        cache.BeginFrame();
        cache.EndFrame(); // Target owner leaves the scene.
        cache.BeginFrame();
        Assert.True(cache.TryApplySwatch([moved], descriptor, 1, out var returned, out diagnostic), diagnostic);
        Assert.NotSame(first[0].Material, returned[0].Material);
        Assert.Same(first[0].CurrentRetailSharedMaterialMappers, returned[0].CurrentRetailSharedMaterialMappers);
    }
    private static EnbDecalDefinitionCatalog CreateCatalog()
        => EnbDecalDefinitionCatalog.ParseText(
            """
            [Decal0000]
            File=zero.tga

            [Decal0001]
            File=insignia.tga
            TextureWrap=TRUE
            """,
            """
            [Decal00]
            Swatch=paint_red
            """,
            """
            [Decal0000]
            File=secret.tga
            """);

    private static ImmutableArray<byte> EncodeColor(Vector4 color)
    {
        byte[] bytes = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0, 4), color.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4, 4), color.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8, 4), color.Z);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12, 4), color.W);
        return bytes.ToImmutableArray();
    }

    private static EnbW3dDrawTask CreatePacket(string renderObjectName)
    {
        var uniforms = new EnbW3dMaterialUniforms(
            Ambient: new Vector3(0.1f),
            Diffuse: new Vector3(0.9f),
            Specular: new Vector3(0.3f),
            Emissive: new Vector3(0.05f),
            Power: 8f,
            DiffuseAlpha: 1f,
            Mapper0: EnbW3dMapperUniforms.Identity,
            Mapper1: EnbW3dMapperUniforms.Identity);
        EnbW3dCompiledMaterialSemantics semantics = default(EnbW3dCompiledMaterialSemantics) with
        {
            Uniforms = uniforms,
        };
        var material = new EnbW3dRenderVertexMaterial(
            0,
            "~decal",
            0,
            new EnbRenderPackedRgb8(26, 26, 26, 0),
            new EnbRenderPackedRgb8(230, 230, 230, 0),
            new EnbRenderPackedRgb8(77, 77, 77, 0),
            new EnbRenderPackedRgb8(13, 13, 13, 0),
            8f,
            1f,
            0f,
            null,
            null);
        var materialBinding = new EnbW3dRenderVertexMaterialBinding(0, material);
        var texture = new EnbW3dRenderTextureBinding(
            0,
            "noseart.tga",
            new EnbW3dRenderTextureLocation("noseart.tga", "test.mix", 12, 64));
        var stage = new EnbW3dRenderTextureStage(
            0,
            new[] { texture },
            Array.Empty<Vector2>(),
            EnbW3dRenderBindingCardinality.Uniform);
        var triangle = new EnbW3dRenderTriangleBinding(
            0,
            0,
            materialBinding,
            null,
            new EnbW3dRenderTextureBinding?[] { texture },
            semantics);
        var pass = new EnbW3dRenderMaterialPass(
            0,
            new[] { materialBinding },
            EnbW3dRenderBindingCardinality.Uniform,
            Array.Empty<EnbW3dRenderShaderBinding>(),
            EnbW3dRenderBindingCardinality.None,
            Array.Empty<EnbRenderColor32>(),
            Array.Empty<EnbRenderColor32>(),
            Array.Empty<EnbRenderColor32>(),
            new[] { stage },
            new[] { triangle },
            semantics);
        var mesh = new EnbW3dRenderMesh(
            0,
            "DECAL020",
            "SHIP",
            1f,
            new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
            new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
            Array.Empty<ushort>(),
            new uint[] { 0, 1, 2 },
            new[] { new EnbW3dRenderTriangleMetadata(0, Vector3.UnitZ, 0f) },
            new[] { material },
            new[] { pass });
        return new EnbW3dDrawTask(
            default,
            9,
            1,
            9,
            Matrix4x4.Identity,
            new EnbW3dWorldBounds(Vector3.Zero, 1f),
            mesh,
            null,
            new EnbW3dDrawMaterialBinding(pass, semantics),
            EnbW3dLightEnvironment.BlackNoLights,
            0,
            null,
            0,
            renderObjectName);
    }
}
