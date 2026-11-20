using EnbNext.Net7.World;

namespace EnbNext.Net7.Tests;

public sealed class Net7AssumptionAuditTests
{

    [Fact]
    public void NamedAuxDataPropertyIdsResolveToExactClosedSchemaPaths()
    {
        var catalog = new Net7AuxDataSchemaCatalog();

        Assert.Equal("ShipAuxData.TargetGameID", catalog.Ship.Resolve(Net7AuxDataPropertyIds.ShipTargetGameId).Path);
        Assert.Equal("LocalPlayerAuxData.ClientSendUITriggers",
            catalog.LocalPlayer.Resolve(Net7AuxDataPropertyIds.LocalPlayerClientSendUiTriggers).Path);
        Assert.Equal("LocalPlayerAuxData.RPGInfo.SkillAbilities.0.Validity",
            catalog.LocalPlayer.Resolve(Net7AuxDataPropertyIds.LocalPlayerSkillAbility0Validity).Path);
        Assert.Equal("LocalPlayerAuxData.RPGInfo.SkillAbilities.28.Validity",
            catalog.LocalPlayer.Resolve(Net7AuxDataPropertyIds.LocalPlayerSkillAbility28Validity).Path);
    }


}
