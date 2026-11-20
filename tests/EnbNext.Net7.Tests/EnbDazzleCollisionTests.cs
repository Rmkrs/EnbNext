using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbDazzleCollisionTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(0x10000u)]
    [InlineData(0x60000u)]
    public void OcclusionMemoPreservesFractionsAndInvalidatesEveryQueryInput(uint attributes)
    {
        var mesh=new EnbW3dRenderMesh(0,"ray","",3,[new(-2,-2,0),new(2,-2,0),new(0,2,0)],
            [],[],[0,1,2],[],[],[]) {BoundingBoxMin=new(-2,-2,0),BoundingBoxMax=new(2,2,0),
                Attributes=attributes,GeometryVersion=0x40001};
        var cached=new EnbRetailDazzleMeshRay(mesh,1);
        var oracle=new EnbRetailDazzleMeshRay(mesh,1){UseUncachedOcclusionQueries=true};
        void Compare(Matrix4x4 pose,Vector3 start,Vector3 end,float fraction=1,bool inside=false)
        {
            var expected=oracle.CastOcclusionSegment(pose,start,end,fraction,inside);
            var actual=cached.CastOcclusionSegment(pose,start,end,fraction,inside);
            Assert.Equal(BitConverter.SingleToUInt32Bits(expected.Fraction),BitConverter.SingleToUInt32Bits(actual.Fraction));
            Assert.Equal(expected.StartInside,actual.StartInside);
        }
        for(int frame=0;frame<4;frame++)
            for(int sample=0;sample<16;sample++)
                Compare(Matrix4x4.Identity,new(sample/8f-1,0,2),new(sample/8f-1,0,-2));
        Assert.Equal(48,cached.OcclusionCacheHits);
        Compare(Matrix4x4.Identity,new(-1,0,2),new(-1,0,-2),.25f);
        Compare(Matrix4x4.Identity,new(-1,0,2),new(-1,0,-2),1,true);
        Compare(Matrix4x4.CreateTranslation(0,0,1),new(-1,0,2),new(-1,0,-2));
        Assert.Equal(48,cached.OcclusionCacheHits);
        var random=new Random(848);
        for(int n=0;n<300;n++)
        {
            var pose=Matrix4x4.CreateTranslation(0,0,n%4);
            var start=new Vector3((float)random.NextDouble()*6-3,0,2);
            var end=new Vector3(start.X,0,-2);
            Compare(pose,start,end);Compare(pose,start,end);
        }
        Assert.True(cached.OcclusionCacheHits>=348);
    }

    [Fact]
    public void SharedWorldRayNormalizationPreservesAllStoredBits()
    {
        var random=new Random(447);
        for(int i=0;i<10000;i++)
        {
            float F()=>BitConverter.UInt32BitsToSingle((uint)random.NextInt64(0,1L<<32));
            Vector3 delta=i%10==0?new(i%20==0?-0f:0f,0,0):new(F(),F(),F());
            EnbRetailDazzleMeshRay.NormalizeWorldRay(delta,false,out var expectedSquare,out var expected);
            for(int repeat=0;repeat<3;repeat++)
            {
                EnbRetailDazzleMeshRay.NormalizeWorldRay(delta,true,out var square,out var normal);
                Assert.Equal(BitConverter.SingleToUInt32Bits(expectedSquare),BitConverter.SingleToUInt32Bits(square));
                Assert.Equal(BitConverter.SingleToUInt32Bits(expected.X),BitConverter.SingleToUInt32Bits(normal.X));
                Assert.Equal(BitConverter.SingleToUInt32Bits(expected.Y),BitConverter.SingleToUInt32Bits(normal.Y));
                Assert.Equal(BitConverter.SingleToUInt32Bits(expected.Z),BitConverter.SingleToUInt32Bits(normal.Z));
            }
        }
    }

    [Fact]
    public void NativeSingleBoundsMatchesLiteralAcrossExponentRange()
    {
        var random=new Random(0x2450);
        for(int i=0;i<30000;i++)
        {
            float F()=>MathF.ScaleB((float)(random.NextDouble()*2-1),random.Next(-40,41));
            Vector3 V()=>new(F(),F(),F());
            var center=V();var extent=Vector3.Abs(V());var start=V();var end=V();
            var delta=end-start;var normal=Vector3.Normalize(delta);
            Assert.Equal(EnbRetailDazzleCollision.ClassifyRayBoundsLiteral(center,extent,start,end,delta,normal),
                EnbRetailDazzleCollision.ClassifyRayBounds(center,extent,start,end,delta,normal));
        }
    }


    [Theory]
    [InlineData(0u, false, true)]
    [InlineData(0x80u, true, true)]
    [InlineData(0x20u, false, false)]
    public void LoaderAndPostprocessedMasksPreserveDifferentNativeRayAdmission(uint attributes,bool loadedHit,bool configuredHit)
    {
        var mesh=new EnbW3dRenderMesh(0,"factory","",3,
            [new(-2,-2,0),new(2,-2,0),new(0,2,0)],[],[],[0,1,2],[],[],[]) {Attributes=attributes};
        foreach(var (mask,hit) in new[]{(EnbRetailDazzleCollision.LoadedMeshMask(mesh),loadedHit),
            (EnbRetailDazzleCollision.ConfiguredMeshMask(mesh),configuredHit)})
        {
            var ray=new EnbRetailDazzleMeshRay(mesh,1,mask);
            var native=ray.CastSegment(Matrix4x4.Identity,new(0,0,1),new(0,0,-1));
            Assert.Equal(hit,native.Fraction<1);
            Assert.Equal(native,ray.CastSegment(Matrix4x4.Identity,new(0,0,1),new(0,0,-1)));
        }
    }


    [Fact]
    public void AuthoredTreeControlsPolygonAdmissionAndScalesWithItsOwner()
    {
        static byte[] Chunk(uint type,byte[] data)
        {
            var bytes=new byte[data.Length+8];BinaryPrimitives.WriteUInt32LittleEndian(bytes,type);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4),data.Length);data.CopyTo(bytes,8);return bytes;
        }
        var header=new byte[32];BinaryPrimitives.WriteInt32LittleEndian(header,1);BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4),1);
        var node=new byte[32];float[] bounds=[-2,-2,0,2,2,0];
        for(int i=0;i<6;i++)BinaryPrimitives.WriteSingleLittleEndian(node.AsSpan(i*4),bounds[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(24),0x80000000);BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(28),1);
        byte[] payload=Chunk(0x91,header).Concat(Chunk(0x92,new byte[4])).Concat(Chunk(0x93,node)).ToArray();
        // The authored leaf references polygon0 only. Do not replace its
        // membership with every render triangle in the model (0092AE10).
        var mesh=new EnbW3dRenderMesh(0,"tree","",6,
            [new(-2,-2,0),new(2,-2,0),new(0,2,0),new(-2,-2,1),new(2,-2,1),new(0,2,1)],
            [],[],[0,1,2,3,4,5],[],[],[]) {AuthoredAabTreePayload=payload};
        foreach(float scale in new[]{.25f,1f,3f})
        {
            var tree=new EnbRetailDazzleMeshRay(mesh,scale);
            var linear=new EnbRetailDazzleMeshRay((mesh).Copy().SetAuthoredAabTreePayload(null),scale);
            Vector3 start=new(0,0,2*scale),end=new(0,0,.5f*scale);
            Assert.Equal((1f,false),tree.CastSegment(Matrix4x4.Identity,start,end));
            Assert.True(linear.CastSegment(Matrix4x4.Identity,start,end).Fraction<1);
            Assert.Equal((1f,false),tree.CastOcclusionSegment(Matrix4x4.Identity,start,end));
            Assert.Equal((.5f,false),tree.CastSegment(Matrix4x4.Identity,new(0,0,scale),new(0,0,-scale)));
        }
        //009BCC40 discards even an authored tree for internal skin flag0400.
        var skinned=new EnbRetailDazzleMeshRay((mesh).Copy().SetAttributes(0x20000).SetGeometryVersion(0x40001),1);
        Assert.True(skinned.CastSegment(Matrix4x4.Identity,new(0,0,2),new(0,0,.5f)).Fraction<1);
        var random=new Random(0x9b38f0);
        foreach(float scale in new[]{-2f,0f,.25f,1f,3f})
        {
            var ray=new EnbRetailDazzleMeshRay(mesh,scale);
            for(int i=0;i<60;i++)
            {
                float F()=>(float)(random.NextDouble()*4-2);
                var world=Matrix4x4.CreateFromYawPitchRoll(F(),F(),F())*Matrix4x4.CreateTranslation(F(),F(),F());
                var start=Vector3.Transform(new(F(),F(),4),world);
                var end=Vector3.Transform(new(F(),F(),-4),world);
                float fraction=i%3==0?.4f:1;
                var native=ray.CastSegment(world,start,end,fraction);
                Assert.Equal(native,ray.CastSegment(world,start,end,fraction));
                Assert.Equal(native.Fraction<1,ray.CastOcclusionSegment(world,start,end,fraction).Fraction<1);
            }
        }
    }



    [Fact]
    public void BoxRayRetainsInsideStartAndOrientation()
    {
        var ray=new EnbRetailDazzleBoxRay();
        Assert.Equal((.25f,false),ray.CastSegment(Vector3.Zero,Vector3.One,null,new(0,0,2),new(0,0,-2)));
        Assert.Equal((1f,true),ray.CastSegment(Vector3.Zero,Vector3.One,null,Vector3.Zero,new(0,0,2)));
        Assert.Equal((1f,false),ray.CastSegment(Vector3.Zero,Vector3.One,null,new(0,0,2),new(0,0,1)));
        Assert.Equal((.25f,false),ray.CastSegment(Vector3.Zero,Vector3.One,Matrix4x4.Identity,new(0,0,2),new(0,0,-2)));
        Assert.Equal((1f,true),ray.CastSegment(Vector3.Zero,Vector3.One,Matrix4x4.Identity,Vector3.Zero,new(0,0,2)));
        // Exact 90-degree rotation: the long local X extent lies on world Y.
        Matrix4x4 turn=new(0,1,0,0,-1,0,0,0,0,0,1,0,0,0,0,1);
        Assert.Equal((.25f,false),ray.CastSegment(Vector3.Zero,new(2,1,1),turn,new(0,4,0),new(0,-4,0)));
    }

    [Fact]
    public void MeshRayUsesConfiguredMaskHiddenFlagAndScaledBaseGeometry()
    {
        var mesh=new EnbW3dRenderMesh(0,"ray","",3,[new(-2,-2,0),new(2,-2,0),new(0,2,0)],
            [],[],[0,1,2],[],[],[]) {BoundingBoxMin=new(-2,-2,0),BoundingBoxMax=new(2,2,0)};
        Vector3 start=new(0,0,2),end=new(0,0,-2);
        Assert.Equal((.5f,false),new EnbRetailDazzleMeshRay(mesh,1).CastSegment(Matrix4x4.Identity,start,end));
        Assert.Equal((1f,false),new EnbRetailDazzleMeshRay((mesh).Copy().SetAttributes(0x20),1).CastSegment(Matrix4x4.Identity,start,end));
        var renderHidden=new EnbRetailDazzleMeshRay((mesh).Copy().SetAttributes(0x1000),1);
        Assert.Equal((.5f,false),renderHidden.CastSegment(Matrix4x4.Identity,start,end));
        Assert.Equal((.5f,false),renderHidden.CastSegment(Matrix4x4.Identity,start,end));
        var hierarchyHidden=new EnbRetailDazzleMeshRay(mesh,1,hidden:true);
        Assert.Equal((1f,false),hierarchyHidden.CastSegment(Matrix4x4.Identity,start,end));
        Assert.Equal((1f,false),hierarchyHidden.CastSegment(Matrix4x4.Identity,start,end));
        Assert.Equal((.25f,false),new EnbRetailDazzleMeshRay(mesh,2).CastSegment(Matrix4x4.CreateTranslation(0,0,1),start,end));
        Assert.Equal((1f,false),new EnbRetailDazzleMeshRay(mesh,0).CastSegment(Matrix4x4.Identity,start,end));
        var sorted=(mesh).Copy().SetCurrentRetailGeometrySortRequestOverride(true);
        Assert.Equal((1f,false),new EnbRetailDazzleMeshRay(sorted,1).CastSegment(Matrix4x4.Identity,start,end));
        Assert.Equal((.5f,false),new EnbRetailDazzleMeshRay((sorted).Copy().SetAttributes(0x80),1).CastSegment(Matrix4x4.Identity,start,end));
    }

    [Fact]
    public void TerrainRayPreservesInteriorEndpointAndWrappingRules()
    {
        ushort[] heights=Enumerable.Repeat((ushort)10,64).ToArray();
        var bounded=new EnbRetailTerrainSampler(8,heights,1,1,1);
        var wrapped=new EnbRetailTerrainSampler(8,heights,1,1,1,wrapping:true);
        Assert.Equal((.5f,false),bounded.CastSegment(new(0,0,20),new(0,0,0)));
        Assert.Equal((.25f,false),bounded.CastSegment(new(0,0,20),new(0,0,0),.25f));
        Assert.Equal((.25f,true),bounded.CastSegment(new(0,0,5),new(0,0,20),.25f));
        Assert.Equal((.5f,true),bounded.CastSegment(new(0,0,20),new(0,0,0),1,true));
        Assert.Equal((1f,true),bounded.CastSegment(new(0,0,5),new(0,0,20)));
        Assert.Equal((1f,false),bounded.CastSegment(new(0,0,20),new(0,0,10)));
        Assert.Equal((1f,false),bounded.CastSegment(new(30,0,20),new(30,0,0)));
        Assert.Equal((.5f,false),wrapped.CastSegment(new(30,0,20),new(30,0,0)));
        Assert.Equal((.5f,false),bounded.CastSegment(new(-3,0,20),new(3,0,0)));
        Assert.Equal((1f,false),bounded.CastSegment(new(-3,0,20),new(3,0,20)));
    }

}
