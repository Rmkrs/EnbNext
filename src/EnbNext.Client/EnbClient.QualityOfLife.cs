using EnbNext.Presentation;
namespace EnbNext.Client;

public sealed partial class EnbClient
{
    private EnbQualityOfLifeOptions qualityOfLife = new();
    private int multiboxLeaderFormation;
    public void ConfigureQualityOfLife(EnbQualityOfLifeOptions value)
    {
        qualityOfLife = value;
        _net7?.ConfigureAutomaticActions(value.AutoAcceptGroupInvites, value.AutoAcceptTrade, value.AutoFormUp, multiboxLeaderFormation);
    }
    /// <summary>Formation (0 none, 1 Pipe, 2 Slot, 3 Block) a leader creates after launching.</summary>
    public void ConfigureMultiboxLeaderFormation(int value)
    {
        value = Math.Clamp(value, 0, 3);
        if (value == multiboxLeaderFormation) return;
        multiboxLeaderFormation = value;
        ConfigureQualityOfLife(qualityOfLife);
    }
    /// <summary>The leader defers its launch formation while a multibox group member is docked.</summary>
    public void ConfigureMultiboxFormationMembers(bool docked) => _net7?.ConfigureFormationMembersDocked(docked);
    /// <summary>A multibox group member arrived in space; the leader re-offers its formation.</summary>
    public void RequestMultiboxFormationRefresh() => _net7?.RequestFormationRefresh();
}
