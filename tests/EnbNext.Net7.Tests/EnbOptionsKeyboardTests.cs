using EnbNext.Engine;
using EnbNext.Presentation;
namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbOptionsKeyboardTests
{
    [Fact]
    public void TranslatedTextRetainsPressIdentityAndUtf16OrderAcrossRepeatAndRelease()
    {
        var owner=new EnbOptionsGadgetCollection();
        Assert.True(owner.PrepareTextKey(0x41,0x100));
        owner.QueueText("Aé\U0001f680",.3f,.4f);
        Assert.True(owner.PrepareTextKey(0x41,0));
        owner.QueueText("a",.5f,.6f);
        owner.Input.Push(new(0x41,0x1000,.7f,.8f));
        foreach(char c in "Aé\U0001f680")Assert.Equal(new EnbUiInputEvent(0x41,0x100,.3f,.4f,c),owner.Input.TakeFrame());
        Assert.Equal(new EnbUiInputEvent(0x41,0x100,.5f,.6f,'a'),owner.Input.TakeFrame());
        Assert.Equal(new EnbUiInputEvent(0x41,0x1100,.7f,.8f),owner.Input.TakeFrame());
        owner.PrepareTextKey(0x41,0);owner.QueueText("a",0,0);
        Assert.Equal((ushort)0,owner.Input.TakeFrame().Modifiers);
    }
    [Fact]
    public void EditingCommandsRemainKeyEventsAndTextDoesNotChangePointerPosition()
    {
        var owner=new EnbOptionsGadgetCollection();owner.Input.SetPosition(.1f,.2f);
        foreach(ushort code in new ushort[]{8,9,13,27,33,34,35,36,37,38,39,40,45,46,16,17,18,256})
            Assert.False(owner.PrepareTextKey(code,0));
        Assert.True(owner.PrepareTextKey(0,0));owner.QueueText("é",.8f,.9f);
        Assert.Equal(new EnbUiInputEvent(0,0,.8f,.9f,'é'),owner.Input.TakeFrame());
        Assert.Equal(.1f,owner.Input.X);Assert.Equal(.2f,owner.Input.Y);
    }
}
