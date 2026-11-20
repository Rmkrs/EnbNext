using System.Net;
using System.Text.Json;
using EnbNext.App;
using EnbNext.Client.Models;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class SavedAccountsTests
{
    private const string Reply="""
        {"result":{"accounts":[{"id":"1","username":"game-a"}],"avatars":[{"id":10,"account_id":"1","slot":"0","avatar":{"name":"Pilot","race":"Terran","prof":"Tradesman"}}]}}
        """;
    [Fact]
    public void ApiRequiresCompleteConsistentRosterAndAcceptsExplicitEmpty()
    {
        var accounts=Net7SiteAccounts.Parse(Reply);var a=Assert.Single(accounts);Assert.Equal("game-a",a.Login);
        Assert.Equal(new SavedGameCharacter(0,"Pilot","Terran","Tradesman"),Assert.Single(a.Characters));
        Assert.Empty(Net7SiteAccounts.Parse("{\"result\":{\"accounts\":[],\"avatars\":[]}}"));
        foreach(string invalid in new[]{"{}","null","<html>login</html>","{\"result\":{\"accounts\":[]}}",Reply.Replace("\"slot\":\"0\"","\"slot\":\"5\""),Reply.Replace("\"account_id\":\"1\"","\"account_id\":\"2\"")})
            Assert.Throws<IOException>(()=>Net7SiteAccounts.Parse(invalid));
    }
    [Fact]
    public void PreviewDoesNotMutateAndPreservesUnrelatedAccountsAndIdentity()
    {
        var old=new SavedGameAccount(Guid.NewGuid(),"sunrise.net-7.org","GAME-A",[new(4,"Removed","Progen","Warrior")]);
        var other=new SavedGameAccount(Guid.NewGuid(),"sunrise.net-7.org","other",[new(1,"Other","Jenquai","Explorer")]);
        var ptr=old with {Id=Guid.NewGuid(),Server="ptr.net-7.org"};
        var preview=SavedAccountStore.Preview([old,other,ptr],"sunrise.net-7.org",Net7SiteAccounts.Parse(Reply));
        Assert.Equal("Removed",Assert.Single(old.Characters).Name);
        Assert.Equal(old.Id,preview.Accounts[0].Id);Assert.Equal("Pilot",Assert.Single(preview.Accounts[0].Characters).Name);
        Assert.Same(other,preview.Accounts[1]);Assert.Same(ptr,preview.Accounts[2]);
        Assert.Contains(preview.Changes,c=>c.Contains("remove character Removed"));
        var empty=SavedAccountStore.Preview([old,other],"sunrise.net-7.org",[]);Assert.Equal(new[]{old,other},empty.Accounts);
    }
    [Fact]
    public void MetadataRoundtripContainsNoSecretsAndCorruptLoadDoesNotOverwrite()
    {
        string folder=Path.Combine(Path.GetTempPath(),"enb-accounts-"+Guid.NewGuid().ToString("N")),path=Path.Combine(folder,"accounts.json");
        try
        {
            var store=new SavedAccountStore(path);store.Load();Assert.Empty(store.Accounts);
            var value=new SavedGameAccount(Guid.NewGuid(),"sunrise.net-7.org","example",[new(0,"Pilot","Terran","Trader",150)]);
            store.Save([value]);var fresh=new SavedAccountStore(path);fresh.Load();
            Assert.Equal(value.Id,Assert.Single(fresh.Accounts).Id);Assert.Equal(value.Characters,fresh.Accounts[0].Characters);
            using var json=JsonDocument.Parse(File.ReadAllText(path));Assert.Equal(new[]{"Id","Server","Login","Characters"},json.RootElement[0].EnumerateObject().Select(p=>p.Name));
            File.WriteAllText(path,"broken");Assert.Throws<JsonException>(fresh.Load);Assert.Equal("broken",File.ReadAllText(path));Assert.Equal(value.Id,fresh.Accounts[0].Id);
        }
        finally{if(File.Exists(path))File.Delete(path);if(Directory.Exists(folder))Directory.Delete(folder);}
    }
    [Fact]
    public async Task SiteUsesObservedFormAndReadsAccountsOnlyAfterSignIn()
    {
        var requests=new List<string>();
        using var site=new Net7SiteAccounts(new Handler(async request=>
        {
            requests.Add(request.Method+" "+request.RequestUri!.AbsolutePath);
            if(request.Method==HttpMethod.Post)
            {
                string body=await request.Content!.ReadAsStringAsync();
                Assert.Equal("login=1&uname=forum%2Buser&pwd=p%26%3D+word&remember=1",body);
                Assert.Equal("www.net-7.org",request.RequestUri.Host);
            }
            return new(HttpStatusCode.OK){Content=new StringContent(request.RequestUri!.AbsolutePath=="/api/account"?Reply:"<html></html>")};
        }));
        await site.SignInAsync("forum+user","p&= word",true,default);var roster=await site.ReadAsync(default);
        Assert.Single(roster);Assert.Equal(new[]{"GET /","POST /","GET /api/account"},requests);
    }
    [Fact]
    public async Task ExpiredCookiesAndExternalRedirectsNeverApplyARoster()
    {
        using var expired=new Net7SiteAccounts(new Handler(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        await Assert.ThrowsAsync<SiteSignedOutException>(()=>expired.ReadAsync(default));
        int count=0;
        using var redirect=new Net7SiteAccounts(new Handler(_=>
        {
            count++;var response=new HttpResponseMessage(HttpStatusCode.Found);response.Headers.Location=new("https://example.invalid/");return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<IOException>(()=>redirect.ReadAsync(default));Assert.Equal(1,count);
    }
    [Fact]
    public void RememberedSessionContainsOnlySiteCookiesAndRoundtrips()
    {
        using var first=new Net7SiteAccounts(new Handler(_=>throw new InvalidOperationException()));
        first.RestoreCookies("""
            [{"Name":"session","Value":"synthetic-cookie","Domain":"www.net-7.org","Path":"/","Expires":"0001-01-01T00:00:00","Secure":true,"HttpOnly":true},
             {"Name":"foreign","Value":"not-restored","Domain":"example.invalid","Path":"/","Expires":"0001-01-01T00:00:00","Secure":true,"HttpOnly":true}]
            """);
        string saved=first.ExportCookies();Assert.Contains("synthetic-cookie",saved);Assert.DoesNotContain("not-restored",saved);
        using var second=new Net7SiteAccounts(new Handler(_=>throw new InvalidOperationException()));
        second.RestoreCookies(saved);Assert.Equal(saved,second.ExportCookies());
    }
    [Fact]
    public async Task WindowsCredentialManagerRoundtripUsesAnIsolatedKey()
    {
        if(!OperatingSystem.IsWindows())return;
        var store=new AccountSecretStore();string key="test/"+Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(await store.ReadAsync(key));await store.WriteAsync(key,"synthetic Ω secret");
            Assert.Equal("synthetic Ω secret",await store.ReadAsync(key));await store.WriteAsync(key,"replacement");
            Assert.Equal("replacement",await store.ReadAsync(key));
        }
        finally{await store.DeleteAsync(key);}
        Assert.Null(await store.ReadAsync(key));
        Assert.Equal(AccountSecretStore.GameKey("SUNRISE.NET-7.ORG"," Pilot "),AccountSecretStore.GameKey("sunrise.net-7.org","pilot"));
        Assert.NotEqual(AccountSecretStore.GameKey("ptr.net-7.org","pilot"),AccountSecretStore.GameKey("sunrise.net-7.org","pilot"));
    }
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> respond):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>respond(request);}
}
