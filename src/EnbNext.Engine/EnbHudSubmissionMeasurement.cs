namespace EnbNext.Engine;

internal readonly struct EnbHudSubmissionMeasurement : IDisposable
{
    private static readonly string[] HudSubmissionStageNames=["hud-submit-setup","hud-submit-uniforms","hud-submit-state","hud-submit-mapper","hud-submit-mapper-uniforms","hud-submit-textures","hud-submit-draw","hud-submit-cleanup"];
    private readonly EnbNext.Rendering.OpenGL.EnbOpenGlStage0TextureBackend? _backend;
    private readonly bool _previous;
    internal EnbHudSubmissionMeasurement(EnbNext.Rendering.OpenGL.EnbOpenGlStage0TextureBackend backend)
    {
        _backend=FullClientBenchmarkMeter.Active is null?null:backend;
        _previous=backend.MeasureSubmissionStages;
        if(_backend is not null){backend.MeasureSubmissionStages=true;backend.ResetSubmissionTiming();}
    }
    public void Dispose()
    {
        if(_backend is null)return;
        var ticks=_backend.SubmissionStageTicks;
        for(int i=0;i<ticks.Length;i++)FullClientBenchmarkMeter.RecordObservedWallTicks(HudSubmissionStageNames[i],ticks[i]);
        _backend.MeasureSubmissionStages=_previous;
    }
}
