using EnbNext.Client;

namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private EnbClient? multiboxInputClient;
    private Task multiboxSending=Task.CompletedTask,multiboxReceiving=Task.CompletedTask;
    private int multiboxPending;
    private void BindMultiboxInput(EnbClient? client)
    {
        if(ReferenceEquals(client,multiboxInputClient))return;
        if(multiboxInputClient is {} old){old.MultiboxInput-=ForwardMultiboxInput;old.MultiboxLeadAsync=null;}
        multiboxInputClient=client;
        if(client is not null){client.MultiboxInput+=ForwardMultiboxInput;client.MultiboxLeadAsync=LeadMultiboxAsync;}
    }
    /// <summary>The spacing between successive dock/land requests of one group:
    /// simultaneous requests left the leader undocked on the live server.</summary>
    internal static readonly TimeSpan MultiboxDockStagger=TimeSpan.FromMilliseconds(400);
    private async Task LeadMultiboxAsync(EnbMultiboxInput input)
    {
        if(multibox is not {} bus||!multiboxOptions.Enabled||multiboxInputClient is not {} client)return;
        // The leader decides the arrival spawn draw for itself and the alts that dock with it.
        if(input.Action=="dock"){input=input with {SpawnDraw=Math.Max(double.Epsilon,Random.Shared.NextDouble())};SetStationSpawnDraw(input.SpawnDraw);}
        var targets=bus.Peers.Where(p=>p.ContextFor(input.Action) is {} context&&context.Sector==input.Context.Sector&&
            p.Server==_savedAccountServer&&p.GroupMembers.Contains(input.Context.Avatar)&&
            client.CanLeadMultibox(input.Context,p.Avatar)&&p.Follows(input.Action)).ToArray();
        foreach(var target in targets)
        {
            if(multibox!=bus||_client!=client||!client.CanLeadMultibox(input.Context,target.Avatar))return;
            try{await bus.SendAsync(target.Instance,"command",input.Id,input.Action,command:input,recipient:target.ContextFor(input.Action));}
            catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException){continue;}
            await Task.Delay(MultiboxDockStagger);
        }
    }
    private void ForwardMultiboxInput(EnbMultiboxInput input,Task<bool> sent)
        => QueueMultiboxInput(input,sent,null);
    private void QueueMultiboxInput(EnbMultiboxInput input,Task<bool> sent,Guid? destination)
    {
        if(multibox is not {} bus||!multiboxOptions.Enabled||multiboxInputClient is not {} client||multiboxPending>=128)return;
        var targets=bus.Peers.Where(p=>p.ContextFor(input.Action) is {} context&&context.Sector==input.Context.Sector&&
            p.Server==_savedAccountServer&&p.GroupMembers.Contains(input.Context.Avatar)&&
            client.CanLeadMultibox(input.Context,p.Avatar,input.Action!="shortcut")&&
            (input.Action=="shortcut"?p.Instance==destination:p.Follows(input.Action))).ToArray();
        if(targets.Length==0)return;
        var previous=multiboxSending;multiboxPending++;
        multiboxSending=Send();dispatch.Observe(multiboxSending);
        async Task Send()
        {
            try
            {
                await previous;
                if(!await sent)return;
                foreach(var target in targets)
                {
                    if(multibox!=bus||!multiboxOptions.Enabled||_client!=client||
                        // A sent launch has already begun leaving the station: authority was checked when queued.
                        input.Action!="launch"&&!client.CanLeadMultibox(input.Context,target.Avatar,input.Action!="shortcut")||
                        !bus.Peers.Any(p=>p.Instance==target.Instance&&p.ContextFor(input.Action)==target.ContextFor(input.Action)&&p.GroupMembers.Contains(input.Context.Avatar)))continue;
                    try{await bus.SendAsync(target.Instance,"command",input.Id,input.Action,command:input,recipient:target.ContextFor(input.Action));}
                    catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException){}
                }
            }
            catch(Exception ex) when(ex is IOException or InvalidOperationException or OperationCanceledException){}
            finally{multiboxPending--;}
        }
    }
    private void PumpMultiboxCommands()
    {
        if(!multiboxReceiving.IsCompleted||multibox is not {} bus)return;
        if(bus.TryReceive(out var message)&&message is not null)
        {multiboxReceiving=Receive(message);dispatch.Observe(multiboxReceiving);}
        async Task Receive(MultiboxMessage message)
        {
            if(multibox!=bus||_client is not {} client)return;
            var leader=bus.Peers.FirstOrDefault(p=>p.Instance==message.Origin);
            if(message.Kind=="event"){if(message.Action=="harvestNote")ShowHarvestNote(client,leader,message.Command);return;}
            bool launch=message.Action=="launch";
            if(!message.Allows(leader,launch?client.StationMultiboxContext:client.MultiboxContext,_savedAccountServer??"",multiboxOptions))return;
            var input=message.Command!;var recipient=message.Recipient!;
            try
            {
                if(launch){if(client.CanFollowMultiboxLaunch(input,recipient))worldScreen?.Owner.RequestStationDeparture();}
                else if(input.Action=="wormhole")
                {
                    // The leader's auto pilot wormhole: accept it, and open it when this client is its caster.
                    if(client.MultiboxContext!=recipient)return;
                    client.ExpectWormhole();
                    if(input.Target!=uint.MaxValue&&input.Target<int.MaxValue)await client.OpenWormholeAsync((int)input.Target);
                }
                else if(input.Action=="form")
                {
                    // After the wormhole: join the leader's formation again.
                    if(client.MultiboxContext==recipient&&client.LatestPresentationSnapshot?.Hud.Group is {IsLeader:false,Formation:0})await client.RequestFormationAsync(7);
                }
                else if(input.Action=="shortcut")await client.ActivateRemoteShortcutAsync(input,recipient);
                else if(input.Action is "harvest" or "harvestItem" or "harvestClose"){if(client.MultiboxContext==recipient){WatchHarvestNotes(client,message.Origin);await ApplyHarvestCommandAsync(client,input);}}
                else
                {
                    if(input.Action=="dock"&&multiboxOptions.SharedStationSpawn&&input.SpawnDraw is >0 and <1)SetStationSpawnDraw(input.SpawnDraw);
                    await client.ApplyMultiboxInputAsync(input,recipient,requireTarget:multiboxOptions.FollowLeaderTarget);
                }
            }
            catch(Exception ex) when(ex is IOException or InvalidOperationException or OperationCanceledException){}
        }
    }
}
