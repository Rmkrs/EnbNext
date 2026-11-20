using EnbNext.Client;
using EnbNext.Engine;
using EnbNext.Presentation;

namespace EnbNext.App;

/// <summary>
/// Next multibox "Shared harvesting" (opt-in, Multibox menu): a follower that can harvest publishes what it knows of its
/// targeted asteroid (contents, its Harvest button, its open harvest window). The leader's target frame shows those
/// contents and lends it the Harvest button; its loot window shows the follower's harvest window, and a click on a
/// resource asks the follower to take it. The follower acts through its own client, with its own checks.
/// </summary>
internal sealed partial class SdlApplication
{
    private MultiboxHarvest? publishedHarvest;
    private EnbRemoteHarvest? shownRemoteHarvest;
    private long harvestNext;
    private EnbWorldUi? harvestUi;
    // Follower: after a leader's harvest command, its game messages (inventory full, out of range) go to the leader.
    private Guid harvestNoteLeader;
    private long harvestNotesUntil,harvestNoteSequence;

    private void TickSharedHarvest()
    {
        long now=Environment.TickCount64;if(now<harvestNext)return;harvestNext=now+200;
        BindSharedHarvest();
        if(multibox is not {} bus||_client is not {} client)return;
        // Follower: publish a change at once (the leader's frame follows it).
        var harvest=multiboxOptions.SharedHarvest?CaptureHarvest(client):null;
        if(harvest is null?publishedHarvest is not null:!harvest.Same(publishedHarvest))
        {
            publishedHarvest=harvest;
            bus.Publish(bus.Current with {Harvest=harvest});
            dispatch.Observe(bus.AnnounceAsync());
        }
        if(now<harvestNotesUntil&&client.LatestPresentationSnapshot?.Hud.Chat is {} chat)
            foreach(var line in chat.Lines)
            {
                if(line.Sequence<=harvestNoteSequence)continue;
                harvestNoteSequence=line.Sequence;
                // The game's own warnings (red), not chat from players.
                if(line.Peer is null&&line.Type is 9 or 17 or 19 or 21&&client.MultiboxContext is {} context)
                {
                    var note=new EnbMultiboxInput(Guid.NewGuid(),"harvestNote",line.Type,context){Text=line.Text};
                    dispatch.Observe(SendNote(bus,note));
                }
            }
        // Leader: the follower's harvest window, redrawn when it changes.
        var window=RemoteHarvestWindow();
        if(!SameRemote(window,shownRemoteHarvest)){shownRemoteHarvest=window;worldScreen?.Owner.Ui.Inventory.RemoteHarvestChanged();}
    }

    private void BindSharedHarvest()
    {
        var ui=worldScreen?.Owner.Ui;
        if(ReferenceEquals(ui,harvestUi))return;
        harvestUi=ui;
        if(ui is null)return;
        ui.RemoteHarvest=RemoteHarvestFor;
        ui.Inventory.RemoteHarvestWindow=RemoteHarvestWindow;
        ui.RemoteHarvestCommand=(action,target,slot,pilot)=>dispatch.Observe(SendHarvestCommandAsync(action,target,slot,pilot));
    }

    /// <summary>This follower's targeted asteroid and harvest window, while it can prospect.</summary>
    private MultiboxHarvest? CaptureHarvest(EnbClient client)
    {
        if(worldScreen?.Owner.Ui is not {} ui||client.LatestPresentationSnapshot is not {} scene||(scene.Hud.ProspectSkillLevel??0)==0)return null;
        uint? target=scene.Hud.CurrentTargetId is uint id&&scene.Objects.Any(o=>o.GameId==id&&o.ObjectType==0x26)?id:null;
        var inventory=scene.Hud.Inventory;uint? open=inventory?.HarvestableId;
        if(target is null&&open is null)return null;
        var contents=target is not null&&ui.Scanner.HarvestContents is {IsDefaultOrEmpty:false} lines?lines.ToArray():[];
        // The Harvest button as the server offers it here (its low bits: not available now, out of range).
        uint verb=target is not null?scene.Hud.CurrentTargetVerbs.FirstOrDefault(v=>(v&0xFFFF0000u)==EnbWorldUi.HarvestVerb):0;
        int tech=target is uint asteroid&&scene.Objects.FirstOrDefault(o=>o.GameId==asteroid)?.HarvestableTechLevel is int level?level:0;
        var items=open is not null&&inventory is not null
            ?inventory.Harvest.Select(i=>new MultiboxHarvestItem(i.Slot,i.TemplateId,i.Name,i.Category,i.Subcategory,i.ItemType,i.Level,i.IconBaseAsset,i.Count,i.Description)).ToArray():[];
        return new(target??open!.Value,contents,verb,open,items,tech);
    }

    /// <summary>The followers in this leader's group and sector that share harvesting, by their published harvest.</summary>
    private IEnumerable<MultiboxState> HarvestPeers(Func<MultiboxHarvest,bool> matches)
    {
        if(!multiboxOptions.SharedHarvest||multibox is not {} bus||_client?.MultiboxContext is not {} context||!bus.Current.Leader)return [];
        return bus.Peers.Where(p=>p.Harvest is {} h&&matches(h)&&p.Server==bus.Current.Server&&p.Context?.Sector==context.Sector&&bus.Current.GroupMembers.Contains(p.Avatar))
            .OrderBy(p=>p.Name,StringComparer.OrdinalIgnoreCase);
    }
    private IReadOnlyList<EnbRemoteHarvest> RemoteHarvestFor(uint target)
        => [..HarvestPeers(h=>h.Target==target&&(h.Verb!=0||h.Contents.Length>0)).Select(p=>Remote(p.Name,p.Harvest!))];
    private EnbRemoteHarvest? RemoteHarvestWindow()
    {
        uint? target=_client?.LatestPresentationSnapshot?.Hud.CurrentTargetId;
        return target is uint id&&HarvestPeers(h=>h.Open==id).FirstOrDefault() is {Harvest:{} h} peer?Remote(peer.Name,h with {Target=id}):null;
    }
    private static EnbRemoteHarvest Remote(string pilot,MultiboxHarvest h)=>new(pilot,h.Target,[..h.Contents],h.Verb,h.Verb!=0&&(h.Verb&0xFFFF)==0,h.Open==h.Target,
        [..h.Items.Select(i=>new EnbSceneItemSnapshot(0x12,i.Slot,i.TemplateId,i.Name,i.Category,i.Subcategory,i.ItemType,i.Level,i.Icon,i.Count,null,null,null,i.Description,"",[],[],0))],h.TechLevel);
    private static bool SameRemote(EnbRemoteHarvest? a,EnbRemoteHarvest? b)
        =>a is null?b is null:b is not null&&a.Pilot==b.Pilot&&a.Target==b.Target&&a.Open==b.Open&&a.Items.Length==b.Items.Length&&
          a.Items.Zip(b.Items).All(p=>p.First.Slot==p.Second.Slot&&p.First.TemplateId==p.Second.TemplateId&&p.First.Count==p.Second.Count);

    /// <summary>Leader: the follower's Harvest button, a resource, or its window's close button.</summary>
    private async Task SendHarvestCommandAsync(string action,uint target,int slot,string pilot)
    {
        if(multibox is not {} bus||_client?.MultiboxContext is not {} context)return;
        var peer=HarvestPeers(h=>action=="harvest"?h.Target==target&&h.Verb!=0:h.Open==target).FirstOrDefault(p=>p.Name==pilot);
        if(peer?.Context is not {} recipient)return;
        var input=new EnbMultiboxInput(Guid.NewGuid(),action,target,context){Slot=slot};
        try{await bus.SendAsync(peer.Instance,"command",input.Id,action,command:input,recipient:recipient);}
        catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException){}
    }

    private async Task SendNote(MultiboxPeer bus,EnbMultiboxInput note)
    {
        try{await bus.SendAsync(harvestNoteLeader,"event",note.Id,note.Action,command:note);}
        catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException){}
    }
    private void WatchHarvestNotes(EnbClient client,Guid leader)
    {
        if(harvestNotesUntil<Environment.TickCount64)harvestNoteSequence=client.LatestPresentationSnapshot?.Hud.Chat?.Lines.Select(l=>l.Sequence).DefaultIfEmpty(0).Max()??0;
        harvestNoteLeader=leader;harvestNotesUntil=Environment.TickCount64+15000;
    }
    /// <summary>Leader: a follower's game message about its harvesting, in this client's chat with its name.</summary>
    private void ShowHarvestNote(EnbClient client,MultiboxState? from,EnbMultiboxInput? note)
    {
        if(!multiboxOptions.SharedHarvest||multibox is not {} bus||from is null||!bus.Current.GroupMembers.Contains(from.Avatar)||
            note is not {Text:{Length:>0 and <=512} text}||client.SelectedFixedSessionId is not uint character)return;
        if(text.StartsWith("COMPUTER: ",StringComparison.Ordinal))text=text[10..];
        client.ReportLocalChatMessage(character,(byte)Math.Min(note.Target,255u),from.Name+": "+text,suppressPrefix:true);
    }

    /// <summary>Follower: the leader's harvest command, done as this client's own click would be.</summary>
    private async Task ApplyHarvestCommandAsync(EnbClient client,EnbMultiboxInput input)
    {
        if(client.LatestPresentationSnapshot is not {} scene)return;
        uint target=input.Target;
        switch(input.Action)
        {
            case "harvest":
                // The Harvest button: on this client's target (taken first when it is another one); it opens or closes the window.
                if(scene.Hud.CurrentTargetId!=target&&!await client.RequestTargetAsync(target))return;
                await client.ExecuteTargetVerbAsync(target,EnbWorldUi.HarvestVerb);
                break;
            case "harvestItem":
                if(scene.Hud.Inventory is not {HarvestableId:{} open} inventory||open!=target||client.SelectedFixedSessionId is not uint character||
                    scene.Objects.FirstOrDefault(o=>o.IsLocalShip) is not {} ship||inventory.Harvest.FirstOrDefault(i=>i.Slot==input.Slot) is not {} item)return;
                await client.LootItemAsync(new EnbInventoryItemRequest(character,ship.RenderIdentity,0x12,item.Slot,item.TemplateId)
                    {HarvestableId=open,HarvestableOwner=inventory.HarvestableOwner});
                break;
            case "harvestClose":
                if(scene.Hud.Inventory is {HarvestableId:{} shown} window&&shown==target)await client.CloseLootAsync(shown,window.HarvestableOwner);
                break;
        }
    }
}
