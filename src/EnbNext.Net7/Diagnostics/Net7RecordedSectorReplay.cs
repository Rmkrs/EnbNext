using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace EnbNext.Net7.Diagnostics;

/// <summary>Local transport for one stationary benchmark trial. Recorded bytes
/// go through real loopback UDP sockets and normal production receive pumps.
/// Generated outbound traffic is drained locally, never sent to a game server.</summary>
public sealed class Net7RecordedSectorReplay : IAsyncDisposable
{
    private readonly UdpClient _master;
    private readonly UdpClient _routed;
    private readonly CancellationTokenSource _stop = new();
    private readonly Net7WireReplayCursor _cursor;
    private readonly long _bootstrapTime;
    private Task? _drainMaster, _drainRouted;
    private IPEndPoint? _masterTarget, _routedTarget;
    private bool _started;
    private Net7Session? _session;
    private readonly List<Net7RecordedTimeSynchronization> _timeSynchronizations=new();
    private readonly Queue<uint> _pendingControlTimes = new();
    private int _disposed;
    public string? ViewBootstrap {get; private set;}
    public Func<string,Task>? ViewInput { get; set; }
    public Net7RecordedSectorBootstrap Bootstrap { get; }
    public Net7WireReplayClock Clock { get; }
    public long ElapsedMicroseconds => _bootstrapTime + Clock.ElapsedMicroseconds;
    public bool IsMeasuring => _cursor.IsMeasuring;
    public long InboundDatagrams { get; private set; }
    private long _outboundDatagrams;
    public long OutboundDatagrams => Interlocked.Read(ref _outboundDatagrams);
    public long MaximumDeliveryLatenessMicroseconds => _cursor.MaximumDeliveryLatenessMicroseconds;

    public Net7RecordedSectorReplay(string path, long measurementStart, long measurementEnd)
    {
        var recording = Net7WireReplayRecording.Load(path);
        // Validate the requested window before acquiring an active session.
        _ = recording.CreateTrial(measurementStart, measurementEnd);
        long bootstrapIndex = -1;
        Net7RecordedSectorBootstrap? bootstrap = null;
        foreach (string line in File.ReadLines(EnbNext.Installation.EnbPathCase.Resolve(path)))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var row=doc.RootElement;
            if(row.GetProperty("type").GetString()!="marker" ||
                row.GetProperty("name").GetString()!="replay-sector-bootstrap-v1")continue;
            long time=row.GetProperty("elapsedMicroseconds").GetInt64();
            if(time>=measurementEnd)continue;
            if(bootstrap is not null)throw new InvalidDataException("Select a capture window without a sector handoff.");
            bootstrap=JsonSerializer.Deserialize<Net7RecordedSectorBootstrap>(row.GetProperty("value").GetString()!)
                ??throw new InvalidDataException("Missing recorded bootstrap.");
            bootstrapIndex=row.GetProperty("index").GetInt64();
            _bootstrapTime=time;
        }
        Bootstrap=bootstrap??throw new InvalidDataException("This recording predates the replay bootstrap. A new bounded recording is required; scene snapshots cannot substitute for it.");
        if(Bootstrap.InputTimingVersion != 2)
            throw new InvalidDataException("This recording lacks applied control/time-synchronization timestamps; it cannot calibrate the full client.");
        if(Bootstrap.MasterSequence is null || Bootstrap.RoutedSequence is null)
        {
            // First diagnostic recordings predate explicit sequence fields. The
            // successful normal negotiation packets establish the same lineage;
            // fixed sends after them must still carry that exact sequence.
            EnbNext.Net7.Transport.Net7UdpSequenceSnapshot Recover(string channel,params ushort[] normalOpcodes)
            {
                var packets=recording.Frames.Where(f=>f.Index<bootstrapIndex && f.Direction==Net7WireDirection.Outbound && f.Transport==Net7WireTransport.Udp && f.Channel==channel)
                    .Select(f=>ServerUdpFrame.Parse(f.WireBytes)).ToArray();
                var normal=packets.LastOrDefault(f=>normalOpcodes.Contains(f.Opcode))??throw new InvalidDataException("Missing successful negotiation sequence.");
                if(packets[^1].Sequence!=normal.Sequence)throw new InvalidDataException("Ambiguous sequence after negotiation.");
                return new(unchecked(normal.Sequence+1),normal.Sequence,true);
            }
            Bootstrap=Bootstrap with {MasterSequence=Bootstrap.MasterSequence??Recover("master-control-udp",0x200F),
                RoutedSequence=Bootstrap.RoutedSequence??Recover("routed-udp",0x2008,0x2026)};
        }
        if(measurementStart<_bootstrapTime)throw new InvalidDataException("Measurement begins before the recorded gameplay boundary.");
        var inputFrames = new List<Net7WireCaptureFrame>();
        foreach(string line in File.ReadLines(EnbNext.Installation.EnbPathCase.Resolve(path)))
        {
            if(string.IsNullOrWhiteSpace(line))continue;
            using var doc=JsonDocument.Parse(line);var row=doc.RootElement;
            if(row.GetProperty("type").GetString()!="marker")continue;
            string? name=row.GetProperty("name").GetString();
            if(name=="replay-view-bootstrap-v1")ViewBootstrap=row.GetProperty("value").GetString();
            if(name=="replay-time-synchronization-v2" && row.GetProperty("elapsedMicroseconds").GetInt64()<measurementEnd)
                _timeSynchronizations.Add(JsonSerializer.Deserialize<Net7RecordedTimeSynchronization>(row.GetProperty("value").GetString()!)
                    ??throw new InvalidDataException("Missing time-synchronization receipt."));
            if(name is not ("replay-flight-applied-v2" or "replay-target-input-v1" or "replay-view-input-v1"))continue;
            inputFrames.Add(new(row.GetProperty("index").GetInt64(),row.GetProperty("elapsedMicroseconds").GetInt64(),
                default,Net7WireDirection.Outbound,Net7WireTransport.Udp,name,"","",
                System.Text.Encoding.UTF8.GetBytes(row.GetProperty("value").GetString()!)));
        }
        var frames=recording.Frames.Concat(inputFrames).Where(f=>f.Index>bootstrapIndex && f.ElapsedMicroseconds<measurementEnd).OrderBy(f=>f.Index).ToArray();
        if(frames.Any(f=>f.Direction==Net7WireDirection.Inbound &&
            (f.Transport!=Net7WireTransport.Udp || f.Channel is not ("master-control-udp" or "routed-udp"))))
            throw new InvalidDataException("Recording contains an unsupported inbound transport after sector bootstrap.");
        _cursor=new(frames,measurementStart,measurementEnd);
        if(Bootstrap.ClockCaptureElapsedMicroseconds is not long anchor || anchor<0 || anchor>_bootstrapTime)
            throw new InvalidDataException("Missing or invalid replay clock anchor.");
        Clock=new(Bootstrap.ClientTime, _bootstrapTime-anchor);
        foreach(var frame in frames.Where(f=>f.Channel=="replay-flight-applied-v2"))
            _pendingControlTimes.Enqueue(JsonSerializer.Deserialize<Net7RecordedFlightInput>(frame.WireBytes)!.ClientTime);
        Clock.LimitSimulationTimeUntilInput(_pendingControlTimes.TryPeek(out var nextTime) ? nextTime : null);
        _master=new(new IPEndPoint(IPAddress.Loopback,0));
        try{_routed=BindGameplayPeer();}
        catch{_master.Dispose();throw;}
    }

    private static UdpClient BindGameplayPeer()
    {
        for(int port=Net7ServerOptions.CurrentMinGameplaySectorUdpPort;port<=Net7ServerOptions.CurrentMaxGameplaySectorUdpPort;port++)
        {
            var socket=new UdpClient(AddressFamily.InterNetwork);
            try{socket.ExclusiveAddressUse=true;socket.Client.Bind(new IPEndPoint(IPAddress.Loopback,port));return socket;}
            catch(SocketException ex)when(ex.SocketErrorCode==SocketError.AddressAlreadyInUse){socket.Dispose();}
            catch{socket.Dispose();throw;}
        }
        throw new IOException("No free loopback gameplay port; replay cannot relax production admission rules.");
    }

    public async Task AttachAsync(Net7Session session,CancellationToken cancellationToken=default)
    {
        if(_masterTarget is not null)throw new InvalidOperationException("Replay is already attached.");
        foreach(var receipt in _timeSynchronizations)session.RecordedTimeSynchronizations.Enqueue(receipt);
        (_masterTarget,_routedTarget)=await session.BeginRecordedSectorAsync(Bootstrap,
            (IPEndPoint)_master.Client.LocalEndPoint!, (IPEndPoint)_routed.Client.LocalEndPoint!,cancellationToken).ConfigureAwait(false);
        _session=session;
        _drainMaster=DrainAsync(_master);_drainRouted=DrainAsync(_routed);
    }
    private async Task DrainAsync(UdpClient socket)
    {
        try {while(!_stop.IsCancellationRequested){await socket.ReceiveAsync(_stop.Token).ConfigureAwait(false);Interlocked.Increment(ref _outboundDatagrams);}}
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
        catch(ObjectDisposedException)when(_stop.IsCancellationRequested){}
        catch(SocketException)when(_stop.IsCancellationRequested){}
    }
    public async Task RunAsync(CancellationToken cancellationToken=default)
    {
        if(_started || _masterTarget is null)throw new InvalidOperationException("A trial can run only once after attachment.");
        _started=true;Clock.Start();
        await _session!.SendRecordedSectorLoginAsync(cancellationToken).ConfigureAwait(false);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,_stop.Token);
        while(!_cursor.IsComplete)
        {
            linked.Token.ThrowIfCancellationRequested();
            await _cursor.AdvanceAsync(ElapsedMicroseconds,DeliverAsync,linked.Token).ConfigureAwait(false);
            if(!_cursor.IsComplete)await Task.Delay(1,linked.Token).ConfigureAwait(false);
        }
    }
    private async ValueTask DeliverAsync(Net7WireCaptureFrame frame,CancellationToken cancellationToken)
    {
        if(frame.Channel=="replay-view-input-v1")
        {
            if(ViewInput is not {} input)throw new InvalidOperationException("Recorded view inputs require the full production view.");
            await input(System.Text.Encoding.UTF8.GetString(frame.WireBytes)).ConfigureAwait(false);return;
        }
        if(frame.Channel=="replay-flight-applied-v2")
        {
            var applied=JsonSerializer.Deserialize<Net7RecordedFlightInput>(frame.WireBytes)
                ??throw new InvalidDataException("Missing applied flight input.");
            if(!await _session!.UpdateRecordedFlightInputAsync(applied.Input,applied.ClientTime,cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("An originally applied flight input was rejected during replay.");
            if(!_pendingControlTimes.TryDequeue(out var delivered) || delivered!=applied.ClientTime)
                throw new InvalidDataException("Replay flight input order differs from its clock boundary.");
            Clock.LimitSimulationTimeUntilInput(_pendingControlTimes.TryPeek(out var nextTime) ? nextTime : null);
            return;
        }
        if(frame.Channel=="replay-target-input-v1")
        {
            uint targetId=uint.Parse(System.Text.Encoding.UTF8.GetString(frame.WireBytes),System.Globalization.CultureInfo.InvariantCulture);
            await _session!.RequestTargetAsync(targetId,cancellationToken).ConfigureAwait(false);return;
        }
        if(frame.Direction!=Net7WireDirection.Inbound)return;
        var socket=frame.Channel=="master-control-udp"?_master:_routed;
        var target=frame.Channel=="master-control-udp"?_masterTarget!:_routedTarget!;
        await socket.SendAsync(frame.WireBytes,target,cancellationToken).ConfigureAwait(false);
        InboundDatagrams++;
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        _stop.Cancel();
        _master.Dispose();_routed.Dispose();
        if(_drainMaster is not null)await _drainMaster.ConfigureAwait(false);
        if(_drainRouted is not null)await _drainRouted.ConfigureAwait(false);
        _stop.Dispose();
    }
}
