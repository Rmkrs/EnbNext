using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.World;

namespace EnbNext.Net7.Gameplay;

public enum Net7MissionNotificationKind
{
    New,
    Updated,
    StageCompleted,
    Completed,
    TimeExpired,
    Failed,
}

public sealed record Net7MissionView(
    int SlotIndex,
    int InternalMissionId,
    string Name,
    string Summary,
    string Reward,
    string FailureConsequence,
    string IssuingFaction,
    int StartTime,
    int Stage,
    int StageCount,
    string CurrentStageText,
    bool IsTimed,
    bool IsForfeitable,
    bool IsComplete,
    bool IsFailed,
    bool IsExpired,
    long? RemainingMilliseconds)
{
    public bool CanForfeit => !IsComplete && !IsFailed && !IsExpired && IsForfeitable;
    public bool CanClear => IsComplete || IsFailed || IsExpired;
}

public sealed record Net7MissionNotification(int SlotIndex, Net7MissionNotificationKind Kind, string MissionName)
{
    public IReadOnlyList<Net7MessageLineUpdate> Lines { get; init; } = [];
    public string? Sound { get; init; }
}

public sealed record Net7SkillAvailability(int Raw0, int Raw1, int RequiredLevel, int UpgradeCost);

public sealed record Net7SkillView(
    int SkillIndex,
    int Level,
    int RecycleTimeMilliseconds,
    long LastActivation,
    Net7SkillAvailability Availability,
    int QuestOnlyLevels,
    int MaxSkillLevels,
    string Affiliations,
    int SkillPoints)
{
    public bool CanTrain => Availability.Raw1 == 0 && Availability.Raw0 != 3 && Level < QuestOnlyLevels;

    public long RechargeRemaining(long currentClientTime)
        => Math.Max(0, LastActivation + RecycleTimeMilliseconds - currentClientTime);
}

public sealed record Net7SkillAbilityPresentation(
    int AbilityIndex,
    bool HasAbility,
    uint UsageRaw,
    uint TargetsRaw,
    int Range,
    int Radius,
    uint ValidityRaw,
    bool CanBeUsedWhileIncapacitated,
    uint? CurrentTargetId,
    float? CurrentTargetDistance,
    long RechargeRemainingMilliseconds);

public sealed record Net7BuffElementPresentation(
    string SourceEntity,
    string SourceObject,
    int Magnitude,
    bool IsActive,
    long ExpirationTime)
{
    // Retail asserts this deterministic relationship while
    // materializing the five server-authored BuffArray elements. Keep
    // it observable for diagnostics without rewriting authoritative state.
    public bool IsSourceObjectMagnitudeConsistent
        => string.IsNullOrEmpty(SourceObject) == (Magnitude == 0);
}

public sealed record Net7BuffPresentation(
    int SlotIndex,
    string BuffType,
    string ScrubTypeName,
    bool IsPermanent,
    long RemovalTime,
    bool IsVisible,
    IReadOnlyList<Net7BuffElementPresentation> Elements);

public sealed record Net7NavigationSemantics(
    uint GameId,
    float Signature,
    byte PlayerHasVisited,
    uint NavType,
    byte IsHuge)
{
    public bool IsRevealed => PlayerHasVisited != 0;
    public bool UsesUnknownStarMapRepresentation => !IsRevealed;
    public bool UsesNormalTypeSpecificStarMapRepresentation => IsRevealed;
    public bool IsOrdinaryRouteCandidate => NavType == 1;
    public bool IsHugeObject => IsHuge != 0;
    public bool ParticipatesInStarMap => true;
    public bool UsesAlternateMapGraphics => IsHugeObject;
    public bool UsesNormalSignatureDistanceTargetability => !IsHugeObject;
    public bool BypassesNormalNavigationRangeGate => IsHugeObject;

    public float AddSignatureToDestinationSpatialRadius(float objectSpatialRadius)
        => objectSpatialRadius + Signature;

    public float? GetOrdinaryRouteCandidateSpatialValue()
        => IsOrdinaryRouteCandidate ? Signature : null;

    public float? GetNormalRadarTargetabilityGeometry(float contextRangeScalar)
        => IsHugeObject ? null : Signature + contextRangeScalar;
}

public sealed record Net7FactionStanding(string FactionKey, float Reaction, int Order);

public enum Net7TransitionKind
{
    Dock,
    Land,
    Gate,
}

public readonly record struct Net7TransitionRequestState(Net7TransitionKind Kind, uint RequestedTargetId);

public sealed record Net7GroupMemberView(string Name, uint GameId, byte[] Formation, int Position);

public sealed record Net7GroupStateView(
    bool IsGroupLeader,
    bool LookingForGroup,
    bool AllowGroupInvite,
    bool ShowNonCombatActivities,
    bool ForceAutoSplit,
    bool RestrictedLootingRights,
    bool AutoReleaseLootingRestrictions,
    string FormationName,
    byte[] Formation,
    int Position,
    IReadOnlyList<Net7GroupMemberView> Members)
{
    public bool IsInGroup => Members.Any(static member => !string.IsNullOrEmpty(member.Name));

    public bool ContainsGameId(uint gameId)
        => gameId != 0 && Members.Any(member => member.GameId == gameId);

    public uint RequireMemberGameId(int index)
    {
        if ((uint)index >= 5u)
            throw new ArgumentOutOfRangeException(nameof(index), "Current GroupInfo contains exactly five member slots.");
        uint gameId = Members[index].GameId;
        if (gameId == 0)
            throw new InvalidOperationException("Current group member slot has no materialized GameID.");
        return gameId;
    }

    public Net7PlayerAction LeaveAction
        => IsGroupLeader ? Net7PlayerAction.GroupDisband : Net7PlayerAction.GroupRemoveOrLeaveSelf;
}

public sealed record Net7GroupFinderSettings(
    bool LookingForGroup,
    bool AllowGroupInvite,
    bool ForceAutoSplit,
    bool RestrictedLootingRights,
    bool AutoReleaseLootingRestrictions)
{
    public Net7GroupFinderSettings WithCurrentRetailOption(uint optionType, bool enabled)
        => optionType switch
        {
            0 => this with { LookingForGroup = enabled },
            1 => this with { AllowGroupInvite = enabled },
            3 => this with { ForceAutoSplit = enabled },
            4 => this with
            {
                RestrictedLootingRights = enabled,
                AutoReleaseLootingRestrictions = enabled ? false : AutoReleaseLootingRestrictions,
            },
            5 => this with
            {
                AutoReleaseLootingRestrictions = enabled,
                RestrictedLootingRights = enabled ? false : RestrictedLootingRights,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(optionType), "Current Group Finder options are 0, 1, 3, 4, and 5."),
        };
}

public sealed record Net7GuildMembershipView(
    string GuildName,
    int GuildRank,
    string GuildRankName,
    byte[] SameGuildTagColor,
    byte[] OtherGuildTagColor)
{
    public bool IsInGuild => GuildRank != -1;
}

/// <summary>
/// Client-owned semantic projections and intent/provenance state. Server-authored
/// AuxData/world objects remain authoritative; this layer derives UI/domain views and
/// stores only local request/selection state that current retail kept outside the wire.
/// </summary>
public sealed class Net7PlayerSemanticDomain
{
    private readonly Net7WorldState _world;
    private bool _initialMissionBatchSeen;
    private Net7AuxDataSchema? _missionSchema;
    private Net7AuxDataProperty? _missionRoot;
    private IReadOnlyList<uint>? _selectedWarpRoute;
    private Net7TransitionRequestState? _transitionRequest;
    private uint _dockTransitionTarget, _gateTransitionTarget;

    public Net7PlayerSemanticDomain(Net7WorldState world)
        => _world = world ?? throw new ArgumentNullException(nameof(world));

    public uint? ActiveStarbaseId { get; private set; }
    //744B60 compares the active presentation pointer with the retained station
    //pointer.737E10 can reactivate MainView without destroying that station.
    public bool IsAvatarViewActive { get; private set; }

    public void ObserveClientType(uint sectorType)
    {
        if(sectorType<=2)IsAvatarViewActive=false;
    }
    public Net7TransitionRequestState? TransitionRequest => _transitionRequest;
    public ulong TransitionRequestRevision { get; private set; }
    public IReadOnlyList<uint>? SelectedWarpRoute => _selectedWarpRoute;

    public Net7GroupStateView GetGroupState()
    {
        Net7AuxDataSchema schema = _world.LocalPlayerAuxData;
        var members = new List<Net7GroupMemberView>(5);
        for (int index = 0; index < 5; index++)
        {
            string prefix = $"LocalPlayerAuxData.GroupInfo.Members.{index}";
            members.Add(new Net7GroupMemberView(
                ReadString(schema, prefix + ".Name"),
                unchecked((uint)ReadInt32(schema, prefix + ".GameID", 0)),
                ReadRaw(schema, prefix + ".Formation", 4),
                ReadInt32(schema, prefix + ".Position", -1)));
        }

        return new Net7GroupStateView(
            ReadBoolean(schema, "LocalPlayerAuxData.GroupInfo.IsGroupLeader", false),
            ReadBoolean(schema, "LocalPlayerAuxData.GroupInfo.LookingForGroup", true),
            ReadBoolean(schema, "LocalPlayerAuxData.GroupInfo.AllowGroupInvite", true),
            ReadBoolean(schema, "LocalPlayerAuxData.GroupInfo.ShowNonCombatActivities", true),
            ReadBoolean(schema, "LocalPlayerAuxData.GroupInfo.ForceAutoSplit", true),
            ReadBoolean(schema, "LocalPlayerAuxData.GroupInfo.RestrictedLootingRights", true),
            ReadBoolean(schema, "LocalPlayerAuxData.GroupInfo.AutoReleaseLootingRestrictions", true),
            ReadString(schema, "LocalPlayerAuxData.GroupInfo.FormationName"),
            ReadRaw(schema, "LocalPlayerAuxData.GroupInfo.Formation", 4),
            ReadInt32(schema, "LocalPlayerAuxData.GroupInfo.Position", -1),
            members.AsReadOnly());
    }

    public Net7GroupFinderSettings GetGroupFinderSettings()
    {
        Net7GroupStateView group = GetGroupState();
        return new Net7GroupFinderSettings(
            group.LookingForGroup,
            group.AllowGroupInvite,
            group.ForceAutoSplit,
            group.RestrictedLootingRights,
            group.AutoReleaseLootingRestrictions);
    }

    public Net7GuildMembershipView GetGuildMembership()
    {
        if (_world.LocalShipObjectId is not uint playerId)
            return new Net7GuildMembershipView(string.Empty, -1, string.Empty, new byte[12], new byte[12]);
        Net7AuxDataSchema? ship = _world.Objects.FindFirst(playerId)?.AuxData;
        if (ship?.Name != "ShipAuxData")
            return new Net7GuildMembershipView(string.Empty, -1, string.Empty, new byte[12], new byte[12]);

        return new Net7GuildMembershipView(
            ReadString(ship, "ShipAuxData.GuildName"),
            ReadRawInt32(ship, "ShipAuxData.GuildRank", -1),
            ReadString(ship, "ShipAuxData.GuildRankName"),
            ReadRaw(ship, "ShipAuxData.SameGuildTagColor", 12),
            ReadRaw(ship, "ShipAuxData.OtherGuildTagColor", 12));
    }

    public IReadOnlyList<Net7MissionView> GetVisibleMissions(long currentClientTime)
    {
        List<Net7MissionView> missions = [];
        for (int i = 0; i < 12; i++)
        {
            Net7MissionView mission = ProjectMission(i, currentClientTime);
            if (!string.IsNullOrEmpty(mission.Name))
                missions.Add(mission);
        }
        missions.Sort(static (a, b) => b.StartTime.CompareTo(a.StartTime));
        return missions;
    }

    public Net7MissionView GetMission(int slotIndex, long currentClientTime)
    {
        if ((uint)slotIndex >= 12u)
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        return ProjectMission(slotIndex, currentClientTime);
    }

    public uint RequireForfeitMissionId(int slotIndex, bool confirmed, long currentClientTime)
    {
        if (!confirmed)
            throw new InvalidOperationException("Current mission forfeit requires explicit confirmation before 0x0086 emission.");
        Net7MissionView mission = GetMission(slotIndex, currentClientTime);
        if (!mission.CanForfeit)
            throw new InvalidOperationException("Mission is not currently eligible for forfeit.");
        // 005C66D0 sends the mission's +0x120 ID as stored; 00753DF0 initializes it to -1.
        return unchecked((uint)mission.InternalMissionId);
    }

    public uint RequireClearMissionId(int slotIndex, long currentClientTime)
    {
        Net7MissionView mission = GetMission(slotIndex, currentClientTime);
        // Current retail reaches Clear only from a selected visible
        // Mission Log row. A non-empty Name is the list-presence
        // criterion, so a terminal-looking hidden/default slot is not actionable.
        if (string.IsNullOrEmpty(mission.Name))
            throw new InvalidOperationException("Mission Clear requires a currently visible selected mission.");
        if (!mission.CanClear)
            throw new InvalidOperationException("Mission Clear is available only for complete, failed, or expired missions.");
        // 005C66D0 sends the stored +0x120 ID (constructor default -1) without a check.
        return unchecked((uint)mission.InternalMissionId);
    }

    /// <summary>
    /// Consume current mission change-state for presentation. The first updated Missions
    /// batch is intentionally silent; later batches use current retail's status precedence.
    /// This normalizes only client-local AuxData change-state, never authoritative values.
    /// </summary>
    public IReadOnlyList<Net7MissionNotification> ConsumeMissionNotifications(long currentClientTime)
    {
        // Schema nodes are retained and mutated in place. Bind once per schema,
        // then read the current change state; input queries must not scan the
        // complete player schema when no mission update is pending.
        var schema = _world.LocalPlayerAuxData;
        if (!ReferenceEquals(_missionSchema, schema))
        {
            _missionRoot = RequireProperty(schema, "LocalPlayerAuxData.Missions");
            _missionSchema = schema;
        }
        Net7AuxDataProperty missionsRoot = _missionRoot!;
        if (missionsRoot.State <= 1)
            return Array.Empty<Net7MissionNotification>();

        if (!_initialMissionBatchSeen)
        {
            _initialMissionBatchSeen = true;
            Net7AuxDataCodec.Normalize(missionsRoot);
            return Array.Empty<Net7MissionNotification>();
        }

        List<Net7MissionNotification> notifications = [];
        for (int slot = 0; slot < 12; slot++)
        {
            string prefix = $"LocalPlayerAuxData.Missions.{slot}";
            string name = ReadString(_world.LocalPlayerAuxData, prefix + ".Name");
            if (string.IsNullOrEmpty(name))
                continue;

            Net7AuxDataProperty complete = RequireProperty(_world.LocalPlayerAuxData, prefix + ".IsComplete");
            Net7AuxDataProperty expired = RequireProperty(_world.LocalPlayerAuxData, prefix + ".IsExpired");
            Net7AuxDataProperty failed = RequireProperty(_world.LocalPlayerAuxData, prefix + ".IsFailed");
            Net7AuxDataProperty stage = RequireProperty(_world.LocalPlayerAuxData, prefix + ".Stage");

            // 005A8350 emits stage completion before independently selecting terminal
            // status/current objective. The stage branch also takes sound precedence.
            List<Net7MessageLineUpdate> lines = [];
            string? sound = null;
            Net7MissionNotificationKind? kind = null;
            int stageNumber = ReadInt32(stage, 1);
            if (stage.State > 1)
            {
                if (stageNumber < 2)
                {
                    bool alreadyMessaged = ReadBoolean(_world.LocalPlayerAuxData, prefix + ".HasGivenNewMissionMessage", false);
                    kind = alreadyMessaged ? Net7MissionNotificationKind.Updated : Net7MissionNotificationKind.New;
                    sound = alreadyMessaged ? "Mission_Updated" : "Mission_New";
                }
                else
                {
                    kind = Net7MissionNotificationKind.StageCompleted;
                    Add(StripMissionMarkup(FormattableString.Invariant($"Completed Stage {stageNumber - 1} of {name}.")), 3);
                }
            }
            if (complete.State > 1 && ReadBoolean(complete, false))
            {
                kind = Net7MissionNotificationKind.Completed;
                Add(name + " Completed!", 3); sound ??= "Mission_Accomplished";
            }
            else if (expired.State > 1 && ReadBoolean(expired, false))
            {
                kind = Net7MissionNotificationKind.TimeExpired;
                Add(name + " Time Expired!", 3); sound ??= "Mission_Failed";
            }
            else if (failed.State > 1 && ReadBoolean(failed, false))
            {
                kind = Net7MissionNotificationKind.Failed;
                Add(name + " Failed!", 3); sound ??= "Mission_Failed";
            }
            else if (stage.State > 1)
            {
                int count = ReadInt32(_world.LocalPlayerAuxData, prefix + ".StageCount", 0);
                string heading = count == 0 ? FormattableString.Invariant($"({stageNumber}) ")
                    : FormattableString.Invariant($"({stageNumber}/{count}) ");
                // Invalid optional stage data must not retire the whole presentation.
                string text = stageNumber is >= 1 and <= 20
                    ? ReadString(_world.LocalPlayerAuxData, prefix + $".Stages.{stageNumber - 1}.Text") : "";
                Add(StripMissionMarkup(heading + text), 7);
                sound ??= "Mission_Updated";
            }
            if (kind is { } notificationKind)
                notifications.Add(new(slot, notificationKind, name) { Lines = lines, Sound = sound });
            void Add(string text, uint priority) => lines.Add(new(Net7MessageLineKind.Priority, text, "MessageLine", 5000, priority));
        }

        Net7AuxDataCodec.Normalize(missionsRoot);
        return notifications;
    }

    public static string StripMissionMarkup(string text)
    {
        // 005C69D0: remove bracketed spans only if at least one closing tag
        // was seen; an unmatched trailing span is then discarded as well.
        var output = new System.Text.StringBuilder(text.Length);
        bool inside = false, closed = false;
        foreach (char c in text)
        {
            if (inside) { if (c == '>') { inside = false; closed = true; } }
            else if (c == '<') inside = true;
            else output.Append(c);
        }
        return closed ? output.ToString() : text;
    }

    public Net7SkillView GetSkill(int skillIndex)
    {
        if ((uint)skillIndex >= 64u)
            throw new ArgumentOutOfRangeException(nameof(skillIndex));
        string prefix = $"LocalPlayerAuxData.RPGInfo.Skills.{skillIndex}";
        byte[] availability = ReadRaw(_world.LocalPlayerAuxData, prefix + ".Availability", 16);
        var parsedAvailability = new Net7SkillAvailability(
            BinaryPrimitives.ReadInt32LittleEndian(availability.AsSpan(0, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(availability.AsSpan(4, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(availability.AsSpan(8, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(availability.AsSpan(12, 4)));

        return new Net7SkillView(
            skillIndex,
            ReadInt32(_world.LocalPlayerAuxData, prefix + ".Level", 0),
            ReadInt32(_world.LocalPlayerAuxData, prefix + ".RecycleTime", 0),
            ReadTime(_world.LocalPlayerAuxData, prefix + ".LastActivation"),
            parsedAvailability,
            ReadInt32(_world.LocalPlayerAuxData, prefix + ".QuestOnlyLevels", 0),
            ReadInt32(_world.LocalPlayerAuxData, prefix + ".MaxSkillLevels", 0),
            ReadString(_world.LocalPlayerAuxData, prefix + ".Affiliations"),
            ReadInt32(_world.LocalPlayerAuxData, "LocalPlayerAuxData.RPGInfo.SkillPoints", 0));
    }

    public Net7SkillAbilityPresentation GetSkillAbilityPresentation(int abilityIndex, int skillIndex, uint currentClientTime)
    {
        if ((uint)abilityIndex >= 0xAAu)
            throw new ArgumentOutOfRangeException(nameof(abilityIndex));
        Net7SkillView skill = GetSkill(skillIndex);
        string prefix = $"LocalPlayerAuxData.RPGInfo.SkillAbilities.{abilityIndex}";
        uint? targetId = _world.CurrentTargetId;
        float? targetDistance = TryGetTargetDistance(targetId, currentClientTime);

        return new Net7SkillAbilityPresentation(
            abilityIndex,
            ReadBoolean(_world.LocalPlayerAuxData, prefix + ".HasAbility", false),
            ReadRawUInt32(_world.LocalPlayerAuxData, prefix + ".Usage"),
            ReadRawUInt32(_world.LocalPlayerAuxData, prefix + ".Targets"),
            ReadInt32(_world.LocalPlayerAuxData, prefix + ".Range", 0),
            ReadInt32(_world.LocalPlayerAuxData, prefix + ".Radius", 0),
            ReadRawUInt32(_world.LocalPlayerAuxData, prefix + ".Validity"),
            ReadBoolean(_world.LocalPlayerAuxData, prefix + ".CanBeUsedWhileIncapacitated", false),
            targetId,
            targetDistance,
            skill.RechargeRemaining(currentClientTime));
    }

    public IReadOnlyList<Net7BuffPresentation> GetBuffPresentation(long currentClientTime)
    {
        if (_world.LocalShipObjectId is not uint playerId)
            return Array.Empty<Net7BuffPresentation>();
        Net7AuxDataSchema? ship = _world.Objects.FindFirst(playerId)?.AuxData;
        return GetBuffPresentation(ship, currentClientTime);
    }

    public static IReadOnlyList<Net7BuffPresentation> GetBuffPresentation(Net7AuxDataSchema? ship, long currentClientTime)
    {
        if (ship?.Name != "ShipAuxData")
            return Array.Empty<Net7BuffPresentation>();

        List<Net7BuffPresentation> result = [];
        for (int i = 0; i < 15; i++)
        {
            string prefix = $"ShipAuxData.BuffArray.{i}";
            string buffType = ReadString(ship, prefix + ".BuffType");
            if (string.IsNullOrEmpty(buffType))
                continue;

            string scrub = ReadString(ship, prefix + ".ScrubTypeName");
            bool permanent = ReadBoolean(ship, prefix + ".IsPermanent", false);
            long removal = ReadTime(ship, prefix + ".BuffRemovalTime");
            var elements = new List<Net7BuffElementPresentation>(5);
            for (int elementIndex = 0; elementIndex < 5; elementIndex++)
            {
                string elementPrefix = $"{prefix}.Elements.{elementIndex}";
                var element = new Net7BuffElementPresentation(
                    ReadString(ship, elementPrefix + ".SourceEntity"),
                    ReadString(ship, elementPrefix + ".SourceObject"),
                    ReadInt32(ship, elementPrefix + ".Magnitude", 0),
                    ReadBoolean(ship, elementPrefix + ".IsActive", false),
                    ReadTime(ship, elementPrefix + ".ExpirationTime"));

                // Retail retains all five authoritative AuxData elements,
                // but only non-empty SourceObject records enter the widget's
                // displayed element collection.
                if (!string.IsNullOrEmpty(element.SourceObject))
                    elements.Add(element);
            }

            elements.Sort(CompareBuffElements);
            // Zero and negative removal times are expired for a
            // non-permanent buff. Equality with current client time is not
            // yet earlier and remains visible until time advances.
            bool visible = permanent || (removal > 0 && removal >= currentClientTime);
            result.Add(new(i, buffType, scrub, permanent, removal, visible, elements.AsReadOnly()));
        }
        return result;
    }

    public Net7NavigationSemantics? GetNavigationSemantics(uint gameId)
    {
        Net7NavigationUpdate? nav = _world.Objects.FindFirst(gameId)?.Navigation;
        return nav is null ? null : new(nav.GameId, nav.Signature, nav.PlayerHasVisited, nav.NavType, nav.IsHuge);
    }

    public bool SelectWarpRoute(IReadOnlyList<uint> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Count == 0)
            return false;
        _selectedWarpRoute = Array.AsReadOnly(route.ToArray());
        return true;
    }

    public IReadOnlyList<uint> RequireSelectedWarpRoute()
        => _selectedWarpRoute ?? throw new InvalidOperationException("Start Warp requires a previously selected/built navigation route.");

    public void ClearWarpRoute() => _selectedWarpRoute = null;

    public ulong GetAuthoritativeMoney()
    {
        Net7AuxDataProperty property = _world.LocalPlayerAuxData.Resolve(Net7AuxDataPropertyIds.LocalPlayerMoney);
        if (property.State == 0 || property.Value is null)
            return 0;
        if (property.Value is ulong value)
            return value;
        throw new InvalidDataException("LocalPlayerAuxData.Money is not a materialized UInt64 value.");
    }

    public string GetAffiliation()
        => ReadString(_world.LocalPlayerAuxData.Resolve(Net7AuxDataPropertyIds.LocalPlayerReputationAffiliation));

    public IReadOnlyList<Net7FactionStanding> GetFactionStandings()
    {
        var result = new List<Net7FactionStanding>(Net7AuxDataPropertyIds.LocalPlayerReputationFactionCount);
        for (int i = 0; i < Net7AuxDataPropertyIds.LocalPlayerReputationFactionCount; i++)
        {
            int offset = i * Net7AuxDataPropertyIds.LocalPlayerReputationFactionStride;
            string name = ReadString(_world.LocalPlayerAuxData.Resolve(Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Name + offset));
            if (string.IsNullOrEmpty(name))
                continue;
            result.Add(new Net7FactionStanding(
                name,
                ReadFloat(_world.LocalPlayerAuxData.Resolve(Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Reaction + offset), 0f),
                ReadInt32(_world.LocalPlayerAuxData.Resolve(Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Order + offset), 0)));
        }
        return result.AsReadOnly();
    }

    public Net7FactionStanding? GetStandingForObject(uint gameId)
    {
        Net7AuxDataSchema? ship = _world.Objects.FindFirst(gameId)?.AuxData;
        if (ship?.Name != "ShipAuxData")
            return null;
        string factionKey = ReadString(ship.Resolve(Net7AuxDataPropertyIds.ShipFactionIdentifier));
        if (string.IsNullOrEmpty(factionKey))
            return null;

        foreach (Net7FactionStanding standing in GetFactionStandings())
        {
            if (string.Equals(standing.FactionKey, factionKey, StringComparison.Ordinal))
                return standing;
        }
        return null;
    }

    public void BeginTransition(Net7TransitionKind kind, uint requestedTargetId)
    {
        if (requestedTargetId == 0)
            throw new ArgumentOutOfRangeException(nameof(requestedTargetId));
        _transitionRequest = new(kind, requestedTargetId);
        TransitionRequestRevision++;
    }

    public void ObserveServerTransitionTarget(Net7TransitionKind kind, uint targetId)
    {
        // 00739FF0 writes independent SClient targets (+13AC/+13BC),
        // regardless of which local request was most recently issued.
        if (kind == Net7TransitionKind.Dock) _dockTransitionTarget = targetId;
        else if (kind == Net7TransitionKind.Gate) _gateTransitionTarget = targetId;
    }

    public uint RequireCommitTarget(Net7TransitionKind kind)
    {
        // 007405E0 initializes these fields to zero; 007992F0/007993E0
        // consume them using only the -1 sentinel, not request ownership.
        if (kind == Net7TransitionKind.Dock) return _dockTransitionTarget;
        if (kind == Net7TransitionKind.Gate) return _gateTransitionTarget;
        if (_transitionRequest is not Net7TransitionRequestState request || request.Kind != kind)
            throw new InvalidOperationException($"No matching {kind} request is pending.");
        return request.RequestedTargetId;
    }

    public void CompleteTransition(Net7TransitionKind kind)
    {
        if (kind == Net7TransitionKind.Dock) _dockTransitionTarget = uint.MaxValue;
        else if (kind == Net7TransitionKind.Gate) _gateTransitionTarget = uint.MaxValue;
        if (_transitionRequest?.Kind == kind) _transitionRequest = null;
    }

    public void ObserveStarbaseSet(Net7StarbaseSetUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        // Current Packet_004F processing branches on Action first: Action 0
        // materializes/opens the supplied StarbaseID, while Action 1 closes
        // the matching local starbase. ExitMode only selects the local close
        // presentation mode and does not decide whether the starbase is open.
        // 74B680 tests the existing view pointer before assigning its ID.
        // A second open cannot replace the identity of the retained station.
        if (update.Action == 0 && ActiveStarbaseId is null)
        {
            ActiveStarbaseId = update.StarbaseId;
            IsAvatarViewActive = true;
        }
        else if (update.Action == 1 && ActiveStarbaseId == update.StarbaseId)
        {
            ActiveStarbaseId = null;
            IsAvatarViewActive = false;
        }
    }

    public void ObserveStarbaseTow(Net7StarbaseTowUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        // Current StarbaseTowPacketProcess always feeds the first packet DWORD
        // to SClientOpenOrRequestStarbase(..., false, id). The remaining two
        // IDs/costs are retained presentation data, not active-starbase identity.
        if (update.Options.Count == 0)
            return;
        uint starbaseId = update.Options[0].StarbaseId;
        if (ActiveStarbaseId is null)
        {
            ActiveStarbaseId = starbaseId;
            IsAvatarViewActive = true;
        }
    }

    public uint RequireActiveStarbaseId()
        => ActiveStarbaseId ?? throw new InvalidOperationException("Leave Starbase requires the current server-materialized active StarbaseID.");

    public void ResetForSectorBoundary()
    {
        // 00741E7B retires the line-display owner; 005A6500 resets its initial-batch latch.
        _initialMissionBatchSeen = false;
        _selectedWarpRoute = null;
        _transitionRequest = null;
        ActiveStarbaseId = null;
        IsAvatarViewActive = false;
    }

    public void ResetForCharacterSelection()
    {
        ResetForSectorBoundary();
        _initialMissionBatchSeen = false;
    }

    private Net7MissionView ProjectMission(int slotIndex, long currentClientTime)
    {
        string prefix = $"LocalPlayerAuxData.Missions.{slotIndex}";
        int stage = ReadInt32(_world.LocalPlayerAuxData, prefix + ".Stage", 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(stage, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stage, 20);

        int stageCount = ReadInt32(_world.LocalPlayerAuxData, prefix + ".StageCount", 1);
        int stageIndex = stage - 1;
        string stageText = ReadString(_world.LocalPlayerAuxData, prefix + $".Stages.{stageIndex}.Text");
        bool stageTimed = ReadBoolean(_world.LocalPlayerAuxData, prefix + $".Stages.{stageIndex}.IsTimed", false);
        bool missionTimed = ReadBoolean(_world.LocalPlayerAuxData, prefix + ".IsTimed", true);
        long expiry = stageTimed
            ? ReadTime(_world.LocalPlayerAuxData, prefix + ".StageExpirationTime")
            : missionTimed ? ReadTime(_world.LocalPlayerAuxData, prefix + ".ExpirationTime") : 0;
        long remaining = expiry - currentClientTime;

        return new Net7MissionView(
            slotIndex,
            ReadInt32(_world.LocalPlayerAuxData, prefix + ".ID", -1),
            ReadString(_world.LocalPlayerAuxData, prefix + ".Name"),
            ReadString(_world.LocalPlayerAuxData, prefix + ".Summary"),
            ReadString(_world.LocalPlayerAuxData, prefix + ".Reward"),
            ReadString(_world.LocalPlayerAuxData, prefix + ".FailureConsequence"),
            ReadString(_world.LocalPlayerAuxData, prefix + ".IssuingFaction"),
            ReadInt32(_world.LocalPlayerAuxData, prefix + ".StartTime", 0),
            stage,
            stageCount,
            stageText,
            missionTimed,
            ReadBoolean(_world.LocalPlayerAuxData, prefix + ".IsForfeitable", true),
            ReadBoolean(_world.LocalPlayerAuxData, prefix + ".IsComplete", false),
            ReadBoolean(_world.LocalPlayerAuxData, prefix + ".IsFailed", false),
            ReadBoolean(_world.LocalPlayerAuxData, prefix + ".IsExpired", false),
            expiry != 0 && remaining > 0 ? remaining : null);
    }

    private float? TryGetTargetDistance(uint? targetId, uint currentClientTime)
    {
        if (targetId is not uint target || _world.LocalShipObjectId is not uint local)
            return null;
        if (!_world.TrySampleObject(local, currentClientTime, out Net7SpatialSample localSample)
            || !_world.TrySampleObject(target, currentClientTime, out Net7SpatialSample targetSample))
            return null;
        return Vector3.Distance(localSample.Position, targetSample.Position);
    }

    private static int CompareBuffElements(Net7BuffElementPresentation left, Net7BuffElementPresentation right)
    {
        int result = right.IsActive.CompareTo(left.IsActive);
        if (result != 0)
            return result;
        result = right.Magnitude.CompareTo(left.Magnitude);
        if (result != 0)
            return result;
        result = right.ExpirationTime.CompareTo(left.ExpirationTime);
        if (result != 0)
            return result;
        result = StringComparer.OrdinalIgnoreCase.Compare(left.SourceEntity, right.SourceEntity);
        if (result != 0)
            return result;
        return StringComparer.OrdinalIgnoreCase.Compare(left.SourceObject, right.SourceObject);
    }

    private static Net7AuxDataProperty RequireProperty(Net7AuxDataSchema schema, string path)
        => schema.Properties.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"AuxData schema '{schema.Name}' is missing expected current property '{path}'.");

    private static int ReadInt32(Net7AuxDataSchema schema, string path, int fallback)
        => ReadInt32(RequireProperty(schema, path), fallback);

    private static int ReadInt32(Net7AuxDataProperty property, int fallback)
        => property.State != 0 && property.Value is int value ? value : fallback;

    private static bool ReadBoolean(Net7AuxDataSchema schema, string path, bool fallback)
        => ReadBoolean(RequireProperty(schema, path), fallback);

    private static bool ReadBoolean(Net7AuxDataProperty property, bool fallback)
        => property.State != 0 && property.Value is byte value ? value != 0 : fallback;

    private static string ReadString(Net7AuxDataSchema schema, string path)
        => ReadString(RequireProperty(schema, path));

    private static string ReadString(Net7AuxDataProperty property)
        => property.State != 0 && property.Value is Net7AuxDataString value ? value.Text : string.Empty;

    private static float ReadFloat(Net7AuxDataSchema schema, string path, float fallback)
        => ReadFloat(RequireProperty(schema, path), fallback);

    private static float ReadFloat(Net7AuxDataProperty property, float fallback)
        => property.State != 0 && property.Value is float value ? value : fallback;

    private static long ReadTime(Net7AuxDataSchema schema, string path)
    {
        Net7AuxDataProperty property = RequireProperty(schema, path);
        return property.State != 0 && property.Value is Net7AuxDataTime value ? value.RelativeValue : 0;
    }

    private static byte[] ReadRaw(Net7AuxDataSchema schema, string path, int requiredLength)
    {
        Net7AuxDataProperty property = RequireProperty(schema, path);
        if (property.State == 0 || property.Value is null)
            return new byte[requiredLength];
        if (property.Value is not byte[] bytes || bytes.Length != requiredLength)
            throw new InvalidDataException($"AuxData property '{path}' is not a {requiredLength}-byte current raw value.");
        return bytes;
    }

    private static uint ReadRawUInt32(Net7AuxDataSchema schema, string path)
    {
        byte[] bytes = ReadRaw(schema, path, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static int ReadRawInt32(Net7AuxDataSchema schema, string path, int fallback)
    {
        Net7AuxDataProperty property = RequireProperty(schema, path);
        if (property.State == 0 || property.Value is null)
            return fallback;
        if (property.Value is not byte[] bytes || bytes.Length != 4)
            throw new InvalidDataException($"AuxData property '{path}' is not a 4-byte current raw value.");
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }
}
