using EnbNext.Assets;
using EnbNext.Net7.Protocol;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStationRemoteNameTests
{
    [Fact]
    public void GroupColorRequiresActiveNamedGroupButMatchesIdsInUnnamedSlots()
    {
        var normal=new EnbRgb(.33f,.33f,.99f);var member=new EnbRgb(.66f,0,.66f);
        var group=new EnbSceneGroupSnapshot(false,"",0,[new(0,"Other",1,0),new(1,"",42,0)],null,false,[]){AuxDataState=1};
        Assert.Equal(member,EnbStationNamePresentation.RemoteNameColor(group,42));
        Assert.Equal(normal,EnbStationNamePresentation.RemoteNameColor(group,2));
        Assert.Equal(normal,EnbStationNamePresentation.RemoteNameColor(group with {AuxDataState=0},42));
        Assert.Equal(normal,EnbStationNamePresentation.RemoteNameColor(group with {Members=[new(0,"",42,0)]},42));
        Assert.Equal(member,EnbStationNamePresentation.RemoteNameColor(group with {AuxDataState=-1},42));
        Assert.Equal(normal,EnbStationNamePresentation.RemoteNameColor(null,42));
        Assert.Equal(normal,EnbStationNamePresentation.RemoteNameColor(group with
        {Members=[new(0,"Other",1,0),new(1,"",2,0),new(2,"",3,0),new(3,"",4,0),new(4,"",5,0),new(5,"Extra",42,0)]},42));
    }
    [Theory]
    [InlineData("First","Last","First Last")]
    [InlineData("First","","First")]
    [InlineData("","Last"," Last")]
    [InlineData("First"," ","First  ")]
    public void FullNamePreservesAuthoredNameBytes(string first,string last,string expected)
    {
        var (avatar,_)=Net7AvatarRosterCodec.DecodeAvatarData(Net7PresentationWireTests.BuildAvatarData(""),"fixture");
        var snapshot=EnbPresentationSnapshotBuilder.BuildAvatar(avatar with {FirstName=first,LastName=last})!;
        Assert.Equal(expected,EnbStationNamePresentation.FullName(snapshot));
    }
}
