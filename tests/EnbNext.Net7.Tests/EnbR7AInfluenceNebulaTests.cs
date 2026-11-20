using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR7AInfluenceNebulaTests
{
    [Fact]
    public void LegacyRuntimeRootUsesAlreadyRemappedPivotReferences()
    {
        var source = Source();
        var hierarchy = source.Resource.Hierarchies.Single().Copy().SetVersion(0x20000).SetHasSyntheticRetailRoot(true);
        hierarchy=hierarchy.Copy().SetPivots(hierarchy.Pivots.Select((p,i)=>i==0?p with{Name="RootTransform"}:p).ToArray());
        source=source with {Resource=source.Resource.Copy().SetHierarchies([hierarchy])};
        var result=new EnbCurrentRetailInfluenceNebulaState(source).Evaluate(new(2.5f,.25f,.375f),Catalog());
        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Room,result.Region);
        Assert.Equal(1,result.RoomReferenceIndex);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfiguredModelScaleMovesPivotsAndKeepsAuthoredBoundaryNames(bool retainedHierarchy)
    {
        var source = Source();
        var meshes = source.Resource.Meshes.Select((mesh,index) =>
        {
            var geometry = new EnbCurrentRetailMeshGeometryState(mesh.Positions.ToArray(), new Vector3[mesh.Positions.Count],
                mesh.Indices.Select(i => checked((ushort)i)).ToArray(), [], mesh.BoundingBoxMin, mesh.BoundingBoxMax,
                mesh.BoundingSphereCenter, mesh.BoundingRadius, 0, null);
            uint seed = 1;
            geometry.Scale(new Vector3(2), ref seed);
            return (mesh).Copy().SetContainerName($"<configured:{index}>").SetCurrentRetailLoadedGeometry(geometry).SetCurrentRetailLoadedGeometryScale(2);
        }).ToArray();
        var original = source.Hlod.Levels[0].Members.Select(m=>m.Name).ToArray();
        var level = source.Hlod.Levels[0] with { Members=source.Hlod.Levels[0].Members.Select((m,i)=>m with
            {Name=EnbW3dRenderObjectNameSemantics.RegisteredMeshName(meshes[i])}).ToArray() };
        source = source with { Resource=source.Resource.Copy().SetMeshes(meshes), Hlod=source.Hlod with {Levels=[level]},
            ModelScale=2, AuthoredMemberNames=original };
        if(retainedHierarchy)source=source with
        {
            Resource=source.Resource.Copy().SetHierarchies(source.Resource.Hierarchies.Select(h=>h.ScaleStoredTranslations(2)).ToArray()),
            PendingHierarchyScale=1
        };
        var state = new EnbCurrentRetailInfluenceNebulaState(source);
        var result = state.Evaluate(new(5,.25f,.375f),Catalog());
        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Room,result.Region);
        Assert.Equal("INFLUENCE_HIER.ROOM01",result.RoomName);
        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Between,state.Evaluate(new(2,.25f,.375f),Catalog()).Region);
    }
    [Fact]
    public void BoundaryQueriesConsumeRetainedModelGeometryAndBounds()
    {
        var source = Source();
        var meshes = source.Resource.Meshes.ToArray();
        var room = meshes[1];
        var loaded = new EnbCurrentRetailMeshGeometryState(room.Positions.ToArray(), new Vector3[room.Positions.Count],
            room.Indices.Select(i => checked((ushort)i)).ToArray(), [], room.BoundingBoxMin, room.BoundingBoxMax,
            room.BoundingSphereCenter, room.BoundingRadius, 0, null);
        uint seed = 1;
        loaded.Scale(new Vector3(.25f), ref seed);
        meshes[1] = (room).Copy().SetCurrentRetailLoadedGeometry(loaded);
        source = source with { Resource = source.Resource.Copy().SetMeshes(meshes) };
        var state = new EnbCurrentRetailInfluenceNebulaState(source);
        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Between, state.Evaluate(new(3, .125f, .125f), Catalog()).Region);
        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Room, state.Evaluate(new(2.5f, .125f, .125f), Catalog()).Region);
        //Negative-X exits avoid the independently proven positive-X projection flag.
        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Between, state.Evaluate(new(2, .125f, .125f), Catalog()).Region);
    }
    [Fact]
    public void NebulaIniUsesRetailRoomBetweenKeysDefaultsAndByteNormalization()
    {
        EnbRetailNebulaEnvironmentCatalog catalog = EnbRetailNebulaEnvironmentCatalog.Parse("""
            [Room]
            FogColorR=255
            FogColorG=128
            FogColorB=1
            FogNearRange=12.5
            FogFarRange=345.75

            [Between]
            SkyBoxColorR=64
            SkyBoxColorG=32
            SkyBoxColorB=16
            FogColorR=9
            FogColorG=8
            FogColorB=7
            """);

        Assert.Equal(1f, catalog.Room.FogColor.X);
        Assert.Equal(128f / 255f, catalog.Room.FogColor.Y);
        Assert.Equal(1f / 255f, catalog.Room.FogColor.Z);
        Assert.Equal(12.5f, catalog.Room.FogNearRange);
        Assert.Equal(345.75f, catalog.Room.FogFarRange);
        Assert.Null(catalog.Room.SkyBoxColor);

        Vector3 sky = Assert.IsType<Vector3>(catalog.Between.SkyBoxColor);
        Assert.Equal(64f / 255f, sky.X);
        Assert.Equal(32f / 255f, sky.Y);
        Assert.Equal(16f / 255f, sky.Z);
        Assert.Equal(0f, catalog.Between.FogNearRange);
        Assert.Equal(1f, catalog.Between.FogFarRange);
        Assert.Equal(0x090807u, catalog.Between.FogColorRgb24);
    }

    [Fact]
    public void OutsideNebulaRootKeepsOrdinarySectorEnvironment()
    {
        EnbCurrentRetailInfluenceNebulaBoundarySource source = Source();

        EnbCurrentRetailInfluenceNebulaEvaluation result =
            EnbCurrentRetailInfluenceNebulaRuntime.Evaluate([source], new Vector3(6f, 0f, 0f), environmentCatalog: null);

        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Outside, result.Region);
        Assert.False(result.IsEnvironmentActive);
        Assert.Null(result.Environment);
    }

    [Fact]
    public void InsideRootOutsideRoomsSelectsBetweenEnvironment()
    {
        EnbRetailNebulaEnvironmentCatalog catalog = Catalog();
        EnbCurrentRetailInfluenceNebulaEvaluation result =
            EnbCurrentRetailInfluenceNebulaRuntime.Evaluate([Source()], Vector3.Zero, catalog);

        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Between, result.Region);
        Assert.Same(catalog.Between, result.Environment);
        Assert.True(result.IsEnvironmentActive);
    }

    [Fact]
    public void InsideRoomAndExactBoundarySurfaceSelectRoomEnvironment()
    {
        EnbRetailNebulaEnvironmentCatalog catalog = Catalog();
        EnbCurrentRetailInfluenceNebulaBoundarySource source = Source();

        EnbCurrentRetailInfluenceNebulaEvaluation interior =
            EnbCurrentRetailInfluenceNebulaRuntime.Evaluate([source], new Vector3(2.5f, 0f, 0f), catalog);
        EnbCurrentRetailInfluenceNebulaEvaluation surface =
            EnbCurrentRetailInfluenceNebulaRuntime.Evaluate([source], new Vector3(3.5f, 0f, 0f), catalog);

        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Room, interior.Region);
        Assert.Equal(1, interior.RoomReferenceIndex);
        Assert.Equal("INFLUENCE_HIER.ROOM01", interior.RoomName);
        Assert.Same(catalog.Room, interior.Environment);
        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Room, surface.Region);
    }

    [Fact]
    public void SelectedRoomIsTestedBeforeSpatialQueryAndRetainsOverlapOwnership()
    {
        var source = Source(overlappingSecondRoom: true);
        var hierarchy = source.Resource.Hierarchies[0];
        var second = hierarchy.Pivots[2] with
        {
            Translation = new(1.5f, 0, 0), LocalTransform = Matrix4x4.CreateTranslation(1.5f, 0, 0),
        };
        source = source with { Resource = source.Resource.Copy().SetHierarchies([hierarchy.Copy().SetPivots([hierarchy.Pivots[0], hierarchy.Pivots[1], second])]) };
        var state = new EnbCurrentRetailInfluenceNebulaState(source);
        Assert.Equal(1, state.Evaluate(new(3, .25f, .375f), Catalog()).RoomReferenceIndex);
        Assert.Equal(1, state.Evaluate(new(2, .25f, .375f), Catalog()).RoomReferenceIndex);
        Assert.Equal(2, state.Evaluate(new(1, .25f, .375f), Catalog()).RoomReferenceIndex);
        Assert.Equal(2, state.Evaluate(new(2, .25f, .375f), Catalog()).RoomReferenceIndex);
        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Between,
            state.Evaluate(new(-.125f, .25f, .375f), Catalog()).Region);
        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Outside,
            state.Evaluate(new(-6, -6, -6), Catalog()).Region);
        Assert.Equal(1, state.Evaluate(new(3, .25f, .375f), Catalog()).RoomReferenceIndex);
    }

    [Fact]
    public void VisibleMembersDoNotReplaceOrDuplicateTheNamedRoomBoundary()
    {
        var source = Source();
        var decoration = Cube(2, "DECORATION", 20f);
        var level = source.Hlod.Levels[0];
        var hlod = source.Hlod with { Levels = [level with
        {
            Members = [new(1, "INFLUENCE_HIER.DECORATION"), .. level.Members,
                new(0, "INFLUENCE_HIER.DECORATION")],
        }] };
        source = source with { Hlod = hlod, Resource = source.Resource.Copy().SetMeshes([.. source.Resource.Meshes, decoration]) };
        var between = EnbCurrentRetailInfluenceNebulaRuntime.Evaluate([source], new(.125f, .25f, .375f), Catalog());
        var room = EnbCurrentRetailInfluenceNebulaRuntime.Evaluate([source], new(2.5f, .25f, .375f), Catalog());
        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Between, between.Region);
        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Room, room.Region);
        Assert.Equal(1, room.RoomReferenceIndex);
    }

    [Fact]
    public void ReferenceZeroCanUseItsPivotNameAsTheSentinelBoundary()
    {
        var source = Source();
        var root = (source.Resource.Meshes[0]).Copy().SetName("ROOT");
        var level = source.Hlod.Levels[0];
        source = source with
        {
            Hlod = source.Hlod with { Levels = [level with
                { Members = [new(0, "INFLUENCE_HIER.ROOT"), level.Members[1]] }] },
            Resource = source.Resource.Copy().SetMeshes([root, source.Resource.Meshes[1]]),
        };
        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Between,
            EnbCurrentRetailInfluenceNebulaRuntime.Evaluate([source], new(.125f, .25f, .375f), Catalog()).Region);
    }

    [Fact]
    public void JustOutsideBoundaryDoesNotAcquireEnvironmentThroughAnEpsilonBand()
    {
        var result = EnbCurrentRetailInfluenceNebulaRuntime.Evaluate([Source()], new(5.00001f, .125f, .25f), Catalog());
        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Outside, result.Region);
    }

    [Fact]
    public void OverlappingRoomsUseFirstNativeSpatialCandidate()
    {
        //Native spatial control 129: nested root + identical rooms, seed 1,
        //point (2.5,0,0), candidates [1,2,0], six draws, state 1030492215.
        uint previous = EnbRetailCrtRandomSource.Current.State;
        try
        {
            EnbRetailCrtRandomSource.Current.State = 1;
            var state = new EnbCurrentRetailInfluenceNebulaState(Source(overlappingSecondRoom: true));
            Assert.Equal(1030492215u, EnbRetailCrtRandomSource.Current.State);
            var result = state.Evaluate(new Vector3(2.5f, 0f, 0f), Catalog());
            Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.Room, result.Region);
            Assert.Equal(1, result.RoomReferenceIndex);
            state.Evaluate(new Vector3(2.5f, .125f, .25f), Catalog());
            Assert.Equal(1030492215u, EnbRetailCrtRandomSource.Current.State);
        }
        finally { EnbRetailCrtRandomSource.Current.State = previous; }
    }
    [Fact]
    public void MultipleBoundaryRootsFailClosedInsteadOfChoosingOne()
    {
        EnbRetailNebulaEnvironmentCatalog catalog = Catalog();
        EnbCurrentRetailInfluenceNebulaBoundarySource source = Source();

        EnbCurrentRetailInfluenceNebulaEvaluation result =
            EnbCurrentRetailInfluenceNebulaRuntime.Evaluate([source, source], Vector3.Zero, catalog);

        Assert.Equal(EnbCurrentRetailInfluenceNebulaRegion.FailClosed, result.Region);
        Assert.Contains("ambiguous", result.Diagnostic, StringComparison.Ordinal);
    }

    private static EnbRetailNebulaEnvironmentCatalog Catalog()
        => EnbRetailNebulaEnvironmentCatalog.Parse("""
            [Room]
            FogColorR=30
            FogColorG=40
            FogColorB=50
            FogNearRange=2
            FogFarRange=20

            [Between]
            SkyBoxColorR=80
            SkyBoxColorG=90
            SkyBoxColorB=100
            FogColorR=10
            FogColorG=20
            FogColorB=30
            FogNearRange=5
            FogFarRange=50
            """);

    private static EnbCurrentRetailInfluenceNebulaBoundarySource Source(bool overlappingSecondRoom = false)
    {
        EnbW3dRenderMesh root = Cube(0, "NEBULA_ROOT", 5f);
        EnbW3dRenderMesh room = Cube(1, "ROOM01", 1f);
        var pivots = new List<EnbW3dRenderPivot>
        {
            new(0, "ROOT", uint.MaxValue, Vector3.Zero, Vector3.Zero, Quaternion.Identity, Matrix4x4.Identity),
            new(1, "ROOM01", 0, new Vector3(2.5f, 0f, 0f), Vector3.Zero, Quaternion.Identity, Matrix4x4.CreateTranslation(2.5f, 0f, 0f)),
        };
        var meshes = new List<EnbW3dRenderMesh> { root, room };
        var members = new List<EnbW3dRenderHlodMember>
        {
            new(0, "INFLUENCE_HIER.NEBULA_ROOT"),
            new(1, "INFLUENCE_HIER.ROOM01"),
        };

        if (overlappingSecondRoom)
        {
            meshes.Add(Cube(2, "ROOM02", 1f));
            pivots.Add(new EnbW3dRenderPivot(
                2,
                "ROOM02",
                0,
                new Vector3(2.5f, 0f, 0f),
                Vector3.Zero,
                Quaternion.Identity,
                Matrix4x4.CreateTranslation(2.5f, 0f, 0f)));
            members.Add(new EnbW3dRenderHlodMember(2, "INFLUENCE_HIER.ROOM02"));
        }

        var hierarchy = new EnbW3dRenderHierarchy(
            0x0003_0000u,
            "INFLUENCE_HIER",
            Vector3.Zero,
            HasSyntheticRetailRoot: false,
            pivots,
            Array.Empty<EnbW3dRenderPivotFixup>());
        var hlod = new EnbW3dRenderHlod(
            "INFLUENCE_HLOD",
            hierarchy.Name,
            [new EnbW3dRenderHlodLevel(0, float.MaxValue, BitConverter.SingleToUInt32Bits(float.MaxValue), members)]);
        var resource = new EnbW3dRenderResource(
            new EnbW3dRenderResourceKey("influence.w3d", "test.mix", 0, 1),
            "influence.w3d",
            meshes,
            [hierarchy],
            Array.Empty<EnbW3dRenderHierarchyAnimation>(),
            Array.Empty<EnbW3dRenderHierarchyModel>(),
            [hlod]);

        return new EnbCurrentRetailInfluenceNebulaBoundarySource(resource, hlod, Matrix4x4.Identity, "test");
    }

    private static EnbW3dRenderMesh Cube(int ordinal, string name, float halfExtent)
    {
        Vector3[] positions =
        [
            new(-halfExtent, -halfExtent, -halfExtent),
            new( halfExtent, -halfExtent, -halfExtent),
            new( halfExtent,  halfExtent, -halfExtent),
            new(-halfExtent,  halfExtent, -halfExtent),
            new(-halfExtent, -halfExtent,  halfExtent),
            new( halfExtent, -halfExtent,  halfExtent),
            new( halfExtent,  halfExtent,  halfExtent),
            new(-halfExtent,  halfExtent,  halfExtent),
        ];
        uint[] indices =
        [
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            3, 7, 6, 3, 6, 2,
            0, 4, 7, 0, 7, 3,
            1, 2, 6, 1, 6, 5,
        ];

        return new EnbW3dRenderMesh(
            ordinal,
            name,
            "INFLUENCE_HIER",
            MathF.Sqrt(3f) * halfExtent,
            positions,
            Array.Empty<Vector3>(),
            Array.Empty<ushort>(),
            indices,
            Array.Empty<EnbW3dRenderTriangleMetadata>(),
            Array.Empty<EnbW3dRenderVertexMaterial>(),
            Array.Empty<EnbW3dRenderMaterialPass>())
        {
            BoundingBoxMin = new Vector3(-halfExtent),
            BoundingBoxMax = new Vector3(halfExtent),
            BoundingSphereCenter = Vector3.Zero,
        };
    }
}
