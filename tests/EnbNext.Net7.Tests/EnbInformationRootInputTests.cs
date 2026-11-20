using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbInformationRootInputTests
{
    [Theory]
    [InlineData("help")]
    [InlineData("complaint")]
    public void RootUsesModelHitsCaptureWithoutSelectionAndOwnerMovement(string prefix)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var session=EnbRenderSession.Of(assets);
        using var ui=new EnbInformationUi(session.Information,session.OptionsPanels,EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory));
        var panel=ui.Load(prefix);var context=new EnbUiGadgetContext();context.Add(panel.Pointer);
        var input=new EnbUiInputQueue();
        string? Frame(ushort code,ushort modifiers,float x,float y)
        {
            input.Push(new(code,modifiers,x,y));var e=input.TakeFrame();panel.PrepareInput(e);
            var frame=new EnbUiPointerFrame(e.Code,e.Modifiers,input.LeftDown,input.RightDown);context.Dispatch(ref frame);
            return panel.CompleteInput(frame);
        }
        foreach(string key in panel.Resource.PointerOrder)
        {
            int index=Array.IndexOf(panel.Resource.PointerOrder,key);var r=panel.Design(key);
            float x=(float)((r.X+r.Width/2)/640),y=(float)((r.Y+r.Height/2)/480);
            Assert.True(panel.Contains(x,y));
            Assert.Equal(key,Frame(1,0,x,y));Assert.Equal(index,panel.Pointer.LeftCapture);
            Assert.False(panel.Pointer.Selected(index));Assert.False(panel.Pointer.Flash(index));
            Assert.Null(Frame(1,0x1000,-1,-1));Assert.Equal(-1,panel.Pointer.LeftCapture);
            Assert.Null(Frame(1,0x200,x,y));Assert.Equal(index,panel.Pointer.LeftCapture);
            Assert.Null(Frame(1,0x1200,x,y));Assert.Equal(-1,panel.Pointer.LeftCapture);
            Assert.Equal(key,Frame(2,0,x,y));Assert.Equal(index,panel.Pointer.RightCapture);
            Assert.Null(Frame(2,0x1000,-1,-1));Assert.Equal(-1,panel.Pointer.RightCapture);
        }
        Assert.Equal(prefix+"/EXIT2",Frame(0x1b,0,0,0));
        // Find an authored model hit outside all command meshes; drag is owned by the root.
        bool dragged=false;
        for(int y=20;y<460&&!dragged;y+=5)for(int x=20;x<620&&!dragged;x+=5)
        {
            panel.CancelInput();input.Clear();ui.Offset=default;
            Frame(1,0,x/640f,y/480f);
            if(panel.Pointer.LeftCapture!=panel.Resource.PointerOrder.Length)continue;
            Frame(0,0,(x+7)/640f,(y+9)/480f);
            Assert.Equal(new EnbUiPoint(7,9),ui.Offset);dragged=true;
            panel.CancelInput();Assert.Equal(-1,panel.Pointer.LeftCapture);Assert.Equal(new EnbUiPoint(7,9),ui.Offset);
        }
        Assert.True(dragged);
        context.Remove(panel.Pointer);Assert.Equal(0,context.Count);
    }
}
