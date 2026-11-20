using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbW3dModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VertexJoinMapUsesHeaderCountLastChildAndPreservesForwardReferences(bool trailingBytes)
    {
        var last=UInt32Payload(2,2,2);
        if(trailingBytes)last=[..last,0xa5,0x5a,0xff];
        var bytes=ContainerChunk(0,
            LeafChunk(0x1f,BuildMeshHeader("JOIN","",0,3,1)),
            LeafChunk(2,Vector3Payload([Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ])),
            LeafChunk(0x22,UInt32Payload(0,0,0)),LeafChunk(0x22,last));
        Assert.True(EnbW3dModel.TryDecode(bytes,out var model));
        Assert.Equal(new uint[]{2,2,2},Assert.Single(model.Meshes).AuthoredVertexJoinMap);
        var missing=ContainerChunk(0,LeafChunk(0x1f,BuildMeshHeader("JOIN","",0,3,1)),
            LeafChunk(2,Vector3Payload([Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ])));
        Assert.True(EnbW3dModel.TryDecode(missing,out var noMap));Assert.Null(Assert.Single(noMap.Meshes).AuthoredVertexJoinMap);
    }

    [Fact]
    public void VertexJoinMapShortReadFailsBeforeACompleteMeshIsPublished()
    {
        var bytes=ContainerChunk(0,LeafChunk(0x1f,BuildMeshHeader("JOIN","",0,3,1)),
            LeafChunk(2,Vector3Payload([Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ])),LeafChunk(0x22,UInt32Payload(2,2)));
        Assert.False(EnbW3dModel.TryDecode(bytes,out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GenericMeshDecoderPreservesGeometryPassesAndRetailUvFlip(bool containerHints)
    {
        Vector3[] vertices =
        [
            new(1, 0, 0),
            new(0, 1, 0),
            new(0, 0, 1),
        ];
        Vector3[] normals =
        [
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ,
        ];
        Vector2[] sourceUvs =
        [
            new(0.10f, 0.20f),
            new(0.50f, 0.75f),
            new(0.90f, 1.25f),
        ];

        byte[] header = BuildMeshHeader("TESTMESH", "TESTROOT", triangleCount: 1, vertexCount: 3, radius: 5.5f);
        byte[] shader = [3, 1, 0, 0, 2, 1, 0, 1, 1, 2, 0, 2, 0, 2, 0, 2];
        byte[] materialInfo = UInt32Payload(1, 0, 1, 1);
        byte[] triangle = TrianglePayload(0, 1, 2, attributes: 0x1234u, Vector3.UnitZ, distance: -0.25f);
        byte[] diffuse =
        [
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
        ];

        byte[] bytes = ContainerChunk(
            0x0000,
            LeafChunk(0x001F, header),
            LeafChunk(0x0002, Vector3Payload(vertices)),
            LeafChunk(0x0003, Vector3Payload(normals)),
            LeafChunk(0x0020, triangle),
            LeafChunk(0x0028, materialInfo),
            LeafChunk(0x0029, shader),
            ContainerChunk(
                0x0030,
                ContainerChunk(0x0031, LeafChunk(0x0032, Encoding.ASCII.GetBytes("TEST.TGA\0")))),
            ContainerChunk(
                0x0038,
                LeafChunk(0x0039, UInt32Payload(0)),
                LeafChunk(0x003A, UInt32Payload(0)),
                LeafChunk(0x003B, diffuse),
                ContainerChunk(
                    0x0048,
                    LeafChunk(0x0049, UInt32Payload(0)),
                    LeafChunk(0x004A, Vector2Payload(sourceUvs)))));

        if(!containerHints)ClearHints(0,bytes.Length);
        void ClearHints(int start,int end)
        {
            for(int at=start;at<end;)
            {
                uint size=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at+4));
                int length=(int)(size&0x7fffffffu);
                if((size&0x80000000u)!=0)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at+4),(uint)length);
                    ClearHints(at+8,at+8+length);
                }
                at+=8+length;
            }
        }
        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);
        Assert.Equal("TESTMESH", mesh.Name);
        Assert.Equal("TESTROOT", mesh.ContainerName);
        Assert.Equal(3, mesh.Vertices.Count);
        Assert.Equal(3, mesh.Normals.Count);
        Assert.Equal(5.5f, mesh.BoundingRadius, 3);

        EnbW3dTriangle decodedTriangle = Assert.Single(mesh.Triangles);
        Assert.Equal((uint)0, decodedTriangle.Vertex0);
        Assert.Equal((uint)1, decodedTriangle.Vertex1);
        Assert.Equal((uint)2, decodedTriangle.Vertex2);
        Assert.Equal(0x1234u, decodedTriangle.Attributes);
        Assert.Equal(Vector3.UnitZ, decodedTriangle.Normal);
        Assert.Equal(-0.25f, decodedTriangle.Distance, 4);

        EnbW3dMaterialInfo info = mesh.MaterialInfo
            ?? throw new Xunit.Sdk.XunitException("Material info was not decoded.");
        Assert.Equal((uint)1, info.PassCount);
        Assert.Equal((uint)1, info.ShaderCount);
        Assert.Equal((uint)1, info.TextureCount);
        Assert.Equal(shader, Assert.Single(mesh.Shaders).Raw.ToArray());
        Assert.Equal(["TEST.TGA"], mesh.TextureNames);

        EnbW3dMaterialPass pass = Assert.Single(mesh.MaterialPasses);
        Assert.Equal([0u], pass.VertexMaterialIds);
        Assert.Equal([0u], pass.ShaderIds);
        Assert.Equal(new EnbColor32(255, 0, 0, 255), pass.DiffuseColors[0]);

        EnbW3dTextureStage stage = Assert.Single(pass.TextureStages);
        Assert.True(stage.TryResolveSingleTextureName(mesh.TextureNames, out string textureName));
        Assert.Equal("TEST.TGA", textureName);
        Assert.Equal(sourceUvs[0].X, stage.RuntimeTexCoords[0].X, 4);
        Assert.Equal(1.0f - sourceUvs[0].Y, stage.RuntimeTexCoords[0].Y, 4);
        Assert.Equal(1.0f - sourceUvs[2].Y, stage.RuntimeTexCoords[2].Y, 4);
    }

    [Fact]
    public void GenericMeshDecoderAcceptsAlternateRetailNormalChunkAndLastNormalChildWins()
    {
        Vector3[] vertices = [Vector3.UnitX, Vector3.UnitY];
        Vector3[] firstNormals = [Vector3.UnitX, Vector3.UnitX];
        Vector3[] winningNormals = [Vector3.UnitY, Vector3.UnitZ];
        byte[] header = BuildMeshHeader("NORMALS", string.Empty, triangleCount: 0, vertexCount: 2, radius: 1.0f);

        byte[] bytes = ContainerChunk(
            0x0000,
            LeafChunk(0x001F, header),
            LeafChunk(0x0002, Vector3Payload(vertices)),
            LeafChunk(0x0003, Vector3Payload(firstNormals)),
            LeafChunk(0x0004, Vector3Payload(winningNormals)));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);
        Assert.Equal(winningNormals, mesh.Normals);
        Assert.Equal<uint?>(0x0004u, mesh.AuthoredNormalChunkType);
    }

    [Fact]
    public void GenericModelDecoderDoesNotFlattenAggregatePayloadIntoMeshes()
    {
        byte[] first = MinimalMesh("ONE");
        byte[] second = MinimalMesh("TWO");
        byte[] wrapped = ContainerChunk(0x0600, first, ContainerChunk(0x0601, second));

        //0091D270 dispatches0600 to its aggregate loader; it never walks
        //embedded mesh/header payloads as top-level model publications.
        Assert.False(EnbW3dModel.TryDecode(wrapped, out _));
        Assert.True(EnbW3dModel.TryDecode([..wrapped, ..MinimalMesh("VISIBLE")], out var model));
        Assert.Equal("VISIBLE", Assert.Single(model.Meshes).Name);
    }

    [Fact]
    public void GenericMeshDecoderRejectsTriangleIndexOutsideVertexArray()
    {
        byte[] header = BuildMeshHeader("BAD", string.Empty, triangleCount: 1, vertexCount: 3, radius: 1.0f);
        byte[] bytes = ContainerChunk(
            0x0000,
            LeafChunk(0x001F, header),
            LeafChunk(0x0002, Vector3Payload(new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })),
            LeafChunk(0x0020, TrianglePayload(0, 1, 3, 0, Vector3.UnitZ, 0)));

        Assert.False(EnbW3dModel.TryDecode(bytes, out _));
    }

    [Fact]
    public void GenericMeshDecoderRejectsMalformedNestedChunkLength()
    {
        byte[] header = BuildMeshHeader("BAD", string.Empty, triangleCount: 0, vertexCount: 1, radius: 1.0f);
        byte[] malformed = ContainerChunk(
            0x0000,
            LeafChunk(0x001F, header),
            LeafChunk(0x0002, Vector3Payload(new[] { Vector3.UnitX })));

        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(4, 4), 0x8000_FFFFu);
        Assert.False(EnbW3dModel.TryDecode(malformed, out _));
    }


    [Fact]
    public void GenericMeshDecoderPreservesVertexMaterialChannelsAndMapperArguments()
    {
        byte[] header = BuildMeshHeader("MATERIAL", string.Empty, triangleCount: 0, vertexCount: 1, radius: 1.0f);
        byte[] materialInfo = UInt32Payload(0, 1, 0, 0);
        byte[] vertexMaterialInfo = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(vertexMaterialInfo.AsSpan(0, 4), 0x0004_0400u);
        new byte[] { 1, 2, 3, 0xA1 }.CopyTo(vertexMaterialInfo, 4);
        new byte[] { 4, 5, 6, 0xA2 }.CopyTo(vertexMaterialInfo, 8);
        new byte[] { 7, 8, 9, 0xA3 }.CopyTo(vertexMaterialInfo, 12);
        new byte[] { 10, 11, 12, 0xA4 }.CopyTo(vertexMaterialInfo, 16);
        WriteSingle(vertexMaterialInfo.AsSpan(20, 4), 1.5f);
        WriteSingle(vertexMaterialInfo.AsSpan(24, 4), 0.75f);
        WriteSingle(vertexMaterialInfo.AsSpan(28, 4), 0.25f);

        byte[] bytes = ContainerChunk(
            0x0000,
            LeafChunk(0x001F, header),
            LeafChunk(0x0002, Vector3Payload(new[] { Vector3.UnitX })),
            LeafChunk(0x0028, materialInfo),
            ContainerChunk(
                0x002A,
                ContainerChunk(
                    0x002B,
                    LeafChunk(0x002C, Encoding.ASCII.GetBytes("MARS 2pass ken\0")),
                    LeafChunk(0x002D, vertexMaterialInfo),
                    LeafChunk(0x002E, Encoding.ASCII.GetBytes("UPerSec=.006\r\nUScale=6\r\nVScale=6\r\nUOffset=25%\r\nVOffset=-.5\r\nClampFix=Y\0")),
                    LeafChunk(0x002F, Encoding.ASCII.GetBytes("VPerSec=.1\0")))));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dVertexMaterial material = Assert.Single(Assert.Single(model.Meshes).VertexMaterials);
        Assert.Equal("MARS 2pass ken", material.Name);
        Assert.Equal(0x0004_0400u, material.Info.Attributes);
        Assert.Equal(0x04, material.Info.Mapper0TypeSelector);
        Assert.Equal(0x04, material.Info.Mapper1TypeSelector);
        Assert.Equal(new EnbW3dPackedRgb(1, 2, 3, 0xA1), material.Info.Ambient);
        Assert.Equal(new EnbW3dPackedRgb(4, 5, 6, 0xA2), material.Info.Diffuse);
        Assert.Equal(new EnbW3dPackedRgb(7, 8, 9, 0xA3), material.Info.Specular);
        Assert.Equal(new EnbW3dPackedRgb(10, 11, 12, 0xA4), material.Info.Emissive);
        Assert.Equal(1.5f, material.Info.Power, 4);
        Assert.Equal(0.75f, material.Info.Opacity, 4);
        Assert.Equal(0.25f, material.Info.UnknownFloat, 4);

        EnbW3dMapperArguments mapper0 = material.Mapper0
            ?? throw new Xunit.Sdk.XunitException("Mapper 0 arguments were not decoded.");
        Assert.Equal(6.0f, mapper0.UScale, 4);
        Assert.Equal(6.0f, mapper0.VScale, 4);
        Assert.Equal(0.006f, mapper0.UPerSecond, 6);
        Assert.Equal(0.0f, mapper0.VPerSecond, 4);
        Assert.Equal(0.25f, mapper0.UOffset, 4);
        Assert.Equal(-0.5f, mapper0.VOffset, 4);
        Assert.True(mapper0.ClampFix);

        EnbW3dMapperArguments mapper1 = material.Mapper1
            ?? throw new Xunit.Sdk.XunitException("Mapper 1 arguments were not decoded.");
        Assert.Equal(1.0f, mapper1.UScale, 4);
        Assert.Equal(1.0f, mapper1.VScale, 4);
        Assert.Equal(0.0f, mapper1.UPerSecond, 4);
        Assert.Equal(0.1f, mapper1.VPerSecond, 4);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GenericModelDecoderPreservesHlodLevelsThresholdsAndMembers(bool containerHints)
    {
        byte[] header = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0001_0000u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 2u);
        WriteFixedAscii(header.AsSpan(8, 16), "PLT04A");
        WriteFixedAscii(header.AsSpan(24, 16), "PLT04A");

        byte[] coarse = HlodLevel(
            float.MaxValue,
            (3u, "PLT04A.ATMOS4"),
            (7u, "PLT04A.PLT034"));
        byte[] fine = HlodLevel(
            123.5f,
            (4u, "PLT04A.ATMOS3"),
            (8u, "PLT04A.PLT033"));

        if (!containerHints)
        {
            coarse[7] &= 0x7F;
            fine[7] &= 0x7F;
        }

        byte[] bytes = ContainerChunk(
            0x0700,
            LeafChunk(0x0701, header),
            coarse,
            fine);

        if (!containerHints) bytes[7] &= 0x7F;

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dHierarchicalLod hlod = Assert.Single(model.HierarchicalLods);
        Assert.Equal(0x0001_0000u, hlod.Version);
        Assert.Equal("PLT04A", hlod.Name);
        Assert.Equal("PLT04A", hlod.HierarchyName);
        Assert.Equal(2, hlod.Levels.Count);

        EnbW3dLodLevel level0 = hlod.Levels[0];
        Assert.Equal(float.MaxValue, level0.Threshold);
        Assert.Equal(0x7F7F_FFFFu, level0.RawThreshold);
        Assert.Equal(new EnbW3dLodMember(3, "PLT04A.ATMOS4"), level0.Members[0]);
        Assert.Equal(new EnbW3dLodMember(7, "PLT04A.PLT034"), level0.Members[1]);

        EnbW3dLodLevel level1 = hlod.Levels[1];
        Assert.Equal(123.5f, level1.Threshold, 4);
        Assert.Equal(4u, level1.Members[0].ReferenceIndex);
        Assert.Equal("PLT04A.ATMOS3", level1.Members[0].Name);
    }

    [Fact]
    public void GenericModelDecoderRejectsHlodMemberCountMismatch()
    {
        byte[] header = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 1u);
        WriteFixedAscii(header.AsSpan(8, 16), "BAD");

        byte[] levelHeader = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(levelHeader.AsSpan(0, 4), 2u);
        WriteSingle(levelHeader.AsSpan(4, 4), 10.0f);

        byte[] bytes = ContainerChunk(
            0x0700,
            LeafChunk(0x0701, header),
            ContainerChunk(
                0x0702,
                LeafChunk(0x0703, levelHeader),
                HlodMember(0, "ONLY_ONE")));

        Assert.False(EnbW3dModel.TryDecode(bytes, out _));
    }

    [Fact]
    public void BinaryHierarchyNamesPreserveSpaceNamedPivotIdentityAndPose()
    {
        byte[] bytes = ContainerChunk(0x100,
            LeafChunk(0x101, HierarchyHeader(0x30000, " TREE ", 2, Vector3.Zero)),
            LeafChunk(0x102, [..Pivot("ROOT", uint.MaxValue, Vector3.Zero, Vector3.Zero, 0, 0, 0, 1),
                ..Pivot(" ", 0, new Vector3(10, 20, 30), Vector3.Zero, 0, 0, 0, 1)]));
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        Assert.Equal(" TREE ", Assert.Single(model.Hierarchies).Name);
        var source = new EnbResolvedW3dResource("tree.w3d", " TREE ", new("tree.w3d", "fixture.mix", 0, bytes.Length), model, []);
        Assert.True(EnbNext.Rendering.EnbW3dRenderResourceCompiler.TryCompile(source, out var resource, out var error), error);
        var hierarchy = Assert.Single(resource.Hierarchies);
        Assert.Equal(1, EnbNext.Rendering.EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy, " "));
        Assert.Equal(0, EnbNext.Rendering.EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy, ""));
        Assert.Equal(new Vector3(10, 20, 30), hierarchy.Pivots[1].LocalTransform.Translation);
    }

    [Fact]
    public void HierarchyPivotCountIsBoundedByItsPayload()
    {
        const int count = 65_537;
        byte[] records = new byte[count * 60];
        for (int index = 0; index < count; index++)
            Pivot(index == count - 1 ? "LAST" : "PIVOT", index == 0 ? uint.MaxValue : 0,
                new Vector3(index, 2, 3), Vector3.Zero, 0, 0, 0, 1).CopyTo(records, index * 60);
        byte[] Make(uint declared) => ContainerChunk(0x100,
            LeafChunk(0x101, HierarchyHeader(0x30000, "LARGE", declared, Vector3.Zero)),
            LeafChunk(0x102, records));

        Assert.True(EnbW3dModel.TryDecode(Make(count), out var model));
        var hierarchy = Assert.Single(model.Hierarchies);
        Assert.Equal(count, hierarchy.Pivots.Count);
        Assert.True(EnbNext.Rendering.EnbW3dRenderResourceCompiler.TryCompileHierarchy(hierarchy, out var compiled, out var error), error);
        Assert.Equal(count - 1, EnbNext.Rendering.EnbCurrentRetailAggregateBoneLookup.Resolve(compiled, "LAST"));
        Assert.Equal(new Vector3(count - 1, 2, 3), compiled.Pivots[count - 1].LocalTransform.Translation);
        Assert.False(EnbW3dModel.TryDecode(Make(count + 1), out _));
        Assert.False(EnbW3dModel.TryDecode(Make(int.MaxValue), out _));
        Assert.False(EnbW3dModel.TryDecode(Make(uint.MaxValue), out _));
    }

    [Fact]
    public void BinaryMeshNamesRetainSurroundingWhitespace()
    {
        Assert.True(EnbW3dModel.TryDecode(MinimalMesh(" NAME "), out var model));
        Assert.Equal(" NAME ", Assert.Single(model.Meshes).Name);
        Assert.NotNull(model.FindMesh(" name "));
        Assert.Null(model.FindMesh("NAME"));
    }

    [Fact]
    public void GenericModelDecoderPreservesHierarchyPivotsAndFixups()
    {
        byte[] header = HierarchyHeader(
            version: 0x0003_0000u,
            name: "SHIP_TREE",
            pivotCount: 3,
            center: new Vector3(1.0f, 2.0f, 3.0f));

        byte[] pivots = new[]
        {
            Pivot("ROOT", uint.MaxValue, Vector3.Zero, Vector3.Zero, 1, 0, 0, 0),
            Pivot("~03_01", 0, new Vector3(10, 20, 30), new Vector3(0.1f, 0.2f, 0.3f), 0.5f, 0.25f, 0.125f, 0.0625f),
            Pivot("~WEAP_01", 1, new Vector3(-1, -2, -3), Vector3.Zero, 0, 0, 0, 1),
        }.SelectMany(static record => record).ToArray();

        byte[] fixups = PivotFixups(
            Enumerable.Range(0, 36).Select(static value => value + 0.5f).ToArray());

        byte[] bytes = ContainerChunk(
            0x0100,
            LeafChunk(0x0101, header),
            LeafChunk(0x0102, pivots),
            LeafChunk(0x0103, fixups));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dHierarchy hierarchy = Assert.Single(model.Hierarchies);
        Assert.Equal(0x0003_0000u, hierarchy.Version);
        Assert.Equal("SHIP_TREE", hierarchy.Name);
        Assert.Equal(new Vector3(1, 2, 3), hierarchy.Center);
        Assert.Equal(3, hierarchy.Pivots.Count);
        Assert.Equal(3, hierarchy.PivotFixups.Count);

        EnbW3dPivot root = hierarchy.Pivots[0];
        Assert.True(root.IsRoot);
        Assert.Equal("ROOT", root.Name);

        EnbW3dPivot mount = hierarchy.Pivots[2];
        Assert.False(mount.IsRoot);
        Assert.Equal((uint)1, mount.ParentIndex);
        Assert.Equal(new Vector3(-1, -2, -3), mount.Translation);
        Assert.Equal(new EnbW3dRawQuaternion(0, 0, 0, 1), mount.Rotation);

        Assert.NotNull(hierarchy.FindPivot("~WEAP_01"));
        Assert.Null(hierarchy.FindPivot("~weap_01"));
        Assert.Equal(0.5f, hierarchy.PivotFixups[0].M00, 4);
        Assert.Equal(11.5f, hierarchy.PivotFixups[0].M32, 4);
        Assert.Equal(12.5f, hierarchy.PivotFixups[1].M00, 4);
    }

    [Fact]
    public void GenericModelDecoderRejectsHierarchyParentCycle()
    {
        byte[] bytes = ContainerChunk(
            0x0100,
            LeafChunk(0x0101, HierarchyHeader(1, "BAD", 2, Vector3.Zero)),
            LeafChunk(
                0x0102,
                Pivot("A", 1, Vector3.Zero, Vector3.Zero, 0, 0, 0, 1)
                    .Concat(Pivot("B", 0, Vector3.Zero, Vector3.Zero, 0, 0, 0, 1))
                    .ToArray()));

        Assert.False(EnbW3dModel.TryDecode(bytes, out _));
    }

    [Fact]
    public void GenericModelDecoderPreservesHierarchyModelConnectionKindsAndPivotIndices()
    {
        byte[] bytes = ContainerChunk(
            0x0300,
            LeafChunk(0x0301, HierarchyModelHeader(7, "SHIP", "SHIP_TREE", 3, padded: true)),
            LeafChunk(0x0302, HierarchyModelNode("HULL", 0)),
            LeafChunk(0x0303, HierarchyModelNode("COLLISION", 1)),
            LeafChunk(0x0304, HierarchyModelNode("SKIN", 2, padded: true)),
            LeafChunk(0x0306, HierarchyModelNode("SHADOW", 3)),
            LeafChunk(0x0305, HierarchyModelAux(meshes: 1, collisions: 1, skins: 1, shadows: 1)));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dHierarchyModel hmodel = Assert.Single(model.HierarchyModels);
        Assert.Equal((uint)7, hmodel.Version);
        Assert.Equal("SHIP", hmodel.Name);
        Assert.Equal("SHIP_TREE", hmodel.HierarchyName);
        Assert.Equal(3, hmodel.Nodes.Count);

        Assert.Equal(EnbW3dHierarchyNodeKind.RenderObject, hmodel.Nodes[0].Kind);
        Assert.Equal((ushort)0, hmodel.Nodes[0].PivotIndex);
        Assert.Equal(EnbW3dHierarchyNodeKind.Collision, hmodel.Nodes[1].Kind);
        Assert.Equal(EnbW3dHierarchyNodeKind.Skin, hmodel.Nodes[2].Kind);
        Assert.Equal((ushort)2, hmodel.Nodes[2].PivotIndex);
        Assert.Single(hmodel.FindNodes("SHIP.SKIN"));
        Assert.Empty(hmodel.FindNodes("ship.skin"));

        Assert.Null(hmodel.AuxData);
    }

    [Fact]
    public void GenericModelDecoderRejectsHierarchyModelConnectionCountMismatch()
    {
        byte[] bytes = ContainerChunk(
            0x0300,
            LeafChunk(0x0301, HierarchyModelHeader(1, "BAD", "TREE", 2, padded: false)),
            LeafChunk(0x0302, HierarchyModelNode("ONLY_ONE", 0)));

        Assert.False(EnbW3dModel.TryDecode(bytes, out _));
    }

    [Fact]
    public void GenericMeshDecoderPreservesVertexInfluencesAndChannelMasks()
    {
        uint vertexChannels = EnbW3dVertexChannels.Location | EnbW3dVertexChannels.BoneId;
        byte[] header = BuildMeshHeader(
            "SKINNED",
            "SHIP",
            triangleCount: 0,
            vertexCount: 3,
            radius: 4.0f,
            vertexChannels: vertexChannels,
            faceChannels: EnbW3dFaceChannels.Face);

        byte[] bytes = ContainerChunk(
            0x0000,
            LeafChunk(0x001F, header),
            LeafChunk(0x0002, Vector3Payload(new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })),
            LeafChunk(0x000E, VertexInfluencePayload(1, 3, 5)));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);
        Assert.Equal(vertexChannels, mesh.VertexChannels);
        Assert.Equal(EnbW3dFaceChannels.Face, mesh.FaceChannels);
        Assert.Equal(3, mesh.VertexInfluences.Count);
        Assert.Equal((ushort)1, mesh.VertexInfluences[0].BoneIndex);
        Assert.Equal((ushort)3, mesh.VertexInfluences[1].BoneIndex);
        Assert.Equal((ushort)5, mesh.VertexInfluences[2].BoneIndex);
    }

    [Fact]
    public void GenericMeshDecoderRejectsBoneChannelWithoutCompleteInfluenceArray()
    {
        byte[] header = BuildMeshHeader(
            "BADSKIN",
            "SHIP",
            triangleCount: 0,
            vertexCount: 2,
            radius: 1.0f,
            vertexChannels: EnbW3dVertexChannels.Location | EnbW3dVertexChannels.BoneId);

        byte[] bytes = ContainerChunk(
            0x0000,
            LeafChunk(0x001F, header),
            LeafChunk(0x0002, Vector3Payload(new[] { Vector3.UnitX, Vector3.UnitY })),
            LeafChunk(0x000E, VertexInfluencePayload(7)));

        Assert.False(EnbW3dModel.TryDecode(bytes, out _));
    }

    [Fact]
    public void GenericModelDecoderPreservesClassicHierarchyAnimationChannels()
    {
        byte[] bytes = ContainerChunk(
            0x0200,
            LeafChunk(0x0201, AnimationHeader(
                version: 0x0001_0000u,
                name: "SHIP.IDLE",
                hierarchyName: "SHIP_TREE",
                numFrames: 4,
                frameRate: 30)),
            LeafChunk(0x0202, AnimationChannel(
                firstFrame: 1,
                lastFrame: 3,
                vectorLength: 1,
                flags: (ushort)EnbW3dAnimationChannelKind.TranslationX,
                pivotIndex: 2,
                padding: 0xBEEF,
                data: new[] { 10.0f, 20.0f, 30.0f })),
            LeafChunk(0x0202, AnimationChannel(
                firstFrame: 0,
                lastFrame: 1,
                vectorLength: 4,
                flags: (ushort)EnbW3dAnimationChannelKind.Quaternion,
                pivotIndex: 1,
                padding: 0,
                data: new[] { 0.0f, 0.0f, 0.0f, 1.0f, 0.1f, 0.2f, 0.3f, 0.9f })),
            LeafChunk(0x0203, AnimationBitChannel(
                firstFrame: 0,
                lastFrame: 3,
                flags: (ushort)EnbW3dAnimationBitChannelKind.Visibility,
                pivotIndex: 3,
                defaultValue: 1,
                packedData: new byte[] { 0b0000_1101 })));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        Assert.Empty(model.Meshes);
        EnbW3dHierarchyAnimation animation = Assert.Single(model.Animations);
        Assert.Equal(0x0001_0000u, animation.Version);
        Assert.Equal("SHIP.IDLE", animation.Name);
        Assert.Equal("SHIP_TREE", animation.HierarchyName);
        Assert.Equal((uint)4, animation.NumFrames);
        Assert.Equal((uint)30, animation.FrameRate);
        Assert.Equal(2, animation.Channels.Count);

        EnbW3dAnimationChannel translation = animation.Channels[0];
        Assert.Equal((ushort)1, translation.FirstFrame);
        Assert.Equal((ushort)3, translation.LastFrame);
        Assert.Equal(3, translation.FrameCount);
        Assert.Equal((ushort)1, translation.VectorLength);
        Assert.Equal(EnbW3dAnimationChannelKind.TranslationX, translation.KnownKind);
        Assert.Equal((ushort)2, translation.PivotIndex);
        Assert.Equal((ushort)0xBEEF, translation.Padding);
        Assert.Equal(new[] { 10.0f, 20.0f, 30.0f }, translation.Data.ToArray());

        EnbW3dAnimationChannel quaternion = animation.Channels[1];
        Assert.Equal(EnbW3dAnimationChannelKind.Quaternion, quaternion.KnownKind);
        Assert.Equal((ushort)4, quaternion.VectorLength);
        Assert.Equal(8, quaternion.Data.Count);

        EnbW3dAnimationBitChannel visibility = Assert.Single(animation.BitChannels);
        Assert.Equal(EnbW3dAnimationBitChannelKind.Visibility, visibility.KnownKind);
        Assert.Equal((ushort)3, visibility.PivotIndex);
        Assert.Equal((byte)1, visibility.DefaultValue);
        Assert.Equal(new byte[] { 0b0000_1101 }, visibility.PackedData.ToArray());
    }

    [Fact]
    public void GenericModelDecoderKeepsAnimationChannelPastDeclaredFrameCount()
    {
        byte[] bytes = ContainerChunk(
            0x0200,
            LeafChunk(0x0201, AnimationHeader(1, "BAD", "TREE", numFrames: 2, frameRate: 30)),
            LeafChunk(0x0202, AnimationChannel(
                firstFrame: 0,
                lastFrame: 2,
                vectorLength: 1,
                flags: (ushort)EnbW3dAnimationChannelKind.TranslationY,
                pivotIndex: 0,
                padding: 0,
                data: new[] { 1.0f, 2.0f, 3.0f })));

        // 009C2EF0 never compares a channel's LastFrame with NumFrames.
        Assert.True(EnbW3dModel.TryDecode(bytes, out _));
    }

    [Fact]
    public void ClassicAnimationReadsDeclaredSamplesAndSkipsTrailingChunkBytes()
    {
        byte[] channel=AnimationChannel(0,1,1,0,0,0,new[] { 2f,7f });
        byte[] bytes=ContainerChunk(0x0200,
            LeafChunk(0x0201,AnimationHeader(0x40001,"RESIZE","TREE",2,30)),
            LeafChunk(0x0202,[..channel,0,0,192,127])); // unused NaN tail, not a sample
        Assert.True(EnbW3dModel.TryDecode(bytes,out var model));
        Assert.Equal(new[] {2f,7f},Assert.Single(Assert.Single(model.Animations).Channels).Data);
        byte[] truncated=ContainerChunk(0x0200,
            LeafChunk(0x0201,AnimationHeader(0x40001,"RESIZE","TREE",2,30)),
            LeafChunk(0x0202,channel[..^1]));
        Assert.False(EnbW3dModel.TryDecode(truncated,out _));
    }

    private static byte[] MinimalMesh(string name)
    {
        byte[] header = BuildMeshHeader(name, string.Empty, triangleCount: 0, vertexCount: 1, radius: 1.0f);
        return ContainerChunk(
            0x0000,
            LeafChunk(0x001F, header),
            LeafChunk(0x0002, Vector3Payload(new[] { Vector3.UnitX })));
    }

    private static byte[] BuildMeshHeader(
        string meshName,
        string containerName,
        uint triangleCount,
        uint vertexCount,
        float radius,
        uint vertexChannels = 0,
        uint faceChannels = 0)
    {
        byte[] header = new byte[116];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 0x0102_0304u);
        WriteFixedAscii(header.AsSpan(8, 16), meshName);
        WriteFixedAscii(header.AsSpan(24, 16), containerName);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), triangleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44, 4), vertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68, 4), vertexChannels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(72, 4), faceChannels);
        WriteSingle(header.AsSpan(112, 4), radius);
        return header;
    }

    private static byte[] TrianglePayload(
        uint v0,
        uint v1,
        uint v2,
        uint attributes,
        Vector3 normal,
        float distance)
    {
        byte[] bytes = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), v0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), v1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), v2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), attributes);
        WriteSingle(bytes.AsSpan(16, 4), normal.X);
        WriteSingle(bytes.AsSpan(20, 4), normal.Y);
        WriteSingle(bytes.AsSpan(24, 4), normal.Z);
        WriteSingle(bytes.AsSpan(28, 4), distance);
        return bytes;
    }

    private static byte[] HlodLevel(float threshold, params (uint ReferenceIndex, string Name)[] members)
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), checked((uint)members.Length));
        WriteSingle(header.AsSpan(4, 4), threshold);

        return ContainerChunk(
            0x0702,
            new[] { LeafChunk(0x0703, header) }
                .Concat(members.Select(static member => HlodMember(member.ReferenceIndex, member.Name)))
                .ToArray());
    }

    private static byte[] HlodMember(uint referenceIndex, string name)
    {
        byte[] payload = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), referenceIndex);
        WriteFixedAscii(payload.AsSpan(4, 32), name);
        return LeafChunk(0x0704, payload);
    }

    private static byte[] HierarchyHeader(uint version, string name, uint pivotCount, Vector3 center)
    {
        byte[] payload = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), version);
        WriteFixedAscii(payload.AsSpan(4, 16), name);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20, 4), pivotCount);
        WriteSingle(payload.AsSpan(24, 4), center.X);
        WriteSingle(payload.AsSpan(28, 4), center.Y);
        WriteSingle(payload.AsSpan(32, 4), center.Z);
        return payload;
    }

    private static byte[] Pivot(
        string name,
        uint parentIndex,
        Vector3 translation,
        Vector3 eulerAngles,
        float q0,
        float q1,
        float q2,
        float q3)
    {
        byte[] payload = new byte[60];
        WriteFixedAscii(payload.AsSpan(0, 16), name);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16, 4), parentIndex);
        WriteSingle(payload.AsSpan(20, 4), translation.X);
        WriteSingle(payload.AsSpan(24, 4), translation.Y);
        WriteSingle(payload.AsSpan(28, 4), translation.Z);
        WriteSingle(payload.AsSpan(32, 4), eulerAngles.X);
        WriteSingle(payload.AsSpan(36, 4), eulerAngles.Y);
        WriteSingle(payload.AsSpan(40, 4), eulerAngles.Z);
        WriteSingle(payload.AsSpan(44, 4), q0);
        WriteSingle(payload.AsSpan(48, 4), q1);
        WriteSingle(payload.AsSpan(52, 4), q2);
        WriteSingle(payload.AsSpan(56, 4), q3);
        return payload;
    }

    private static byte[] PivotFixups(params float[] values)
    {
        byte[] payload = new byte[values.Length * 4];
        for (int index = 0; index < values.Length; index++)
            WriteSingle(payload.AsSpan(index * 4, 4), values[index]);
        return payload;
    }

    private static byte[] HierarchyModelHeader(
        uint version,
        string name,
        string hierarchyName,
        ushort connections,
        bool padded)
    {
        byte[] payload = new byte[padded ? 40 : 38];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), version);
        WriteFixedAscii(payload.AsSpan(4, 16), name);
        WriteFixedAscii(payload.AsSpan(20, 16), hierarchyName);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(36, 2), connections);
        return payload;
    }

    private static byte[] HierarchyModelNode(string name, ushort pivotIndex, bool padded = false)
    {
        byte[] payload = new byte[padded ? 20 : 18];
        WriteFixedAscii(payload.AsSpan(0, 16), name);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16, 2), pivotIndex);
        return payload;
    }

    private static byte[] HierarchyModelAux(uint meshes, uint collisions, uint skins, uint shadows)
    {
        byte[] payload = new byte[184];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 0xAABB_CCDDu);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), meshes);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), collisions);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12, 4), skins);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16, 4), shadows);
        WriteSingle(payload.AsSpan(48, 4), -25.0f);
        WriteSingle(payload.AsSpan(52, 4), 250.0f);
        return payload;
    }

    private static byte[] VertexInfluencePayload(params ushort[] boneIndices)
    {
        byte[] payload = new byte[boneIndices.Length * 8];
        for (int index = 0; index < boneIndices.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(index * 8, 2), boneIndices[index]);
        return payload;
    }

    private static byte[] AnimationHeader(
        uint version,
        string name,
        string hierarchyName,
        uint numFrames,
        uint frameRate)
    {
        byte[] payload = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), version);
        WriteFixedAscii(payload.AsSpan(4, 16), name);
        WriteFixedAscii(payload.AsSpan(20, 16), hierarchyName);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(36, 4), numFrames);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(40, 4), frameRate);
        return payload;
    }

    private static byte[] AnimationChannel(
        ushort firstFrame,
        ushort lastFrame,
        ushort vectorLength,
        ushort flags,
        ushort pivotIndex,
        ushort padding,
        params float[] data)
    {
        byte[] payload = new byte[12 + (data.Length * 4)];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), firstFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), lastFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), vectorLength);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6, 2), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), pivotIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10, 2), padding);
        for (int index = 0; index < data.Length; index++)
            WriteSingle(payload.AsSpan(12 + (index * 4), 4), data[index]);
        return payload;
    }

    private static byte[] AnimationBitChannel(
        ushort firstFrame,
        ushort lastFrame,
        ushort flags,
        ushort pivotIndex,
        byte defaultValue,
        byte[] packedData)
    {
        byte[] payload = new byte[9 + packedData.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), firstFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), lastFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6, 2), pivotIndex);
        payload[8] = defaultValue;
        packedData.CopyTo(payload, 9);
        return payload;
    }

    private static byte[] UInt32Payload(params uint[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4, 4), values[index]);
        return bytes;
    }

    private static byte[] Vector2Payload(IEnumerable<Vector2> values)
    {
        Vector2[] snapshot = values.ToArray();
        byte[] bytes = new byte[snapshot.Length * 8];
        for (int index = 0; index < snapshot.Length; index++)
        {
            WriteSingle(bytes.AsSpan((index * 8) + 0, 4), snapshot[index].X);
            WriteSingle(bytes.AsSpan((index * 8) + 4, 4), snapshot[index].Y);
        }
        return bytes;
    }

    private static byte[] Vector3Payload(IEnumerable<Vector3> values)
    {
        Vector3[] snapshot = values.ToArray();
        byte[] bytes = new byte[snapshot.Length * 12];
        for (int index = 0; index < snapshot.Length; index++)
        {
            WriteSingle(bytes.AsSpan((index * 12) + 0, 4), snapshot[index].X);
            WriteSingle(bytes.AsSpan((index * 12) + 4, 4), snapshot[index].Y);
            WriteSingle(bytes.AsSpan((index * 12) + 8, 4), snapshot[index].Z);
        }
        return bytes;
    }

    private static void WriteSingle(Span<byte> destination, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length - 1)).CopyTo(destination);
    }

    private static byte[] LeafChunk(uint type, byte[] payload)
        => Chunk(type, payload, container: false);

    private static byte[] ContainerChunk(uint type, params byte[][] children)
        => Chunk(type, children.SelectMany(static child => child).ToArray(), container: true);

    private static byte[] Chunk(uint type, byte[] payload, bool container)
    {
        byte[] bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        uint size = checked((uint)payload.Length) | (container ? 0x8000_0000u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), size);
        payload.CopyTo(bytes, 8);
        return bytes;
    }
}
