using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbG33NPatchTests
{
    [Theory]
    [InlineData(0u, 1)] [InlineData(1u, 1)] [InlineData(4u, 4)] [InlineData(8u, 8)]
    [InlineData(9u, 8)] [InlineData(uint.MaxValue, 8)]
    public void NativeSegmentClamp(uint requested, int expected)
        => Assert.Equal(expected, EnbW3dNPatchCompiler.NormalizeSegments(requested));

    [Fact]
    public void HeaderVersionAndCapabilityControlAdmission()
    {
        Assert.False(EnbW3dNPatchSemantics.FromAuthoredAttributes(0x20000, 0x40000).Requested);
        Assert.True(EnbW3dNPatchSemantics.FromAuthoredAttributes(0x20000, 0x40001).Requested);
        Assert.False(EnbW3dNPatchSemantics.FromAuthoredAttributes(0x60000, 0x40001).Requested);
        var packet = Source().Packet;
        Assert.True(EnbW3dNPatchCompiler.IsEligible(packet, true, 4));
        Assert.False(EnbW3dNPatchCompiler.IsEligible(packet, false, 4));
        Assert.False(EnbW3dNPatchCompiler.IsEligible(packet, true, 1));
        Assert.False(EnbW3dNPatchCompiler.IsEligible((packet).ForkTask().SetCurrentRetailAdditionalMeshPassFlags(0), true, 4));
        Assert.False(EnbW3dNPatchCompiler.IsEligible((packet).ForkTask().SetMesh((packet.Mesh).Copy().SetCurrentRetailUsesMeshRenderer(false)), true, 4));
    }

    [Theory]
    [InlineData(2)] [InlineData(4)] [InlineData(8)]
    public void PlanarGeometryPreservesWindingAreaCornersAndLinearStreams(int segments)
    {
        var source = Source();
        var result = EnbW3dNPatchCompiler.Compile(source, segments);
        Assert.Equal(segments * segments * 3, result.Indices.Length);
        Assert.Equal((segments + 1) * (segments + 2) / 2, result.Positions.Length);
        float area = 0;
        for (int i = 0; i < result.Indices.Length; i += 3)
        {
            Vector3 a = result.Positions[(int)result.Indices[i]], b = result.Positions[(int)result.Indices[i + 1]], c = result.Positions[(int)result.Indices[i + 2]];
            float cross = Vector3.Cross(b - a, c - a).Z;
            Assert.True(cross > 0); area += cross / 2;
        }
        Assert.Equal(.5f, area, 5);
        var uv = result.Interpolate(new Vector2[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY });
        for (int i = 0; i < result.Positions.Length; i++)
        {
            Assert.True(Vector2.Distance(uv[i], new(result.Positions[i].X, result.Positions[i].Y)) < 1e-6f);
            Assert.Equal(Vector3.UnitZ, result.Normals[i]);
        }
        Assert.Contains(Vector3.Zero, result.Positions); Assert.Contains(Vector3.UnitX, result.Positions); Assert.Contains(Vector3.UnitY, result.Positions);
    }

    [Fact]
    public void CurvedPositionAndLinearNormalHaveIndependentAnalyticValues()
    {
        // Edge tangent planes: n0=(0,0,1), n1=(1,0,0).
        // Cubic Bezier edge controls are 0, 1/3, 1, 1; midpoint x=5/8.
        Assert.Equal(new Vector3(.625f, 0, 0), EnbW3dNPatchCompiler.EvaluatePosition(Vector3.Zero,
            Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitZ, new(.5f, .5f, 0)));
        var source = Source();
        source = source with { Packet = (source.Packet).ForkTask().SetMesh((source.Packet.Mesh).Copy().SetNormals([Vector3.UnitZ, Vector3.UnitX, Vector3.UnitZ])) };
        var result = EnbW3dNPatchCompiler.Compile(source, 4);
        int midpoint = result.Samples.IndexOf(new(0, 1, 2, new(.5f, .5f, 0)));
        Assert.Equal(new Vector3(.5f, 0, .5f), result.Normals[midpoint]);
        Assert.Equal(new Vector3(.625f, 0, 0), result.Positions[midpoint]);
    }

    [Fact]
    public void SeamTrianglesRetainSourceMaterialAndSharedIndexEdgesDoNotBridge()
    {
        Vector3[] p = [new(0,0,0), new(2,0,0), new(0,2,0), new(2,0,0), new(0,0,0), new(2,-2,0)];
        Assert.Equal(new[] { new EnbW3dNPatchSeam(1,4,1,0), new EnbW3dNPatchSeam(0,3,1,4) },
            EnbW3dNPatchCompiler.CompileSeams(p, [0,1,2,3,4,5]));
        Assert.Empty(EnbW3dNPatchCompiler.CompileSeams(p, [0,1,2,1,0,5]));
        var source = Source();
        source = source with { Packet = (source.Packet).ForkTask().SetMesh((source.Packet.Mesh).Copy().SetPositions(p).SetNormals(Enumerable.Repeat(Vector3.UnitZ,6).ToArray()).SetIndices([0,1,2,3,4,5])),
            Runs = [new(0,1,0,3,0,2,3,default), new(1,1,3,3,3,5,3,default)] };
        var geometry = EnbW3dNPatchCompiler.Compile(source,4);
        Assert.Equal(6,EnbW3dNPatchCompiler.PreparedIndexCount(source.Packet.Mesh,false,4));
        Assert.Equal(12,EnbW3dNPatchCompiler.PreparedIndexCount(source.Packet.Mesh,true,4));
        Assert.Equal(192,geometry.Indices.Length);
        Assert.Equal(new[] {32,32},geometry.Runs.Select(r=>r.TriangleCount));
        Assert.Contains(new EnbW3dNPatchSample(3,1,4,Vector3.UnitX),geometry.Samples.Take(30));
        Assert.Contains(new EnbW3dNPatchSample(4,1,0,Vector3.UnitX),geometry.Samples.Skip(30));
    }

    [Fact]
    public void EnabledAndOrdinaryDescriptorsDoNotMergeAndOffDescriptorsStillShare()
    {
        var source=Source();
        var pass=new EnbW3dRenderMaterialPass(0,[],default,[],default,[],[],[],[],[],null);
        var binding=new EnbW3dEffectiveSubmissionBinding(null,null,[],default,true,true);
        source=source with {Packet=(source.Packet).ForkTask().SetMaterial(new(pass,null)),Runs=[source.Runs[0] with {Binding=binding}]};
        var registry=new EnbCurrentRetailDescriptorBatchRegistry();object container=new();
        object Batch(bool enabled)
        {
            Assert.True(EnbCurrentRetailMeshSubmissionBatches.TryCompile(source,new object(),container,registry,
                new Dictionary<EnbW3dRenderVertexMaterial,EnbW3dSharedMaterialMappers>(),_=>null,out var batches,out var error,enabled),error);
            return Assert.Single(batches).Identity;
        }
        var ordinary=Batch(false);var patch=Batch(true);
        Assert.NotSame(ordinary,patch);Assert.Same(ordinary,Batch(false));Assert.Same(patch,Batch(true));
    }

    [Fact]
    public void MaterialLocalizationInvalidatesCachedDescriptorIdentity()
    {
        var white=new EnbRenderPackedRgb8(255,255,255,0);
        var material=new EnbW3dRenderVertexMaterial(0,"test",0,white,white,white,new(0,0,0,0),1,1,0,null,null);
        var vertex=new EnbW3dRenderVertexMaterialBinding(0,material){CurrentRetailRuntimeCoordinates=new(0,1)};
        var binding=new EnbW3dEffectiveSubmissionBinding(vertex,null,[],default,true,true);
        var source=Source();
        var pass=new EnbW3dRenderMaterialPass(0,[],default,[],default,[],[],[],[],[],null);
        source=source with {Packet=(source.Packet).ForkTask().SetMaterial(new(pass,null)),Runs=[source.Runs[0] with {Binding=binding}]};
        Assert.True(EnbW3dSharedMaterialMappers.TryCreate(material,0,out var mapper,out var diagnostic),diagnostic);
        var mappers=new Dictionary<EnbW3dRenderVertexMaterial,EnbW3dSharedMaterialMappers>{{material,mapper}};
        var registry=new EnbCurrentRetailDescriptorBatchRegistry();object container=new(),model=new();
        object Batch()
        {
            Assert.True(EnbCurrentRetailMeshSubmissionBatches.TryCompile(source,model,container,registry,mappers,
                _=>null,out var batches,out var error),error);
            return Assert.Single(batches).Identity;
        }
        var original=Batch();Assert.Same(original,Batch());
        using var owner=new EnbCurrentRetailMeshModelOwner([(material,null,null)]);
        uint next=42;owner.MarkLoadedMaterialsInstanceLocal(ref next);
        var localized=Batch();Assert.NotSame(original,localized);Assert.Same(localized,Batch());
    }

    private static EnbW3dIndexedSubmissionPlan Source()
    {
        var mesh = new EnbW3dRenderMesh(0,"patch","",1,[Vector3.Zero,Vector3.UnitX,Vector3.UnitY],
            [Vector3.UnitZ,Vector3.UnitZ,Vector3.UnitZ],[],[0,1,2],[],[],[])
            { Attributes = 0x20000000, CurrentRetailUsesMeshRenderer = true };
        var packet = new EnbW3dDrawTask(default,1,1,0,Matrix4x4.Identity,default,mesh,null,null!,null!,0,null,0,"patch");
        return new(packet,EnbW3dSubmissionVertexSource.RigidMesh,EnbW3dIndexedPrimitiveTopology.TriangleList,
            [new(0,1,0,3,0,2,3,default)]);
    }
}
