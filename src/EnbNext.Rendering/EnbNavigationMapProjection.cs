using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Local XY map coordinates; deliberately independent of flight and network state.</summary>
public sealed class EnbNavigationMapProjection
{
    private Vector2 minimum, maximum, configuredViewport;
    private float baseScale = 1, currentScale = 1;
    private bool boundsChanged;
    private readonly Dictionary<ulong, Vector3> presentedPositions = [];
    private readonly HashSet<ulong> presentedThisRefresh = [];
    public Vector2 Center { get; private set; }
    public Vector2 ScaledCenter { get; private set; }
    public Vector2 Extent { get; private set; } = Vector2.One;
    public float Zoom { get; private set; } = 1;
    public uint? LockedObjectId { get; private set; }
    public bool IsInitialized { get; private set; }
    /// <summary>The sector extent the map fits and clamps its centre to.</summary>
    public Vector2 Minimum => minimum;
    public Vector2 Maximum => maximum;
    /// <summary>
    /// Next Modern UI map: the view never leaves the sector. The furthest zoom out shows the whole sector, and the
    /// centre is clamped so the viewport stays inside it. Off (the retail map): zoom out to half and centre anywhere
    /// in the sector.
    /// </summary>
    public bool Bounded
    {
        get => bounded;
        set { if (bounded == value) return; bounded = value; if (value && IsInitialized && Zoom < 1) SetZoom(1); else if (IsInitialized) ApplyCenter(Center); }
    }
    private bool bounded;
    /// <summary>Next: a share of the sector's size added on every side when fitting, so edge markers are not cut off.</summary>
    public float FitMargin { get; set; }
    private float MinimumZoom => bounded ? 1 : .5f;

    public void Fit(IReadOnlyList<IEnbRenderObject> objects)
    {
        Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (var obj in objects)
        {
            if (!obj.IsLocalShip && obj.Radar.Navigation is null || obj.Spatial?.Transform is not { } t || !Finite(t.Position)) continue;
            float radius = EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(obj);
            if (!float.IsFinite(radius) || radius < 0) continue;
            var p = new Vector2(t.Position.X,t.Position.Y);
            min = Vector2.Min(min,p-new Vector2(radius)); max = Vector2.Max(max,p+new Vector2(radius));
        }
        if (!float.IsFinite(min.X)) return;
        if (FitMargin > 0) { var margin = (max-min)*FitMargin; min -= margin; max += margin; }
        FitBounds(min,max);
    }

    public float Scale(Vector2 viewport)
    {
        if (!IsInitialized) return Math.Min(viewport.X/Extent.X,viewport.Y/Extent.Y)*Zoom;
        if (viewport != configuredViewport)
        {
            configuredViewport = viewport;
            RefreshScale(false); // 007C0C80 viewport changes preserve zoom.
        }
        return currentScale;
    }
    public void FitBounds(Vector2 minimum, Vector2 maximum)
    {
        this.minimum=minimum; this.maximum=maximum;
        Center=(minimum+maximum)*.5f; Extent=Vector2.Max(maximum-minimum,Vector2.One);
        Zoom=1; LockedObjectId=null; IsInitialized=true;
        currentScale=baseScale;
        ScaledCenter=Center*currentScale;
        boundsChanged=false; presentedPositions.Clear(); presentedThisRefresh.Clear();
        var fittedCenter=Center;
        RefreshScale(false);
        ApplyCenter(fittedCenter);
    }
    public void BeginRefresh(Vector2 viewport)
    {
        Scale(viewport);
        if (boundsChanged) RefreshScale(true); // 007C0440, before object updates.
    }
    public void ObservePresentedObject(ulong identity, Vector3 position, float radius)
    {
        if (!IsInitialized || !Finite(position) || !float.IsFinite(radius) || radius<0) return;
        presentedThisRefresh.Add(identity);
        // 007BE970 requests expansion on movement or icon scene admission
        // (007BC1F0), not on every draw or every radius change.
        bool moved=!presentedPositions.TryGetValue(identity,out var previous) || previous!=position;
        presentedPositions[identity]=position;
        if (!moved) return;
        var xy=new Vector2(position.X,position.Y);
        var nextMin=Vector2.Min(minimum,xy-new Vector2(radius));
        var nextMax=Vector2.Max(maximum,xy+new Vector2(radius));
        if (nextMin==minimum && nextMax==maximum) return;
        minimum=nextMin; maximum=nextMax; Extent=maximum-minimum;
        boundsChanged=true; // 007C1280; scale changes on the next refresh.
    }
    public void EndRefresh()
    {
        foreach (ulong identity in presentedPositions.Keys.Where(id=>!presentedThisRefresh.Contains(id)).ToArray())
            presentedPositions.Remove(identity);
        presentedThisRefresh.Clear();
    }
    private void RefreshScale(bool preserveScale)
    {
        if (!IsInitialized || configuredViewport.X<=0 || configuredViewport.Y<=0 || Extent.X<.0001f || Extent.Y<.0001f) return;
        float previousBase=baseScale;
        float nextBase=Math.Min(configuredViewport.X/Extent.X,configuredViewport.Y/Extent.Y);
        if (baseScale!=nextBase)
        {
            var center=ScaledCenter/(currentScale<.0001f ? 1 : currentScale);
            baseScale=nextBase; currentScale=baseScale*Zoom;
            ApplyCenter(center);
        }
        if (preserveScale) SetZoom(Math.Max(MinimumZoom,(previousBase*Zoom)/baseScale));
        boundsChanged=false;
    }
    private Vector2 ClampCenter(Vector2 value)
    {
        if (!IsInitialized) return value;
        if (!bounded || configuredViewport.X<=0 || configuredViewport.Y<=0 || currentScale<.0001f) return Vector2.Clamp(value,minimum,maximum);
        // The viewport's half extent in world units: the centre keeps it inside the sector (centred when it is larger).
        var half=configuredViewport*.5f/currentScale;
        var low=minimum+half; var high=maximum-half;
        return new(low.X<=high.X ? Math.Clamp(value.X,low.X,high.X) : (minimum.X+maximum.X)*.5f,
            low.Y<=high.Y ? Math.Clamp(value.Y,low.Y,high.Y) : (minimum.Y+maximum.Y)*.5f);
    }
    private void ApplyCenter(Vector2 value)
    {
        Center=ClampCenter(value);
        ScaledCenter=Center*currentScale;
    }
    private void SetZoom(float value)
    {
        if (Zoom==value) return;
        var center=IsInitialized ? ScaledCenter/(currentScale<.0001f ? 1 : currentScale) : Center;
        Zoom=value; currentScale=baseScale*Zoom;
        ApplyCenter(center);
    }
    public Vector2 Project(Vector3 world, Vector2 viewport)
    {
        var d = new Vector2(world.X,world.Y)-Center;
        return viewport*.5f + new Vector2(d.X,-d.Y)*Scale(viewport);
    }
    public Vector3 Unproject(Vector2 point, Vector2 viewport)
    {
        if (IsInitialized)
        {
            Scale(viewport);
            return UnprojectNormalized(point/viewport,viewport);
        }
        var d = (point-viewport*.5f)/Scale(viewport);
        return new(Center.X+d.X,Center.Y-d.Y,0);
    }
    private Vector3 UnprojectNormalized(Vector2 point, Vector2 viewport)
    {
        // 007C1200 retains scaled center and multiplies by a reciprocal.
        // Replacing this with world-center plus divided offset changes words.
        float x=(point.X-.5f)*viewport.X+ScaledCenter.X;
        float y=-((point.Y-.5f)*viewport.Y)+ScaledCenter.Y;
        float reciprocal=1/currentScale;
        return new(x*reciprocal,y*reciprocal,0);
    }
    public void Pan(Vector2 screenDelta, Vector2 viewport)
    {
        float scale = Scale(viewport);
        if (!float.IsFinite(scale) || scale <= 0) return;
        if (IsInitialized)
        {
            var world=UnprojectNormalized(new Vector2(.5f)-screenDelta/viewport,viewport);
            ApplyCenter(new(world.X,world.Y));
        }
        else ApplyCenter(Center+new Vector2(-screenDelta.X,screenDelta.Y)/scale);
        LockedObjectId = null;
    }
    public void CenterAt(Vector3 position, uint? objectId = null)
    {
        if (!Finite(position)) return;
        ApplyCenter(new(position.X,position.Y)); LockedObjectId = objectId;
    }
    public void CenterOnLocalShip(Vector3 position, uint objectId, bool normalizeZoom)
    {
        // 007C0260: both center actions retain the local-object lock. The
        // alternate action sets ScaleMult to 0.5; it does not refit the census.
        if (normalizeZoom && IsInitialized) SetZoom(MinimumZoom);
        CenterAt(position, objectId);
    }
    public void Follow(IReadOnlyList<IEnbRenderObject> objects)
    {
        if (LockedObjectId is not uint id) return;
        if (objects.FirstOrDefault(o=>o.GameId==id)?.Spatial?.Transform is { } t) CenterAt(t.Position,id);
        // 007C0440 keeps the lock while lookup is absent, allowing reacquisition.
    }
    public void StepZoom(float amount)
    {
        // StarMapRadarViewStepZoomAnimation 007C1120, floor 0.5; finite guard
        // protects the presentation transform, never validates protocol data.
        float next = Zoom + 2*Zoom*amount;
        if (float.IsFinite(next)) SetZoom(Math.Max(MinimumZoom,next));
    }
    /// <summary>Next Modern UI find: zoom to a level (not below the furthest zoom out).</summary>
    public static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    public static string Icon(byte type, bool unknown) => unknown ? "m_unknown.w3d" : type switch
    {
        0=>"m_npc.w3d",1=>"m_pc.w3d",2=>"m_cap.w3d",3=>"m_planet.w3d",4=>"m_item.w3d",
        9=>"m_misile.w3d",10=>"m_nebula.w3d",11=>"m_gate.w3d",12=>"m_sbase.w3d",
        0x18=>"m_build.w3d",0x1b=>"m_mine.w3d",0x22 or 0x26=>"m_resrce.w3d",
        0x23=>"m_drone.w3d",0x25=>"m_deco.w3d",_=>"m_dead.w3d",
    };
}
