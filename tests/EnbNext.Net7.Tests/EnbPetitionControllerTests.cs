using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

public sealed class EnbPetitionControllerTests
{
    [Fact]
    public async Task PendingSendIsExclusiveAndRetiredCompletionCannotMutateTheNextForm()
    {
        var form=new EnbPetitionController();uint? session=123;int starts=0,calls=0;
        form.SendStarted+=()=>{Assert.True(form.Sending);starts++;};
        var completions=new List<EnbPetitionSendResult>();form.Completed+=completions.Add;
        var old=new TaskCompletionSource<bool>();var next=new TaskCompletionSource<bool>();
        form.Open(3);
        var first=form.SendAsync(123,7,"subject","body","players",()=>session,(owner,handoff,category,subject,body,players)=>
        {calls++;Assert.Equal((123u,7ul,3u,"subject","body","players"),(owner,handoff,category,subject,body,players));return old.Task;});
        Assert.True(form.Sending);
        Assert.Equal(EnbPetitionSendResult.Ignored,await form.SendAsync(123,7,"s","b","p",()=>session,(_,_,_,_,_,_)=>{calls++;return Task.FromResult(true);}));
        Assert.Equal(1,calls);form.Retire();session=124;form.Open(2);
        var second=form.SendAsync(124,8,"new subject","new body","",()=>session,(_,_,_,_,_,_)=>next.Task);
        old.SetResult(true);Assert.Equal(EnbPetitionSendResult.Ignored,await first);
        Assert.True(form.Sending);Assert.Equal(2u,form.Category);Assert.Empty(completions);
        next.SetResult(false);Assert.Equal(EnbPetitionSendResult.Failed,await second);
        Assert.False(form.Sending);Assert.Contains("kept",form.Error);Assert.Equal(2,starts);
        Assert.Equal(EnbPetitionSendResult.Sent,await form.SendAsync(124,8,"new subject","new body","",()=>session,(_,_,_,_,_,_)=>Task.FromResult(true)));
        Assert.Equal(new[]{EnbPetitionSendResult.Failed,EnbPetitionSendResult.Sent},completions);
        form.Back();Assert.Equal(0u,form.Category);
    }

    [Fact]
    public async Task ExpectedTransportFailuresAreContainedAndValidationKeepsSignedByteRules()
    {
        var form=new EnbPetitionController();form.Open(1);
        foreach(var error in new Exception[]{new IOException("io"),new InvalidOperationException("state"),new OperationCanceledException("cancel"),new ArgumentException("argument")})
        {
            Assert.Equal(EnbPetitionSendResult.Failed,await form.SendAsync(1,0,"subject","body","",()=>1,(_,_,_,_,_,_)=>Task.FromException<bool>(error)));
            Assert.False(form.Sending);Assert.EndsWith(error.Message,form.Error);
        }
        Assert.False(EnbPetitionController.DataValid("abc ","valid"));
        Assert.True(EnbPetitionController.DataValid("a b c d","valid"));
        Assert.False(EnbPetitionController.TextValid("Ã©Ã©Ã©Ã©",4));
        Assert.True(EnbPetitionController.TextValid("abc",3));
    }
}
