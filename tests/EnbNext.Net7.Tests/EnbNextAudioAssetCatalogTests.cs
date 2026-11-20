using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbNextAudioAssetCatalogTests
{
    [Theory]
    [InlineData("terran-scout")]
    [InlineData("progen-privateer")]
    [InlineData("jenquai-seeker")]
    public void MissingProfessionSequenceResolvesOnlyToPackagedApplicationAudio(string profession)
    {
        string root=Path.GetFullPath("application-root");

        for(int part=1;part<=3;part++)
        {
            string logicalName=EnbNextAudioAssetCatalog.CharacterCreation(profession,part);
            Assert.StartsWith(EnbNextAudioAssetCatalog.Prefix,logicalName,StringComparison.Ordinal);
            Assert.True(EnbNextAudioAssetCatalog.TryResolve(logicalName,root,out string path));
            Assert.StartsWith(root,path,StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(Path.Combine("Assets","Audio","CharacterCreation",$"{profession}-{part}.mp3"),
                path,StringComparison.OrdinalIgnoreCase);
            var fallback=new EnbRetailSoundDefinition(
                Name:logicalName,Files:[new(logicalName,null,false)],LoopCount:null,
                DropOffRadius:null,MaxVolumeRadius:null,Priority:null,Volume:.5f,
                MinVolumeShift:null,MaxVolumeShift:null,MinPitchShift:null,MaxPitchShift:null,
                MinDopplerShift:null,MaxDopplerShift:null,DopplerTweak:null,SoundType:1,
                MaxInstances:null,Pitch:null,SecondsBetweenCalls:null,
                Properties:new Dictionary<string,string>());
            var definition=EnbNextAudioAssetCatalog.CharacterCreationSpeechDefinition(logicalName,fallback);
            Assert.Equal(1,definition.EffectiveLoopCount);
            Assert.Equal(1f,definition.EffectivePriority);
            Assert.Equal(.8f,definition.EffectiveVolume);
            Assert.Equal(2,definition.EffectiveSoundType);
        }
    }

    [Fact]
    public void UnknownApplicationAudioDoesNotResolve()
    {
        Assert.False(EnbNextAudioAssetCatalog.TryResolve(
            "enbnext:audio/character-creation/not-a-profession-1.mp3","application-root",out string path));
        Assert.Equal("",path);
    }
}
