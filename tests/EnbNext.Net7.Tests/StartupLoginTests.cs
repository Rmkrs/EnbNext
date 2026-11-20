using EnbNext.App;
using EnbNext.Client.Models;

namespace EnbNext.Net7.Tests;

public sealed class StartupLoginTests
{
    [Fact] public void NoArgumentsUsesInteractiveStartup()=>Assert.Null(StartupLogin.Parse([]));
    [Theory]
    [InlineData("--username", "user")]
    [InlineData("--password")]
    [InlineData("--unknown", "secret")]
    public void IncompleteOrUnknownArgumentsFailWithoutEchoingValues(params string[] args)
    {
        var value=StartupLogin.Parse(args)!;
        Assert.NotNull(value.Error);
        Assert.DoesNotContain("secret",value.Error);

    }
    [Fact] public void UsesSavedCredentialsAndMatchesFullCharacterName()
    {
        var value=StartupLogin.Parse(["--character"," FIRST LAST ","--username"," user "])!;
        Assert.Null(value.Error); Assert.Equal("user",value.Username);
        var character=new CharacterSummary(0,1,1,"First","Last",0,0,0,1,1,1,1,"Station",1,0,0,0);
        Assert.Same(character,value.Resolve([character]));
        Assert.Null(value.Resolve([character with { AvatarId=0 }]));
        Assert.Null(value.Resolve([character,character with { Slot=1 }]));
        Assert.Null(value.Resolve([character with { LastName="Other" }]));
        Assert.NotNull(StartupLogin.Parse(["--username","u","--password","secret","--character","c"])!.Error);
    }
    [Fact] public void DuplicateAndEmptyParametersAreRejected()
    {
        Assert.NotNull(StartupLogin.Parse(["--username","u","--password","p","--character","c","--username","u"])!.Error);
        Assert.NotNull(StartupLogin.Parse(["--username","u","--password","","--character","c"])!.Error);
    }
}
