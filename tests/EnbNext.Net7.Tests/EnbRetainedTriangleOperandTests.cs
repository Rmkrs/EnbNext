using System.Numerics;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
public sealed class EnbRetainedTriangleOperandTests
{
    [Fact]
    public void RetainedOperandsMatchLiteralForChangingAndExceptionalRays()
    {
        var random=new Random(0x8c91e0);
        float[] scales=[0,1,-1,1e-38f,1e-20f,1e-7f,1e10f,1e30f];
        Vector3 V(float scale)=>new((float)(random.NextDouble()*2-1)*scale,(float)(random.NextDouble()*2-1)*scale,(float)(random.NextDouble()*2-1)*scale);
        int hits=0;
        for(int triangle=0;triangle<400;triangle++)
        {
            float scale=scales[triangle%scales.Length];
            var a=V(scale);var b=V(scale);var c=V(scale);
            var normal=Vector3.Normalize(Vector3.Cross(b-a,c-a));
            if(triangle%11==0)normal=new(0,0,1);
            if(triangle%13==0)normal*=1e-30f;
            if(triangle%17==0)a.X=float.Epsilon;
            if(triangle%19==0)normal.X=float.NaN;
            if(triangle%23==0)c.Z=float.PositiveInfinity;
            var retained=new EnbRetailDazzleCollision.Triangle(a,b,c,normal,retainLiteral:true);
            for(int ray=0;ray<40;ray++)
            {
                var origin=V(scale);var delta=V(scale);
                if(ray%3==0){origin=(a+b+c)/3+normal;delta=-normal*2;}
                if(ray%7==0)delta=Vector3.Zero;
                if(ray%11==0)origin=a;
                float expected=ray%5==0?.25f:1,actual=expected;
                bool expectedHit=EnbRetailDazzleCollision.IntersectTriangleLiteral(origin,delta,a,b,c,normal,ref expected);
                bool actualHit=retained.Intersect(origin,delta,false,ref actual);
                Assert.Equal(expectedHit,actualHit);
                Assert.Equal(BitConverter.SingleToUInt32Bits(expected),BitConverter.SingleToUInt32Bits(actual));
                if(actualHit)hits++;
            }
        }
        Assert.True(hits>100);
    }
}
