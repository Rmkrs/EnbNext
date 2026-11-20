using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public sealed record EnbRetailDisplacementSettings(bool Enabled=true,int BaseTessellationSize=16,
    int DisplacementMapSize=256,int TessellationRate=80000);

/// <summary>00466BE0 terrain owner, 004677B0 height upload, 00467A50 grid and
/// 00466F60/00468D50 draw. The generated geometry is consumed by the GPU terrain material.</summary>
public sealed class EnbRetailTerrainDisplacement
{
    private readonly EnbRetailTerrain terrain;
    private readonly EnbRetailDisplacementSettings settings;
    private readonly Vector3[] grid;
    private readonly byte[][] levels;
    private readonly EnbW3dRenderMesh materialTemplate;
    private EnbResolvedRenderCamera? lastCamera;
    private EnbW3dRenderMesh? lastMesh;
    public int MipCount=>levels.Length;

    public EnbRetailTerrainDisplacement(EnbRetailTerrain terrain,EnbRetailDisplacementSettings settings,
        EnbW3dRenderMesh materialTemplate,int maximumTextureSize)
    {
        this.terrain=terrain;this.settings=settings;this.materialTemplate=materialTemplate;
        int n=settings.BaseTessellationSize,w=settings.DisplacementMapSize;
        // The native source grid uses 16-bit indices and the displacement texture
        // has a complete square power-of-two mip chain. Invalid optional resources
        // are contained by the caller, never substituted with an invented surface.
        if(n<2||n>256||w<1||w>maximumTextureSize||(w&(w-1))!=0)
            throw new InvalidDataException("Displacement grid or mip texture exceeds the admitted resource extent.");
        grid=new Vector3[checked(n*n)];
        for(int y=0;y<n;y++)for(int x=0;x<n;x++)grid[y*n+x]=new((float)x/(n-1)-.5f,(float)y/(n-1)-.5f,0);
        var mips=new List<byte[]>();var pixels=new byte[checked(w*w)];
        float ratio=(float)terrain.Width/w;
        for(int y=0;y<w;y++)for(int x=0;x<w;x++)
            pixels[y*w+x]=(byte)terrain.Heights[(int)(y*ratio)%terrain.Width*terrain.Width+(int)(x*ratio)%terrain.Width];
        mips.Add(pixels);
        // Embedded D3DX 008EA5F4: L8 box filter adds two before shifting by two.
        while(w>1)
        {
            int next=w/2;var reduced=new byte[next*next];
            for(int y=0;y<next;y++)for(int x=0;x<next;x++)
            {int p=y*2*w+x*2;reduced[y*next+x]=(byte)((pixels[p]+pixels[p+1]+pixels[p+w]+pixels[p+w+1]+2)>>2);}
            mips.Add(reduced);pixels=reduced;w=next;
        }
        levels=mips.ToArray();
    }

    public float Sample(Vector2 uv,float lod)
    {
        lod=Math.Clamp(lod,0,levels.Length-1);
        int low=(int)lod,high=Math.Min(low+1,levels.Length-1);
        float a=Bilinear(low,uv),b=Bilinear(high,uv);
        return (a+(b-a)*(lod-low))*(1f/255);
    }
    private float Bilinear(int level,Vector2 uv)
    {
        int w=Math.Max(1,settings.DisplacementMapSize>>level);
        float x=uv.X*w-.5f,y=uv.Y*w-.5f;
        int ix=(int)MathF.Floor(x),iy=(int)MathF.Floor(y);
        float fx=x-ix,fy=y-iy;
        byte At(int a,int b)=>levels[level][(b&(w-1))*w+(a&(w-1))];
        float top=At(ix,iy)*(1-fx)+At(ix+1,iy)*fx;
        float bottom=At(ix,iy+1)*(1-fx)+At(ix+1,iy+1)*fx;
        return top*(1-fy)+bottom*fy;
    }
    public static Vector3 Displace(Vector3 position,Vector3 direction,float height)
        =>position+Vector3.Normalize(direction)*height;

    public static float SampleLod(float factor,int mipCount)
        // 00130E10 stores the last resident index at +E48; 0012E100 subtracts
        // one so trilinear sampling always has a following level.
        =>Math.Clamp(MathF.Log2(19/(Math.Clamp(factor-3,1,16)+3)),0,Math.Clamp(mipCount-2,0,2));

    public EnbW3dRenderMesh Evaluate(EnbResolvedRenderCamera camera)
    {
        if(lastCamera==camera&&lastMesh is {} retained)return retained;
        var scale=new Vector3(terrain.HorizontalScale,terrain.HorizontalScale,terrain.VerticalScale*terrain.Width);
        int n=settings.BaseTessellationSize;
        var flags=new float[grid.Length];
        var planes=SidePlanes(camera).Select(p=>new Vector4(p.X*scale.X,p.Y*scale.Y,p.Z*scale.Z,p.W)).ToArray();
        for(int y=1;y<n;y++)for(int x=1;x<n;x++)
        {
            int a=(y-1)*n+x,b=y*n+x-1,c=(y-1)*n+x-1,d=y*n+x;
            var min=grid[c];var max=grid[d]+Vector3.UnitZ;
            if(Classify(min,max,planes)==0)continue;
            flags[a]=flags[b]=flags[c]=flags[d]=1;
        }
        var positions=new List<Vector3>();var uv0=new List<Vector2>();var uv1=new List<Vector2>();var indices=new List<uint>();
        float Depth(int i)=>Vector3.Dot(grid[i]*scale-camera.Position,camera.Basis.Forward);
        void Triangle(int ia,int ib,int ic)
        {
            var a=grid[ia];var b=grid[ib];var c=grid[ic];
            var part=EnbRetailDisplacementPartition.Build(a,b,c,new(Depth(ia),Depth(ib),Depth(ic)),new(flags[ia],flags[ib],flags[ic]),settings.TessellationRate);
            float Lod(float factor)=>SampleLod(factor,levels.Length);
            var lods=new Vector3(Lod(part.Factors[0]),Lod(part.Factors[1]),Lod(part.Factors[2]));
            uint start=(uint)positions.Count;
            foreach(var weights in part.Weights)
            {
                var p=a*weights.X+b*weights.Y+c*weights.Z;
                var uv=new Vector2(p.X+.5f,p.Y+.5f);
                float lod=Vector3.Dot(lods,weights);
                positions.Add(Displace(p,Vector3.UnitZ,Sample(uv,lod))*scale);
                uv0.Add(uv);uv1.Add(uv*terrain.DetailScale);
            }
            indices.AddRange(part.Indices.Select(i=>start+i));
        }
        for(int y=1;y<n;y++)for(int x=1;x<n;x++)
        {int a=(y-1)*n+x,b=y*n+x-1,c=(y-1)*n+x-1,d=y*n+x;Triangle(a,b,c);Triangle(d,b,a);}
        var sourcePass=materialTemplate.MaterialPasses[0];
        var stages=sourcePass.TextureStages.Select((s,i)=>(s).Copy().SetRuntimeTexCoords(i==0?uv0:uv1).SetEffectiveRuntimeTexCoords(i==0?uv0:uv1)).ToArray();
        var binding=sourcePass.TriangleBindings[0];
        var bindings=Enumerable.Range(0,indices.Count/3).Select(i=>binding with {TriangleOrdinal=i,MaterialVertexIndex=(int)indices[i*3]}).ToArray();
        var pass=(sourcePass).Copy().SetTextureStages(stages).SetTriangleBindings(bindings);
        var minBound=positions.Aggregate(new Vector3(float.PositiveInfinity),Vector3.Min);
        var maxBound=positions.Aggregate(new Vector3(float.NegativeInfinity),Vector3.Max);
        lastMesh=(materialTemplate).Copy().SetName("terrain.displacement").SetPositions(positions).SetNormals(Enumerable.Repeat(Vector3.UnitZ,positions.Count).ToArray()).SetIndices(indices).SetTriangleMetadata(new EnbW3dRenderTriangleMetadata[indices.Count/3]).SetMaterialPasses([pass]).SetBoundingBoxMin(minBound).SetBoundingBoxMax(maxBound).SetBoundingSphereCenter((minBound+maxBound)*.5f).SetBoundingRadius(Vector3.Distance(minBound,maxBound)*.5f);
        lastCamera=camera;
        return lastMesh;
    }

    private static Vector4[] SidePlanes(EnbResolvedRenderCamera camera)
    {
        // 00468240: -(rowW-rowX), -(rowW+rowX), -(rowW-rowY), -(rowW+rowY).
        var r=camera.Basis.Right;var u=camera.Basis.Up;var f=-camera.Basis.Forward;
        var view=new Matrix4x4(r.X,u.X,f.X,0,r.Y,u.Y,f.Y,0,r.Z,u.Z,f.Z,0,
            -Vector3.Dot(r,camera.Position),-Vector3.Dot(u,camera.Position),-Vector3.Dot(f,camera.Position),1);
        var m=view*camera.Projection.Matrix;
        Vector4 x=new(m.M11,m.M21,m.M31,m.M41),y=new(m.M12,m.M22,m.M32,m.M42),w=new(m.M14,m.M24,m.M34,m.M44);
        return [x-w,-w-x,y-w,-w-y];
    }
    public static int Classify(Vector3 min,Vector3 max,IReadOnlyList<Vector4> planes)
    {
        // Preserve 00468760's immediate intersect return and its four-plane order.
        foreach(var p in planes)
        {
            var normal=new Vector3(p.X,p.Y,p.Z);
            var low=new Vector3(p.X<0?max.X:min.X,p.Y<0?max.Y:min.Y,p.Z<0?max.Z:min.Z);
            var high=min+max-low;
            if(Vector3.Dot(normal,low)+p.W>0)return 0;
            if(Vector3.Dot(normal,high)+p.W>=0)return 1;
        }
        return 2;
    }
}
