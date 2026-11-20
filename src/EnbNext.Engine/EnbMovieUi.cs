using System.Collections.Immutable;
using EnbNext.Assets;

namespace EnbNext.Engine;

internal sealed class EnbMovieUi : IDisposable
{
    private readonly EnbRetailAssetBundle _assets;
    private readonly Func<(EnbUiSize Size,double Scale)> _viewport;
    private readonly EnbRetailMovieAssets _resources;
    private readonly EnbRetailMovieScene _scene;
    private readonly EnbRetailMovieRuntime _runtime;
    private readonly EnbRetailMovieClock _clock = new();
    public EnbWorldRenderer Renderer { get; }
    private readonly EnbRetailFontFace? _debugFont;
    public readonly EnbRetailText[] DebugLines=[new(),new(),new()];
    public string DebugText {get;private set;}="";
    public uint FadeColor {get;private set;}
    public double LetterboxHeight {get;private set;}
    private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EnbRetailStarLayerSelection? _builtStars;
    private LiveWorldStarFieldRenderer? _starOwner;
    private EnbBackdropGeometry _sky = new([], null, null);
    private ImmutableArray<EnbBackdropGeometry> _stars = [];
    private long _frame;
    private bool _disposed,_pumping;
    private (int Width,int Height) _publishedSize;
    public string? CapturePrefix {get;set;}
    // A missing native surface is not ready for capture and must not advance time.
    public Func<string,Task?>? CaptureFrame {get;set;}
    public Action<string>? Diagnostic {get;set;}
    public event Action? Published;
    public event Action? Completed;
    public Task Completion=>_complete.Task;
    public EnbRetailMovieRuntime Runtime=>_runtime;
    public Func<uint> MovieClock {get;set;}=()=>unchecked((uint)Environment.TickCount64);

    public EnbMovieUi(EnbRetailAssetBundle assets,EnbRetailMovieScript script,Func<(EnbUiSize Size,double Scale)> viewport,bool enableAudio=true)
    {
        _viewport=viewport;_assets=assets;_debugFont=EnbRetailFontFactory.CreateMovieFont(assets);
        _resources=new(assets);_scene=new(_resources);
        Renderer=new(()=>assets.BaseAssetResolver,()=>assets,()=>null,enableAudio,
            useMainViewEnvironment:false,continuousPresentation:false);
        _resources.ModelFactory=Renderer.CreateMovieModelFactory;
        _resources.PassEffectPreloader=Renderer.PreloadMoviePassEffect;
        _resources.ParticleJetEffectPreloader=Renderer.PreloadMovieParticleJetEffect;
        _resources.ObjectEffectPreloader=Renderer.PreloadMovieObjectEffect;
        _resources.RetainedObjectEffectFactory=Renderer.AcquireMovieRetainedObjectEffect;
        _runtime=new(script,_scene,true);_resources.EffectDeadline=_runtime.RetireEffectAt;
    }
    public void MovePointer(EnbUiPoint point,EnbUiSize size)
    {if(size.Width>0&&size.Height>0)_scene.Mouse=new((float)(point.X/size.Width),(float)(point.Y/size.Height));}
    private static (int Width,int Height) Pixels(EnbUiSize size,double scale)
        =>(Math.Max(1,(int)Math.Round(size.Width*scale)),Math.Max(1,(int)Math.Round(size.Height*scale)));
    private void Complete(){Completed?.Invoke();_complete.TrySetResult();}
    public async Task Pump()
    {
        var(size,scale)=_viewport();
        if(_disposed||_complete.Task.IsCompleted||_pumping||size.Width<=0||size.Height<=0)return;
        _pumping=true;
        try
        {
            if(CapturePrefix is {} prefix && _runtime.Started && !_runtime.Ended && !_runtime.Paused)
            {
                if(Renderer.LastRenderFailure is {} failure)throw new InvalidOperationException(failure);
                if(Pixels(size,scale)!=_publishedSize){PublishFrame();return;}
                if(Renderer.PresentedFrameToken!=_frame)return;
                var capture=CaptureFrame?.Invoke(prefix);if(capture is null)return;
                await capture;
                if(_disposed||_runtime.Ended)return;
            }
            _clock.Advance(_runtime,MovieClock(),CapturePrefix is not null);
            if(_runtime.Ended)
            {
                if(_runtime.FailedCommand is {} failed)Diagnostic?.Invoke($"Movie line {failed.LineNumber}: {failed.Text}");
                Complete();return;
            }
            PublishFrame();
        }
        catch(Exception ex) when(ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {Diagnostic?.Invoke("Movie playback: "+ex.Message);Complete();}
        finally{_pumping=false;}
    }
    private void PublishFrame()
    {
        var(size,scale)=_viewport();
        RefreshBackdrop();_publishedSize=Pixels(size,scale);
        var frame=EnbRetailMovieFrame.Capture(_scene,_resources,_runtime,_assets,_publishedSize.Width,_publishedSize.Height);
        Renderer.SetMovieFrame(frame,new(_sky,_stars),++_frame);
        FadeColor=EnbRetailMovieFrame.PackFade(frame.Fade);LetterboxHeight=Math.Max(0,size.Height*frame.Letterbox);
        double fps=1.0/(Math.Max(1u,_runtime.LastDeltaMilliseconds)*(double).001f);
        var statistics=Renderer.MovieRenderStatistics;
        DebugText=frame.ShowFrame?FormattableString.Invariant($"Frame: {unchecked((int)_runtime.DisplayFrame)}  Frame Rate: {fps:F6}\n{statistics.Polygons} Polys/frame ({(uint)(statistics.Polygons*(float)fps)/1000}k pps)\n{statistics.Vertices} Verts/frame ({(uint)(statistics.Vertices*(float)fps)/1000}k vps)"):"";
        var lines=DebugText.Split('\n');
        for(int i=0;i<DebugLines.Length;i++)
        {DebugLines[i].SetText(i<lines.Length?lines[i]:"",_debugFont,new(1,0,0));DebugLines[i].SetDisplayScale(1,1);}
        Published?.Invoke();
    }
    private void RefreshBackdrop()
    {
        if(!ReferenceEquals(_builtStars,_resources.Stars))
        {
            _starOwner?.Dispose();_starOwner=null;_stars=[];_builtStars=_resources.Stars;
            if(_builtStars is not null && LiveWorldStarFieldRenderer.TryCreateForMovie(_assets,_resources,out var stars,out var error))
            {_stars=stars.Geometry;_starOwner=stars;}
        }
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;Renderer.ClearFrame();_runtime.Dispose();Complete();
        _starOwner?.Dispose();_starOwner=null;foreach(var text in DebugLines)text.Dispose();
    }
}
