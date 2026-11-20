using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

// UI-thread-owned completed query storage. The renderer and gutter each retain
// their current completed view; a failed/in-progress invocation cannot reuse it.
internal sealed class LiveCompletedRenderQueries
{
    private int users;
    public ulong SnapshotVersion {get;set;}
    public Dictionary<ulong,LiveRenderedObjectQuery> BoundsByRenderIdentity {get;}=[];
    public IReadOnlyDictionary<ulong,float> DistanceRadiiByRenderIdentity {get;set;}=ImmutableDictionary<ulong,float>.Empty;
    public Dictionary<ulong,EnbW3dWorldBounds> RenderSpheresByRenderIdentity {get;}=[];
    public Dictionary<ulong,Vector3> LocalSphereCenters {get;}=[];
    public static LiveCompletedRenderQueries Empty {get;}=new();
    public bool IsRetained=>users!=0;
    public void Retain(){if(!ReferenceEquals(this,Empty))users++;}
    public void Release()
    {
        if(ReferenceEquals(this,Empty))return;
        if(users==0)throw new InvalidOperationException("Unbalanced completed-query release");
        if(--users==0)Clear();
    }
    public void Clear()
    {
        if(IsRetained)throw new InvalidOperationException("Cannot overwrite a retained completed view");
        foreach(var bounds in BoundsByRenderIdentity.Values)bounds.BodyQueries?.Release();
        BoundsByRenderIdentity.Clear();RenderSpheresByRenderIdentity.Clear();LocalSphereCenters.Clear();
        DistanceRadiiByRenderIdentity=ImmutableDictionary<ulong,float>.Empty;
    }
    public bool TryGetValue(ulong identity,out LiveRenderedObjectQuery bounds)=>BoundsByRenderIdentity.TryGetValue(identity,out bounds!);
}

internal sealed class LiveRenderedObjectQuery
{
    public ulong RenderIdentity {get;set;}
    public double MinimumX {get;set;}
    public double MinimumY {get;set;}
    public double MaximumX {get;set;}
    public double MaximumY {get;set;}
    public Vector3? PickSphereCenter {get;set;}
    public float PickSphereRadius {get;set;}
    public EnbBodyQueryOwner BodyQueries {get;}=new();
    public EnbRenderViewport ProjectionViewport {get;set;}
    public ImmutableArray<EnbSubmittedMeshPick> CapturePickDiagnostics()=>BodyQueries.CaptureDiagnostics();
    public bool TryResolveProjection(out LiveRenderedObjectQuery resolved)
    {
        resolved=this;
        if(BodyQueries.Count==0)return HasArea;
        var bounds=BodyQueries.Bounds;
        if(!bounds.HasArea || bounds.MaxX<0 || bounds.MinX>ProjectionViewport.PixelWidth ||
            bounds.MaxY<0 || bounds.MinY>ProjectionViewport.PixelHeight)return false;
        MinimumX=ProjectionViewport.ToLogicalX(bounds.MinX);MinimumY=ProjectionViewport.ToLogicalY(bounds.MinY);
        MaximumX=ProjectionViewport.ToLogicalX(bounds.MaxX);MaximumY=ProjectionViewport.ToLogicalY(bounds.MaxY);
        return resolved.HasArea;
    }
    public double CenterX => (MinimumX + MaximumX) * 0.5;

    public double CenterY => (MinimumY + MaximumY) * 0.5;

    public double Width => MaximumX - MinimumX;

    public double Height => MaximumY - MinimumY;

    public bool HasArea =>
        double.IsFinite(MinimumX) &&
        double.IsFinite(MinimumY) &&
        double.IsFinite(MaximumX) &&
        double.IsFinite(MaximumY) &&
        MaximumX > MinimumX &&
        MaximumY > MinimumY;

    public bool HasPickSphere =>
        PickSphereCenter is Vector3 center &&
        float.IsFinite(center.X) &&
        float.IsFinite(center.Y) &&
        float.IsFinite(center.Z) &&
        float.IsFinite(PickSphereRadius) &&
        PickSphereRadius > 0f;
}
