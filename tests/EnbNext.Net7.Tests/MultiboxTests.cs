using System.Text.Json;
using EnbNext.App;
using EnbNext.Presentation;
using Xunit;
namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class MultiboxTests
{
    private static MultiboxState State(Guid id)=>new(id,"test",Guid.NewGuid(),1,"Pilot",42,null,false,false,[],"Earth",null,false);
    [Fact]
    public void PresenceLeaseAdmitsOnePublisherAndTransfersAfterRelease()
    {
        string path=Path.Combine(Path.GetTempPath(),"enb-presence-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        try
        {
            using(var first=MultiboxPeer.TryAcquirePresenceLease(path))
            {Assert.NotNull(first);Assert.Null(MultiboxPeer.TryAcquirePresenceLease(path));}
            using var next=MultiboxPeer.TryAcquirePresenceLease(path);Assert.NotNull(next);
        }
        finally{Directory.Delete(path,true);}
    }
    [Fact]
    public void OptionsDefaultOffAndRoundTrip()
    {
        Assert.Equal(new(),JsonSerializer.Deserialize<EnbMultiboxOptions>("{}"));
        var enabled=new EnbMultiboxOptions(true,true,true,true,true);
        Assert.Equal(enabled,JsonSerializer.Deserialize<EnbMultiboxOptions>(JsonSerializer.Serialize(enabled)));
    }
    [Fact]
    public async Task FramingRoundTripsAndRejectsOversizedMessages()
    {
        var message=new MultiboxMessage(1,"state",Guid.NewGuid(),Guid.Empty,"",State(Guid.NewGuid()));
        using var stream=new MemoryStream();await MultiboxPeer.WriteAsync(stream,message,default);stream.Position=0;
        var result=await MultiboxPeer.ReadAsync(stream,default);Assert.Equal(message.Origin,result.Origin);Assert.Equal(message.State!.Name,result.State!.Name);
        using var bad=new MemoryStream(BitConverter.GetBytes(MultiboxPeer.MaximumMessage+1));
        await Assert.ThrowsAsync<InvalidDataException>(()=>MultiboxPeer.ReadAsync(bad,default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>MultiboxPeer.WriteAsync(stream,message with {Action=new string('x',MultiboxPeer.MaximumMessage)},default));
    }
    [Fact]
    public async Task LocalPeersDiscoverDeduplicateAndDisappearOnExit()
    {
        string path=Path.Combine(Path.GetTempPath(),"enb-multibox-test-"+Guid.NewGuid().ToString("N"));
        try
        {
            await using var a=new MultiboxPeer(path,State(Guid.NewGuid()));
            var b=new MultiboxPeer(path,State(Guid.NewGuid()));
            try
            {
                await Until(()=>a.Peers.Length==1&&b.Peers.Length==1);
                using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                await using(var unsupported=new System.IO.Pipes.NamedPipeClientStream(".","EnbNext-multibox-"+b.Instance.ToString("N"),System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous|System.IO.Pipes.PipeOptions.CurrentUserOnly))
                {
                    await unsupported.ConnectAsync(timeout.Token);
                    await MultiboxPeer.WriteAsync(unsupported,new(MultiboxPeer.Version+1,"state",a.Instance,Guid.Empty,"",null),timeout.Token);
                    await Assert.ThrowsAsync<EndOfStreamException>(()=>MultiboxPeer.ReadAsync(unsupported,timeout.Token));
                }
                var input=Guid.NewGuid();await a.SendAsync(b.Instance,"command",input,"test");
                MultiboxMessage? got=null;await Until(()=>b.TryReceive(out got));
                Assert.Equal(input,got!.Input);Assert.Equal(a.Instance,got.Origin);
                await a.SendAsync(b.Instance,"command",input,"test");
                var next=Guid.NewGuid();await a.SendAsync(b.Instance,"event",next,"barrier");
                await Until(()=>b.TryReceive(out got));Assert.Equal(next,got!.Input);
                Assert.False(a.TryReceive(out _));Assert.False(b.TryReceive(out _));
                var context=new EnbNext.Client.EnbMultiboxContext(1,42,42,4,5,6,Guid.NewGuid());
                var recipient=context with {Character=2,Avatar=43,Owner=7};
                var target=new EnbNext.Client.EnbMultiboxInput(Guid.NewGuid(),"target",99,context);
                var fire=target with {Id=Guid.NewGuid(),Action="fire"};
                await a.SendAsync(b.Instance,"command",target.Id,target.Action,command:target,recipient:recipient);
                await a.SendAsync(b.Instance,"command",fire.Id,fire.Action,command:fire,recipient:recipient);
                await Until(()=>b.TryReceive(out got));Assert.Equal(target,got!.Command);Assert.Equal(recipient,got.Recipient);
                await Until(()=>b.TryReceive(out got));Assert.Equal(fire,got!.Command);
                Assert.False(a.TryReceive(out _));Assert.False(b.TryReceive(out _));
                a.Publish(a.Current with {Context=context});
                b.Publish(b.Current with {Context=recipient,Leader=true,GroupMembers=[42]});
                await Until(()=>a.Peers.Single().Context==recipient&&b.Peers.Single().Context==context);
                var slot=new EnbRemoteShortcut(0,"Buff","Buff","icon",1,1,1,true,true,false){RangeFrame=15};
                await a.PublishShortcutsAsync([slot]);
                var barrier=Guid.NewGuid();await a.SendAsync(b.Instance,"event",barrier,"feedback barrier");
                await Until(()=>b.TryReceive(out got));Assert.Equal(barrier,got!.Input);
                Assert.Equal(slot,Assert.Single(b.Peers.Single().Shortcuts));
                long revision=a.Current.ShortcutRevision;
                await a.PublishShortcutsAsync([slot]);Assert.Equal(revision,a.Current.ShortcutRevision);
            }
            finally{await b.DisposeAsync();}
            await Until(()=>a.Peers.Length==0);
        }
        finally{if(Directory.Exists(path))Directory.Delete(path,true);}
    }
    [Fact]
    public async Task CombatReadsRequireMutualGroupMembership()
    {
        string path=Path.Combine(Path.GetTempPath(),"enb-meter-test-"+Guid.NewGuid().ToString("N"));
        try
        {
            await using var a=new MultiboxPeer(path,State(Guid.NewGuid()) with {Avatar=42,GroupMembers=[43]});
            await using var b=new MultiboxPeer(path,State(Guid.NewGuid()) with {Avatar=43,GroupMembers=[42],Context=new(1,43,43,5,7,6,Guid.NewGuid())});
            Guid journal=Guid.NewGuid();
            b.CombatRead=(j,c,since)=>new(journal,[new(1,10,7,43,99,1,2,43,100,2)],20,false);
            b.CombatTargetLife=id=>new(4,id==99?2UL:0);
            await Until(()=>a.Peers.Length==1&&b.Peers.Length==1);
            var reply=await a.ReadCombatAsync(a.Peers[0],Guid.Empty,0,99,7);
            Assert.NotNull(reply);Assert.Equal(journal,reply.Combat!.Journal);Assert.Equal(new EnbCombatTarget(4,2),reply.CombatTargetLife);
            b.Publish(b.Current with {GroupMembers=[]});
            await Until(()=>a.Peers[0].GroupMembers.Length==0);
            Assert.Null(await a.ReadCombatAsync(a.Peers[0],journal,1,99,7));
        }
        finally{if(Directory.Exists(path))Directory.Delete(path,true);}
    }
    private static async Task Until(Func<bool> ready)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while(!ready())await Task.Delay(10,timeout.Token);
    }
    [Fact]
    public void AggregationCountsOnlySuppliedOptedInShipsAndPreservesEarliestSession()
    {
        var first=new DiscordPresenceActivity("Earth","Pilot",200);
        Assert.Same(first,DiscordPresenceActivity.Combine(first,"Earth",[]));
        var two=DiscordPresenceActivity.Combine(first,"Earth",[("Earth",first with {StartedAt=100})]);
        Assert.Equal("Earth",two.Details);Assert.Equal("Multiboxing \u00b7 2 ships",two.State);Assert.Equal(100,two.StartedAt);Assert.Equal(2,two.PartySize);
        var three=DiscordPresenceActivity.Combine(first,"Earth",[("Mars",first),("Earth",first)]);
        Assert.Equal("Across 2 sectors",three.Details);Assert.Equal(3,three.PartySize);
    }
    [Fact]
    public void CombinedPresenceHasPartyCountWithoutJoinSecrets()
    {
        using var json=JsonDocument.Parse(DiscordPresenceClient.ActivityPayload(new("Across 2 sectors","Multiboxing \u00b7 3 ships",100){PartySize=3}));
        var activity=json.RootElement.GetProperty("args").GetProperty("activity");
        Assert.Equal(3,activity.GetProperty("party").GetProperty("size")[0].GetInt32());
        Assert.Equal(3,activity.GetProperty("party").GetProperty("size")[1].GetInt32());
        Assert.False(activity.TryGetProperty("secrets",out _));
    }
}
