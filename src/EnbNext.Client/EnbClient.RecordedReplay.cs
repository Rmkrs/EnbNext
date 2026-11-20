using EnbNext.Client.Models;
using EnbNext.Net7;
using EnbNext.Net7.Diagnostics;
using EnbNext.Net7.LocalData;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    public string? RecordedViewBootstrap {get;set;}
    private bool _isRecordedReplay;
    public async Task CompleteBenchmarkCaptureAsync()
    {
        Task? pending=CancelPostInWorldWireCaptureStop();
        if(pending is not null)await pending.ConfigureAwait(false);
        await StopWireCaptureAsync("benchmark-recording-complete").ConfigureAwait(false);
    }
    public long? RecordedCaptureElapsedMicroseconds => _wireCapture.CurrentElapsedMicroseconds;
    public void MarkRecordedBenchmark(string name, string value) => _wireCapture.Mark(name, value);

    /// <summary>Uses the same session, scene publisher and render change feed as
    /// live play. Only the remote server is replaced by a recorded loopback peer.</summary>
    public async Task<Net7RecordedSectorReplay> PrepareRecordedReplayAsync(string capture,
        long measurementStartMicroseconds, long measurementEndMicroseconds,
        CancellationToken cancellationToken=default)
    {
        if(State!=ClientLifecycleState.Disconnected || _net7 is not null)
            throw new InvalidOperationException("Recorded replay requires a fresh client.");
        if(_options.ServerHost!="127.0.0.1" || !string.IsNullOrEmpty(_options.WireCaptureDirectory))
            throw new InvalidOperationException("Replay clients must explicitly select loopback and disable live recording.");
        var replay=new Net7RecordedSectorReplay(capture,measurementStartMicroseconds,measurementEndMicroseconds);
        try
        {
            _isRecordedReplay=true;
            _net7=new Net7Session(new Net7ServerOptions {Host="127.0.0.1"},Trace,replay.Clock,useCameraController:true);
            _net7.SetCDataCatalog(Net7LocalDataAssets.CurrentCData);
            _presentationSnapshots.Clear();
            LoadInstalledPresentationAssets();
            _net7.PresentationStateChanged+=OnNet7PresentationStateChanged;
            _net7.LoginStateChanged+=OnNet7LoginStateChanged;
            var selection=replay.Bootstrap.Selection;
            SelectedCharacter=MapCharacter(selection.Character,selection.SlotIndex);
            SelectedFixedSessionId=selection.FixedSessionId;
            Characters=[SelectedCharacter];
            _ = RetailAssets?.ResolveCameraModes(selection.FixedSessionId);
            _net7.GalaxyMapShowAll=RetailAssets?.ResolvePlayerOptions(selection.FixedSessionId).Options.GalaxyMapShowAll??false;
            if(RetailAssets is {} colorAssets)
                SetChatColors(selection.FixedSessionId,EnbNext.Assets.EnbRetailChatColors.LoadFile(Path.Combine(colorAssets.Paths.ClientOutputDirectory,
                    $"player{EnbNext.Assets.EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(selection.FixedSessionId)}_options.ini")));
            await replay.AttachAsync(_net7,cancellationToken).ConfigureAwait(false);
            var route=_net7.Route!;
            Route=new SectorRouteResult(SelectedCharacter,route.FixedSessionId,
                route.RetainedMasterResponseDword,route.GameplaySectorEndPoint);
            return replay;
        }
        catch
        {
            await replay.DisposeAsync().ConfigureAwait(false);
            if(_net7 is not null)await _net7.DisposeAsync().ConfigureAwait(false);
            _net7=null;throw;
        }
    }

    public async Task CompleteRecordedReplayEntryAsync(CancellationToken cancellationToken=default)
    {
        var session=_net7??throw new InvalidOperationException("No replay session.");
        await session.CompleteRecordedSectorEntryAsync(cancellationToken).ConfigureAwait(false);
        if(RetailAssets is {} assets)
        {
            var options=assets.ResolvePlayerOptions(session.FixedSessionId).Options;
            await session.InitializeChatAsync(options.MonitoredChatChannels,options.RestrictedStatus,cancellationToken).ConfigureAwait(false);
        }
    }
}
