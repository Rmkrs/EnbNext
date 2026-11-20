using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using EnbNext.Presentation;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbChatUi
{
    private EnbAuthoredUiAsset? _comboAsset;
    private EnbRetailChatLayoutPose? _comboPose;
    private IReadOnlyDictionary<string,Vector3> _shellAnchors=new Dictionary<string,Vector3>();
    private readonly Dictionary<(int Width,float Opening,string Mesh),(EnbAuthoredUiAsset Asset,Rect Bounds)> _comboGeometry=[];
    private string _comboMenu="";
    private float _comboOpening;
    private int _comboState,_comboRows;
    private long _comboTick;
    private bool _comboShown;
    internal Func<long> ComboClock {get;set;}=()=>Environment.TickCount64;

    private void ToggleChatCombo(string tab,int index)
    {
        _replyPopup=false;
        if(_menu==tab && _comboMenu==tab)
            _comboState=_comboState==2?1:2;
        else
        {
            if(_menu.Length>0 && _comboShown)PlayCommandMenuOpen();
            _menu=tab;_menuTab=index;_menuOffset=0;
        }
        Publish();
    }

    private void SelectComboEntry(Action action)
    {
        string menu=_menu;int tab=_menuTab,rows=_comboRows;
        float frame=_comboOpening;bool shown=_comboShown;
        PlayCommandMenuSelection();
        action();
        if(_comboAsset is not null && shown)
        {
            // 0057A060 calls HUD event15 first, then changes this combo to
            // state2. Its forced-negative branch applies only to another owner.
            _menu=_comboMenu=menu;_menuTab=tab;_comboRows=rows;
            _comboOpening=frame;_comboShown=true;_comboState=2;_comboTick=ComboClock();
            Publish();
        }
        else PlayCommandMenuOpen();
    }

    private void SynchronizeCombo(int count)
    {
        _comboRows=Math.Min(count,_menu=="Chat"?32:_menu=="Emote"?33:count);
        if(_comboMenu==_menu)return;
        _comboMenu=_menu;_comboOpening=0;_comboState=count>0?1:0;_comboTick=ComboClock();_comboShown=false;
    }

    private bool AdvanceCombo()
    {
        long now=ComboClock();
        uint elapsed=unchecked((uint)(now-_comboTick));_comboTick=now;
        if(_menu.Length==0){_comboMenu="";_comboState=0;_comboOpening=0;return false;}
        if(_comboMenu!=_menu || _comboState is 0 or 3)return false;
        if(_comboState==1 && !_comboShown){_comboShown=true;PlayCommandMenuOpen();}
        _comboOpening=EnbRetailChatLayoutPose.AdvanceComboFrame(_comboOpening,elapsed,_comboState==1);
        if(_comboState==1 && _comboOpening>=_comboRows){_comboOpening=_comboRows;_comboState=3;}
        else if(_comboState==2 && _comboOpening<0){_comboOpening=0;_comboState=0;_menu="";_comboMenu="";PlayCommandMenuOpen();}
        // Animated frames must not accumulate an unbounded GPU asset cache.
        foreach(var key in _comboGeometry.Keys.Where(k=>k.Opening!=0).ToArray())_comboGeometry.Remove(key);
        return true;
    }

    private void LoadChatCombo()
    {
        _comboAsset=null;_comboPose=null;_comboGeometry.Clear();
        _shellAnchors=new Dictionary<string,Vector3>();
        if(_assets?.RetailMixResources is not {} resources ||
            EnbAuthoredUiAsset.Load(_assets,"tsi05b.w3d") is not {} shell ||
            !resources.TryReadResource("combo01.w3d",out var bytes,out var location) ||
            !resources.TryReadResource("combo01-1.w3d",out var width,out _) ||
            !resources.TryReadResource("combo01-2.w3d",out var opening,out _) ||
            !EnbW3dModel.TryDecode([..bytes,..width,..opening],out var model) ||
            model.Hierarchies.FirstOrDefault() is not {} hierarchy ||
            model.Animations.FirstOrDefault(a=>a.Name.Equals("COMBO01-1",StringComparison.OrdinalIgnoreCase)) is not {} widthAnimation ||
            model.Animations.FirstOrDefault(a=>a.Name.Equals("COMBO01-2",StringComparison.OrdinalIgnoreCase)) is not {} openingAnimation ||
            !EnbAuthoredUiAsset.TryCompile(resources,location,model,hierarchy.Name,out var resource))return;
        string[] anchors=["TEXTFIELD00","TEXTFIELD01","TEXTFIELD02",..Enumerable.Range(1,33).Select(i=>$"TBONE{i:00}")];
        if(anchors.Any(name=>!hierarchy.Pivots.Any(p=>p.Name==name)) ||
            Enumerable.Range(0,5).Any(i=>!shell.Hierarchy.Pivots.Any(p=>p.Name.Equals($"B{i:00}",StringComparison.OrdinalIgnoreCase))) ||
            !resource.Meshes.Any(m=>m.Name=="CANCEL") || !resource.Meshes.Any(m=>m.Name=="DROP_DOWN00"))return;
        EnbRetailChatLayoutPose pose;
        try {pose=new(hierarchy,widthAnimation,openingAnimation,masked:false);}
        catch(InvalidDataException){return;}
        var transforms=EnbCockpitUi.ResolveTransforms(shell);
        _shellAnchors=shell.Hierarchy.Pivots.ToDictionary(p=>p.Name,p=>transforms[p.Ordinal].Translation,StringComparer.OrdinalIgnoreCase);
        _comboAsset=new(resource,hierarchy);
        _comboPose=pose;
    }

    private Point ComboOrigin(int tab)
    {
        var p=ShellOrigin(tab);
        return new(p.X+320,240-p.Y);
    }

    /// <summary>Next Modern UI hides the menu tabs; their drop-downs open upward from its menu buttons (screen space).</summary>
    private bool _modernMenus;
    internal bool ModernMenus {get=>_modernMenus;set{if(_modernMenus==value)return;_modernMenus=value;_menu="";Publish();}}
    private static readonly string[] MenuTabs=["Chat","Group","Options","Help","Emote"];
    private Vector3 ShellOrigin(int tab)
    {
        return _shellAnchors[$"B{tab:00}"];
    }

    private Rect ComboScreen(Rect design)=>new(design.X*_size.Width/640,design.Y*_size.Height/480,
        design.Width*_size.Width/640,design.Height*_size.Height/480);

    private (EnbAuthoredUiAsset Asset,Rect Bounds)? ComboMesh(int width,float opening,string name)
    {
        if(_comboAsset is not {} asset || _comboPose is null)return null;
        var key=(width,opening,name);
        if(_comboGeometry.TryGetValue(key,out var cached))return cached;
        var mesh=asset.Resource.Meshes.FirstOrDefault(m=>m.Name==name);
        if(mesh is null || !EnbW3dSkinDeformer.TryDeform(mesh,_comboPose.EvaluatePose(width,opening),out var geometry,out _))return null;
        var min=geometry.Positions.Aggregate(Vector3.Min);var max=geometry.Positions.Aggregate(Vector3.Max);
        var source=new Rect(min.X,-max.Y,max.X-min.X,max.Y-min.Y);
        var part=asset with {Resource=asset.Resource.Copy().SetMeshes([mesh]),Geometry=geometry};
        return _comboGeometry[key]=(part,source);
    }

    private Rect ComboTextBounds(int tab,string bone,string rightBone)
    {
        var asset=_comboAsset!;
        var pose=_comboPose!.EvaluatePose(tab==0?80:72,0);
        Vector3 Anchor(string name)=>pose.WorldTransforms[asset.Hierarchy.Pivots.First(p=>p.Name==name).Ordinal].Translation;
        var origin=ShellOrigin(tab);
        var local=Anchor(bone);var right=Anchor(rightBone);
        // 005790C0 truncates the two authored X coordinates separately.
        int width=(int)right.X-(int)local.X;
        var p=local+origin;
        // 006D6390 font+10 returns cell height; 006D5C10 converts its
        // half extent to 480-space. 00578350 truncates all four bounds.
        float half=(float)((Font("FONT_SMALL")?.CellHeight??0)*480.0/(_size.Height*_scale)*.5);
        int left=(int)p.X,top=(int)(p.Y+half),bottom=(int)(p.Y-half),edge=(int)(p.X+width);
        return ComboScreen(new(left+320,240-top,edge-left,top-bottom));
    }

    private bool PublishChatTab(int tab,string label,Action action)
    {
        if(_comboAsset is null || ComboMesh(tab==0?80:72,0,"CANCEL") is not {} part)return false;
        string id="tab/"+label;
        var origin=ComboOrigin(tab);
        var text=ComboTextBounds(tab,"TEXTFIELD00","TEXTFIELD02");
        int first=_images.Count;
        Text(label,text,_hover==id?new(1,1,0):new(1,1,1),"FONT_SMALL",centerVertically:true);
        var captions=_images.Skip(first).ToImmutableArray();
        while(_images.Count>first)_images.RemoveAt(_images.Count-1);
        _draws.Add(new(part.Asset,0,(ComboScreen(part.Bounds.Translate(origin.X,origin.Y))),false)
            { LayoutGroup=EnbUiGroup.Chat,DesignBounds=(part.Bounds),Images=captions});
        Hit(id,text,action);
        return true;
    }

    private bool PublishChatCombo(IReadOnlyList<(string Label,Action? Action,bool Selected)> entries)
    {
        if(_comboAsset is null)return false;
        SynchronizeCombo(entries.Count);
        if(!_comboShown)return true;
        if(ComboMesh(_menuTab==0?80:72,_comboOpening,"DROP_DOWN00") is not {} part)return false;
        var origin=ComboOrigin(_menuTab);
        var bounds=ComboScreen(part.Bounds.Translate(origin.X,origin.Y));
        int first=_images.Count;
        _menuOffset=Math.Clamp(_menuOffset,0,Math.Max(0,entries.Count-_comboRows));
        Hit("menu",bounds,()=>{},wheel:d=>{_menuOffset+=d>0?-3:3;Publish();});
        for(int row=0;row<_comboRows;row++)
        {
            if(_comboOpening==0 || _comboOpening<row)continue;
            int index=_menuOffset+row;
            var entry=entries[index];string id="entry"+index;
            var rect=ComboTextBounds(_menuTab,$"TBONE{row+1:00}","TEXTFIELD01");
            Text(entry.Label,rect,_hover==id||entry.Selected?new(1,1,0):new(1,1,1),"FONT_SMALL",centerVertically:true);
            if(entry.Action is {} action)Hit(id,rect,action,wheel:d=>{_menuOffset+=d>0?-3:3;Publish();});
        }
        var labels=_images.Skip(first).ToImmutableArray();
        while(_images.Count>first)_images.RemoveAt(_images.Count-1);
        _draws.Add(new(part.Asset,0,(bounds),false){ LayoutGroup=EnbUiGroup.Chat,DesignBounds=(part.Bounds),Images=labels});
        return true;
    }
}
