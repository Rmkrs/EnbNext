using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnbNext.Client;
using EnbNext.Engine;
using EnbNext.Presentation;

namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private EnbMultiboxOptions multiboxOptions=new();
    private uint? multiboxCharacter;
    private string multiboxAccount="";
    private MultiboxPeer? multibox;
    private Task multiboxRetiring=Task.CompletedTask;
    private readonly Guid multiboxInstance=Guid.NewGuid();
    private long multiboxNext;
    private FileStream? presenceLease;
    private MultiboxState?[] panelPeers=[];
    private readonly Dictionary<Guid,bool> formationMemberDocked=[];
    /// <summary>The server offers a formation only to members in space. The leader
    /// waits for docked multibox members and re-offers it when one of them arrives.</summary>
    private void ObserveFormationMembers(EnbClient client,MultiboxState self)
    {
        var members=multibox!.Peers.Where(p=>p.Instance!=self.Instance&&p.Server==self.Server&&self.GroupMembers.Contains(p.Avatar)).ToArray();
        client.ConfigureMultiboxFormationMembers(members.Any(p=>p.StationContext is not null));
        foreach(var member in members)
        {
            bool docked=member.StationContext is not null;
            if(formationMemberDocked.TryGetValue(member.Instance,out bool was)&&was&&!docked&&member.Context is not null&&self.Leader)
                client.RequestMultiboxFormationRefresh();
            if(docked||member.Context is not null)formationMemberDocked[member.Instance]=docked;
        }
        foreach(var gone in formationMemberDocked.Keys.Where(k=>!members.Any(m=>m.Instance==k)).ToArray())formationMemberDocked.Remove(gone);
    }
    private Guid? invitedLaunch;
    /// <summary>The clients (process instances) the launch's invite went to: a member launched again is a new instance.</summary>
    private readonly HashSet<Guid> invitedInstances=[];
    private Guid launchInput;
    private Task presenceRetiring=Task.CompletedTask;
    private bool combinedPresenceActive;
    private string MultiboxDirectory=>Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_preferencesPath))!,"multibox");
    private string MultiboxOptionsPath=>MultiboxOptionsPathFor(_savedAccountServer,_savedAccountLogin,multiboxCharacter);
    private string MultiboxOptionsPathFor(string? server,string? login,uint? character)=>Path.Combine(MultiboxDirectory,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((server??"").ToUpperInvariant()+"/"+(login??"").ToUpperInvariant())))[..16]+"-"+character+".json");
    private void SaveMultiboxOptions(EnbMultiboxOptions value)
    {
        if(multiboxCharacter is null)return;
        value=value with {ShortcutButtons=Math.Clamp(value.ShortcutButtons,0,12),EncounterPauseSeconds=Math.Clamp(value.EncounterPauseSeconds,1,60)};
        Directory.CreateDirectory(MultiboxDirectory);
        string temporary=MultiboxOptionsPath+"."+multiboxInstance.ToString("N")+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(value));File.Move(temporary,MultiboxOptionsPath,true);
        multiboxOptions=value with {ShortcutButtons=Math.Clamp(value.ShortcutButtons,0,12),EncounterPauseSeconds=Math.Clamp(value.EncounterPauseSeconds,1,60)};multiboxNext=0;
    }
    private void TickMultibox()
    {
        PumpMultiboxCommands();TickRemoteShortcutFeedback();TickSharedHarvest();TickSharedReactor();
        if(_client is {} routeClient)
        {
            routeClient.AnswerExpectedWormhole();
            routeClient.Autopilot.Wormhole??=OpenRouteWormholeAsync;
            if(worldScreen is {} routeWorld){routeWorld.Owner.Ui.Map.Guidance.Travel??=()=>_routeTravel;routeWorld.Owner.AutopilotHold??=()=>RegroupHolds()||FormationRepairHolds();}
            AdvanceRegroup(routeClient);
            RepairFormationLinks(routeClient);
        }
        long now=Environment.TickCount64;if(now<multiboxNext)return;multiboxNext=now+1000;
        uint? character=worldScreen is not null?_client?.SelectedFixedSessionId:null;
        string account=(_savedAccountServer??"")+"/"+(_savedAccountLogin??"");
        if(character!=multiboxCharacter||account!=multiboxAccount)
        {
            StopMultibox();multiboxAccount=account;multiboxCharacter=character;multiboxOptions=new();
            if(character is not null)
                try
                {
                    if(File.Exists(MultiboxOptionsPath))
                    {
                        string json=File.ReadAllText(MultiboxOptionsPath);
                        multiboxOptions=JsonSerializer.Deserialize<EnbMultiboxOptions>(json)??new();
                        // Before the cockpit toggle, "Show damage meter" was the only switch.
                        using var document=JsonDocument.Parse(json);
                        if(!document.RootElement.TryGetProperty(nameof(EnbMultiboxOptions.EnableDamageMeter),out _)&&multiboxOptions.ShowDamageMeter)
                            multiboxOptions=multiboxOptions with {EnableDamageMeter=true};
                    }
                }
                catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException){AppendDiagnostic("Multibox preferences: "+ex.Message);}
            multiboxOptions=multiboxOptions with {ShortcutButtons=Math.Clamp(multiboxOptions.ShortcutButtons,0,12),EncounterPauseSeconds=Math.Clamp(multiboxOptions.EncounterPauseSeconds,1,60),AutoFormation=Math.Clamp(multiboxOptions.AutoFormation,0,3),StationFollowDistance=Math.Clamp(multiboxOptions.StationFollowDistance,1,30)};
            LoadRemoteShortcutBindings();
        }
        if(!multiboxOptions.Enabled||character is null||_client is not {} client||client.State is ClientLifecycleState.Disconnected or ClientLifecycleState.Disconnecting or ClientLifecycleState.Faulted or ClientLifecycleState.CharacterSelection||_selectedCharacter is null)
        {_client?.ConfigureMultiboxLeaderFormation(0);StopStationMultibox();StopMultibox();if(_client is {} solo)UpdateRouteTravel(solo,null);return;}
        if(multibox is null&&client.State!=ClientLifecycleState.InWorld)return;
        var scene=client.LatestPresentationSnapshot;if(scene is null||scene.LocalAvatarObjectId is not uint avatar||avatar==0)return;
        var group=scene.Hud.Group;
        if(multiboxOptions.CombinePresence&&!combinedPresenceActive){RetirePresenceOwner(true);combinedPresenceActive=true;}
        else if(!multiboxOptions.CombinePresence)combinedPresenceActive=false;
        var stationPlace=StationMultiboxPlace();
        var presence=_presenceSession.Update(client.State,true,_selectedCharacter,scene,_preferences.DiscordPresence,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var state=new MultiboxState(multiboxInstance,_savedAccountServer??"",new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(account.ToUpperInvariant())).AsSpan(0,16)),
            character.Value,_selectedCharacter.FullName,avatar,CurrentGroup?.Id,groupLaunch is not null,group?.IsLeader==true,
            group?.Members.Where(m=>m.GameId!=0).Select(m=>m.GameId).ToArray()??[],_presenceSession.SettledLocation??_selectedCharacter.SectorName,
            presence,multiboxOptions.CombinePresence&&presenceRetiring.IsCompleted)
        {Context=client.MultiboxContext,Shortcuts=worldScreen?.Owner.Cockpit.CaptureRemoteShortcuts()??[],FollowTarget=multiboxOptions.FollowLeaderTarget,FollowFire=multiboxOptions.FollowLeaderFire,
            StationContext=client.StationMultiboxContext,FollowDock=multiboxOptions.FollowLeaderDock,FollowLaunch=multiboxOptions.FollowLeaderLaunch,
            StationPose=stationPlace.Pose,StationSpawn=stationPlace.Spawn,PanelClaim=panelClaim,Focus=focusLayout,
            RedDragon=RedDragonStanding(client,scene),CreateWormhole=WormholeRanks(scene).Create,ExtendedWormhole=WormholeRanks(scene).Extended,
            FollowWormhole=multiboxOptions.FollowLeaderWormhole,InFormation=group is {IsInGroup:true,Formation:not 0},Harvest=publishedHarvest,Reactor=publishedReactor};
        UpdateRouteTravel(client,state);
        client.ConfigureMultiboxLeaderFormation(multiboxOptions.AutoFormation);
        if(multibox is null)
        {
            if(!multiboxRetiring.IsCompleted)return;
            try{multibox=new(MultiboxDirectory,state);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){AppendDiagnostic("Local multiboxing unavailable: "+ex.Message);return;}
        }
        else multibox.Publish(state);
        ObserveFormationMembers(client,state);
        ApplyStationMultibox(state);
        BindMultiboxInput(client);
        RefreshMultiboxPanel(state);
        RefreshRemoteShortcuts(state);
        SyncAdoptedMembers();
        if(multiboxOptions.InviteAfterLaunch && launchInput!=Guid.Empty && activeGroup is {} saved && groupLaunch is {} launch && (adoptedLaunch||launch.Members.All(m=>m.State=="Online")))
        {
            // A launch taken up in game also invites the group's characters that were already running.
            var ready=multibox.Peers.Where(p=>p.Server==state.Server&&(p.LaunchGroup==saved.Id||adoptedLaunch&&saved.Members.Any(m=>SavedAccountStore.Same(m.Character,p.Name)))).ToArray();
            if(invitedLaunch!=saved.Id)
            {
                if(ready.Length==saved.Members.Length-1){invitedLaunch=saved.Id;invitedInstances.UnionWith(ready.Select(p=>p.Instance));dispatch.Observe(InviteMultiboxAsync(ready,launchInput));}
            }
            // A member launched again from the panel (a new client) is invited back once it is in the game, while
            // this client may invite (no group, or its leader) and the member is not in the group already.
            else if(!state.GroupMembers.Any()||state.Leader)
            {
                var late=ready.Where(p=>p.Avatar!=0&&!invitedInstances.Contains(p.Instance)&&!state.GroupMembers.Contains(p.Avatar)).ToArray();
                if(late.Length>0){invitedInstances.UnionWith(late.Select(p=>p.Instance));dispatch.Observe(InviteMultiboxAsync(late,launchInput));}
            }
        }
    }
    private void RefreshMultiboxPanel(MultiboxState self)
    {
        var peers=multibox!.Peers.Where(p=>p.Server==self.Server).OrderBy(p=>p.Name,StringComparer.OrdinalIgnoreCase).ToArray();
        // The client where the user last chose the panel shows it; before any choice, the
        // launching host, else the oldest client outside a group launch.
        long claim=peers.Select(p=>p.PanelClaim).DefaultIfEmpty(0).Max();
        panelOwned=panelClaim!=0||claim!=0
            ?panelClaim>claim||panelClaim==claim&&peers.Where(p=>p.PanelClaim==claim).All(p=>p.Instance.CompareTo(self.Instance)>0)
            :self.Host||groupReporter is null&&!peers.Any(p=>p.Host)&&peers.All(p=>p.Instance.CompareTo(self.Instance)>0);
        var group=CurrentGroup;
        // Solo with one character online there is nothing to manage.
        bool show=panelOwned&&multiboxOptions.ShowCharacterPanel&&(group is not null||groupLaunch is not null||peers.Length>0);
        if(!show){if(worldScreen is {} hiddenWorld&&ReferenceEquals(hiddenWorld.Owner.Hud.LaunchPanel,launchPanel))hiddenWorld.Owner.Hud.LaunchPanel=null;return;}
        launchPanel??=new(_frontend?.Assets??_client!.RetailAssets!);
        launchPanel.GroupName=group?.Name??"My characters";
        var rows=new List<(string,string,string)>();var identities=new List<MultiboxState?>();
        if(LaunchRows&&groupLaunch is {} launch)
            foreach(var member in launch.Members)
            {rows.Add((member.Definition.Character,member.State,member.Error));identities.Add(member.Definition.Character==self.Name?self:peers.FirstOrDefault(p=>p.Name==member.Definition.Character&&p.LaunchGroup==self.LaunchGroup));}
        else if(group is not null)
        {
            // A group chosen in game lists its members as they are; nothing is launched.
            if(!group.Members.Any(m=>SavedAccountStore.Same(m.Character,self.Name))){rows.Add((self.Name,"Online",""));identities.Add(self);}
            foreach(var member in group.Members)
            {
                var peer=SavedAccountStore.Same(member.Character,self.Name)?self:peers.FirstOrDefault(p=>SavedAccountStore.Same(p.Name,member.Character));
                rows.Add((member.Character,peer is null?"Offline":"Online",""));identities.Add(peer);
            }
        }
        else{rows.Add((self.Name,"Online",""));identities.Add(self);}
        // A group chosen in game (no launch of it here yet): Launch takes the group up and starts the member.
        launchPanel.LaunchRequested=LaunchRows?launchRetry:group is {} chosen?index=>
        {
            if(launchPanel?.Members is {} shown&&(uint)index<(uint)shown.Length)dispatch.Observe(AdoptGroupAsync(chosen,self.Name,shown[index].Item1));
        }:null;
        foreach(var peer in peers)
            if(!identities.Any(p=>p?.Instance==peer.Instance)){rows.Add((peer.Name,"Online",""));identities.Add(peer);}
        panelPeers=identities.ToArray();launchPanel.Members=rows.ToArray();
        launchPanel.Details=panelPeers.Select(p=>p is null?"":MultiboxStatus(p,self)).ToArray();
        launchPanel.InviteEnabled=panelPeers.Select(p=>p is not null&&p.Instance!=self.Instance&&(!self.GroupMembers.Any()||self.Leader)&&!self.GroupMembers.Contains(p.Avatar)).ToArray();
        launchPanel.InviteRequested=index=>
        {
            var targets=index<0?panelPeers.Where((p,i)=>p is not null&&launchPanel.InviteEnabled[i]).Select(p=>p!).ToArray():
                (uint)index<(uint)panelPeers.Length&&panelPeers[index] is {} peer?[peer]:[];
            dispatch.Observe(InviteMultiboxAsync(targets,Guid.NewGuid()));
        };
        launchPanel.Invalidate();if(worldScreen is {} world){launchPanel.Layout=world.Owner.Ui.Layout;world.Owner.Hud.LaunchPanel=launchPanel;}
    }
    // Row tooltip status: where the character is, whether it is docked, and its group role.
    private static string MultiboxStatus(MultiboxState state,MultiboxState self)
    {
        string where=state.Sector.Length>0?state.Sector:"Unknown location";
        string place=state.StationContext is not null?"Docked":"In space";
        string group=state.Leader?"Group leader":state.GroupMembers.Length>0?(state.GroupMembers.Contains(self.Avatar)?"In your group":"In another group"):"Not grouped";
        return where+"\n"+place+" - "+group;
    }
    private async Task InviteMultiboxAsync(MultiboxState[] targets,Guid input)
    {
        // One finite batch admitted by one user input (or that launch's explicit opt-in).
        if(input==Guid.Empty)return;
        foreach(var target in targets)
        {
            if(multibox is null||_client is not {} client||!multibox.Peers.Any(p=>p.Instance==target.Instance&&p.Character==target.Character&&p.Avatar==target.Avatar&&p.Server==target.Server))continue;
            if(await client.InviteOwnCharacterAsync(target.Avatar))worldScreen?.Owner.Hud.QueueUtilitySound("SR_scifielectronic40.wav");
        }
    }
    private void StopMultibox()
    {
        focusLayout=null;
        BindMultiboxInput(null);
        worldScreen?.Owner.Inventory.ClearRemoteShortcuts();
        if(multibox is {} old){multibox=null;multiboxRetiring=old.DisposeAsync().AsTask();dispatch.Observe(multiboxRetiring);}
        RetirePresenceOwner();combinedPresenceActive=false;
        if(groupLaunch is null&&launchPanel is {} panel){if(worldScreen is {} world)world.Owner.Hud.LaunchPanel=null;panel.Dispose();launchPanel=null;}
        else if(launchPanel is {} existing){existing.InviteEnabled=[];existing.InviteRequested=null;existing.Invalidate();}
    }
    private void RetirePresenceOwner(bool includeIndividual=false)
    {
        var lease=presenceLease;if(lease is null&&(!includeIndividual||_discordPresence is null))return;presenceLease=null;
        var publisher=_discordPresence;_discordPresence=null;
        presenceRetiring=Retire();dispatch.Observe(presenceRetiring);
        async Task Retire(){try{if(publisher is not null)await publisher.DisposeAsync();}finally{lease?.Dispose();}}
    }
    private DiscordPresenceActivity? MultiboxPresence(DiscordPresenceActivity? single)
    {
        if(!presenceRetiring.IsCompleted)return null;
        if(multibox is null||!multiboxOptions.CombinePresence||single is null){RetirePresenceOwner();return presenceRetiring.IsCompleted?single:null;}
        presenceLease??=MultiboxPeer.TryAcquirePresenceLease(MultiboxDirectory);
        if(presenceLease is null)return null;
        return DiscordPresenceActivity.Combine(single,_presenceSession.SettledLocation??_selectedCharacter?.SectorName??"",
            multibox.Peers.Where(p=>p.CombinePresence&&p.Presence is not null).Select(p=>(p.Sector,p.Presence!)));
    }
}
