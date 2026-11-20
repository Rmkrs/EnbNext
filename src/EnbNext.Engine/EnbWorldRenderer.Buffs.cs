using System.Numerics;
using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly record struct BuffGpuKey(EnbAuthoredUiAsset Asset, Matrix4x4 Attachment, bool Highlight);
    private readonly Dictionary<BuffGpuKey,List<(LiveLanePlan Lane, LiveGpuResource Gpu)>> _buffGpu = [];
    private readonly EnbOpenGlBackdropRenderer[] _buffCountRenderers = Enumerable.Range(0,15).Select(_ => new EnbOpenGlBackdropRenderer()).ToArray();
    private (int Width, int Height) _buffGpuSize;
    private Matrix4x4 _buffLayout=Matrix4x4.Identity;
    private void DestroyBuffResources(EnbGlApi? gl)
    {
        foreach (var draws in _buffGpu.Values)
            foreach (var draw in draws) draw.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
        _buffGpu.Clear();
        foreach (var text in _buffCountRenderers) text.Deinitialize(gl);
    }

    // Same framebuffer/camera boundary as cockpit/world: no independently resized buff surface.
    private void RenderBuffs(EnbGlApi gl,int fb,int width,int height,double scaling,uint time,EnbBuffDraw? buffs)
    {
        if (_buffGpuSize != (width,height) || _buffLayout!=LayoutClip(EnbNext.Presentation.EnbUiGroup.Buffs)) { DestroyBuffResources(gl); _buffGpuSize = (width,height); _buffLayout=LayoutClip(EnbNext.Presentation.EnbUiGroup.Buffs); }
        if (buffs is null||UiLayout?.Visible(EnbUiGroup.Buffs)==false) { if (_buffGpu.Count != 0) DestroyBuffResources(gl); return; }
        if (_retailAssetsAccessor() is not { } assets) return;
        // 00945440 restores the UI camera's 0..1 depth range after world/planet rendering.
        ApplyOrbitalDepthRange(gl, false, 1);
        var keys = new HashSet<BuffGpuKey> { new(buffs.Frame,Matrix4x4.Identity,false) };
        foreach (var icon in buffs.Icons)
        { keys.Add(new(icon.Asset,icon.Attachment,false)); keys.Add(new(icon.Asset,icon.Attachment,true)); }
        foreach (var key in _buffGpu.Keys.Where(k => !keys.Contains(k)).ToArray())
        {
            foreach (var draw in _buffGpu[key]) draw.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
            _buffGpu.Remove(key);
        }
        // Prebuild both hover states. A pointer move must not trigger texture/geometry compilation.
        foreach (var key in keys) Ensure(key);
        Draw(new(buffs.Frame,Matrix4x4.Identity,false));
        foreach (var icon in buffs.Icons)
        {
            Draw(new(icon.Asset,icon.Attachment,false),icon.Opacity);
            if (icon.Hovered) Draw(new(icon.Asset,icon.Attachment,true),icon.HighlightOpacity);
        }
        for (int i = 0; i < buffs.Counts.Length; i++)
        {
            var text = buffs.Counts[i]; var r = LayoutRect(EnbNext.Presentation.EnbUiGroup.Buffs,text.Bounds); var renderer = _buffCountRenderers[i];
            if (!renderer.IsInitialized) renderer.Initialize(gl);
            renderer.RenderOverlay(gl,fb,(int)Math.Round(r.X*scaling),height-(int)Math.Round(r.Bottom*scaling),
                Math.Max(1,(int)Math.Round(r.Width*scaling)),Math.Max(1,(int)Math.Round(r.Height*scaling)),text.Raster);
        }
        void Draw(BuffGpuKey key,float? opacity=null)
        {
            foreach (var draw in _buffGpu[key])
            {
                var lane=draw.Lane;
                if(opacity is float alpha && lane.Textured is {} textured)
                {
                    //66C2A0 ->496240 ->92F010 replaces material diffuse alpha;
                    //it does not alter authored blend/shader or vertex alpha.
                    var lighting=(textured.LightingPlan).Copy().SetRunStates(textured.LightingPlan.RunStates.Select(r=>
                        (r).Copy().SetMaterial(r.Material with {DiffuseAlpha=alpha})).ToImmutableArray());
                    lane=LiveLanePlan.From((textured).Copy().SetLightingPlan(lighting).SetRunStates(textured.RunStates.Select((r,i)=>
                        r with {LightingRun=lighting.RunStates[i]}).ToImmutableArray()));
                }
                SubmitGpuResource(gl,fb,width,height,draw.Gpu,lane,new(false,0,0,0),default,null,time);
            }
        }
        void Ensure(BuffGpuKey key)
        {
            if (_buffGpu.ContainsKey(key)) return;
            var draws = new List<(LiveLanePlan Lane,LiveGpuResource Gpu)>();
            _buffGpu.Add(key,draws);
            var orientation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(0,0,-1,0,-1,0,0,0,0,1,0,0,0,0,0,1));
            if (!EnbResolvedRenderCamera.TryCreate(new(0,0,320),orientation,new(width,height,1),Math.PI/2,4d/3,new(300,1000),out var camera))
                throw new InvalidOperationException("Invalid buff HUD camera.");
            var transforms = EnbAuthoredUiAsset.ResolveTransforms(key.Asset);
            var textures = new Dictionary<string,EnbTextureImage>();
            foreach (var mesh in key.Asset.Resource.Meshes.OrderByDescending(m => m.StaticSort.RuntimeLevel)
                .ThenBy(m => key.Asset.Hierarchy.Pivots.First(p => p.Name == m.Name).Ordinal))
            {
                var pivot = key.Asset.Hierarchy.Pivots.First(p => p.Name == mesh.Name);
                var transform = transforms[pivot.Ordinal]*key.Attachment;
                foreach (var authored in mesh.MaterialPasses)
                {
                    var pass = EnbCurrentRetailVerbPresentation.Apply(authored,key.Highlight,false);
                    var packet = new EnbW3dDrawTask(new(EnbRenderPassKind.ModernOverlayBoundary,EnbW3dDrawBlendClass.Blended,
                        mesh.StaticSort,key.Asset.Resource.Key,mesh.Ordinal,pass.Ordinal,1,0),1,1,0,transform,
                        new(transform.Translation,mesh.BoundingRadius),mesh,null,new(pass,pass.SingleBindingSemantics),
                        EnbW3dLightEnvironment.BlackNoLights,time,key.Asset.Hierarchy.Name,pivot.Ordinal,mesh.Name);
                    if (!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out var error) ||
                        !TryCompileLane(submission,camera,EnbRenderTextureRuntimeSettings.RetailDefaults,
                            EnbRenderTextureBackendCapabilities.ModernDefault,assets.Textures,textures,null,LiveLaneKind.Textured,out var lane,out error) ||
                        !TryCreateGpuResource(gl,lane,LiveLaneKind.Textured,0,out var gpu,out error))
                        throw new InvalidOperationException("Buff HUD GPU: " + error);
                    if(_buffLayout!=Matrix4x4.Identity)lane.Textured!.LightingPlan.CanonicalClipFromModel*=_buffLayout;
                    draws.Add((lane,gpu));
                }
            }
        }
    }
}
