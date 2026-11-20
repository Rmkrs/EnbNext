using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private EnbCockpitDraw? _cockpitPresentation;
    private EnbAuthoredUiAsset? _cockpitAsset;
    private (int Width, int Height) _cockpitSize;
    private Matrix4x4 _cockpitLayout=Matrix4x4.Identity;
    private readonly List<(EnbW3dOverlayDrawOwner Owner, LiveGpuResource Gpu)> _cockpitDraws = [];
    private readonly Dictionary<string, List<(LiveLanePlan Lane, LiveGpuResource Gpu)>> _bankHighlights = [];
    private readonly EnbOpenGlBackdropRenderer[] _cockpitText = [new(), new()];
    private readonly EnbOpenGlBackdropRenderer[] _shortcutImages = Enumerable.Range(0,14).Select(_ => new EnbOpenGlBackdropRenderer()).ToArray();
    private readonly EnbOpenGlBackdropRenderer _shortcutHighlight = new();
    private EnbAuthoredUiAsset? _shortcutGpuAsset;
    private ulong? _cockpitOwner;
    private uint _cockpitTime;
    private float _energyTrail, _cockpitHullTrail, _cockpitShieldTrail;
    private readonly EnbRetailGaugeTrail _energyGauge=new(),_cockpitHullGauge=new(),_cockpitShieldGauge=new();

    internal bool CockpitReplaced {get;set;}
    public void SetCockpitPresentation(EnbCockpitDraw? presentation)
    {
        ObserveGadgetFlashes("cockpit", CockpitFlashes(presentation));
        Volatile.Write(ref _cockpitPresentation, presentation);
    }

    private void DestroyCockpitResources(EnbGlApi? gl)
    {
        DestroyBuffResources(gl);
        DestroyWeaponFeedback(gl);
        foreach (var draw in _cockpitDraws) draw.Gpu.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
        _cockpitDraws.Clear(); _cockpitAsset = null;
        foreach (var draws in _bankHighlights.Values)
            foreach (var draw in draws) draw.Gpu.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
        _bankHighlights.Clear();
        foreach (var text in _cockpitText) text.Deinitialize(gl);
        foreach (var image in _shortcutImages) image.Deinitialize(gl);
        _shortcutHighlight.Deinitialize(gl);
        _shortcutGpuAsset = null;
    }

    private void RenderCockpit(EnbGlApi gl, int fb, int width, int height, double scaling, uint time)
    {
        if (Volatile.Read(ref _cockpitPresentation) is not { } p || _retailAssetsAccessor() is not { } assets ||
            (int)Math.Round(p.Size.Width * scaling) != width || (int)Math.Round(p.Size.Height * scaling) != height) return;
        if(UiLayout?.Visible(EnbUiGroup.Cockpit)==false){RenderBuffs(gl,fb,width,height,scaling,time,LayoutBuffPreview?.Invoke());return;}
        // Modern UI replaces the cockpit; its buff row stays.
        // Modern UI replaces the cockpit, its buffs included (they show under the unit frames).
        if(CockpitReplaced){RenderBuffs(gl,fb,width,height,scaling,time,null);return;} if (_cockpitOwner != p.Owner || !ReferenceEquals(_cockpitAsset, p.Asset))
        { _energyGauge.Reset(p.Energy);_cockpitHullGauge.Reset(p.Hull);_cockpitShieldGauge.Reset(p.Shield); _energyTrail = p.Energy; _cockpitHullTrail = p.Hull; _cockpitShieldTrail = p.Shield; _cockpitTime = time; _cockpitOwner = p.Owner; }
        uint elapsed = unchecked(time - _cockpitTime); _cockpitTime = time;
        _energyTrail = _energyGauge.Update(p.Energy, elapsed);
        _cockpitHullTrail = _cockpitHullGauge.Update(p.Hull, elapsed);
        _cockpitShieldTrail = _cockpitShieldGauge.Update(p.Shield, elapsed);
        if (!ReferenceEquals(_cockpitAsset, p.Asset) || !ReferenceEquals(_shortcutGpuAsset,p.Shortcuts?.Asset) || _cockpitSize != (width, height) || _cockpitLayout!=LayoutClip(EnbNext.Presentation.EnbUiGroup.Cockpit))
        {
            DestroyCockpitResources(gl);
            _cockpitLayout=LayoutClip(EnbNext.Presentation.EnbUiGroup.Cockpit);
            var orientation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(0,0,-1,0,-1,0,0,0,0,1,0,0,0,0,0,1));
            if (!EnbResolvedRenderCamera.TryCreateOrthographic(new(0,0,10), orientation,
                new(width,height,1), 320, 240, new(1,20), out var camera)) throw new InvalidOperationException("Invalid cockpit camera.");
            var decodedTextures = new Dictionary<string, EnbTextureImage>();
            foreach (var asset in p.Shortcuts is { } shortcuts ? new[] { shortcuts.Asset, p.Asset } : new[] { p.Asset })
            {
            var transforms = EnbAuthoredUiAsset.ResolveTransforms(asset);
            foreach (var mesh in asset.Resource.Meshes.OrderByDescending(m => m.StaticSort.RuntimeLevel)
                .ThenBy(m => asset.Hierarchy.Pivots.First(v => v.Name == m.Name).Ordinal))
            {
                // Offscreen steering/UV controller source meshes are not free-standing HUD art.
                if (mesh.Name.EndsWith("_UV", StringComparison.Ordinal) || mesh.Name is "LEFT" or "RIGHT") continue;
                var pivot = asset.Hierarchy.Pivots.First(v => v.Name == mesh.Name);
                var transform = transforms[pivot.Ordinal];
                // Exporter/controller source pieces can lie wholly outside the authored viewport.
                // Reject those before resolving textures (TECH references an absent source texture).
                var bounds = EnbAuthoredUiAsset.MeshBounds(asset, mesh.Name);
                if (!bounds.Intersects(new EnbUiRect(0, 0, 640, 480))) continue;
                // The bank gadget owns only BANK_n; its page glyph is a separate object.
                // Generic GadgetClass hover clones that owned mesh with the retail additive shader.
                foreach (bool highlight in mesh.Name is "BANK_0" or "BANK_1" ? new[] { false, true } : new[] { false })
                foreach (var authored in mesh.MaterialPasses)
                {
                    var pass = EnbCurrentRetailVerbPresentation.Apply(authored, highlight, false);
                    var packet = new EnbW3dDrawTask(new(EnbRenderPassKind.ModernOverlayBoundary, EnbW3dDrawBlendClass.Blended,
                        mesh.StaticSort, asset.Resource.Key, mesh.Ordinal, pass.Ordinal, 1, 0),
                        1, 1, 0, transform, new(transform.Translation, mesh.BoundingRadius), mesh, null,
                        new(pass, pass.SingleBindingSemantics), EnbW3dLightEnvironment.BlackNoLights, time, asset.Hierarchy.Name, pivot.Ordinal, mesh.Name);
                    if (!EnbW3dIndexedSubmissionCompiler.TryCompile(packet, out var submission, out var error) ||
                        !TryCompileLane(submission, camera, EnbRenderTextureRuntimeSettings.RetailDefaults,
                            EnbRenderTextureBackendCapabilities.ModernDefault, assets.Textures, decodedTextures, null,
                            LiveLaneKind.Textured, out var lane, out error) ||
                        !TryCreateGpuResource(gl, lane, LiveLaneKind.Textured, 0, out var gpu, out error))
                        throw new InvalidOperationException("Cockpit GPU: " + error);
                    if(_cockpitLayout!=Matrix4x4.Identity)lane.Textured!.LightingPlan.CanonicalClipFromModel*=_cockpitLayout;
                    if (highlight)
                    {
                        if (!_bankHighlights.TryGetValue(mesh.Name, out var draws)) _bankHighlights[mesh.Name] = draws = [];
                        draws.Add((lane, gpu));
                    }
                    else _cockpitDraws.Add((new EnbW3dOverlayDrawOwner(lane.Textured!), gpu));
                }
            }
            }
            _cockpitAsset = p.Asset; _shortcutGpuAsset = p.Shortcuts?.Asset; _cockpitSize = (width, height);
        }
        bool iconsDrawn = false;
        foreach (var draw in _cockpitDraws)
        {
            var source = draw.Owner.Lane;
            var original = source.Submission.Packet;
            string name = original.Mesh.Name;
            // Shortcut chrome first, icons next, overlapping cockpit gauges above them.
            if (!iconsDrawn && !name.StartsWith("BANK_",StringComparison.Ordinal) && !name.StartsWith("BTTN_",StringComparison.Ordinal))
            {
                if (p.Shortcuts is { } icons)
                { RenderShortcutLayer(icons.Icons,0); RenderWeaponFeedback(gl,fb,width,height,time,icons.Combat); }
                iconsDrawn = true;
            }
            if (name.StartsWith("BANK_",StringComparison.Ordinal) && name.Length == 8 && p.Shortcuts is { } shortcuts)
            {
                bool left = name[5] == '0';
                if (!(left ? shortcuts.LeftEnabled : shortcuts.RightEnabled) ||
                    name[7]-'0' != (left ? shortcuts.LeftGroup : shortcuts.RightGroup)) continue;
            }
            if (name == "WARP_ON" && !p.WarpEngaged && !(p.WarpEnabled && p.Hovered == "WARP")) continue;
            if (name == "THROTTLE_ON" && p.Thrust != 1 && p.Hovered != "THROTTLE") continue;
            if (name == "REVERSE_ON" && p.Thrust != -1 && p.Hovered != "REVERSE") continue;
            bool trail = name.EndsWith('1');
            float? ratio = name.StartsWith("REACTOR_BAR", StringComparison.Ordinal) ? (trail ? _energyTrail : p.Energy)
                : name.StartsWith("HULL_BAR", StringComparison.Ordinal) ? (trail ? _cockpitHullTrail : p.Hull)
                : name.StartsWith("SHIELD_BAR", StringComparison.Ordinal) ? (trail ? _cockpitShieldTrail : p.Shield) : null;
            Vector2? offset = name == "WARP_BAR" ? new Vector2(0, .46f * p.WarpFill)
                : ratio is float value && original.Material.Source.Ordinal == 0
                ? new Vector2(0, EnbCurrentRetailGaugeRuntime.TextureOffset(value)) : null;
            Vector3? emission = name == "WARP_BAR" ? new Vector3(p.WarpRed, 0, 0)
                : name == "WARP" ? p.WarpButtonEmission ?? new Vector3(p.WarpEnabled ? 1 : .35f) : null;
            draw.Owner.Update(time,offset,emission);
            var lane=LiveLanePlan.From(source);
            SubmitGpuResource(gl, fb, width, height, draw.Gpu, lane, new(false,0,0,0), default, null, time);
            if (p.Hovered == name && p.Shortcuts is { } bank &&
                (name == "BANK_0" ? bank.LeftEnabled : name == "BANK_1" && bank.RightEnabled) &&
                _bankHighlights.TryGetValue(name, out var highlights))
                foreach (var highlight in highlights)
                    SubmitGpuResource(gl, fb, width, height, highlight.Gpu, highlight.Lane,
                        new(false,0,0,0), default, null, time);
        }
        if (p.Shortcuts is { } scut) RenderShortcutLayer(scut.Labels,6);
        void RenderShortcutLayer(System.Collections.Immutable.ImmutableArray<EnbUiTextDraw> images, int index)
        {
            foreach (var image in images)
            {
                var r = index<12?LayoutRect(EnbNext.Presentation.EnbUiGroup.Cockpit,image.Bounds):image.Bounds; var renderer = _shortcutImages[index++];
                if (!renderer.IsInitialized) renderer.Initialize(gl);
                renderer.RenderOverlay(gl,fb,(int)Math.Round(r.X*scaling),height-(int)Math.Round(r.Bottom*scaling),
                    Math.Max(1,(int)Math.Round(r.Width*scaling)),Math.Max(1,(int)Math.Round(r.Height*scaling)),image.Raster);
                if (image.Highlight is { } highlight)
                {
                    if (!_shortcutHighlight.IsInitialized) _shortcutHighlight.Initialize(gl);
                    _shortcutHighlight.RenderOverlay(gl,fb,(int)Math.Round(r.X*scaling),height-(int)Math.Round(r.Bottom*scaling),
                        Math.Max(1,(int)Math.Round(r.Width*scaling)),Math.Max(1,(int)Math.Round(r.Height*scaling)),highlight,additive: true);
                }
            }
        }
        for (int i = 0; i < p.Text.Length; i++)
        {
            var text = p.Text[i]; var r = LayoutRect(EnbNext.Presentation.EnbUiGroup.Cockpit,text.Bounds); var renderer = _cockpitText[i];
            if (!renderer.IsInitialized) renderer.Initialize(gl);
            renderer.RenderOverlay(gl, fb, (int)Math.Round(r.X * scaling), height - (int)Math.Round(r.Bottom * scaling),
                Math.Max(1, (int)Math.Round(r.Width * scaling)), Math.Max(1, (int)Math.Round(r.Height * scaling)), text.Raster);
        }
        RenderBuffs(gl, fb, width, height, scaling, time, UiLayout?.Editing==true?LayoutBuffPreview?.Invoke():p.Buffs);
        if(p.Shortcuts is {} drag)RenderShortcutLayer(drag.Drag,12);
        gl.Viewport(0, 0, width, height);
    }
}
