using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbSettingsNativeInputTests
{
    [InstalledRetailFact]
    public void NavigationCheckboxDependencyPreservesDraftAndCancelDoesNotSave()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var resources=EnbRenderSession.Of(assets);var state=new EnbSettingsState(assets);
        using var panel=new EnbSettingsPanel(resources.SliderPanels,resources.HorizontalSlider,resources.OptionsPanels,EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory),state);
        var queue=new EnbUiInputQueue();long now=0;
        void Click(int row)
        {
            var p=panel.Design(row==0?"settings/ROUTE":"settings/CHECK00").Center;float x=(float)((p.X+panel.Offset.X)/640),y=(float)((p.Y+panel.Offset.Y)/480);
            queue.SetPosition(x,y);queue.Push(new(1,0,x,y));panel.AdvanceInput(queue,new(1280,720),1,++now);
            queue.Push(new(1,0x1000,x,y));panel.AdvanceInput(queue,new(1280,720),1,++now);
        }
        state.Begin(123,7,1280,720,true,EnbSettingsState.NavigationSection);panel.Open(EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fa_opt04a-interface.ini")));
        Assert.False(state.RoutePlanner);Click(1);Assert.False(state.Check);
        Click(0);Assert.True(state.RoutePlanner);Click(1);Assert.True(state.Check);
        Click(0);Assert.False(state.RoutePlanner);Click(1);Assert.True(state.Check);
        state.Retire();state.Begin(123,7,1280,720,true,EnbSettingsState.NavigationSection);Assert.False(state.RoutePlanner);Assert.False(state.Check);state.Retire();
    }
    [InstalledRetailFact]
    public void NativeSettingsCaptureRepeatModifiersAndMovableBounds()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var resources=EnbRenderSession.Of(assets);var state=new EnbSettingsState(assets);
        using var panel=new EnbSettingsPanel(resources.SliderPanels,resources.HorizontalSlider,resources.OptionsPanels,EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory),state);
        var queue=new EnbUiInputQueue();var size=new EnbUiSize(1920,1080);
        void Open(int page)
        {
            state.Begin(123,page,1920,1080,true);
            panel.Open(EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,page==8?"fa_opt04a-sound.ini":"fc_option_font.ini")));
            queue.Clear();
        }
        string? Step(EnbUiPoint point,long now,ushort code=0,ushort modifiers=0)
        {
            float x=(float)(point.X/640),y=(float)(point.Y/480);queue.SetPosition(x,y);
            if(code!=0)queue.Push(new(code,modifiers,x,y));
            return panel.AdvanceInput(queue,size,1,now);
        }
        try
        {
            Open(8);
            for(int i=0;i<100;i++)Step(new(0,0),i);
            long revision=assets.SettingsRevision,beforeBytes=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<4096;i++)Step(new(0,0),i);
            Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-beforeBytes);Assert.Equal(revision,assets.SettingsRevision);
            state.Change(0,50);var arrow=panel.Design("settings/left0").Center;
            Step(arrow,0,1);Assert.Equal(5,panel.Pointer.LeftCapture);Assert.Equal(49,state.Sliders[0].Value);
            Step(arrow,499);Assert.Equal(49,state.Sliders[0].Value);
            Step(arrow,500);Assert.Equal(48,state.Sliders[0].Value);
            Step(new(0,0),600);Assert.Equal(48,state.Sliders[0].Value);
            Step(arrow,700);Assert.Equal(47,state.Sliders[0].Value);
            Step(new(0,0),701,1,0x1000);Assert.Equal(-1,panel.Pointer.LeftCapture);
            var check=panel.Design("settings/CHECK00").Center;bool before=state.Check;
            Step(check,800,1,0x200);Assert.NotEqual(before,state.Check);Step(check,801,1,0x1200);
            var ok=panel.Design("settings/OK").Center;
            Assert.Null(Step(ok,900,1,0x200));Step(ok,901,1,0x1200);
            Assert.Equal("settings/OK",Step(ok,1000,2));Step(ok,1001,2,0x1000);
            Open(9);var frame=panel.Design("settings/FRAME");var origin=new EnbUiPoint(frame.X+5,frame.Y+35);
            Step(origin,2000,1);Assert.Equal("settings/FRAME",EnbSettingsPanel.InputKey(panel.Pointer.LeftCapture));
            Step(new(origin.X+20.2,origin.Y+10.2),2001);Assert.Equal(new EnbUiPoint(20,10),panel.Offset);
            Step(new(640,480),2002);
            Assert.Equal(320-resources.SliderPanels.BackgroundExtents.X,panel.Offset.X);
            Assert.Equal(240-resources.SliderPanels.BackgroundExtents.Y,panel.Offset.Y);
            Step(new(640,480),2003,1,0x1000);Assert.Equal(-1,panel.Pointer.LeftCapture);
            Assert.Equal("settings/EXIT",Step(origin,2004,0x1b));
            state.Retire();Step(origin,2005);Assert.Equal(-1,panel.Pointer.LeftCapture);Assert.Empty(panel.DrawFrame(size,1,2005));
        }
        finally{state.Retire();}
    }
}
