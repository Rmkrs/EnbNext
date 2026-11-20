using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10StreakTests
{
    private static readonly EnbCurrentRetailParticleScalarProperty Scalar = new(0,[new(0,1)]);
    private static readonly EnbCurrentRetailParticleEmitterDefinition Definition = new(0,"streak","",1,100,0,
        Vector3.Zero,Vector3.Zero,0,0,0,2,new(0,Vector3.Zero),new(0,Vector3.Zero),0,0,
        new(3,0,0,1,0,0,0,1,1,0,0,0,0,0,0,0),4,0,new(Vector3.Zero,[new(0,255,255,255,0)]),
        Scalar,Scalar,Scalar,0,Scalar);
    private static readonly EnbCurrentRetailStreakSample Sample = new(new(0,0,0),new(-5,0,0),2,
        new(0,180,255,255),new(0,0,0,255));

    private static EnbResolvedRenderCamera Camera(Quaternion rotation=default)
    {
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(20,0,0), rotation==default ? Quaternion.Identity : rotation,
            new(800,600,1),Math.PI/3,new(1,10000),out var camera));
        return camera;
    }

    private static bool Build(IReadOnlyList<EnbCurrentRetailStreakSample> samples, EnbResolvedRenderCamera camera,
        out EnbW3dDrawTask? packet, out string? error, EnbCurrentRetailParticleEmitterDefinition? definition=null)
        => EnbCurrentRetailStreakRuntime.TryBuildPacket(new("streak.w3d","fixture.mix",0,1),1,1,1,"streak",0,
            definition??Definition,samples,camera,100,out packet,out error);

    [Fact]
    public void PrismUsesExactRetailTopologyAndIndependentEndpointColors()
    {
        Assert.True(Build([Sample,Sample with { Head=new(10,10,10),Tail=new(8,9,10) }],Camera(),out var packet,out var error),error);
        Assert.Equal(12,packet!.Mesh.Positions.Count);
        Assert.Equal(16,packet.Mesh.TriangleMetadata.Count);
        uint[] expected=[0,1,2,0,3,1,1,3,4,1,4,5,1,5,2,0,2,5,0,5,3,3,5,4];
        Assert.Equal(expected,packet.Mesh.Indices.Take(24));
        Assert.Equal(expected.Select(i=>i+6),packet.Mesh.Indices.Skip(24));
        for (int i=0;i<6;i++) Assert.Equal(i<3 ? Sample.HeadColor : Sample.TailColor,packet.Mesh.EffectiveColor1[i]);
        Assert.Equal(Matrix4x4.Identity,packet.WorldTransform);
        foreach (var p in packet.Mesh.Positions)
            Assert.True(Vector3.Distance(p,packet.Mesh.BoundingSphereCenter)<=packet.Mesh.BoundingRadius+1e-5f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.7f)]
    public void CrossSectionFacesCameraAndDoesNotRotateWithStreakDirection(float angle)
    {
        var camera=Camera(Quaternion.CreateFromAxisAngle(Vector3.UnitZ,angle));
        Assert.True(Build([Sample],camera,out var packet,out var error),error);
        var moved=Sample with { Head=Sample.Head+new Vector3(10,20,30), Tail=Sample.Tail+new Vector3(-20,10,5) };
        Assert.True(Build([moved],camera,out var changed,out error),error);
        for(int i=0;i<3;i++)
        {
            var offset=packet!.Mesh.Positions[i]-Sample.Head;
            Assert.Equal(2,offset.Length(),5);
            Assert.InRange(MathF.Abs(Vector3.Dot(offset,camera.Basis.Forward)),0,1e-5f);
            Assert.True(Vector3.Distance(offset,changed!.Mesh.Positions[i]-moved.Head)<1e-5f);
            Assert.True(Vector3.Distance(offset,packet.Mesh.Positions[i+3]-Sample.Tail)<1e-5f);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeUntexturedMaterialReachesGpuLaneWithoutTextureLookup(bool unusedMapper)
    {
        Assert.True(Build([Sample],Camera(),out var packet,out var error),error);
        Assert.Empty(packet!.Material.Source.TextureStages);
        Assert.Equal(0,packet.Material.SingleBindingSemantics!.Value.PermutationKey.TextureStageCount);
        Assert.True((packet.Material.SingleBindingSemantics.Value.FixedFunction.PackedRetailState & 0x80000)!=0);
        // 009634F0 selects zero RGBA tails for this untextured ONE/ONE state.
        Assert.Equal(0x4020u,packet.Material.SingleBindingSemantics.Value.FixedFunction.PackedRetailState & 0xc0e0u);
        Assert.Equal(10,packet.OrderKey.StaticSort.RuntimeLevel);
        if (unusedMapper)
        {
            var material = packet.Material.Source;
            var semantics = packet.Material.SingleBindingSemantics!.Value;
            semantics = semantics with { Mapper0Program = semantics.Mapper0Program with { Kind = EnbW3dMapperRuntimeProgramKind.Linear } };
            material = (material).Copy().SetSingleBindingSemantics(semantics).SetTriangleBindings(material.TriangleBindings.Select(b => b with { Semantics = semantics }).ToArray());
            packet = (packet).ForkTask().SetMaterial(new(material, semantics));
        }
        Assert.True(EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out error),error);
        Assert.True(EnbW3dProgrammableTextureLaneCompiler.TryCompile(submission,Camera(),
            EnbRenderTextureRuntimeSettings.RetailDefaults,EnbRenderTextureBackendCapabilities.ModernDefault,
            _=>throw new Exception("Untextured streak requested a fabricated texture"),out var lane,out error),error);
        Assert.All(lane.RunStates,r=>Assert.False(r.SamplesTexture0));
        Assert.All(lane.LightingPlan.RunStates,r=>Assert.False(r.Lighting.LightingEnabled));
    }

    [Fact]
    public void EmptyZeroLengthAndZeroSizeHaveDefinedGeometryWithoutNaNs()
    {
        Assert.True(Build([],Camera(),out var empty,out var error),error); Assert.Null(empty);
        Assert.True(Build([Sample with { Tail=Sample.Head,Size=0 }],Camera(),out var packet,out error),error);
        Assert.All(packet!.Mesh.Positions,p=>Assert.Equal(Sample.Head,p));
        Assert.Equal(0,packet.Mesh.BoundingRadius);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1f)]
    public void InvalidSizeFailsWithoutPartialPacket(float size)
    {
        Assert.False(Build([Sample with { Size=size }],Camera(),out var packet,out _)); Assert.Null(packet);
    }

    [Fact]
    public void OtherFamiliesStayClosed()
    {
        Assert.False(Build([Sample],Camera(),out _,out _,Definition.Copy().SetRenderMode(2)));
        Assert.False(Build([Sample],Camera(),out _,out _,Definition.Copy().SetTextureFileName("foo.tga")));
        Assert.False(Build([Sample with { Head=new(float.NaN,0,0) }],Camera(),out _,out _));
    }
}
