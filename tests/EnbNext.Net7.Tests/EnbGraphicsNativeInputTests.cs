using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbGraphicsNativeInputTests
{
    [InstalledRetailFact]
    public void ActualGraphicsOwnersHandleCaptureDisabledRowsModifiersAndRetirement()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);var resources=EnbRenderSession.Of(assets);
        var state=new EnbGraphicsSettingsState(resources.SliderPanels);state.Load(new(false,false),EnbRetailRenderBudgetSourceSettings.RetailExecutableDefaults);state.BeginPreview();
        using var panel=new EnbGraphicsPanel(resources.SliderPanels,resources.HorizontalSlider,resources.OptionsPanels,EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory),state,
            EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fa_opt04a-graphics.ini")));
        panel.OpenInput();var queue=new EnbUiInputQueue();var size=new EnbUiSize(1920,1080);
        string? Step(EnbUiPoint p,long now,ushort code=0,ushort modifiers=0,bool dropdown=false)
        {float x=(float)(p.X/640),y=(float)(p.Y/480);queue.SetPosition(x,y);if(code!=0)queue.Push(new(code,modifiers,x,y));return panel.AdvanceInput(queue,size,1,now,dropdown,3);}
        for(int i=0;i<100;i++)Step(new(0,0),i);
        long bytes=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<4096;i++)Step(new(0,0),i);
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-bytes);
        var arrow=panel.Design("graphics/right0").Center;state.Sliders[0].SetValue(50.5);
        Assert.Equal("graphics/TRACK00",Step(arrow,5000,2));Assert.Equal(19,panel.Pointer.RightCapture);Assert.Equal(51.5,state.Sliders[0].Value);
        Step(arrow,5499);Assert.Equal(51.5,state.Sliders[0].Value);Step(arrow,5500);Assert.Equal(52.5,state.Sliders[0].Value);
        Step(new(0,0),5600);Assert.Equal(52.5,state.Sliders[0].Value);Step(new(0,0),5601,2,0x1000);Assert.Equal(-1,panel.Pointer.RightCapture);
        var slider=state.Sliders[1];slider.SetValue(50.5);var grab=new EnbUiPoint(slider.ThumbStart+3.2,186);
        Step(grab,6000,1);Step(new(grab.X+30.5,grab.Y),6001);Assert.InRange(slider.Value,75.249,75.251);
        Step(new(grab.X,0),6002);Assert.Equal(50.5,slider.Value);Step(grab,6003,1,0x1000);
        var check=panel.Design("graphics/CHECK00").Center;
        Assert.Null(Step(check,7000,1,0x200));Assert.True(state.Recommended);Step(check,7001,1,0x1200);
        Assert.True(panel.SlidersEnabled); // modified toggle did not admit005CF580
        Step(check,7010,1);Assert.False(state.Recommended);Step(check,7011,1,0x1000);
        Step(check,7020,1);Assert.True(state.Recommended);Assert.False(panel.SlidersEnabled);Step(check,7021,1,0x1000);
        double before=state.Sliders[0].Value;Step(arrow,7030,1);Assert.Equal(-1,panel.Pointer.LeftCapture);Assert.Equal(before,state.Sliders[0].Value);Step(arrow,7031,1,0x1000);
        Step(arrow,7032,0x102);Assert.Equal(before,state.Sliders[0].Value);
        var draws=panel.DrawFrame(size,1,null,[],0,false,EnbDisplayScope.System,"Pilot");
        Assert.DoesNotContain(draws,d=>d.GadgetOwner is EnbGraphicsPanel.Gadget g&&g.Visual is not null);
        Assert.Equal(5,draws.SelectMany(d=>d.Images).Count(t=>t.Opacity==BitConverter.Int32BitsToSingle(0x3ea8f5c3)));
        Step(check,7100,1);Assert.False(state.Recommended);Assert.True(panel.SlidersEnabled);Step(check,7101,1,0x1000);
        Assert.Equal("graphics/scopeCharacter",Step(panel.Design("graphics/scopeCharacter").Center,7200,1));Step(new(0,0),7201,1,0x1000);
        Assert.Equal("graphics/choice01",Step(panel.Design("graphics/choice01").Center,7300,1,dropdown:true));Step(new(0,0),7301,1,0x1000);
        Step(arrow,7400,1);panel.CancelInput();Assert.Equal(-1,panel.Pointer.LeftCapture);Assert.Equal(EnbScrollbarPart.None,state.Sliders[0].Pressed);
        // UI scale, textures and enhanced options live on Advanced Graphics; retail Graphics has none of them.
        state.Advanced=true;panel.OpenInput();
        // The Enhanced Graphics master takes the authored Use recommended values position.
        Assert.Equal("graphics/ENHANCED",Step(check,7442,1));Step(new(0,0),7443,1,0x1000);
        state.LoadBackgroundCap(true,60);Assert.Equal("graphics/TRACK04",Step(panel.Design("graphics/right4").Center,7444,1));Step(new(0,0),7445,1,0x1000);
        Assert.Equal(75,state.BackgroundCap);
        Assert.False(panel.Design("graphics/UISCALE").Intersects(panel.Design("graphics/OK")));
        Assert.Equal("graphics/UISCALE",Step(panel.Design("graphics/UISCALE").Center,7450,1));
        Step(new(0,0),7451,1,0x1000);
        state.UiScaleDropdown=true;
        panel.DrawFrame(size,1,null,[],0,false,EnbDisplayScope.System,"Pilot");
        for(int i=0;i<state.UiScales.Length;i++)
        {
            var p=panel.Design($"graphics/choice{i:00}").Center;
            queue.Push(new(1,0,(float)(p.X/640),(float)(p.Y/480)));
            Assert.Equal($"graphics/choice{i:00}",panel.AdvanceInput(queue,size,1,7460+i*2,true,state.UiScales.Length));
            queue.Push(new(1,0x1000,0,0));panel.AdvanceInput(queue,size,1,7461+i*2,true,state.UiScales.Length);
        }
        state.UiScaleDropdown=false;
        var overrideCheck=panel.Design("graphics/TEXTUREOVERRIDES");
        Assert.False(overrideCheck.Intersects(panel.Design("graphics/UISCALE")));
        Assert.False(overrideCheck.Intersects(panel.Design("graphics/OK")));
        Assert.False(overrideCheck.Intersects(panel.Design("graphics/CANCEL")));
        Assert.Equal("graphics/TEXTUREOVERRIDES",Step(overrideCheck.Center,7490,1));
        Step(new(0,0),7491,1,0x1000);
        Assert.Null(Step(new(overrideCheck.Right+20,overrideCheck.Center.Y),7492,1));
        Step(new(0,0),7493,1,0x1000);
        state.TextureDropdown=true;state.TexturePacks=["","Bicubic 4x","RealESRGAN 4x"];
        panel.DrawFrame(size,1,null,[],0,false,EnbDisplayScope.System,"Pilot");
        for(int i=0;i<state.TexturePacks.Length;i++)
        {
            var p=panel.Design($"graphics/choice{i:00}").Center;
            Assert.Equal($"graphics/choice{i:00}",Step(p,7494+i*2,1,dropdown:true));
            Step(new(0,0),7495+i*2,1,0x1000,dropdown:true);
        }
        var expandedFrame=panel.Design("graphics/FRAME");
        Assert.True(panel.Contains((float)(expandedFrame.Center.X/640),(float)((expandedFrame.Bottom-1)/480),false,3));
        state.TextureDropdown=false;
        var bloom=panel.Design("graphics/BLOOM").Center;
        Assert.Null(Step(bloom,7499,1));Step(new(0,0),7500,1,0x1000);
        Assert.Equal("graphics/ENHANCED",Step(panel.Design("graphics/ENHANCED").Center,7501,1));Step(new(0,0),7502,1,0x1000);
        state.Enhanced=new(true,true);
        Assert.Equal("graphics/BLOOM",Step(bloom,7503,1));Step(new(0,0),7504,1,0x1000);
        state.Enhanced=new(false,true);
        Assert.Null(Step(bloom,7505,1));Step(new(0,0),7506,1,0x1000);Assert.True(state.Enhanced.Bloom);
        var hdr=panel.Design("graphics/HDR").Center;
        Assert.Null(Step(hdr,7507,1));Step(new(0,0),7508,1,0x1000);
        state.Enhanced=new(true,true,true);
        Assert.Equal("graphics/HDR",Step(hdr,7509,1));Step(new(0,0),7510,1,0x1000);
        state.Enhanced=new(false,true,true);Assert.True(state.Enhanced.Hdr);Assert.False(state.Enhanced.HdrActive);
        Assert.True(panel.Design("graphics/FRAME").Bottom>panel.Design("graphics/HDR").Bottom);
        var supersampling=panel.Design("graphics/SUPERSAMPLING").Center;
        Assert.Null(Step(supersampling,7511,1));Step(new(0,0),7512,1,0x1000);
        state.Enhanced=new(true,false,false,true);
        Assert.Equal("graphics/SUPERSAMPLING",Step(supersampling,7513,1));Step(new(0,0),7514,1,0x1000);
        Assert.True(panel.Design("graphics/FRAME").Bottom>panel.Design("graphics/SUPERSAMPLING").Bottom);
        var anisotropic=panel.Design("graphics/ANISOTROPIC").Center;
        state.Enhanced=new(false,false,false,true,true);
        Assert.Null(Step(anisotropic,7515,1));Step(new(0,0),7516,1,0x1000);
        state.Enhanced=state.Enhanced with {Enabled=true};
        Assert.Equal("graphics/ANISOTROPIC",Step(anisotropic,7517,1));Step(new(0,0),7518,1,0x1000);
        Assert.True(panel.Design("graphics/FRAME").Bottom>panel.Design("graphics/ANISOTROPIC").Bottom);
        state.TextureDropdown=false;
        foreach(var key in new[]{"graphics/OK","graphics/CANCEL"})
        {
            Assert.Equal(key,Step(panel.Design(key).Center,7520,1));Step(new(0,0),7521,1,0x1000);
        }
        state.Advanced=false;panel.OpenInput();
        Assert.True(panel.Design("graphics/OK").Bottom<=panel.Design("graphics/FRAME").Bottom);
        foreach(var key in new[]{"graphics/OK","graphics/CANCEL","graphics/RESOLUTION"})
        {
            Assert.Equal(key,Step(panel.Design(key).Center,7523,1));Step(new(0,0),7524,1,0x1000);
        }
        state.Retire(null,null);Assert.Null(Step(arrow,7522,1));Assert.False(panel.Contains(.5f,.5f,true,3));
    }

    [InstalledRetailFact]
    public void SharedDispatcherCompletesWithoutSubscriberAndPreservesQueuedRelease()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);var resources=EnbRenderSession.Of(assets);
        var state=new EnbGraphicsSettingsState(resources.SliderPanels);state.Load(new(false,false),EnbRetailRenderBudgetSourceSettings.RetailExecutableDefaults);state.BeginPreview();
        using var panel=new EnbGraphicsPanel(resources.SliderPanels,resources.HorizontalSlider,resources.OptionsPanels,EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory),state,
            EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fa_opt04a-graphics.ini")));
        var collection=new EnbOptionsGadgetCollection();var camera=new EnbNext.Client.EnbCameraOptionsState();
        panel.OpenInput();collection.Synchronize(null,panel.Pointer,null,null,null,true,0,0);
        void Advance(long now)=>collection.Advance(6,new(1920,1080),1,1920,1080,now,null,null,null,panel,null,camera);
        var point=panel.Design("graphics/right0").Center;float x=(float)(point.X/640),y=(float)(point.Y/480);
        state.Sliders[0].SetValue(50);collection.Input.Push(new(2,0,x,y));collection.Input.Push(new(2,0x1000,0,0));
        Advance(1000);Assert.Equal(51,state.Sliders[0].Value);Assert.True(collection.HasCapture);Assert.Equal(1,collection.Input.Count);
        Advance(1001);Assert.False(collection.HasCapture);Assert.Equal(0,collection.Input.Count);
        for(int i=0;i<100;i++)Advance(2000+i);
        long bytes=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<4096;i++)Advance(3000+i);
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-bytes);
        var commands=new List<EnbOptionsInputCommand>();collection.Command+=commands.Add;
        collection.Input.Push(new(2,0,x,y));Advance(8000);
        Assert.Equal(EnbOptionsInputCommandKind.Graphics,Assert.Single(commands).Kind);Assert.Equal("graphics/TRACK00",commands[0].Key);
        Assert.Equal(52,state.Sliders[0].Value);
        collection.Synchronize(null,null,null,null,null,false,0,0);Assert.False(collection.HasCapture);Assert.Equal(0,collection.Context.Count);
    }
}
