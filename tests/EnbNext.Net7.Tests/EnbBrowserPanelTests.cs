using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbBrowserPanelTests
{
    [Fact]
    public void BrowserCollectionOwnsCaptureCommandOrderAndRetirement()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);var resources=EnbRenderSession.Of(assets);
        using var panel=new EnbBrowserPanel(resources.BrowserPanel,resources.OptionsPanels);
        var collection=new EnbOptionsGadgetCollection();var camera=new EnbNext.Client.EnbCameraOptionsState();
        var commands=new List<string>();collection.Command+=c=>{if(c.Key is {} key)commands.Add(key);};
        var r=panel.Design("manual/EXIT_BUTTON");float x=(float)(r.Center.X/640),y=(float)(r.Center.Y/480);
        Assert.Equal(3u,panel.HitMask(x,y));
        collection.Synchronize(null,null,null,null,null,true,x,y,panel);Assert.Equal(2,collection.Context.Count);
        void Frame()=>collection.Advance(4,new(800,600),1,800,600,1000,null,null,null,null,null,camera);
        collection.Input.Push(new(1,0x100,x,y));Frame();Assert.Empty(commands);Assert.Equal(0,panel.Pointer.LeftCapture);Assert.True(panel.Pointer.Selected(0));
        collection.Input.Push(new(1,0x1100,0,0));Frame();Assert.Empty(commands);Assert.Equal(-1,panel.Pointer.LeftCapture);Assert.False(panel.Pointer.Selected(0));
        collection.Input.Push(new(1,0,x,y));Frame();Assert.Equal(new[]{"manual/EXIT_BUTTON"},commands);Assert.True(collection.HasCapture);
        collection.Synchronize(null,null,null,null,null,false,0,0);Assert.False(collection.HasCapture);Assert.Equal(0,collection.Context.Count);Assert.Equal(0,collection.Input.Count);
        collection.Synchronize(null,null,null,null,null,true,x,y,panel);Assert.False(panel.Pointer.Selected(0));
        collection.Input.Push(new(2,0x200,x,y));Frame();Assert.Equal(0,panel.Pointer.RightCapture);Assert.Single(commands);
        panel.CancelInput();Assert.False(collection.HasCapture);
        collection.Input.Clear();collection.Input.SetPosition(.5f,.5f);Frame();Assert.Equal(2u,panel.HitMask(.5,.5));
        collection.Input.Push(new(1,0,.5f,.5f));Frame();Assert.Single(commands);Assert.False(collection.HasCapture);
        for(int i=0;i<100;i++)Frame();long before=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<4096;i++)Frame();Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
        panel.Dispose();Assert.Equal(0,collection.Context.Count);Assert.False(collection.HasCapture);
    }

    [Fact]
    public void BrowserResourcesStayInAssetOwnerAndPanelInstancesRetireIndependently()
    {
        var paths=EnbInstalledAssetLocator.TryLocate()!;
        var assets=EnbRetailAssetBundle.Load(paths);var session=EnbRenderSession.Of(assets);var resources=session.BrowserPanel;
        Assert.Same(resources,EnbRenderSession.Of(assets).BrowserPanel);
        using var first=new EnbBrowserPanel(resources,session.OptionsPanels);using var second=new EnbBrowserPanel(resources,session.OptionsPanels);
        ImmutableList<EnbUiModelDraw> Draw(EnbBrowserPanel panel,string? hover=null,string? press=null,string? error=null)
        {var list=ImmutableList.CreateBuilder<EnbUiModelDraw>();panel.Draw(list,new(800,600),1,"Online Manual",hover,press,error);return list.ToImmutable();}
        var idle=Draw(first);var other=Draw(second);Assert.NotEmpty(idle);Assert.Equal(idle.Count,other.Count);
        for(int i=0;i<idle.Count;i++){Assert.Same(idle[i].Asset,other[i].Asset);Assert.NotSame(idle[i].GadgetOwner,other[i].GadgetOwner);}
        var hovered=Draw(first,"manual/EXIT_BUTTON");Assert.Single(hovered,d=>d.Hovered);
        var pressed=Draw(first,"manual/EXIT_BUTTON","manual/EXIT_BUTTON","Offline failure");Assert.DoesNotContain(pressed,d=>d.Hovered);Assert.Single(pressed,d=>d.Verb==1);
        for(int i=0;i<idle.Count;i++)Assert.Same(idle[i].GadgetOwner,pressed[i].GadgetOwner);
        Assert.Single(idle[^1].Images);Assert.Equal(2,pressed[^1].Images.Length);
        Assert.DoesNotContain(idle,d=>d.Asset.Resource.Meshes.Any(m=>m.Name=="BROWSER"));
        Assert.True(first.Design("manual/BROWSER").Width>0);
        first.Dispose();Assert.Empty(Draw(first));Assert.Equal(other.Count,Draw(second).Count);
        var replacement=EnbRetailAssetBundle.Load(paths);Assert.NotSame(resources,EnbRenderSession.Of(replacement).BrowserPanel);
    }
}
