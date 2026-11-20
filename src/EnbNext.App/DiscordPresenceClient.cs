using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace EnbNext.App;

// Presence-only local IPC. No authentication, subscriptions or game commands.
internal sealed class DiscordPresenceClient : IAsyncDisposable
{
    internal const string ApplicationId="1569629670507544617";
    private readonly Func<CancellationToken,Task<Stream?>> connect;
    private readonly Channel<byte> changed=Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true});
    private readonly CancellationTokenSource stop=new();
    private readonly Task worker;
    private DiscordPresenceActivity? desired;
    private int stopping,disposed;
    public DiscordPresenceClient():this(ConnectAsync){}
    internal DiscordPresenceClient(Func<CancellationToken,Task<Stream?>> connect)
    {this.connect=connect;worker=Task.Run(RunAsync);}
    public void SetActivity(DiscordPresenceActivity? value)
    {
        if(Volatile.Read(ref stopping)!=0)return;
        if(Equals(Interlocked.Exchange(ref desired,value),value))return;
        changed.Writer.TryWrite(0);
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref stopping,1)==0)
        {
            Volatile.Write(ref desired,null);changed.Writer.TryWrite(0);
            stop.CancelAfter(TimeSpan.FromSeconds(2));
        }
        await worker.ConfigureAwait(false);
        // The worker has retired every pending operation before returning.
        if(Interlocked.Exchange(ref disposed,1)==0)stop.Dispose();
    }
    private async Task RunAsync()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                while(changed.Reader.TryRead(out _)){}
                if(Volatile.Read(ref desired) is null)
                {
                    if(Volatile.Read(ref stopping)!=0)return;
                    await changed.Reader.WaitToReadAsync(stop.Token).ConfigureAwait(false);continue;
                }
                try
                {
                    using var stream=await connect(stop.Token).ConfigureAwait(false);
                    if(stream is not null)await RunConnectionAsync(stream,stop.Token).ConfigureAwait(false);
                }
                catch(Exception ex) when(IsUnavailable(ex)){}
                if(Volatile.Read(ref stopping)!=0)return;
                // Failed/absent Discord is optional. Retry quietly, never on the render thread.
                using var retry=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                retry.CancelAfter(TimeSpan.FromSeconds(5));
                try{await changed.Reader.WaitToReadAsync(retry.Token).ConfigureAwait(false);}
                catch(OperationCanceledException) when(!stop.IsCancellationRequested){}
            }
        }
        catch(OperationCanceledException){}
    }
    private async Task RunConnectionAsync(Stream stream,CancellationToken token)
    {
        using var connection=CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct=connection.Token;
        Task<(int Opcode,byte[] Payload)>? read=null;
        try
        {
            // A disable while connecting must not create a presence or even handshake.
            if(Volatile.Read(ref desired) is null)return;
            await WriteAsync(stream,0,JsonSerializer.SerializeToUtf8Bytes(new{v=1,client_id=ApplicationId}),ct).ConfigureAwait(false);
            using(var handshake=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(2));
                var ready=await ReadAsync(stream,handshake.Token).ConfigureAwait(false);
                using var json=JsonDocument.Parse(ready.Payload);
                if(ready.Opcode!=1 || !json.RootElement.TryGetProperty("evt",out var evt) || evt.GetString()!="READY")return;
            }
            DiscordPresenceActivity? sent=null;
            read=ReadAsync(stream,ct);
            Task<bool> wake=changed.Reader.WaitToReadAsync(ct).AsTask();
            while(true)
            {
                var value=Volatile.Read(ref desired);
                if(value is null || value!=sent)
                {
                    await WriteAsync(stream,1,ActivityPayload(value),ct).ConfigureAwait(false);
                    sent=value;
                    if(value is null)return;
                }
                var completed=await Task.WhenAny(read,wake).ConfigureAwait(false);
                if(completed==read)
                {
                    var frame=await read.ConfigureAwait(false);
                    if(frame.Opcode==2)return;
                    if(frame.Opcode==3)await WriteAsync(stream,4,frame.Payload,ct).ConfigureAwait(false);
                    else if(frame.Opcode==1)
                    {
                        using var reply=JsonDocument.Parse(frame.Payload);
                        if(reply.RootElement.TryGetProperty("evt",out var evt)&&evt.ValueKind==JsonValueKind.String&&evt.GetString()=="ERROR")return;
                    }
                    read=ReadAsync(stream,ct);
                }
                if(wake.IsCompleted)
                {
                    await wake.ConfigureAwait(false);
                    while(changed.Reader.TryRead(out _)){}
                    wake=changed.Reader.WaitToReadAsync(ct).AsTask();
                }
            }
        }
        finally
        {
            connection.Cancel();
            if(read is not null)try{await read.ConfigureAwait(false);}catch(Exception ex) when(IsUnavailable(ex)){}
        }
    }
    // Omit absent optional activity fields; clearing still requires explicit activity:null.
    private static readonly JsonSerializerOptions ActivityOptions=new()
        {DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull};
    internal static byte[] ActivityPayload(DiscordPresenceActivity? value)=>JsonSerializer.SerializeToUtf8Bytes(new
    {
        cmd="SET_ACTIVITY",args=new{pid=Environment.ProcessId,activity=value is null?null:new
        {type=0,details=value.Details,state=value.State,party=value.PartySize is int count?new{size=new[]{count,count}}:null,timestamps=new{start=value.StartedAt},assets=new{large_image="logo",large_text="Earth & Beyond"}}},
        nonce=Guid.NewGuid().ToString("N")
    },value is null?null:ActivityOptions);
    internal static async Task WriteAsync(Stream stream,int opcode,byte[] payload,CancellationToken token)
    {
        byte[] frame=new byte[8+payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame,opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4),payload.Length);payload.CopyTo(frame,8);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await stream.WriteAsync(frame,deadline.Token).ConfigureAwait(false);
        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
    }
    internal static async Task<(int Opcode,byte[] Payload)> ReadAsync(Stream stream,CancellationToken token)
    {
        byte[] header=new byte[8];await stream.ReadExactlyAsync(header,token).ConfigureAwait(false);
        int opcode=BinaryPrimitives.ReadInt32LittleEndian(header),length=BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        // Bound local peer input; presence replies never need arbitrary-size buffers.
        if(length<0||length>64*1024)throw new InvalidDataException("Invalid IPC frame size.");
        byte[] payload=new byte[length];await stream.ReadExactlyAsync(payload,token).ConfigureAwait(false);
        return(opcode,payload);
    }
    private static bool IsUnavailable(Exception ex)=>ex is IOException or SocketException or UnauthorizedAccessException
        or OperationCanceledException or JsonException or InvalidOperationException or ArgumentException;
    private static async Task<Stream?> ConnectAsync(CancellationToken token)
    {
        string directory=Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")??Environment.GetEnvironmentVariable("TMPDIR")
            ??Environment.GetEnvironmentVariable("TMP")??Environment.GetEnvironmentVariable("TEMP")??"/tmp";
        for(int index=0;index<10;index++)
        {
            token.ThrowIfCancellationRequested();
            Stream? stream=null;Socket? socket=null;
            try
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(100);
                if(OperatingSystem.IsWindows())
                {
                    var pipe=new NamedPipeClientStream(".","discord-ipc-"+index,PipeDirection.InOut,PipeOptions.Asynchronous);
                    stream=pipe;await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                }
                else
                {
                    socket=new(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(directory,"discord-ipc-"+index)),deadline.Token).ConfigureAwait(false);
                    stream=new NetworkStream(socket,ownsSocket:true);socket=null;
                }
                return stream;
            }
            catch(Exception ex) when(IsUnavailable(ex)){stream?.Dispose();socket?.Dispose();}
        }
        return null;
    }
}
