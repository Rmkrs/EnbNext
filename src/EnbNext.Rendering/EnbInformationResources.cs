using System.Numerics;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Authored Information resources prepared once by the asset session.</summary>
public sealed class EnbInformationResources(EnbRetailAssetBundle assets)
{
    public sealed record LabelEffect(EnbAttachedTextStyle Style,float Initial,float Target,float Fade);
    public sealed record Piece(string Key, EnbAuthoredUiAsset Asset, EnbUiRect Bounds)
    { public EnbRetailMeshPick Query { get; init; } }
    public sealed record Panel(string Prefix, EnbAuthoredUiAsset Asset, EnbInterfaceLayoutCatalog Layout,
        Piece[] Pieces, Matrix4x4[] Transforms)
    {
        public string[] PointerOrder { get; init; } = [];
        public EnbW3dWorldBounds? BackgroundSphere { get; init; }
        public Vector3 BackgroundExtents { get; init; }
        public Vector3 DragOrigin { get; init; }
        public EnbUiRect ScrollTrack { get; init; }
        public EnbUiRect ScrollBounds { get; init; }
        public EnbUiRect ScrollUp { get; init; }
        public EnbUiRect ScrollDown { get; init; }
        public EnbUiRect WheelBounds { get; init; }
        public LabelEffect[] LabelStyles { get; init; } = [];
        public EnbUiRect Design(string key) => Pieces.First(p=>p.Key==key).Bounds;
        public Vector3 Pivot(string name) => Transforms[Asset.Hierarchy.Pivots.First(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).Ordinal].Translation;
    }
    public EnbRetailAssetBundle Assets => assets;
    private EnbSceneAvatarSnapshot? helpAvatar;
    public EnbSceneAvatarSnapshot HelpAvatar => OwnerCache.Get(ref helpAvatar,assets,static a=>EnbRetailAvatarDefaults.Help(a));
    public EnbVerticalScrollbarResources Scrollbar => EnbRenderSession.Of(assets).VerticalScrollbar;
    private Panel? log, help, petition, player, complaint;
    private EnbInterfaceLayoutCatalog? defaults;
    public EnbInterfaceLayoutCatalog Defaults => OwnerCache.Get(ref defaults,assets,
        static a=>EnbInterfaceLayoutCatalog.Load(Path.Combine(a.Paths.ClientIniDirectory,"fc_default.ini")));
    public Panel Get(string prefix) => prefix switch
    {
        "log"=>OwnerCache.Get(ref log,assets,static a=>Load(a,"log","log00a")),
        "help"=>OwnerCache.Get(ref help,assets,static a=>Load(a,"help","hsi01a")),
        "petition"=>OwnerCache.Get(ref petition,assets,static a=>Load(a,"petition","hsi02a")),
        "player"=>OwnerCache.Get(ref player,assets,static a=>Load(a,"player","hsi03a")),
        "complaint"=>OwnerCache.Get(ref complaint,assets,static a=>Load(a,"complaint","hsi04a")),
        _=>throw new ArgumentOutOfRangeException(nameof(prefix))
    };
    private static Panel Load(EnbRetailAssetBundle assets,string prefix,string model)
    {
        var asset=EnbAuthoredUiAsset.Load(assets,model+".w3d")??throw new InvalidDataException(model+" unavailable.");
        var layout=EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fa_"+model+".ini"));
        var transforms=EnbAuthoredUiAsset.ResolveTransforms(asset);
        var pieces=asset.Resource.Meshes.Select(mesh=>new Piece(prefix+"/"+mesh.Name,
            asset with {Resource=asset.Resource.Copy().SetMeshes(new[]{mesh})},EnbAuthoredUiAsset.MeshBounds(asset,mesh.Name))
            { Query=EnbUiModelQuery.BindMainView(mesh,transforms[asset.Hierarchy.Pivots.First(p=>p.Name.Equals(mesh.Name,StringComparison.OrdinalIgnoreCase)).Ordinal]) })
            .OrderBy(p=>p.Key.Contains("FRAME")?0:1).ThenByDescending(p=>p.Key.Contains("FRAME")?p.Bounds.Width*p.Bounds.Height:0).ToArray();
        string[] order=prefix switch
        {
            "log"=>["ENTRY_FIELD00","DATE_STAMP","NEXT","PREVIOUS","EXIT2"],
            "petition"=>["ENTRY_FIELD00","SUBMIT","BACK","EXIT_BUTTON"],
            "player"=>["ENTRY_FIELD01","EXIT_IMPORT"],
            "help"=>["PLAYER","STUCK","GAMEPLAY","BILLING","BUG","KNOWLEDGE","EXIT2"],
            "complaint"=>["BUTTON_05","BUTTON_04","BUTTON_03","BUTTON_02","BUTTON_01","EXIT2"],
            _=>[]
        };
        EnbW3dWorldBounds? sphere=null;Vector3 extents=default;EnbUiRect wheel=default;
        if(order.Length>0 && asset.Resource.HierarchicalLods.SingleOrDefault() is {} hlod)
        {
            if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(asset.Resource,hlod,Matrix4x4.Identity,null,0,out var bounds,out var error))
                throw new InvalidDataException(error);
            sphere=bounds.WorldSphere;extents=bounds.LocalBox.Extents;
            var b=bounds.LocalBox;wheel=new(b.Center.X-b.Extents.X+320,240-b.Center.Y-b.Extents.Y,b.Extents.X*2,b.Extents.Y*2);
        }
        EnbUiRect track=default,scrollBounds=default,up=default,down=default;
        if(prefix is "log" or "petition")
        {
            var scrollbar=EnbRenderSession.Of(assets).VerticalScrollbar;
            Vector3 Pivot(string name)=>transforms[asset.Hierarchy.Pivots.First(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).Ordinal].Translation;
            var a=Pivot("SLIDER_00_MIN");var b=Pivot("SLIDER_00_MAX");
            double x=a.X+320,top=240-Math.Max(a.Y,b.Y),bottom=240-Math.Min(a.Y,b.Y),arrow=scrollbar.Up.Bounds.Height-2;
            up=scrollbar.Up.Bounds.Translate(x,top);down=scrollbar.Down.Bounds.Translate(x,bottom);
            track=new(x-arrow/2,top+arrow,arrow,bottom-top-2*arrow);scrollBounds=new(x-arrow/2,top,arrow,bottom-top);
            var region=pieces.FirstOrDefault(p=>p.Key.Equals(prefix+"/SLIDER_00_REGIO",StringComparison.OrdinalIgnoreCase));
            if(region is not null)wheel=region.Bounds;
        }
        LabelEffect LoadLabel(EnbInterfaceTextDefinition d)
        {
            float initial=0,target=1,fade=0;
            foreach(var effect in EnbRenderSession.Of(assets).FontEffects.EnumerateSections().Select(s=>s.Value))
                if(effect.GetValueOrDefault("EffectID") is {} id && effect.GetValueOrDefault("EffectType")=="FADE" &&
                    d.Properties.GetValueOrDefault("EffectOn","").Split(',').Any(v=>v.Trim()==id))
                {
                    float Number(string key)=>float.Parse(effect.GetValueOrDefault(key,"0"),CultureInfo.InvariantCulture);
                    initial=Number("Initial");target=Number("Intensity");fade=Number("Speed");
                }
            return new(EnbAttachedTextStyle.Load(assets,asset,d,Matrix4x4.Identity,transforms),initial,target,fade);
        }
        return new(prefix,asset,layout,pieces,transforms)
        {
            PointerOrder=order.Select(n=>prefix+"/"+n).ToArray(),BackgroundSphere=sphere,BackgroundExtents=extents,
            ScrollTrack=track,ScrollBounds=scrollBounds,ScrollUp=up,ScrollDown=down,WheelBounds=wheel,
            DragOrigin=prefix is not ("help" or "complaint")?default:transforms[asset.Hierarchy.Pivots.First(p=>p.Name.Equals("FRAME04",StringComparison.OrdinalIgnoreCase)).Ordinal].Translation,
            LabelStyles=order.Length==0?[]:layout.Definitions.Select(d=>LoadLabel(d)).ToArray()
        };
    }
}
