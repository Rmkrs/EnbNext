using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbGraphicsSettingsStateTests
{
    private static EnbGraphicsSettingsState Create() => new(EnbRenderSession.Of(EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!)).SliderPanels);
    [Fact]
    public void ActualDraftPreservesMissingInputDefaultsAndFractionalValues()
    {
        var state=Create();var values=state.Sliders;
        state.Load(new(false),new(24000,2000,16000,7,2,1));
        Assert.Equal(new float[]{10,25,87.5f,50,100f-200f/3},state.Sliders.Select(s=>(float)s.Value));
        state.Load(new(false,false,-10,12.25f,200,42.75f,0),EnbRetailRenderBudgetSourceSettings.RetailExecutableDefaults);
        Assert.Same(values,state.Sliders);Assert.Equal(new float[]{0,12.25f,99,42.75f,0},values.Select(s=>(float)s.Value));
        state.BeginPreview();state.Sliders[2].SetValue(33.125f);
        EnbRetailGraphicsOptions? preview=null;state.Preview(v=>preview=v);
        Assert.Equal(33.125f,preview!.LandDetail);Assert.False(preview.UseRecommendedValues);
        var slider=state.Sliders[2];
        slider.Begin(EnbScrollbarPart.Thumb,new(slider.ThumbStart,202),0);
        Assert.Equal(EnbScrollbarPart.Thumb,slider.Pressed);
        state.Retire(null,null);Assert.Equal(EnbScrollbarPart.None,slider.Pressed);
    }

    [Fact]
    public void AllFiveSlidersClampAndRoundTripRetailEndpoints()
    {
        var state=Create();
        foreach(float value in new[]{-1f,0f,49.5f,99f,100f})
        {
            state.Load(new(false,false,value,value,value,value,value),EnbRetailRenderBudgetSourceSettings.RetailExecutableDefaults);
            float expected=Math.Clamp(value,0,99);
            Assert.All(state.Sliders,s=>{Assert.Equal(0,s.Minimum);Assert.Equal(99,s.Maximum);Assert.Equal(expected,s.Value);});
            state.BeginPreview();EnbRetailGraphicsOptions? saved=null;
            Assert.True(state.TryApply(v=>saved=v,null,null));
            Assert.Equal(new(false,expected,expected,expected,expected,expected),saved);
            state.Load(new(false,saved!.UseRecommendedValues,saved.Gamma,saved.LandPoly,saved.LandDetail,saved.SpacePoly,saved.TextureSize),EnbRetailRenderBudgetSourceSettings.RetailExecutableDefaults);
            Assert.All(state.Sliders,s=>Assert.Equal(expected,s.Value));
        }
    }

    [Fact]
    public void FailedApplyRetainsDraftAndRetryCommitsInOrder()
    {
        var state=Create();state.Load(new(false),EnbRetailRenderBudgetSourceSettings.RetailExecutableDefaults);state.BeginPreview();
        state.Sliders[0].SetValue(34.125f);var expected=state.Options();var calls=new List<string>();
        Assert.False(state.TryApply(_=>throw new IOException("fixture"),_=>calls.Add("preview"),()=>calls.Add("display")));
        Assert.True(state.PreviewActive);Assert.Equal(expected,state.Options());Assert.Empty(calls);Assert.Contains("fixture",state.Error);
        Assert.True(state.TryApply(v=>{Assert.Equal(expected,v);calls.Add("save");},v=>{Assert.Null(v);calls.Add("preview");},()=>calls.Add("display")));
        Assert.Equal(new[]{"save","preview","display"},calls);Assert.False(state.PreviewActive);Assert.Null(state.Error);
        state.Retire(_=>throw new Exception("already committed"),()=>throw new Exception("already committed"));
        Assert.False(state.TryApply(_=>throw new Exception("retired"),null,null));
    }

    [Fact]
    public void RetirementRestoresDisplayEvenWhenPreviewRemovalFails()
    {
        var state=Create();state.Load(new(false),EnbRetailRenderBudgetSourceSettings.RetailExecutableDefaults);state.BeginPreview();
        state.Preview(null);Assert.Contains("unavailable",state.Error);Assert.True(state.PreviewActive);
        state.Preview(_=>{});Assert.Null(state.Error);
        int cancelled=0;
        Assert.Throws<IOException>(()=>state.Retire(_=>throw new IOException("fixture"),()=>cancelled++));
        Assert.False(state.PreviewActive);Assert.Equal(1,cancelled);
        state.Retire(_=>throw new Exception("duplicate"),()=>cancelled++);Assert.Equal(1,cancelled);
        state.Preview(_=>throw new Exception("retired"));
    }

    [Fact]
    public void MonitorRefreshRetainsCustomCurrentSelectionWithoutApplyingIt()
    {
        var state=Create();
        var old=new EnbNext.Presentation.EnbDisplayChoice(3840,2160,EnbNext.Presentation.EnbDisplayMode.Windowed);
        var borderless=new EnbNext.Presentation.EnbDisplayChoice(2560,1440,EnbNext.Presentation.EnbDisplayMode.BorderlessFullscreen);
        var native=new EnbNext.Presentation.EnbDisplayChoice(2560,1440,EnbNext.Presentation.EnbDisplayMode.Windowed);
        state.OpenDisplay(null,null,()=>old,()=>new[]{old});state.BeginPreview();state.ResolutionDropdown=true;
        Assert.True(state.RefreshDisplay(()=>old,()=>new[]{old,borderless,native}));
        Assert.Equal(new[]{old,borderless,native},state.DisplayChoices);Assert.Equal(0,state.DisplayChoiceIndex);
        Assert.True(state.ResolutionDropdown);Assert.True(state.PreviewActive);
        Assert.Null(state.PreviewDisplay(2,c=>Assert.Equal(native,c)));Assert.Equal(2,state.DisplayChoiceIndex);
    }

    [Fact]
    public void DisplayCallbacksPreserveSelectionOnFailureAndOpeningOnlyFallback()
    {
        var state=Create();var calls=new List<string>();
        var a=new EnbNext.Presentation.EnbDisplayChoice(800,600,EnbNext.Presentation.EnbDisplayMode.Windowed);
        var b=new EnbNext.Presentation.EnbDisplayChoice(1920,1080,EnbNext.Presentation.EnbDisplayMode.BorderlessFullscreen);
        IReadOnlyList<EnbNext.Presentation.EnbDisplayChoice> choices=new[]{a,b};
        state.OpenDisplay(()=>calls.Add("begin"),()=>{calls.Add("scope");return EnbNext.Presentation.EnbDisplayScope.Character;},
            ()=>{calls.Add("current");return b;},()=>{calls.Add("choices");return choices;});
        Assert.Equal(new[]{"begin","scope","current","choices"},calls);
        Assert.Same(choices,state.DisplayChoices);Assert.Equal(1,state.DisplayChoiceIndex);
        state.ResolutionDropdown=true;
        Assert.Equal("Could not preview resolution: fixture",state.PreviewDisplay(0,_=>throw new IOException("fixture")));
        Assert.True(state.ResolutionDropdown);Assert.Equal(1,state.DisplayChoiceIndex);
        Assert.Null(state.PreviewDisplay(0,v=>Assert.Same(a,v)));
        Assert.False(state.ResolutionDropdown);Assert.Equal(0,state.DisplayChoiceIndex);
        Assert.False(state.RefreshDisplay(()=>throw new Exception("inactive"),null));
        state.BeginPreview();state.Preview(null);string? graphicsError=state.Error;
        state.ResolutionDropdown=true;
        Assert.Equal("Could not select display settings: fixture",state.SelectDisplayScope(EnbNext.Presentation.EnbDisplayScope.System,null,null,
            ()=>throw new InvalidOperationException("fixture"),null));
        Assert.Equal(EnbNext.Presentation.EnbDisplayScope.System,state.DisplayScope);
        Assert.Same(choices,state.DisplayChoices);Assert.True(state.ResolutionDropdown);Assert.Equal(graphicsError,state.Error);
        Assert.Null(state.SelectDisplayScope(EnbNext.Presentation.EnbDisplayScope.Character,null,null,()=>a,()=>[]));
        Assert.Empty(state.DisplayChoices);Assert.Equal(0,state.DisplayChoiceIndex);Assert.False(state.ResolutionDropdown);
        state.OpenDisplay(null,null,()=>b,()=>[]);
        Assert.Same(b,Assert.Single(state.DisplayChoices));Assert.Equal(EnbNext.Presentation.EnbDisplayScope.System,state.DisplayScope);
        Assert.True(state.RefreshDisplay(()=>b,()=>[]));Assert.Empty(state.DisplayChoices);
        Assert.False(state.RefreshDisplay(null,()=>throw new Exception("no current")));
        state.OpenDisplay(null,null,null,null);
        Assert.Equal(new(1280,720,EnbNext.Presentation.EnbDisplayMode.Windowed),Assert.Single(state.DisplayChoices));
    }
}
