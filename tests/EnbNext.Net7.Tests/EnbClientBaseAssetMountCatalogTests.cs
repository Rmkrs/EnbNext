using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbClientBaseAssetMountCatalogTests
{
    [Fact]
    public void ControllerRadiusUsesAuthoredAdjustmentAndNativeDefaultsWithoutRequiringMounts()
    {
        var catalog = EnbClientBaseAssetMountCatalog.ParseText("[BASE-1]\nRadius=12\nRadiusAdjust=-2\n[BASE-2]\nRadiusAdjust=3\n[BASE-3]\nRadius=0\n");
        Assert.Equal(10f, catalog.ResolveControllerRadius(1));
        Assert.Equal(4f, catalog.ResolveControllerRadius(2));
        Assert.Equal(0f, catalog.ResolveControllerRadius(3));
        Assert.Equal(1f, catalog.ResolveControllerRadius(999));
    }

    [Fact]
    public void ParsesSequentialMountsAndResolvesExactBoneNameToBoneIndex()
    {
        EnbClientBaseAssetMountCatalog catalog = EnbClientBaseAssetMountCatalog.ParseText(
            """
            [BASE-1672]
            Radius=10

            [BASE-1672/Mount00]
            BoneName=~03_01
            BoneIndex=4

            [BASE-1672/Mount01]
            BoneName=~03_02
            BoneIndex=5

            [BASE-1672/Mount02]
            BoneName=~WEAP_01
            BoneIndex=7
            """);

        Assert.Equal(1, catalog.BaseAssetCount);
        Assert.Equal(3, catalog.MountCount);
        Assert.True(catalog.TryGetMountSet(1672, out EnbClientBaseAssetMountSet set));
        Assert.Collection(
            set.Mounts,
            mount => Assert.Equal(new EnbClientBaseAssetMount(1672, 0, "~03_01", 4), mount),
            mount => Assert.Equal(new EnbClientBaseAssetMount(1672, 1, "~03_02", 5), mount),
            mount => Assert.Equal(new EnbClientBaseAssetMount(1672, 2, "~WEAP_01", 7), mount));

        Assert.True(catalog.TryResolveMount(1672, "~WEAP_01", out EnbClientBaseAssetMount weapon));
        Assert.Equal(7, weapon.BoneIndex);
        Assert.False(catalog.TryResolveMount(1672, "~weap_01", out _));
    }

    [Fact]
    public void StopsAtFirstMissingMountSectionLikeRetail()
    {
        EnbClientBaseAssetMountCatalog catalog = EnbClientBaseAssetMountCatalog.ParseText(
            """
            [BASE-42/Mount00]
            BoneName=~ROOT
            BoneIndex=1

            [BASE-42/Mount02]
            BoneName=~MUST_NOT_LOAD
            BoneIndex=3
            """);

        Assert.Equal(1, catalog.MountCount);
        Assert.True(catalog.TryResolveMount(42, "~ROOT", out _));
        Assert.False(catalog.TryResolveMount(42, "~MUST_NOT_LOAD", out _));
    }

    [Fact]
    public void InvalidBoneIndexStopsLaterMountConstructionButKeepsEarlierMounts()
    {
        EnbClientBaseAssetMountCatalog catalog = EnbClientBaseAssetMountCatalog.ParseText(
            """
            [BASE-88/Mount00]
            BoneName=~VALID
            BoneIndex=2

            [BASE-88/Mount01]
            BoneName=~INVALID
            BoneIndex=0

            [BASE-88/Mount02]
            BoneName=~LATE
            BoneIndex=6
            """);

        Assert.Equal(1, catalog.MountCount);
        Assert.True(catalog.TryResolveMount(88, "~VALID", out EnbClientBaseAssetMount valid));
        Assert.Equal(2, valid.BoneIndex);
        Assert.False(catalog.TryResolveMount(88, "~INVALID", out _));
        Assert.False(catalog.TryResolveMount(88, "~LATE", out _));
    }

    [Fact]
    public void LaterDuplicateBoneNameIsTheLookupResult()
    {
        EnbClientBaseAssetMountCatalog catalog = EnbClientBaseAssetMountCatalog.ParseText(
            """
            [BASE-99/Mount00]
            BoneName=~WEAP
            BoneIndex=1

            [BASE-99/Mount01]
            BoneName=~WEAP
            BoneIndex=9
            """);

        Assert.Equal(2, catalog.MountCount);
        Assert.True(catalog.TryResolveMount(99, "~WEAP", out EnbClientBaseAssetMount resolved));
        Assert.Equal(1, resolved.Ordinal);
        Assert.Equal(9, resolved.BoneIndex);
    }
}
