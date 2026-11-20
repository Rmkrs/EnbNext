using EnbNext.Assets;
using EnbNext.Presentation;
namespace EnbNext.Net7.Tests;

public sealed class EnbSkillDetailsParityTests
{
    [Theory]
    [InlineData(0x40, "Maximum Level Attained")]
    [InlineData(0xc3, "Maximum Level Attained")]
    [InlineData(0x83, "Progen Only.")]
    [InlineData(0x3b, "Upgrade requires level 12")]
    [InlineData(0x3a, "Requires combat level 12")]
    [InlineData(0x32, "Requires trade level 12")]
    [InlineData(0x22, "Requires explore level 12")]
    [InlineData(2, "Insufficient skill points")]
    [InlineData(4, "")]
    public void NativeReasonPriorityUsesAvailabilityFlags(int flags,string reason)
    {
        var skill=new EnbCharacterSkillSnapshot(0,"Test","",1,9,""){QuestOnlyLevels=9,Availability=new(2,flags,12,3),Affiliations="Progen"};
        Assert.Equal(reason,skill.UpgradeReason);Assert.False(skill.CanTrain);
        Assert.Equal((flags&0x40)!=0,skill.HideUpgradeCost);
    }
    [Fact]
    public void AttainedAndNonUpgradeableStatesSuppressCostWithoutInventingReason()
    {
        var skill=new EnbCharacterSkillSnapshot(0,"Test","",9,9,""){QuestOnlyLevels=9,Availability=new(2,1,12,3)};
        Assert.True(skill.HideUpgradeCost);Assert.Equal("",skill.UpgradeReason);Assert.False(skill.CanTrain);
        skill=skill with{Level=1,Availability=new(3,1,12,3)};
        Assert.True(skill.HideUpgradeCost);Assert.Equal("",skill.UpgradeReason);Assert.False(skill.CanTrain);
        skill=skill with{Availability=new(2,0,12,3)};
        Assert.False(skill.HideUpgradeCost);Assert.True(skill.CanTrain);
        skill=skill with{QuestOnlyLevels=1,Maximum=9};Assert.False(skill.CanTrain);
        skill=skill with{QuestOnlyLevels=9,Maximum=1};Assert.True(skill.CanTrain);
    }
    [Theory]
    [InlineData(0,false,false)]
    [InlineData(1,false,false)]
    [InlineData(2,true,false)]
    [InlineData(3,true,false)]
    [InlineData(4,true,true)]
    public void RowMembershipAndUpgradeIndicatorHaveDistinctNativePredicates(int state,bool visible,bool indicator)
    {
        var skill=new EnbCharacterSkillSnapshot(0,"Test","",0,0,""){Availability=new(state,0,0,0),QuestOnlyLevels=9};
        Assert.Equal(visible,skill.VisibleInSkillPanel);Assert.Equal(indicator,skill.ShowUpgradeIndicator);
        Assert.False((skill with{Availability=new(state,1,0,0)}).ShowUpgradeIndicator);
    }
    [Fact]
    public void AcquisitionUsesClassTemplateAndMergedAuthoredValues()
    {
        var basic=EnbRetailIniDocument.Parse("[All Skills]\n0=Test\n[Test]\nDescription=base\nDesc_1=base level\n[Test\\Tradesman]\nTrainer=Alice\nLocation=Dock\nSector=Home\nSystem=Sol\nLearn Lvl=5");
        var extra=EnbRetailIniDocument.Parse("[Test]\nDescription=extension\nTradesman=5\nDesc_1=updated\n[Test\\Tradesman]\nTrainer=Bob");
        var family=EnbRetailSkillCatalog.Parse(basic,extra,EnbRetailIniDocument.Parse("")).Families.Single();
        Assert.Equal(5,family.ClassMaximum(0,1));Assert.Equal(0,family.ClassMaximum(0,0));
        Assert.Equal("extension",family.Description);Assert.Equal("updated",family.LevelDescriptions[1]);
        Assert.Equal("See Bob at Dock in Home, Sol System upon reaching level 5.",family.AcquisitionDescription(0,1));
        Assert.Equal("Bug: Report Me",family.AcquisitionDescription(2,0));
    }
}
