using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailTerrainTests
{
    [Fact]
    public void ReducedTerrainCullingRetainsTheOriginalHeightEnvelope()
    {
        // Reduction can erase an isolated peak. 46B777 still loads the
        // original height+8 maximum for the conservative patch culling box.
        var terrain=new EnbRetailTerrain(9901,64,new ushort[4096],1024,1,"color","detail",Vector2.One,
            false,4,false,0,"") {OriginalMaximum=128};
        var surface=new EnbRetailTerrainSurface(terrain,new("color","test",0,1),new("detail","test",1,1));
        var shortened=new EnbRetailTerrainSurface(terrain.Copy().SetOriginalMaximum(0),new("color","test",0,1),new("detail","test",1,1));
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(-600,0,64),Quaternion.Identity,new(800,600,1),.01,new(6,6000),out var camera));
        Assert.NotEmpty(surface.Evaluate(camera,0));
        Assert.Empty(shortened.Evaluate(camera,0));
    }

    [Fact]
    public void WrappingSurfaceRetainsGeometryAcrossPositiveAndNegativeTileCrossings()
    {
        var terrain=new EnbRetailTerrain(9901,64,Enumerable.Range(0,4096).Select(i=>(ushort)(1+i%64)).ToArray(),
            1024,1,"color","detail",Vector2.One,false,4,false,0,"") {OriginalMaximum=64,Wrapping=true};
        var surface=new EnbRetailTerrainSurface(terrain,new("color","test",0,1),new("detail","test",1,1));
        IReadOnlyList<EnbRetailTerrainPatchDraw> Render(Vector3 offset)
        {
            Assert.True(EnbResolvedRenderCamera.TryCreate(new Vector3(0,0,128)+offset,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY,.45f),new(800,600,1),Math.PI/2,new(6,6000),out var camera));
            return surface.Evaluate(camera,0);
        }
        var original=Render(Vector3.Zero).ToArray();Assert.NotEmpty(original);
        var originalPatches=surface.CurrentPatches.ToArray();
        foreach(var offset in new[]{new Vector3(1024,0,0),new Vector3(-1024,0,0),new Vector3(0,2048,0),new Vector3(-2048,-1024,0),Vector3.Zero})
        {
            var draws=Render(offset);Assert.Equal(original.Length,draws.Count);
            for(int i=0;i<originalPatches.Length;i++)
                Assert.Equal(originalPatches[i] with {Translation=originalPatches[i].Translation+offset},surface.CurrentPatches[i]);
            for(int i=0;i<draws.Count;i++)
            {
                Assert.Same(original[i].Mesh,draws[i].Mesh);
                Assert.Equal(original[i].WorldTransform.Translation+offset,draws[i].WorldTransform.Translation);
            }
        }
    }

    [Fact]
    public void SeaSwatchUsesTheNamedRegisteredMeshAndRejectsNonMeshPrototypes()
    {
        string path=Path.GetTempFileName();
        try
        {
            var source=EnbR4W3dRenderResourceTests.BuildResolvedResource(0,writePath:path);
            byte[] bytes=File.ReadAllBytes(path);
            byte[]? second=null;
            for(int offset=0;offset<bytes.Length;)
            {
                uint id=System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
                int length=checked((int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset+4))&0x7fffffffu)+8);
                if(id==0){second=bytes.AsSpan(offset,length).ToArray();break;}
                offset+=length;
            }
            Assert.NotNull(second);
            byte[] name=Encoding.ASCII.GetBytes("SHIPMESH"),replacement=Encoding.ASCII.GetBytes("TIDEMESH");
            for(int i=0;i<=second.Length-name.Length;i++)
                if(second.AsSpan(i,name.Length).SequenceEqual(name))replacement.CopyTo(second,i);
            Assert.True(EnbW3dModel.TryDecode([..bytes,..second],out var model));
            using var cache=new EnbW3dRenderResourceCache();
            Assert.True(cache.TryLoadFile(source with {Model=model},out var compiled,out var error),error);
            Assert.Equal(2,compiled.Meshes.Count);
            Assert.True(EnbRetailTerrainWater.TrySelectSwatch(cache,"SHIPROOT.TIDEMESH",out var selected));
            Assert.Same(compiled.Meshes[1],selected);
            Assert.True(EnbRetailTerrainWater.TrySelectSwatch(cache,"shiproot.shipmesh",out selected));
            Assert.Same(compiled.Meshes[0],selected);
            Assert.False(EnbRetailTerrainWater.TrySelectSwatch(cache,"SHIPROOT",out _));
            Assert.False(EnbRetailTerrainWater.TrySelectSwatch(cache,"missing",out _));
            // A duplicate file load preserves the first registered prototype.
            Assert.True(cache.TryLoadFile(source,out _,out error),error);
            Assert.True(EnbRetailTerrainWater.TrySelectSwatch(cache,"SHIPROOT.TIDEMESH",out selected));
            Assert.Same(compiled.Meshes[1],selected);
        }
        finally {File.Delete(path);}
    }

    private static EnbRetailTerrain Load(bool flip) => EnbRetailTerrain.Load(4195,
        "[HEIGHTMAP]\nHEIGHT_MAP=test.pgm\nHSCALE=40\nVSCALE=150\nFLIP_PGM=" + flip +
        "\n[LAND]\nPATCH_GRID_WIDTH=2\nCOLOR_MAP=color.tga\nDETAIL_MAP=detail.tga\nFLIP_TGA=TRUE\nXDETAIL_SCALE=50\nYDETAIL_SCALE=50",
        _ => Encoding.ASCII.GetBytes("P2\n# Header maximum deliberately differs from sample maximum\n4 4\n255\n0 1 2 3\n4 5 6 7\n8 9 10 11\n12 13 14 15"));

    [Fact]
    public void HeightUsesActualMaximumAndAuthoredRowFlip()
    {
        var normal = Load(false);
        var flipped = Load(true);
        Assert.Equal("water_swatch", normal.SeaSwatch);
        Assert.Equal(new Vector3(-20, -20, 0), normal.Position(0, 0));
        Assert.Equal(new Vector3(10, 10, 150), normal.Position(3, 3));
        Assert.Equal(120, flipped.Position(0, 0).Z);
        Assert.Equal(30, flipped.Position(3, 3).Z);
        // Retail masks the boundary sample even on a non-wrapping land grid.
        Assert.Equal(new Vector3(20, -20, 0), normal.Position(4, 0));
    }

    [Theory]
    [InlineData("$2")]
    [InlineData("2h")]
    [InlineData("2 trailing")]
    public void PatchGridUsesRetailIntegerSelector(string value)
    {
        var terrain=EnbRetailTerrain.Load(1,"[HEIGHTMAP]\nHEIGHT_MAP=test.pgm\n[LAND]\nPATCH_GRID_WIDTH="+value,
            _=>Encoding.ASCII.GetBytes("P2 4 4 255 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16"));
        Assert.Equal(2,terrain.PatchGridWidth);
    }

    [Fact]
    public void AdjacentPatchesSharePositionsAndTextureCoordinates()
    {
        var terrain = Load(false);
        var meshes = EnbRetailTerrainMesh.Build(terrain,
            new("color.tga", "test.mix", 0, 1), new("detail.tga", "test.mix", 1, 1));
        Assert.Equal(4, meshes.Count);
        for (int y = 0; y < 3; y++)
        {
            Assert.Equal(meshes[0].Positions[y * 3 + 2], meshes[1].Positions[y * 3]);
            Assert.Equal(meshes[0].MaterialPasses[0].TextureStages[0].EffectiveRuntimeTexCoords[y * 3 + 2],
                meshes[1].MaterialPasses[0].TextureStages[0].EffectiveRuntimeTexCoords[y * 3]);
        }
        foreach (var mesh in meshes)
        {
            Assert.Equal(24, mesh.Indices.Count);
            Assert.All(mesh.Indices, i => Assert.InRange(i, 0u, 8u));
            Assert.All(mesh.MaterialPasses[0].TriangleBindings, binding =>
                Assert.InRange(binding.MaterialVertexIndex, 0, 8));
        }
    }

    [Fact]
    public void PlacementHeightMatchesRenderedTrianglesOnBothStripDirections()
    {
        var terrain = Load(false).Copy().SetHeights([0, 8, 2, 9, 4, 1, 15, 3, 7, 14, 0, 6, 2, 5, 13, 1]);
        var meshes = EnbRetailTerrainMesh.Build(terrain,
            new("color.tga", "test.mix", 0, 1), new("detail.tga", "test.mix", 1, 1));
        foreach (var mesh in meshes)
        for (int i = 0; i < mesh.Indices.Count; i += 3)
        {
            var center = (mesh.Positions[(int)mesh.Indices[i]] + mesh.Positions[(int)mesh.Indices[i+1]] +
                mesh.Positions[(int)mesh.Indices[i+2]]) / 3;
            Assert.InRange(MathF.Abs(center.Z - EnbRetailTerrainSampler.For(terrain).Height(new(center.X, center.Y))), 0, .001f);
        }
    }

    [Fact]
    public void PrelightShadowsAndNightAmbientModulateTheAuthoredColor()
    {
        var terrain = Load(false);
        var cache = new EnbRetailTerrainPrelight(new byte[16], new ushort[16], new ushort[16],
            Enumerable.Repeat((byte)255, 16).ToArray(), Enumerable.Repeat((byte)255, 16).ToArray(),
            Enumerable.Repeat((byte)255, 16).ToArray(), Enumerable.Repeat(Vector3.UnitZ, 256).ToArray());
        var color = EnbTextureImage.CreateCurrentRetailRuntimeRgb8([(4, 4,
            Enumerable.Repeat(new EnbColor32(200, 160, 120, 255), 16).ToArray())]);
        var daylight = new EnbRetailTerrainLightState(Vector3.One, Vector3.UnitZ, 1, .1f, Vector3.Zero, true);
        var day = RetailTestShims.ShadeTerrain(terrain, cache, color, daylight).GetPixel(0, 0);
        Assert.Equal(new EnbColor32(199, 159, 119, 255), day);
        cache.PositiveHorizons[0] = ushort.MaxValue;
        Assert.Equal(new EnbColor32(0, 0, 0, 255),
            RetailTestShims.ShadeTerrain(terrain, cache, color, daylight).GetPixel(0, 3));
        Assert.Equal(new EnbColor32(199, 159, 119, 255),
            RetailTestShims.ShadeTerrain(terrain, cache, color, daylight).GetPixel(0, 0));
        var night = RetailTestShims.ShadeTerrain(terrain, cache, color,
            daylight with { Enabled = false, Ambient = new(0, .1f, 0) }).GetPixel(0, 0);
        Assert.Equal(new EnbColor32(0, 15, 0, 255), night);
        var emissiveNight = RetailTestShims.ShadeTerrain(terrain, cache, color,
            daylight with { Enabled = false, Ambient = Vector3.Zero }, colorAlphaIsIllumination: true).GetPixel(0, 0);
        Assert.Equal(new EnbColor32(199, 159, 119, 255), emissiveNight);
        var saturated = RetailTestShims.ShadeTerrain(terrain, cache, color,
            daylight with { Enabled = false, Ambient = Vector3.One }, colorAlphaIsIllumination: true).GetPixel(0, 0);
        Assert.Equal(new EnbColor32(255, 255, 238, 255), saturated);
    }

    [Fact]
    public void PrelightCacheRejectsTruncationAndWrongSampleCount()
    {
        var bytes = new byte[4 + 16 * 8 + 256 * 12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, 16);
        Assert.Equal(16, EnbRetailTerrainPrelight.Decode(bytes, 16).NormalIndices.Length);
        Assert.Throws<InvalidDataException>(() => EnbRetailTerrainPrelight.Decode(bytes, 4));
        Assert.Throws<InvalidDataException>(() => EnbRetailTerrainPrelight.Decode(bytes[..^1], 16));
    }

    [Fact]
    public void PrelightCachePreservesNormalWordsAndIgnoresTrailingBytes()
    {
        const int count=16, normalOffset=4+count*8;
        var bytes=new byte[normalOffset+256*12+19];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes,count);
        uint[] words=[0x7fc12345,0x7f800000,0xff800000,0x80000000,1,0x3f800000];
        for(int index=0;index<words.Length;index++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(normalOffset+index*4),words[index]);
        bytes.AsSpan(bytes.Length-19).Fill(0xff);
        var cache=EnbRetailTerrainPrelight.Decode(bytes,count);
        float[] actual=[cache.Normals[0].X,cache.Normals[0].Y,cache.Normals[0].Z,
            cache.Normals[1].X,cache.Normals[1].Y,cache.Normals[1].Z];
        Assert.Equal(words,actual.Select(BitConverter.SingleToUInt32Bits));
        Assert.Equal(EnbRetailTerrainPrelight.Decode(bytes[..^19],count).NormalIndices,cache.NormalIndices);
    }

    [Fact]
    public void DecorationCacheKeepsQuaternionPositionAndScaleInTheirAuthoredFields()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
        {
            writer.Write(0xDEC0DEC0u); writer.Write(1u);
            var name = new byte[32]; Encoding.ASCII.GetBytes("TREE").CopyTo(name, 0); writer.Write(name);
            writer.Write(1u); writer.Write(0u);
            foreach (float value in new[] {0f,0f,0f,1f,12f,-24f,2.5f}) writer.Write(value);
        }
        var data = EnbRetailTerrainDecorations.Decode(stream.ToArray());
        Assert.Equal("TREE", data.Prototypes[0]);
        Assert.Equal(new Vector2(12,-24), data.Placements[0].Position);
        Assert.Equal(2.5f, data.Placements[0].Scale);
        Assert.Equal(Quaternion.Identity, data.Placements[0].Rotation);
        Assert.Empty(EnbRetailTerrainDecorations.Decode(stream.ToArray()[..^1]).Placements);
    }

    [Fact]
    public void DecorationCacheRetainsCompleteRecordsAndRegeneratesOnlyEmptyCollections()
    {
        using var stream=new MemoryStream();
        using(var writer=new BinaryWriter(stream,Encoding.ASCII,true))
        {
            writer.Write(0xDEC0DEC0u);writer.Write(2u);
            foreach(string name in new[]{"TREE","BUSH"})
            {var bytes=new byte[32];Encoding.ASCII.GetBytes(name).CopyTo(bytes,0);writer.Write(bytes);}
            writer.Write(3u);
            foreach(uint flags in new[]{0x10001u,2u,255u})
            {
                writer.Write(flags);
                foreach(uint word in new[]{0u,0u,0u,0x3f800000u,0x80000000u,0x7fc12345u,0x7f800000u})writer.Write(word);
            }
        }
        byte[] cache=stream.ToArray();
        int generationCalls=0;
        EnbRetailTerrainDecorations Generate(){generationCalls++;return new(["GENERATED"],[]);}
        for(int length=0;length<=cache.Length;length++)
        {
            byte[] prefix=cache[..length];
            var decoded=EnbRetailTerrainDecorations.Decode(prefix);
            int completed=Math.Max(0,(length-76)/32);
            Assert.Equal(completed,decoded.Placements.Length);
            int before=generationCalls;
            var loaded=EnbRetailTerrainDecorations.Load(1,_=>prefix,Generate);
            Assert.Equal(before+(completed==0?1:0),generationCalls);
            if(completed>0)Assert.Equal(completed,loaded.Placements.Length);
        }
        var full=EnbRetailTerrainDecorations.Decode([..cache,9,8,7]);
        Assert.Equal(new[]{1,0,0},full.Placements.Select(p=>p.Prototype));
        Assert.All(full.Placements,p=>
        {
            Assert.Equal(0x80000000u,BitConverter.SingleToUInt32Bits(p.Position.X));
            Assert.Equal(0x7fc12345u,BitConverter.SingleToUInt32Bits(p.Position.Y));
            Assert.Equal(float.PositiveInfinity,p.Scale);
        });
        cache[0]=0;
        Assert.Empty(EnbRetailTerrainDecorations.Decode(cache).Prototypes);
    }

    [Fact]
    public void GroundPlacementSamplesTheAuthoredTriangleRatherThanBilinearHeight()
    {
        var heights = new ushort[16]; heights[1] = 10; heights[4] = 20; heights[5] = 40;
        var terrain = Load(false).Copy().SetHeights(heights).SetVerticalScale(1);
        Assert.Equal(15f, EnbRetailTerrainSampler.For(terrain).Height(new(-17.5f,-15)));
        Assert.Equal(12.5f, EnbRetailTerrainSampler.For(terrain).Height(new(-15f,-17.5f)));
        Assert.True(EnbRetailTerrainSampler.For(terrain).Normal(new(-17.5f,-15)).Z > 0);
    }

    [Fact]
    public void SeaUsesAuthoredLevelWithTwoSidedFog()
    {
        var terrain = Load(false).Copy().SetSeaLevel(50);
        var swatch = EnbRetailTerrainMesh.Build(terrain, new("color","test",0,1),new("detail","test",1,1))[0];
        var water = EnbRetailTerrainWater.Build(terrain, swatch,[]);
        Assert.All(water.Positions, p => Assert.Equal(50, p.Z));
        Assert.NotEmpty(water.Indices);
        Assert.All(water.MaterialPasses, pass => {
            Assert.Equal(0u, pass.SingleBindingSemantics!.Value.FixedFunction.PackedRetailState & 0x80000u);
            Assert.Equal(0x100u, pass.SingleBindingSemantics.Value.FixedFunction.PackedRetailState & 0x300u);
            Assert.All(pass.TriangleBindings, binding => Assert.InRange(binding.MaterialVertexIndex,0,water.Positions.Count-1));
        });
    }

    [Fact]
    public void GeneratedSeaDoesNotInheritSwatchMeshAdmissionOrLoadedBounds()
    {
        var terrain=Load(false).Copy().SetSeaLevel(50);
        var swatch=EnbRetailTerrainMesh.Build(terrain,new("color","test",0,1),new("detail","test",1,1))[0];
        var loaded=new EnbCurrentRetailMeshGeometryState([Vector3.Zero],[Vector3.UnitZ],[],[],
            new(-1),new(1),Vector3.Zero,1,0);
        swatch.SetCurrentRetailUsesMeshRenderer(true).SetCurrentRetailLoadedGeometry(loaded);
        var water=EnbRetailTerrainWater.Build(terrain,swatch,[new(new(100,200,0),0,0,true)]);
        Assert.False(water.CurrentRetailUsesMeshRenderer);
        Assert.Null(water.CurrentRetailLoadedGeometry);
        Assert.True(swatch.CurrentRetailUsesMeshRenderer);
        Assert.Same(loaded,swatch.CurrentRetailLoadedGeometry);
        Assert.Contains(new Vector3(100,200,50),water.Positions);
        Assert.True(water.BoundingBoxMax.Y>=200);
        Assert.Equal(swatch.MaterialPasses.Count,water.MaterialPasses.Count);
    }

    [Fact]
    public void DryTerrainStillHasTheAuthoredOuterSea()
    {
        var terrain = Load(false).Copy().SetSeaLevel(-1);
        var swatch = EnbRetailTerrainMesh.Build(terrain, new("color","test",0,1),new("detail","test",1,1))[0];
        var water = EnbRetailTerrainWater.Build(terrain, swatch,[]);
        Assert.NotEmpty(water.Indices);
        Assert.All(water.Positions,p=>Assert.Equal(-1,p.Z));
        Assert.True(water.BoundingBoxMin.X < -terrain.HorizontalScale/2);
        Assert.True(water.BoundingBoxMax.X > terrain.HorizontalScale/2);
    }
}
