using System.Text.Json;
using EnbNext.Presentation;
using EnbNext.Client;
namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private long remoteShortcutNext;
    private Task remoteShortcutSending=Task.CompletedTask;
    private MultiboxState[]? remoteShortcutPeers;
    private void TickRemoteShortcutFeedback()
    {
        if(multibox is not {} bus || worldScreen is not {} world || !multiboxOptions.Enabled)return;
        var peers=bus.Peers;
        if(!ReferenceEquals(peers,remoteShortcutPeers))
        {remoteShortcutPeers=peers;RefreshRemoteShortcuts(bus.Current);}
        if(!peers.Any(p=>p.Leader&&p.Server==bus.Current.Server&&p.GroupMembers.Contains(bus.Current.Avatar)))return;
        long now=Environment.TickCount64;
        if(now<remoteShortcutNext || !remoteShortcutSending.IsCompleted)return;
        remoteShortcutNext=now+50;
        remoteShortcutSending=bus.PublishShortcutsAsync(world.Owner.Cockpit.CaptureRemoteShortcuts(bus.Current.Shortcuts));
        dispatch.Observe(remoteShortcutSending);
    }
    private sealed record RemoteShortcutBinding(string Key,int[] Slots);
    private RemoteShortcutBinding[] remoteShortcutBindings=[];
    private EnbRemoteShortcutMember[] remoteShortcutMembers=[];
    private string RemoteShortcutPath=>Path.ChangeExtension(MultiboxOptionsPath,"shortcuts.json");
    private void LoadRemoteShortcutBindings()
    {
        remoteShortcutMembers=[];remoteShortcutBindings=[];
        if(multiboxCharacter is null)return;
        try{if(File.Exists(RemoteShortcutPath))remoteShortcutBindings=JsonSerializer.Deserialize<RemoteShortcutBinding[]>(File.ReadAllText(RemoteShortcutPath))??[];}
        catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException){AppendDiagnostic("Shortcut bindings: "+ex.Message);}
    }
    private void RefreshRemoteShortcuts(MultiboxState self)
    {
        if(worldScreen is not {} world||multibox is not {} bus)return;
        var ui=world.Owner.Inventory;ui.RemoteShortcutCount=Math.Clamp(multiboxOptions.ShortcutButtons,0,12);
        var members=remoteShortcutMembers.Where(m=>self.GroupMembers.Contains(m.Avatar)).Select(m=>m with {Online=false}).ToList();
        foreach(var peer in bus.Peers.Where(p=>p.Server==self.Server&&self.GroupMembers.Contains(p.Avatar)))
        {
            string key=peer.Account.ToString("N")+"/"+peer.Character;
            var bindings=remoteShortcutBindings.FirstOrDefault(b=>b.Key==key)?.Slots??Enumerable.Repeat(-1,12).ToArray();
            var member=new EnbRemoteShortcutMember(peer.Avatar,key,peer.Name,peer.Context is {} context&&self.Context?.Sector==context.Sector,
                (peer.Shortcuts??[]).Where(s=>s is not null&&s.Slot is >=0 and <12).DistinctBy(s=>s.Slot).ToArray(),bindings);
            int index=members.FindIndex(m=>m.Avatar==peer.Avatar);if(index<0)members.Add(member);else members[index]=member;
        }
        ui.RemoteShortcutMembers=remoteShortcutMembers=members.ToArray();
        ui.RemoteShortcutBound=BindRemoteShortcut;ui.RemoteShortcutRequested=ActivateRemoteShortcut;
    }
    private void BindRemoteShortcut(string key,int circle,int slot)
    {
        if(circle is <0 or >=12||slot is <-1 or >=12||!remoteShortcutMembers.Any(m=>m.Key==key))return;
        var values=Enumerable.Repeat(-1,12).ToArray();
        if(remoteShortcutBindings.FirstOrDefault(b=>b.Key==key)?.Slots is {} old)Array.Copy(old,values,Math.Min(old.Length,12));
        values[circle]=slot;
        var next=remoteShortcutBindings.Where(b=>b.Key!=key).Append(new RemoteShortcutBinding(key,values)).ToArray();
        try
        {
            string temporary=RemoteShortcutPath+"."+multiboxInstance.ToString("N")+".tmp";
            File.WriteAllText(temporary,JsonSerializer.Serialize(next));File.Move(temporary,RemoteShortcutPath,true);remoteShortcutBindings=next;
            remoteShortcutMembers=remoteShortcutMembers.Select(m=>m.Key==key?m with {Bindings=values}:m).ToArray();
            if(worldScreen is {} world)world.Owner.Inventory.RemoteShortcutMembers=remoteShortcutMembers;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){AppendDiagnostic("Could not save shortcut binding: "+ex.Message);}
    }
    private void ActivateRemoteShortcut(uint avatar,int slot)
    {
        if(slot is <0 or >=12||_client?.MultiboxContext is not {} context||multibox?.Peers.FirstOrDefault(p=>p.Avatar==avatar&&p.Server==_savedAccountServer) is not {} peer)return;
        QueueMultiboxInput(new(Guid.NewGuid(),"shortcut",(uint)slot,context),Task.FromResult(true),peer.Instance);
    }
}
