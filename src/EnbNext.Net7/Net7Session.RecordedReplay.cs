using System.Net;
using EnbNext.Net7.Diagnostics;
using EnbNext.Net7.LocalData;
using EnbNext.Net7.Protocol;
using EnbNext.Net7.Transport;

namespace EnbNext.Net7;

/// <summary>Recorded after route negotiation and before the first sector login.
/// This is session bootstrap data, never a replacement scene snapshot.</summary>
public sealed record Net7RecordedSectorBootstrap(Net7CharacterSelectionResult Selection,
    Net7SectorJoinClientData JoinData, uint RetainedMasterResponseDword,
    int ServerFrequencyMultiplier, uint ClientTime)
{
    public long? ClockCaptureElapsedMicroseconds {get;init;}
    public int InputTimingVersion {get;init;} = 1;
    public Net7UdpSequenceSnapshot? MasterSequence {get;init;}
    public Net7UdpSequenceSnapshot? RoutedSequence {get;init;}
    public byte[] JoinWire { get; init; } = JoinData.ToWireBytes();
}

public sealed record Net7RecordedFlightInput(EnbNext.Net7.Gameplay.Net7FlightInput Input, uint ClientTime);
public sealed record Net7RecordedTimeSynchronization(string PayloadBase64, uint ClientTime);

public sealed partial class Net7Session
{
    internal Queue<Net7RecordedTimeSynchronization> RecordedTimeSynchronizations {get;} = new();
    private uint ResolveRecordedTimeSynchronization(EnbNext.Protocol.Net.Packet packet, uint clientTime)
    {
        if (packet.Header.Opcode != 0x0034) return clientTime;
        string payload=Convert.ToBase64String(packet.Payload.Span);
        if (_recordedReplay)
        {
            if (!RecordedTimeSynchronizations.TryDequeue(out var receipt) || receipt.PayloadBase64!=payload)
                throw new InvalidDataException("Replay time-synchronization receipt is missing or out of order.");
            return receipt.ClientTime;
        }
        if (_wireCapture?.IsActive == true)
            _wireCapture.Mark("replay-time-synchronization-v2", System.Text.Json.JsonSerializer.Serialize(
                new Net7RecordedTimeSynchronization(payload, clientTime)));
        return clientTime;
    }

    private bool _recordedReplay;
    /// <summary>Diagnostic replay entry. Both peers must be loopback endpoints;
    /// raw datagrams still cross the normal socket, admission, reliability and
    /// gameplay dispatch path. No account authentication or remote connection.</summary>
    public async Task<(IPEndPoint Master, IPEndPoint Routed)> BeginRecordedSectorAsync(
        Net7RecordedSectorBootstrap recorded, IPEndPoint masterPeer, IPEndPoint routedPeer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        if (!IPAddress.IsLoopback(masterPeer.Address) || !IPAddress.IsLoopback(routedPeer.Address) ||
            _options.Host != "127.0.0.1")
            throw new InvalidOperationException("Recorded sessions are restricted to explicit loopback transports.");
        if (LoginState != Net7LoginState.Created || _master is not null || _routed is not null || _account is not null)
            throw new InvalidOperationException("Each replay trial requires a fresh session.");
        if (recorded.Selection.FixedSessionId == 0 || recorded.JoinData.FixedSessionId != recorded.Selection.FixedSessionId ||
            recorded.ServerFrequencyMultiplier <= 0)
            throw new InvalidDataException("Invalid recorded sector bootstrap.");
        var joinData=Net7SectorJoinClientData.FromWireBytes(recorded.JoinWire);
        if(joinData.FixedSessionId!=recorded.JoinData.FixedSessionId || joinData.SelectedSector!=recorded.JoinData.SelectedSector || joinData.AdminLevel!=recorded.JoinData.AdminLevel)
            throw new InvalidDataException("Recorded join wire and semantic bootstrap disagree.");
        CharacterSelection = recorded.Selection;
        _transportState.CommitValidatedFixedSessionId(recorded.Selection.FixedSessionId);
        ServerFrequencyMultiplier = recorded.ServerFrequencyMultiplier;
        _localGalaxyMap = GalaxyMap = Net7LocalDataAssets.CurrentGalaxyMap;
        _master = await MasterControlUdpChannel.ConnectAsync(IPAddress.Loopback, masterPeer.Port,
            _transportState, _trace, cancellationToken).ConfigureAwait(false);
        _routed = await RoutedSectorUdpChannel.CreateAsync(IPAddress.Loopback,
            _transportState, _trace, cancellationToken).ConfigureAwait(false);
        _master.Sequence.RestoreRecorded(recorded.MasterSequence??throw new InvalidDataException("Missing master sequence bootstrap."));
        _routed.Sequence.RestoreRecorded(recorded.RoutedSequence??throw new InvalidDataException("Missing routed sequence bootstrap."));
        HookMasterEvents(_master);
        HookRoutedEvents(_routed);
        _recordedReplay = true;
        InitializeReliabilityAndKeepalive();
        _routed.CommitGameplayPort(routedPeer.Port);
        Route = new(recorded.Selection.Character, recorded.Selection.FixedSessionId,
            recorded.RetainedMasterResponseDword, routedPeer, joinData);
        _finalLoginCompletion = NewBoolWaiter();
        _startAcknowledged = NewBoolWaiter();
        _intermediateLoginComplete = NewBoolWaiter();
        _gameplayClientTargetActive = true;
        _routed.SetGameplayForwardingActive(true);
        _reliability!.StartWorkerIfConfigured();
        SetLoginState(Net7LoginState.SectorJoining);
        StartIntermediateLoginWatchdog();
        StartMasterPump();
        StartRoutedPump();
        return (new(IPAddress.Loopback, _master.LocalEndPoint.Port),
            new(IPAddress.Loopback, _routed.LocalEndPoint.Port));
    }

    internal async Task SendRecordedSectorLoginAsync(CancellationToken cancellationToken)
    {
        if(!_recordedReplay || Route is null || _routed is null)throw new InvalidOperationException("No recorded route.");
        await _routed.SendToActiveBestEffortAsync(0x0002,Net7SectorLoginWire.Build(Route.SectorJoinData,_clock.GetMilliseconds()),cancellationToken).ConfigureAwait(false);
    }
    public async Task CompleteRecordedSectorEntryAsync(CancellationToken cancellationToken = default)
    {
        if (!_recordedReplay || _options.Host != "127.0.0.1" || Route is null || LoginState != Net7LoginState.SectorJoining)
            throw new InvalidOperationException("No recorded sector entry is active.");
        await WaitForStableSectorEntryAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfFatalLoginSignaled();
        await StopIntermediateLoginWatchdogAsync().ConfigureAwait(false);
        SetLoginState(Net7LoginState.SectorReady);
    }
}
