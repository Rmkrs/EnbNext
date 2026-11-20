using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbInformationPetitionInputTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    public void CombinedEditorsAttachmentsModalAndRetirement(uint category)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var session=EnbRenderSession.Of(assets);var palette=EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory);
        using var ui=new EnbInformationUi(session.Information,session.OptionsPanels,palette);
        var controller=ui.Petition;controller.Open(category);
        var body=ui.Body;body.Text="body text";body.Entered=true;
        var entry=ui.Entry;var player=ui.Player;

        using var dialog=new EnbConfirmationPanel(assets,palette);var confirmation=new EnbConfirmationState();
        var input=ui.BindPetition(dialog,confirmation);
        var root=ui.Load(category==1?"help":"complaint");var context=new EnbUiGadgetContext();context.Add(root.Pointer);input.Attach(context);
        var queue=new EnbUiInputQueue();long now=0;
        void Draw()
        {
            var list=ImmutableList.CreateBuilder<EnbUiModelDraw>();int scroll=input.Editor.Scroll;bool follow=input.Editor.FollowCaret;
            input.Editor.Panel.Draw(list,new(640,480),1,input.Editor.Panel.Offset,category==1?11:12,category,1,null,null,1,null,body,entry,player,ref scroll,ref follow,true,now);
        }
        string? Step(ushort code,ushort modifiers,EnbUiPoint point,char character='\0')
        {
            queue.Push(new(code,modifiers,(float)(point.X/640),(float)(point.Y/480),character));var e=queue.TakeFrame();
            input.PrepareInput(e,new(640,480),1,now+=16);var frame=new EnbUiPointerFrame(e.Code,e.Modifiers,queue.LeftDown,queue.RightDown);
            context.Dispatch(ref frame);var command=input.CompleteInput(frame);Draw();return command;
        }
        Draw();Assert.Equal(category==1?5:7,context.Count);
        var panel=input.Editor.Panel;var point=panel.Design("petition/MESSAGE").Center;point=new(point.X+panel.Offset.X,point.Y+panel.Offset.Y);
        Assert.Null(Step(1,0,point));Step(1,0x1000,point);Assert.Same(body.Pointer,context.LeftCapture.Owner);
        int length=body.Text.Length;Step(65,0,point,'Z');Assert.Equal(length+1,body.Text.Length);Step(65,0x1000,point);
        var subject=panel.Design("petition/ENTRY_FIELD00").Center;subject=new(subject.X+panel.Offset.X,subject.Y+panel.Offset.Y);
        Assert.Equal("petition/ENTRY_FIELD00",Step(1,0,subject));entry.Activate();Step(1,0x1000,subject);
        Assert.True(entry.IsFocused);Assert.Null(context.LeftCapture.Owner);
        Step(1,0,point);Step(1,0x1000,point);Assert.Same(body.Pointer,context.LeftCapture.Owner);
        confirmation.Show("Cancel?",false,"PETITION_ENTRY_CANCEL");input.SynchronizeModal();
        Assert.Null(context.LeftCapture.Owner);Assert.False(body.Pointer.Enabled(0));Assert.True(body.IsFocused);Assert.False(entry.IsFocused);
        Step(27,0,default);Assert.Equal(0,input.Answer);Step(27,0x1000,default);
        Step(0x4e,0x400,default);Assert.Equal(2,input.Answer);confirmation.Clear();input.SynchronizeModal();Step(0x4e,0x1400,default);
        Assert.True(body.Pointer.Enabled(0));Assert.Null(context.LeftCapture.Owner);
        input.CancelInput();entry.Activate();Step(9,0,default);Assert.Same(body.Pointer,context.LeftCapture.Owner);Assert.False(entry.IsFocused);
        input.CancelInput();queue.Clear();
        var original=panel.Offset;panel.Offset=new(original.X+13,original.Y+11);Assert.Equal(new EnbUiPoint(original.X+13,original.Y+11),panel.Offset);
        ui.Offset=new(7,9);Assert.Equal(new EnbUiPoint(original.X+7,original.Y+9),panel.Offset);
        if(category==3)
        {
            var playerPanel=ui["player"];var before=playerPanel.Offset;playerPanel.Offset=new(before.X+5,before.Y+6);
            var pivot=playerPanel.Resource.Pivot("TRANS_BONE");Assert.Equal(new EnbUiPoint(playerPanel.Offset.X+pivot.X,playerPanel.Offset.Y-pivot.Y),panel.Offset);
            player.Activate();Step(65,0,default,'A');Step(65,0x1000,default);Step(65,0,default,'1');Assert.Equal("A",player.Text);
            Step(65,0x1000,default);player.Text="mIXEDCASEabcdefghijklmnop";Step(0,0,default);
            Assert.Equal("Mixedcaseabcdefg",player.Text);Assert.Equal(16,player.CaretIndex);
            Step(9,0,default);Assert.False(player.IsFocused);Assert.True(entry.IsFocused);Assert.Null(context.LeftCapture.Owner);
        }
        input.Detach();context.Remove(root.Pointer);Assert.Equal(0,context.Count);Assert.Null(context.LeftCapture.Owner);Assert.Null(context.RightCapture.Owner);
    }

    [Fact]
    public void DisabledHitRetainsTooltipButNotInputOrCapture()
    {
        var pointer=new EnbUiGadgetPointer(2);pointer.AcquireCapture(0,1);pointer.SetSelected(0,true);pointer.SetEnabled(0,false);
        Assert.Equal(-1,pointer.LeftCapture);Assert.True(pointer.Selected(0));
        var frame=new EnbUiPointerFrame(1,0,true,false);pointer.Dispatch(frame,3);
        Assert.True(pointer.Hit(0));Assert.False(pointer.FirstEnabled(0));Assert.True(pointer.FirstEnabled(1));Assert.Equal(1,pointer.LastHandler);Assert.Equal(0,pointer.TooltipGadget);
    }
}
