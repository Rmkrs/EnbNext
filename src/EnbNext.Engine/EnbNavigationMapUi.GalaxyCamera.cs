using Point = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbNavigationMapUi
{
    private EnbGalaxyMapCamera _galaxyCamera = new();
    private readonly Dictionary<uint,(Vector3 Minimum,Vector3 Maximum)> _galaxySystemBounds = [];
    private readonly Dictionary<uint,Vector4> _galaxySystemSpheres = [];
    private Vector2 _galaxyMinimum, _galaxyMaximum;
    private Vector3 _galaxyCenter;
    private float _galaxyRadius, _galaxyLargestSystemRadius, _galaxyMinimumRadius = 10;
    private bool _galaxyCameraInitialized;

    private void BuildGalaxyBounds()
    {
        _galaxySystemBounds.Clear(); _galaxySystemSpheres.Clear(); ClearGalaxyFocus();
        _galaxyMinimumRadius = 10; // 007A6020; optional [Render] setting.
        if (_galaxyIni!.TryGetSection("Render",out var render) && render.TryGetValue("SystemZoomMinRadius",out var value) &&
            float.TryParse(value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out float radius))
            _galaxyMinimumRadius = radius;
        _galaxyMinimum = new(float.MaxValue); _galaxyMaximum = new(-float.MaxValue);
        _galaxyLargestSystemRadius = 1;
        var sectors = EnbNext.Presentation.EnbGalaxyMapPresentation.VisibleSectors(Galaxy);
        foreach (var system in Galaxy.Systems)
        {
            var points = sectors.Where(s=>s.Field2==system.SystemHashId).Select(s=>_galaxySectors[s.SectorHashId]).ToArray();
            Vector3 min,max;
            if (points.Length == 0) min=max=_galaxySystems[system.SystemHashId]+new Vector3(-.5f,-5,0);
            else
            {
                min=points.Aggregate(Vector3.Min)-new Vector3(1,1,0);
                max=points.Aggregate(Vector3.Max)+new Vector3(1,1,0);
                var margin=(max-min)*.125f; min-=margin; max+=margin;
                min.Z=max.Z=_galaxySystems[system.SystemHashId].Z;
            }
            _galaxySystemBounds[system.SystemHashId]=(min,max);
            // DynamicMeshModel +14 -> 0092C590 recomputes the sphere with
            // the shared mesh inflation, before 009421B0 transforms its center.
            var localMin=Vector3.Zero; var localMax=Vector3.Zero; var center=Vector3.Zero; float r=0; uint flags=1;
            var offset=_galaxySystems[system.SystemHashId];
            EnbCurrentRetailMeshGeometryRuntime.CalculateBounds([min-offset,max-offset],ref localMin,ref localMax,ref center,ref r,ref flags);
            center+=offset; _galaxySystemSpheres[system.SystemHashId]=new(center,r);
            _galaxyLargestSystemRadius=Math.Max(_galaxyLargestSystemRadius,r);
            var extent=new Vector2(r+_galaxyMinimumRadius);
            _galaxyMinimum=Vector2.Min(_galaxyMinimum,new Vector2(center.X,center.Y)-extent);
            _galaxyMaximum=Vector2.Max(_galaxyMaximum,new Vector2(center.X,center.Y)+extent);
        }
        if (_galaxySystemBounds.Count==0) { _galaxyCenter=default; _galaxyRadius=0; return; }
        var half=(_galaxyMaximum-_galaxyMinimum)*.5f;
        _galaxyCenter=new(_galaxyMinimum+half,0); _galaxyRadius=Math.Max(half.X,half.Y);
    }

    private void FrameGalaxyHome(bool immediate)
    {
        AdvanceGalaxyModels(); // 005404E0 updates before framing home.
        if (_galaxySystemBounds.Count==0) return;
        var center=_galaxyCenter; float radius=_galaxyRadius;
        var home=Galaxy.Sectors.FirstOrDefault(s=>s.SectorHashId==_scene?.Environment?.SectorNum);
        if (home is not null && _galaxySystemBounds.TryGetValue(home.Field2,out var bounds))
        {
            var sphere=_galaxySystemSpheres[home.Field2];
            center=new(sphere.X,sphere.Y,sphere.Z);
            radius=Math.Max(_galaxyMinimumRadius,sphere.W);
        }
        _galaxyCamera.Frame(center,radius,EnbGalaxyMapCamera.HomePitch,Viewport,immediate);
        _galaxyCameraInitialized=true;
    }

    private Vector2 GalaxyNormalized(Point point)
    {
        var d=Design(point)-Content.TopLeft;
        return new((float)d.X/Viewport.X,(float)d.Y/Viewport.Y);
    }

    private EnbResolvedRenderCamera GalaxyRenderCamera()
        => _galaxyCamera.Resolve(Viewport,new((int)Math.Max(2,Viewport.X),(int)Math.Max(2,Viewport.Y),1));

    private Vector2 ProjectGalaxy(Vector3 point)
    {
        var camera=GalaxyRenderCamera();
        if (!camera.Projection.TryProjectViewPoint(camera.WorldToCanonicalView(point),out var ndc))
            return new(float.NaN);
        return new((float)Content.X+(ndc.X+1)*Viewport.X*.5f,
            (float)Content.Y+(1-ndc.Y)*Viewport.Y*.5f);
    }
}
