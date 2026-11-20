using System.Numerics;
using EnbNext.Net7.Presentation;

namespace EnbNext.Net7.Gameplay;

public enum Net7PlayerAction : int
{
    Tractor = 0x01,
    MessageNonAvatar = 0x02,
    Dock = 0x07,
    Landing = 0x08,
    FireAllWeapons = 0x09,
    GroupInvite = 0x0A,
    GroupAccept = 0x0B,
    GroupRefuse = 0x0C,
    GroupDisband = 0x0D,
    GroupRemoveOrLeaveSelf = 0x0E,
    GroupKick = 0x0F,
    GroupFinder = 0x10,
    Prospect = 0x11,
    GateRequest = 0x12,
    Gate = 0x13,
    TradeRequest = 0x14,
    TradeConfirm = 0x15,
    TradeCancel = 0x16,
    TradeSync = 0x17,
    TradeCredits = 0x18,
    Register = 0x19,
    Jumpstart = 0x1A,
    CameraControllerSelfAction = 0x1B,
    DockRequest = 0x1C,
    LandRequest = 0x1D,
    Scan = 0x1E,
}

/// <summary>Current retail-compatible 0x002C Action payload.</summary>
public sealed record Net7ActionCommand(uint GameId, int Action, uint Target, uint OptionalVar);

/// <summary>Current retail-compatible 0x0092 CameraControl payload.</summary>
public sealed record Net7CameraControl(uint Message, uint GameId);

/// <summary>Current retail-compatible 0x004E Starbase Request payload.</summary>
public sealed record Net7StarbaseRequest(uint PlayerId, uint StarbaseId, byte Action);

/// <summary>Current retail-compatible 0x0057 SkillAction payload.</summary>
public sealed record Net7SkillActionCommand(uint GameId, uint Action, uint SkillId);

/// <summary>Current retail-compatible 0x0058 SkillAbility payload.</summary>
public sealed record Net7SkillAbilityCommand(uint GameId, uint Action, uint AbilityIndex);

/// <summary>Current retail-compatible 0x005D UseInventoryItem payload.</summary>
public sealed record Net7UseInventoryItemRequest(uint TargetGameId, byte Inventory, byte Slot);

public enum Net7ManufacturingMode : uint
{
    None = 0,
    Manufacture = 1,
    Analyze = 2,
    Refining = 4,
}

/// <summary>Current retail-compatible 0x0079 manufacturing-family mode request payload.</summary>
public sealed record Net7ManufacturingModeRequest(uint PlayerId, uint Mode);

/// <summary>Current retail-compatible 0x007A final manufacturing-subcategory request payload.</summary>
public sealed record Net7ManufacturingCategoryRequest(uint PlayerId, uint SelectedSubcategoryId);

public enum Net7ChatCommandType : byte
{
    Group = 1,
    Guild = 2,
    LocalArea = 3,
    EntireSector = 4,
}

public enum Net7LogoffDestination : byte
{
    Desktop = 1,
    LoginScreen = 2,
    CharacterSelection = 3,
}

public static class Net7LogoffSemantics
{
    public static int ToRetailTransitionState(Net7LogoffDestination destination)
        => destination switch
        {
            Net7LogoffDestination.Desktop => 3,
            Net7LogoffDestination.LoginScreen => 1,
            Net7LogoffDestination.CharacterSelection => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(destination), destination, "Unsupported graceful-logoff destination."),
        };
}

/// <summary>One Submit-generated PlayerNLP record used by the chat stream.</summary>
public sealed record Net7PlayerNlpRecord(byte RecordType, byte RecordArg, string? TextPayload = null);

// Null receipt means unobserved; null ordinal inside a receipt means absent.
public sealed record Net7EffectEndpointReceipt(ulong? SourceCreationOrdinal, ulong? TargetCreationOrdinal);
public readonly record struct Net7EffectPoseKey(Guid Context, uint EffectId, ulong Generation);
// For Line/ParticleJet, Position is the retained segment start; the getter
// projects the packet source context onto [Position, BinaryEndpoint]. Other
// admitted classes publish their getter position directly.
public readonly record struct Net7RetainedEffectPosition(Vector3 Position, Vector3? BinaryEndpoint = null)
{
    // Shared immutable caller context from the same production frame as this target.
    public System.Collections.Immutable.ImmutableDictionary<(uint GameId, ulong CreationOrdinal), Net7EffectSourceControllerPose>? SourceControllers { get; init; }
}

public sealed record Net7EffectState(
    uint EffectId,
    uint SourceId,
    uint TargetId,
    ushort DefinitionId,
    uint Timestamp,
    int DurationMilliseconds,
    string BoneName,
    ushort Mask,
    bool IsObjectToObject,
    float Scale = 1.0f,
    Vector3 SpatialOffset = default,
    ushort EndpointShortA = 0,
    ushort EndpointShortB = 0,
    bool DescriptorFlag = false,
    Vector3 EffectOffset = default,
    float EffectScalar = 1.0f,
    Net7EffectPresentationResolution? Presentation = null,
    Net7EffectGeometryKind GeometryKind = Net7EffectGeometryKind.Object,
    Vector3 WorldPoint = default,
    short DurationLink = 0,
    Vector3 HsvShift = default,
    short AuxiliaryEffectFieldA = 0,
    short AuxiliaryEffectFieldB = 0,
    ulong InstanceGeneration = 0)
{
    public bool HasInfiniteDuration => DurationMilliseconds < 0;
    // Native tractor-extension ownership, not an ordinary packet flag.
    public bool EndWithTarget { get; init; }
    public Net7EffectEndpointReceipt? EndpointReceipt { get; init; }
    public ulong? TargetEffectGeneration { get; init; }
    public Vector3? CapturedTargetEffectPoint { get; init; }
    public Guid PoseContext { get; init; }
}

/// <param name="Channel">The named chat channel a channel line belongs to (public or private channels, types 9-15); null otherwise.</param>
public sealed record Net7MessageState(byte Type, string Text, uint? ShipId = null, string? RoutingIdentity = null, string? DisplayIdentity = null, string? Channel = null)
{
    public long Sequence { get; init; }
}

public sealed record Net7TradeState(uint PartnerId, byte Type);

public sealed record Net7ManufacturingState(uint ActiveObjectId, Net7ManufacturingMode RequestedMode);

public sealed record Net7GuildPrompt(ushort Opcode, uint Type, string First, string Second);
public sealed record Net7GuildPermissions(uint Mask, uint MaxPromoteRank, uint MaxRemoveRank, uint MinDemoteRank);
public sealed record Net7GuildRankName(string Name, uint Rank);
public sealed record Net7GuildMessage(uint MessageType, string OtherName, string GuildName);

public sealed record Net7ProspectExtension(uint SourceId, uint TargetId, uint EffectId, uint Timestamp, uint DurationMilliseconds);

public sealed record Net7TractorExtension(
    ushort Opcode,
    uint SourceId,
    uint BassetId,
    uint EffectId,
    ushort BaseAsset,
    uint Timestamp,
    string Name,
    uint DurationMilliseconds,
    float TractorSpeed,
    Vector3 Position);

public sealed record Net7WarpCompletionExtension(
    Vector3 Position,
    Quaternion Orientation,
    uint MovementId,
    uint Timestamp);

public sealed record Net7CDataRecord(uint RecordId, byte[] Payload);

public sealed record Net7PositionFeedbackSample(
    Vector3 Position,
    Vector3 OrientationColumn0,
    int LegacyPositionFeedbackSpeed);

public sealed record Net7HullUpgradeNotice(uint ShipId, uint HullUpgradeLevel);
public sealed record Net7DamagePresentation(float Damage, float Modifier, uint Type, uint Inflicted, uint SourceId, uint VictimId);
public sealed record Net7IncapacitationDetails(string FirstOuterString, byte[] AvatarData, IReadOnlyList<string> RemainingOuterStrings);
