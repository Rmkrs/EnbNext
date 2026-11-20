using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailProjectedDecalTests
{
    private static readonly EnbProjectedDecalDefinition Definition = new(2,"bullet01.tga",true);
    private static EnbW3dDrawTask Plane(bool skin = false)
    {
        Vector3[] points = [new(1,-2,-2), new(1,2,-2), new(1,2,2), new(1,-2,2)];
        var mesh = new EnbW3dRenderMesh(0,"test","test",3,points,Enumerable.Repeat(Vector3.UnitX,4).ToArray(),[],
            [0,1,2,0,2,3],[],[],[]);
        return new(default,1,1,1,Matrix4x4.Identity,new(Vector3.Zero,3),mesh,
            skin ? new(points.ToImmutableArray(),mesh.Normals.ToImmutableArray()) : null,
            new(null!,null),EnbW3dLightEnvironment.BlackNoLights,0,null,-1,"test");
    }

    [Fact]
    public void AuthoredCatalogStopsAtFirstGapAndKeepsLightingAndDefaults()
    {
        var catalog = new EnbProjectedDecalCatalog(EnbRetailIniDocument.Parse("[Decal000]\nBaseSize=3.5\nTexture=x.tga\nLighting=TRUE\n[Decal001]\n[Decal003]\nBaseSize=9"));
        Assert.Equal(2,catalog.Entries.Count); Assert.Equal(new(3.5f,"x.tga",true),catalog.Entries[0]);
        Assert.Equal(new(0,"",false),catalog.Entries[1]);
    }

    [Fact]
    public void ProjectionKeepsNativeBasisDepthRangeAndTextureWidthCorrection()
    {
        var projection = EnbRetailDecalProjection.Create(Vector3.UnitX,1,Definition,new(Vector3.Zero,1));
        Assert.Equal(new Vector3(2,0,0),projection.Origin); Assert.Equal(new Vector3(.75f,0,0),projection.Center);
        Assert.Equal(new Vector3(1,1,.75f),projection.Extent);
        Assert.Equal(new Vector2(63f/128,63f/128),projection.Uv(Vector3.UnitX,64));
        var tilted = EnbRetailDecalProjection.Create(new(.6f,0,.8f),1,Definition,new(Vector3.Zero,1));
        Assert.Equal(new Vector3(0,.6f,0),tilted.X);
        Assert.Equal(new Vector3(-.6f*.8f,0,.6f*.6f),tilted.Y); //No basis normalization.
    }

    [Fact]
    public void RigidMarksClipButSkinnedMarksRetainOriginalTrianglesAndUvs()
    {
        var projection = EnbRetailDecalProjection.Create(Vector3.UnitX,1,Definition,new(Vector3.Zero,1));
        var rigid = EnbRetailProjectedDecal.Build(projection,Plane(),64);
        Assert.NotEmpty(rigid.Indices);
        Assert.All(rigid.Positions,p => { Assert.InRange(p.Y,-1,1); Assert.InRange(p.Z,-1,1); Assert.Equal(1,p.X); });
        Assert.All(rigid.Uvs,uv => { Assert.InRange(uv.X,0,63f/64); Assert.InRange(uv.Y,0,63f/64); });
        Assert.Empty(rigid.SourceVertices);
        var skin = EnbRetailProjectedDecal.Build(projection,Plane(true),64);
        Assert.Equal(new[]{0,1,2,0,2,3},skin.SourceVertices); Assert.Contains(skin.Uvs,uv => uv.X < 0 || uv.X > 1);
        var reversed = (Plane()).ForkTask().SetMesh((Plane().Mesh).Copy().SetIndices([2,1,0]));
        Assert.Empty(EnbRetailProjectedDecal.Build(projection,reversed,64).Indices);
        reversed = (reversed).ForkTask().SetDeformedGeometry(Plane(true).DeformedGeometry);
        Assert.Equal(3,EnbRetailProjectedDecal.Build(projection,reversed,64).Indices.Length);
    }

    [Fact]
    public void GeneratedMarkHasCompleteIndexedSubmissionMetadata()
    {
        var projection=EnbRetailDecalProjection.Create(Vector3.UnitX,1,Definition,new(Vector3.Zero,1));
        var mesh=EnbRetailProjectedDecalMaterial.Build(EnbRetailProjectedDecal.Build(projection,Plane(),64),Definition,"mark");
        var packet=(Plane()).ForkTask().SetMesh(mesh).SetMaterial(new(mesh.MaterialPasses[0],mesh.MaterialPasses[0].SingleBindingSemantics));
        Assert.True(EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var plan,out var error),error);
        Assert.Equal(mesh.Indices.Count,plan.Runs.Sum(r=>r.IndexCount));
    }

    [Fact]
    public void ContactToleranceRejectsTouchingAndRigidMotionRetainsProjectionCoordinates()
    {
        var projection = EnbRetailDecalProjection.Create(Vector3.UnitX,1,Definition,new(Vector3.Zero,1));
        Assert.False(EnbRetailProjectedDecal.Intersects(projection,new(0,-.5f,-.5f),new(0,.5f,-.5f),new(0,0,.5f)));
        Assert.True(EnbRetailProjectedDecal.Intersects(projection,new(.01f,-.5f,-.5f),new(.01f,.5f,-.5f),new(.01f,0,.5f)));
        var transform = Matrix4x4.CreateRotationZ(MathF.PI/2)*Matrix4x4.CreateTranslation(10,20,30);
        var moved = projection with { X=Vector3.TransformNormal(projection.X,transform),Y=Vector3.TransformNormal(projection.Y,transform),
            Z=Vector3.TransformNormal(projection.Z,transform),Origin=Vector3.Transform(projection.Origin,transform),Center=Vector3.Transform(projection.Center,transform) };
        var baseline = EnbRetailProjectedDecal.Build(projection,Plane(),64);
        var result = EnbRetailProjectedDecal.Build(moved,(Plane()).ForkTask().SetWorldTransform(transform),64);
        Assert.Equal(baseline.Indices,result.Indices);
        for(int i=0;i<baseline.Uvs.Length;i++)Assert.True(Vector2.Distance(baseline.Uvs[i],result.Uvs[i])<.00001f);
    }

    [Fact]
    public void UpdatesReplaceOnlyDirtySlotsAndClearOtherPoolsWithoutReplayingOldValues()
    {
        var updates=new EnbRetailDamageDecalUpdates();var calls=new List<string>();
        EnbSceneDamageDecalPoolSnapshot Pool(ulong revision,params EnbSceneDamageDecalSnapshot[] slots)=>new(2,revision,[..slots]);
        EnbSceneDamageDecalSnapshot Slot(ulong revision,int asset=0)=>new(2,revision,Vector3.UnitX,asset,1);
        void Apply(params EnbSceneDamageDecalPoolSnapshot[] pools)=>updates.Apply(pools,p=>calls.Add($"clear{p}"),(p,s,v)=>calls.Add($"{p}/{s}/{v.AssetId}"));
        Apply(Pool(1,Slot(1)),Pool(1,Slot(1)),Pool(0));
        Assert.Equal(new[]{"0/0/0","1/0/0","clear2"},calls);calls.Clear();
        Apply(Pool(1,Slot(1)),Pool(1,Slot(1)),Pool(0));Assert.Empty(calls);
        Apply(Pool(2,Slot(1),Slot(1)),Pool(1,Slot(1)),Pool(0));
        Assert.Equal(new[]{"0/1/0","clear1","clear2"},calls);calls.Clear();
        Apply(Pool(3,Slot(2,-1),Slot(1)),Pool(1,Slot(1)),Pool(0));Assert.Equal("0/0/-1",calls[0]);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(25)]
    public void ActualAuxDeltaPublishesDamageMarkAndRetailConstructorDefaults(byte type)
    {
        var schema=new Net7AuxDataSchemaCatalog().CreateObjectSchema(type)!;
        var array=schema.Root.Children.Single(p=>p.Path.EndsWith(".DamageSpot",StringComparison.Ordinal));
        var world=new Net7WorldState();world.MaterializeCreate(new Net7CreateUpdate(23,1,10,type,0,0,0));
        using var delta=new MemoryStream();using(var writer=new BinaryWriter(delta,System.Text.Encoding.UTF8,true))
        {writer.Write((byte)0);writer.Write(1u);writer.Write((uint)array.Children[2].Id);writer.Write(.6f);writer.Write(0f);writer.Write(.8f);writer.Write(2);writer.Write(1.5f);writer.Write(0u);writer.Write(0u);}
        using var payload=new MemoryStream();using(var writer=new BinaryWriter(payload,System.Text.Encoding.UTF8,true))
        {writer.Write(23u);writer.Write((ushort)delta.Length);writer.Write(delta.ToArray());}
        world.Apply(Packet.ForOpcode(0x001B,payload.ToArray()),0);
        var pools=new EnbPresentationSnapshotBuilder().Build(world,null,0).Objects[0].DamageDecalPools;
        Assert.Equal(15,pools[0].Slots.Length);Assert.Equal(10,pools[1].Slots.Length);Assert.Equal(5,pools[2].Slots.Length);
        Assert.Equal(new Vector3(.6f,0,.8f),pools[0].Slots[2].Direction); Assert.Equal(2,pools[0].Slots[2].AssetId);
        Assert.Equal(1.5f,pools[0].Slots[2].Scale);Assert.True(pools[0].Slots[2].Revision>0);
        Assert.Equal(-1,pools[0].Slots[0].AssetId);Assert.Equal(Vector3.UnitX,pools[0].Slots[0].Direction);
    }
}
