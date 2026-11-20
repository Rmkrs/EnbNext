using EnbNext.Engine;
namespace EnbNext.Net7.Tests;

public sealed class EnbChatUiInputTests
{
    [Fact]
    public async Task PendingPrivateRequestReportsTheRetailMessageAndSendsNothing()
    {
        // 005EA370 / 005EA7F0 / 005EB2F0: with +0x225 set, local type 0x13 with
        // the fixed text and return before any request.
        using var chat=new EnbChatUi(()=>7);
        var messages=new List<(uint,byte,string)>();int requests=0;
        chat.LocalChatMessageRequested=(c,t,m)=>messages.Add((c,t,m));
        chat.PrivateChannelRequested=(_,_,_)=>{requests++;return Task.FromResult(true);};
        const System.Reflection.BindingFlags any=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(EnbChatUi).GetField("_privateRequestPending",any)!.SetValue(chat,true);
        await (Task)typeof(EnbChatUi).GetMethod("CreatePrivateChannelAsync",any)!.Invoke(chat,null)!;
        await (Task)typeof(EnbChatUi).GetMethod("SubmitPrivateControlAsync",any)!.Invoke(chat,[EnbNext.Net7.Social.Net7ClientChatRequestKind.ExitChannel,"x",null])!;
        await (Task)typeof(EnbChatUi).GetMethod("ConfirmPrivateJoinAsync",any)!.Invoke(chat,null)!;
        Assert.Equal(0,requests);
        Assert.Equal([(7u,(byte)19,"Another request is pending. Cannot create a new channel."),
            (7u,(byte)19,"Another request is pending. Cannot leave the channel."),
            (7u,(byte)19,"Another request is pending. Cannot join a new channel. ")],messages);
    }

    [Fact]
    public void EngineChatFocusAndRetirementDoNotRequireNativeControls()
    {
        using var chat=new EnbChatUi(()=>null);
        var main=chat.MainInput;var verify=chat.VerifyInput;
        Assert.False(main.Focus());
        main.IsVisible=true;Assert.True(main.Focus());Assert.True(main.IsFocused);
        main.Text="retained draft";
        verify.IsVisible=true;Assert.True(verify.Focus());
        Assert.False(main.IsFocused);Assert.False(main.IsVisible);Assert.Equal("",main.Text);
        Assert.True(verify.IsFocused);
        verify.IsVisible=false;Assert.False(verify.IsFocused);Assert.False(verify.Focus());
        main.IsVisible=true;Assert.True(main.Focus());main.Text="cancelled draft";
        chat.CloseInput();Assert.False(main.IsFocused);Assert.False(main.IsVisible);Assert.Equal("",main.Text);
    }
}
