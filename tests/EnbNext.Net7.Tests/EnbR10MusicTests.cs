using EnbNext.Assets;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10MusicTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void PlanetSnapshotKeepsMusicAndCombatTakesPrecedence(int mode)
    {
        var world=new Net7WorldState();
        world.LocalPlayerAuxData.Resolve(2571).State=2;
        world.LocalPlayerAuxData.Resolve(2571).Value=new byte[]{2,0,0,0};
        var context=new EnbPresentationSnapshotBuilder().Build(world,null,0,presentationMode:mode).Music;
        Assert.NotNull(context);
        Assert.Equal("Planet",context.Category);
        Assert.Equal("Combat",(context with {Combat=true}).Category);
        var catalog=EnbRetailMusicCatalog.Parse(EnbRetailIniDocument.Parse("[Global]\nPlanetFadeIn=3\nPlanetFadeOut=4"));
        Assert.Equal(3,catalog.PlanetFadeInSeconds);
        Assert.Equal(4,catalog.PlanetFadeOutSeconds);
    }
    [Fact]
    public void SnapshotMusicUsesAnyTrackedAttackerAndClearsWhenFlagClears()
    {
        var world=new Net7WorldState();
        world.LocalPlayerAuxData.Resolve(2571).State=2;
        world.LocalPlayerAuxData.Resolve(2571).Value=new byte[]{2,0,0,0};
        var builder=new EnbPresentationSnapshotBuilder();
        Assert.False(builder.Build(world,null,0).Music!.Combat);
        byte[] create=new byte[23];create[0]=7;create[10]=3;
        BitConverter.TryWriteBytes(create.AsSpan(4),1f);
        world.Apply(Packet.ForOpcode(4,create),0);
        var attacker=world.Objects.FindFirst(7)!;
        attacker.IsAttacking=1;
        // No local ship, selected target or spatial/radar eligibility is needed.
        Assert.True(builder.Build(world,null,0).Music!.Combat);
        attacker.IsAttacking=0;
        Assert.False(builder.Build(world,null,0).Music!.Combat);
    }
    [Fact]
    public void ScanTextDoesNotCreateCombatButServerAttackerByteDoes()
    {
        var world=new Net7WorldState();
        world.LocalPlayerAuxData.Resolve(2571).State=2;
        world.LocalPlayerAuxData.Resolve(2571).Value=new byte[]{2,0,0,0};
        byte[] create=new byte[23];create[0]=7;create[10]=3;
        BitConverter.TryWriteBytes(create.AsSpan(4),1f);world.Apply(Packet.ForOpcode(4,create),0);
        var game=new EnbNext.Net7.Gameplay.Net7GameplayState(resolveWorldObject:world.Objects.FindFirst);
        var builder=new EnbPresentationSnapshotBuilder();
        var scan=Packet.ForOpcode(0x1D,new byte[]{5,0,19,83,99,97,110,0});
        world.Apply(scan,0);game.Apply(scan);
        Assert.False(builder.Build(world,null,0).Music!.Combat);
        world.Apply(Packet.ForOpcode(0x8B,new byte[]{1,0,0,0,1,0,0,0,7}),0);
        Assert.Equal((byte)1,world.Objects.FindFirst(7)!.IsAttacking);
        Assert.True(builder.Build(world,null,0).Music!.Combat);
        world.Apply(scan,0);game.Apply(scan);
        Assert.True(builder.Build(world,null,0).Music!.Combat);
        world.Apply(Packet.ForOpcode(0x8B,new byte[]{0,0,0,0,1,0,0,0,7}),0);
        Assert.False(builder.Build(world,null,0).Music!.Combat);
    }
    [Fact]
    public void CombatUsesItsOwnAuthoredPlaylistAndGlobalFades()
    {
        var catalog=EnbRetailMusicCatalog.Parse(EnbRetailIniDocument.Parse("""
            [Global]
            CombatFadeIn=3.5
            CombatFadeOut=0
            WanderFadeOut=1.25
            [MusicTheme-001]
            Race=Progen
            Type=Combat
            ComponentCount=1
            Component-00=combat.mp3
            [MusicTheme-002]
            Race=Progen
            Type=Wander
            ComponentCount=1
            Component-00=wander.mp3
            """));
        Assert.Equal(3.5,catalog.CombatFadeInSeconds);
        Assert.Equal(0,catalog.CombatFadeOutSeconds);
        Assert.Equal(1.25,catalog.WanderFadeOutSeconds);
        Assert.Equal(6,catalog.WanderFadeInSeconds);
        Assert.Equal(1,Assert.Single(catalog.Playlist("Combat",2,0,null,new Random(1))).Id);
        Assert.Equal(2,Assert.Single(catalog.WanderPlaylist(2,0,null,new Random(1))).Id);
        Assert.Empty(catalog.Playlist("Combat",1,0,null,new Random(1)));
    }
    [Fact]
    public void AuthoredThemesPreserveComponentOrderAndIndependentFades()
    {
        var catalog = EnbRetailMusicCatalog.Parse(EnbRetailIniDocument.Parse("""
            [Global]
            SilenceMin=20
            SilenceMax=60
            [MusicTheme-012]
            Race=Jenquai
            ComponentCount=2
            Component-01=second.mp3
            Component-00=first.mp3
            FadeIn-00=10
            FadeOut-01=3
            """));
        var theme = Assert.Single(catalog.Themes);
        Assert.Equal(12,theme.Id); Assert.Equal(1,theme.Race);
        Assert.Equal("Wander",theme.Type); Assert.Equal(-1,theme.Sector);
        Assert.Equal(0,theme.LoopCount);
        Assert.Equal(new("first.mp3",10,0),theme.Components[0]);
        Assert.Equal(new("second.mp3",0,3),theme.Components[1]);
        Assert.Equal(20,catalog.SilenceMinSeconds); Assert.Equal(60,catalog.SilenceMaxSeconds);
    }

    [Fact]
    public void PlaylistFiltersRaceLevelSectorAndCategoryWithoutRepeatingPriorTheme()
    {
        EnbRetailMusicTheme Theme(int id,int race=1,int level=0,int sector=-1,string type="Wander")
            => new(id,race,type,level,sector,0,[new("fixture.mp3",0,0)]);
        var catalog = new EnbRetailMusicCatalog([Theme(1),Theme(2),Theme(3,8),Theme(4,0),
            Theme(5,level:100),Theme(6,sector:42),Theme(7,type:"Combat")],0,0);
        Assert.Equal(new[] {2,3},catalog.WanderPlaylist(1,50,1,new Random(1)).Select(t=>t.Id).Order().ToArray());
        Assert.Equal(1,Assert.Single(new EnbRetailMusicCatalog([Theme(1)],0,0)
            .WanderPlaylist(1,50,1,new Random(1))).Id);
    }

    [Theory]
    [InlineData("[Global]\nSilenceMin=10\nSilenceMax=2")]
    [InlineData("[Global]\nSilenceMax=NaN")]
    [InlineData("[MusicTheme-001]\nComponentCount=1")]
    [InlineData("[MusicTheme-001]\nComponentCount=-1")]
    public void InvalidLocalDefinitionsFailWithDiagnostic(string text)
        => Assert.Throws<InvalidDataException>(()=>EnbRetailMusicCatalog.Parse(EnbRetailIniDocument.Parse(text)));

    [Fact]
    public void MusicContextRequiresPublishedRaceAndUsesRetainedPlayerValues()
    {
        var player = new Net7AuxDataSchemaCatalog().LocalPlayer;
        Assert.Null(EnbSceneMusicContext.From(player));
        player.Resolve(2571).State=2; player.Resolve(2571).Value=new byte[] {1,0,0,0};
        player.Resolve(4450).Value=10; player.Resolve(4452).Value=20; player.Resolve(4454).Value=30;
        player.Resolve(4460).Value=-1;
        Assert.Equal(new(1,60,-1),EnbSceneMusicContext.From(player));
        Assert.True(EnbSceneMusicContext.From(player,combat:true)!.Combat);
        player.Resolve(4460).Value=12;
        Assert.Equal(12,EnbSceneMusicContext.From(player)!.MusicId);
        player.Resolve(2571).Value=new byte[] {5,0,0,0};
        Assert.Equal(5,EnbSceneMusicContext.From(player)!.Race);
    }

    [Theory]
    [InlineData(0,0)] [InlineData(25,25)] [InlineData(-10,0)] [InlineData(110,100)]
    public void PersistedMusicAndSfxVolumesRespectMuteAndClamp(float input,float expected)
    {
        var options=EnbRetailPlayerOptionsCatalog.ParseText(FormattableString.Invariant(
            $"[FLOAT-1]\nOptionName=Sound_Music\nOptionValue={input}\n[FLOAT-2]\nOptionName=Sound_SFX\nOptionValue={input}"));
        Assert.Equal(expected,options.SoundMusic); Assert.Equal(expected,options.SoundSfx);
        Assert.Equal(30,EnbRetailPlayerOptionsCatalog.ParseText("").SoundMusic);
        Assert.Equal(80,EnbRetailPlayerOptionsCatalog.ParseText("").SoundSfx);
    }

}
