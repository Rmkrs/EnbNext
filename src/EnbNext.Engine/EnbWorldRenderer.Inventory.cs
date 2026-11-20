using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private EnbWorldRenderer? _inventoryModelRenderer;
    private bool _customizationPreview;
    private EnbW3dLightEnvironment? _customizationLightEnvironment;
    private readonly EnbOpenGlScannerFramebuffer _inventoryModelFramebuffer = new();
    public string? InventoryPreviewDiagnostic {get;private set;}
    private float _inventoryCameraDistance;
    private readonly EnbSceneFrame _inventoryScene = new();
    private readonly EnbSampledSceneObject _inventoryShip = new();
    private IEnbRenderObject[]? _inventoryObjects;
    private EnbResolvedRenderCamera? _inventoryPreviewCamera;

    private void RenderEquipmentPreview(EnbGlApi gl, int fb, int width, int height, double scaling, EnbUiRect bounds, IEnbSceneFrame scene, Vector3 view,bool transparent=false,Vector2 pan=default,float spin=0,Quaternion? rotation=null,EnbUiRect? clip=null)
    {
        var ship = scene.Objects.FirstOrDefault(o => o.IsLocalShip);
        if (ship?.Spatial is not { } spatial) return;
        int x=(int)Math.Round(bounds.X*scaling),y=height-(int)Math.Round(bounds.Bottom*scaling);
        int w=(int)Math.Round(bounds.Width*scaling),h=(int)Math.Round(bounds.Height*scaling);
        if(w<1||h<1||x<0||y<0||x+w>width||y+h>height)return;
        _inventoryModelRenderer ??= CreatePreviewModelRenderer(gl);
        _inventoryModelRenderer._customizationPreview=transparent;
        _inventoryModelRenderer._customizationLightEnvironment=null;
        var sphere=new EnbW3dWorldBounds(Vector3.Zero,Math.Max(1,EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(ship)));
        bool fitted=_inventoryModelRenderer.ProjectedObjectBounds.RenderSpheresByRenderIdentity.TryGetValue(ship.RenderIdentity,out var previousSphere);
        if(fitted)sphere=previousSphere;
        var forward=Vector3.Normalize(new Vector3(-1,-1,-1));
        var left=Vector3.Normalize(Vector3.Cross(Vector3.UnitZ,forward));
        var up=Vector3.Cross(forward,left);
        var orientation=Quaternion.CreateFromRotationMatrix(new(forward.X,forward.Y,forward.Z,0,left.X,left.Y,left.Z,0,up.X,up.Y,up.Z,0,0,0,0,1));
        if(!transparent)
        {
            orientation=Quaternion.Normalize(Quaternion.Concatenate(rotation??Quaternion.Identity,orientation));
            forward=Vector3.Transform(Vector3.UnitX,orientation);
        }
        // Retail 00542250 gives the preview wrapper its own transform;
        // 004800A0 installs that complete transform on the model for drawing.
        // A retained world-provider matrix must not override this local pose.
        var previewOrientation=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,spin);
        _inventoryShip.AdoptViewObject(ship);
        var pose=_inventoryShip.Pose;
        _inventoryShip.Pose=pose with {Sample=pose.Sample with {Position=Vector3.Zero,Orientation=previewOrientation,
            OrientationColumn0=Vector3.Transform(Vector3.UnitX,previewOrientation),ProviderTransform=null,Velocity=Vector3.Zero}};
        ship=_inventoryShip;
        _inventoryScene.AdoptMetadata(scene);
        _inventoryScene.Objects=_inventoryObjects??=[_inventoryShip];
        _inventoryScene.Effects=[];_inventoryScene.StationAvatars=[];
        scene=_inventoryScene;
        // Collect the assembled LEGO ship's geometry sphere before fitting the preview.
        // Base-asset radar radii do not include all installed wings and engines.
        for(int pass=0;pass<(fitted?1:2);pass++)
        {
            float radius=Math.Max(1,sphere.Radius);
            if(transparent)
            {
                forward=Vector3.Normalize(new Vector3(-128,100,-radius*2.1f));
                left=Vector3.Normalize(Vector3.Cross(Vector3.UnitZ,forward));up=Vector3.Cross(forward,left);
                var basis=Quaternion.CreateFromRotationMatrix(new(forward.X,forward.Y,forward.Z,0,left.X,left.Y,left.Z,0,up.X,up.Y,up.Z,0,0,0,0,1));
                var orbit=Quaternion.Normalize(Quaternion.Concatenate(rotation??Quaternion.Identity,basis));
                forward=Vector3.Transform(Vector3.UnitX,orbit);left=Vector3.Transform(Vector3.UnitY,orbit);up=Vector3.Transform(Vector3.UnitZ,orbit);
                orientation=Quaternion.CreateFromRotationMatrix(new(forward.X,forward.Y,forward.Z,0,left.X,left.Y,left.Z,0,up.X,up.Y,up.Z,0,0,0,0,1));
            }
            // Equipment's ShipPIP uses CameraCtor's 50-degree / 4:3 perspective,
            // centered on the model origin, with distance 37.5..150 (00542EE0).
            if(!EnbResolvedRenderCamera.TryCreate(-forward*(75*view.Z),orientation,new(w,h,1),50*Math.PI/180,4.0/3,new(1,1000),out var camera))return;
            if(transparent && !EnbResolvedRenderCamera.TryCreate(sphere.Center+left*pan.X+up*(pan.Y-3)-forward*(new Vector3(128,-100,radius*2.1f).Length()*view.Z),orientation,new(w,h,1),2*Math.Atan(2.0/3),4.0/3,new(.1f,Math.Max(1000,radius*20)),out camera))return;
            InventoryPreviewDiagnostic=$"sphere={sphere} camera={camera.Position} forward={camera.Basis.Forward} transparent={transparent}";
            _inventoryPreviewCamera=camera;
            _inventoryCameraDistance=transparent?new Vector3(128,-100,radius*2.1f).Length()*view.Z:75*view.Z;
            var light=new EnbSceneLightSnapshot(0,0,Vector3.One,Vector3.One,new(.3f),camera.Position,camera.Basis.Forward,
                1_000_000,1,1,0,0,MathF.PI/4,MathF.PI/4);
            _inventoryScene.Lighting=new(0x00333333,[light]);
            if(transparent)
            {
                Vector3 Direction(Vector3 p)=>camera.Basis.Right*p.X+camera.Basis.Up*p.Y-camera.Basis.Forward*p.Z;
                _inventoryModelRenderer._customizationLightEnvironment=EnbW3dLightEnvironment.CreateCurrentRetail(0,[
                    new(true,new(EnbW3dLightType.Directional,new(3),Vector3.Zero,Vector3.Zero,Vector3.Zero,Direction(new(1,-1,-1)),1000000,1,1,0,0,0,0)),
                    new(true,new(EnbW3dLightType.Directional,new Vector3(.06f,.42f,.83f)*1.3f,Vector3.Zero,Vector3.Zero,Vector3.Zero,Direction(new(-1,1,1)),1000000,1,1,0,0,0,0))]);
            }
            var frame=new LiveFrame(scene,camera,false,scene.ClientTime,System.Diagnostics.Stopwatch.GetTimestamp(),null,0,
                System.Collections.Immutable.ImmutableDictionary<LivePlanetGpuIdentity,float>.Empty,
                System.Collections.Immutable.ImmutableDictionary<LivePlanetGpuIdentity,uint>.Empty)
                { RslObjectUpdateOverrides=MapWorldRenderer?._frameRslObjectUpdates };
            int target=_inventoryModelFramebuffer.Begin(gl,fb,x,y,w,h);
            try
            {
                // Begin preserves the authored FRAME_DETAIL color beneath the ship.
                _inventoryModelRenderer.RenderFrame(gl,target,w,h,frame);
                if(_inventoryModelRenderer.ProjectedObjectBounds.RenderSpheresByRenderIdentity.TryGetValue(ship.RenderIdentity,out var actual))sphere=actual;
            }
            finally
            {
                int bottomCrop=clip is {} c?Math.Clamp(height-(int)Math.Round(c.Bottom*scaling)-y,0,h):0;
                _inventoryModelFramebuffer.End(gl,fb,x,y,bottomCrop);gl.Viewport(0,0,width,height);
            }
        }
    }
}
