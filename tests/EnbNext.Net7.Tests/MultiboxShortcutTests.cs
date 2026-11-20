using System.Text.Json;
using EnbNext.App;
using EnbNext.Client;
using EnbNext.Presentation;
namespace EnbNext.Net7.Tests;
public sealed class MultiboxShortcutTests
{
    [Fact]
    public void BothBanksHaveSixDistinctRoundTrippingAddresses()
    {
        var addresses=Enumerable.Range(0,12).Select(EnbRemoteShortcut.Address).ToArray();
        Assert.Equal(12,addresses.Distinct().Count());
        for(int bank=0;bank<2;bank++)for(int key=0;key<6;key++)
        {
            var address=addresses[bank*6+key];
            Assert.Equal(bank,address.Group);Assert.Equal(key/3,address.Bar);Assert.Equal(key%3,address.Button);
            Assert.Equal(bank*6+key,EnbRemoteShortcut.Index(address));
        }
        Assert.Equal(3,JsonSerializer.Deserialize<EnbMultiboxOptions>("{}")!.ShortcutButtons);
        foreach(int count in new[]{0,1,3,12})
        {
            var options=new EnbMultiboxOptions(ShortcutButtons:count);
            Assert.Equal(options,JsonSerializer.Deserialize<EnbMultiboxOptions>(JsonSerializer.Serialize(options)));
        }
    }
    [Fact]
    public async Task ShortcutPresentationSurvivesFramingWithAllTwelveSlots()
    {
        var slots=Enumerable.Range(0,12).Select(i=>new EnbRemoteShortcut(i,"Skill"+i,"Tooltip\n"+i,"icon.tga",.2f,.4f,.6f,i%2==0,i%3==0,i%4==0){RangeFrame=i,RangeAngle=i*.1f}).ToArray();
        var state=new MultiboxState(Guid.NewGuid(),"server",Guid.NewGuid(),1,"Pilot",8,null,false,false,[],"Sector",null,false){Shortcuts=slots};
        var message=new MultiboxMessage(MultiboxPeer.Version,"state",state.Instance,Guid.Empty,"",state);
        using var stream=new MemoryStream();await MultiboxPeer.WriteAsync(stream,message,default);stream.Position=0;
        var received=await MultiboxPeer.ReadAsync(stream,default);
        Assert.Equal(slots,received.State!.Shortcuts);
    }
    [Fact]
    public void ExplicitShortcutRequiresParticipationLeaderContextAndBoundedSlot()
    {
        var source=new EnbMultiboxContext(1,8,8,12,4,3);
        var recipient=new EnbMultiboxContext(2,7,7,21,4,8);
        var leader=new MultiboxState(Guid.NewGuid(),"test",Guid.NewGuid(),1,"Pilot",8,null,false,true,[7],"Sector",null,false){Context=source};
        var input=new EnbMultiboxInput(Guid.NewGuid(),"shortcut",11,source);
        var command=new MultiboxMessage(MultiboxPeer.Version,"command",leader.Instance,input.Id,"shortcut",null,input,recipient);
        var options=new EnbMultiboxOptions(Enabled:true);
        Assert.True(command.Allows(leader,recipient,"test",options));
        Assert.False(command.Allows(leader,recipient,"test",options with {Enabled=false}));
        Assert.False(command.Allows(leader with {Leader=false},recipient,"test",options));
        Assert.False(command.Allows(leader with {GroupMembers=[]},recipient,"test",options));
        Assert.False(command.Allows(leader,recipient with {Transition=9},"test",options));
        Assert.False(command.Allows(leader,recipient with {Sector=5},"test",options));
        Assert.False(command.Allows(leader,recipient,"another",options));
        Assert.False((command with {Command=input with {Target=12}}).Allows(leader,recipient,"test",options));
        Assert.False((command with {Command=input with {Target=uint.MaxValue}}).Allows(leader,recipient,"test",options));
    }
    [Fact]
    public void FeedbackRejectsOlderDiscoveryAndForeignContextsWithoutChangingAuthority()
    {
        var context=new EnbMultiboxContext(1,8,8,12,4,3);
        var known=new MultiboxState(Guid.NewGuid(),"test",Guid.NewGuid(),1,"Pilot",8,null,false,false,[7],"Sector",null,false){Context=context,ShortcutRevision=4};
        var slot=new EnbRemoteShortcut(0,"Buff","Buff","icon",1,1,1,true,true,false){RangeFrame=15};
        var newer=known with {ShortcutRevision=5,Shortcuts=[slot],Leader=true,GroupMembers=[]};
        var merged=MultiboxPeer.MergeShortcutFeedback(known,newer);
        Assert.Equal(slot,Assert.Single(merged.Shortcuts));Assert.False(merged.Leader);Assert.Equal(known.GroupMembers,merged.GroupMembers);
        Assert.Same(merged,MultiboxPeer.MergeShortcutFeedback(merged,known));
        Assert.Same(known,MultiboxPeer.MergeShortcutFeedback(known,newer with {Context=context with {Transition=9}}));
        Assert.Same(known,MultiboxPeer.MergeShortcutFeedback(known,newer with {Instance=Guid.NewGuid()}));
        Assert.Same(known,MultiboxPeer.MergeShortcutFeedback(known,newer with {Shortcuts=[slot with {Slot=12}]}));
    }
}
