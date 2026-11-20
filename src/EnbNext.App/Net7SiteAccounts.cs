using System.Net;
using System.Text.Json;
using EnbNext.Client.Models;

namespace EnbNext.App;

internal sealed class SiteSignedOutException():IOException("Net-7.org login expired or was not accepted. Enter your forum username and password.");
internal sealed class Net7SiteAccounts : IDisposable
{
    private static readonly Uri Origin=new("https://www.net-7.org/");
    private readonly CookieContainer cookies=new();
    private readonly HttpClient http;
    public Net7SiteAccounts(HttpMessageHandler? handler=null)
    {
        http=new(handler??new HttpClientHandler{CookieContainer=cookies,UseCookies=true,AllowAutoRedirect=false}){BaseAddress=Origin,Timeout=TimeSpan.FromSeconds(30)};
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EnbNext");
    }
    public async Task SignInAsync(string user,string password,bool remember,CancellationToken token)
    {
        using(var front=await GetAsync("",token))Ensure(front);
        var fields=new List<KeyValuePair<string,string>>{new("login","1"),new("uname",user),new("pwd",password)};
        if(remember)fields.Add(new("remember","1"));
        using var form=new FormUrlEncodedContent(fields);
        using var response=await http.PostAsync("",form,token);
        if((int)response.StatusCode is >=300 and <400)
        {using var next=await GetAsync(SameOrigin(response.Headers.Location).AbsoluteUri,token);Ensure(next);}
        else Ensure(response);
    }
    private static Uri SameOrigin(Uri? uri)
    {
        if(uri is null)throw new IOException("Net-7.org returned an invalid redirect.");
        var next=new Uri(Origin,uri);
        if(next.Scheme!=Origin.Scheme||next.Host!=Origin.Host||next.Port!=Origin.Port)throw new IOException("Net-7.org redirected outside its sign-in site.");
        return next;
    }
    private async Task<HttpResponseMessage> GetAsync(string path,CancellationToken token)
    {
        for(int count=0;count<5;count++)
        {
            var response=await http.GetAsync(path,token);
            if((int)response.StatusCode is <300 or >=400)return response;
            try{path=SameOrigin(response.Headers.Location).AbsoluteUri;}finally{response.Dispose();}
        }
        throw new IOException("Net-7.org returned too many redirects.");
    }
    private static void Ensure(HttpResponseMessage response)
    {
        if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)throw new SiteSignedOutException();
        if(!response.IsSuccessStatusCode)throw new IOException("Net-7.org is unavailable. Please try again later.");
    }
    public async Task<SavedGameAccount[]> ReadAsync(CancellationToken token)
    {using var response=await GetAsync("api/account",token);Ensure(response);return Parse(await response.Content.ReadAsStringAsync(token));}
    public string ExportCookies()=>JsonSerializer.Serialize(cookies.GetAllCookies().Where(c=>!c.Expired)
        .Select(c=>new StoredCookie(c.Name,c.Value,c.Domain,c.Path,c.Expires,c.Secure,c.HttpOnly)).ToArray());
    public void RestoreCookies(string value)
    {
        foreach(var c in JsonSerializer.Deserialize<StoredCookie[]>(value)??[])
            if(c.Domain.TrimStart('.').Equals(Origin.Host,StringComparison.OrdinalIgnoreCase)||c.Domain.TrimStart('.').Equals("net-7.org",StringComparison.OrdinalIgnoreCase))
                cookies.Add(new Cookie(c.Name,c.Value,c.Path,c.Domain){Expires=c.Expires,Secure=c.Secure,HttpOnly=c.HttpOnly});
    }
    private sealed record StoredCookie(string Name,string Value,string Domain,string Path,DateTime Expires,bool Secure,bool HttpOnly);
    public static SavedGameAccount[] Parse(string json)
    {
        try
        {
            using var document=JsonDocument.Parse(json);
            var result=document.RootElement.GetProperty("result");
            var accounts=result.GetProperty("accounts").EnumerateArray().ToArray();var avatars=result.GetProperty("avatars").EnumerateArray().ToArray();
            var ids=new HashSet<int>();var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var output=new Dictionary<int,SavedGameAccount>();
            foreach(var a in accounts)
            {
                int id=Number(a,"id");string name=a.GetProperty("username").GetString()!.Trim();
                if(id<=0||name.Length==0||!ids.Add(id)||!names.Add(name))throw new FormatException();
                output.Add(id,new(Guid.Empty,"sunrise.net-7.org",name,[]));
            }
            var avatarIds=new HashSet<int>();var avatarNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var slots=new HashSet<(int,int)>();
            foreach(var a in avatars)
            {
                int id=Number(a,"id"),account=Number(a,"account_id"),slot=Number(a,"slot");var info=a.GetProperty("avatar");string name=info.GetProperty("name").GetString()!.Trim();
                if(id<=0||slot is <0 or >4||name.Length==0||!output.ContainsKey(account)||!avatarIds.Add(id)||!avatarNames.Add(name)||!slots.Add((account,slot)))throw new FormatException();
                string? Text(string key)=>info.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
                string? profession=Text("prof");
                var owner=output[account];output[account]=owner with {Characters=[..owner.Characters,new(slot,name,Text("race"),profession)]};
            }
            return output.Values.Select(a=>a with {Characters=a.Characters.OrderBy(c=>c.Slot).ToArray()}).ToArray();
        }
        catch(Exception ex) when(ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or NullReferenceException)
        {throw new IOException("Net-7.org returned an incomplete or inconsistent account list. No saved accounts were changed.");}
    }
    private static int Number(JsonElement value,string key)
    {var item=value.GetProperty(key);return item.ValueKind==JsonValueKind.String?int.Parse(item.GetString()!,System.Globalization.CultureInfo.InvariantCulture):item.GetInt32();}
    public void Dispose()=>http.Dispose();
}
