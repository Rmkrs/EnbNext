using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Engine;

internal sealed partial class EnbFrontendUi : IDisposable
{
    public readonly EnbFrontendResources Resources;
    public readonly EnbFrontendTextUi Text;
    public readonly EnbFrontendDialogUi Dialogs;
    public readonly EnbSavedAccountsPanel Accounts;
    public readonly EnbLoginGroupsPanel Groups;
    private static readonly string[] Servers=["sunrise.net-7.org","ptr.net-7.org","dev.net-7.org"];
    private int serverIndex;
    public readonly EnbFrontendServerStatus ServerStatus=new();
    public EnbUiRect? ServerCheckBounds {get;private set;}
    public string SelectedServer=>Servers[serverIndex];
    public event Action? ServerChanged;
    public event Action<string>? SoundRequested;
    public EnbUiRect? ServerBounds {get;private set;}
    public EnbFrontendUi(EnbRetailAssetBundle assets)
    {
        InitializeInputs();Resources=EnbRenderSession.Of(assets).Frontend;Text=new(assets);Dialogs=new(assets,Text);Accounts=new(this);Groups=new(this);
    }
    public void SelectServer(string? name)
    {
        int index=Math.Max(0,Array.IndexOf(Servers,name));
        if(serverIndex==index)return;
        serverIndex=index;ServerChanged?.Invoke();
        if(ServerStatus.Enabled)_=ServerStatus.RefreshAsync(SelectedServer);
    }
    public bool HandleServerClick(EnbUiPoint point,string screen,bool busy)
    {
        if(screen!="login" || busy || Dialogs.Active is not null)return false;
        if(ServerCheckBounds is {} check && check.Contains(point))
        {
            if(!ServerStatus.Checking){_=ServerStatus.RefreshAsync(SelectedServer);SoundRequested?.Invoke("Login_Mouse_Click");}
            return true;
        }
        if(ServerBounds is not {} bounds || !bounds.Contains(point))return false;
        serverIndex=(serverIndex+1)%Servers.Length;ServerChanged?.Invoke();
        if(ServerStatus.Enabled)_=ServerStatus.RefreshAsync(SelectedServer);
        SoundRequested?.Invoke("Login_Mouse_Click");return true;
    }
    public void RenderServer(ImmutableArray<EnbFrontendDraw>.Builder draws,EnbResolvedRenderCamera camera,
        EnbUiRect bounds,EnbUiPoint pointer,string screen,bool busy,EnbUiRect? nameBounds)
    {
        ServerBounds=null;ServerCheckBounds=null;
        if(screen!="login")return;
        var model=Resources.Model("fe01a");
        var mesh=model.Resource.Meshes.Single(m=>m.Name=="BUTTON_NAME");
        var min=mesh.BoundingBoxMin;var max=mesh.BoundingBoxMax;
        if(nameBounds is not {} name)return;
        EnbUiRect Screen(EnbUiRect r)=>new(r.X*bounds.Width/640,r.Y*bounds.Height/480,r.Width*bounds.Width/640,r.Height*bounds.Height/480);
        var row=new EnbUiRect(name.X*640/bounds.Width,70,name.Width*640/bounds.Width,name.Height*480/bounds.Height);
        var checkRect=new EnbUiRect(row.Right-40,row.Y,40,row.Height);
        var header=ServerStatus.Enabled?new EnbUiRect(row.X,row.Y,row.Width-44,row.Height):row;
        var target=Screen(header);ServerBounds=target;
        var world=Matrix4x4.CreateTranslation(-min.X,-max.Y,0)*
            Matrix4x4.CreateScale((float)header.Width/(max.X-min.X),(float)header.Height/(max.Y-min.Y),1)*
            Matrix4x4.CreateTranslation((float)header.X-320,240-(float)header.Y,150);
        bool hover=target.Contains(pointer) && !busy && Dialogs.Active is null;
        draws.Add(new("server/server",model.Resource,mesh,world,null,camera,0){Highlight=hover,Disabled=busy||Dialogs.Active is not null});
        Text.PlaceCentered("server/server",SelectedServer,header,"FONT_SMALL",busy||Dialogs.Active is not null?new(.35f,.35f,.35f):hover?new(0,1,0):new(1,1,1),bounds.Size);
        if(!ServerStatus.Enabled)return;
        if(Text.Labels.TryGetValue("N_fe01a",out var prompt) && prompt.IsVisible)
        {
            Text.PlaceCentered("server/status",ServerStatus.Text,row,"FONT_SMALL",
                ServerStatus.Checking?new(1,1,0):ServerStatus.Result.Online?new(0,1,0):new(1,.35f,.35f),bounds.Size);
            // Follow the authored credential prompt's physical text origin, including DPI rounding.
            Text.Get("server/status").X=prompt.X;
        }
        ServerCheckBounds=Screen(checkRect);
        bool disabled=busy||Dialogs.Active is not null||ServerStatus.Checking;
        bool checkHover=!disabled&&ServerCheckBounds.Value.Contains(pointer);
        var checkWorld=Matrix4x4.CreateTranslation(-min.X,-max.Y,0)*
            Matrix4x4.CreateScale((float)checkRect.Width/(max.X-min.X),(float)checkRect.Height/(max.Y-min.Y),1)*
            Matrix4x4.CreateTranslation((float)checkRect.X-320,240-(float)checkRect.Y,150);
        draws.Add(new("server/check",model.Resource,mesh,checkWorld,null,camera,0){Highlight=checkHover,Disabled=disabled});
        Text.PlaceCentered("server/check","Check",checkRect,"FONT_SMALL",disabled?new(.35f,.35f,.35f):checkHover?new(0,1,0):new(1,1,1),bounds.Size);
    }
    public void Dispose(){Accounts.Close();ServerStatus.Dispose();DisposeInputs();Dialogs.Dispose();Text.Dispose();}
}
