using Point = EnbNext.Presentation.EnbUiPoint;
using Rect = EnbNext.Presentation.EnbUiRect;
using Size = EnbNext.Presentation.EnbUiSize;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldController
{
    public LiveIssueLog Issues {get;set;}=new();
    private long _lastIssueSample;
    private bool _presentationAssetFailed;
    public string BuildIssueReport()=>Issues.Describe()+"\n\n"+DiagnosticsText;
    private char[] _issueSampleBuffer=[];
    private ReadOnlySpan<char> IssueDiagnostics()
    {
        string main=Volatile.Read(ref _diagnosticsText),world=_w3dWorld.ClientAudioStatus,
            hud=_hudGpu.ClientAudioStatus,utility=Volatile.Read(ref _utilityInputDiagnostics);
        const string worldPrefix=" · world",hudPrefix=" · hud";
        int length=checked(main.Length+worldPrefix.Length+world.Length+hudPrefix.Length+hud.Length+utility.Length);
        if(_issueSampleBuffer.Length<length)Array.Resize(ref _issueSampleBuffer,Math.Max(length,_issueSampleBuffer.Length*2));
        var output=_issueSampleBuffer.AsSpan(0,length);int offset=0;
        main.AsSpan().CopyTo(output);offset+=main.Length;
        worldPrefix.AsSpan().CopyTo(output[offset..]);offset+=worldPrefix.Length;
        world.AsSpan().CopyTo(output[offset..]);offset+=world.Length;
        hudPrefix.AsSpan().CopyTo(output[offset..]);offset+=hudPrefix.Length;
        hud.AsSpan().CopyTo(output[offset..]);offset+=hud.Length;
        utility.AsSpan().CopyTo(output[offset..]);return output;
    }
    private void UpdateIssues()
    {
        long now=Environment.TickCount64;
        if(now-_lastIssueSample<250)return;
        _lastIssueSample=now;
        var text=IssueDiagnostics();
        while(!text.IsEmpty)
        {
            int newline=text.IndexOf('\n');var line=newline<0?text:text[..newline];
            if(LiveIssueLog.IsPresentationProblem(line))
            {
                int colon=line.IndexOf(':');
                Issues.Report("Presentation",(colon<0?line:line[..colon]).ToString(),line.ToString());
            }
            if(newline<0)break;text=text[(newline+1)..];
        }
        void Asset(string name,string? error)
        {if(!string.IsNullOrWhiteSpace(error) && !error.StartsWith("Waiting",StringComparison.OrdinalIgnoreCase))Issues.Report("Assets",name,error);}
        Asset("Cockpit",_ui.Cockpit.Error);Asset("Inventory",_ui.Inventory.Error);Asset("Loading",_loadingScreen.Error);
        Asset("Map",_ui.Map.Error);Asset("Options",_ui.Options.Error);
        Asset("Station",_stationLoadError);
        Asset("Bridge transition",_bridgeTransition.LastFailure);
        if (_bridgeAsset is { AnimationLoadFailures.Count: > 0 } bridge)
            Asset("Bridge animations",string.Join("; ",bridge.AnimationLoadFailures));
        if(_w3dWorld.StationRenderStatus.StartsWith("Station render blocked:",StringComparison.Ordinal))Asset("Station renderer",_w3dWorld.StationRenderStatus);
        // A presentation wait includes account selection and network entry. Elapsed
        // time alone is not an asset/render failure; network tasks own their timeouts.
        _presentationAssetFailed=_w3dWorld.StatusText.Contains("render failure",StringComparison.OrdinalIgnoreCase) ||
            (_stationActive && (_w3dWorld.StationRenderStatus.StartsWith("Station render blocked:",StringComparison.Ordinal) ||
            _stationLoadError?.StartsWith("Interior unavailable:",StringComparison.OrdinalIgnoreCase)==true)) || _loadingScreen.Error is not null;
        if(_presentationAssetFailed)
        {
            Issues.Report("Presentation","Loading failure",
                _stationLoadError??_loadingScreen.Error??_w3dWorld.StatusText);
            LoadingVisible=false;_hudGpu.SetLoadingPresentation(null);
        }
    }
}
