using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7PresentationStateTests
{
    [Fact]
    public void WorldPresentationPacketsMaterializeWithoutChangingObjectIdentity()
    {
        var catalogs = BuildCatalogs();
        var world = new Net7WorldState(presentation: catalogs);
        Net7WorldObject original = world.MaterializeCreate(new Net7CreateUpdate(42, 1f, 10, 0x25, 0f, 0f, 0f));
        Assert.Equal(10, original.BaseAsset);
        Assert.Equal(10, original.BaseAssetPresentation!.Selector);

        byte[] change = new byte[24];
        Net7PresentationWireTests.WriteU32(change, 0, 42);
        BinaryPrimitives.WriteInt32LittleEndian(change.AsSpan(4, 4), 20);
        Net7PresentationWireTests.WriteF32(change, 8, 2f);
        Net7PresentationWireTests.WriteF32(change, 12, 0.1f);
        Net7PresentationWireTests.WriteF32(change, 16, 0.2f);
        Net7PresentationWireTests.WriteF32(change, 20, 0.3f);
        world.Apply(Packet.ForOpcode(0x0026, change), 0);

        Net7WorldObject updated = world.Objects.FindFirst(42)!;
        Assert.Same(original, updated);
        Assert.Equal(42u, updated.GameId);
        Assert.Equal(20, updated.BaseAsset);
        Assert.Equal(20, updated.BaseAssetPresentation!.Selector);
        Assert.Equal(2f, updated.Scale);
        Assert.Equal(0.3f, updated.HsvV);

        world.Apply(Packet.ForOpcode(0x0037, Convert.FromHexString("44332211")), 0);
        world.Apply(Packet.ForOpcode(0x0047, Convert.FromHexString("88776655")), 0);
        Assert.Equal(0x11223344u, world.ClientAvatarId);
        Assert.Equal(0x55667788u, world.ClientShipId);

        byte[] parameters = new byte[70];
        BinaryPrimitives.WriteInt16LittleEndian(parameters.AsSpan(64, 2), -7);
        Net7PresentationWireTests.WriteU32(parameters, 66, 123);
        world.Apply(Packet.ForOpcode(0x0042, parameters), 0);
        Assert.Equal(-7, world.ServerParameters!.EnvironmentBaseAsset);
        Assert.Equal(-7, world.EnvironmentPresentation!.Selector);
        Assert.Equal(Net7BaseAssetDefinitionKind.Rsl, world.EnvironmentPresentation.Kind);

        byte[] avatarData = Net7PresentationWireTests.BuildAvatarData("WORLD_AVATAR");
        byte[] description = new byte[4 + avatarData.Length];
        Net7PresentationWireTests.WriteU32(description, 0, 42);
        avatarData.CopyTo(description, 4);
        world.Apply(Packet.ForOpcode(0x0061, description), 0);
        Assert.Equal("WORLD_AVATAR", updated.AvatarDescription!.Skeleton);

        byte[] station = Net7PresentationWireTests.BuildStarbaseAvatarChange(42, 3, 1.5f, new Vector3(4, 5, 6), 7);
        world.Apply(Packet.ForOpcode(0x009E, station), 0);
        Assert.Equal(3u, world.StationAvatars[42].RoomType);
        Assert.Equal(new Vector3(4, 5, 6), world.StationAvatars[42].Position);
    }

    [Theory]
    [InlineData(1, 0x3FC)]
    [InlineData(0, 0x135)]
    [InlineData(5, 0x475)]
    [InlineData(2, -1)]
    public void NegativeSkyBaseAssetTakesTheDefaultOfTheModeCurrentAtServerParameters(int mode, int expected)
    {
        var world = new Net7WorldState(presentation: BuildCatalogs());
        byte[] parameters = new byte[70];
        BinaryPrimitives.WriteInt16LittleEndian(parameters.AsSpan(64, 2), -1);
        world.Apply(Packet.ForOpcode(0x0042, parameters), 0);
        world.CaptureServerParametersPresentationMode(mode);
        var environment = new EnbNext.Presentation.EnbPresentationSnapshotBuilder().Build(world, null, 0).Environment!;
        Assert.Equal(-1, environment.BaseAsset);
        Assert.Equal(expected, environment.SkyBaseAsset);
        // 00513540 keeps a non-negative selector unchanged.
        Assert.Equal(378, EnbNext.Presentation.EnbSceneEnvironmentSnapshot.ResolveSkyBaseAsset(378, mode));
    }

    [Fact]
    public void RecustomizationPacketsRemainTransientPresentationStateAndResetCleanly()
    {
        var state = new Net7GameplayState(BuildCatalogs());
        state.Apply(Packet.ForOpcode(0x0081, Net7PresentationWireTests.BuildShipRecustomization()));
        Assert.Equal("I05 Ship", state.ShipRecustomization!.Customization.ShipName);
        Assert.Equal(12, state.ShipRecustomization.AuxiliaryDwords.Count);
        Assert.Equal(5, state.ShipRecustomization.TrailingNetworkValues.Count);

        byte[] avatarStart = new byte[60];
        for (int i = 0; i < 14; i++)
            Net7PresentationWireTests.WriteU32(avatarStart, i * 4, (uint)i);
        BinaryPrimitives.WriteUInt32BigEndian(avatarStart.AsSpan(56, 4), 0x01020304);
        state.Apply(Packet.ForOpcode(0x0083, avatarStart));
        Assert.Equal(14, state.AvatarRecustomizationStart!.OptionDwords.Count);
        Assert.Equal(0x01020304u, state.AvatarRecustomizationStart.TrailingNetworkValue);

        byte[] avatar = Net7PresentationWireTests.BuildAvatarData("RECUSTOM");
        byte[] avatarUpdate = new byte[avatar.Length + 4];
        avatar.CopyTo(avatarUpdate, 0);
        BinaryPrimitives.WriteUInt32BigEndian(avatarUpdate.AsSpan(avatar.Length, 4), 0x10203040);
        state.Apply(Packet.ForOpcode(0x0085, avatarUpdate));
        Assert.Equal("RECUSTOM", state.AvatarRecustomizationUpdate!.Avatar.Skeleton);
        Assert.Equal(0x10203040u, state.AvatarRecustomizationUpdate.TrailingNetworkValue);

        state.ResetForServerHandoff();
        Assert.Null(state.ShipRecustomization);
        Assert.Null(state.AvatarRecustomizationStart);
        Assert.Null(state.AvatarRecustomizationUpdate);
    }

    [Fact]
    public void EffectStateUsesDefinitionCatalogWhileRemovalStillUsesRuntimeEffectId()
    {
        var catalogs = BuildCatalogs();
        var state = new Net7GameplayState(catalogs);

        byte[] objectEffect = new byte[11];
        objectEffect[0] = 0x01; // runtime EffectID present
        Net7PresentationWireTests.WriteU32(objectEffect, 1, 0x11111111);
        BinaryPrimitives.WriteUInt16LittleEndian(objectEffect.AsSpan(5, 2), 191);
        Net7PresentationWireTests.WriteU32(objectEffect, 7, 0xABCDEF01);
        Assert.True(state.Apply(Packet.ForOpcode(0x0009, objectEffect)));

        Net7EffectState effect = state.Effects[0xABCDEF01];
        Assert.Equal(0xABCDEF01u, effect.EffectId);
        Assert.Equal((ushort)191, effect.DefinitionId);
        Assert.Equal(191, effect.Presentation!.DefinitionId);
        Assert.Equal(1911, effect.Presentation.BaseAsset!.Selector);

        byte[] objectToObject = new byte[17];
        BinaryPrimitives.WriteUInt16LittleEndian(objectToObject.AsSpan(0, 2), 0x0001);
        Net7PresentationWireTests.WriteU32(objectToObject, 2, 0x22222222);
        Net7PresentationWireTests.WriteU32(objectToObject, 6, 0x33333333);
        BinaryPrimitives.WriteUInt16LittleEndian(objectToObject.AsSpan(10, 2), 2);
        objectToObject[12] = 0; // empty canonical bone C-string
        Net7PresentationWireTests.WriteU32(objectToObject, 13, 0xABCDEF02);
        Assert.True(state.Apply(Packet.ForOpcode(0x000B, objectToObject)));
        Net7EffectState linkedEffect = state.Effects[0xABCDEF02];
        Assert.True(linkedEffect.IsObjectToObject);
        Assert.Equal((ushort)2, linkedEffect.DefinitionId);
        Assert.Equal(68, linkedEffect.Presentation!.BaseAsset!.Selector);

        // Synthetic Prospect/Tractor paths use the same catalog while retaining
        // their independent runtime EffectID and temporary-object BaseAsset.
        state.AddSyntheticEffect(new Net7EffectState(0x90000001, 1, 2, 2, 3, -1, string.Empty, 0, true));
        Assert.Equal(68, state.Effects[0x90000001].Presentation!.BaseAsset!.Selector);

        var world = new Net7WorldState(presentation: catalogs);
        Net7WorldObject temporary = world.MaterializeCreate(new Net7CreateUpdate(0x70000001, 1f, 77, 0x25, 0, 0, 0));
        Assert.Equal(77, temporary.BaseAssetPresentation!.Selector);
        Assert.NotEqual(temporary.BaseAssetPresentation.Selector, state.Effects[0x90000001].Presentation!.BaseAsset!.Selector);

        byte[] remove = new byte[4];
        Net7PresentationWireTests.WriteU32(remove, 0, 0xABCDEF01);
        state.Apply(Packet.ForOpcode(0x000F, remove));
        Assert.DoesNotContain(0xABCDEF01u, state.Effects.Keys);
        Assert.Contains(0x90000001u, state.Effects.Keys);
    }

    [Fact]
    public async Task SessionCatalogLoadingIsLocalAndEmitsNoNetworkTraffic()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"enbnext-i05-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string basset = Path.Combine(directory, "basset.ini");
        string effect = Path.Combine(directory, "effect.ini");
        await File.WriteAllTextAsync(basset, "[BASE-10]\nRSLID=10\n");
        await File.WriteAllTextAsync(effect, "[EFFECT-191]\nEffectClass=LineEffect\nBaseAssetID=10\n");

        try
        {
            await using var session = new Net7Session(new Net7ServerOptions());
            int tcpSends = 0;
            int udpSends = 0;
            session.TcpPacketSent += _ => tcpSends++;
            session.UdpFrameSent += (_, _) => udpSends++;

            session.LoadBaseAssetCatalog(basset);
            session.LoadEffectCatalog(effect);

            Assert.Equal(Net7BaseAssetDefinitionKind.Rsl, session.Presentation.ResolveBaseAsset(10).Kind);
            Assert.Equal(10, session.Presentation.ResolveEffect(191).BaseAsset!.Selector);
            Assert.Equal(0, tcpSends);
            Assert.Equal(0, udpSends);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CharacterSelectionResetClearsI05MaterializedStateButNotLocalCatalogDefinitions()
    {
        var catalogs = BuildCatalogs();
        var world = new Net7WorldState(presentation: catalogs);
        var gameplay = new Net7GameplayState(catalogs);

        world.MaterializeCreate(new Net7CreateUpdate(1, 1, 10, 0x25, 0, 0, 0));
        world.Apply(Packet.ForOpcode(0x0037, Convert.FromHexString("01000000")), 0);
        world.Apply(Packet.ForOpcode(0x0047, Convert.FromHexString("02000000")), 0);
        world.Apply(Packet.ForOpcode(0x009E, Net7PresentationWireTests.BuildStarbaseAvatarChange(1, 2, 3, Vector3.One, 4)), 0);
        gameplay.Apply(Packet.ForOpcode(0x0081, Net7PresentationWireTests.BuildShipRecustomization()));

        world.ResetForCharacterSelection();
        gameplay.ResetForCharacterSelection();

        Assert.Null(world.ClientAvatarId);
        Assert.Null(world.ClientShipId);
        Assert.Empty(world.StationAvatars);
        Assert.Empty(world.Objects.Snapshot());
        Assert.Null(gameplay.ShipRecustomization);
        Assert.Equal(Net7BaseAssetDefinitionKind.Rsl, catalogs.ResolveBaseAsset(10).Kind);
        Assert.False(catalogs.ResolveEffect(191).MissingDefinition);
    }


    private static Net7PresentationCatalogs BuildCatalogs()
    {
        var catalogs = new Net7PresentationCatalogs();
        catalogs.LoadBaseAssetsText("""
            [BASE-10]
            RSLID=10
            [BASE-20]
            RSLID=20
            [BASE--7]
            RSLID=7
            [BASE-68]
            RSLID=68
            [BASE-77]
            RSLID=77
            [BASE-1911]
            RSLID=1911
            """);
        catalogs.LoadEffectsText("""
            [EFFECT-2]
            EffectClass=LineEffect
            BaseAssetID=68
            Duration=INFINITE
            [EFFECT-191]
            EffectClass=LineEffect
            BaseAssetID=1911
            Duration=INFINITE
            """);
        return catalogs;
    }
}
