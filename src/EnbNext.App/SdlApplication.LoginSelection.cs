using System.Text.Json;
using EnbNext.Engine;

namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private sealed record LoginSelection(string Server,bool GroupMode,Guid? Group,string Login);
    private bool loginSelectionRestored;
    private LoginSelection? savedLoginSelection;
    private string LoginSelectionPath=>Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_preferencesPath))!,"login-selection.json");
    private bool RestoreLoginSelection(EnbFrontendController frontend)
    {
        if(parameterLaunch||loginSelectionRestored||string.IsNullOrEmpty(_preferencesPath))return false;
        loginSelectionRestored=true;
        try
        {
            if(!File.Exists(LoginSelectionPath))return false;
            var value=JsonSerializer.Deserialize<LoginSelection>(File.ReadAllText(LoginSelectionPath));
            if(value is null)return false;
            frontend.Owner.SelectServer(value.Server);
            frontend.Username.Text=value.Login;
            _groups.Load();frontend.Owner.Groups.Groups=_groups.Groups;
            frontend.Owner.Groups.RestoreSelection(value.GroupMode,_groups.Groups.FirstOrDefault(g=>g.Id==value.Group&&SavedAccountStore.Same(g.Server,frontend.Owner.SelectedServer)));
            savedLoginSelection=value;return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException)
        {AppendDiagnostic("Last login selection could not be read: "+ex.Message);return false;}
    }
    private void SaveLoginSelection()
    {
        if(parameterLaunch||!loginSelectionRestored||_frontend is not {IsLoginScreen:true} frontend||frontend.Owner.Accounts.Busy)return;
        var value=new LoginSelection(frontend.Owner.SelectedServer,frontend.Owner.Groups.GroupMode,frontend.Owner.Groups.Selected?.Id,frontend.Username.Text);
        if(value==savedLoginSelection)return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LoginSelectionPath)!);
            string temporary=LoginSelectionPath+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllText(temporary,JsonSerializer.Serialize(value));File.Move(temporary,LoginSelectionPath,true);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
            savedLoginSelection=value;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {AppendDiagnostic("Last login selection could not be saved: "+ex.Message);}
    }
}
