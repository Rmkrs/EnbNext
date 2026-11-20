using EnbNext.Client.Models;

namespace EnbNext.App;

// Deliberately not a record: diagnostic ToString must never print credentials.
internal sealed class StartupLogin
{
    public string Username { get; private init; } = "";
    public string Server { get; private init; } = "sunrise.net-7.org";
    public string Group { get; private init; } = "";
    public Guid Member { get; private init; }
    public string Pipe { get; private init; } = "";
    public string Character { get; private init; } = "";
    public string? Error { get; private init; }

    public static StartupLogin? Parse(string[] args)
    {
        if(args.Length==0)return null;
        var values=new Dictionary<string,string>(StringComparer.Ordinal);
        for(int i=0;i<args.Length;i++)
        {
            string key=args[i];
            if(key is not ("--username" or "--character" or "--server" or "--group" or "--member" or "--launch-pipe") || i+1>=args.Length || values.ContainsKey(key))
                return new(){Error="Use saved credentials: --username and --character, or --group. Password arguments are not supported."};
            values[key]=args[++i];
        }
        string group=values.GetValueOrDefault("--group","");
        string username=values.GetValueOrDefault("--username","").Trim(),character=values.GetValueOrDefault("--character","").Trim();
        Guid member=Guid.Empty;
        if(values.TryGetValue("--member",out var identity)&&!Guid.TryParse(identity,out member))return new(){Error="Invalid group member identity."};
        string pipe=values.GetValueOrDefault("--launch-pipe","");
        if((member!=Guid.Empty||pipe.Length>0)&&(group.Length==0||member==Guid.Empty||!pipe.StartsWith("EnbNext-launch-",StringComparison.Ordinal)))return new(){Error="Invalid group launch context."};
        if(group.Length==0&&(username.Length==0||character.Length==0))return new(){Error="Supply a saved username and character, or a group name."};
        return new(){Username=username,Character=character,Group=group,Member=member,Pipe=pipe,Server=values.GetValueOrDefault("--server","sunrise.net-7.org")};
    }
    public static StartupLogin ForMember(string username,string character,string server)=>new(){Username=username,Character=character,Server=server};

    public CharacterSummary? Resolve(IReadOnlyList<CharacterSummary> characters)
    {
        var matches=characters.Where(c=>!c.IsEmpty &&
            string.Equals(c.FullName,Character,StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length==1 ? matches[0] : null;
    }
}
