using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Linear displacement partition admitted by retail's MatroxEx1 declaration.
/// 001277E0/00127A00/00127C80/00128DF0/0012F070 in the fingerprinted API-boundary receipt.
/// Produces GPU mesh input; no driver, software rasterizer or vendor check is required.</summary>
public static class EnbRetailDisplacementPartition
{
    public sealed record Result(Vector3[] Weights, uint[] Indices, float[] Factors, int[] EdgeCounts);

    public static Result Build(Vector3 a, Vector3 b, Vector3 c, Vector3 depths, Vector3 visibility, float rate)
    {
        Vector3[] source = [a,b,c];
        float[] factors = new float[3], raw = new float[3], interior = new float[3], morph = new float[3];
        int[] counts = new int[3];
        for(int i=0;i<3;i++) factors[i]=Math.Clamp(visibility[i]*rate/Math.Max(1e-37f,depths[i]),1,19);
        for(int i=0;i<3;i++)
        {
            raw[i]=(factors[i]+factors[(i+1)%3])*.5f;
            interior[i]=Math.Clamp(raw[i]-3,1,16);
            int n=1;
            if(interior[i]>1) while(n<=interior[i] && n<16)n*=2;
            counts[i]=n;
            morph[i]=(interior[i]-n/2)/(n-n/2);
        }
        int size=counts.Max();
        float inner=interior.Max(), fade=Fade(raw.Max());
        float inset=1/(inner+3-fade), step=(1-3*inset)/size;
        float innerMorph=1-(size-inner)/Math.Max(1,size/2);
        int[] edges = [Tri(size+1),Tri(size+1)+counts[0]+4,Tri(size+1)+counts[0]+counts[1]+8];
        int corner=edges[2]+counts[2]+4;
        var weights=new Vector3[corner+3];
        void Point(int index,float u,float v) => weights[index]=new(1-Q(u)-Q(v),Q(u),Q(v));
        void Grid(int i,int j,float u,float v)=>Point(Tri(i+j)+j,u*step+inset,v*step+inset);
        if(size==1)
        {
            for(int i=0;i<=size;i++)for(int j=0;j<=size-i;j++)Grid(i,j,i,j);
        }
        else
        {
            for(int i=0;i<=size;i+=2)for(int j=0;j<=size-i;j+=2)
            {
                Grid(i,j,i,j);
                if(i+j+1<=size)Grid(i+1,j,i+innerMorph,j);
                if(i>0)Grid(i-1,j+1,i-innerMorph,j+innerMorph);
                if(j>0)Grid(i,j-1,i,j-innerMorph);
            }
        }
        for(int e=0;e<3;e++)
        {
            int n=counts[e], start=edges[e], next=(e+1)%3;
            float border=1/(interior[e]+3);
            bool forward=Forward(source[e],source[next]);
            Vector3 from=Unit(forward?e:next),to=Unit(forward?next:e);
            weights[start]=Unit(e);weights[start+n+3]=Unit(next);
            float Coordinate(int i)
            {
                float fine=(float)(i-1)/(n+1),coarse=(float)(i/2)/(n/2+1);
                return (fine*morph[e]+coarse*(1-morph[e]))*(1-2*border)+border;
            }
            void Edge(int index,float value)=>weights[start+index]=from*(1-Q(value))+to*Q(value);
            for(int i=1;i<=n+2;i++)Edge(forward?i:n-i+3,Coordinate(i));
            if(raw[e]<4)
            {
                float f=Fade(raw[e]);
                if(forward)
                {
                    Edge(1,(Coordinate(2)-Coordinate(1))*f+Coordinate(1));
                    Edge(3,(Coordinate(2)-Coordinate(3))*f+Coordinate(3));
                }
                else
                {
                    Edge(1,(Coordinate(n+1)-Coordinate(n+2))*f+Coordinate(n+2));
                    Edge(3,(Coordinate(n+1)-Coordinate(n))*f+Coordinate(n));
                }
            }
        }
        int[] innerCorners=[0,Tri(size),Tri(size)+size];
        for(int e=0;e<3;e++)
        {
            float f=Fade(Math.Min(raw[e],raw[(e+2)%3]));
            var p=(weights[innerCorners[e]]*(1+f)+Unit(e)*(1-f))*.5f;
            Point(corner+e,p.Y,p.Z);
        }
        var indices=new List<uint>();
        void Triangle(int x,int y,int z){indices.Add((uint)x);indices.Add((uint)y);indices.Add((uint)z);}
        for(int k=0;k<size;k++)for(int j=0;j<=k;j++)
        {
            Triangle(Tri(k)+j,Tri(k+1)+j,Tri(k+1)+j+1);
            if(j<k)Triangle(Tri(k)+j,Tri(k+1)+j+1,Tri(k)+j+1);
        }
        for(int e=0;e<3;e++)
        {
            int start=edges[e],n=counts[e],ratio=size/n;
            int Inside(int i)=>e==0?Tri(i):e==1?Tri(size)+i:Tri(size-i)+size-i;
            for(int i=0;i<=n;i++)
            {
                Triangle(start+i+1,start+i+2,Inside(i*ratio));
                if(i<n)for(int j=0;j<ratio;j++)Triangle(start+i+2,Inside(i*ratio+j+1),Inside(i*ratio+j));
            }
            int end=edges[(e+2)%3]+counts[(e+2)%3]+2;
            Triangle(corner+e,end,start);
            Triangle(corner+e,start,start+1);
            Triangle(corner+e,start+1,innerCorners[e]);
            Triangle(corner+e,innerCorners[e],end);
        }
        return new(weights,indices.ToArray(),factors,counts);
    }

    private static int Tri(int n)=>n*(n+1)/2;
    private static float Q(float value)=>MathF.Round(value*32768)/32768;
    private static float Fade(float value)=>value<2?1:value<4?(4-value)*.5f:0;
    private static Vector3 Unit(int i)=>i==0?Vector3.UnitX:i==1?Vector3.UnitY:Vector3.UnitZ;
    private static bool Forward(Vector3 a,Vector3 b)
    {
        var delta=b-a;
        for(int i=0;i<3;i++){if(delta[i]>1e-4f)return true;if(delta[i]<-1e-4f)return false;}
        return false;
    }
}
