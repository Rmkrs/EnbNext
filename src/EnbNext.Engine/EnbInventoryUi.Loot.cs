using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    public Func<EnbInventoryItemRequest,Task<bool>>? LootItemRequested { get; set; }
    public Func<uint,ulong,Task<bool>>? LootCloseRequested { get; set; }
    private EnbAuthoredUiAsset? _lootAsset;
    private EnbW3dRenderHierarchyAnimation? _lootAnimation;
    private long? _lootOpenedAt;
    private float _lootFrame = -1;
    private EnbAuthoredUiAsset? _lootThumbAsset;
    private Vector3 _lootSliderMin, _lootSliderMax;
    private double _lootScroll, _lootScrollAtGrab, _lootGrab, _lootMaximum;
    private Rect _lootTrack, _lootThumb, _lootSliderBounds;
    private int _lootRepeat;
    private long _lootRepeatAt;

    private void LoadLootArt(EnbRetailAssetBundle assets)
    {
        // Current retail 00585500 / 005852D0: PSI09A, PSI09A.PSI09A-1,
        // eight S00..S07 anchors and X_SPECIALS.
        var asset=EnbAuthoredUiAsset.Load(assets,"psi09a.w3d")??throw new InvalidDataException("PSI09A unavailable");
        if(!assets.RetailMixResources!.TryReadResource("psi09a-1.w3d",out var bytes,out _) ||
            !EnbW3dModel.TryDecode(bytes,out var model) || model.Animations.FirstOrDefault() is not {} animation ||
            !EnbW3dRenderResourceCompiler.TryCompileAnimation(0,animation,out var compiled,out _))
            throw new InvalidDataException("PSI09A opening animation unavailable");
        _lootAsset=asset; _lootAnimation=compiled; _lootOpenedAt=null; _lootFrame=-1;
        SetLootPose(0);
        var catalog=assets.RetailMixResources;
        if(EnbAuthoredUiAsset.Load(assets,"slider_hor.w3d") is {} thumb &&
            catalog.TryReadResource("slider_hor.w3d",out var meshBytes,out var location) &&
            catalog.TryReadResource("slider_hor-1.w3d",out var animationBytes,out _) &&
            EnbW3dModel.TryDecode([..meshBytes,..animationBytes],out var thumbModel) &&
            EnbAuthoredUiAsset.TryCompile(catalog,location,thumbModel,"slider_hor",out var resource))
            _lootThumbAsset=thumb with {Resource=resource};
        // Next "Loot all": the target interface's own loot verb button (vb_loot),
        // in its verb design space.
        if(EnbAuthoredUiAsset.Load(assets,"vb_loot.w3d") is {} hand)_art["loot/ALL"]=new(hand,new(-12,-12,24,24));
    }

    private void SetLootPose(float frame)
    {
        if(_lootAsset is not {} asset || _lootAnimation is not {} animation || _lootFrame==frame)return;
        if(!EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.Single(),Matrix4x4.Identity,animation,
            frame,out var pose,out var error))throw new InvalidDataException(error);
        foreach(var mesh in asset.Resource.Meshes)
        {
            EnbW3dDeformedGeometry geometry;
            if(mesh.BoneIndices.Count>0)
            {if(!EnbW3dSkinDeformer.TryDeform(mesh,pose,out geometry,out error))throw new InvalidDataException(error);}
            else
            {
                var pivot=asset.Hierarchy.Pivots.First(p=>p.Name.Equals(mesh.Name,StringComparison.OrdinalIgnoreCase));
                var transform=pose.WorldTransforms[pivot.Ordinal];
                geometry=new(mesh.Positions.Select(v=>Vector3.Transform(v,transform)).ToImmutableArray(),
                    mesh.Normals.Select(v=>Vector3.TransformNormal(v,transform)).ToImmutableArray());
            }
            var min=geometry.Positions.Aggregate(Vector3.Min);var max=geometry.Positions.Aggregate(Vector3.Max);
            _art["loot/"+mesh.Name]=new(asset with {Resource=asset.Resource.Copy().SetMeshes([mesh]),Geometry=geometry},
                new(min.X,-max.Y,max.X-min.X,max.Y-min.Y));
        }
        _lootFrame=frame;
        // 005857C0 refreshes the independent slider transforms when opening
        // completes. Its wheel region belongs to the moving panel mesh.
        if(frame==0 || frame==animation.NumFrames-1)
        {
            _lootSliderMin=pose.WorldTransforms[asset.Hierarchy.Pivots.Single(p=>p.Name.Equals("SLIDER_00_MIN",StringComparison.OrdinalIgnoreCase)).Ordinal].Translation;
            _lootSliderMax=pose.WorldTransforms[asset.Hierarchy.Pivots.Single(p=>p.Name.Equals("SLIDER_00_MAX",StringComparison.OrdinalIgnoreCase)).Ordinal].Translation;
        }
    }

    private void LootPanel()
    {
        if(!_layoutLootPreview&&(Data is not {} live || live.HulkId is null&&live.HarvestableId is null))return;
        var data=Data;
        // 005852D0 queues the opening HAnim; 005857C0 -> 005B9410 ->
        // 005DD3C0 advances it at its authored rate and retains the final pose.
        long now=Environment.TickCount64;
        if(_lootOpenedAt is null){_lootOpenedAt=now;_lootScroll=0;_lootRepeat=0;}
        if(_lootAnimation is {} animation)
        {
            float last=animation.NumFrames-1;
            float frame=_layoutLootPreview?last:Math.Min(last,Math.Max(0,now-_lootOpenedAt.Value)*animation.PlaybackFrameRate*.001f);
            SetLootPose(frame);
            _animatingText|=frame<last;
        }
        var items=_layoutLootPreview?Enumerable.Empty<EnbSceneItemSnapshot>():data!.HarvestableId is not null ? data.Harvest : data.Loot.Where(i=>i.Inventory==6);
        var keys=_layoutLootPreview?ImmutableArray<int>.Empty:data!.HarvestableId is not null ? data.HarvestSlotKeys : data.LootSlotKeys;
        _lootMaximum=Math.Max(0,(keys.Length+3)/4-2);
        // PSI09A has no SLIDER_00_REGIO mesh. 0066E460 uses its parent
        // render object as the wheel region in that case.
        if(_lootMaximum>0)Hit("lootWheelRegion",_art.Where(a=>a.Key.StartsWith("loot/",StringComparison.Ordinal))
            .Select(a=>a.Value.Design).Aggregate((a,b)=>a.Union(b)).Translate(new(320,240)));
        Draw("loot/X_SPECIALS",Native("loot/X_SPECIALS"),"lootClose");
        LootAllButton(items.Any());
        SetLootScroll(_lootScroll);
        if(_pressed is "lootPrevious" or "lootNext" or "lootTrack")
        {
            if(now>=_lootRepeatAt && (_pressed!="lootTrack" || (_lootRepeat<0?_point.X<Screen(_lootThumb).Left:_point.X>Screen(_lootThumb).Right)))
            {SetLootScroll(_lootScroll+_lootRepeat);_lootRepeatAt=now+100;}
            _animatingText=true;
        }
        int offset=(int)(_lootScroll+.5)*4;
        for(int i=0;i<8;i++)
        {
            string id="lootItem/"+i;
            var item=offset+i<keys.Length?items.FirstOrDefault(item=>item.Slot==keys[offset+i]):null;
            Slot(id,Native("loot/S"+i.ToString("00")),item,
                frameArt:"loot/FRAME"+(i%4*2+i/4).ToString("00"));
            // 688B90 retains empty (-1) keys; 68CCC0 -> 68AC50 describes
            // these as Empty Slot. No item request is created for an empty key.
            if(item is null && offset+i<keys.Length)
                EmptyInventorySlotTooltip(id);
        }
        Flush();
        LootSlider(keys.Length);
    }

    private void SetLootScroll(double value)
    {
        double next=Math.Clamp(value,0,_lootMaximum);
        if(Math.Round(next)!=Math.Round(_lootScroll))InteractionSoundRequested?.Invoke("slider_bar_1.wav");
        _lootScroll=next;
    }

    private void LootSlider(int count)
    {
        if(_lootMaximum<=0 || _lootThumbAsset is not {} thumb)return;
        double y=240-_lootSliderMin.Y, left=320+Math.Min(_lootSliderMin.X,_lootSliderMax.X), right=320+Math.Max(_lootSliderMin.X,_lootSliderMax.X);
        var previous=_art["left/CLICK_LEFT"].Design.Translate(new(left,y));
        var next=_art["right/CLICK_RIGHT"].Design.Translate(new(right,y));
        double arrowSize=_art["left/CLICK_LEFT"].Design.Width-2;
        double start=left+arrowSize, length=Math.Max(0,right-left-2*arrowSize);
        Draw("left/CLICK_LEFT",previous,"lootPrevious");Draw("right/CLICK_RIGHT",next,"lootNext");
        double width=Math.Max(16,length*2/((count+3)/4));
        var animation=thumb.Resource.Animations.Single();
        if(!EnbW3dHierarchyPose.TryEvaluate(thumb.Resource.Hierarchies.Single(),Matrix4x4.Identity,animation,(float)Math.Max(0,width-16),out var pose,out _) ||
            !EnbW3dSkinDeformer.TryDeform(thumb.Resource.Meshes.Single(),pose,out var geometry,out _))return;
        var min=geometry.Positions.Aggregate(Vector3.Min);var max=geometry.Positions.Aggregate(Vector3.Max);
        var source=new Rect(min.X,-max.Y,max.X-min.X,max.Y-min.Y);
        _lootTrack=new(start,y-arrowSize/2,length,arrowSize);
        _lootThumb=source.Translate(new(start+Math.Max(0,length-width)*_lootScroll/_lootMaximum,y));
        _lootSliderBounds=new(left,y-arrowSize/2,right-left,arrowSize);
        Hit("lootTrack",_lootTrack);
        _draws.Add(new(thumb with {Geometry=geometry},0,(Screen(_lootThumb)),_hover=="lootThumb"){LayoutGroup=_layoutGroup,DesignBounds=(source)});
        Hit("lootThumb",_lootThumb);
    }

    private void LootPress(string id)
    {
        _lootRepeat=id switch {"lootPrevious"=>-1,"lootNext"=>1,"lootTrack"=>_point.X<Screen(_lootThumb).Left?-2:2,_=>0};
        if(_lootRepeat!=0){SetLootScroll(_lootScroll+_lootRepeat);_lootRepeatAt=Environment.TickCount64+500;}
        if(id=="lootThumb"){_lootGrab=_point.X-Screen(_lootThumb).Left;_lootScrollAtGrab=_lootScroll;}
    }

    private void LootMove(Point point)
    {
        if(_pressed=="lootThumb")
        {
            if(!Screen(_lootSliderBounds).Inflate(32*_size.Width/640,32*_size.Height/480).Contains(point))SetLootScroll(_lootScrollAtGrab);
            else SetLootScroll((point.X-_lootGrab-Screen(_lootTrack).Left)/Math.Max(.0001,Screen(_lootTrack).Width-Screen(_lootThumb).Width)*_lootMaximum);
        }
        _point=point;Publish();
    }

    private bool _lootingAll,_lootAllEnabled;
    /// <summary>Interface option "Enable Loot all button" (off by default: the original loot window).</summary>
    public bool LootAllEnabled {get=>_lootAllEnabled;set{if(_lootAllEnabled!=value){_lootAllEnabled=value;Publish();}}}
    /// <summary>Next-only: mirrors the close button at the panel's other corner.</summary>
    private void LootAllButton(bool any)
    {
        if(!_lootAllEnabled || !any&&!_layoutLootPreview || !_art.ContainsKey("loot/ALL"))return;
        var close=Native("loot/X_SPECIALS");
        var slots=Enumerable.Range(0,8).Select(i=>Native("loot/S"+i.ToString("00"))).Aggregate((a,b)=>a.Union(b));
        double size=Math.Min(close.Width,close.Height);
        var r=new Rect(slots.X+slots.Right-close.Right+(close.Width-size),close.Y+(close.Height-size)/2,size,size);
        Draw("loot/ALL",r,"lootAll",enabled:!_lootingAll&&!_busy);
        _tips["lootAll"]="Loot all";
    }

    /// <summary>One click takes every item in turn, as if the first slot were clicked each
    /// time: after an item is taken the server moves the rest up one slot, so every step reads
    /// the current loot and takes its first item. The tractor beam then carries the item to the
    /// ship and stays in use until it arrives, so the next item waits until the loot holds one
    /// item fewer and the hold has changed. It stops when an item cannot be taken (for example
    /// a full hold) or the loot target changes.</summary>
    private async Task LootAll()
    {
        if(!_lootAllEnabled||_busy||_lootingAll||LootItemRequested is not {} send||_owner is not uint owner||Data is not {} start)return;
        bool harvest=start.HarvestableId is not null;
        uint? target=harvest?start.HarvestableId:start.HulkId;
        EnbSceneItemSnapshot[] Current()=>Data is {} data&&(harvest?data.HarvestableId:data.HulkId)==target
            ?(harvest?data.Harvest:data.Loot.Where(i=>i.Inventory==6)).OrderBy(i=>i.Slot).ToArray():[];
        int Hold(){var hash=new HashCode();if(Data is {} data)foreach(var c in data.Cargo){hash.Add(c.Slot);hash.Add(c.TemplateId);hash.Add(c.Count);}return hash.ToHashCode();}
        _lootingAll=true;Publish();
        try
        {
            for(int remaining=Current().Length;remaining>0;)
            {
                var data=Data!;var item=Current()[0];int hold=Hold();
                var request=new EnbInventoryItemRequest(owner,_ship,item.Inventory,item.Slot,item.TemplateId)
                {AmmoLayer=item.IsAmmoLayer,HarvestableId=harvest?data.HarvestableId:null,HarvestableOwner=harvest?data.HarvestableOwner:0,
                 HulkId=harvest?null:data.HulkId,HulkOwner=harvest?0:data.HulkOwner};
                bool sent=false;
                await Send(async()=>sent=await send(request),()=>Resolve(request) is not null);
                if(!sent)break;
                long until=Environment.TickCount64+2000;
                while(Current().Length>=remaining&&Environment.TickCount64<until)await Task.Delay(30);
                int now=Current().Length;if(now>=remaining)break;
                remaining=now;if(remaining==0)break;
                // The tractor beam is busy until the item reaches the hold.
                until=Environment.TickCount64+15000;
                while(Hold()==hold&&Environment.TickCount64<until)await Task.Delay(50);
                if(Hold()==hold)break;
                await Task.Delay(250);
            }
        }
        finally{_lootingAll=false;Publish();}
    }

    private async Task<bool> ActivateLoot(string id,EnbInventoryItemRequest? request)
    {
        if(!id.StartsWith("loot",StringComparison.Ordinal))return false;
        if(id=="lootClose")await CloseLoot();
        else if(id=="lootAll")await LootAll();
        else if(!_busy && request is {Inventory:6 or 0x12} item && Resolve(item) is not null && LootItemRequested is {} send)
            await Send(()=>send(item),()=>Resolve(item) is not null);
        return true;
    }

    private async Task CloseLoot()
    {
        if(!_busy && Data is {} data && (data.HarvestableId ?? data.HulkId) is uint id && LootCloseRequested is {} close)
            await Send(()=>close(id,data.HarvestableId is not null ? data.HarvestableOwner : data.HulkOwner));
        Publish();
    }
}
