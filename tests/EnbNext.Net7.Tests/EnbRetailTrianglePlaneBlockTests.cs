using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailTrianglePlaneBlockTests
{
    [Fact]
    public void RejectedLanesCannotHideLiteralHitsAcrossNormalAndExceptionalInputs()
    {
        var random=new Random(0x1753);
        int rejections=0;
        for(int batch=0;batch<160;batch++)
        {
            int count=EnbRetailTrianglePlaneBlocks.Width*2+3;
            var triangles=new EnbRetailDazzleCollision.Triangle[count];
            var inputs=new (Vector3 A,Vector3 B,Vector3 C,Vector3 N)[count];
            float F()=>(float)(random.NextDouble()*200-100);
            Vector3 V()=>new(F(),F(),F());
            for(int i=0;i<count;i++)
            {
                var a=V();var b=V();var c=V();
                var n=Vector3.Normalize(Vector3.Cross(b-a,c-a));
                if(i%7==0)n*=MathF.Pow(2,-70); // Outside the existing PC24 fast-range proof.
                if(i%11==0)a.X=float.Epsilon;
                if(i%13==0)n=new(-0f,0,1);
                if(i%17==0)n=new(float.NaN,0,1);
                inputs[i]=(a,b,c,n);triangles[i]=new(a,b,c,n);
            }
            var blocks=new EnbRetailTrianglePlaneBlocks(triangles);
            for(int ray=0;ray<12;ray++)
            {
                var origin=V();var delta=ray==0?Vector3.Zero:V();
                Assert.True(EnbRetailDazzleCollision.SingleTriangleRange(origin));
                Assert.True(EnbRetailDazzleCollision.SingleTriangleRange(delta));
                for(int block=0;block<blocks.Count;block++)
                {
                    var mask=blocks.Candidates(block,origin,delta);
                    for(int lane=0;lane<EnbRetailTrianglePlaneBlocks.Width;lane++)
                    {
                        int i=block*EnbRetailTrianglePlaneBlocks.Width+lane;if(i>=count)break;
                        if(mask[lane]!=0)continue;
                        rejections++;
                        var t=inputs[i];float fraction=1;
                        Assert.False(EnbRetailDazzleCollision.IntersectTriangleLiteral(origin,delta,t.A,t.B,t.C,t.N,ref fraction));
                        Assert.Equal(BitConverter.SingleToUInt32Bits(1),BitConverter.SingleToUInt32Bits(fraction));
                    }
                }
            }
        }
        Assert.True(rejections>1000);
    }

    [Theory]
    [InlineData(9,1f,false)]
    [InlineData(9,1f,true)]
    [InlineData(37,.7f,false)]
    [InlineData(37,.7f,true)]
    [InlineData(64,1.3f,false)]
    [InlineData(64,1.3f,true)]
    public void RetainedBlocksPreserveNearestAndOcclusionFractionsAndTriangleOrder(int count,float scale,bool tree)
    {
        var positions=new List<Vector3>();var indices=new List<uint>();
        // Deliberately non-depth-sorted planes. Occlusion returns the first
        // accepted original triangle, while nearest-hit consumes the rest.
        for(int i=0;i<count;i++)
        {
            float z=(i*17%count)-count*.5f;
            positions.AddRange([new(-10,-10,z),new(10,-10,z),new(0,10,z)]);
            indices.AddRange([(uint)(i*3),(uint)(i*3+1),(uint)(i*3+2)]);
        }
        var mesh=new EnbW3dRenderMesh(0,"planes","",positions.Count,positions,[],[],indices,[],[],[])
        {GeometryVersion=0x40001,BoundingBoxMin=new(-10,-10,-count),BoundingBoxMax=new(10,10,count),BoundingRadius=count*2};
        if(tree)mesh=(mesh).Copy().SetAuthoredAabTreePayload(Tree(count));
        var baseline=new EnbRetailDazzleMeshRay(mesh,scale){UseUncachedOcclusionQueries=true};
        var candidate=new EnbRetailDazzleMeshRay(mesh,scale,usePlaneBlocks:true,useTreePlaneBlocks:tree){UseUncachedOcclusionQueries=true};
        var random=new Random(0x6331);
        for(int frame=0;frame<200;frame++)
        {
            var world=Matrix4x4.CreateRotationY(frame*.003f)*Matrix4x4.CreateTranslation(frame%7,frame%5,frame%3);
            float x=(float)(random.NextDouble()*40-20),y=(float)(random.NextDouble()*40-20);
            var start=new Vector3(x,y,count*2);var end=new Vector3(x,y,-count*2);
            if(frame%9==0)end=start;
            float fraction=frame%5==0?.25f:1;
            Compare(baseline.CastSegment(world,start,end,fraction),candidate.CastSegment(world,start,end,fraction));
            Compare(baseline.CastOcclusionSegment(world,start,end,fraction),candidate.CastOcclusionSegment(world,start,end,fraction));
            Compare(baseline.CastSegment(world,start,end,fraction,true),candidate.CastSegment(world,start,end,fraction,true));
        }
        // Segments wholly beyond all planes ensure the SIMD rejection path
        // executes, independently of early accepted-hit behavior above.
        for(int i=0;i<20;i++)
        {
            var start=new Vector3(i,0,count*4);var end=new Vector3(i,0,count*3);
            Compare(baseline.CastSegment(Matrix4x4.Identity,start,end),candidate.CastSegment(Matrix4x4.Identity,start,end));
        }
        Compare(baseline.CastSegment(Matrix4x4.Identity,new(50,0,count*.8f),new(50,0,-count*.8f)),candidate.CastSegment(Matrix4x4.Identity,new(50,0,count*.8f),new(50,0,-count*.8f)));
        if(Vector.IsHardwareAccelerated)Assert.True(candidate.PlaneRejectedTriangles>0);
        Assert.Equal(baseline.IsCurrentGeometry,candidate.IsCurrentGeometry);
    }
    internal static byte[] Tree(int count)
    {
        static byte[] Chunk(uint type,byte[] data)
        {
            var bytes=new byte[data.Length+8];System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes,type);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4),data.Length);data.CopyTo(bytes,8);return bytes;
        }
        // Two leaves with reversed, repeated and deliberately omitted polygon
        // indices. The optimization must preserve authored admission/order.
        var ids=Enumerable.Range(0,count-2).Reverse().Concat(new[]{1,1,0}).ToArray();
        var header=new byte[32];System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header,3);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4),ids.Length);
        var polygons=new byte[ids.Length*4];for(int i=0;i<ids.Length;i++)System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(polygons.AsSpan(i*4),ids[i]);
        var nodes=new byte[96];int split=ids.Length<16?1:ids.Length/2;
        for(int i=0;i<3;i++)
        {
            for(int axis=0;axis<6;axis++)System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(nodes.AsSpan(i*32+axis*4),axis<3?-1000:1000);
            uint first=i==0?1u:0x80000000u+(uint)(i==1?0:split);
            uint second=i==0?2u:(uint)(i==1?split:ids.Length-split);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(nodes.AsSpan(i*32+24),first);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(nodes.AsSpan(i*32+28),second);
        }
        return Chunk(0x91,header).Concat(Chunk(0x92,polygons)).Concat(Chunk(0x93,nodes)).ToArray();
    }
    private static void Compare((float Fraction,bool StartInside) expected,(float Fraction,bool StartInside) actual)
    {
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.Fraction),BitConverter.SingleToUInt32Bits(actual.Fraction));
        Assert.Equal(expected.StartInside,actual.StartInside);
    }
}
