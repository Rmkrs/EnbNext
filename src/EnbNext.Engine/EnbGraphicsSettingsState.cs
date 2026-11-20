using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>005CF6C0/005CF310/005CF7E0 dialog values and existing Next preview transaction.</summary>
internal sealed partial class EnbGraphicsSettingsState
{
    public EnbSliderInput[] Sliders { get; } = new EnbSliderInput[5];
    public EnbGraphicsSettingsState(EnbSliderPanelResources resources)
    {
        for(int i=0;i<Sliders.Length;i++)
        {
            var t=resources.Tracks[i];double span=resources.ArrowSpan;
            var track=new EnbNext.Presentation.EnbUiRect(320+t.X,240-t.Y-span/2,t.Width,span);
            Sliders[i]=new(track,new(track.X-span,track.Y,track.Width+2*span,span),horizontal:true,singlePrecision:true);
            Sliders[i].Configure(100,1);
        }
        var last=resources.Tracks[4];double arrow=resources.ArrowSpan;
        var cap=new EnbNext.Presentation.EnbUiRect(320+last.X+NextSliderInset,240-last.Y-arrow/2,last.Width-NextSliderInset,arrow);
        Background=new(cap,new(cap.X-arrow,cap.Y,cap.Width+2*arrow,arrow),horizontal:true,singlePrecision:true);
        Background.Configure(BackgroundCaps.Length,1);
    }
    // Advanced Graphics: Next options on the same OPT04A art, with the background frame cap on the last slider row.
    public bool Advanced { get; set; }
    // Next slider rows start this far right of the authored track, after a left-aligned caption.
    public const double NextSliderInset=40;
    public static readonly int[] BackgroundCaps=[10,15,20,24,30,40,50,60,75,90,120,144];
    public bool BackgroundCapEnabled { get; set; }
    public EnbSliderInput Background { get; }
    public int BackgroundCap=>BackgroundCaps[Math.Clamp((int)Math.Round(Background.Value),0,BackgroundCaps.Length-1)];
    public void LoadBackgroundCap(bool enabled,int cap)
    {
        BackgroundCapEnabled=enabled;if(cap<=0)cap=60;
        int index=0;for(int i=1;i<BackgroundCaps.Length;i++)if(Math.Abs(BackgroundCaps[i]-cap)<Math.Abs(BackgroundCaps[index]-cap))index=i;
        Background.Configure(BackgroundCaps.Length,1);Background.SetValue(index);Background.Cancel();
    }
    public EnbEnhancedGraphicsOptions Enhanced = EnbEnhancedGraphicsOptions.Default;
    public EnbEnhancedGraphicsOptions OriginalEnhanced = EnbEnhancedGraphicsOptions.Default;
    public bool Recommended { get; set; }
    public string? TextureOverrides {get;set;}
    public string? OriginalTextureOverrides {get;set;}
    public bool PreviewActive { get; private set; }
    public string? Error { get; private set; }

    public void Load(EnbRetailPlayerOptions options, EnbRetailRenderBudgetSourceSettings source)
    {
        Recommended = options.GraphicsDefault;
        Sliders[0].SetValue(options.GraphicsGamma ?? 10f);
        Sliders[1].SetValue(options.GraphicsLandPoly ?? Normalize(source.LandPolyBudget, 8_000, 40_000));
        Sliders[2].SetValue(options.GraphicsLandDetail ?? Normalize(source.LandLod, 0, 8));
        Sliders[3].SetValue(options.GraphicsSpacePoly ?? Normalize(source.PolyBudget, 8_000, 40_000));
        Sliders[4].SetValue(options.GraphicsTextureSize ?? 100f - Normalize(source.TextureReduction, 0, 3));
        CancelInput();
        Error = null;
    }

    public void BeginPreview() => PreviewActive = true;
    public EnbRetailGraphicsOptions Options() => new(Recommended,(float)Sliders[0].Value,(float)Sliders[1].Value,(float)Sliders[2].Value,(float)Sliders[3].Value,(float)Sliders[4].Value);

    public void Preview(Action<EnbRetailGraphicsOptions?>? preview)
    {
        if (!PreviewActive) return;
        try
        {
            if (preview is null) throw new InvalidOperationException("Graphics preview is unavailable.");
            preview(Options()); Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Error = "Could not preview graphics settings: " + ex.Message; }
    }

    public bool TryApply(Action<EnbRetailGraphicsOptions>? save, Action<EnbRetailGraphicsOptions?>? preview, Action? commitDisplay)
    {
        if (!PreviewActive) return false;
        try
        {
            if (save is null) throw new InvalidOperationException("Character graphics settings are unavailable.");
            save(Options()); preview?.Invoke(null); commitDisplay?.Invoke();
            PreviewActive = false; CancelInput(); Error = null; return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Error = "Could not apply graphics settings: " + ex.Message; return false; }
    }

    public void Retire(Action<EnbRetailGraphicsOptions?>? preview, Action? cancelDisplay)
    {
        CancelInput();
        if (!PreviewActive) return;
        PreviewActive = false;
        try { preview?.Invoke(null); }
        finally { cancelDisplay?.Invoke(); }
    }

    private void CancelInput() { foreach(var slider in Sliders) slider.Cancel(); Background.Cancel(); }

    private static float Normalize(int value, int minimum, int maximum)
        => maximum == minimum ? 0f : (value - minimum) * 100f / (maximum - minimum);
}
