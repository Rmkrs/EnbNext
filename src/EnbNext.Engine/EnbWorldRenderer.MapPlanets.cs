using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<(ulong Identity,ulong Visual,int Asset,float Scale),EnbOpenGlMapPlanetImage> _mapPlanets=[];
    private (uint? Session,ulong? Ship,uint? Sector) _mapPlanetOwner;
    private EnbWorldRenderer? _mapPlanetRenderer;
    private EnbOpenGlBackdropRenderer? _mapPlanetCompositor;

    private readonly EnbSceneFrame _mapPlanetScene=new();
    private readonly EnbSampledSceneObject _mapPlanetObject=new();
    private IEnbRenderObject[]? _mapPlanetObjects;

    private void DestroyMapPlanets(EnbGlApi? gl,bool preserveScene=false)
    {
        foreach(var image in _mapPlanets.Values) image.Deinitialize(gl);
        _mapPlanets.Clear(); if(!preserveScene)_mapPlanetOwner=default;
        _mapPlanetRenderer?.ReleaseGpuContext(gl); if(!preserveScene)_mapPlanetRenderer=null;
        _mapPlanetCompositor?.Deinitialize(gl); _mapPlanetCompositor=null;
    }

    private void RenderMapPlanets(EnbGlApi gl,int fb,int width,int height,EnbMapDraw p)
    {
        if(_mapPlanetOwner!=p.ModelOwner)
        {
            DestroyMapPlanets(gl); _mapPlanetOwner=p.ModelOwner;
        }
        foreach(var obj in p.ModelScene!.Objects.Where(o=>o.ObjectType==3))
        {
            var key=(obj.RenderIdentity,obj.VisualGeneration,obj.BaseAsset,obj.Scale);
            if(!_mapPlanets.TryGetValue(key,out var image))
            {
                if(_mapPlanetCompositor is null) { _mapPlanetCompositor=new(); _mapPlanetCompositor.InitializeMapTexture(gl); }
                if(_mapPlanetRenderer is null)
                {
                    _mapPlanetRenderer=CreatePreviewModelRenderer(gl);MapModelProgramInitializations++;
                    _mapPlanetRenderer.FrustumCullingEnabled=false;
                }
                // A replacement visual invalidates only this object's retained image.
                foreach(var stale in _mapPlanets.Keys.Where(k=>k.Identity==obj.RenderIdentity).ToArray())
                { _mapPlanets[stale].Deinitialize(gl); _mapPlanets.Remove(stale); }
                image=new();
                try
                {
                    if(!CaptureMapPlanet(gl,image,obj,p.ModelScene)) { image.Deinitialize(gl); continue; }
                    _mapPlanets.Add(key,image);
                }
                catch { image.Deinitialize(gl); throw; }
            }
            var r=p.ContentBounds;
            ScissorMap(gl,r,width,height);
            gl.Enable(0x0C11);
            var position=obj.Spatial!.Transform.Position;
            float radius=EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(obj)*p.WorldScale;
            float x=(float)(r.X+r.Width/2)+(position.X-p.WorldCenter.X)*p.WorldScale;
            float y=(float)(r.Y+r.Height/2)-(position.Y-p.WorldCenter.Y)*p.WorldScale;
            if(p.ModelPositions.TryGetValue(obj.GameId,out var at)){x=at.X;y=at.Y;}
            var placed=(UiLayout?.Placement(EnbUiGroup.Map)??EnbUiPlacement.Identity).Apply(new EnbUiRect(x-radius,y-radius,radius*2,radius*2),new(640,480));int left=(int)Math.Round(placed.Left*width/640),right=(int)Math.Round(placed.Right*width/640);
            int top=(int)Math.Round(placed.Top*height/480),bottom=(int)Math.Round(placed.Bottom*height/480);
            _mapPlanetCompositor!.RenderMapTexture(gl,fb,left,height-bottom,Math.Max(1,right-left),Math.Max(1,bottom-top),image.Texture,image.WhiteTexture);
        }
        gl.BindFramebuffer(EnbGlConstants.GL_FRAMEBUFFER,fb); gl.Viewport(0,0,width,height);
    }

    private bool CaptureMapPlanet(EnbGlApi gl,EnbOpenGlMapPlanetImage image,IEnbRenderObject obj,IEnbSceneFrame scene)
    {
        // 007BEF50 constructs the model image once with the root at the origin.
        // Camera-aligned planet meshes must see that camera, never map pan coordinates.
        _mapPlanetObject.AdoptViewObject(obj);
        var pose=_mapPlanetObject.Pose;
        _mapPlanetObject.Pose=pose with {Sample=pose.Sample with {Position=Vector3.Zero,
            Orientation=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,MathF.PI/2),OrientationColumn0=Vector3.UnitY,ProviderTransform=null}};
        obj=_mapPlanetObject;
        _mapPlanetScene.AdoptMetadata(scene);
        _mapPlanetScene.Objects=_mapPlanetObjects??=[_mapPlanetObject];
        _mapPlanetScene.Effects=[];_mapPlanetScene.StationAvatars=[];scene=_mapPlanetScene;
        var renderer=_mapPlanetRenderer!;
        uint mapperTime=MapWorldRenderer?.ReadMapperClock() ?? unchecked((uint)Environment.TickCount);
        renderer.MapperClock=()=>mapperTime;
        var planet=MapWorldRenderer?.PlanetGpuPresentation ?? LivePlanetGpuPresentation.Waiting;
        uint time=scene.ClientTime;
        var orientation=Quaternion.CreateFromRotationMatrix(new Matrix4x4(0,0,-1,0,-1,0,0,0,0,1,0,0,0,0,0,1));
        var sphere=new EnbW3dWorldBounds(Vector3.Zero,EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(obj));
        // First collect the geometry-owned fit sphere, then capture the same time
        // with RObj2Texture's 0.707 sphere crop and 0.01r/10r clip planes.
        for(int pass=0;pass<3;pass++)
        {
            if(pass==1) mapperTime=MapWorldRenderer?.ReadMapperClock() ?? unchecked((uint)Environment.TickCount);
            float radius=sphere.Radius*(pass==0 ? 1 : .70710677f);
            if(!EnbResolvedRenderCamera.TryCreateOrthographic(sphere.Center+new Vector3(0,0,radius),orientation,
                new(EnbOpenGlMapPlanetImage.Size,EnbOpenGlMapPlanetImage.Size,1),radius,radius,new(radius*.01f,radius*10),out var camera))
                throw new InvalidDataException("Map planet camera unavailable.");
            var light=new EnbSceneLightSnapshot(0,0,Vector3.One,Vector3.One,new(.3f),camera.Position+new Vector3(radius*.5f,radius*.5f,0),
                camera.Basis.Forward,1_000_000,1,1,0,0,MathF.PI/4,MathF.PI/4);
            _mapPlanetScene.Lighting=new(0x00333333,[light]);
            var frame=new LiveFrame(scene,camera,false,time,System.Diagnostics.Stopwatch.GetTimestamp(),null,0,
                planet.DayNightPhasesByRenderIdentity,planet.MapperStartMillisecondsByRenderIdentity)
                { RslObjectUpdateOverrides=MapWorldRenderer?._frameRslObjectUpdates };
            int target=image.Begin(gl,white:pass==2);
            if(!renderer.RenderFrame(gl,target,EnbOpenGlMapPlanetImage.Size,EnbOpenGlMapPlanetImage.Size,frame))
                throw new InvalidDataException("Map planet capture failed: "+renderer.StatusText);
            // A mode-0 snapshot may reach the overlay before MainView publishes
            // its generation-qualified phase. Retry; never retain an empty image.
            if(!renderer.PlanetGpuPresentation.Contains(obj)) return false;
            if(pass==0 && renderer.ProjectedObjectBounds.RenderSpheresByRenderIdentity.TryGetValue(obj.RenderIdentity,out var actual)) sphere=actual;
        }
        return true;
    }
}
