namespace EnbNext.Net7.World;

/// <summary>
/// Named PropertyIds whose numeric identity is established by
/// current-client AuxData dossiers. Keeping these names centralized prevents
/// anonymous schema numbers from becoming accidental authority in gameplay code.
/// </summary>
public static class Net7AuxDataPropertyIds
{
    // ShipAuxData.LockSpeed.
    public const int ShipLockSpeed = 23;

    // ShipAuxData.LockOrient: 0057D6C0 reads it beside LockSpeed for every
    // current steering producer; 005821C0 enables the steering buttons from it.
    public const int ShipLockOrient = 24;

    // ShipAuxData.TargetGameID.
    public const int ShipTargetGameId = 34;

    // Current-client scanner RefreshSelectedTargetUi @ 0x00512400.
    // These are server-authored display semantics; the client does not derive
    // the con string from the two ships' combat levels.
    public const int ShipTargetThreat = 35;
    public const int ShipTargetThreatSound = 36;
    public const int ShipTargetThreatLevel = 37;

    // ShipAuxData.IsInPVP, consumed by current retail's
    // IsOtherPlayerAttackableInPvp path before selecting [PVPAttackable].
    public const int ShipIsInPvp = 30;

    // Active radar range scalar consumed by the current
    // client's RadarObjectContextIsTargetable path.
    public const int ShipCurrentScanRange = 1912;

    // Persistent credits and faction materialization.
    public const int LocalPlayerMoney = 1;
    public const int LocalPlayerReputationRoot = 5422;
    public const int LocalPlayerReputationFactionAggregate = 5423;
    public const int LocalPlayerReputationFaction0Root = 5424;
    public const int LocalPlayerReputationFaction0Name = 5425;
    public const int LocalPlayerReputationFaction0Reaction = 5426;
    public const int LocalPlayerReputationFaction0Order = 5427;
    public const int LocalPlayerReputationFactionStride = 4;
    public const int LocalPlayerReputationFactionCount = 32;
    public const int LocalPlayerReputationAffiliation = 5552;
    public const int ShipFactionIdentifier = 1932;

    // Fixed five-member LocalPlayerAuxData.GroupInfo member array used by
    // IsPlayerObjectInLocalGroup @ client.exe 0x0070BAC0.
    public const int LocalPlayerGroupMember0GameId = 5573;
    public const int LocalPlayerGroupMemberStride = 4;
    public const int LocalPlayerGroupMemberCount = 5;

    // LocalPlayerAuxData.ClientSendUITriggers.
    public const int LocalPlayerClientSendUiTriggers = 5558;

    // Tractor-A synthetic Validity destinations.
    public const int LocalPlayerSkillAbility0Validity = 0x0C15;
    public const int LocalPlayerSkillAbility28Validity = 0x0CF5;

    // LastActivation fixture used by AuxData codec conformance.
    public const int LocalPlayerSkill41LastActivation = 0x0B59;
}
