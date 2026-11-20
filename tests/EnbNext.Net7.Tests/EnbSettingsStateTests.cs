using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbSettingsStateTests
{
    private static EnbRetailAssetBundle Assets()
    {
        var paths = EnbInstalledAssetLocator.TryLocate(); Assert.NotNull(paths);
        return EnbRetailAssetBundle.Load(paths);
    }

    [InstalledRetailFact]
    public void NextOptionsPreviewLiveAndCancelRestoresTheOpeningValues()
    {
        string directory=Path.Combine(Path.GetTempPath(),"settings-"+Guid.NewGuid().ToString("N"));
        try
        {
            var assets=Assets();assets.CharacterInterface=EnbCharacterInterfaceSettings.Load(1,2,directory);
            var state=new EnbSettingsState(assets);var previews=new List<EnbQualityOfLifeOptions>();EnbQualityOfLifeOptions? saved=null;
            var opening=new EnbQualityOfLifeOptions(WaypointArrow:true);
            void Open(){state.Begin(123,7,1280,720,true,EnbSettingsState.NavigationSection);state.QualityOfLife=opening;
                state.PreviewQualityOfLife=previews.Add;state.SaveQualityOfLife=v=>saved=v;state.BeginQualityPreview();}
            Open();
            state.PreviewQualityChanges();Assert.Empty(previews);
            state.SetArrowSize(150);state.PreviewQualityChanges();state.ToggleConvenience(17);state.PreviewQualityChanges();state.PreviewQualityChanges();
            Assert.Equal([opening with {WaypointArrowSize=150},opening with {WaypointArrowSize=150,WaypointArrowDistance=false}],previews);
            state.Retire();
            Assert.Equal(opening,previews[^1]);Assert.Null(saved);
            Open();previews.Clear();
            state.ToggleConvenience(18);state.PreviewQualityChanges();
            Assert.True(state.TrySave(_=>{},null,null));
            Assert.Equal(opening with {WaypointArrowHeight=true},saved);Assert.Single(previews);
            Assert.Equal(200,(opening with {WaypointArrowSize=900}).ArrowSizePercent);
        }
        finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [InstalledRetailFact]
    public void NavigationSaveRetainsDisabledAutoPilotAndCancelRestoresSavedChoices()
    {
        string directory=Path.Combine(Path.GetTempPath(),"settings-"+Guid.NewGuid().ToString("N"));
        try
        {
            var assets=Assets();assets.CharacterInterface=EnbCharacterInterfaceSettings.Load(1,2,directory);
            var state=new EnbSettingsState(assets);state.Begin(123,7,1280,720,true,EnbSettingsState.NavigationSection);
            state.ToggleRoutePlanner();state.ToggleCheck();state.ToggleRoutePlanner();
            Assert.True(state.TrySave(_=>{},null,null));
            var saved=EnbCharacterInterfaceSettings.Load(1,2,directory);Assert.False(saved.RoutePlanner);Assert.True(saved.AutoPilot);
            state.Begin(123,7,1280,720,true,EnbSettingsState.NavigationSection);state.ToggleRoutePlanner();state.ToggleCheck();state.Retire();
            state.Begin(123,7,1280,720,true,EnbSettingsState.NavigationSection);Assert.False(state.RoutePlanner);Assert.True(state.Check);state.Retire();
        }
        finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [InstalledRetailFact]
    public void SoundPreviewRetiresForItsOriginalCharacterAndSaveFailureCanRetry()
    {
        var assets = Assets(); var original = assets.ResolvePlayerOptions(123).Options;
        var other = assets.ResolvePlayerOptions(124).Options;
        var state = new EnbSettingsState(assets, _ => null);
        state.Begin(123,8,1920,1080,true);state.Change(2,12);
        Assert.Equal(12,assets.ResolvePlayerOptions(123).Options.SoundDialog);
        Assert.Equal(other,assets.ResolvePlayerOptions(124).Options);
        Assert.False(state.TrySave(null,_=>throw new IOException("fixture"),null));
        Assert.Equal(8,state.Page);Assert.Contains("fixture",state.Error);
        Assert.Equal(12,assets.ResolvePlayerOptions(123).Options.SoundDialog);
        EnbRetailSoundOptions? saved=null;
        Assert.True(state.TrySave(null,v=>saved=v,null));Assert.Equal(12,saved!.Dialog);
        Assert.Equal(0,state.Page);Assert.Null(state.Error);
        Assert.Equal(original,assets.ResolvePlayerOptions(123).Options);
        state.Begin(123,8,1920,1080,true);state.Change(0,0);
        state.Begin(124,7,1920,1080,true);
        Assert.Equal(original,assets.ResolvePlayerOptions(123).Options);
        state.Retire();var value=(float)state.Sliders[0].Value;state.Change(0,99);
        Assert.Equal(value,(float)state.Sliders[0].Value);Assert.False(state.TrySave(_=>throw new Exception("retired"),null,null));
    }

    [InstalledRetailFact]
    public void FontDraftPreservesFractionalValuesIndependentOfCueNotches()
    {
        var assets=Assets();var state=new EnbSettingsState(assets,f=>EnbRetailFontFactory.TryCreate(assets,f,out var face)?face.CellHeight:null);
        state.Begin(123,9,1920,1080,true);var profile=state.Profile!;var original=state.Fonts.ToArray();
        if(!state.Check)state.ToggleCheck();
        state.Change(2,12);var scale=state.Fonts[0].Scale;
        int cues=0;state.SoundRequested+=_=>cues++;
        state.Sliders[3].SetValue(4.25); // A modified input can leave another row fractional.
        state.Change(2,12.2f);Assert.Equal(0,cues);Assert.Equal(.5f+12.2f*.1f,state.Fonts[0].Scale);
        Assert.Equal(4.25,state.Sliders[3].Value);
        scale=state.Fonts[0].Scale;
        Assert.All(state.Fonts,f=>Assert.Equal(scale,f.Scale));
        Assert.Equal(original.Select(f=>f.FontId),state.Fonts.Select(f=>f.FontId));
        state.ToggleCheck();state.Change(0,1);state.Change(2,3);
        Assert.Equal(scale,state.Fonts[0].Scale);Assert.NotEqual(scale,state.Fonts[1].Scale);
        state.Retire();
        foreach(var font in original)Assert.Equal(font,assets.ResolveChatFont(123,profile,font));
    }

    [InstalledRetailFact]
    public void MissingOptionalFontKeepsThePreviouslyResolvedDefinition()
    {
        var assets=Assets();var state=new EnbSettingsState(assets,_=>null);
        state.Begin(123,9,1920,1080,false);var original=state.Fonts[0];
        state.Change(2,original.Scale>1?0:15);
        Assert.Equal(original,state.Fonts[0]);Assert.Contains("unavailable",state.Error);
        state.Retire();Assert.Empty(state.Fonts);Assert.Null(state.Profile);
    }

    [InstalledRetailFact]
    public void PreparedFontMetricsMatchDecodedFacesAndReuseAuthoredSlots()
    {
        var assets=Assets();var session=EnbNext.Rendering.EnbRenderSession.Of(assets);var metrics=session.SettingsFontMetrics;
        Assert.Same(metrics,EnbNext.Rendering.EnbRenderSession.Of(assets).SettingsFontMetrics);
        var definitions=assets.Fonts!.AvailableFonts.Select(name=>new EnbFontDefinition(0,null,name,1,0)).ToArray();
        foreach(var definition in definitions)
        {
            Assert.True(EnbRetailFontFactory.TryCreate(assets,definition,out var face));
            Assert.Equal(face.CellHeight,metrics.Height(definition));
        }
        for(int i=0;i<100;i++)foreach(var definition in definitions)_=metrics.Height(definition);
        // A steady-state allocation repeats in every pass; a one-off runtime event charged to this thread
        // (tiered compilation's worker starting, say) does not. One clean pass of 4096 rounds proves the lookup.
        long allocated=-1;
        for(int pass=0;pass<3&&allocated!=0;pass++)
        {
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<4096;i++)foreach(var definition in definitions)_=metrics.Height(definition);
            allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        }
        Assert.Equal(0,allocated);
        Assert.Null(metrics.Height(new(0,null,"missing-settings-font-fixture.tga",1,0)));
    }
    [InstalledRetailFact]
    public void SettingsReadActualSliderValuesAndRetirementCancelsEveryCapture()
    {
        var assets=Assets();var state=new EnbSettingsState(assets);
        state.Begin(123,8,1920,1080,true);
        var slider=state.Sliders[0];
        slider.SetValue(12.5000001); // D4 rounds to single precision before the notch comparison.
        Assert.Equal((double)(float)12.5000001,slider.Value);
        Assert.Equal((float)slider.Value,state.Sound().Music);
        var resources=EnbNext.Rendering.EnbRenderSession.Of(assets).SliderPanels;
        for(int i=0;i<state.Sliders.Length;i++)
        {
            var t=resources.Tracks[i];double span=resources.ArrowSpan;
            var edge=new EnbUiPoint(320+t.X-span,240-t.Y-span/2);
            Assert.Equal(EnbScrollbarPart.Up,state.Sliders[i].Hit(edge));
            Assert.Equal(EnbScrollbarPart.None,state.Sliders[i].Hit(new(edge.X-.01,edge.Y)));
            state.Sliders[i].Begin(EnbScrollbarPart.Up,edge,100);
        }
        float actual=(float)slider.Value;EnbRetailSoundOptions? saved=null;
        Assert.True(state.TrySave(null,v=>saved=v,null));Assert.Equal(actual,saved!.Music);
        Assert.All(state.Sliders,s=>{Assert.Equal(EnbScrollbarPart.None,s.Pressed);Assert.Equal(0,s.RepeatAt);});
        state.Begin(123,9,1920,1080,true);
        Assert.Same(slider,state.Sliders[0]);
        for(int i=0;i<5;i++)Assert.Equal(state.Range(i)-1,state.Sliders[i].Maximum);
        state.Retire();
    }

}
