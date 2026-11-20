using Point = EnbNext.Presentation.EnbUiPoint;
using Rect = EnbNext.Presentation.EnbUiRect;
using Size = EnbNext.Presentation.EnbUiSize;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Engine;

internal sealed partial class EnbWorldController
{
    private readonly EnbLandCameraRuntime _landCamera=new();
    private EnbRetailAssetBundle? _landingAssets;
    private EnbRetailIniDocument? _landingIni;
    private ulong _landingEffectGeneration;
    private Net7LandCameraTransition? _landingEffectRequest;
    private ulong _landingEffectOwner;
    public Func<ulong,ulong,uint,Net7LandCameraPhase,Task<bool>>? LandCameraCompletion {get;set;}

    private EnbCameraPoseOverride? UpdateLandCamera(IEnbSceneFrame snapshot,IEnbRenderObject ship,uint now)
    {
        var request=snapshot.Camera.LandTransition;
        if(request is null){_landCamera.Reset();_landingEffectRequest=null;return null;}
        var assets=_retailAssetsAccessor();
        if(!ReferenceEquals(_landingAssets,assets))
        {
            _landingAssets=assets;
            var path=assets is null?null:Path.Combine(assets.Paths.ClientIniDirectory,"landingpattern.ini");
            _landingIni=path is not null && EnbNext.Installation.EnbPathCase.FileExists(path)?EnbRetailIniDocument.Load(path):EnbRetailIniDocument.Parse("");
        }
        var terrain=_w3dWorld.TerrainCameraData;
        if(request.Phase==Net7LandCameraPhase.SurfaceArrival && snapshot.Camera.LandCameraEnabled &&
            (terrain is null || terrain.Sector!=snapshot.Environment?.SectorNum))return null;
        var target=snapshot.Objects.FirstOrDefault(o=>o.GameId==request.TargetId);
        float radius=target is null?0:_w3dWorld.ProjectedObjectBounds.DistanceRadiiByRenderIdentity.GetValueOrDefault(target.RenderIdentity,
            EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(target));
        var pose=_landCamera.Update(request,ship,target,radius,
            EnbRetailLandingPattern.Resolve(_landingIni??EnbRetailIniDocument.Parse(""),snapshot.Environment?.SectorNum??0),
            terrain is null?null:EnbRetailTerrainSampler.For(terrain).Height,now,snapshot.Camera.LandCameraEnabled,out bool complete,
            _w3dWorld.ProjectedObjectBounds.LocalSphereCenters.GetValueOrDefault(ship.RenderIdentity));
        if(_landingEffectRequest!=request || _landingEffectOwner!=ship.RenderIdentity)
        {_landingEffectRequest=request;_landingEffectOwner=ship.RenderIdentity;_landingEffectGeneration++;}
        if(complete)_retailCamera.Enqueue(request.Phase == Net7LandCameraPhase.SurfaceArrival ? 14 : 9);
        return pose;
    }
    private async Task CompleteLandCameraAsync(ulong owner,Net7LandCameraTransition request)
    {
        try
        {
            bool completed=LandCameraCompletion is {} callback && await callback(owner,request.Revision,request.TargetId,request.Phase);
            _cameraControlStatus=completed?"Landing camera completed.":"Landing camera completion was not sent.";
        }
        catch(Exception ex){_cameraControlStatus="Landing camera completion failed: "+ex.Message;}
    }
    private EnbSceneFrame ApplyLandCamera(EnbSceneFrame snapshot,ulong owner,int poseSlot)
    {
        if(_landCamera.ShipTransform is not {} transform)return snapshot;
        snapshot.ApplyTransform(owner,transform);
        var ship=snapshot.Objects.First(o=>o.RenderIdentity==owner);
        var effects=snapshot.Effects;
        if(_landCamera.Active)
        {
            uint id=uint.MaxValue;
            while(effects.Any(e=>e.EffectId==id))id--;
            var effect=new EnbSceneEffectSnapshot(id,_landingEffectGeneration,ship.GameId,ship.GameId,362,_landCamera.StartTime,
                4000,"",0,false,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
            effects=effects.Add(effect);
        }
        snapshot.Effects=effects;return snapshot;
    }
}
