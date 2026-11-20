using EnbNext.Net7.Protocol;
namespace EnbNext.Presentation;
public static class EnbAvatarCustomizationProjection
{
    public static Net7AvatarData ToWire(EnbSceneAvatarSnapshot a) => new(
        a.FirstName,
        a.LastName,
        a.AvatarType,
        a.UnknownU8,
        a.AvatarVersion,
        a.Race,
        a.Profession,
        a.Gender,
        a.MoodType,
        a.Personality,
        a.Nlp,
        a.BodyType,
        a.PantsType,
        a.HeadType,
        a.HairNum,
        a.EarNum,
        a.GoggleNum,
        a.BeardNum,
        a.WeaponHipNum,
        a.WeaponUniqueNum,
        a.WeaponBackNum,
        a.HeadTextureNum,
        a.TattooTextureNum,
        a.TattooOffset.ToArray(),
        a.HairColor.ToArray(),
        a.BeardColor.ToArray(),
        a.EyeColor.ToArray(),
        a.SkinColor.ToArray(),
        a.ShirtPrimaryColor.ToArray(),
        a.ShirtSecondaryColor.ToArray(),
        a.PantsPrimaryColor.ToArray(),
        a.PantsSecondaryColor.ToArray(),
        a.ShirtPrimaryMetal,
        a.ShirtSecondaryMetal,
        a.PantsPrimaryMetal,
        a.PantsSecondaryMetal,
        a.Skeleton,
        a.BodyWeight.ToArray(),
        a.HeadWeight.ToArray() );
    public static EnbSceneAvatarSnapshot ToScene(Net7AvatarData a) => EnbPresentationSnapshotBuilder.BuildAvatar(a)!;
}

