using System.Collections.Immutable;
using System.Numerics;

namespace EnbNext.Presentation;

public enum EnbSceneSpatialProviderKind
{
    // NullSpatialStateProvider (vtable 00B006B8), installed by 6F2820 until a
    // positional packet replaces it; its virtual+4 (6F2E30) returns 0.
    Null = 0,
    Fixed = 1,
    Simple = 3,
    AdvancedLocal = 4,
    AdvancedRemote = 8,
    Component = 11,
    Planet = 12,
}

public enum EnbPresentationBaseAssetKind
{
    Missing,
    NullObject,
    Rsl,
    FileName,
    ProceduralWidth,
    Unsupported,
    NoRenderObject,
}

public sealed record EnbPresentationProvenance(
    string Path,
    string? Section = null,
    string? Key = null,
    string? ResourceName = null,
    string? ArchiveName = null);

public sealed record EnbPresentationProperty(string Key, string Value);

public sealed record EnbPresentationBaseAssetReference(
    int Selector,
    EnbPresentationBaseAssetKind Kind,
    bool IsCatalogEntry,
    int? RslId,
    int? LinkId,
    int? RsId,
    string? File,
    string? Name,
    float? Width,
    bool OverbrightOff,
    ImmutableArray<EnbPresentationProperty> Properties,
    EnbPresentationProvenance Provenance);

public sealed record EnbPresentationClientBaseAssetReference(
    int Selector,
    float Radius,
    float RadiusAdjust,
    EnbPresentationProvenance Provenance);

public sealed record EnbSceneTransformSnapshot(
    uint ClientTime,
    Vector3 Position,
    Quaternion Orientation,
    Vector3 Velocity,
    float Speed,
    Vector3 Rotation,
    Vector3 OrientationColumn0) : IEnbRenderTransform
{
    public uint ClientTime {get;init;}=ClientTime;
    public Vector3 Position {get;init;}=Position;
    public Quaternion Orientation {get;init;}=Orientation;
    public Vector3 Velocity {get;init;}=Velocity;
    public float Speed {get;init;}=Speed;
    public Vector3 Rotation {get;init;}=Rotation;
    public Vector3 OrientationColumn0 {get;init;}=OrientationColumn0;
    // Temporary providers can supply a matrix directly (retail spatial +08).
    // Preserve it through the RenderObject handoff instead of round-tripping a quaternion.
    public Matrix4x4? ProviderTransform { get; init; }
    public float BankHeadingTarget { get; init; }
    // Populated only on the renderer's private compatibility projection. The
    // binding validates its exact publication identity before being consumed.
    [System.Text.Json.Serialization.JsonIgnore]
    public object? RendererTransformState { get; init; }
}

public sealed record EnbScenePlanetSnapshot(
    uint ServerTimestamp,
    Vector3 SpatialVector,
    uint OrbitId,
    float OrbitDistance,
    float OrbitAngle,
    float OrbitRate,
    float RotationAngle,
    float RotationRate,
    float TiltAngle);

/// <summary>
/// Detached copy of the raw 0x003E controls that own current-retail advanced
/// spatial evolution. This is diagnostic evidence; render consumers continue
/// to use the sampled transform above.
/// </summary>
public sealed record EnbSceneAdvancedSpatialSnapshot(
    ushort OptionalMask,
    uint MovementId,
    float CurrentSpeed,
    float SetSpeed,
    float Acceleration,
    float RotY,
    float DesiredY,
    float RotZ,
    float DesiredZ,
    Vector3 ImpartedVelocity,
    float ImpartedSpin,
    float ImpartedRoll,
    float ImpartedPitch,
    uint UpdatePeriod);

public sealed record EnbSceneSpatialSnapshot(
    EnbSceneSpatialProviderKind ProviderKind,
    EnbSceneTransformSnapshot Transform,
    EnbScenePlanetSnapshot? Planet) : IEnbRenderSpatial
{
    IEnbRenderTransform IEnbRenderSpatial.Transform=>Transform;
    public EnbSceneAdvancedSpatialSnapshot? Advanced { get; init; }
    public uint? FormationLeaderId { get; init; }
    public string ModelAudit { get; init; } = "";
    public uint? PacketTime { get; init; }
    public uint? ReceiptTime { get; init; }
    public uint TransitionMilliseconds { get; init; }
}

public sealed record EnbSceneNavigationSnapshot(
    float Signature,
    bool PlayerHasVisited,
    uint NavType,
    bool IsHuge);

public sealed record EnbSceneRadarSnapshot(
    bool AppearsInRadar,
    float RadarRange,
    EnbSceneNavigationSnapshot? Navigation);

public sealed record EnbSceneDecalSnapshot(
    uint Field0,
    uint Field1,
    ImmutableArray<byte> Data);

public sealed record EnbSceneDamageDecalSnapshot(int State, ulong Revision,
    Vector3 Direction, int AssetId, float Scale);

public sealed record EnbSceneDamageDecalPoolSnapshot(int State, ulong Revision,
    ImmutableArray<EnbSceneDamageDecalSnapshot> Slots);

public sealed record EnbSceneColorizationSnapshot(
    uint Field,
    ImmutableArray<byte> Vector);

public sealed record EnbSceneRenderStateLinkSnapshot(
    uint RenderStateId,
    uint LinkId);

public sealed record EnbSceneRenderStateActionSnapshot(
    ushort Opcode,
    uint RenderStateId,
    uint ClientTime = 0,
    uint? InitializerArgument = null);

public sealed record EnbSceneAttachmentSnapshot(
    uint ChildId,
    uint AttachmentSelector);

public sealed record EnbSceneNameDecalSnapshot(
    string Name,
    ImmutableArray<byte> RawName,
    Vector3 Rgb);

public sealed record EnbSceneSubPartSnapshot(
    string Name,
    uint PartValue)
{
    public ulong MaterializationRevision { get; init; }
}

public sealed record EnbSceneShipLegoAttachmentSnapshot(
    int Ordinal,
    string BoneName,
    int Type,
    int BaseAsset,
    string Data)
{
    public ulong MaterializationRevision { get; init; }
}

public sealed record EnbSceneShipLegoSnapshot(
    float Scale,
    ImmutableArray<EnbSceneShipLegoAttachmentSnapshot> Attachments);

public sealed record EnbSceneShipInventoryMountSnapshot(
    int Ordinal,
    string BoneName,
    bool MountDataMaterialized);

public sealed record EnbSceneShipEquippedSlotSnapshot(
    int Ordinal,
    int ItemTemplateId);

/// <summary>
/// Detached current-retail ShipAuxData presentation evidence used.
/// Inventory fields are diagnostic-only until a live-world consumer is proven;
/// IsOrganic participates in the proven fixed ship-visual eligibility test, while
/// the retained thrust/warp values drive retail's exhaust-bone visibility gate.
/// </summary>
public sealed record EnbSceneShipPresentationSnapshot(
    bool? IsOrganic,
    int? PrivateWarpState,
    int? GlobalWarpState,
    int? EngineThrustState,
    bool InventoryMaterialized,
    string? EquipMountModel,
    int MaterializedMountCount,
    ImmutableArray<EnbSceneShipInventoryMountSnapshot> Mounts,
    ImmutableArray<EnbSceneShipEquippedSlotSnapshot> EquippedSlots)
{
    /// <summary>Retained ShipAuxData 1922; selects retail engine audio independently of the ship model.</summary>
    public int? EngineTrailType { get; init; }
    public int WarpPowerLevel { get; init; } = 1;
    public int CachedWarpState { get; init; }
    public int? EngineWarpState { get; init; }
    public ulong PrivateWarpRevision { get; init; }
    public ulong GlobalWarpRevision { get; init; }
    public bool PrivateWarpMaterialized { get; init; }
    public bool GlobalWarpMaterialized { get; init; }
    public bool GlobalWarpUpdatesSuppressed { get; init; }
    public ImmutableArray<EnbSceneWarpPresentationChange> WarpChanges { get; init; } = [];
    public ImmutableArray<EnbSceneWarpPresentationChange> PrivateWarpChanges { get; init; } = [];
    public EnbSceneShakeSnapshot Shake { get; init; } = new(0, null, 0, null);
    public ImmutableArray<float> VisibleQuadrantHealth { get; init; } = [];
}

// 5129D0 -> 6E2CC0 samples final Aux values once after the packet drain.
public sealed record EnbSceneShakeSnapshot(ulong ForceRevision, Vector3? Force, ulong DamageRevision, float? Damage);

public sealed record EnbSceneWarpPresentationChange(ulong Sequence, int State, int PowerLevel, int EngineTrailType);

/// <summary>
/// Detached ShipAuxData fields consumed by current retail's gutter player-name
/// and guild-tag paths (FUN_006FF7A0, FUN_006FFFF0 and FUN_00700670).
/// </summary>
public sealed record EnbScenePlayerPresentationSnapshot(
    string? Owner,
    string? Title,
    string? Rank,
    int? CombatLevel,
    float? ShieldPercent,
    string? GuildName,
    int GuildRank,
    string? GuildRankName,
    Vector3? SameGuildTagColor,
    Vector3? OtherGuildTagColor)
{
    public bool? IsInPvp { get; init; }
    public string? FactionIdentifier { get; init; }
    public float? FactionReaction { get; init; }
}

/// <summary>
/// Detached values consumed by Retail's scanner health controller
/// (UpdateShipHealthHud @ 0x005DC4A0).
/// </summary>
public sealed record EnbSceneTargetVitalsSnapshot(
    float? ShieldPercent,
    float? MaximumShieldPower,
    float? HullPoints,
    float? MaximumHullPoints) : IEnbVitalsFrame;

public sealed record EnbSceneHarvestableResourceSnapshot(
    int Slot,
    int ItemTemplateId,
    int StackCount,
    string? Name);

public sealed record EnbSceneAuxDataMaterializationAuditSnapshot(
    int AttemptCount,
    int LargestDeltaLength,
    byte LargestSelector,
    bool LargestAccepted,
    string? LargestError)
{
    public bool LargestRetailCompatiblePartial { get; init; }
}

public sealed record EnbSceneColorizationApplicationAuditSnapshot(
    int ApplyCount,
    int ActiveShipLegoAttachmentsAtLastApply,
    int ActiveSubPartsAtLastApply,
    int ObjectAuxDataAttemptsAtLastApply,
    ImmutableArray<EnbSceneColorizedShipLegoScopeEntry> ShipLegoScope,
    ImmutableArray<EnbSceneColorizedSubPartScopeEntry> SubPartScope);

public sealed record EnbSceneColorizedShipLegoScopeEntry(
    int Ordinal,
    string BoneName,
    int Type,
    int BaseAsset,
    ulong MaterializationRevision);

public sealed record EnbSceneColorizedSubPartScopeEntry(
    int Ordinal,
    string Name,
    uint PartValue,
    ulong MaterializationRevision);

public sealed record EnbSceneAvatarSnapshot(
    string FirstName,
    string LastName,
    uint AvatarType,
    byte UnknownU8,
    byte AvatarVersion,
    uint Race,
    uint Profession,
    uint Gender,
    uint MoodType,
    byte Personality,
    byte Nlp,
    byte BodyType,
    byte PantsType,
    byte HeadType,
    byte HairNum,
    byte EarNum,
    byte GoggleNum,
    byte BeardNum,
    byte WeaponHipNum,
    byte WeaponUniqueNum,
    byte WeaponBackNum,
    byte HeadTextureNum,
    byte TattooTextureNum,
    ImmutableArray<float> TattooOffset,
    ImmutableArray<float> HairColor,
    ImmutableArray<float> BeardColor,
    ImmutableArray<float> EyeColor,
    ImmutableArray<float> SkinColor,
    ImmutableArray<float> ShirtPrimaryColor,
    ImmutableArray<float> ShirtSecondaryColor,
    ImmutableArray<float> PantsPrimaryColor,
    ImmutableArray<float> PantsSecondaryColor,
    uint ShirtPrimaryMetal,
    uint ShirtSecondaryMetal,
    uint PantsPrimaryMetal,
    uint PantsSecondaryMetal,
    string Skeleton,
    ImmutableArray<uint> BodyWeight,
    ImmutableArray<uint> HeadWeight);

public sealed record EnbSceneCreatedBaseAsset(int BaseAsset,float Scale,float HsvH,float HsvS,float HsvV);

public sealed record EnbSceneObjectSnapshot(
    ulong RenderIdentity,
    ulong VisualGeneration,
    uint GameId,
    bool IsLocalShip,
    bool IsCurrentTarget,
    byte ObjectType,
    int BaseAsset,
    float Scale,
    float HsvH,
    float HsvS,
    float HsvV,
    uint RelationshipRaw,
    bool IsAttacking,
    string? Name,
    EnbSceneRadarSnapshot Radar,
    EnbSceneSpatialSnapshot? Spatial,
    EnbPresentationBaseAssetReference? BaseAssetReference,
    EnbPresentationClientBaseAssetReference? ClientBaseAssetReference,
    ImmutableArray<EnbSceneDecalSnapshot> Decals,
    ImmutableArray<EnbSceneColorizationSnapshot> ColorizationEntries,
    ImmutableArray<EnbSceneRenderStateLinkSnapshot> RenderStateLinks,
    ImmutableArray<uint> ActiveRenderStates,
    ImmutableArray<EnbSceneRenderStateActionSnapshot> RenderStateActions,
    ImmutableArray<EnbSceneAttachmentSnapshot> Attachments,
    EnbSceneNameDecalSnapshot? NameDecal,
    ImmutableArray<EnbSceneSubPartSnapshot> SubParts,
    EnbSceneAvatarSnapshot? Avatar,
    uint SpecialTargetEndpointId,
    float SpecialTargetTransition) : IEnbRenderObject
{
    IEnbRenderSpatial? IEnbRenderObject.Spatial=>Spatial;
    public EnbSceneSpatialSnapshot? Spatial {get;init;}=Spatial;
    public float? HarvestablePercentFull { get; init; }
    // Gameplay identity provenance; synthetic snapshots may leave it unobserved.
    public ulong? WorldCreationOrdinal { get; init; }
    public ulong SpeakerRevision { get; init; }
    public byte SpeakerType { get; init; }
    public EnbNext.Assets.EnbRgb? SpeakerColor { get; init; }
    public EnbNext.Assets.EnbRgb? SpeakingColor { get; init; }
    //The type0A helper clones the creation model before later0026 updates.
    public EnbSceneCreatedBaseAsset? CreatedBaseAsset { get; init; }
    public int? HarvestableTechLevel { get; init; }

    /// <summary>
    /// Current-retail ShipAuxData.Legos materialization. The object snapshot
    /// keeps this detached from the mutable AuxData tree so the render thread
    /// can rebuild the ship's Lego attachment controller deterministically.
    /// </summary>
    public EnbSceneShipLegoSnapshot? ShipLegos { get; init; }
    public EnbSceneDisplayNameProperties? DisplayNameProperties { get; init; }
    public ImmutableArray<EnbNext.Net7.World.Net7LegoControllerMutation> LegoControllerMutations { get; init; }=[];
    public ulong FixedShipVisualRevision { get; init; }
    public ulong BaseAssetMaterializationRevision { get; init; }
    /// <summary>Lifetime of the base RenderObject, excluding Lego composition.
    /// Synthetic effect owners retain their explicit VisualGeneration.</summary>
    public ulong? BaseVisualGeneration { get; init; }
    public ulong RenderObjectGeneration => BaseVisualGeneration ?? VisualGeneration;

    /// <summary>
    /// Current-retail ShipAuxData fields relevant to ship presentation.
    /// Equipment/mount data is exposed for passive diagnostics only.
    /// </summary>
    public EnbSceneShipPresentationSnapshot? ShipPresentation { get; init; }
    public ImmutableArray<EnbSceneDamageDecalPoolSnapshot> DamageDecalPools { get; init; } = [];

    /// <summary>Presentation-only bank from the ship visual controller, never a physics input.</summary>
    public float? ShipVisualBankRadians { get; init; }
    /// <summary>Source-assigned compact pose slot, carried through diagnostic visual transforms.</summary>
    public int RenderPoseSlot { get; init; } = -1;
    public EnbSceneShipEmotePlayback? ShipEmote { get; init; }

    public EnbScenePlayerPresentationSnapshot? PlayerPresentation { get; init; }
    public bool IsRescueBeaconActive { get; init; }
    public ulong RelationshipNotificationRevision { get; init; }
    public ulong PvpUpdateRevision { get; init; }
    public bool PvpUpdated { get; init; }

    public EnbSceneTargetVitalsSnapshot? TargetVitals { get; init; }
    public EnbSceneInterruptionSnapshot? Interruption { get; init; }
    public int ScannerFactionState {get;init;}
    public int ScannerTechLevelState {get;init;}
    public ulong ScannerTechLevelRevision {get;init;}

    public int HarvestableResourcesState { get; init; }
    public ImmutableArray<int> HarvestableResourceSlotStates { get; init; } = ImmutableArray<int>.Empty;

    public ImmutableArray<EnbSceneHarvestableResourceSnapshot> HarvestableResources { get; init; } =
        ImmutableArray<EnbSceneHarvestableResourceSnapshot>.Empty;

    /// <summary>
    /// Passive receipt showing whether object-targeted ShipAuxData reached and
    /// survived the generic materialization boundary for this object.
    /// </summary>
    public EnbSceneAuxDataMaterializationAuditSnapshot? ShipAuxDataAudit { get; init; }

    /// <summary>
    /// Detached receipt of the physical attachment scope visible when the last
    /// current-retail 0x0011-equivalent update was processed.
    /// </summary>
    public EnbSceneColorizationApplicationAuditSnapshot? ColorizationAudit { get; init; }
    public int NameDecalApplyCount { get; init; }
    /// <summary>Color packet count at the retained name write; null for authored previews without packet order.</summary>
    public int? NameDecalColorizationApplyCount { get; init; }
    public int DecalApplyCount { get; init; }
}

public sealed record EnbSceneCameraModeSnapshot(
    int Index,
    float HeadingRadians,
    float Distance,
    float TiltRadians,
    float HorizontalFieldOfViewRadians,
    float ElevationOffsetRadians);

public sealed record EnbSceneClipRangeSnapshot(
    float NearPlane,
    float FarPlane);

/// <summary>
/// Immutable camera intent/configuration available to the later camera
/// controller. The active mode and transition state are deliberately not
/// invented here; only resolves the current retail mode source and proven
/// chase/clip semantics across the mutable-world boundary.
/// </summary>
public sealed record EnbSceneCameraStateSnapshot(
    ulong? AnchorRenderIdentity,
    uint? AnchorGameId,
    bool UsesSessionOverride,
    int? SessionOverrideSelector,
    EnbPresentationProvenance? ModeSourceProvenance,
    int? PlayerOptionsSelector,
    EnbPresentationProvenance? PlayerOptionsSourceProvenance,
    bool StartInFirstPerson,
    ImmutableArray<EnbSceneCameraModeSnapshot> Modes,
    float ChaseDistanceScale,
    EnbSceneClipRangeSnapshot NormalClipRange,
    ImmutableArray<EnbSceneClipRangeSnapshot> TemporaryPassClipRanges)
{
    public bool WarpCameraEnabled { get; init; } = true;
    public ImmutableArray<EnbNext.Net7.Gameplay.Net7CameraEvent> Events { get; init; } = [];
    public ulong? TakeoffRevision { get; init; }
    public bool GateCameraEnabled { get; init; }
    public bool BridgeCameraEnabled { get; init; } = true;
    public bool LootCameraEnabled { get; init; } = true;
    public bool DockCameraEnabled { get; init; } = true;
    public bool KeepShipInView { get; init; } = true;
    public bool LandCameraEnabled {get;init;} = true;
    public EnbNext.Net7.Gameplay.Net7LandCameraTransition? LandTransition {get;init;}
    public EnbNext.Net7.Gameplay.Net7GateCameraTransition? GateTransition { get; init; }
    public EnbNext.Net7.Gameplay.Net7DockCameraTransition? DockTransition { get; init; }
    public EnbNext.Net7.Gameplay.Net7LootCameraRequest? LootRequest { get; init; }
}


public enum EnbSceneEffectGeometryKind
{
    Object = 0,
    Point = 1,
    ObjectToObject = 2,
    ObjectToEffect = 3,
    ObjectToPoint = 4,
    ObjectToObjectDurationLinked = 5,
}

public enum EnbPresentationEffectClassKind
{
    Unknown,
    RObjLookAtEffect,
    RObjEffect,
    WarpEffect,
    LineEffect,
    StraightSeekEffect,
    SpiralSeekEffect,
    SplineRandomSeekEffect,
    LinearRandomSeekEffect,
    SoundEffect,
    PassEffect,
    AttachEffect,
    RenderStateEffect,
    ParticleJetEffect,
}

public enum EnbPresentationRenderStateEffectActionKind
{
    Unknown = -1,
    Activate = 0,
    Deactivate = 1,
    Linger = 2,
    Ramp = 3,
    ActivateNextStateOnce = 4,
}

public sealed record EnbPresentationEffectReference(
    int DefinitionId,
    string? EffectClass,
    EnbPresentationEffectClassKind EffectClassKind,
    int? BaseAssetId,
    int? StartLinkId,
    int? NextLinkId,
    string? DurationRaw,
    bool InfiniteDuration,
    float? DurationValue,
    float? StartDelay,
    float? StartLinkDelay,
    float? CycleTime,
    bool UnaryStartLinkToTarget,
    bool UnaryNextLinkToTarget,
    bool TrackTarget,
    bool TrackSource,
    bool GoAwayWhenTargetDoes,
    bool GoAwayWhenSourceDoes,
    bool OffsetDurationPassedToStartLink,
    bool PassDurationToStartLink,
    bool DurationLinkedEffectUseNextLink,
    bool DurationLinkedEffectUseStartLink,
    string? Sfx,
    bool MissingDefinition,
    bool InvalidDefinition,
    bool MissingBaseAsset,
    ImmutableArray<EnbPresentationProperty> Properties,
    EnbPresentationBaseAssetReference? BaseAssetReference,
    EnbPresentationProvenance Provenance,
    int? RenderStateId = null,
    EnbPresentationRenderStateEffectActionKind RenderStateAction = EnbPresentationRenderStateEffectActionKind.Unknown,
    int? RenderStateActionTimeMilliseconds = null,
    bool RenderStateForceReactivate = false);

public sealed record EnbSceneEffectEndpointReceipt(ulong? SourceCreationOrdinal, ulong? TargetCreationOrdinal);

// Source is the first complete publication; target is the last complete
// publication of the original packet-time object. Neither restores membership.
public sealed record EnbSceneEffectEndpointSnapshots(EnbSceneObjectSnapshot Source, EnbSceneObjectSnapshot Target)
{
    public EnbSceneObjectSnapshot? InitialTarget { get; init; }
    public bool CompleteAtFirstPublication { get; init; }
}

public sealed record EnbSceneEffectSnapshot(
    uint EffectId,
    ulong InstanceGeneration,
    uint SourceGameId,
    uint TargetGameId,
    int DefinitionId,
    uint Timestamp,
    int DurationMilliseconds,
    string BoneName,
    ushort Mask,
    bool IsObjectToObject,
    float Scale,
    Vector3 SpatialOffset,
    ushort EndpointShortA,
    ushort EndpointShortB,
    bool DescriptorFlag,
    Vector3 EffectOffset,
    float EffectScalar,
    EnbSceneEffectGeometryKind GeometryKind,
    Vector3 WorldPoint,
    short DurationLink,
    Vector3 HsvShift,
    short AuxiliaryEffectFieldA,
    short AuxiliaryEffectFieldB,
    EnbPresentationEffectReference? Presentation)
{
    public bool EndWithTarget { get; init; }
    // Null means no packet-time observation, not an absent endpoint.
    public EnbSceneEffectEndpointReceipt? EndpointReceipt { get; init; }
    public EnbSceneEffectEndpointSnapshots? PublishedEndpoints { get; init; }
    public EnbSceneObjectSnapshot? InitialSourceObject { get; init; }
    public EnbSceneObjectSnapshot? LastSourceObject { get; init; }
    public Guid PoseContext { get; init; }
}

public sealed record EnbSceneEnvironmentSnapshot(
    short BaseAsset,
    bool SwapBackdrop,
    uint FinalRawValue,
    ImmutableArray<byte> RawServerParameters,
    EnbPresentationBaseAssetReference? BaseAssetReference,
    float FogNear,
    float FogFar,
    bool LightBackdrop,
    bool FogBackdrop,
    float BackdropFogNear,
    float BackdropFogFar,
    int? PresentationMode,
    int SplineUpdateCountAtLoad)
{
    public short BackdropBaseAsset => BaseAsset;
    public uint SectorNum => FinalRawValue;
    /// <summary>
    /// Selector 00513540 hands to CreateClientBaseAssetRenderObject for the sky:
    /// a negative BaseAsset becomes 0x3FC, 0x135 or 0x475 for the client mode 1, 0
    /// or 5 current when 0x0042 was processed; another mode keeps it.
    /// </summary>
    public int SkyBaseAsset { get => _skyBaseAsset ?? BaseAsset; init => _skyBaseAsset = value; }
    private readonly int? _skyBaseAsset;

    /// <summary>
    /// The sector's XMin/YMin/XMax/YMax from 0x0042 Server Parameters (wire bytes 8-23, after ZBandMin and ZBandMax;
    /// 0x008A5FD0 prints packet +0x0C..+0x20 in that order and 0x00737F00 copies them to SClient +0x10D4..+0x10E8),
    /// or null when the packet does not carry a usable extent.
    /// </summary>
    public (System.Numerics.Vector2 Minimum, System.Numerics.Vector2 Maximum)? SectorExtent
    {
        get
        {
            if (RawServerParameters.IsDefault || RawServerParameters.Length < 24) return null;
            var raw = RawServerParameters;
            float F(int offset) => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(offset, 4));
            System.Numerics.Vector2 min = new(F(8), F(12)), max = new(F(16), F(20));
            return float.IsFinite(min.X) && float.IsFinite(min.Y) && float.IsFinite(max.X) && float.IsFinite(max.Y) && max.X > min.X && max.Y > min.Y ? (min, max) : null;
        }
    }

    public static int ResolveSkyBaseAsset(short baseAsset, int? modeAtServerParameters)
        => baseAsset >= 0 ? baseAsset : modeAtServerParameters switch { 1 => 0x3FC, 0 => 0x135, 5 => 0x475, _ => baseAsset };
}

public sealed record EnbSceneSplineUpdateSnapshot(ImmutableArray<byte> Data);


public sealed record EnbSceneLightSnapshot(
    ulong CreationOrdinal,
    uint GameId,
    Vector3 Diffuse,
    Vector3 Specular,
    Vector3 Ambient,
    Vector3 Position,
    Vector3 Direction,
    float Range,
    float Falloff,
    float Attenuation0,
    float Attenuation1,
    float Attenuation2,
    float Theta,
    float Phi);

public sealed record EnbSceneLightingSnapshot(
    uint GlobalAmbientRgb24,
    ImmutableArray<EnbSceneLightSnapshot> Lights);

public sealed record EnbSceneHudSnapshot(
    uint? CurrentTargetId,
    int? ScanRange,
    bool LocalShipLockSpeedActive,
    bool ClientSendUiTriggersEnabled) : IEnbHudFrame
{
    IEnbCockpitFrame? IEnbHudFrame.Cockpit => Cockpit;

    public EnbSceneCockpitSnapshot? Cockpit { get; init; }
    public int ReputationState {get;init;}
    public ulong ReputationRevision {get;init;}
    public EnbSceneExperienceSnapshot Experience { get; init; } = new(0, 0, 0);
    public EnbSceneInventorySnapshot? Inventory { get; init; }
    public EnbSceneChatSnapshot? Chat { get; init; }
    public EnbNext.Net7.Gameplay.Net7WarpRouteSnapshot? WarpRoute { get; init; }
    public EnbSceneShortcutSnapshot? Shortcuts { get; init; }
    public ImmutableArray<EnbSceneBuffSnapshot> Buffs { get; init; } = [];
    /// <summary>The selected target's buffs, when the server sends them in that object's ShipAuxData BuffArray.</summary>
    public ImmutableArray<EnbSceneBuffSnapshot> TargetBuffs { get; init; } = [];
    public ImmutableArray<uint> GroupMemberIds { get; init; } = ImmutableArray<uint>.Empty;
    public EnbSceneGroupSnapshot? Group { get; init; }
    public EnbSceneGuildSnapshot? Guild { get; init; }
    public uint? LocalSelectedTargetId { get; init; }
    public bool HasLocalTargetSelection { get; init; }
    public ulong TargetSelectionSequence { get; init; }
    public int? ProspectSkillLevel { get; init; }
    public ImmutableArray<uint> CurrentTargetVerbs { get; init; } = ImmutableArray<uint>.Empty;
    public string? TargetThreat { get; init; }
    public string? TargetThreatSound { get; init; }
    public int? TargetThreatLevel { get; init; }
}

/// <summary>Local ship HUD, sampled under the session boundary at presentation time.</summary>
public sealed record EnbSceneExperienceSnapshot(float Combat, float Explore, float Trade);

public sealed record EnbSceneCockpitSnapshot(float? EnergyPercent, float? MaximumEnergyPower,
    EnbSceneTargetVitalsSnapshot? Vitals, float? MaximumSpeed, bool IsAutoFollowing) : IEnbCockpitFrame
{
    IEnbVitalsFrame? IEnbCockpitFrame.Vitals => Vitals;

    public EnbNext.Net7.Gameplay.Net7WarpStatus? Warp { get; init; }
    public EnbSceneCockpitWarpCommand? WarpCommand { get; init; }
}

public sealed record EnbSceneCockpitWarpCommand(ulong Sequence, int State, long? TriggerTime, bool HasTriggerTime = true);

public sealed record EnbSceneStationAvatarSnapshot(
    uint AvatarId,
    uint RoomType,
    float Orientation,
    Vector3 Position,
    uint ActionFlag)
{
    public EnbSceneAvatarSnapshot? Avatar { get; init; }
    public long MotionRevision {get;init;}
    public ImmutableArray<EnbNext.Net7.Presentation.Net7StarbaseAvatarPose> MotionHistory {get;init;}=[];
}

public sealed record EnbSceneZBandSnapshot(
    ImmutableArray<byte> FieldA,
    ImmutableArray<byte> FieldB);

public sealed record EnbSceneBoundingBoxSnapshot(
    ImmutableArray<byte> BlockA,
    ImmutableArray<byte> BlockB);

public sealed record EnbSceneSnapshot(
    ulong Version,
    uint ClientTime,
    uint? LocalPlayerId,
    uint? LocalShipObjectId,
    uint? ClientAvatarId,
    bool IsTransitioning,
    EnbSceneEnvironmentSnapshot? Environment,
    EnbSceneCameraStateSnapshot Camera,
    ImmutableArray<EnbSceneEffectSnapshot> Effects,
    EnbSceneHudSnapshot Hud,
    EnbSceneLightingSnapshot Lighting,
    ImmutableArray<EnbSceneObjectSnapshot> Objects,
    ImmutableArray<EnbSceneStationAvatarSnapshot> StationAvatars,
    EnbSceneZBandSnapshot? ZBand,
    EnbSceneBoundingBoxSnapshot? BoundingBox,
    ImmutableArray<byte> SplineData,
    ImmutableArray<EnbSceneSplineUpdateSnapshot> SplineUpdates) : IEnbSceneFrame
{
    IEnbHudFrame IEnbSceneFrame.Hud => Hud;
    IReadOnlyList<IEnbRenderObject> IEnbSceneFrame.Objects => (IReadOnlyList<EnbSceneObjectSnapshot>)Objects;

    public uint? LocalAvatarObjectId => ClientAvatarId ?? LocalPlayerId;
    public ImmutableDictionary<uint,string> AvatarFirstNames {get;init;}=ImmutableDictionary<uint,string>.Empty;
    public ImmutableDictionary<uint,EnbSceneAvatarSnapshot>? AvatarDescriptions {get;init;}
    public ImmutableDictionary<uint,ulong> AvatarCacheLifetimes {get;init;}=ImmutableDictionary<uint,ulong>.Empty;
    public Guid StationAvatarStreamId {get;init;}
    public ImmutableList<EnbNext.Net7.Presentation.Net7StationAvatarEvent> StationAvatarEvents {get;init;}=[];
    public ImmutableArray<EnbNext.Net7.Gameplay.Net7ShipEmoteEvent> ShipEmotes { get; init; } = [];
    public long ServerTimeCorrectionMilliseconds { get; init; }
    public ulong MeshBufferResetRevision { get; init; }
    // Transport sequence for the independent renderer mutation stream. Capture
    // files remain plain immutable scene data and use an explicit replay adapter.
    [System.Text.Json.Serialization.JsonIgnore]
    public ulong RenderSceneSequence { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public ulong SourceMembershipRevision { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public EnbRenderPoseSet RenderPoses {get;init;}
    public ulong WorldRegistryRevision { get; init; }


    // MainViewPrepareCameraForRender @ 0050F1B0: corrected duration is narrowed
    // with signed saturation before the orbital-hours conversion. Gameplay keeps
    // its original local clock and timestamp conversion semantics.
    public int GetOrbitalTimeMilliseconds(uint renderClientTime) =>
        (int)Math.Clamp((long)renderClientTime + ServerTimeCorrectionMilliseconds,
            int.MinValue, int.MaxValue);
    public EnbSceneLocationTransition? LocationTransition { get; init; }
    public EnbSceneMusicContext? Music { get; init; }
    public uint? ActiveStarbaseId { get; init; }
    public EnbStationConversation? StationConversation { get; init; }
    public EnbNext.Net7.Gameplay.Net7IncapacitationDetails? IncapacitationDetails { get; init; }
    public EnbSceneAvatarSnapshot? RecoveryAvatar { get; init; }
    public EnbStationTerminalSnapshot? StationTerminal { get; init; }
    public EnbNext.Net7.Presentation.Net7AvatarRecustomizeStart? AvatarCustomization { get; init; }
    public EnbNext.Net7.Presentation.Net7ShipRecustomizeStart? ShipCustomization { get; init; }
    public System.Collections.Immutable.ImmutableArray<EnbNext.Net7.Presentation.Net7JobListEntry> StationJobs { get; init; } = [];
    /// <summary>The job ids the server deleted since the job list opened and has not listed again.</summary>
    public System.Collections.Immutable.ImmutableArray<uint> StationJobsWithdrawn { get; init; } = [];
    public EnbNext.Net7.Presentation.Net7JobDescriptionUpdate? StationJobDescription { get; init; }
    public EnbNext.Net7.Presentation.Net7JobAcceptReplyUpdate? StationJobReply { get; init; }
    public bool StationJobRequestPending { get; init; }
    public EnbNext.Net7.Gameplay.Net7DamageStream? DamageEvents {get;init;}
    public Guid ClientSoundStreamId {get;init;}
    public ImmutableList<EnbNext.Net7.Presentation.Net7ClientSoundEvent> ClientSoundEvents {get;init;} = [];
    public ulong DockingSoundRevision {get;init;}
    public EnbSceneStationDefinition? StationDefinition { get; init; }
}

public sealed record EnbSceneLocationTransition(ulong Version, string SourceSectorName,
    string SourceSystemName, string DestinationSectorName, string DestinationSystemName)
{
    // LoadingViewBeginLocationTransition @ 00691830; raw formatting branches 00691CD7..00691DD4.
    public string SourceText => SourceSectorName.Length == 0 && SourceSystemName.Length == 0
        ? "" : "Now leaving " + SourceSectorName;
    public string DestinationText => DestinationSectorName.Length == 0 && DestinationSystemName.Length == 0
        ? "" : DestinationSystemName.Length == 0 ? "Docking with " + DestinationSectorName
        : $"Entering {DestinationSectorName} ({DestinationSystemName})";
}
