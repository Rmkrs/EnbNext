using EnbNext.Client;
using EnbNext.Client.Models;
using EnbNext.Presentation;

namespace EnbNext.App;

internal sealed record DiscordPresenceActivity(string Details,string State,long StartedAt)
{
    public int? PartySize {get;init;}
    public static DiscordPresenceActivity Create(string location,bool docked,string profession,ulong level,string? name,long startedAt)
        =>new(Limit(docked?"Docked at "+location:location),
            Limit(string.IsNullOrEmpty(name)?$"{profession} · Level {level}":$"{name} · {profession} · Level {level}"),startedAt);
    internal static DiscordPresenceActivity Combine(DiscordPresenceActivity self,string sector,IEnumerable<(string Sector,DiscordPresenceActivity Presence)> peers)
    {
        int count=1;long started=self.StartedAt;var sectors=new HashSet<string>(StringComparer.OrdinalIgnoreCase){sector};
        foreach(var peer in peers){count++;sectors.Add(peer.Sector);started=Math.Min(started,peer.Presence.StartedAt);}
        return count==1?self:new(Limit(sectors.Count==1?sector:$"Across {sectors.Count} sectors"),$"Multiboxing \u00b7 {count} ships",started){PartySize=count};
    }
    // Discord's text fields are limited to 128 UTF-8 bytes. Keep complete runes.
    private static string Limit(string value)
    {
        int bytes=0,chars=0;
        foreach(var rune in value.EnumerateRunes())
        {if(bytes+rune.Utf8SequenceLength>128)break;bytes+=rune.Utf8SequenceLength;chars+=rune.Utf16SequenceLength;}
        return chars==value.Length?value:value[..chars];
    }
}

// Game-session time and last settled location survive sector and IPC handoffs.
internal sealed class DiscordPresenceSession
{
    private long? enteredAt;
    private string? location;
    public string? SettledLocation=>location;
    private bool docked;
    private ulong level;
    public void Reset(){enteredAt=null;location=null;}
    public DiscordPresenceActivity? Update(ClientLifecycleState? state,bool worldOpen,CharacterSummary? character,
        EnbSceneSnapshot? scene,EnbDiscordPresenceOptions? options,long now)
    {
        if(!worldOpen || state is null or ClientLifecycleState.Disconnected or ClientLifecycleState.Disconnecting or ClientLifecycleState.Faulted or ClientLifecycleState.CharacterSelection)
        {Reset();return null;}
        if(state==ClientLifecycleState.InWorld)enteredAt??=now;
        if(character is null || enteredAt is not long started)return null;
        if(state==ClientLifecycleState.InWorld && scene is {IsTransitioning:false})
        {
            location=scene.LocationTransition?.DestinationSectorName??character.SectorName;
            docked=scene.ActiveStarbaseId is not null;
            level=scene.Music is {} music?(ulong)Math.Max(0,music.OverallLevel):character.OverallLevel;
        }
        if(options?.Enabled!=true || string.IsNullOrWhiteSpace(location))return null;
        return DiscordPresenceActivity.Create(location,docked,character.ClassName,level,
            options.IncludeCharacterName?character.FullName:null,started);
    }
}
