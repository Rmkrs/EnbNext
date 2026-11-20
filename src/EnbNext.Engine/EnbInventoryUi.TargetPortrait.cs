using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private bool _targetPortraitCollapsed;
    private readonly EnbCallerPortraitState _callerPortrait = new();
    private IEnbRenderObject? TargetPortrait => _callerPortrait.Current;
    /// <summary>Modern UI owns the target portrait; its unit frames animate these avatars (render identities).</summary>
    internal bool ModernUi {get;set;}
    internal IReadOnlyList<ulong> ModernPortraits {get;set;}=[];
    internal EnbAvatarPortraitAsset? PortraitPoseFor(ulong id)=>_callerPortrait.PoseFor(id);
    private string _targetPortraitFont="FONT_SMALL";
    private Point _targetPortraitName;
    private Rect _targetPortraitNameClip;
    private float _targetPortraitNameHalfWidth;
    private float _targetPortraitScrollSpeed;
    private readonly EnbAttachedLabelScroll _targetPortraitScroll = new();
    private string? _targetPortraitTip;
    public event Action<string>? TargetPortraitChatRequested;
    public Action<string,bool,System.Numerics.Vector3>? AnimationSoundRequested {get;set;}
    public IReadOnlyList<EnbNext.Net7.Gameplay.Net7PlayerNlpRecord>? ProduceAvatarChat(IEnbSceneFrame scene,EnbRetailAssetBundle assets,string text)
        =>_callerPortrait.ProduceChat(scene,assets,text,Environment.TickCount64*.001);

    private void UpdateCallerPortrait(IEnbSceneFrame scene,EnbRetailAssetBundle? assets,double now)
    {
        var additional=new List<ulong>();
        if(scene.ActiveStarbaseId is null)
        {
            if(_character && _characterTab==0 && !_expanded && scene.Objects.FirstOrDefault(o=>o.IsLocalShip) is {} local)
                additional.Add(local.RenderIdentity);
            if(_playerTrade is not null)
                additional.AddRange(scene.Objects.Where(o=>o.IsLocalShip || o.GameId==_tradeVisualPartner).Select(o=>o.RenderIdentity));
        }
        if(scene.ActiveStarbaseId is null)additional.AddRange(ModernPortraits);
        _callerPortrait.AdditionalPortraits=additional;
        _callerPortrait.Expanded=!_targetPortraitCollapsed;
        _callerPortrait.Update(scene,assets,now);
        foreach(var cue in _callerPortrait.AnimationSounds)
            AnimationSoundRequested?.Invoke(cue.Name,cue.Flat,cue.Position);
    }

    private void LoadTargetPortrait(EnbRetailAssetBundle assets)
    {
        var asset=EnbAuthoredUiAsset.Load(assets,"tsi05b.w3d")??throw new InvalidDataException("TSI05B unavailable.");
        var layout=EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fa_tsi05b.ini"));
        if(!layout.TryResolve("avatar_name_TSI05B",out var label))throw new InvalidDataException("Target portrait name layout unavailable.");
        _targetPortraitFont=label.Properties["FontType"];
        var tips=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"tooltip.ini"));
        if(tips.TryGetSection("UI_CHAT_PANEL",out var section) && section.TryGetValue("AVATAR_THEM",out var tip))_targetPortraitTip=tip;
        var style=EnbAttachedTextStyle.Load(assets,asset,label,System.Numerics.Matrix4x4.Identity);
        _targetPortraitName=style.Anchor;
        _targetPortraitNameClip=style.Clip;
        _targetPortraitNameHalfWidth=style.HalfWidth;
        _targetPortraitScrollSpeed=style.ScrollSpeed;
        _targetPortraitScroll.Clear();
        foreach(var mesh in asset.Resource.Meshes.Where(m=>m.Name is "FRAME_DETAIL" or "FRAME_DETAIL_MI" or "COCKPIT" or "AVATAR_THEM" or "RESIZE_MIN00" or "RESIZE_MIN01" or "RESIZE_MAX00" or "RESIZE_MAX01"))
            _art["targetPortrait/"+mesh.Name]=new(asset with {Resource=asset.Resource.Copy().SetMeshes([mesh])},
                EnbCockpitUi.MeshBounds(asset,mesh.Name).Translate(new(-320,-240)));
    }

    private void TargetPortraitPanel()
    {
        if(TargetPortrait is not {Avatar:{} avatar} target)return;
        const string p="targetPortrait/";
        string frame=p+(_targetPortraitCollapsed?"FRAME_DETAIL_MI":"FRAME_DETAIL");
        Draw(frame,Native(frame));Hit("targetPortraitFrame",Native(frame));
        if(!_targetPortraitCollapsed)
        {
            Draw(p+"COCKPIT",Native(p+"COCKPIT"));
            var bounds=Native(p+"AVATAR_THEM");
            Hit("targetPortraitTalk",bounds);
            if(_targetPortraitTip is {} tip)_tips["targetPortraitTalk"]=tip;
            _draws[^1]=_draws[^1] with {Portrait=avatar,PortraitEnvironment=_scene!.Environment,
                PortraitCacheKey="target/player",PortraitPose=_callerPortrait.Pose,PortraitSharedActor=true,PortraitBounds=(Screen(bounds)),PortraitAspect=bounds.Width/bounds.Height};
        }
        string background=p+(_targetPortraitCollapsed?"RESIZE_MAX00":"RESIZE_MIN01");
        string arrow=p+(_targetPortraitCollapsed?"RESIZE_MAX01":"RESIZE_MIN00");
        Draw(background,Native(background),"targetPortraitToggle");Draw(arrow,Native(arrow));
        if(Font(_targetPortraitFont) is {} font)
        {
            const string id="targetPortraitName";
            // 563910 copies the cached avatar's first name; 563DC0 consumes
            // that string independently of the world object's display name.
            string text=avatar.FirstName;
            if(!_labels.TryGetValue(id,out var label))_labels[id]=label=new();
            label.SetText(text,font,new(1,1,0));label.SetDisplayScale(1,1);
            float width=(float)(label.Width*640/_size.Width);
            float offset=_targetPortraitScroll.Update(text,Environment.TickCount64,width,_targetPortraitNameHalfWidth,_targetPortraitScrollSpeed);
            _animatingText|=_targetPortraitScrollSpeed!=0&&width>(int)(_targetPortraitNameHalfWidth*1.9f);
            var anchor=Screen(new Rect(_targetPortraitName.X+offset,_targetPortraitName.Y,0,0));
            var bounds=new Rect(anchor.X,anchor.Y-label.Height/2,label.Width,label.Height);
            var visible=bounds.Intersect(Screen(_targetPortraitNameClip));
            if(label.Raster is {} raster&&visible.Width>0&&visible.Height>0)_images.Add(new(raster,(bounds)){Clip=(visible)});
        }
        Flush();
    }
}
