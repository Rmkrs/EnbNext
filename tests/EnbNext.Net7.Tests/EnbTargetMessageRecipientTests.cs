using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbTargetMessageRecipientTests
{
    private static EnbSceneObjectSnapshot Target(byte type, string? owner) =>
        new EnbSceneObjectSnapshot(1,1,1,false,true,type,0,1,0,0,0,0,false,"Ship Name",new(false,0,null),null,null,null,[],[],[],[],[],[],null,[],null,0,0)
            { DisplayNameProperties = new("Ship Name", owner, "Title", "Rank") };

    [Fact]
    public void RecipientRequiresAPlayerOwnerAndRemovesItsBracketTag()
    {
        // 0065E840: an active player target with a non-empty Owner; "[tag] " is removed;
        // neither Name nor title/rank is used.
        Assert.Equal("Pilot", EnbRetailObjectDisplayName.TargetMessageRecipient(Target(1, "[GM] Pilot")));
        Assert.Equal("Pilot", EnbRetailObjectDisplayName.TargetMessageRecipient(Target(1, "Pilot\0junk")));
        Assert.Null(EnbRetailObjectDisplayName.TargetMessageRecipient(Target(0, "Pilot")));
        Assert.Null(EnbRetailObjectDisplayName.TargetMessageRecipient(Target(1, "")));
        Assert.Null(EnbRetailObjectDisplayName.TargetMessageRecipient(Target(1, null)));
        Assert.Null(EnbRetailObjectDisplayName.TargetMessageRecipient(null));
    }
}
