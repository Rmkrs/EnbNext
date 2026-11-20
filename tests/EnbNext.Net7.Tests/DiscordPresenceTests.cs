using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using EnbNext.App;
using EnbNext.Presentation;
using Xunit;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class DiscordPresenceTests
{
    [Fact]
    public void PreferencesAreOptInAndBothChoicesSurviveSerialization()
    {
        var old=JsonSerializer.Deserialize<EnbStartupPreferences>("{}")!;
        Assert.False((old.DiscordPresence??new()).Enabled);
        Assert.False((old.DiscordPresence??new()).IncludeCharacterName);
        var saved=old with {DiscordPresence=new(true,true)};
        Assert.Equal(saved,JsonSerializer.Deserialize<EnbStartupPreferences>(JsonSerializer.Serialize(saved)));
    }
    [Fact]
    public void PayloadUsesLocationClassLevelTimeAndOnlyOptedInName()
    {
        var space=DiscordPresenceActivity.Create("Earth",false,"Terran Trader",150,null,1234);
        var dock=DiscordPresenceActivity.Create("Earth Station",true,"Terran Trader",151,"Test Pilot",1234);
        using var first=JsonDocument.Parse(DiscordPresenceClient.ActivityPayload(space));
        var activity=first.RootElement.GetProperty("args").GetProperty("activity");
        Assert.Equal("Earth",activity.GetProperty("details").GetString());
        Assert.Equal("Terran Trader · Level 150",activity.GetProperty("state").GetString());
        Assert.Equal(1234,activity.GetProperty("timestamps").GetProperty("start").GetInt64());
        Assert.Equal("logo",activity.GetProperty("assets").GetProperty("large_image").GetString());
        Assert.Equal("Earth & Beyond",activity.GetProperty("assets").GetProperty("large_text").GetString());
        Assert.False(activity.TryGetProperty("secrets",out _));
        Assert.False(activity.TryGetProperty("party",out _));
        Assert.Equal("Docked at Earth Station",dock.Details);
        Assert.Equal("Test Pilot · Terran Trader · Level 151",dock.State);
        Assert.Equal(space.StartedAt,dock.StartedAt);
        using var clear=JsonDocument.Parse(DiscordPresenceClient.ActivityPayload(null));
        Assert.Equal(JsonValueKind.Null,clear.RootElement.GetProperty("args").GetProperty("activity").ValueKind);
    }
    [Fact]
    public void SessionPreservesElapsedAcrossHandoffsAndOptInButResetsAtLogout()
    {
        var session=new DiscordPresenceSession();
        var pilot=new EnbNext.Client.Models.CharacterSummary(0,1,1,"Test","Pilot",0,0,0,50,50,50,1,"Earth",1,0,0,0);
        EnbSceneSnapshot Scene(string name,bool docked=false,bool transition=false)=>new(0,0,1,null,null,transition,null,null!,[],null!,null!,[],[],null,null,[],[])
            {LocationTransition=new(1,"Earth","Sol",name,docked?"":"Sol"),ActiveStarbaseId=docked?1u:null,Music=new(0,151,0)};
        var first=session.Update(EnbNext.Client.ClientLifecycleState.InWorld,true,pilot,Scene("Earth"),new(),100);
        Assert.Null(first); // The clock starts even while sharing is disabled.
        first=session.Update(EnbNext.Client.ClientLifecycleState.InWorld,true,pilot,Scene("Earth"),new(true,true),120)!;
        Assert.Equal(100,first.StartedAt);Assert.Contains("Test Pilot",first.State);Assert.Contains("151",first.State);
        var transition=session.Update(EnbNext.Client.ClientLifecycleState.Redirecting,true,pilot,Scene("Earth Station",true,true),new(true,false),130)!;
        Assert.Equal("Earth",transition.Details);Assert.DoesNotContain("Test Pilot",transition.State);
        var station=session.Update(EnbNext.Client.ClientLifecycleState.InWorld,true,pilot,Scene("Earth Station",true),new(true),140)!;
        Assert.Equal("Docked at Earth Station",station.Details);Assert.Equal(100,station.StartedAt);
        var undock=session.Update(EnbNext.Client.ClientLifecycleState.InWorld,true,pilot,Scene("Earth"),new(true),150)!;
        Assert.Equal("Earth",undock.Details);Assert.Equal(100,undock.StartedAt);
        Assert.Null(session.Update(EnbNext.Client.ClientLifecycleState.CharacterSelection,true,pilot,null,new(true),160));
        var second=session.Update(EnbNext.Client.ClientLifecycleState.InWorld,true,pilot,Scene("Mars"),new(true),200)!;
        Assert.Equal(200,second.StartedAt);Assert.Equal("Mars",second.Details);
        Assert.Null(session.Update(EnbNext.Client.ClientLifecycleState.Faulted,true,pilot,null,new(true),210));
    }
    [Fact]
    public void Utf8LimitsDoNotSplitCharacters()
    {
        var value=DiscordPresenceActivity.Create(string.Concat(Enumerable.Repeat("🚀",40)),false,"Explorer",150,null,1);
        Assert.Equal(128,Encoding.UTF8.GetByteCount(value.Details));
        Assert.Equal(32,value.Details.EnumerateRunes().Count());
    }
    [Fact]
    public async Task DefaultOffNeverConnectsEvenAtShutdown()
    {
        int connections=0;
        var client=new DiscordPresenceClient(_=>{Interlocked.Increment(ref connections);return Task.FromResult<Stream?>(null);});
        client.SetActivity(null);await client.DisposeAsync();Assert.Equal(0,connections);
    }
    [Fact]
    public async Task LocalPeerGetsHandshakeUpdatesPongAndClear()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(4));var token=deadline.Token;
        string name="enb-presence-test-"+Guid.NewGuid().ToString("N");
        using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        var accepted=server.WaitForConnectionAsync(token);
        await using var client=new DiscordPresenceClient(async ct=>
        {var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);await pipe.ConnectAsync(ct);return pipe;});
        var first=DiscordPresenceActivity.Create("Earth",false,"Terran Trader",150,null,1234);
        client.SetActivity(first);await accepted;
        var hello=await DiscordPresenceClient.ReadAsync(server,token);Assert.Equal(0,hello.Opcode);
        using(var json=JsonDocument.Parse(hello.Payload))
        {Assert.Equal(1,json.RootElement.GetProperty("v").GetInt32());Assert.Equal("1569629670507544617",json.RootElement.GetProperty("client_id").GetString());}
        await DiscordPresenceClient.WriteAsync(server,1,"{\"cmd\":\"DISPATCH\",\"evt\":\"READY\"}"u8.ToArray(),token);
        var initial=await Activity();Assert.Equal("Earth",initial.GetProperty("details").GetString());
        client.SetActivity(first); // No duplicate frame: the next frame must be our pong.
        await DiscordPresenceClient.WriteAsync(server,3,"ping"u8.ToArray(),token);
        var pong=await DiscordPresenceClient.ReadAsync(server,token);Assert.Equal(4,pong.Opcode);Assert.Equal("ping",Encoding.UTF8.GetString(pong.Payload));
        client.SetActivity(DiscordPresenceActivity.Create("Earth Station",true,"Terran Trader",150,"Test Pilot",1234));
        var dock=await Activity();Assert.Equal("Docked at Earth Station",dock.GetProperty("details").GetString());
        Assert.Contains("Test Pilot",dock.GetProperty("state").GetString());Assert.Equal(1234,dock.GetProperty("timestamps").GetProperty("start").GetInt64());
        client.SetActivity(DiscordPresenceActivity.Create("Mars",false,"Terran Trader",150,null,1234));
        var undock=await Activity();Assert.Equal("Mars",undock.GetProperty("details").GetString());Assert.DoesNotContain("Test Pilot",undock.GetProperty("state").GetString());
        var combined=DiscordPresenceActivity.Combine(first,"Earth",[("Mars",first)]);
        client.SetActivity(combined);
        var group=await Activity();Assert.Equal(2,group.GetProperty("party").GetProperty("size")[0].GetInt32());
        // The last peer exits: the owner must return to an ordinary accepted activity.
        client.SetActivity(DiscordPresenceActivity.Combine(first,"Earth",[]));
        var remaining=await Activity();Assert.Equal("Earth",remaining.GetProperty("details").GetString());
        Assert.False(remaining.TryGetProperty("party",out _));
        client.SetActivity(null);Assert.Equal(JsonValueKind.Null,(await Activity()).ValueKind);
        byte[] end=new byte[1];Assert.Equal(0,await server.ReadAsync(end,token));
        // Re-enable uses a fresh local connection, then application exit clears it.
        server.Disconnect();accepted=server.WaitForConnectionAsync(token);
        client.SetActivity(first);await accepted;
        Assert.Equal(0,(await DiscordPresenceClient.ReadAsync(server,token)).Opcode);
        await DiscordPresenceClient.WriteAsync(server,1,"{\"evt\":\"READY\"}"u8.ToArray(),token);
        Assert.Equal("Earth",(await Activity()).GetProperty("details").GetString());
        var shutdown=client.DisposeAsync().AsTask();
        Assert.Equal(JsonValueKind.Null,(await Activity()).ValueKind);
        await shutdown;
        Assert.Equal(0,await server.ReadAsync(end,token));
        async Task<JsonElement> Activity()
        {var frame=await DiscordPresenceClient.ReadAsync(server,token);Assert.Equal(1,frame.Opcode);using var json=JsonDocument.Parse(frame.Payload);Assert.Equal("SET_ACTIVITY",json.RootElement.GetProperty("cmd").GetString());return json.RootElement.GetProperty("args").GetProperty("activity").Clone();}
    }
    [Fact]
    public async Task FailedConnectionIsQuietAndDisableStopsRetry()
    {
        var attempted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int count=0;
        var client=new DiscordPresenceClient(_=>{Interlocked.Increment(ref count);attempted.TrySetResult();throw new IOException("Discord absent");});
        client.SetActivity(new("Earth","Explorer",1));
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(4));
        client.SetActivity(null);await client.DisposeAsync();Assert.Equal(1,count);
    }
    [Fact]
    public async Task FramingIsLittleEndianAndRejectsOversizedPeerInput()
    {
        using var stream=new MemoryStream();await DiscordPresenceClient.WriteAsync(stream,3,[1,2,3],default);
        Assert.Equal(new byte[]{3,0,0,0,3,0,0,0,1,2,3},stream.ToArray());
        stream.Position=0;var frame=await DiscordPresenceClient.ReadAsync(stream,default);Assert.Equal(3,frame.Opcode);Assert.Equal(new byte[]{1,2,3},frame.Payload);
        byte[] invalid=new byte[8];BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(4),int.MaxValue);
        await Assert.ThrowsAsync<InvalidDataException>(()=>DiscordPresenceClient.ReadAsync(new MemoryStream(invalid),default));
    }
}
