using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbGroupLaunchPanel(EnbRetailAssetBundle assets) : IDisposable
{
    internal EnbUiLayoutProfiles? Layout {get;set;}
    public string GroupName { get; set; }="";
    public (string Name,string State,string Error)[] Members { get; set; }=[];
    public bool Paused { get; set; }
    public bool Stopped { get; set; }
    public Action<int>? LaunchRequested;
    public Action<int>? InviteRequested;
    public bool[] InviteEnabled {get;set;}=[];
    /// <summary>Next status line per row (location, docked, leader), shown in the row tooltip.</summary>
    public string[] Details {get;set;}=[];
    private readonly EnbAuthoredUiAsset? inviteArt=EnbAuthoredUiAsset.Load(assets,"vb_group.w3d");
    public bool Collapsed { get; private set; }
    public bool Dragging { get; private set; }
    private readonly EnbConfirmationResources resources=EnbRenderSession.Of(assets).Confirmations;
    private readonly EnbOptionsPanelResources fonts=EnbRenderSession.Of(assets).OptionsPanels;
    private readonly EnbAuthoredUiAsset? up=Piece(assets,"slider_up.w3d","CLICK_UP"),down=Piece(assets,"slider_dn.w3d","CLICK_DOWN");
    private readonly EnbRetailText?[] labels=new EnbRetailText?[36];
    private int labelCount;
    private int menuMember=-1;
    private EnbUiRect menuBounds;
    private readonly EnbCurrentRetailTooltipDelay tooltipDelay=new();
    private EnbUiTooltipDraw? tooltip;
    public EnbUiTooltipDraw? Tooltip(EnbUiSize size,double scale)
    {
        var invite=hits.FirstOrDefault(h=>h.Key.StartsWith("invite/")&&h.Rect.Contains(pointer));
        var row=hits.FirstOrDefault(h=>h.Key.StartsWith("row/")&&h.Rect.Contains(pointer));
        if(Layout?.Editing==true||Dragging||menuMember>=0||row.Key is null&&invite.Key is null){tooltipDelay.Reset();return null;}
        int index=invite.Key is not null?int.Parse(invite.Key[7..]):int.Parse(row.Key![4..]);if(index>=Members.Length)return null;
        var resources=EnbRenderSession.Of(assets).TooltipResources;
        if(!tooltipDelay.IsReady(index,Environment.TickCount64,EnbCurrentRetailTooltipDelay.Milliseconds(assets.ResolvePlayerOptions(null).Options.InterfaceTooltipDelay)))return null;
        if(tooltip is not null)return tooltip;
        if(resources.Frame is not {} frame||resources.Definition is not {} definition||fonts.Font(definition.FontId,size,scale) is not {} font)return null;
        var member=index<0?(Name:"",State:"",Error:""):Members[index];
        return tooltip=EnbTooltipContent.Create(frame,font,invite.Key is not null?(index<0?"Invite all":"Invite "+member.Name):member.Name+": "+member.State+(member.State!="Offline"&&member.Error.Length>0?"\n"+member.Error:"")+((uint)index<(uint)Details.Length&&Details[index].Length>0?"\n"+Details[index]:""),size,scale,new(.1f,1,.1f),Layout?.Placement(EnbUiGroup.Multibox).Apply(pointer,size)??pointer,i=>labels[16+i]??=new());
    }
    private bool CanLaunch(int i)=>i>=0&&i<Members.Length&&(Members[i].State is "Offline" or "Failed"||Members[i].State=="Waiting"&&(Paused||Stopped));
    public bool RightClick(EnbUiPoint point)
    {
        if(modern)return ModernRightClick(point);
        var row=hits.FirstOrDefault(h=>h.Key.StartsWith("row/")&&h.Rect.Contains(point));
        bool handled=menuMember>=0||Contains(point);menuMember=row.Key is null?-1:int.Parse(row.Key![4..]);
        pointer=point;menuAnchor=point;Invalidate();return handled;
    }
    private readonly List<(int Index,double Width,double Height,EnbAuthoredUiAsset Asset)> frames=[];
    private readonly List<(string Key,EnbUiRect Rect)> hits=[];
    private EnbUiRect panelBounds;
    private EnbUiPoint pointer,position,menuAnchor;

    private ImmutableList<EnbUiModelDraw>? cached;
    private EnbUiSize cachedSize;
    private double cachedScale;
    private bool cachedEditing;
    public bool OwnsTooltip=>Dragging||menuMember>=0||Contains(pointer);
    public EnbUiRect Bounds=>panelBounds;
    public EnbUiRect? FindHit(string key)=>hits.Where(h=>h.Key==key).Select(h=>(EnbUiRect?)h.Rect).FirstOrDefault();
    public void Invalidate(){cached=null;tooltip=null;}
    public void Pointer(EnbUiPoint point)
    {

        if(Dragging&&modern)ModernPosition=new(point.X-modernGrab.X,point.Y-modernGrab.Y);
        if(pointer!=point){pointer=point;Invalidate();}
    }
    public void EndDrag(){if(Dragging&&modern)ModernMoved?.Invoke(ModernPosition??position);Dragging=false;}
    public bool Contains(EnbUiPoint point)=>panelBounds.Contains(point)||(menuMember>=0&&menuBounds.Contains(point));
    public bool Click(EnbUiPoint point)
    {
        if(modern)return ModernClick(point); // Next Modern UI: no buttons (the title drags); everything else is on the right-click menu.
        if(menuMember>=0){int member=menuMember;bool launch=FindHit("launch/"+member)?.Contains(point)==true&&CanLaunch(member);menuMember=-1;Invalidate();if(launch)LaunchRequested?.Invoke(member);return true;}
        string? key=hits.FirstOrDefault(h=>h.Rect.Contains(point)).Key;
        if(key?.StartsWith("invite/")==true){int i=int.Parse(key[7..]);if(i<0?InviteEnabled.Any(v=>v):(uint)i<(uint)InviteEnabled.Length&&InviteEnabled[i])InviteRequested?.Invoke(i);}
        else if(key=="collapse")Collapsed=!Collapsed;

        else if(key?.StartsWith("launch/")==true)LaunchRequested?.Invoke(int.Parse(key[7..]));
        Invalidate();return Contains(point);
    }
    public ImmutableList<EnbUiModelDraw> Draw(EnbUiSize size,double scale)
    {
        bool editing=Layout?.Editing==true;
        if(cached is not null&&cachedSize==size&&cachedScale==scale&&cachedEditing==editing)return cached;
        cachedEditing=editing;
        if(cachedSize!=size||cachedScale!=scale)frames.Clear();cachedSize=size;cachedScale=scale;
        if(modern)return DrawModern(size,scale,editing);
        hits.Clear();labelCount=0;var draws=ImmutableList.CreateBuilder<EnbUiModelDraw>();
        var font=fonts.Font("FONT_SMALL",size,scale);if(font is null)return [];
        double line=font.CellHeight*font.Scale+4,icon=line-6,pad=6;
        double Measure(string value)=>font.MeasureWidth(value)*font.Scale;
        double width=Measure(GroupName)+icon+pad*4;
        foreach(var m in Members)width=Math.Max(width,Measure(m.Name)+pad*2);
        if(InviteRequested is not null)width+=icon+pad;
        width=Math.Min(size.Width,Math.Max(line*3,width));
        int count=editing?6:Members.Length;
        double height=line+pad*2+(!editing&&Collapsed?0:count*line);
        position=new(EnbGutterUi.EdgeClearance*size.Width/640,size.Height*.26);
        position=new(Math.Clamp(position.X,0,Math.Max(0,size.Width-width)),Math.Clamp(position.Y,0,Math.Max(0,size.Height-height)));
        var outer=new EnbUiRect(position.X,position.Y,width,height);panelBounds=outer;Frame(0,outer,false);
        Text(GroupName,new(outer.X+pad,outer.Y+pad,width-icon-pad*3-(InviteRequested is null?0:icon+pad),line));
        var toggle=new EnbUiRect(outer.Right-pad-icon,outer.Y+pad+3,icon,icon);
        var art=Collapsed?down:up;
        if(art is not null){var mesh=art.Resource.Meshes.Single();var min=mesh.BoundingBoxMin;var max=mesh.BoundingBoxMax;draws.Add(new(art,0,toggle,toggle.Contains(pointer)){DesignBounds=new(min.X,-max.Y,max.X-min.X,max.Y-min.Y)});hits.Add(("collapse",toggle));}
        if(InviteRequested is not null)Invite(-1,new(toggle.X-icon-pad,toggle.Y,icon,icon),InviteEnabled.Any(v=>v));
        hits.Add(("title",new(outer.X,outer.Y,width,line+pad)));
        if(editing||!Collapsed)
            for(int i=0;i<count;i++)
            {
                var m=i<Members.Length?Members[i]:(Name:"Group member "+(i+1),State:"Online",Error:"");double y=outer.Y+pad+line*(i+1);
                var rect=new EnbUiRect(outer.X+pad,y,width-pad*2-(InviteRequested is null?0:icon+pad),line);
                if(rect.Contains(pointer)&&menuMember<0)Frame(2,rect,true);
                Text(m.Name,rect,m.State switch {"Online"=>new(0,1,0),"Offline" or "Failed"=>new(1,.2f,.2f),"Starting"=>new(1,1,0),_=>new(.7f,.7f,.7f)});
                hits.Add(("row/"+i,rect));
                if(InviteRequested is not null)Invite(i,new(outer.Right-pad-icon,y+3,icon,icon),(uint)i<(uint)InviteEnabled.Length&&InviteEnabled[i]);
            }
        if(menuMember>=Members.Length)menuMember=-1;
        if(menuMember>=0)
        {
            double mw=Measure("Launch")+pad*4;
            menuBounds=new(Math.Clamp(menuAnchor.X,0,Math.Max(0,size.Width-mw)),Math.Clamp(menuAnchor.Y,0,Math.Max(0,size.Height-line-pad*2)),mw,line+pad*2);
            Frame(0,menuBounds,false);
            var action=new EnbUiRect(menuBounds.X+pad,menuBounds.Y+pad,mw-pad*2,line);
            Frame(2,action,CanLaunch(menuMember)&&action.Contains(pointer));
            Text("Launch",action,CanLaunch(menuMember)?new(1,1,0):new(.4f,.4f,.4f));
            hits.Insert(0,("launch/"+menuMember,action));
        }
        return cached=draws.Select(d=>d with {LayoutGroup=EnbUiGroup.Multibox}).ToImmutableList();
        void Invite(int index,EnbUiRect rect,bool enabled)
        {
            if(inviteArt is null)return;
            var bounds=EnbAuthoredUiAsset.MeshBounds(inviteArt,inviteArt.Resource.Meshes[0].Name);
            draws.Add(new(inviteArt,0,rect,enabled&&rect.Contains(pointer)){DesignBounds=bounds.Translate(-320,-240),EmissiveOverride=enabled?null:new Vector3(.33f)});
            hits.Insert(0,("invite/"+index,rect));
        }
        void Frame(int index,EnbUiRect rect,bool hover)
        {
            var asset=frames.FirstOrDefault(f=>f.Index==index&&f.Width==rect.Width&&f.Height==rect.Height).Asset;
            if(asset is null){var source=resources.Frames[index];asset=source.Asset with {Geometry=source.Resize.Geometry((float)rect.Width,(float)rect.Height)};frames.Add((index,rect.Width,rect.Height,asset));}
            var min=asset.Geometry!.Positions.Aggregate(Vector3.Min);var max=asset.Geometry.Positions.Aggregate(Vector3.Max);
            draws.Add(new(asset,0,rect,hover){DesignBounds=new(min.X,-max.Y,max.X-min.X,max.Y-min.Y)});
        }
        void Text(string value,EnbUiRect rect,EnbRgb? color=null)
        {
            var label=labels[labelCount++]??=new();label.SetText(value,font,color??new(1,1,1));label.SetDisplayScale(1,1);label.FitTo(rect.Size,scale);
            if(label.Raster is {} raster)draws[^1]=draws[^1] with {Images=draws[^1].Images.Add(new(raster,new(rect.X,rect.Center.Y-label.Height/2,label.Width,label.Height)))};
        }
    }
    private static EnbAuthoredUiAsset? Piece(EnbRetailAssetBundle assets,string file,string name)
    {
        var asset=EnbAuthoredUiAsset.Load(assets,file);var mesh=asset?.Resource.Meshes.FirstOrDefault(m=>m.Name==name);
        return asset is null||mesh is null?null:asset with {Resource=asset.Resource.Copy().SetMeshes([mesh])};
    }
    public void Dispose(){foreach(var label in labels)label?.Dispose();}
}
