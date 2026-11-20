using System.Collections.Immutable;
using System.Numerics;

namespace EnbNext.Rendering;

public readonly record struct EnbSubmittedPixelBounds(float MinX,float MinY,float MaxX,float MaxY,int Count)
{
    public static EnbSubmittedPixelBounds Empty => new(float.PositiveInfinity,float.PositiveInfinity,float.NegativeInfinity,float.NegativeInfinity,0);
    public bool HasArea => Count>0 && float.IsFinite(MinX) && float.IsFinite(MinY) &&
        float.IsFinite(MaxX) && float.IsFinite(MaxY) && MaxX>MinX && MaxY>MinY;
    public EnbSubmittedPixelBounds Include(EnbSubmittedPixelBounds next) => next.Count==0?this:
        new(MathF.Min(MinX,next.MinX),MathF.Min(MinY,next.MinY),MathF.Max(MaxX,next.MaxX),MathF.Max(MaxY,next.MaxY),Count+next.Count);
}

/// <summary>Exact vertex-projection prefilter evaluated only if a UI query asks
/// for it. The submitted geometry is already safe across the render-frame drain.
/// This never replaces the triangle intersection required for a body hit.</summary>
public sealed class EnbSubmittedMeshProjection(ImmutableArray<EnbSubmittedMeshPick> picks,int width,int height)
{
    private sealed record Result(EnbSubmittedPixelBounds Bounds);
    private Result? _result;
    public EnbSubmittedPixelBounds Bounds
    {
        get
        {
            if(Volatile.Read(ref _result) is {} ready)return ready.Bounds;
            var bounds=EnbSubmittedPixelBounds.Empty;
            foreach(var pick in picks)bounds=bounds.Include(pick.ProjectBounds(width,height));
            var result=new Result(bounds);
            return (Interlocked.CompareExchange(ref _result,result,null)??result).Bounds;
        }
    }
    public static EnbSubmittedPixelBounds Project(IReadOnlyList<Vector3> positions,Matrix4x4 clipFromModel,int width,int height)
    {
        var result=EnbSubmittedPixelBounds.Empty;
        for(int i=0;i<positions.Count;i++)
        {
            Vector4 clip=Vector4.Transform(new Vector4(positions[i],1f),clipFromModel);
            if(!float.IsFinite(clip.X)||!float.IsFinite(clip.Y)||!float.IsFinite(clip.Z)||!float.IsFinite(clip.W)||clip.W<=0f)continue;
            float inverseW=1f/clip.W;
            float x=clip.X*inverseW,y=clip.Y*inverseW,z=clip.Z*inverseW;
            if(!float.IsFinite(x)||!float.IsFinite(y)||!float.IsFinite(z)||z<0f||z>1f)continue;
            float px=((x*0.5f)+0.5f)*width,py=(0.5f-(y*0.5f))*height;
            if(!float.IsFinite(px)||!float.IsFinite(py))continue;
            result=new(MathF.Min(result.MinX,px),MathF.Min(result.MinY,py),MathF.Max(result.MaxX,px),MathF.Max(result.MaxY,py),result.Count+1);
        }
        return result;
    }
}

// One definition-owned mutable query slot. Return values remain independent of
// later publications; the lock also protects callers evaluating different views.
internal sealed class EnbSubmittedProjectionBinding
{
    private readonly object gate=new();
    private IReadOnlyList<Vector3>? positions;
    private Matrix4x4 clip;
    private int width,height;
    private EnbSubmittedPixelBounds bounds;
    public EnbSubmittedPixelBounds Project(IReadOnlyList<Vector3> source,Matrix4x4 matrix,int w,int h)
    {
        lock(gate)
        {
            if(ReferenceEquals(positions,source)&&clip==matrix&&width==w&&height==h)return bounds;
            bounds=EnbSubmittedMeshProjection.Project(source,matrix,w,h);
            positions=source;clip=matrix;width=w;height=h;
            return bounds;
        }
    }
}
