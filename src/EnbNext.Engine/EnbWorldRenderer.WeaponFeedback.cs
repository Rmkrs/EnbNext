using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<string,List<(LiveLanePlan Lane,LiveGpuResource Gpu)>> _weaponOverlayGpu=[];
    private (int Width,int Height) _weaponOverlaySize;
    private EnbRetailAssetBundle? _weaponOverlayAssets;

    private void DestroyWeaponFeedback(EnbGlApi? gl)
    {
        foreach(var draws in _weaponOverlayGpu.Values)
            foreach(var draw in draws)draw.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
        _weaponOverlayGpu.Clear();_weaponOverlayAssets=null;
    }

    private void RenderWeaponFeedback(EnbGlApi gl,int fb,int width,int height,uint time,
        ImmutableArray<EnbCombatIcon> icons)
    {
        if(_retailAssetsAccessor() is not {} assets)return;
        if(_weaponOverlaySize!=(width,height) || !ReferenceEquals(_weaponOverlayAssets,assets))
        {DestroyWeaponFeedback(gl);_weaponOverlaySize=(width,height);_weaponOverlayAssets=assets;}
        foreach(var icon in icons)
        {
            if(icon.CannotUse)Draw("item_noskill.w3d",icon,0,null);
            if(icon.Powerup.HasValue)Draw("itemon01.w3d",icon,0,null);
            if(icon.NoAmmo)Draw("item_noammo.w3d",icon,0,null);
            if(icon.Invalid)Draw("item_dark.w3d",icon,0,null);
            if(icon.WrongTarget)Draw("item_noskill.w3d",icon,0,null);
            if(icon.State.Active)Draw("itemon.w3d",icon,0,null);
            if(icon.State.Frame>=0)Draw("if_targ.w3d",icon,-icon.State.Angle,icon.State.Frame);
            if(icon.State.Cooling)Draw("item_timer.w3d",icon,0,null);
        }
        void Draw(string name,EnbCombatIcon icon,float angle,int? frame)
        {
            Ensure(name);
            var attachment=Matrix4x4.CreateRotationZ(angle)*Matrix4x4.CreateTranslation(icon.Anchor);
            foreach(var draw in _weaponOverlayGpu[name])
            {
                var source=draw.Lane.Textured!;
                var original=source.Submission.Packet;
                var packet=(original).ForkTask().SetFrameTimeMilliseconds(time).SetWorldTransform(original.WorldTransform*attachment).SetCurrentRetailStage0UvOffset(name=="itemon01.w3d" && original.Mesh.Name=="REACTOR_BAR1" &&
                        original.Material.Source.Ordinal==0 && icon.Powerup is float progress
                        ? new Vector2(0,EnbCurrentRetailGaugeRuntime.TextureOffset(progress)) : null).SetCurrentRetailEffectMapper(frame is int f ? EnbW3dMapperRuntimeEvaluation.Identity with
                    {TextureOffset=new((f&3)*.25f,(f>>2)*.25f)} : null);
                var lighting=source.LightingPlan;var clip=original.WorldTransform*attachment*_weaponClip;
                if(UiLayout is {} layout&&!layout.Placement(EnbNext.Presentation.EnbUiGroup.Cockpit).IsIdentity)clip*=LayoutClip(EnbNext.Presentation.EnbUiGroup.Cockpit);
                // The cached GPU vertices remain in authored local coordinates.
                // Slot placement and range rotation are uniform transforms only.
                var lane=LiveLanePlan.From((source).Copy().SetLightingPlan((lighting).Copy().SetSubmission(source.Submission with {Packet=packet}).SetModelToWorld(lighting.ModelToWorld*attachment).SetCanonicalClipFromModel(clip).SetCanonicalViewFromModel(original.WorldTransform*attachment*_weaponView)));
                SubmitGpuResource(gl,fb,width,height,draw.Gpu,lane,new(false,0,0,0),default,null,time);
            }
        }
        void Ensure(string name)
        {
            if(_weaponOverlayGpu.ContainsKey(name))return;
            var resources=assets.RetailMixResources!;
            if(!resources.TryReadResource(name,out var bytes,out var location) || !EnbW3dModel.TryDecode(bytes,out var model) ||
                !EnbAuthoredUiAsset.TryCompile(resources,location,model,model.Hierarchies.FirstOrDefault()?.Name??model.Meshes[0].Name,out var resource))
                throw new InvalidDataException("Installed weapon overlay unavailable: "+name);
            var orientation=Quaternion.CreateFromRotationMatrix(new Matrix4x4(0,0,-1,0,-1,0,0,0,0,1,0,0,0,0,0,1));
            if(!EnbResolvedRenderCamera.TryCreateOrthographic(new(0,0,10),orientation,new(width,height,1),320,240,new(1,20),out var camera))
                throw new InvalidOperationException("Invalid weapon HUD camera.");
            var draws=new List<(LiveLanePlan,LiveGpuResource)>();
            _weaponOverlayGpu.Add(name,draws);
            var hierarchy=model.Hierarchies.FirstOrDefault();
            var transforms=hierarchy is null?[]:EnbAuthoredUiAsset.ResolveTransforms(new(resource,hierarchy));
            var textures=new Dictionary<string,EnbTextureImage>();
            foreach(var mesh in resource.Meshes.OrderByDescending(m=>m.StaticSort.RuntimeLevel))
            {
                var pivot=hierarchy?.Pivots.FirstOrDefault(p=>p.Name==mesh.Name);
                var transform=pivot is null?Matrix4x4.Identity:transforms[pivot.Ordinal];
                foreach(var pass in mesh.MaterialPasses)
                {
                    var packet=new EnbW3dDrawTask(new(EnbRenderPassKind.ModernOverlayBoundary,EnbW3dDrawBlendClass.Blended,
                        mesh.StaticSort,resource.Key,mesh.Ordinal,pass.Ordinal,1,0),1,1,0,transform,
                        new(transform.Translation,mesh.BoundingRadius),mesh,null,new(pass,pass.SingleBindingSemantics),
                        EnbW3dLightEnvironment.BlackNoLights,time,hierarchy?.Name,pivot?.Ordinal??0,mesh.Name);
                    if(!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out var error) ||
                        !TryCompileLane(submission,camera,EnbRenderTextureRuntimeSettings.RetailDefaults,
                            EnbRenderTextureBackendCapabilities.ModernDefault,assets.Textures,textures,null,LiveLaneKind.Textured,out var lane,out error) ||
                        !TryCreateGpuResource(gl,lane,LiveLaneKind.Textured,0,out var gpu,out error))
                        throw new InvalidDataException("Weapon HUD GPU: "+error);
                    // Recover camera-only matrices from the compiled authored transform.
                    Matrix4x4.Invert(transform,out var inverse);
                    _weaponClip=inverse*lane.Textured!.LightingPlan.CanonicalClipFromModel;
                    _weaponView=inverse*lane.Textured.LightingPlan.CanonicalViewFromModel;
                    draws.Add((lane,gpu));
                }
            }
        }
    }
    private Matrix4x4 _weaponClip,_weaponView;
}
