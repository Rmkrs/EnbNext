using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR9ABCurrentRetailShipColorizationTests
{
    [Theory]
    [InlineData("~01/", "~01")]
    [InlineData("~01//", "~01/")]
    [InlineData(" ", " ")]
    [InlineData("/", "")]
    public void LiteralControllerPathsPreservePacketTimeColorizationScope(string authored,string rendered)
    {
        var child=new EnbSceneShipLegoAttachmentSnapshot(0,authored,2,832,""){MaterializationRevision=2};
        var legos=new EnbSceneShipLegoSnapshot(1,[child]);
        var before=new EnbSceneColorizationApplicationAuditSnapshot(1,0,0,0,[],[]);
        foreach(string suffix in new[]{"","/nested"})
        {
            string path="lego/"+rendered+suffix;
            Assert.False(EnbCurrentRetailShipColorizationRuntime.IsInstanceInCapturedRenderObjectScope(path,legos,[],before));
            var captured=before with {ShipLegoScope=[new(0,authored,2,832,2)]};
            Assert.True(EnbCurrentRetailShipColorizationRuntime.IsInstanceInCapturedRenderObjectScope(path,legos,[],captured));
            Assert.False(EnbCurrentRetailShipColorizationRuntime.IsInstanceInCapturedRenderObjectScope(path,
                new(1,[child with {MaterializationRevision=3}]),[],captured));
            Assert.False(EnbCurrentRetailShipColorizationRuntime.IsInstanceInCapturedRenderObjectScope(path,null,
                [new(authored,832){MaterializationRevision=2}],before));
        }
    }

    [Fact]
    public void MixedLoadedSidesWithoutShaderArrayKeepShaderAfterMaterialWrites()
    {
        var raw=CreatePacket("~S1","~S2");
        var source=(raw.Material.Source).Copy().SetShaderCardinality(EnbW3dRenderBindingCardinality.Uniform).SetCurrentRetailUniformShader(0xC9451B);
        raw=(raw).ForkTask().SetMaterial(new(source,null));
        var result=EnbCurrentRetailShipColorizationRuntime.Apply(raw,[Side(0,Vector3.Zero),Side(0,Vector3.Zero)],0,EnbEnvironmentMapAxis.Y,out var receipt);
        Assert.True(receipt.Applied);
        Assert.Equal(source.CurrentRetailUniformShader,result.Material.Source.CurrentRetailUniformShader);
        for(int i=0;i<2;i++)
        {
            Assert.Equal(source.TriangleBindings[i].Semantics!.Value.FixedFunction,result.Material.Source.TriangleBindings[i].Semantics!.Value.FixedFunction);
            Assert.Equal(new Vector3(.25f),result.Material.Source.TriangleBindings[i].Semantics!.Value.Uniforms.Diffuse);
        }
    }

    [Theory]
    [InlineData(1,false)]
    [InlineData(2,true)]
    public void SkinLoadedTableOnlyRewritesForeignShaderInExpandedMultipleMaterialBranch(int count,bool rewritten)
    {
        var raw=CreatePacket("~S1");
        var skin=raw.Mesh.CurrentRetailLoadedMaterials[0] with {Name="~SK"};
        raw=(raw).ForkTask().SetCurrentRetailLoadedMaterials(Enumerable.Repeat(skin,count).ToArray());
        var result=EnbCurrentRetailShipColorizationRuntime.Apply(raw,[Side(0,Vector3.Zero),Side(0,Vector3.Zero)],0,EnbEnvironmentMapAxis.Y,out var receipt);
        Assert.True(receipt.Applied);
        Assert.Same(raw.Material.Source.TriangleBindings[0].VertexMaterial!.Value.Material,result.Material.Source.TriangleBindings[0].VertexMaterial!.Value.Material);
        Assert.Equal(rewritten?0xB00000u:0xC00000u,result.Material.Source.TriangleBindings[0].Semantics!.Value.FixedFunction.PackedRetailState&0xF00000u);
    }

    [Theory]
    [InlineData(1, true, 0, true)]
    [InlineData(2, true, 0, false)]
    [InlineData(2, false, 0, true)]
    [InlineData(1, true, 1, false)]
    public void ShaderMutationHonorsPrimaryPassLoadedCountAndExpandedStorage(int loadedCount,bool expanded,int passOrdinal,bool changesUniform)
    {
        var packet=CreatePacket("~S1","~SK");
        var source=(packet.Material.Source).Copy().SetOrdinal(passOrdinal).SetCurrentRetailUniformShader(0x0099451B).SetShaderCardinality(expanded?EnbW3dRenderBindingCardinality.PerTriangle:EnbW3dRenderBindingCardinality.Uniform);
        packet=(packet).ForkTask().SetMesh((packet.Mesh).Copy().SetCurrentRetailLoadedMaterials(packet.Mesh.VertexMaterials.Take(loadedCount).ToArray())).SetMaterial(new(source,null));
        var result=EnbCurrentRetailShipColorizationRuntime.Apply(packet,[Side(-1,new(.47f,.025f,.025f)),Side(0,Vector3.Zero)],0,EnbEnvironmentMapAxis.Z,out var receipt);
        Assert.True(receipt.Applied);
        Assert.Equal(changesUniform?0x00C9451Bu:0x0099451Bu,result.Material.Source.CurrentRetailUniformShader);
        for(int i=0;i<2;i++)
        {
            uint before=source.TriangleBindings[i].Semantics!.Value.FixedFunction.PackedRetailState;
            uint expected=passOrdinal==0&&(i==0||changesUniform)?(before&0xFF0FFFFF)|0x00C00000:before;
            Assert.Equal(expected,result.Material.Source.TriangleBindings[i].Semantics!.Value.FixedFunction.PackedRetailState);
        }
        Assert.Equal(0x0099451Bu,source.CurrentRetailUniformShader);
    }

    [Theory]
    [InlineData("root", 0)]
    [InlineData("root/lego/~01", 1)]
    [InlineData("root/lego/~02/~03_01/fixed", 3)]
    [InlineData("root/~2x", 0)]
    [InlineData("root/~99tail", 99)]
    public void DecimalHierarchySegmentsSelectAndInheritRetailRegion(string path, int expected)
        => Assert.Equal(expected, EnbCurrentRetailShipColorizationRuntime.ResolveRegion(path));

    [Fact]
    public void CapturedFlatStartupEntriesPairByHierarchyRegionAndApplyBothSurfaceFamilies()
    {
        ImmutableArray<EnbSceneColorizationSnapshot> entries =
        [
            Side(-1, new Vector3(.10f, .01f, .01f)), Side(0, Vector3.Zero),
            Side(-1, new Vector3(.20f, .02f, .02f)), Side(1, Vector3.Zero),
            Side(-1, new Vector3(.30f, .03f, .03f)), Side(2, Vector3.Zero),
            Side(-1, new Vector3(.47f, .025f, .025f)), Side(3, Vector3.Zero),
        ];
        EnbW3dDrawTask source = CreatePacket("~s1", "~s2");

        EnbW3dDrawTask effective = EnbCurrentRetailShipColorizationRuntime.Apply(
            source,
            entries,
            EnbCurrentRetailShipColorizationRuntime.ResolveRegion("root/lego/~02/~03_01"),
            EnbEnvironmentMapAxis.Z,
            out EnbCurrentRetailShipColorizationReceipt receipt);

        Assert.Equal(EnbCurrentRetailShipColorizationOutcome.Applied, receipt.Outcome);
        Assert.Equal(3, receipt.Region);
        Assert.Equal(2, receipt.TargetTriangles);
        Assert.Equal(2, receipt.AppliedTriangles);
        Assert.Equal(1, receipt.PrimaryTriangles);
        Assert.Equal(1, receipt.SecondaryTriangles);
        Assert.Equal(0, receipt.SkinTriangles);

        EnbW3dCompiledMaterialSemantics primary = effective.Material.Source.TriangleBindings[0].Semantics!.Value;
        Assert.Equal(new Vector3(.47f, .025f, .025f), primary.Uniforms.Ambient);
        Assert.Equal(primary.Uniforms.Ambient, primary.Uniforms.Diffuse);
        Assert.Equal((byte)0x0C, primary.FixedFunction.TextureCombiners.Selector1);
        Assert.Equal(EnbW3dMapperRuntimeProgramKind.Disabled, primary.Mapper0Program.Kind);
        Assert.Equal(EnbW3dMapperRuntimeProgramKind.GridAtlasAxisProjection, primary.Mapper1Program.Kind);
        Assert.Equal(EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector, primary.Mapper1Program.CoordinateSource);
        Assert.Equal(EnbW3dMapperProjectionAxis.Z, primary.Mapper1Program.Axis);
        Assert.Equal(new EnbW3dMapperAtlasParameters(0f, 1, 0, 3), primary.Mapper1Program.Atlas);

        EnbW3dCompiledMaterialSemantics secondary = effective.Material.Source.TriangleBindings[1].Semantics!.Value;
        Assert.Equal(new Vector3(.25f), secondary.Uniforms.Ambient);
        Assert.Equal(secondary.Uniforms.Ambient, secondary.Uniforms.Diffuse);
        Assert.Equal((byte)0x0B, secondary.FixedFunction.TextureCombiners.Selector1);
        Assert.Equal(EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector, secondary.Mapper1Program.CoordinateSource);
        Assert.Equal(new EnbW3dMapperAtlasParameters(0f, 2, 0, 3), secondary.Mapper1Program.Atlas);
    }

    [Fact]
    public void SelectorMinusTwoUsesNormalAtlasWhileSkinIsBlackWithoutShaderRewrite()
    {
        EnbW3dDrawTask source = CreatePacket("~S1_detail", "~SK");
        ImmutableArray<EnbSceneColorizationSnapshot> entries =
        [
            Side(-2, new Vector3(.7f, .6f, .5f)),
            Side(-1, new Vector3(.4f, .3f, .2f)),
        ];

        EnbW3dDrawTask effective = EnbCurrentRetailShipColorizationRuntime.Apply(
            source,
            entries,
            0,
            EnbEnvironmentMapAxis.Y,
            out EnbCurrentRetailShipColorizationReceipt receipt);

        Assert.True(receipt.Applied);
        Assert.Equal(1, receipt.PrimaryTriangles);
        Assert.Equal(0, receipt.SecondaryTriangles);
        Assert.Equal(1, receipt.SkinTriangles);
        EnbW3dCompiledMaterialSemantics primary = effective.Material.Source.TriangleBindings[0].Semantics!.Value;
        Assert.Equal(new Vector3(.7f, .6f, .5f), primary.Uniforms.Diffuse);
        Assert.Equal((byte)0x02, primary.FixedFunction.TextureCombiners.Selector1);
        Assert.Equal(EnbW3dMapperTypeSelector.Mode13, effective.Material.Source.TriangleBindings[0].VertexMaterial!.Value.Material.Mapper1!.TypeSelector);
        Assert.Equal(EnbW3dMapperCoordinateSource.CameraSpaceNormal, primary.Mapper1Program.CoordinateSource);
        Assert.Equal(EnbW3dMapperProjectionAxis.Y, primary.Mapper1Program.Axis);
        Assert.Equal(new EnbW3dMapperAtlasParameters(0f, 4, 0, 255), primary.Mapper1Program.Atlas);

        EnbW3dCompiledMaterialSemantics skin = effective.Material.Source.TriangleBindings[1].Semantics!.Value;
        Assert.Equal(Vector3.Zero, skin.Uniforms.Ambient);
        Assert.Equal(Vector3.Zero, skin.Uniforms.Diffuse);
        Assert.Null(effective.Material.Source.TriangleBindings[1].VertexMaterial!.Value.Material.Mapper1);
        Assert.Equal((byte)0x0C, skin.FixedFunction.TextureCombiners.Selector1);
    }

    [Fact]
    public void MissingPairInvalidSelectorAndNonTargetStayFailClosed()
    {
        EnbW3dDrawTask target = CreatePacket("~s1");
        ImmutableArray<EnbSceneColorizationSnapshot> oneEntry = [Side(-1, Vector3.One)];

        Assert.Same(target, EnbCurrentRetailShipColorizationRuntime.Apply(
            target, oneEntry, 0, EnbEnvironmentMapAxis.Z, out EnbCurrentRetailShipColorizationReceipt missing));
        Assert.Equal(EnbCurrentRetailShipColorizationOutcome.MissingRegionPair, missing.Outcome);

        ImmutableArray<EnbSceneColorizationSnapshot> invalid =
        [
            Side(-3, Vector3.One),
            Side(-1, Vector3.One),
        ];
        Assert.Same(target, EnbCurrentRetailShipColorizationRuntime.Apply(
            target, invalid, 0, EnbEnvironmentMapAxis.Z, out EnbCurrentRetailShipColorizationReceipt rejected));
        Assert.Equal(EnbCurrentRetailShipColorizationOutcome.InvalidSelector, rejected.Outcome);

        EnbW3dDrawTask nonTarget = CreatePacket("body");
        Assert.Same(nonTarget, EnbCurrentRetailShipColorizationRuntime.Apply(
            nonTarget, invalid, 0, null, out EnbCurrentRetailShipColorizationReceipt ignored));
        Assert.Equal(EnbCurrentRetailShipColorizationOutcome.NotColorizationMaterial, ignored.Outcome);
    }

    [Fact]
    public void SuffixedPrimaryWeaponMaterialRemainsAColorizationTarget()
    {
        ImmutableArray<EnbSceneColorizationSnapshot> entries =
        [
            Side(-1, new Vector3(.47f, .025f, .025f)),
            Side(0, Vector3.Zero),
        ];
        EnbW3dDrawTask source = CreatePacket("~s1b");

        EnbW3dDrawTask effective = EnbCurrentRetailShipColorizationRuntime.Apply(
            source,
            entries,
            0,
            EnbEnvironmentMapAxis.Z,
            out EnbCurrentRetailShipColorizationReceipt receipt);

        Assert.True(receipt.Applied);
        Assert.Equal(1, receipt.PrimaryTriangles);
        Assert.Equal(0, receipt.SecondaryTriangles);
        Assert.Equal(0, receipt.SkinTriangles);
        EnbW3dCompiledMaterialSemantics semantics =
            Assert.Single(effective.Material.Source.TriangleBindings).Semantics!.Value;
        Assert.Equal(new Vector3(.47f, .025f, .025f), semantics.Uniforms.Diffuse);
        Assert.Equal(EnbW3dMapperRuntimeProgramKind.GridAtlasAxisProjection, semantics.Mapper1Program.Kind);
        Assert.Equal(new EnbW3dMapperAtlasParameters(0f, 1, 0, 3), semantics.Mapper1Program.Atlas);
    }

    [Fact]
    public void PacketTimeScopeKeepsExactLifetimesAndLeavesLaterWeaponMapperAuthored()
    {
        var capturedWeapon = new EnbSceneShipLegoAttachmentSnapshot(
            0, "~01/~WEAP_01", 2, 500, string.Empty)
        {
            MaterializationRevision = 1,
        };
        var laterWeapon = new EnbSceneShipLegoAttachmentSnapshot(
            1, "~02/~WEAP_01", 2, 501, string.Empty)
        {
            MaterializationRevision = 2,
        };
        var shipLegos = new EnbSceneShipLegoSnapshot(1f, [capturedWeapon, laterWeapon]);
        EnbSceneSubPartSnapshot[] subParts =
        [
            new EnbSceneSubPartSnapshot("~01", 600) { MaterializationRevision = 1 },
            new EnbSceneSubPartSnapshot("~02", 601) { MaterializationRevision = 1 },
        ];
        var audit = new EnbSceneColorizationApplicationAuditSnapshot(
            1,
            1,
            2,
            0,
            [new EnbSceneColorizedShipLegoScopeEntry(0, "~01/~WEAP_01", 2, 500, 1)],
            [
                new EnbSceneColorizedSubPartScopeEntry(0, "~01", 600, 1),
                new EnbSceneColorizedSubPartScopeEntry(1, "~02", 601, 1),
            ]);

        Assert.True(EnbCurrentRetailShipColorizationRuntime.IsInstanceInCapturedRenderObjectScope(
            "root", shipLegos, subParts, audit));
        Assert.True(EnbCurrentRetailShipColorizationRuntime.IsInstanceInCapturedRenderObjectScope(
            "lego/~01", shipLegos, subParts, audit));
        Assert.True(EnbCurrentRetailShipColorizationRuntime.IsInstanceInCapturedRenderObjectScope(
            "lego/~01/~WEAP_01", shipLegos, subParts, audit));
        Assert.False(EnbCurrentRetailShipColorizationRuntime.IsInstanceInCapturedRenderObjectScope(
            "lego/~02/~WEAP_01", shipLegos, subParts, audit));
        var replacementLegos = shipLegos with
        {
            Attachments = [capturedWeapon with { MaterializationRevision = 2 }, laterWeapon],
        };
        Assert.False(EnbCurrentRetailShipColorizationRuntime.IsInstanceInCapturedRenderObjectScope(
            "lego/~01/~WEAP_01", replacementLegos, subParts, audit));

        EnbW3dDrawTask authored = CreatePacket("~s1b");
        EnbW3dDrawTask effective = EnbCurrentRetailShipColorizationRuntime.Apply(
            authored,
            [Side(-1, new Vector3(.47f, .025f, .025f)), Side(0, Vector3.Zero)],
            2,
            EnbEnvironmentMapAxis.Y,
            applyMutation: false,
            out EnbCurrentRetailShipColorizationReceipt receipt);

        Assert.Same(authored, effective);
        Assert.Equal(EnbCurrentRetailShipColorizationOutcome.OutsideCapturedRenderObjectScope, receipt.Outcome);
        Assert.Equal(1, receipt.TargetTriangles);
        Assert.Equal(0, receipt.AppliedTriangles);
        Assert.Equal(1, receipt.PrimaryTriangles);
        Assert.Null(effective.Material.Source.TriangleBindings[0].VertexMaterial!.Value.Material.Mapper1);
        Assert.Equal((byte)0x0C, effective.Material.Source.TriangleBindings[0].Semantics!.Value.FixedFunction.TextureCombiners.Selector1);
    }

    [Fact]
    public void ShipInitializationWeaponMetalPassColorsPrimaryShellAndRetainsAuthoredGradientLane()
    {
        var authoredMapper = new EnbW3dRenderMapper(
            EnbW3dMapperTypeSelector.Mode04,
            string.Empty,
            1f,
            1f,
            0f,
            1.5f,
            .05f,
            0f,
            false);
        EnbW3dDrawTask source = CreatePacketWithMapper1(authoredMapper, "~s1", "~s1b", "uncolored");
        ImmutableArray<EnbSceneColorizationSnapshot> entries =
        [
            Side(-1, new Vector3(.10f, .01f, .01f)), Side(0, Vector3.Zero),
            Side(-1, new Vector3(.20f, .02f, .02f)), Side(0, Vector3.Zero),
            Side(-1, new Vector3(.47f, .025f, .025f)), Side(0, Vector3.Zero),
        ];

        EnbW3dDrawTask effective = EnbCurrentRetailWeaponMetalColorRuntime.Apply(
            source,
            "lego/~02/~WEAP_01",
            entries,
            2,
            out EnbCurrentRetailWeaponMetalColorReceipt receipt);

        Assert.Equal(EnbCurrentRetailWeaponMetalColorOutcome.Applied, receipt.Outcome);
        Assert.Equal(2, receipt.Region);
        Assert.Equal(2, receipt.TargetTriangles);
        Assert.Equal(2, receipt.AppliedTriangles);
        for (int index = 0; index < 2; index++)
        {
            EnbW3dRenderTriangleBinding triangle = effective.Material.Source.TriangleBindings[index];
            Assert.Equal(new Vector3(.47f, .025f, .025f), triangle.Semantics!.Value.Uniforms.Ambient);
            Assert.Equal(triangle.Semantics.Value.Uniforms.Ambient, triangle.Semantics.Value.Uniforms.Diffuse);
            Assert.Same(authoredMapper, triangle.VertexMaterial!.Value.Material.Mapper1);
            Assert.Equal((byte)0x0C, triangle.Semantics.Value.FixedFunction.TextureCombiners.Selector1);
            Assert.Equal(EnbW3dMapperRuntimeProgramKind.Linear, triangle.Semantics.Value.Mapper1Program.Kind);
        }

        EnbW3dRenderTriangleBinding uncolored = effective.Material.Source.TriangleBindings[2];
        Assert.Null(uncolored.VertexMaterial!.Value.Material.CurrentRetailUniformOverride);
        Assert.Same(authoredMapper, uncolored.VertexMaterial.Value.Material.Mapper1);
        Assert.InRange(
            Vector3.Distance(new Vector3(113f / 255f, 0f, 0f), uncolored.Semantics!.Value.Uniforms.Diffuse),
            0f,
            1e-6f);
    }

    [Fact]
    public void WeaponMetalPassRequiresWeaponContainerAndUsesWhiteForMissingMetalTable()
    {
        EnbW3dDrawTask source = CreatePacket("~s1");
        ImmutableArray<EnbSceneColorizationSnapshot> direct =
        [
            Side(-1, new Vector3(.47f, .025f, .025f)),
            Side(0, Vector3.Zero),
        ];

        Assert.Same(source, EnbCurrentRetailWeaponMetalColorRuntime.Apply(
            source,
            "lego/~01/~ENGINE_01",
            direct,
            1,
            out EnbCurrentRetailWeaponMetalColorReceipt nonWeapon));
        Assert.Equal(EnbCurrentRetailWeaponMetalColorOutcome.NotWeaponInstance, nonWeapon.Outcome);

        ImmutableArray<EnbSceneColorizationSnapshot> metalTable =
        [
            Side(7, Vector3.Zero),
            Side(0, Vector3.Zero),
        ];
        EnbMetalEquivalentRgbCatalog metals = EnbMetalEquivalentRgbCatalog.ParseText("""
            [Metal07]
            R=0.70
            G=0.08
            B=0.03
            """);
        EnbW3dDrawTask metalEffective = EnbCurrentRetailWeaponMetalColorRuntime.Apply(
            source,
            "lego/~00/~weap_01",
            metalTable,
            0,
            metals,
            out EnbCurrentRetailWeaponMetalColorReceipt metalApplied);
        Assert.Equal(EnbCurrentRetailWeaponMetalColorOutcome.Applied, metalApplied.Outcome);
        Assert.Equal(new Vector3(.70f, .08f, .03f),
            Assert.Single(metalEffective.Material.Source.TriangleBindings).Semantics!.Value.Uniforms.Diffuse);

        var missing = EnbCurrentRetailWeaponMetalColorRuntime.Apply(
            source,
            "lego/~00/~weap_01",
            metalTable,
            0,
            out EnbCurrentRetailWeaponMetalColorReceipt unresolved);
        Assert.True(unresolved.Applied);
        Assert.Equal(Vector3.One, Assert.Single(missing.Material.Source.TriangleBindings).Semantics!.Value.Uniforms.Diffuse);
        var absentSection = EnbCurrentRetailWeaponMetalColorRuntime.Apply(source, "~WEAP", metalTable, 0,
            EnbMetalEquivalentRgbCatalog.ParseText(""), out var absent);
        Assert.True(absent.Applied);
        Assert.Equal(Vector3.One, Assert.Single(absentSection.Material.Source.TriangleBindings).Semantics!.Value.Uniforms.Diffuse);
    }

    [Fact]
    public void PreparedBaseSurfaceMatchesSequentialMutationAndKeepsQueuedFrameInputs()
    {
        var obj = Assert.Single(new EnbPresentationSnapshotBuilder().Build(EnbR11FlightTests.FlightWorld(), null, 1000).Objects)
            with { ColorizationEntries = [Side(-1, Vector3.UnitX)], ColorizationAudit = new(1, 0, 0, 0, [], []) };
        var raw = CreatePacket("~S1");
        var overbright = new EnbCurrentRetailBaseAssetOverbrightCache();
        var cache = new EnbShipMaterialCache();
        cache.BeginFrame();
        foreach (bool disabled in new[] { false, true, false })
        {
            var sequential = overbright.Apply(raw, disabled, out var oldReceipt);
            sequential = cache.ApplySurface(sequential, obj, "root", 0, EnbEnvironmentMapAxis.Y, true, null, null, out var oldSurface);
            var moved = (raw).ForkTask().SetWorldTransform(Matrix4x4.CreateTranslation(7,8,9)).SetFrameTimeMilliseconds(1234);
            var binding = overbright.Resolve(moved, disabled, out var receipt);
            var combined = cache.ApplySurface(moved, obj, "root", 0, EnbEnvironmentMapAxis.Y, true, null, null,
                out var surface, preparedBaseMaterial: binding);
            Assert.Equal(oldReceipt, receipt);
            Assert.Same(oldSurface, surface);
            Assert.Same(sequential.Material, combined.Material);
            Assert.Same(sequential.CurrentRetailLoadedMaterials, combined.CurrentRetailLoadedMaterials);
            Assert.Equal(moved.FrameInputs, combined.FrameInputs);
            Assert.Equal(Matrix4x4.Identity, sequential.WorldTransform);
            Assert.Equal(0, sequential.FrameTimeMilliseconds);
            obj = obj with { ColorizationAudit = obj.ColorizationAudit! with { ApplyCount = obj.ColorizationAudit.ApplyCount + 1 } };
        }
    }

    [Fact]
    public void CachedShipMaterialsKeepFrameStateAndInvalidateVisualChanges()
    {
        var obj = Assert.Single(new EnbPresentationSnapshotBuilder().Build(EnbR11FlightTests.FlightWorld(), null, 1000).Objects)
            with { ColorizationEntries = [Side(-1, Vector3.UnitX), Side(-1, Vector3.UnitY)] };
        var raw = CreatePacket("~S1", "~S2");
        var cache = new EnbShipMaterialCache();
        EnbW3dDrawTask Apply(EnbW3dDrawTask packet, EnbEnvironmentMapAxis axis = EnbEnvironmentMapAxis.Y)
            => cache.ApplySurface(packet, obj, "root", 0, axis, true, null, null, out _);
        cache.BeginFrame();
        var first = Apply(raw);
        cache.EndFrame();
        var moved = (raw).ForkTask().SetWorldTransform(Matrix4x4.CreateTranslation(10,20,30)).SetFrameTimeMilliseconds(1234);
        cache.BeginFrame();
        var next = Apply(moved);
        Assert.Same(first.Material, next.Material);
        Assert.Equal(moved.WorldTransform, next.WorldTransform);
        Assert.Equal(1234, next.FrameTimeMilliseconds);
        Assert.Equal(1, cache.Hits);
        var residentOwners = new HashSet<(ulong,ulong)> { (raw.RenderIdentity,raw.VisualGeneration) };
        cache.EndFrame(residentOwners);
        cache.BeginFrame(); cache.EndFrame(residentOwners); // no visible packets
        cache.BeginFrame();
        Assert.Same(first.Material,Apply(moved).Material);
        Assert.Equal(1,cache.Hits);
        var direct = EnbCurrentRetailShipColorizationRuntime.Apply(moved, obj.ColorizationEntries, 0, EnbEnvironmentMapAxis.Y, out _);
        AssertEquivalentColorizationBindings(direct, next);
        Assert.NotSame(first.Material, Apply(raw, EnbEnvironmentMapAxis.X).Material);
        obj = obj with { ColorizationEntries = [Side(-1, Vector3.UnitZ), Side(-1, Vector3.One)] };
        Assert.NotSame(first.Material, Apply((raw).ForkTask().SetVisualGeneration(raw.VisualGeneration + 1)).Material);
        cache.EndFrame();
        cache.Clear();
        cache.BeginFrame();
        Assert.NotSame(first.Material, Apply(raw).Material);
        cache.EndFrame();
        cache.BeginFrame(); cache.EndFrame(); // disappearance retires material state
        cache.BeginFrame(); Apply(raw);
        Assert.Equal(1, cache.Misses);
        cache.EndFrame();
    }

    [Fact]
    public void RepeatedLegoInstancesDoNotEvictEachOthersIndexedMaterials()
    {
        var raw = CreatePacket("~S1");
        var second = (raw).ForkTask().SetRenderInstancePath("lego/~02/~WEAP_01").SetMaterial(raw.Material with { Source = (raw.Material.Source).Copy() });
        var cache = new EnbW3dIndexedSubmissionCache();
        cache.BeginFrame();
        Assert.True(cache.TryCompile(raw, out _, out _));
        Assert.True(cache.TryCompile(second, out _, out _));
        cache.EndFrame();
        cache.BeginFrame();
        Assert.True(cache.TryCompile(raw, out _, out _));
        Assert.True(cache.TryCompile(second, out _, out _));
        Assert.Equal(2, cache.HitCount);
        Assert.Equal(0, cache.MissCount);
        cache.EndFrame();
    }

    [Fact]
    public void IdenticalColorizationReplacesMapperOwnerWithoutReplacingRenderObject()
    {
        var obj = Assert.Single(new EnbPresentationSnapshotBuilder().Build(EnbR11FlightTests.FlightWorld(), null, 1000).Objects)
            with { ColorizationEntries = [Side(-1, Vector3.UnitX), Side(-1, Vector3.UnitY)],
                ColorizationAudit = new(1, 0, 0, 0, [], []) };
        var raw = CreatePacket("~S1", "~S2");
        var cache = new EnbShipMaterialCache();
        EnbW3dDrawTask Apply(EnbW3dDrawTask packet, bool eligible = true)
            => cache.ApplySurface(packet, obj, "root", 0, EnbEnvironmentMapAxis.Y, eligible, null, null, out _);
        cache.BeginFrame();
        var first = Apply(raw);
        Assert.Equal(1, first.CurrentRetailColorizationRevision);
        Assert.Same(first.Material, Apply(raw).Material);
        var excluded = Apply(raw, false);
        obj = obj with { ColorizationAudit = obj.ColorizationAudit with { ApplyCount = 2 } };
        var repeated = Apply(raw);
        Assert.NotSame(first.Material, repeated.Material);
        Assert.Equal(2, repeated.CurrentRetailColorizationRevision);
        Assert.Equal(first.VisualGeneration, repeated.VisualGeneration);
        AssertEquivalentColorizationBindings(first, repeated);
        Assert.Same(repeated.Material, Apply(raw).Material);
        Assert.Same(excluded.Material, Apply(raw, false).Material);
        Assert.Equal(0, excluded.CurrentRetailColorizationRevision);
    }

    private static void AssertEquivalentColorizationBindings(EnbW3dDrawTask left, EnbW3dDrawTask right)
    {
        var a = left.Material.Source.TriangleBindings;
        var b = right.Material.Source.TriangleBindings;
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            var am = a[i].VertexMaterial!.Value;
            var bm = b[i].VertexMaterial!.Value;
            Assert.NotNull(am.Material.CurrentRetailMapper1Owner);
            Assert.NotSame(am.Material.CurrentRetailMapper1Owner, bm.Material.CurrentRetailMapper1Owner);
            // Independent mutation calls retain equal definitions but distinct mapper pointers.
            Assert.Equal(a[i] with { VertexMaterial = am with { Material = am.Material with
                { CurrentRetailMapper1Owner = bm.Material.CurrentRetailMapper1Owner } } }, b[i]);
        }
    }

    [Fact]
    public void ColorizationPreservesSharedMaterialIdentityInRetainedAndExpandedBindings()
    {
        var raw = CreatePacket("~S1", "~S1");
        var source = raw.Material.Source;
        var shared = source.VertexMaterials[0];
        using var owner = new EnbCurrentRetailMeshModelOwner([(shared.Material, null, null)]);
        uint materialId = 71;
        owner.MarkLoadedMaterialsInstanceLocal(ref materialId);
        source = (source).Copy().SetVertexMaterials([shared, shared]).SetCurrentRetailUniformMaterial(shared).SetTriangleBindings(source.TriangleBindings.Select(triangle => triangle with
            {
                VertexMaterial = shared,
            }).ToArray());
        raw = (raw).ForkTask().SetMaterial(raw.Material with { Source = source });
        var result = EnbCurrentRetailShipColorizationRuntime.Apply(raw,
            [Side(-1, Vector3.UnitX), Side(-1, Vector3.UnitY)], 0,
            EnbEnvironmentMapAxis.Y, out var receipt);
        Assert.True(receipt.Applied);
        var changed = result.Material.Source.VertexMaterials[0].Material;
        Assert.NotSame(shared.Material, changed);
        Assert.Equal(71u, EnbCurrentRetailMeshModelOwner.MaterialRuntimeWord64(changed));
        Assert.All(result.Material.Source.VertexMaterials, binding => Assert.Same(changed, binding.Material));
        Assert.All(result.Material.Source.TriangleBindings,
            triangle => Assert.Same(changed, triangle.VertexMaterial!.Value.Material));
        Assert.Same(changed, result.Material.Source.CurrentRetailUniformMaterial!.Value.Material);
        Assert.Same(shared.Material, raw.Material.Source.CurrentRetailUniformMaterial!.Value.Material);
        Assert.Same(shared.Material, source.TriangleBindings[1].VertexMaterial!.Value.Material);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("lego/~00/~WEAP_01")]
    public void SurfacePassesShareMaterialsUntilUpdateOrOwnerRetirement(string path)
    {
        var obj = Assert.Single(new EnbPresentationSnapshotBuilder().Build(EnbR11FlightTests.FlightWorld(), null, 1000).Objects)
            with { ColorizationEntries = [Side(-1, Vector3.UnitX), Side(-1, Vector3.UnitY)],
                ColorizationAudit = new(1, 0, 0, 0, [], []) };
        var first = CreatePacket("~S1");
        var second = (first).ForkTask().SetMaterial(first.Material with { Source = (first.Material.Source).Copy().SetOrdinal(1) });
        var cache = new EnbShipMaterialCache();
        EnbW3dRenderVertexMaterial Apply(EnbW3dDrawTask packet, string? instance = null)
            => cache.ApplySurface(packet, obj, instance ?? path, 0, EnbEnvironmentMapAxis.Y, true, null, null, out _)
                .Material.Source.TriangleBindings[0].VertexMaterial!.Value.Material;
        cache.BeginFrame();
        var initial = Apply(first);
        Assert.Same(initial, Apply(second));
        Assert.NotSame(initial, Apply(second, path + "/other"));
        Assert.NotSame(initial, Apply((second).ForkTask().SetMesh((second.Mesh).Copy())));
        var resident = new HashSet<(ulong, ulong)> { (first.RenderIdentity, first.VisualGeneration) };
        cache.EndFrame(resident);
        cache.BeginFrame(); cache.EndFrame(resident);
        cache.BeginFrame();
        Assert.Same(initial, Apply(second));
        obj = obj with { ColorizationAudit = obj.ColorizationAudit with { ApplyCount = 2 } };
        var repeated = Apply(second); // Later pass can be visited first by another camera.
        Assert.NotSame(initial, repeated);
        Assert.Same(repeated, Apply(first));
        cache.EndFrame();
        cache.BeginFrame(); cache.EndFrame();
        cache.BeginFrame();
        var recreated = Apply(first);
        Assert.NotSame(repeated, recreated);
        Assert.Same(recreated, Apply(second));
    }

    [Fact]
    public void WeaponMetalPreservesSharedMaterialAcrossRetainedAndExpandedBindings()
    {
        var raw = CreatePacket("~S1", "~S1", "uncolored");
        var source = raw.Material.Source;
        var shared = source.VertexMaterials[0];
        using var owner = new EnbCurrentRetailMeshModelOwner([(shared.Material, null, null)]);
        uint materialId = 83;
        owner.MarkLoadedMaterialsInstanceLocal(ref materialId);
        source = (source).Copy().SetVertexMaterials([shared, shared, source.VertexMaterials[2]]).SetCurrentRetailUniformMaterial(shared).SetTriangleBindings(source.TriangleBindings.Select((triangle, index) => index < 2
                ? triangle with { VertexMaterial = shared } : triangle).ToArray());
        raw = (raw).ForkTask().SetMaterial(raw.Material with { Source = source });
        var result = EnbCurrentRetailWeaponMetalColorRuntime.Apply(raw, "lego/~01/~WEAP_01",
            [Side(-1, Vector3.UnitX)], 0, out var receipt);
        Assert.True(receipt.Applied);
        Assert.Equal(2, receipt.AppliedTriangles);
        var changed = result.Material.Source.CurrentRetailUniformMaterial!.Value.Material;
        Assert.NotSame(shared.Material, changed);
        Assert.Equal(83u, EnbCurrentRetailMeshModelOwner.MaterialRuntimeWord64(changed));
        Assert.Equal(Vector3.UnitX, changed.CurrentRetailUniformOverride!.Value.Ambient);
        Assert.Equal(Vector3.UnitX, changed.CurrentRetailUniformOverride.Value.Diffuse);
        for (int index = 0; index < 2; index++)
        {
            Assert.Same(changed, result.Material.Source.VertexMaterials[index].Material);
            Assert.Same(changed, result.Material.Source.TriangleBindings[index].VertexMaterial!.Value.Material);
        }
        Assert.Same(source.VertexMaterials[2].Material, result.Material.Source.VertexMaterials[2].Material);
        Assert.Same(shared.Material, raw.Material.Source.CurrentRetailUniformMaterial!.Value.Material);
    }

    [Theory]
    [InlineData(0.004f, 0f, false, false)]
    [InlineData(0.004f, -1e-11f, false, true)]
    [InlineData(0.004000001f, 0f, true, true)]
    [InlineData(-0.004000001f, 0f, true, true)]
    [InlineData(float.NaN, 0f, false, false)]
    [InlineData(float.PositiveInfinity, 0f, true, true)]
    public void WeaponRgbGateUsesNativePrecisionAndOrderedStrictThreshold(float actual, float desired, bool single, bool wide)
    {
        var material = CreatePacket("~S1").Material.Source.VertexMaterials[0].Material with
        {
            CurrentRetailUniformOverride = new(new(actual), new(actual), Vector3.Zero, Vector3.Zero, 0, 1),
        };
        Assert.Equal(single, EnbCurrentRetailWeaponMetalColorRuntime.RequiresRgbMutation(material, new(desired), EnbRetailLinePrecision.Single24));
        Assert.Equal(wide, EnbCurrentRetailWeaponMetalColorRuntime.RequiresRgbMutation(material, new(desired), EnbRetailLinePrecision.Double53));
    }

    [Fact]
    public void LoadedMaterialWritesSurviveUnusedDescriptorsAndDuplicateTableEntries()
    {
        var raw = CreatePacket("plain");
        var plain = raw.Mesh.CurrentRetailLoadedMaterials[0];
        var unused = plain with { Name = "~S1" };
        raw = (raw).ForkTask().SetMesh((raw.Mesh).Copy().SetCurrentRetailLoadedMaterials([plain, unused, unused]));
        var colored = EnbCurrentRetailShipColorizationRuntime.Apply(raw,
            [Side(-1, Vector3.UnitX), Side(-1, Vector3.UnitY)], 0, EnbEnvironmentMapAxis.Y, out var color);
        Assert.True(color.Applied);
        Assert.Equal(0, color.AppliedTriangles);
        Assert.Same(plain, colored.CurrentRetailLoadedMaterials![0]);
        Assert.Same(colored.CurrentRetailLoadedMaterials[1], colored.CurrentRetailLoadedMaterials[2]);
        Assert.Equal(Vector3.UnitX, colored.CurrentRetailLoadedMaterials[1].CurrentRetailUniformOverride!.Value.Diffuse);
        var weapon = EnbCurrentRetailWeaponMetalColorRuntime.Apply(colored, "~WEAP",
            [Side(-1, Vector3.UnitY)], 0, out var metal);
        Assert.True(metal.Applied);
        Assert.Equal(0, metal.AppliedTriangles);
        Assert.Same(plain, weapon.CurrentRetailLoadedMaterials![0]);
        Assert.Same(weapon.CurrentRetailLoadedMaterials[1], weapon.CurrentRetailLoadedMaterials[2]);
        Assert.Equal(Vector3.UnitY, weapon.CurrentRetailLoadedMaterials[1].CurrentRetailUniformOverride!.Value.Diffuse);
        Assert.Same(plain, weapon.Material.Source.VertexMaterials[0].Material);
        Assert.Same(unused, raw.Mesh.CurrentRetailLoadedMaterials[1]);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    public void ColorizationUsesRawRgbWordsOrMetalConstantsWithoutFiniteRejection(int selector)
    {
        var input = new Vector3(BitConverter.Int32BitsToSingle(0x7FC12345), float.PositiveInfinity, float.NegativeInfinity);
        var result = EnbCurrentRetailShipColorizationRuntime.Apply(CreatePacket("~S1", "~S2", "~SK"),
            [Side(selector,input),Side(selector,input)],0,EnbEnvironmentMapAxis.Y,out var receipt);
        Assert.True(receipt.Applied);
        var expected = selector < 0 ? input : new Vector3(.25f);
        foreach (var triangle in result.Material.Source.TriangleBindings.Take(2))
        {
            var uniforms = triangle.Semantics!.Value.Uniforms;
            foreach(var rgb in new[]{uniforms.Ambient,uniforms.Diffuse})
            {
                Assert.Equal(BitConverter.SingleToInt32Bits(expected.X),BitConverter.SingleToInt32Bits(rgb.X));
                Assert.Equal(BitConverter.SingleToInt32Bits(expected.Y),BitConverter.SingleToInt32Bits(rgb.Y));
                Assert.Equal(BitConverter.SingleToInt32Bits(expected.Z),BitConverter.SingleToInt32Bits(rgb.Z));
            }
        }
        Assert.Equal(Vector3.Zero,result.Material.Source.TriangleBindings[2].Semantics!.Value.Uniforms.Diffuse);
    }

    [Fact]
    public void LoadedTableMutationPreservesForeignDescriptorMaterialsButUpdatesShaders()
    {
        var raw = CreatePacket("~S1", "~S2", "~SK");
        var source = raw.Material.Source;
        // Equal-valued clones are loaded; drawn pointers are deliberately foreign.
        raw = (raw).ForkTask().SetCurrentRetailLoadedMaterials(source.VertexMaterials.Select(b => b.Material with { }).ToArray());
        var entries = new[] { Side(-1, Vector3.UnitY), Side(-1, Vector3.UnitZ) };
        var colored = EnbCurrentRetailShipColorizationRuntime.Apply(raw, [Side(0, Vector3.Zero), entries[1]], 0, EnbEnvironmentMapAxis.Y, out var receipt);
        Assert.True(receipt.Applied);
        for (int i = 0; i < 3; i++)
        {
            var before = source.TriangleBindings[i];
            var after = colored.Material.Source.TriangleBindings[i];
            Assert.Same(before.VertexMaterial!.Value.Material, after.VertexMaterial!.Value.Material);
            Assert.Equal(before.Semantics!.Value.Uniforms, after.Semantics!.Value.Uniforms);
            Assert.NotSame(raw.CurrentRetailLoadedMaterials![i], colored.CurrentRetailLoadedMaterials![i]);
        }
        Assert.Equal(0xB00000u, colored.Material.Source.TriangleBindings[0].Semantics!.Value.FixedFunction.PackedRetailState & 0xF00000u);
        var metal = EnbCurrentRetailWeaponMetalColorRuntime.Apply(raw, "~WEAP", entries, 0, out var metalReceipt);
        Assert.True(metalReceipt.Applied);
        Assert.NotSame(raw.CurrentRetailLoadedMaterials![0], metal.CurrentRetailLoadedMaterials![0]);
        Assert.Same(source.VertexMaterials[0].Material, metal.Material.Source.VertexMaterials[0].Material);
        Assert.Equal(source.TriangleBindings[0].Semantics, metal.Material.Source.TriangleBindings[0].Semantics);
    }

    [Fact]
    public void ColorizationSharesOneMapperPerSideButModelCopyClonesEachTableEntry()
    {
        var raw=CreatePacket("~S1a","~S1b","~S2");
        var entries=new[]{Side(-1,Vector3.UnitX),Side(-1,Vector3.UnitX)};
        var colored=EnbCurrentRetailShipColorizationRuntime.Apply(raw,entries,0,EnbEnvironmentMapAxis.Y,out _);
        var table=colored.CurrentRetailLoadedMaterials!;
        var pairs=new List<EnbW3dSharedMaterialMappers>();
        for(int i=0;i<table.Count;i++)
        {
            Assert.True(EnbW3dSharedMaterialMappers.TryCreate(table[i],(uint)(100+i),out var pair,out var error),error);
            pairs.Add(pair);
        }
        Assert.Same(pairs[0].Stage1,pairs[1].Stage1);
        Assert.Equal(100u,pairs[1].Stage1.InitialMilliseconds);
        Assert.NotSame(pairs[0].Stage1,pairs[2].Stage1);
        using var owner=new EnbCurrentRetailMeshModelOwner(table.Select((material,i)=>
            ((object)material,(object?)pairs[i].Stage0,(object?)pairs[i].Stage1)));
        using var copy=owner.CloneRenderObject();copy.EnsureUniqueModel(300);
        Assert.NotSame(copy.MapperIdentityAt(0,1),copy.MapperIdentityAt(1,1));
        for(int i=0;i<table.Count;i++)
        {
            var material=Assert.IsType<EnbW3dRenderVertexMaterial>(copy.MaterialIdentityAt(i));
            Assert.True(EnbW3dSharedMaterialMappers.TryCreate(material,999,out var pair,out var error),error);
            Assert.Same(copy.MapperIdentityAt(i,0),pair.Stage0);
            Assert.Same(copy.MapperIdentityAt(i,1),pair.Stage1);
            Assert.Equal(300u,pair.Stage1.InitialMilliseconds);
        }
        var repeated=EnbCurrentRetailShipColorizationRuntime.Apply(raw,entries,0,EnbEnvironmentMapAxis.Y,out _);
        Assert.True(EnbW3dSharedMaterialMappers.TryCreate(repeated.CurrentRetailLoadedMaterials![0],400,out var replacement,out _));
        Assert.NotSame(pairs[0].Stage1,replacement.Stage1);
    }

    [Fact]
    public void CachedSurfaceRetainsLoadedTableAcrossPassesAndInvalidatesUnusedEntriesOnUpdate()
    {
        var obj = Assert.Single(new EnbPresentationSnapshotBuilder().Build(EnbR11FlightTests.FlightWorld(), null, 1000).Objects)
            with { ColorizationEntries = [Side(-1, Vector3.UnitX), Side(-1, Vector3.UnitY)], ColorizationAudit = new(1, 0, 0, 0, [], []) };
        var raw = CreatePacket("plain");
        var unused = raw.Mesh.CurrentRetailLoadedMaterials[0] with { Name="~S1" };
        raw = (raw).ForkTask().SetMesh((raw.Mesh).Copy().SetCurrentRetailLoadedMaterials([unused,unused]));
        var cache = new EnbShipMaterialCache();
        EnbW3dDrawTask Apply(EnbW3dDrawTask packet) => cache.ApplySurface(packet,obj,"root",0,EnbEnvironmentMapAxis.Y,true,null,null,out _);
        cache.BeginFrame();
        var first = Apply(raw);
        Assert.Same(first.CurrentRetailLoadedMaterials,Apply(raw).CurrentRetailLoadedMaterials);
        var second = (raw).ForkTask().SetMaterial(raw.Material with {Source=(raw.Material.Source).Copy().SetOrdinal(1)});
        Assert.Same(first.CurrentRetailLoadedMaterials![0],Apply(second).CurrentRetailLoadedMaterials![0]);
        obj=obj with {ColorizationEntries=[Side(-1,Vector3.UnitZ),Side(-1,Vector3.UnitY)],ColorizationAudit=obj.ColorizationAudit! with {ApplyCount=2}};
        var changed=Apply(second);
        Assert.NotSame(first.CurrentRetailLoadedMaterials[0],changed.CurrentRetailLoadedMaterials![0]);
        Assert.Same(changed.CurrentRetailLoadedMaterials[0],Apply(raw).CurrentRetailLoadedMaterials![0]);
        Assert.Equal(Vector3.UnitZ,changed.CurrentRetailLoadedMaterials[0].CurrentRetailUniformOverride!.Value.Diffuse);
    }

    [Fact]
    public void WeaponRgbGateScansUnusedLoadedEntriesAndPreservesUnchangedDescriptor()
    {
        var raw = CreatePacket("~S1");
        var original = raw.Material.Source.VertexMaterials[0].Material;
        var target = new Vector3(113f / 255f, 0, 0);
        var unchanged = EnbCurrentRetailWeaponMetalColorRuntime.Apply(raw, "~WEAP", [Side(-1, target)], 0, out var receipt);
        Assert.Same(raw, unchanged);
        Assert.Equal(EnbCurrentRetailWeaponMetalColorOutcome.Unchanged, receipt.Outcome);
        Assert.Equal(0, receipt.AppliedTriangles);
        var unused = original with { CurrentRetailUniformOverride = new(Vector3.One, Vector3.One, Vector3.Zero, Vector3.Zero, 0, 1) };
        raw = (raw).ForkTask().SetMesh((raw.Mesh).Copy().SetCurrentRetailLoadedMaterials([original, unused]));
        var changed = EnbCurrentRetailWeaponMetalColorRuntime.Apply(raw, "~WEAP", [Side(-1, target)], 0, out receipt);
        Assert.True(receipt.Applied);
        Assert.NotSame(original, changed.Material.Source.VertexMaterials[0].Material);
        raw = (raw).ForkTask().SetMesh((raw.Mesh).Copy().SetCurrentRetailLoadedMaterials([]));
        Assert.Same(raw, EnbCurrentRetailWeaponMetalColorRuntime.Apply(raw, "~WEAP", [Side(-1, Vector3.One)], 0, out receipt));
    }

    [Fact]
    public void SurfaceMutationsPreserveLoaderRgbBitsForEveryAuthoredByte()
    {
        float reciprocal = BitConverter.UInt32BitsToSingle(0x3b808081);
        for (int value = 0; value <= 255; value++)
        {
            var raw = CreatePacket("~S1");
            var binding = raw.Material.Source.VertexMaterials[0];
            var packed = new EnbRenderPackedRgb8((byte)value, (byte)(255-value), (byte)value, 0);
            binding = binding with { Material = binding.Material with { Specular = packed, Emissive = packed } };
            var pass = (raw.Material.Source).Copy().SetVertexMaterials([binding]).SetCurrentRetailUniformMaterial(binding).SetTriangleBindings(raw.Material.Source.TriangleBindings.Select(t => t with { VertexMaterial = binding }).ToArray());
            raw = (raw).ForkTask().SetMesh((raw.Mesh).Copy().SetCurrentRetailLoadedMaterials([binding.Material])).SetMaterial(new(pass, null));
            var expected = new Vector3(value * reciprocal, (255-value) * reciprocal, value * reciprocal);
            var colored = EnbCurrentRetailShipColorizationRuntime.Apply(raw, [Side(-1, Vector3.UnitX), Side(-1, Vector3.UnitY)], 0,
                EnbEnvironmentMapAxis.Y, out _);
            var metal = EnbCurrentRetailWeaponMetalColorRuntime.Apply(raw, "~WEAP", [Side(-1, Vector3.UnitY)], 0, out _);
            foreach (var result in new[] { colored, metal })
            {
                var material = result.Material.Source.CurrentRetailUniformMaterial!.Value.Material;
                Assert.Equal(expected, material.CurrentRetailUniformOverride!.Value.Specular);
                Assert.Equal(expected, material.CurrentRetailUniformOverride.Value.Emissive);
                Assert.Equal(expected, result.Material.Source.TriangleBindings[0].Semantics!.Value.Uniforms.Specular);
            }
        }
    }

    [Fact]
    public void WeaponUnorderedChannelsSkipComparisonButDoNotRejectTheWholeVector()
    {
        var raw = CreatePacket("~S1");
        Assert.Same(raw, EnbCurrentRetailWeaponMetalColorRuntime.Apply(raw, "~WEAP", [Side(-1, new(float.NaN))], 0, out var receipt));
        Assert.Equal(EnbCurrentRetailWeaponMetalColorOutcome.Unchanged, receipt.Outcome);
        var changed = EnbCurrentRetailWeaponMetalColorRuntime.Apply(raw, "~WEAP", [Side(-1, new(float.NaN, 1, 0))], 0, out receipt);
        Assert.True(receipt.Applied);
        var rgb = changed.Material.Source.VertexMaterials[0].Material.CurrentRetailUniformOverride!.Value.Diffuse;
        Assert.True(float.IsNaN(rgb.X));
        Assert.Equal(1f, rgb.Y);
        Assert.Equal(0f, rgb.Z);
    }

    private static EnbSceneColorizationSnapshot Side(int selector, Vector3 rgb)
    {
        byte[] bytes = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0, 4), rgb.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4, 4), rgb.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8, 4), rgb.Z);
        return new EnbSceneColorizationSnapshot(unchecked((uint)selector), bytes.ToImmutableArray());
    }

    [Fact]
    public void PersistentMaterialOwnerRequiresNoMarkSeenOrSweepAndPreservesQueuedValues()
    {
        var obj=Assert.Single(new EnbPresentationSnapshotBuilder().Build(EnbR11FlightTests.FlightWorld(),null,1000).Objects)
            with {ColorizationEntries=[Side(-1,Vector3.UnitX),Side(-1,Vector3.UnitY)],ColorizationAudit=new(1,0,0,0,[],[])};
        var raw=CreatePacket("~S1");var owner=(raw.RenderIdentity,raw.VisualGeneration);
        var cache=new EnbShipMaterialCache();var binding=new EnbShipMaterialCache.SurfaceBinding();
        cache.RetainOwner(owner);
        EnbW3dDrawTask Apply(EnbW3dDrawTask p)=>cache.ApplySurface(p,obj,"root",0,EnbEnvironmentMapAxis.Y,true,null,null,out _,binding:binding);
        cache.BeginFrame();var queued=Apply(raw);cache.EndFrame();long seen=cache.MarkSeenWrites;
        for(int i=0;i<100;i++)
        {
            cache.BeginFrame();
            if(i%2==0)
            {
                var moved=Apply((raw).ForkTask().SetWorldTransform(Matrix4x4.CreateTranslation(i,2,3)).SetFrameTimeMilliseconds(i));
                Assert.Same(queued.Material,moved.Material);Assert.Equal(i,moved.FrameTimeMilliseconds);
            }
            cache.EndFrame();Assert.Equal(0,cache.LastSweepCandidateCount);Assert.Equal(seen,cache.MarkSeenWrites);
        }
        Assert.Equal(Matrix4x4.Identity,queued.WorldTransform);Assert.Equal(0,queued.FrameTimeMilliseconds);
        obj=obj with {ColorizationAudit=obj.ColorizationAudit! with {ApplyCount=2}};
        var replacement=Apply(raw);Assert.NotSame(queued.Material,replacement.Material);
        cache.ReleaseOwner(owner);cache.RetainOwner(owner);
        var reborn=Apply(raw);Assert.NotSame(replacement.Material,reborn.Material);
        Assert.Equal(Matrix4x4.Identity,queued.WorldTransform);Assert.Equal(0,queued.FrameTimeMilliseconds);
        cache.Clear();var reloaded=Apply(raw);Assert.NotSame(reborn.Material,reloaded.Material);
        cache.BeginFrame();cache.EndFrame();Assert.Equal(0,cache.LastSweepCandidateCount);
        Assert.Same(reloaded.Material,Apply(raw).Material);
    }

    [Fact]
    public void OwnedSurfaceBindingRetainsMaterialsButNeverQueuedFrameValues()
    {
        var obj=Assert.Single(new EnbPresentationSnapshotBuilder().Build(EnbR11FlightTests.FlightWorld(),null,1000).Objects)
            with {ColorizationEntries=[Side(-1,Vector3.UnitX),Side(-1,Vector3.UnitY)],ColorizationAudit=new(1,0,0,0,[],[])};
        var raw=CreatePacket("~S1");
        var cache=new EnbShipMaterialCache();var binding=new EnbShipMaterialCache.SurfaceBinding();
        EnbW3dDrawTask Apply(EnbW3dDrawTask p)=>cache.ApplySurface(p,obj,"root",0,EnbEnvironmentMapAxis.Y,true,null,null,out _,binding:binding);
        cache.BeginFrame();var first=Apply(raw);Assert.NotSame(raw.Material,first.Material);cache.EndFrame();
        var moved=(raw).ForkTask().SetWorldTransform(Matrix4x4.CreateTranslation(3,4,5)).SetFrameTimeMilliseconds(1234);
        cache.BeginFrame();var next=Apply(moved);
        Assert.Equal(1,cache.DirectSurfaceUses);Assert.Same(first.Material,next.Material);
        Assert.Equal(moved.FrameInputs,next.FrameInputs);Assert.Equal(Matrix4x4.Identity,first.WorldTransform);
        Assert.Equal(0,first.FrameTimeMilliseconds);
        var resident=new HashSet<(ulong,ulong)>{(raw.RenderIdentity,raw.VisualGeneration)};
        cache.EndFrame(resident);cache.BeginFrame();cache.EndFrame(resident);cache.BeginFrame();
        Assert.Same(first.Material,Apply(raw).Material);Assert.Equal(2,cache.DirectSurfaceUses);
        obj=obj with {ColorizationAudit=obj.ColorizationAudit! with {ApplyCount=2}};
        var changed=Apply(raw);Assert.NotSame(first.Material,changed.Material);Assert.Equal(2,cache.DirectSurfaceUses);
        Assert.Same(changed.Material,Apply(moved).Material);Assert.Equal(3,cache.DirectSurfaceUses);
        cache.EndFrame();cache.BeginFrame();cache.EndFrame();cache.BeginFrame();
        var recreated=Apply(raw);Assert.NotSame(changed.Material,recreated.Material);Assert.Equal(3,cache.DirectSurfaceUses);
        cache.Clear();Assert.NotSame(recreated.Material,Apply(raw).Material);Assert.Equal(3,cache.DirectSurfaceUses);
        Assert.NotSame(first.Material,Apply((raw).ForkTask().SetVisualGeneration(raw.VisualGeneration+1)).Material);
    }

    [Fact]
    public void OwnedSurfaceBindingsKeepCrossPassMapperIdentityAndInvalidateOtherViewReplacement()
    {
        var obj=Assert.Single(new EnbPresentationSnapshotBuilder().Build(EnbR11FlightTests.FlightWorld(),null,1000).Objects)
            with {ColorizationEntries=[Side(-1,Vector3.UnitX),Side(-1,Vector3.UnitY)],ColorizationAudit=new(1,0,0,0,[],[])};
        var first=CreatePacket("~S1");
        var second=(first).ForkTask().SetMaterial(first.Material with {Source=(first.Material.Source).Copy().SetOrdinal(1)});
        var cache=new EnbShipMaterialCache();var a=new EnbShipMaterialCache.SurfaceBinding();var b=new EnbShipMaterialCache.SurfaceBinding();
        EnbW3dRenderVertexMaterial Apply(EnbW3dDrawTask p,EnbShipMaterialCache.SurfaceBinding slot)
            =>cache.ApplySurface(p,obj,"root",0,EnbEnvironmentMapAxis.Y,true,null,null,out _,binding:slot)
                .Material.Source.TriangleBindings[0].VertexMaterial!.Value.Material;
        cache.BeginFrame();var initial=Apply(first,a);Assert.NotSame(first.Material.Source.TriangleBindings[0].VertexMaterial!.Value.Material,initial);Assert.Same(initial,Apply(second,b));
        Assert.Same(initial,Apply(first,a));Assert.Equal(1,cache.DirectSurfaceUses);
        obj=obj with {ColorizationAudit=obj.ColorizationAudit! with {ApplyCount=2}};
        var changed=Apply(second,b);Assert.NotSame(initial,changed);Assert.Same(changed,Apply(first,a));
        var replacement=(first).ForkTask().SetMaterial(first.Material with {Source=(first.Material.Source).Copy().SetOrdinal(3)});
        Assert.Same(changed,Apply(replacement,a));Assert.Same(changed,Apply(second,b));
        cache.BeginFrame();cache.EndFrame();cache.BeginFrame();
        var retired=Apply(second,b);Assert.NotSame(changed,retired);Assert.Same(retired,Apply(first,a));
    }

    private static EnbW3dDrawTask CreatePacket(params string[] materialNames)
        => CreatePacketCore(null, materialNames);

    private static EnbW3dDrawTask CreatePacketWithMapper1(
        EnbW3dRenderMapper mapper1,
        params string[] materialNames)
        => CreatePacketCore(mapper1, materialNames);

    private static EnbW3dDrawTask CreatePacketCore(
        EnbW3dRenderMapper? mapper1,
        params string[] materialNames)
    {
        var materials = new EnbW3dRenderVertexMaterialBinding[materialNames.Length];
        var shaders = new EnbW3dRenderShaderBinding[materialNames.Length];
        var triangles = new EnbW3dRenderTriangleBinding[materialNames.Length];
        for (int index = 0; index < materialNames.Length; index++)
        {
            var material = new EnbW3dRenderVertexMaterial(
                index,
                materialNames[index],
                0,
                new EnbRenderPackedRgb8(113, 0, 0, 0),
                new EnbRenderPackedRgb8(113, 0, 0, 0),
                new EnbRenderPackedRgb8(64, 64, 64, 0),
                new EnbRenderPackedRgb8(0, 0, 0, 0),
                8f,
                1f,
                0f,
                null,
                mapper1);
            materials[index] = new EnbW3dRenderVertexMaterialBinding((uint)index, material);
            uint packed = 0x00C9_451Bu;
            Assert.True(EnbW3dMaterialCompiler.TryDecodePackedRetailState(
                packed, out EnbW3dFixedFunctionState fixedFunction, out string? decodeDiagnostic), decodeDiagnostic);
            shaders[index] = new EnbW3dRenderShaderBinding(
                (uint)index,
                new EnbW3dRenderShaderRecord(packed, 0, 0, 0),
                fixedFunction);
            Assert.True(EnbW3dMaterialCompiler.TryCompilePackedRetailState(
                packed,
                material,
                2,
                out EnbW3dCompiledMaterialSemantics semantics,
                out string? compileDiagnostic), compileDiagnostic);
            triangles[index] = new EnbW3dRenderTriangleBinding(
                index,
                0,
                materials[index],
                shaders[index],
                new EnbW3dRenderTextureBinding?[] { null, null },
                semantics);
        }

        var stages = new[]
        {
            new EnbW3dRenderTextureStage(0, Array.Empty<EnbW3dRenderTextureBinding>(), Array.Empty<Vector2>(), EnbW3dRenderBindingCardinality.None),
            new EnbW3dRenderTextureStage(1, Array.Empty<EnbW3dRenderTextureBinding>(), Array.Empty<Vector2>(), EnbW3dRenderBindingCardinality.None),
        };
        var pass = new EnbW3dRenderMaterialPass(
            0,
            materials,
            EnbW3dRenderBindingCardinality.PerVertex,
            shaders,
            EnbW3dRenderBindingCardinality.PerTriangle,
            Array.Empty<EnbRenderColor32>(),
            Array.Empty<EnbRenderColor32>(),
            Array.Empty<EnbRenderColor32>(),
            stages,
            triangles,
            null);
        uint[] indices = Enumerable.Range(0, materialNames.Length)
            .SelectMany(static _ => new uint[] { 0, 1, 2 })
            .ToArray();
        EnbW3dRenderTriangleMetadata[] metadata = Enumerable.Range(0, materialNames.Length)
            .Select(static i => new EnbW3dRenderTriangleMetadata((uint)i, Vector3.UnitZ, 0f))
            .ToArray();
        var mesh = new EnbW3dRenderMesh(
            0,
            "SHIP_SURFACE",
            "SHIP",
            1f,
            new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
            new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
            Array.Empty<ushort>(),
            indices,
            metadata,
            materials.Select(static binding => binding.Material).ToArray(),
            new[] { pass }) { CurrentRetailLoadedMaterials = materials.Select(binding => binding.Material).ToArray() };
        return new EnbW3dDrawTask(
            default,
            1,
            1,
            1,
            Matrix4x4.Identity,
            new EnbW3dWorldBounds(Vector3.Zero, 1f),
            mesh,
            null,
            new EnbW3dDrawMaterialBinding(pass, null),
            EnbW3dLightEnvironment.BlackNoLights,
            0,
            null,
            0,
            "SHIP.SURFACE");
    }
}
