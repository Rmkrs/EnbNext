using Point = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Net7.Protocol;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbNavigationMapUi
{
    private uint? _galaxyFocusedSystem, _galaxyFocusedSector;
    private int? _galaxyFocusedGate;
    private Vector3 _galaxySurface, _galaxyGateEntry;
    private bool _galaxyGateSource;
    private uint _galaxySystemPulse;
    private readonly Dictionary<uint,float> _galaxyLineFade = [];
    private uint _galaxyRenderElapsed;
    private Net7GalaxyMapOrder _galaxySystemOrder=Net7GalaxyMapOrder.Empty,
        _galaxySectorOrder=Net7GalaxyMapOrder.Empty, _galaxyGateOrder=Net7GalaxyMapOrder.Empty;
    private uint[] _galaxySectorConstruction=[], _galaxyGateConstruction=[];

    private void RefreshGalaxyOrder()
    {
        var systems=Galaxy.SystemOrder ?? Net7GalaxyMapOrder.Empty.Retain(Galaxy.Systems.Select(s=>s.SystemHashId));
        var sectors=Galaxy.SectorOrder ?? Net7GalaxyMapOrder.Empty.Retain(Galaxy.Sectors.Select(s=>s.SectorHashId));
        var gates=Galaxy.GateOrder ?? Net7GalaxyMapOrder.Empty.Retain(Galaxy.Gates.Select(g=>g.GateHashId));
        var visible=EnbGalaxyMapPresentation.VisibleSectors(Galaxy).Select(s=>s.SectorHashId).ToHashSet();
        _galaxySectorConstruction=sectors.Keys.Where(visible.Contains).ToArray();
        _galaxyGateConstruction=gates.Keys.ToArray();
        _galaxySystemOrder=_galaxySystemOrder.Retain(systems.Keys);
        _galaxySectorOrder=_galaxySectorOrder.Retain(_galaxySectorConstruction);
        _galaxyGateOrder=_galaxyGateOrder.Retain(gates.Keys.Where(key=>Galaxy.Gates.Any(g=>g.GateHashId==key && visible.Contains(g.SectorReferenceA) && visible.Contains(g.SectorReferenceB))));
    }

    private bool GalaxyMainVisible(uint sector) => _galaxyFocusedSector is uint selected ? sector==selected : !_galaxyFocusedGate.HasValue;

    private bool GalaxySectorHighlighted(uint sector,uint system)
    {
        if(_galaxyFocusedSector is uint selected) return selected==sector;
        // Next Modern UI: a sector found with the find box stays lit until something else is pointed at.
        if(_modern && _modernFoundSector==sector && !_galaxyFocusedSystem.HasValue && !_galaxyFocusedGate.HasValue) return true;
        if(_galaxyFocusedSystem is uint selectedSystem) return system==selectedSystem;
        if(_galaxyFocusedGate is int index)
        {
            var gate=Galaxy.Gates[index];
            return _galaxyGateSource ? gate.SectorReferenceA==sector : gate.SectorReferenceB==sector;
        }
        return false;
    }

    private bool GalaxyLinkVisible(int index)
    {
        var gate=Galaxy.Gates[index];
        if(_galaxyFocusedSector is uint selected) return gate.SectorReferenceA==selected;
        if(_galaxyFocusedGate is int focused)
        {
            var current=Galaxy.Gates[focused];
            // 007AD850 explicitly enables the selected link and first reciprocal.
            var reciprocal=_galaxyGateConstruction.Select(key=>Galaxy.Gates.First(g=>g.GateHashId==key))
                .FirstOrDefault(g=>g.SectorReferenceA==current.SectorReferenceB && g.SectorReferenceB==current.SectorReferenceA);
            return index==focused || reciprocal is not null && gate.GateHashId==reciprocal.GateHashId;
        }
        if(_galaxyFocusedSystem.HasValue)
        {
            var from=Galaxy.Sectors.First(s=>s.SectorHashId==gate.SectorReferenceA);
            var to=Galaxy.Sectors.First(s=>s.SectorHashId==gate.SectorReferenceB);
            return from.Field2!=to.Field2;
        }
        return true;
    }

    private float GalaxyLinkOpacity(int index)
    {
        uint key=Galaxy.Gates[index].GateHashId;
        float alpha=_galaxyLineFade.GetValueOrDefault(key);
        // 007B43F0: retained line fade moves by frame milliseconds * .001.
        alpha=Math.Clamp(alpha+(GalaxyLinkVisible(index) ? 1 : -1)*_galaxyRenderElapsed*.001f,0,1);
        _galaxyLineFade[key]=alpha;
        return alpha;
    }

    private void ClearGalaxyFocus()
    {
        _galaxyFocusedSystem=null; _galaxyFocusedSector=null; _galaxyFocusedGate=null;
        _galaxySystemPulse=0;
    }

    private void LocateGalaxy()
    {
        if (!Content.Contains(Design(_pointer)) || _hovered is not null)
        { ClearGalaxyFocus(); return; }
        _galaxySurface=_galaxyCamera.SurfacePoint(GalaxyNormalized(_pointer),Viewport);
        var camera=GalaxyRenderCamera();
        var origin=camera.Position;
        // 007A7560 moves a camera near the map plane back along local Z.
        if(Math.Abs(origin.Z)<25) origin-=camera.Basis.Forward*25;
        var end=origin+2*(_galaxySurface-origin);
        uint? system=null;
        float nearest=float.MaxValue;
        foreach(var item in _galaxySystemOrder.Keys.Select(key=>new KeyValuePair<uint,(Vector3 Minimum,Vector3 Maximum)>(key,_galaxySystemBounds[key])))
        {
            var (min,max)=item.Value;
            if(EnbGalaxyMapPicking.Intersect(origin,end,_galaxySystemSpheres[item.Key],min,max,out var hit))
            {
                float distance=Vector3.DistanceSquared(hit,_galaxySurface);
                if(distance<nearest) { nearest=distance; system=item.Key; }
            }
        }
        if(system!=_galaxyFocusedSystem) { _galaxySystemPulse=system.HasValue ? 1u : 0; _galaxyFocusedGate=null; }
        _galaxyFocusedSystem=system; _galaxyFocusedSector=null;
        var sectors=EnbGalaxyMapPresentation.VisibleSectors(Galaxy);
        if(system.HasValue)
        {
            _galaxyFocusedGate=null; nearest=float.MaxValue;
            foreach(var sector in _galaxySectorConstruction.Select(key=>sectors.First(s=>s.SectorHashId==key)).Where(s=>s.Field2==system))
            {
                string name=sector.Subtype is 7 or 8 ? "sm_planet.w3d" : "sm_sector.w3d";
                if(!_glyphs.TryGetValue(name,out var glyph)) _glyphs[name]=glyph=EnbMapGlyph.Load(_assets!,name);
                // Both authored assets are ordinary single-mesh render objects.
                // Their mesh sphere and AABB are the +F0/+F4 query consumers.
                if(glyph.Resource.Meshes.Count!=1 || glyph.Resource.HierarchicalLods.Count!=0)
                    throw new InvalidDataException("Galaxy sector bounds require the authored single mesh: "+name);
                var mesh=glyph.Resource.Meshes[0];
                float scale=sector.Subtype is 7 or 8 ? 1.5f : 1;
                var transform=(sector.Subtype is 7 or 8 ? _galaxyPlanetBasis : EnbGalaxyMapModelRotation.InitialSector())*
                    Matrix4x4.CreateTranslation(_galaxySectors[sector.SectorHashId]);
                var center=Vector3.Transform((mesh.BoundingBoxMin+mesh.BoundingBoxMax)*(.5f*scale),transform);
                var extent=(mesh.BoundingBoxMax-mesh.BoundingBoxMin)*(.5f*scale);
                var worldExtent=new Vector3(Math.Abs(transform.M11)*extent.X+Math.Abs(transform.M21)*extent.Y,
                    Math.Abs(transform.M12)*extent.X+Math.Abs(transform.M22)*extent.Y,extent.Z);
                var sphere=new Vector4(Vector3.Transform(mesh.BoundingSphereCenter*scale,transform),mesh.BoundingRadius*scale);
                if(EnbGalaxyMapPicking.Intersect(origin,end,sphere,center-worldExtent,center+worldExtent,out var hit))
                {
                    float distance=Vector3.DistanceSquared(hit,_galaxySurface);
                    if(distance<nearest) { nearest=distance; _galaxyFocusedSector=sector.SectorHashId; }
                }
            }
            return;
        }
        float Distance(int index)
        {
            var gate=Galaxy.Gates[index];
            if(!_galaxySectors.TryGetValue(gate.SectorReferenceA,out var from) || !_galaxySectors.TryGetValue(gate.SectorReferenceB,out var to)) return -1;
            return EnbGalaxyMapPicking.LinkDistance(_galaxySurface,from,to);
        }
        float retained=_galaxyFocusedGate is int old ? Distance(old) : -1;
        if(!(retained>0 && retained<1.5f))
        {
            int? gate=null; nearest=1.5f;
            foreach(int i in _galaxyGateOrder.Keys.Select(key=>Galaxy.Gates.ToList().FindIndex(g=>g.GateHashId==key)))
            {
                var link=Galaxy.Gates[i];
                var from=sectors.FirstOrDefault(s=>s.SectorHashId==link.SectorReferenceA);
                var to=sectors.FirstOrDefault(s=>s.SectorHashId==link.SectorReferenceB);
                if(from is null || to is null || from.Field2==to.Field2) continue;
                float distance=Distance(i);
                if(distance>0 && distance<nearest) { nearest=distance; gate=i; }
            }
            if(gate!=_galaxyFocusedGate) _galaxyGateEntry=_galaxySurface;
            _galaxyFocusedGate=gate;
        }
        if(_galaxyFocusedGate is int selected)
        {
            var destination=_galaxySectors[Galaxy.Gates[selected].SectorReferenceB];
            _galaxyGateSource=Vector3.DistanceSquared(_galaxyGateEntry,destination)<Vector3.DistanceSquared(_galaxySurface,destination);
        }
    }

    private void ClickGalaxy(bool right)
    {
        LocateGalaxy();
        if(RoutePlannerEnabled && !right && _galaxyFocusedSector is uint destination && _scene is not null)
        {
            Guidance.Choose(destination);
            Guidance.Update(GalaxyMapAccessor?.Invoke(),_scene);
            return;
        }
        Vector3 center; float radius;
        if(_galaxyFocusedSystem is uint system)
        {
            var sphere=_galaxySystemSpheres[system]; center=new(sphere.X,sphere.Y,sphere.Z);
            radius=Math.Max(sphere.W,_galaxyMinimumRadius);
            if(right) radius*=2;
        }
        else if(_galaxyFocusedGate is int index)
        {
            var gate=Galaxy.Gates[index];
            var from=_galaxySectors[gate.SectorReferenceA]; var to=_galaxySectors[gate.SectorReferenceB];
            center=right ? (from+to)*.5f : _galaxyGateSource ? from : to;
            radius=right ? Vector3.Distance(from,to)*.5f : _galaxyLargestSystemRadius;
        }
        else { center=right ? _galaxyCenter : _galaxySurface; radius=right ? _galaxyRadius : _galaxyLargestSystemRadius; }
        _galaxyCamera.Frame(center,Math.Max(radius,_galaxyMinimumRadius),right ? EnbGalaxyMapCamera.HomePitch : EnbGalaxyMapCamera.ClickPitch,Viewport,false);
    }
}
