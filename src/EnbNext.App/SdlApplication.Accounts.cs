using System.Net;
using System.Text.Json;
using EnbNext.Client.Models;
using EnbNext.Engine;

namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private readonly SavedAccountStore _accounts=new(SavedAccountStore.DefaultPath);
    private readonly AccountSecretStore _accountSecrets=new();
    private bool _accountsLoaded,_siteSessionLoaded,_siteCookieRemembered;
    private Net7SiteAccounts? _siteAccounts;
    private string? _siteUsername,_savedAccountLogin,_savedAccountServer;
    private SavedGameAccount[]? _accountPreview;
    private CancellationTokenSource? _accountOperation;
    private sealed record RememberedSite(string Username,string Cookies);
    private void ConfigureSavedAccounts(EnbFrontendController frontend)
    {
        var panel=frontend.Owner.Accounts;
        panel.Opened=async ()=>await AccountOperationAsync(panel,async token=>
        {await LoadAccountsAsync();token.ThrowIfCancellationRequested();panel.Accounts=_accounts.Accounts;panel.Status="Select an account; enter its game password once if needed.";if(RestoreLoginSelection(frontend))await RestoreLoginPasswordAsync(frontend,token);});
        panel.Closed=()=>{_accountOperation?.Cancel();_accountPreview=null;};
        panel.Selected=async id=>await AccountOperationAsync(panel,async token=>
        {
            var account=_accounts.Accounts.Single(a=>a.Id==id);
            frontend.Username.Text=account.Login;frontend.Password.Text="";
            string? password=null;
            try{password=await _accountSecrets.ReadAsync(AccountSecretStore.GameKey(account.Server,account.Login),token);}
            catch(IOException){/* Selecting a saved username still permits manual login. */}
            token.ThrowIfCancellationRequested();
            if(!SavedAccountStore.Same(frontend.Username.Text,account.Login))return;
            frontend.Username.Text=account.Login;frontend.Password.Text=password??"";
            panel.RememberGameLogin=true;panel.Close();
            frontend.Username.IsEnabled=frontend.Password.IsEnabled=true;frontend.Password.Focus();
        });
        panel.ForgetRequested=async id=>
        {
            var account=_accounts.Accounts.FirstOrDefault(a=>a.Id==id);if(account is null)return;
            if(!await frontend.ConfirmAsync("Forget saved login '"+account.Login+"'? This removes its saved password from this computer, not the game account."))return;
            await AccountOperationAsync(panel,async token=>
            {
                try
                {
                    await _accountSecrets.DeleteAsync(AccountSecretStore.GameKey(account.Server,account.Login),token);
                    await Task.Run(()=>_accounts.Save(_accounts.Accounts.Where(a=>a.Id!=id).ToArray()),token);
                    panel.Accounts=_accounts.Accounts;panel.Status="Saved account forgotten.";
                    if(SavedAccountStore.Same(frontend.Owner.SelectedServer,account.Server)&&SavedAccountStore.Same(frontend.Username.Text,account.Login))
                    {frontend.Username.Text=frontend.Password.Text="";panel.RememberGameLogin=false;}
                }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
                {await frontend.ShowMessageAsync("Could not fully forget this saved login. "+ex.Message);}
            });
        };
        panel.SyncOpened=async ()=>await AccountOperationAsync(panel,async token=>
        {
            // The site API describes the public live server, not PTR/dev rosters.
            if(frontend.Owner.SelectedServer!="sunrise.net-7.org")throw new IOException("Select sunrise.net-7.org before importing website accounts.");
            if(!_siteSessionLoaded)
            {
                _siteSessionLoaded=true;
                string? value=await _accountSecrets.ReadAsync("site-login",token);
                if(value is not null)
                {
                    var saved=JsonSerializer.Deserialize<RememberedSite>(value)??throw new IOException("Remembered website login could not be read. Use Forget website login.");
                    _siteAccounts??=new();_siteAccounts.RestoreCookies(saved.Cookies);_siteUsername=saved.Username;_siteCookieRemembered=true;
                }
            }
            token.ThrowIfCancellationRequested();frontend.Owner.SiteUsername.Text=_siteUsername??"";
            panel.RememberSiteLogin=_siteCookieRemembered;
            panel.Status="Use your Net-7.org website/forum credentials.";
        });
        panel.ReadRequested=async ()=>
        {
            string username=frontend.Owner.SiteUsername.Text.Trim(),password=frontend.Owner.SitePassword.Text;
            if(panel.Busy)return;
            frontend.Owner.SitePassword.Text="";bool remember=panel.RememberSiteLogin;
            panel.Status="Importing accounts...";
            await AccountOperationAsync(panel,async token=>
            {
                if(frontend.Owner.SelectedServer!="sunrise.net-7.org")throw new IOException("Select sunrise.net-7.org before importing website accounts.");
                await LoadAccountsAsync();
                if(username.Length==0||password.Length==0&&(_siteAccounts is null||!SavedAccountStore.Same(_siteUsername??"",username)))
                    throw new IOException("Enter your Net-7.org username and password.");
                if(password.Length>0)
                {
                    if(username.Length==0)throw new IOException("Enter your forum username.");
                    _siteAccounts?.Dispose();_siteAccounts=new();
                    await _siteAccounts.SignInAsync(username,password,remember,token);token.ThrowIfCancellationRequested();_siteUsername=username;
                }
                else if(_siteUsername is not null&&!SavedAccountStore.Same(_siteUsername,username))throw new SiteSignedOutException();
                var site=_siteAccounts??=new();var roster=await site.ReadAsync(token);
                token.ThrowIfCancellationRequested();
                if(remember)
                {
                    await _accountSecrets.WriteAsync("site-login",JsonSerializer.Serialize(new RememberedSite(_siteUsername??username,site.ExportCookies())),token);
                    token.ThrowIfCancellationRequested();_siteCookieRemembered=true;
                }
                else if(_siteCookieRemembered){await _accountSecrets.DeleteAsync("site-login",token);token.ThrowIfCancellationRequested();_siteCookieRemembered=false;}
                var preview=SavedAccountStore.Preview(_accounts.Accounts,"sunrise.net-7.org",roster);
                token.ThrowIfCancellationRequested();_accountPreview=preview.Accounts;panel.Preview(roster.Select(a=>a.Login+": "+(a.Characters.Length==0?"No characters":string.Join(", ",a.Characters.Select(c=>c.Name)))).ToArray());
            });
            password="";
        };
        panel.ApplyRequested=async ()=>await AccountOperationAsync(panel,async token=>
        {
            if(_accountPreview is not {} preview)return;
            await Task.Run(()=>_accounts.Save(preview),token);token.ThrowIfCancellationRequested();_accountPreview=null;panel.Accounts=_accounts.Accounts;
            panel.Close();panel.Status="Accounts imported. Select one and enter its game password once.";
        });
        panel.ForgetSiteRequested=async ()=>await AccountOperationAsync(panel,async token=>
        {
            await _accountSecrets.DeleteAsync("site-login",token);token.ThrowIfCancellationRequested();_siteAccounts?.Dispose();_siteAccounts=null;_siteUsername=null;_siteCookieRemembered=false;
            frontend.Owner.SiteUsername.Text=frontend.Owner.SitePassword.Text="";panel.RememberSiteLogin=false;panel.Status="Website login forgotten.";
        });
        panel.Opened?.Invoke();
    }
    private async Task ShowLoginWithSavedAccountAsync(EnbFrontendController frontend)
    {
        frontend.ShowLogin();
        await AccountOperationAsync(frontend.Owner.Accounts,async token=>
        {
            await LoadAccountsAsync();token.ThrowIfCancellationRequested();
            RestoreLoginSelection(frontend);await RestoreLoginPasswordAsync(frontend,token);
        });
    }
    private async Task RestoreLoginPasswordAsync(EnbFrontendController frontend,CancellationToken token)
    {
        string server=frontend.Owner.SelectedServer,login=frontend.Username.Text;
        var account=_accounts.Find(server,login);if(account is null)return;
        string? password=await _accountSecrets.ReadAsync(AccountSecretStore.GameKey(server,account.Login),token);
        token.ThrowIfCancellationRequested();
        if(!SavedAccountStore.Same(frontend.Owner.SelectedServer,server)||
            !SavedAccountStore.Same(frontend.Username.Text,login)||frontend.Password.Text.Length!=0)return;
        frontend.Password.Text=password??"";
        frontend.Owner.Accounts.RememberGameLogin=true;
    }
    private async Task LoadAccountsAsync()
    {if(_accountsLoaded)return;await Task.Run(_accounts.Load);_accountsLoaded=true;}
    private async Task AccountOperationAsync(EnbSavedAccountsPanel panel,Func<CancellationToken,Task> action)
    {
        if(panel.Busy)return;
        using var operation=new CancellationTokenSource();_accountOperation=operation;panel.Busy=true;
        try{await action(operation.Token);}
        catch(OperationCanceledException){if(ReferenceEquals(_accountOperation,operation)&&panel.IsOpen)panel.Status="Request cancelled or timed out. Please try again.";}
        catch(SiteSignedOutException ex){if(!ReferenceEquals(_accountOperation,operation)||!panel.IsOpen)return;panel.ShowSync();panel.Status=ex.Message;_siteAccounts?.Dispose();_siteAccounts=null;}
        catch(Exception ex) when(ex is IOException or HttpRequestException or JsonException or CookieException or UnauthorizedAccessException)
        {if(ReferenceEquals(_accountOperation,operation)&&panel.IsOpen)panel.Status=ex is HttpRequestException?"Net-7.org could not be reached. Please try again later.":ex is JsonException or CookieException?"The account data could not be read. Please try importing again.":ex.Message;}
        finally{if(ReferenceEquals(_accountOperation,operation)){_accountOperation=null;panel.Busy=false;}}
    }
    private async Task RememberGameLoginAsync(string server,string login,string password,IReadOnlyList<CharacterSummary> characters,bool storedPassword=false)
    {
        _savedAccountLogin=login;_savedAccountServer=server;
        try
        {
            await LoadAccountsAsync();var old=_accounts.Find(server,login);
            if(old is null&&_frontend?.Owner.Accounts.RememberGameLogin!=true)return;
            if(!storedPassword)await _accountSecrets.WriteAsync(AccountSecretStore.GameKey(server,login),password);
            var value=new SavedGameAccount(old?.Id??Guid.NewGuid(),server,login,SavedAccountStore.Roster(characters));
            if(old is null||old.Login!=value.Login||!old.Characters.SequenceEqual(value.Characters))await Task.Run(()=>_accounts.Save([.._accounts.Accounts.Where(a=>a.Id!=value.Id),value]));
            if(_frontend is {} frontend)frontend.Owner.Accounts.Accounts=_accounts.Accounts;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {if(_frontend is {} frontend)await frontend.ShowMessageAsync("Login succeeded, but it could not be remembered. "+(ex is JsonException?"The saved account list could not be read.":ex.Message));}
    }
    private async Task RefreshSavedRosterAsync(IReadOnlyList<CharacterSummary> characters)
    {
        if(!_accountsLoaded||_savedAccountLogin is null||_savedAccountServer is null)return;
        var account=_accounts.Find(_savedAccountServer,_savedAccountLogin);if(account is null)return;
        var roster=SavedAccountStore.Roster(characters);if(roster.SequenceEqual(account.Characters))return;
        try{await Task.Run(()=>_accounts.Save(_accounts.Accounts.Select(a=>a.Id==account.Id?a with {Characters=roster}:a).ToArray()));}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {if(_frontend is {} frontend)await frontend.ShowMessageAsync("The saved character list could not be updated.");}
    }
    private void DisposeSavedAccounts(){_accountOperation?.Cancel();_siteAccounts?.Dispose();_siteAccounts=null;}
}
