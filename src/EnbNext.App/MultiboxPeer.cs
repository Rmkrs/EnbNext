using System.Buffers.Binary;
using EnbNext.Client;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace EnbNext.App;

internal sealed record MultiboxState(Guid Instance,string Server,Guid Account,uint Character,string Name,uint Avatar,
    Guid? LaunchGroup,bool Host,bool Leader,uint[] GroupMembers,string Sector,DiscordPresenceActivity? Presence,bool CombinePresence)
{
    public EnbMultiboxContext? Context {get;init;}
    public EnbNext.Presentation.EnbRemoteShortcut[] Shortcuts {get;init;}=[];
    public long ShortcutRevision {get;init;}
    public bool FollowTarget {get;init;}
    public bool FollowFire {get;init;}
    public EnbMultiboxContext? StationContext {get;init;}
    public bool FollowDock {get;init;}
    public bool FollowLaunch {get;init;}
    /// <summary>Focus mode layout this client knows; the highest revision among all clients wins.</summary>
    public MultiboxFocusLayout? Focus {get;init;}
    /// <summary>When the user last chose this client's My Characters panel; the latest claim shows it.</summary>
    public long PanelClaim {get;init;}
    /// <summary>Where this client stands in its station, and the spawn it arrived at.</summary>
    public MultiboxStationPose? StationPose {get;init;}
    public MultiboxStationSpawn? StationSpawn {get;init;}
    /// <summary>Next route planning: standing with The Red Dragon (Smuggler's Run), wormhole ranks, and whether this
    /// client opens and accepts the leader's auto pilot wormholes.</summary>
    public float? RedDragon {get;init;}
    public int CreateWormhole {get;init;}
    public int ExtendedWormhole {get;init;}
    public bool FollowWormhole {get;init;}
    /// <summary>In the group's formation (after a wormhole the leader waits for every follower to be in it).</summary>
    public bool InFormation {get;init;}
    /// <summary>Next "Shared harvesting": what this follower knows of its targeted asteroid and its open harvest window.</summary>
    public MultiboxHarvest? Harvest {get;init;}
    /// <summary>Next "Reactor in group frame": this client's reactor in whole percent, while it shares it (in space).</summary>
    public int? Reactor {get;init;}
    /// <summary>The identity a command of this action addresses: station launch uses the station context.</summary>
    public EnbMultiboxContext? ContextFor(string action)=>action=="launch"?StationContext:Context;
    public bool Follows(string action)=>action switch {"target"=>FollowTarget,"fire"=>FollowFire,"dock" or "land"=>FollowDock,"launch"=>FollowLaunch,_=>false};
}
/// <summary>Focus mode: Main fills the monitor area above a band of thumbnails of every other client.
/// NameSize is the text size of the thumbnails' character names (0: the default).</summary>
internal sealed record MultiboxFocusLayout(long Revision,bool On,Guid Main,int X,int Y,int Width,int Height,int NameSize=0);
/// <summary>A follower's targeted asteroid: its contents as the target frame lists them, whether its Harvest button is
/// there, and its open harvest window (the asteroid and its items).</summary>
internal sealed record MultiboxHarvest(uint Target,string[] Contents,uint Verb,uint? Open,MultiboxHarvestItem[] Items,int TechLevel)
{
    public bool Same(MultiboxHarvest? other)=>other is not null&&Target==other.Target&&Verb==other.Verb&&Open==other.Open&&TechLevel==other.TechLevel&&
        Contents.SequenceEqual(other.Contents)&&Items.SequenceEqual(other.Items);
}
internal sealed record MultiboxHarvestItem(int Slot,int TemplateId,string Name,uint Category,uint Subcategory,uint ItemType,int Level,int Icon,int Count,string Description);
internal sealed record MultiboxStationPose(uint Starbase,uint Room,float X,float Y,float Z,bool Run);
internal sealed record MultiboxStationSpawn(uint Starbase,int Index,int Max);
internal sealed record MultiboxMessage(int Version,string Kind,Guid Origin,Guid Input,string Action,MultiboxState? State,EnbMultiboxInput? Command=null,EnbMultiboxContext? Recipient=null)
{
    public long CombatSince {get;init;}=long.MinValue;
    public Guid CombatJournal {get;init;}
    public long CombatCursor {get;init;}
    public uint CombatTarget {get;init;}
    public uint CombatSector {get;init;}
    public EnbNext.Presentation.EnbCombatTarget CombatTargetLife {get;init;}
    public EnbNext.Presentation.EnbCombatBatch? Combat {get;init;}
    public bool Allows(MultiboxState? leader,EnbMultiboxContext? current,string server,EnbNext.Presentation.EnbMultiboxOptions options)
        => Version==MultiboxPeer.Version&&Kind=="command"&&Input!=Guid.Empty&&options.Enabled&&
            Command is {} input&&Recipient is {} recipient&&recipient==current&&
            input.Id==Input&&input.Action==Action&&
            Action switch {"target"=>options.FollowLeaderTarget,"fire"=>options.FollowLeaderFire,"dock" or "land"=>options.FollowLeaderDock,
                "launch"=>options.FollowLeaderLaunch,"shortcut"=>input.Target<12,"wormhole" or "form"=>options.FollowLeaderWormhole,
                "harvest" or "harvestItem" or "harvestClose"=>options.SharedHarvest,_=>false}&&
            leader is {Leader:true}&&leader.Instance==Origin&&leader.ContextFor(Action)==input.Context&&
            leader.Server==server&&leader.GroupMembers.Contains(recipient.Avatar)&&
            input.Context.Sector==recipient.Sector;
}

// Each opted-in process owns one current-user pipe. Discovery never establishes membership:
// a successful versioned exchange with a live endpoint does.
internal sealed partial class MultiboxPeer : IAsyncDisposable
{
    internal const int Version=4,MaximumMessage=65536;
    private readonly string directory,endpoint;
    private readonly CancellationTokenSource stop=new();
    private readonly ConcurrentQueue<MultiboxMessage> incoming=new();
    private readonly Task listener,discovery;
    private MultiboxState current;
    private MultiboxState[] peers=[];
    private readonly HashSet<(Guid,Guid)> consumed=[];
    private readonly Queue<(Guid,Guid)> consumption=[];
    public MultiboxState[] Peers=>Volatile.Read(ref peers);
    public Guid Instance=>current.Instance;
    public MultiboxPeer(string directory,MultiboxState state)
    {
        this.directory=directory;current=state;
        Directory.CreateDirectory(directory);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        endpoint=Path.Combine(directory,state.Instance.ToString("N")+".peer");
        listener=Task.Run(ListenAsync);discovery=Task.Run(DiscoverAsync);
    }
    public void Publish(MultiboxState state)
    {if(state.Instance!=Instance)throw new ArgumentException("Instance cannot change.");var old=Volatile.Read(ref current);Volatile.Write(ref current,state with {ShortcutRevision=old.ShortcutRevision+(old.Context!=state.Context||!old.Shortcuts.SequenceEqual(state.Shortcuts)?1:0)});}
    public void PublishFocus(MultiboxFocusLayout? layout){var old=Volatile.Read(ref current);Volatile.Write(ref current,old with {Focus=layout});}
    public bool TryReceive(out MultiboxMessage? message)=>incoming.TryDequeue(out message);
    private string Pipe(Guid id)=>"EnbNext-multibox-"+id.ToString("N");
    private async Task ListenAsync()
    {
        try
        {
            // The next instance is created as soon as a connection is accepted. On
            // Unix a named pipe is a domain socket whose listener lives only while
            // an instance exists; a client queued while one request is handled
            // would otherwise be dropped when that instance is disposed.
            NamedPipeServerStream NewServer()=>new(Pipe(Instance),PipeDirection.InOut,2,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            var waiting=NewServer();
            try
            {
            while(!stop.IsCancellationRequested)
            {
                if(!File.Exists(endpoint))await File.WriteAllTextAsync(endpoint,Environment.ProcessId.ToString()+":"+Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,stop.Token);
                await waiting.WaitForConnectionAsync(stop.Token);
                await using var server=waiting;
                waiting=NewServer();
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    var request=await ReadAsync(server,deadline.Token);
                    if(request.Version!=Version||request.Origin==Guid.Empty||request.Origin==Instance)continue;
                    if(request.Kind=="state")await WriteAsync(server,new(Version,"state",Instance,Guid.Empty,"",Volatile.Read(ref current)),deadline.Token);
                    else if(request.Kind=="combat"&&CombatRead is {} read&&Peers.FirstOrDefault(p=>p.Instance==request.Origin) is {} requester&&requester.Server==current.Server&&requester.GroupMembers.Contains(current.Avatar)&&current.GroupMembers.Contains(requester.Avatar))
                        await WriteAsync(server,new(Version,"combat",Instance,Guid.Empty,"",null){Combat=read(request.CombatJournal,request.CombatCursor,request.CombatSince),CombatTargetLife=current.Context?.Sector==request.CombatSector?CombatTargetLife?.Invoke(request.CombatTarget)??default:default},deadline.Token);
                    else if(request.Kind=="feedback" && request.State is {} feedback && feedback.Instance==request.Origin)AcceptShortcutFeedback(feedback);
                    else if(request.Kind=="announce" && request.State is {} announced && announced.Instance==request.Origin)AcceptAnnouncedState(announced);
                    else if(request.Kind is "command" or "event" && request.Input!=Guid.Empty && Peers.Any(p=>p.Instance==request.Origin) && incoming.Count<128)
                    {
                        var key=(request.Origin,request.Input);
                        if(consumed.Add(key)){consumption.Enqueue(key);if(consumption.Count>1024)consumed.Remove(consumption.Dequeue());incoming.Enqueue(request);}
                    }
                }
                catch(Exception ex) when(ex is IOException or JsonException or OperationCanceledException){}
            }
            }
            finally{await waiting.DisposeAsync();}
        }
        catch(Exception ex) when(ex is OperationCanceledException or IOException or UnauthorizedAccessException){}
        finally{try{File.Delete(endpoint);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}}
    }
    private async Task DiscoverAsync()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var found=new List<MultiboxState>();
                foreach(string file in Directory.EnumerateFiles(directory,"*.peer"))
                {
                    if(!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file),"N",out var id)||id==Instance)continue;
                    if(!EndpointAlive(file))continue;
                    try
                    {
                        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
                        await using var client=new NamedPipeClientStream(".",Pipe(id),PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                        await client.ConnectAsync(deadline.Token);
                        await WriteAsync(client,new(Version,"state",Instance,Guid.Empty,"",null),deadline.Token);
                        var reply=await ReadAsync(client,deadline.Token);
                        if(reply.Version==Version&&reply.Kind=="state"&&reply.Origin==id&&reply.State is {} state&&state.Instance==id&&state.GroupMembers is not null&&state.Name is not null&&state.Server is not null&&state.Sector is not null)found.Add(state);
                    }
                    catch(Exception ex) when(ex is IOException or JsonException or OperationCanceledException){}
                }
                lock(peerFeedbackGate)Volatile.Write(ref peers,found.Select(state=>MergeShortcutFeedback(state,peers.FirstOrDefault(p=>p.Instance==state.Instance))).ToArray());
                await Task.Delay(1000,stop.Token);
            }
        }
        catch(Exception ex) when(ex is OperationCanceledException or IOException or UnauthorizedAccessException){}
    }
    private static bool EndpointAlive(string path)
    {
        try
        {
            var fields=File.ReadAllText(path).Split(':');
            if(fields.Length!=2||!int.TryParse(fields[0],out int pid)||!long.TryParse(fields[1],out long started))return false;
            using var process=Process.GetProcessById(pid);
            if(process.StartTime.ToUniversalTime().Ticks==started&&!process.HasExited)return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception){}
        return false;
    }
    internal static FileStream? TryAcquirePresenceLease(string directory)
    {
        try{return new(Path.Combine(directory,"presence.owner"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){return null;}
    }
    // A changed layout reaches the other clients now instead of at their next discovery round.
    public async Task AnnounceAsync()
    {
        var state=Volatile.Read(ref current);
        foreach(var peer in Peers)
            try
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
                await using var client=new NamedPipeClientStream(".",Pipe(peer.Instance),PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(deadline.Token);
                await WriteAsync(client,new(Version,"announce",Instance,Guid.Empty,"",state),deadline.Token);
            }
            catch(Exception ex) when(ex is IOException or OperationCanceledException or TimeoutException){}
    }
    private void AcceptAnnouncedState(MultiboxState state)
    {
        lock(peerFeedbackGate)
        {
            var list=Volatile.Read(ref peers);int index=Array.FindIndex(list,p=>p.Instance==state.Instance);if(index<0)return;
            var copy=(MultiboxState[])list.Clone();copy[index]=MergeShortcutFeedback(state,list[index]);Volatile.Write(ref peers,copy);
        }
    }
    public async Task SendAsync(Guid destination,string kind,Guid input,string action,CancellationToken token=default,EnbMultiboxInput? command=null,EnbMultiboxContext? recipient=null)
    {
        if(kind is not ("command" or "event")||input==Guid.Empty||!Peers.Any(p=>p.Instance==destination))return;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token,stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await using var client=new NamedPipeClientStream(".",Pipe(destination),PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(deadline.Token);
        await WriteAsync(client,new(Version,kind,Instance,input,action,null,command,recipient),deadline.Token);
    }
    internal static async Task WriteAsync(Stream stream,MultiboxMessage message,CancellationToken token)
    {
        byte[] payload=JsonSerializer.SerializeToUtf8Bytes(message);
        if(payload.Length>MaximumMessage)throw new InvalidDataException("Local message is too large.");
        byte[] size=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(size,payload.Length);
        await stream.WriteAsync(size,token);await stream.WriteAsync(payload,token);await stream.FlushAsync(token);
    }
    internal static async Task<MultiboxMessage> ReadAsync(Stream stream,CancellationToken token)
    {
        byte[] size=new byte[4];await stream.ReadExactlyAsync(size,token);int length=BinaryPrimitives.ReadInt32LittleEndian(size);
        if(length<1||length>MaximumMessage)throw new InvalidDataException("Invalid local message size.");
        byte[] payload=new byte[length];await stream.ReadExactlyAsync(payload,token);
        return JsonSerializer.Deserialize<MultiboxMessage>(payload)??throw new InvalidDataException("Empty local message.");
    }
    public async ValueTask DisposeAsync()
    {stop.Cancel();await Task.WhenAll(listener,discovery);stop.Dispose();}
}
