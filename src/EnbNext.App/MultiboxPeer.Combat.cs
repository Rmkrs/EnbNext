using System.IO.Pipes;
using EnbNext.Presentation;
namespace EnbNext.App;
internal sealed partial class MultiboxPeer
{
    public Func<Guid,long,long,EnbCombatBatch>? CombatRead;
    public Func<uint,EnbCombatTarget>? CombatTargetLife;
    public async Task<MultiboxMessage?> ReadCombatAsync(MultiboxState peer,Guid journal,long after,uint target,uint sector,long since=long.MinValue)
    {
        if(!Peers.Any(p=>p.Instance==peer.Instance)||peer.Server!=current.Server||!current.GroupMembers.Contains(peer.Avatar))return null;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            await using var client=new NamedPipeClientStream(".",Pipe(peer.Instance),PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(deadline.Token);
            await WriteAsync(client,new(Version,"combat",Instance,Guid.Empty,"",null){CombatJournal=journal,CombatCursor=after,CombatTarget=target,CombatSector=sector,CombatSince=since},deadline.Token);
            var reply=await ReadAsync(client,deadline.Token);
            return reply.Version==Version&&reply.Kind=="combat"&&reply.Origin==peer.Instance&&reply.Combat is {Hits:{Length:<=128}}?reply:null;
        }
        catch(Exception e) when(e is IOException or System.Text.Json.JsonException or OperationCanceledException){return null;}
    }
}
