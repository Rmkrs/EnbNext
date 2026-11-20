using System.Collections.Immutable;
using EnbNext.Presentation;
using EnbNext.Rendering;
using Rect=EnbNext.Presentation.EnbUiRect;
using Point=EnbNext.Presentation.EnbUiPoint;
namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    public EnbRemoteShortcutMember[] RemoteShortcutMembers {get;set;}=[];
    public int RemoteShortcutCount {get;set;}=3;
    public Action<string,int,int>? RemoteShortcutBound {get;set;}
    public Action<uint,int>? RemoteShortcutRequested {get;set;}
    private (uint Avatar,int Circle,Point Point)? remoteShortcutMenu;
    public void ClearRemoteShortcuts(){RemoteShortcutMembers=[];remoteShortcutMenu=null;}
    private void DrawRemoteShortcuts(uint avatar,Rect row,bool rightSide=false)
    {
        var member=RemoteShortcutMembers.FirstOrDefault(m=>m.Avatar==avatar);
        if(RemoteShortcutCount<=0||member is null||StationGroup||Group?.IsLeader!=true||_missingGroupSymbolArt.Contains("remoteShortcut"))return;
        if(!_art.ContainsKey("remoteShortcut"))
        {
            try
            {
            var asset=_assets is {} a?EnbAuthoredUiAsset.Load(a,"tsi01a_short.w3d"):null;
            var mesh=asset?.Resource.Meshes.FirstOrDefault(m=>m.Name=="BTTN_0_00");
            if(asset is null||mesh is null){_missingGroupSymbolArt.Add("remoteShortcut");return;}
            _art["remoteShortcut"]=new(asset with {Resource=asset.Resource.Copy().SetMeshes([mesh])},EnbCockpitUi.MeshBounds(asset,mesh.Name).Translate(new(-320,-240)));
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException){_missingGroupSymbolArt.Add("remoteShortcut");return;}
        }
        int count=Math.Clamp(RemoteShortcutCount,0,12),rows=count>6?2:1,columns=Math.Min(count,6);
        const double frameGap=2;double gap=rows==1?.5:1;
        double size=rows==1?Math.Min(16,row.Height*.64):Math.Min(20,(row.Height-gap)/2);
        double step=size+gap,width=columns*step-gap,height=rows*step-gap;
        // Next Modern UI: a group frame moved to the left of the screen has them on its right.
        double left=rightSide?row.Right+frameGap:row.X-frameGap-width,top=row.Center.Y-height/2;
        for(int i=0;i<count;i++)
        {
            string id="remoteShortcut/slot/"+avatar+"/"+i;
            var r=new Rect(left+(i%6)*step,top+(i/6)*step,size,size);
            var slot=i<member.Bindings.Length?member.Slots.FirstOrDefault(s=>s.Slot==member.Bindings[i]):null;
            DrawRemoteShortcutCircle(id,r,slot,member.Online);
            _tips[id]=!member.Online?member.Name+": Offline":slot is null?"Right-click to choose a shortcut slot.":slot.Tooltip;
        }
    }
    private void DrawRemoteShortcutCircle(string id,Rect r,EnbRemoteShortcut? slot,bool online,bool selecting=false)
    {
            Draw("remoteShortcut",r,id,online||selecting);
            if(slot?.Icon is {} icon)
            {
                double inset=Math.Min(2,r.Width*.1);
                int before=_images.Count;ImageTexture(icon,new(r.X+inset,r.Y+inset,r.Width-2*inset,r.Height-2*inset),new(slot.Red,slot.Green,slot.Blue));
                if(_images.Count>before)_images[^1]=_images[^1] with {Brightness=online&&slot.Usable?1:.35f};
            }
            Flush();
            void Feedback(string name)
            {
                string key=LoadGroupSymbolArt(name);
                if(!_art.TryGetValue(key,out var overlay)||!_art.TryGetValue(LoadGroupSymbolArt("itemon"),out var ring))return;
                // Preserve authored size and offset: the timer is a small corner
                // overlay, not a quad stretched across the entire shortcut.
                double sx=r.Width/ring.Design.Width,sy=r.Height/ring.Design.Height;
                var bounds=new Rect(r.X+(overlay.Design.X-ring.Design.X)*sx,r.Y+(overlay.Design.Y-ring.Design.Y)*sy,overlay.Design.Width*sx,overlay.Design.Height*sy);
                int before=_draws.Count;Draw(key,bounds);
                if(_draws.Count>before)_draws[^1]=_draws[^1] with {AnimateMaterials=true};
            }
            if(slot?.Active==true&&online)Feedback("itemon");
            if(slot is {RangeFrame:>=0} && online)
            {
                int before=_draws.Count;Feedback("if_targ");
                if(_draws.Count>before)_draws[^1]=_draws[^1] with {ShortcutRangeFrame=slot.RangeFrame,ShortcutRangeAngle=slot.RangeAngle};
            }
            if(slot?.Cooling==true&&online)Feedback("item_timer");
    }
    private bool ActivateRemoteShortcut(string id,bool right)
    {
        if(!id.StartsWith("remoteShortcut/"))return false;
        var parts=id.Split('/');
        if(parts[1]=="close"){remoteShortcutMenu=null;return true;}
        if(parts.Length!=4||!uint.TryParse(parts[2],out uint avatar)||!int.TryParse(parts[3],out int index))return true;
        var member=RemoteShortcutMembers.FirstOrDefault(m=>m.Avatar==avatar);
        if(RemoteShortcutCount<=0||member is null||Group?.IsLeader!=true||StationGroup)return true;
        if(parts[1]=="choose")
        {
            if(remoteShortcutMenu is {} menu&&menu.Avatar==avatar&&index is >=-1 and <12)
            {RemoteShortcutBound?.Invoke(member.Key,menu.Circle,index);remoteShortcutMenu=null;}
        }
        else if(index>=0&&index<Math.Clamp(RemoteShortcutCount,0,12))
        {
            if(right)remoteShortcutMenu=(avatar,index,new(_point.X*640/_size.Width,_point.Y*480/_size.Height));
            else if(member.Online&&index<member.Bindings.Length&&member.Slots.FirstOrDefault(s=>s.Slot==member.Bindings[index]) is {Usable:true} slot)
                RemoteShortcutRequested?.Invoke(avatar,slot.Slot);
        }
        return true;
    }
    private void RemoteShortcutMenu()
    {
        if(remoteShortcutMenu is not {} menu)return;
        var member=RemoteShortcutMembers.FirstOrDefault(m=>m.Avatar==menu.Avatar);
        if(RemoteShortcutCount<=0||menu.Circle>=RemoteShortcutCount||member is null||Group?.IsLeader!=true){remoteShortcutMenu=null;return;}
        const double size=20,step=24,width=28;
        double height=(member.Slots.Length+1)*step+4;
        double x=Math.Clamp(menu.Point.X,0,640-width),y=Math.Clamp(menu.Point.Y,0,480-height);
        Flush();Hit("remoteShortcut/close",new(0,0,640,480));Frame("SYMBOL_CHAT_WINDOW",new(x,y,width,height),"remoteShortcut/frame");
        string clear="remoteShortcut/choose/"+menu.Avatar+"/-1";
        DrawRemoteShortcutCircle(clear,new(x+4,y+4,size,size),null,true);_tips[clear]="Clear shortcut binding";y+=step;
        foreach(var slot in member.Slots.OrderBy(s=>s.Slot))
        {
            string id="remoteShortcut/choose/"+menu.Avatar+"/"+slot.Slot;
            DrawRemoteShortcutCircle(id,new(x+4,y+4,size,size),slot,member.Online,selecting:true);
            _tips[id]="Bank "+(slot.Slot/6+1)+" / "+(slot.Slot%6+1)+": "+slot.Tooltip;y+=step;
        }
        Flush();
    }
}
