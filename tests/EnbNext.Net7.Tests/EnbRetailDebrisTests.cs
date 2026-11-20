using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailDebrisTests
{
    private static EnbRetailDebrisDefinition Definition(int primitive=0)=>new(40,2,0,250,new(1,.5f,.25f,1),true,.9f,.1f,primitive,1,"",30);
    private static EnbResolvedRenderCamera Camera(Vector3 position=default,float near=1)
    {
        Assert.True(EnbResolvedRenderCamera.TryCreate(position,Quaternion.Identity,new(800,600,1),Math.PI/2,new(near,10000),out var result));return result;
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void BorrowedFramesMatchDetachedGeometryAndRetainQueuedRanges(int primitive)
    {
        var field=new EnbRetailDebrisRuntime(Definition(primitive));
        var detached=field.BuildPacket(Camera(),null,0,0)!;
        var arena=new EnbW3dTransientGeometryArena();
        var frame=arena.BeginFrame();
        var first=field.BuildPacket(Camera(),null,0,0,frame)!;
        Assert.Equal(detached.Mesh.Positions.ToArray(),first.Mesh.Positions.ToArray());
        Assert.Equal(detached.CurrentRetailViewPositions!.ToArray(),first.CurrentRetailViewPositions!.ToArray());
        Assert.Equal(detached.Mesh.EffectiveColor1!.ToArray(),first.Mesh.EffectiveColor1!.ToArray());
        var saved=first.Mesh.Positions.ToArray();
        var moved=field.BuildPacket(Camera(new(20,10,30)),null,1,0,frame)!;
        Assert.Equal(saved,first.Mesh.Positions.ToArray());
        Assert.NotEqual(saved,moved.Mesh.Positions.ToArray());
        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(()=>first.Mesh.Positions[0]);
        long arrays=arena.ArrayAllocations;
        using var next=arena.BeginFrame();
        var reused=field.BuildPacket(Camera(),null,2,0,next)!;
        Assert.Equal(saved,reused.Mesh.Positions.ToArray());
        Assert.Equal(arrays,arena.ArrayAllocations);
        Assert.Equal(saved,detached.Mesh.Positions.ToArray());
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void OwnedPreparationReusesDescriptorsAndKeepsQueuedViewsIndependent(int primitive)
    {
        var definition=Definition(primitive) with {Additive=false};
        var field=new EnbRetailDebrisRuntime(definition);
        var texture=new EnbResolvedTextureResource("fixture.tga",EnbTextureResolutionStatus.Decoded);
        var taskArena=new EnbW3dDrawTaskArena();var nestedArena=new EnbW3dDrawTaskArena();
        var geometry=new EnbW3dTransientGeometryArena();
        EnbW3dDrawTask first;
        using(taskArena.Enter()) using(var frame=geometry.BeginFrame())
        {
            first=field.BuildPacket(Camera(),texture,0,0,frame)!;
            var expected=new EnbRetailDebrisRuntime(definition).BuildPacket(Camera(),texture,0,0)!;
            EqualGeometry(expected,first);
            var positions=first.Mesh.Positions.ToArray();
            var coordinates=first.Material.Source.TextureStages[0].EffectiveRuntimeTexCoords.ToArray();
            var sibling=field.BuildPacket(Camera(new(20,10,30)),texture,1,0,frame)!;
            Assert.NotSame(first.Mesh,sibling.Mesh);Assert.NotSame(first.Material.Source,sibling.Material.Source);
            using(nestedArena.Enter()) using(var nested=geometry.BeginFrame())
            {
                var inner=field.BuildPacket(Camera(new(40,10,20)),texture,2,0,nested)!;
                Assert.NotSame(first.Mesh,inner.Mesh);
            }
            Assert.Equal(positions,first.Mesh.Positions);
            Assert.Equal(coordinates,first.Material.Source.TextureStages[0].EffectiveRuntimeTexCoords);
        }
        var mesh=first.Mesh;var material=first.Material.Source;
        using(taskArena.Enter()) using(var frame=geometry.BeginFrame())
        {
            var next=field.BuildPacket(Camera(),texture,3,0,frame)!;
            Assert.Same(first,next);Assert.Same(mesh,next.Mesh);Assert.Same(material,next.Material.Source);
            EqualGeometry(new EnbRetailDebrisRuntime(definition).BuildPacket(Camera(),texture,3,0)!,next);
        }
        field.BlendMode=0;field.Amplitude=.25f;
        using(taskArena.Enter()) using(var frame=geometry.BeginFrame())
        {
            var changed=field.BuildPacket(Camera(new(20,10,30)),texture,4,0,frame)!;
            Assert.NotSame(material,changed.Material.Source);
            EqualGeometry(new EnbRetailDebrisRuntime(definition){BlendMode=0,Amplitude=.25f}.BuildPacket(Camera(new(20,10,30)),texture,4,0)!,changed);
        }
        static void EqualGeometry(EnbW3dDrawTask expected,EnbW3dDrawTask actual)
        {
            Assert.Equal(expected.OrderKey,actual.OrderKey);Assert.Equal(expected.Bounds,actual.Bounds);
            Assert.Equal(expected.Mesh.Positions.ToArray(),actual.Mesh.Positions.ToArray());
            Assert.Equal(expected.Mesh.Normals.ToArray(),actual.Mesh.Normals.ToArray());
            Assert.Equal(expected.Mesh.Indices.ToArray(),actual.Mesh.Indices.ToArray());
            Assert.Equal(expected.CurrentRetailViewPositions!.ToArray(),actual.CurrentRetailViewPositions!.ToArray());
            Assert.Equal(expected.Mesh.EffectiveColor1!.ToArray(),actual.Mesh.EffectiveColor1!.ToArray());
            Assert.Equal(expected.Material.Source.TextureStages[0].EffectiveRuntimeTexCoords.ToArray(),actual.Material.Source.TextureStages[0].EffectiveRuntimeTexCoords.ToArray());
            Assert.Equal(expected.Material.SingleBindingSemantics,actual.Material.SingleBindingSemantics);
        }
    }

    [Fact] public void CatalogUsesConsecutiveExactSectionsAndNativeDefaultsAndClamps()
    {
        var d=EnbRetailDebrisCatalog.Read(EnbRetailIniDocument.Parse("[Debris00]\nMinDist=-2\nMaxDist=0\nRowsColumnsLog2=-1\nFadeIn=0\nFadeOut=2\n[Debris01]\nMinDist=10\nMaxDist=2\nPrimitive=3\nBlurTime=50;\n[Debris03]\nSize=20"));
        Assert.Equal(2,d.Count);Assert.Equal(200,d[0].Spacing);Assert.Equal(.0001f,d[0].Size);Assert.Equal(0,d[0].MinimumDistance);
        Assert.Equal(1,d[0].MaximumDistance);Assert.Equal(4,d[0].RowsColumnsLog2);Assert.Equal(.995f,d[0].FadeIn);Assert.Equal(.99f,d[0].FadeOut);
        Assert.Equal(11,d[1].MaximumDistance);Assert.Equal(50,d[1].BlurTime);Assert.Equal(3,d[1].Primitive);
    }
    [Fact] public void OffsetDeltaRetainsNativeZeroOriginRule()
    {
        var field=new EnbRetailDebrisRuntime(Definition(2));field.SetOffset(new(10,0,0));Assert.Equal(Vector3.Zero,field.OffsetDelta);
        field.SetOffset(new(15,2,0));Assert.Equal(new(-5,-2,0),field.OffsetDelta);
        field.SetOffset(Vector3.Zero);field.SetOffset(new(2,0,0));Assert.Equal(new(15,2,0),field.OffsetDelta);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(4)]
    public void PointAtlasUsesMaskedSelectorAndAuthoredGrid(int log2)
    {
        var texture=new EnbResolvedTextureResource("fixture.tga",EnbTextureResolutionStatus.Decoded);
        foreach(int primitive in new[]{0,1})
        {
            var field=new EnbRetailDebrisRuntime(Definition(primitive) with {RowsColumnsLog2=log2});
            var sample=field.Sample(Camera())[0];var packet=field.BuildPacket(Camera(),texture,0,0)!;
            var uv=packet.Material.Source.TextureStages[0].RuntimeTexCoords;
            int side=1<<log2,cell=sample.Selector%(side*side),count=primitive==0?3:4;
            Vector2[] shape=primitive==0?[new(.5f,0),new(0,.866f),new(1,.866f)]:[new(0,0),new(0,1),new(1,1),new(1,0)];
            for(int i=0;i<count;i++)Assert.Equal((shape[i]+new Vector2(cell%side,cell/side))/side,uv[i]);
        }
    }
    [Fact] public void NearClipRaisesRetainedDistancesAndDoesNotRestoreThem()
    {
        var field=new EnbRetailDebrisRuntime(Definition());field.Sample(Camera(near:200));
        Assert.True(field.MinimumDistance>250);Assert.Equal(field.MinimumDistance*1.1f,field.MaximumDistance);
        float min=field.MinimumDistance;field.Sample(Camera());Assert.Equal(min,field.MinimumDistance);
    }
    [Theory] [InlineData(0,1,3)] [InlineData(1,2,4)] [InlineData(2,4,4)] [InlineData(3,8,6)]
    public void AllVariantsSubmitExactTopologyAndBudget(int primitive,int cost,int vertices)
    {
        var field=new EnbRetailDebrisRuntime(Definition(primitive));field.SetOffset(Vector3.One);field.SetOffset(new(10,3,2));
        var packet=field.BuildPacket(Camera(),null,100,primitive)!;Assert.NotNull(packet);
        Assert.Equal(field.LastCount*cost,field.PrimitiveCost);Assert.Equal(field.PrimitiveCost*3,packet.Mesh.Indices.Count);
        Assert.Equal(field.LastCount*vertices,packet.Mesh.Positions.Count);Assert.Equal(packet.Mesh.Positions.Count,packet.CurrentRetailViewPositions!.Count);
        Assert.Equal(primitive>=2,(packet.Material.SingleBindingSemantics!.Value.FixedFunction.PackedRetailState&0x80000)!=0);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ReusedTopologyPreservesFreshGeometryAndInvalidatesOnMaterialChange(int primitive)
    {
        var definition=Definition(primitive) with {Additive=false};
        var field=new EnbRetailDebrisRuntime(definition);
        var first=field.BuildPacket(Camera(),null,0,0)!;
        var second=field.BuildPacket(Camera(),null,1,0)!;
        Assert.Same(first.Mesh.Indices,second.Mesh.Indices);
        Assert.Same(first.Material.Source.TriangleBindings,second.Material.Source.TriangleBindings);
        Assert.NotSame(first.Mesh.Positions,second.Mesh.Positions);
        var cache=new EnbW3dIndexedSubmissionCache();cache.BeginFrame();
        Assert.True(cache.TryCompile(first,out var original,out var error),error);
        Assert.True(cache.TryCompile(second,out var updated,out error),error);
        Assert.Equal(1,cache.HitCount);Assert.Equal(1,cache.MissCount);
        Assert.Same(second,updated.Packet);
        Assert.False(EnbW3dIndexedSubmissionCompiler.CanReuseGeometry(original,second));
        var invalid=(second).ForkTask().SetMesh((second.Mesh).Copy().SetIndices(new uint[]{uint.MaxValue,0,1}));
        Assert.False(cache.TryCompile(invalid,out _,out _));
        field.BlendMode=0;
        var changed=field.BuildPacket(Camera(),null,2,0)!;
        var fresh=new EnbRetailDebrisRuntime(definition){BlendMode=0}.BuildPacket(Camera(),null,2,0)!;
        Assert.NotSame(second.Material.Source.TriangleBindings,changed.Material.Source.TriangleBindings);
        Assert.True(cache.TryCompile(changed,out _,out error),error);
        Assert.Equal(3,cache.MissCount);
        Assert.Equal(fresh.Material.SingleBindingSemantics,changed.Material.SingleBindingSemantics);
        Assert.Equal(fresh.Mesh.Positions,changed.Mesh.Positions);
        Assert.Equal(fresh.Mesh.Indices,changed.Mesh.Indices);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void SamplingScratchDoesNotChangePublishedFramesAndFadeTracksLocalChanges(int primitive)
    {
        var field=new EnbRetailDebrisRuntime(Definition(primitive));
        var first=field.BuildPacket(Camera(),null,0,0)!;
        var positions=first.DeformedGeometry!.Positions.ToArray();
        var normals=first.DeformedGeometry.Normals.ToArray();
        var samples=field.Sample(Camera());var sampleCopy=samples.ToArray();
        Assert.Same(first.Mesh.Positions,System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(Assert.IsType<System.Collections.Immutable.ImmutableArray<Vector3>>(first.DeformedGeometry.Positions)));
        Assert.Same(first.Mesh.Normals,System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(Assert.IsType<System.Collections.Immutable.ImmutableArray<Vector3>>(first.DeformedGeometry.Normals)));
        field.Amplitude=.25f;
        var moved=field.BuildPacket(Camera(new(20,10,30)),null,1,0)!;
        var fresh=new EnbRetailDebrisRuntime(Definition(primitive)){Amplitude=.25f}.BuildPacket(Camera(new(20,10,30)),null,1,0)!;
        Assert.Equal(fresh.Mesh.EffectiveColor1,moved.Mesh.EffectiveColor1);
        Assert.Equal(positions,first.DeformedGeometry.Positions);
        Assert.Equal(normals,first.DeformedGeometry.Normals);
        Assert.Equal(sampleCopy,samples);
    }
    [Fact] public void FieldIsStableAcrossRedrawAndMovesWithCameraWithoutRandomConsumption()
    {
        var field=new EnbRetailDebrisRuntime(Definition());var first=field.Sample(Camera()).ToArray();
        Assert.NotEmpty(first);Assert.Equal(first,field.Sample(Camera()).ToArray());
        var moved=field.Sample(Camera(new(1,0,0))).ToArray();Assert.False(first.SequenceEqual(moved));
        Assert.Equal(first,field.Sample(Camera()).ToArray());
        Assert.All(first,s=>Assert.True(s.ViewPosition.LengthSquared()<250*250));
    }
    [Fact] public void DenseFieldStopsAtNativeTenThousandCapacity()
    {
        var field=new EnbRetailDebrisRuntime(Definition() with {Spacing=5});field.Sample(Camera());Assert.Equal(10000,field.LastCount);
    }
    [Theory] [InlineData(true,0,0x94033u)] [InlineData(false,0,0x980b3u)] [InlineData(false,1,0xd401bu)] [InlineData(false,2,0xd80bbu)]
    public void ShaderAndFadeBranchesFollowNativeMaterial(bool additive,int mode,uint shader)
    {
        var field=new EnbRetailDebrisRuntime(Definition() with {Additive=additive}){BlendMode=mode,Amplitude=.4f};
        var packet=field.BuildPacket(Camera(),null,0,0)!;
        Assert.Equal(additive?10:mode==0?20:0,packet.OrderKey.StaticSort.RuntimeLevel);
        Assert.Equal(shader,field.Shader);var samples=field.Sample(Camera());var fade=field.FadeTable();Assert.Equal(0,fade[0]);
        Assert.All(samples,s=>{if(additive||mode!=0)Assert.Equal(1,s.Color.W);else Assert.Equal(1,s.Color.X);});
        Assert.InRange(fade.Max(),.399f,.401f);Assert.True(fade[511]<.02f);
    }
}

