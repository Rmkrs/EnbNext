using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Client.Models;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbSavedAccountsPanel(EnbFrontendUi owner)
{
    private SavedGameAccount[] accounts=[];
    public SavedGameAccount[] Accounts {get=>accounts;set=>accounts=value.OrderBy(a=>a.Login,StringComparer.OrdinalIgnoreCase).ToArray();}
    public bool IsOpen {get;private set;}
    public bool Busy {get;set;}
    public bool RememberGameLogin {get;set;}
    public bool RememberSiteLogin {get;set;}
    public string Status {get;set;}="Choose an account, or remember your next successful login.";
    public string[] Changes {get;private set;}=[];
    public string Page {get;private set;}="accounts";
    public const float ArrowWidth=14;
    private int offset,selection,listCount;
    private bool keyboardSelection,importFocus;
    public bool HasAccounts=>Accounts.Any(a=>SavedServer(a.Server,owner.SelectedServer));
    private EnbUiRect listBounds;
    private EnbUiPoint lastListPointer;
    public bool Wheel(double delta)
    {
        if(IsOpen&&Page=="preview"){offset=Math.Max(0,offset+(delta>0?-1:1));return true;}
        if(!IsOpen||Page!="accounts")return false;
        offset=Math.Clamp(offset+(delta>0?-1:1),0,Math.Max(0,listCount-9));return true;
    }
    private void Choose(Guid id){Close();Selected?.Invoke(id);}
    // Nine account rows have two controls each, plus four navigation/actions.
    private readonly (string Key,EnbUiRect Bounds)[] hits=new (string,EnbUiRect)[22];
    private int hitCount;
    public EnbUiRect? FindHit(string key)
    {for(int i=0;i<hitCount;i++)if(hits[i].Key==key)return hits[i].Bounds;return null;}
    public (int Id,EnbUiRect Bounds)? ForgetAt(EnbUiPoint point)
    {for(int i=0;i<hitCount;i++)if(hits[i].Key.StartsWith("forget/")&&hits[i].Bounds.Contains(point))return(i+2,hits[i].Bounds);return null;}
    public Action? Opened,Closed,SyncOpened,ReadRequested,ApplyRequested,ForgetSiteRequested;
    public Action<Guid>? Selected,ForgetRequested;
    public void Open()
    {
        IsOpen=true;Page="accounts";var list=Accounts.Where(a=>SavedServer(a.Server,owner.SelectedServer)).ToArray();
        selection=Math.Max(0,Array.FindIndex(list,a=>string.Equals(a.Login,owner.Username.Text,StringComparison.OrdinalIgnoreCase)));
        offset=Math.Clamp(selection-8,0,Math.Max(0,list.Length-9));keyboardSelection=false;Opened?.Invoke();
    }
    public void Close(){IsOpen=false;Busy=false;owner.SitePassword.Text="";owner.SiteUsername.IsVisible=owner.SitePassword.IsVisible=false;Closed?.Invoke();}
    public void Preview(string[] changes){Changes=changes;Page="preview";offset=0;Status="Apply saves these accounts. Existing game passwords are kept.";}
    public void ShowSync(){IsOpen=true;importFocus=true;Page="sync";offset=0;Status="Use your Net-7.org website/forum credentials.";}
    public bool Key(EnbUiInputEvent key)
    {
        if(!IsOpen)return false;
        if(key.Code==0x1b){Close();return true;}
        if(Page=="accounts")
        {
            if(!Busy && key.Code is 0x26 or 0x28)
            {keyboardSelection=true;selection=Math.Clamp(selection+(key.Code==0x26?-1:1),0,Math.Max(0,listCount-1));offset=Math.Clamp(selection<offset?selection:selection>=offset+9?selection-8:offset,0,Math.Max(0,listCount-9));}
            else if(!Busy&&key.Code==0x0d)
            {var list=Accounts.Where(a=>SavedServer(a.Server,owner.SelectedServer)).ToArray();if(selection<list.Length)Choose(list[selection].Id);}
            return true;
        }
        if(key.Code==0x0d){if(!Busy&&Page=="sync")ReadRequested?.Invoke();return true;}
        return false;
    }
    public bool Click(EnbUiPoint point)
    {
        string? key=null;
        for(int i=0;i<hitCount;i++)if(hits[i].Bounds.Contains(point)){key=hits[i].Key;break;}
        if(!IsOpen)
        {
            if(Busy)return key is "open" or "remember";
            if(key=="remember"){RememberGameLogin=!RememberGameLogin;return true;}
            if(key!="open")return false;Open();return true;
        }
        if(Page=="accounts")
        {
            if(key=="open"||!listBounds.Contains(point)){Close();return true;}
            if(Busy)return true;
            if(key=="previous")offset=Math.Max(0,offset-1);
            else if(key=="next")offset=Math.Min(Math.Max(0,listCount-9),offset+1);
            else if(key?.StartsWith("forget/")==true){var id=Guid.Parse(key.AsSpan(7));Close();ForgetRequested?.Invoke(id);}
            else if(key?.StartsWith("select/")==true)Choose(Guid.Parse(key.AsSpan(7)));
            return true;
        }
        if(key=="close"){Close();return true;}
        if(Busy)return true;
        if(key=="previous")offset=Math.Max(0,offset-8);
        else if(key=="next")offset+=8;
        else if(key=="remember")RememberGameLogin=!RememberGameLogin;
        else if(key=="sync"){ShowSync();SyncOpened?.Invoke();}
        else if(key=="site-user")owner.SiteUsername.Focus();
        else if(key=="site-password")owner.SitePassword.Focus();
        else if(key=="site-remember")RememberSiteLogin=!RememberSiteLogin;
        else if(key=="read")ReadRequested?.Invoke();
        else if(key=="apply")ApplyRequested?.Invoke();
        else if(key=="forget-site")ForgetSiteRequested?.Invoke();
        else if(key?.StartsWith("select/")==true)Selected?.Invoke(Guid.Parse(key.AsSpan(7)));
        else if(key?.StartsWith("forget/")==true)ForgetRequested?.Invoke(Guid.Parse(key.AsSpan(7)));
        return true;
    }
    public void Render(ImmutableArray<EnbFrontendDraw>.Builder draws,EnbResolvedRenderCamera camera,EnbUiRect bounds,
        EnbUiPoint pointer,EnbUiRect? nameBounds,bool available,bool disabled,EnbFrontendVisual caret)
    {
        Array.Clear(hits,0,hitCount);hitCount=0;
        if(!available||!IsOpen||Page!="sync")owner.SiteUsername.IsVisible=owner.SitePassword.IsVisible=false;
        if(!available){if(IsOpen)Close();return;}
        if(disabled&&IsOpen)Close();
        EnbUiRect Screen(EnbUiRect rect)=>new(rect.X*bounds.Width/640,rect.Y*bounds.Height/480,rect.Width*bounds.Width/640,rect.Height*bounds.Height/480);
        if(nameBounds is not {} name)return;
        var field=new EnbUiRect(name.X*640/bounds.Width,name.Y*480/bounds.Height,name.Width*640/bounds.Width,name.Height*480/bounds.Height);
        var arrow=new EnbUiRect(field.Right-ArrowWidth,field.Y+1,ArrowWidth,Math.Max(1,field.Height-2));
        Art("open","slider_dn","CLICK_DOWN",arrow,null);
        hits[hitCount++]=("open",Screen(arrow));
        var password=owner.Password.Bounds;
        var remember=new EnbUiRect(field.X,password.Bottom*480/bounds.Height+7,10,10);
        Art("remember","opt04a","CHECK00",remember,RememberGameLogin?"if_check_on.tga":"if_check_off.tga");
        Label("remember","Remember login",new(remember.Right+4,remember.Y,field.Width-14,10));
        owner.Text.Get("accounts/remember").X=Screen(new(remember.Right+4,remember.Y,0,0)).X;
        owner.Text.Get("accounts/remember").Opacity=disabled?.35:1;
        if(!disabled)hits[hitCount++]=("remember",Screen(new(remember.X,remember.Y,field.Width,12)));
        if(!IsOpen)return;
        owner.Username.IsEnabled=owner.Password.IsEnabled=false;caret.IsVisible=false;
        if(Page=="accounts")
        {
            var list=Accounts.Where(a=>SavedServer(a.Server,owner.SelectedServer)).ToArray();listCount=list.Length;
            offset=Math.Clamp(offset,0,Math.Max(0,list.Length-9));
            int rows=Math.Max(1,Math.Min(list.Length,9));
            var drop=new EnbUiRect(field.X,field.Y,field.Width-ArrowWidth,(rows+1)*field.Height);
            listBounds=Screen(drop);
            foreach(var pair in owner.Text.Labels)
                if(pair.Value.IsVisible&&new EnbUiRect(pair.Value.X,pair.Value.Y,pair.Value.Width,pair.Value.Height).Intersects(listBounds))pair.Value.IsVisible=false;
            var fieldDraw=draws.First(d=>d.Key=="ui/BUTTON_NAME");
            var sourceLabel=owner.Text.Get("Name_fe01a");
            // The authored surface is translucent; suppress covered controls
            // so their artwork does not bleed through the account rows.
            for(int i=draws.Count-1;i>=0;i--)
                if(
                    draws[i].Key=="ui/BUTTON_PASSWORD"&&password.Intersects(listBounds) ||
                    draws[i].Key=="accounts/remember"&&Screen(remember).Intersects(listBounds))draws.RemoveAt(i);
            // Reuse the field draw, including its authored pivot and material.
            bool moved=pointer!=lastListPointer;lastListPointer=pointer;if(moved)keyboardSelection=false;
            double scroll=list.Length>9?12:0;
            for(int i=offset;i<Math.Min(list.Length,offset+9);i++)
            {
                var row=new EnbUiRect(drop.X,drop.Y+(i-offset+1)*field.Height,drop.Width-scroll,field.Height);
                string key="select/"+list[i].Id;
                var forget=new EnbUiRect(row.Right-14,row.Center.Y-5,10,10);
                var selectRow=row with {Width=row.Width-16};
                bool hovered=Screen(selectRow).Contains(pointer);
                if(moved&&hovered)selection=i;
                bool active=hovered||keyboardSelection&&selection==i;
                draws.Add(fieldDraw with {Key="accounts/row-background/"+(i-offset),
                    Transform=fieldDraw.Transform*Matrix4x4.CreateTranslation(0,-(float)(field.Height*(i-offset+1)),0),
                    Highlight=active,Pressed=false});
                var label=owner.Text.Get("accounts/row"+(i-offset));
                label.SetText(list[i].Login,sourceLabel.Font??owner.Text.Font("FONT_SMALL",bounds.Size),active?new(0,1,0):new(1,1,1));label.SetDisplayScale(1,1);
                label.X=owner.Username.Bounds.Center.X-label.Width/2;
                label.Y=owner.Username.Bounds.Center.Y-label.Height/2+(i-offset+1)*name.Height;
                double pixelScale=owner.Text.RenderScaling;
                label.X=Math.Round(label.X*pixelScale)/pixelScale;label.Y=Math.Round(label.Y*pixelScale)/pixelScale;
                label.Opacity=1;label.ZIndex=sourceLabel.ZIndex;
                var clip=owner.Username.Bounds with {Y=owner.Username.Bounds.Y+(i-offset+1)*name.Height};
                label.Clip=new(clip.X-label.X,clip.Y-label.Y,Screen(selectRow).Width,clip.Height);
                string forgetKey="forget/"+list[i].Id;
                Art(forgetKey,"trade00a","EXIT",forget,null);
                if(!Busy){hits[hitCount++]=(forgetKey,Screen(forget with {X=forget.X-1,Y=forget.Y-1,Width=12,Height=12}));hits[hitCount++]=(key,Screen(selectRow));}
            }
            if(list.Length==0)Label("empty",Busy?"Loading...":"No saved accounts",new(drop.X+4,drop.Y+field.Height,drop.Width-8,field.Height));
            if(list.Length>9)
            {
                var up=new EnbUiRect(drop.Right-12,drop.Y+field.Height+2,10,10);
                var down=up with {Y=drop.Bottom-12};
                Art("previous","slider_up","CLICK_UP",up,null);Art("next","slider_dn","CLICK_DOWN",down,null);
                hits[hitCount++]=("previous",Screen(up));hits[hitCount++]=("next",Screen(down));
            }
            return;
        }
        RenderImport(draws,camera,bounds,pointer,caret);
        void Label(string key,string value,EnbUiRect rect)
        {owner.Text.PlaceCentered("accounts/"+key,value,rect,"FONT_SMALL",new(1,1,1),bounds.Size);}
        void Art(string key,string modelName,string meshName,EnbUiRect rect,string? texture)
        {
            var model=owner.Resources.Model(modelName);var mesh=model.Resource.Meshes.Single(m=>m.Name==meshName);
            var min=mesh.BoundingBoxMin;var max=mesh.BoundingBoxMax;
            var world=Matrix4x4.CreateTranslation(-min.X,-max.Y,0)*Matrix4x4.CreateScale((float)rect.Width/(max.X-min.X),(float)rect.Height/(max.Y-min.Y),1)*Matrix4x4.CreateTranslation((float)rect.X-320,240-(float)rect.Y,180);
            draws.Add(new("accounts/"+key,model.Resource,mesh,world,null,camera,0){TextureOverride=texture,Highlight=!Busy&&!disabled&&(Screen(rect).Contains(pointer)||key=="open"&&IsOpen),Disabled=Busy||disabled});
        }
    }

    private static bool SavedServer(string a,string b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
}
