using System.Numerics;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
public sealed class EnbPlaneOnlyRangeTests
{
    [Fact]
    public void ExpandedPlaneRejectionCannotHideLiteralHits()
    {
        var random=new Random(0x8e1730);int rejected=0,hits=0,wideQueries=0;
        float[] scales=[1,1e-5f,1e-12f,MathF.Pow(2,-50),1e10f,MathF.Pow(2,49)];
        Vector3 V(float s)=>new((float)(random.NextDouble()*2-1)*s,(float)(random.NextDouble()*2-1)*s,(float)(random.NextDouble()*2-1)*s);
        for(int batch=0;batch<180;batch++)
        {
            int count=Vector<float>.Count*2+3;
            var triangles=new EnbRetailDazzleCollision.Triangle[count];
            var inputs=new (Vector3 A,Vector3 B,Vector3 C,Vector3 N)[count];
            for(int i=0;i<count;i++)
            {
                float scale=scales[(batch+i)%scales.Length];var a=V(scale);var b=V(scale);var c=V(scale);
                var n=Vector3.Normalize(Vector3.Cross(b-a,c-a));
                if(i%3==0)n=new(0,0,1);
                if(i%5==0)a.X=float.Epsilon;
                if(i%7==0)n.Y=MathF.Pow(2,-31);
                if(i%11==0)n.Z=float.NaN;
                triangles[i]=new(a,b,c,n);inputs[i]=(a,b,c,n);
            }
            var blocks=new EnbRetailTrianglePlaneBlocks(triangles);
            for(int ray=0;ray<60;ray++)
            {
                var origin=V(scales[ray%scales.Length]);var delta=V(scales[(ray+1)%scales.Length]);
                if(ray%4==0){var t=inputs[ray%count];origin=(t.A+t.B+t.C)/3+new Vector3(0,0,1);delta=new(0,0,-2);}
                if(ray%9==0)delta=Vector3.Zero;
                if(!EnbRetailDazzleCollision.SinglePlaneRange(origin)||!EnbRetailDazzleCollision.SinglePlaneRange(delta))continue;
                bool edges=EnbRetailDazzleCollision.SingleTriangleRange(origin)&&EnbRetailDazzleCollision.SingleTriangleRange(delta);
                if(!edges)wideQueries++;
                for(int block=0;block<blocks.Count;block++)
                {
                    var mask=blocks.Candidates(block,origin,delta,true,edges);
                    for(int lane=0;lane<Vector<float>.Count;lane++)
                    {
                        int i=block*Vector<float>.Count+lane;if(i>=count)break;
                        var t=inputs[i];float fraction=1;
                        bool hit=EnbRetailDazzleCollision.IntersectTriangleLiteral(origin,delta,t.A,t.B,t.C,t.N,ref fraction);
                        if(hit)hits++;
                        if(mask[lane]==0){rejected++;Assert.False(hit);}
                    }
                }
            }
        }
        Assert.True(wideQueries>1000);Assert.True(rejected>10000);Assert.True(hits>100);
    }
}
