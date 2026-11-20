using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.Protocol;
using EnbNext.Net7.World;

namespace EnbNext.Presentation;

/// <summary>
/// Converts snapshot-visible Net7 domain state into a detached presentation snapshot.
/// Build must run while the caller owns the mutation boundary for every mutable
/// source supplied to it (currently world state plus transient effects). After
/// Build returns, the snapshot contains no mutable Net7 collections or object
/// references and is safe to publish to a render thread.
/// </summary>
public sealed class EnbPresentationSnapshotBuilder
{
    private readonly EnbInventoryPublicationOwner _inventoryOwner=new();
    // Current-binary camera semantics. These are executable behavior,
    // not values copied from the supplied retail INI snapshot. Camera mode
    // values themselves remain runtime inputs from spacecam/cam*.ini.
    private const float RetailChaseDistanceScale = 75f;
    private const float RetailNormalNearPlane = 6f;
    private const float RetailNormalFarPlane = 600000f;
    private const float RetailWorldPassNearPlane = 20f;
    private const float RetailWorldPassFarPlane = 100000f;
    private const float RetailSpecialPassNearPlane = 50f;
    private const float RetailSpecialPassFarPlane = 150f;

    // Compatibility value retained as the fail-closed fallback.
    // replaces it at the render boundary when exact ordinary-world environment
    // ambient is reconstructed. Later current-client work proved that
    // ordinary world W3Ds belong to MainView+0x10C, not the +0x11C scene used
    // by the original universal-ambient argument. The user's live receipt
    // remains valid for that sector; this constant is no longer claimed to be
    // universal retail ambient behavior.
    private const uint RetailMainWorldAmbientRgb24 = 0x007F7F7F;
    private const int RetailSceneLightSlotCount = 4;
    private const float RetailLightAttenuationEpsilon = 1.0e-5f;
    private const float RetailDefaultLightNearRange = 50f;
    private const float RetailDefaultLightRange = 100f;
    private const float RetailInfiniteLightRange = 1_000_000f;

    // Current Ship.csv schema IDs. Retail constructs one Legos aggregate with
    // Scale plus 18 fixed attachment records {BoneName,Type,Assets,Data}.
    private const int ShipLegosPropertyId = 1254;
    private const int ShipLegosScalePropertyId = 1255;
    private const int ShipLegosFirstAttachmentPropertyId = 1257;
    private const int ShipLegosAttachmentStride = 5;
    private const int ShipLegosAttachmentCount = 18;

    // Current Ship.csv presentation diagnostics and fixed-visual gate.
    // Inventory fields are carried across the presentation boundary only for
    // passive evidence until current client.exe proves live-world ownership.
    private const int ShipIsOrganicPropertyId = 29;
    private const int ShipPrivateWarpStatePropertyId = 38;
    private const int ShipGlobalWarpStatePropertyId = 39;
    private const int ShipInventoryPropertyId = 47;
    private const int ShipEquipMountModelPropertyId = 49;
    private const int ShipMountsFirstPropertyId = 51;
    private const int ShipMountBoneNamesFirstPropertyId = 72;
    private const int ShipInventoryMountCount = 20;
    private const int ShipEquippedPropertyId = 535;
    private const int ShipEquippedFirstAggregatePropertyId = 536;
    private const int ShipEquippedFirstItemTemplateIdPropertyId = 537;
    private const int ShipEquippedStride = 19;
    private const int ShipEquippedCount = 20;
    private const int ShipEngineThrustStatePropertyId = 1921;
    private const int ShipEngineTrailTypePropertyId = 1922;
    private const int ShipOwnerPropertyId = 2;
    private const int ShipTitlePropertyId = 3;
    private const int ShipRankPropertyId = 4;
    private const int ShipShieldPercentStartValuePropertyId = 13;
    private const int ShipShieldPercentEndTimePropertyId = 11;
    private const int ShipShieldPercentChangePerTickPropertyId = 12;
    private const int ShipMaximumShieldPowerPropertyId = 14;
    private const int ShipHullPointsPropertyId = 15;
    private const int ShipMaximumHullPointsPropertyId = 16;
    private const int ShipCombatLevelPropertyId = 33;
    private const int ShipGuildNamePropertyId = 1923;
    private const int ShipGuildRankPropertyId = 1924;
    private const int ShipGuildRankNamePropertyId = 1925;
    private const int ShipSameGuildTagColorPropertyId = 1926;
    private const int ShipOtherGuildTagColorPropertyId = 1927;
    private const int LocalPlayerProspectSkillLevelPropertyId = 2903;
    private const int HarvestableFirstResourceItemTemplateIdPropertyId = 4;
    private const int HarvestableResourceStride = 11;
    private const int HarvestableResourceCount = 40;

    private readonly object _gate = new();
    private readonly Dictionary<Net7WorldObject, TrackedObject> _trackedObjects =
        new(ReferenceEqualityComparer.Instance);
    private Net7WorldRegistry? _membershipRegistry;
    private Net7WorldMembershipSubscription? _membershipSubscription;
    private ulong _nextSnapshotVersion;
    private ulong _nextRenderIdentity;
    private readonly Dictionary<(uint, ulong), (EnbSceneObjectSnapshot Initial, EnbSceneObjectSnapshot Last)> _effectSources = [];
    private readonly Dictionary<(uint, ulong), EnbSceneEffectEndpointSnapshots> _effectEndpoints = [];
    private readonly HashSet<(uint, ulong)> _observedEffectEndpoints = [];

    public EnbSceneSnapshot Build(
        Net7WorldState world,
        EnbRetailAssetBundle? assets,
        uint clientTime,
        uint? fixedSessionId = null,
        int? presentationMode = null)
        => Build(world, effects: null, assets, clientTime, fixedSessionId, presentationMode);

    public EnbSceneSnapshot Build(
        Net7WorldState world,
        IReadOnlyDictionary<uint, Net7EffectState>? effects,
        EnbRetailAssetBundle? assets,
        uint clientTime,
        uint? fixedSessionId = null,
        int? presentationMode = null,
        Net7GameplayState? gameplay = null,
        Net7ItemTemplateCatalog? itemTemplates = null)
    {
        ArgumentNullException.ThrowIfNull(world);

        lock (_gate)
        {
            IReadOnlyList<Net7WorldObject> worldObjects = world.Objects.Snapshot();
            uint? localShipObjectId = world.LocalShipObjectId;
            uint? currentTargetId = world.CurrentTargetId;
            Net7WorldObject? localShip = localShipObjectId is uint localId
                ? world.Objects.FindFirst(localId)
                : null;
            Net7WorldObject? currentTarget = currentTargetId is uint targetId
                ? world.Objects.FindFirst(targetId)
                : null;

            ApplyMembershipChanges(world.Objects);
            var objects = ImmutableArray.CreateBuilder<EnbSceneObjectSnapshot>(worldObjects.Count);
            IReadOnlyDictionary<string, float> factionReactions =
                BuildFactionReactions(world.LocalPlayerAuxData);

            foreach (Net7WorldObject worldObject in worldObjects)
            {
                TrackedObject tracked = GetOrCreateTracking(worldObject);
                VisualSignature visual = VisualSignature.From(worldObject,tracked,BuildShipLegos(worldObject,tracked));
                if (tracked.Visual != visual)
                {
                    if ((tracked.Visual with { CompositionFingerprint = 0 }) !=
                        (visual with { CompositionFingerprint = 0 }))
                        tracked.BaseVisualGeneration = checked(tracked.BaseVisualGeneration + 1);
                    tracked.Visual = visual;
                    tracked.VisualGeneration = checked(tracked.VisualGeneration + 1);
                }

                objects.Add(BuildObject(
                    world, worldObject, tracked, assets, clientTime, localShip, currentTarget, itemTemplates, factionReactions));
            }


            ulong? localRenderIdentity = localShip is not null &&
                _trackedObjects.TryGetValue(localShip, out TrackedObject? localTracking)
                    ? localTracking.RenderIdentity
                    : null;

            ImmutableArray<EnbSceneStationAvatarSnapshot> stationAvatars = world.StationAvatars.Values
                .OrderBy(static avatar => avatar.AvatarId)
                .Select(avatar => new EnbSceneStationAvatarSnapshot(
                    avatar.AvatarId,
                    avatar.RoomType,
                    avatar.Orientation,
                    avatar.Position,
                    avatar.ActionFlag) { Avatar = BuildAvatar(world.AvatarDescriptions.GetValueOrDefault(avatar.AvatarId)),
                        MotionRevision=avatar.MotionRevision,MotionHistory=avatar.MotionHistory.ToImmutableArray() })
                .ToImmutableArray();

            return new EnbSceneSnapshot(
                Version: NextSnapshotVersion(),
                ClientTime: clientTime,
                LocalPlayerId: world.LocalPlayerId,
                LocalShipObjectId: localShipObjectId,
                ClientAvatarId: world.ClientAvatarId,
                IsTransitioning: world.IsTransitioning,
                Environment: BuildEnvironment(world, assets, presentationMode),
                Camera: BuildCameraState(assets, fixedSessionId, localRenderIdentity, localShipObjectId) with
                    { GateTransition = gameplay?.GateCamera, LootRequest = gameplay?.LootCamera, DockTransition = gameplay?.DockCamera, LandTransition = gameplay?.LandCamera,
                      Events = gameplay?.NativeCameraEvents.ToImmutableArray() ?? [], TakeoffRevision = gameplay?.TakeoffRevision },
                Effects: BuildEffects(effects, assets, objects),
                Hud: new EnbSceneHudSnapshot(
                    currentTargetId,
                    ReadCurrentScanRange(localShip),
                    world.LocalShipLockSpeedActive,
                    world.ClientSendUiTriggersEnabled)
                {
                    GroupMemberIds = BuildGroupMemberIds(world.LocalPlayerAuxData),
                    ReputationState = world.LocalPlayerAuxData.Resolve(5422).State,
                    ReputationRevision = world.LocalPlayerAuxData.Resolve(5422).UpdateRevision,
                    Group = EnbSceneGroupSnapshot.From(new Net7PlayerSemanticDomain(world).GetGroupState(), gameplay?.Social) with
                    { AuxDataState = world.LocalPlayerAuxData.Properties.First(p => p.Path == "LocalPlayerAuxData.GroupInfo").State,
                      AuxRevision = world.LocalPlayerAuxData.Properties.First(p => p.Path == "LocalPlayerAuxData.GroupInfo").UpdateRevision },
                    Chat = gameplay is null ? null : EnbSceneChatSnapshot.From(gameplay.Social, gameplay.IncomingMessage, gameplay.ConfirmedDialog),
                    Guild = gameplay is null ? null : EnbSceneGuildSnapshot.From(gameplay),
                    Experience = BuildExperience(world.LocalPlayerAuxData),
                    Inventory = EnbSceneInventorySnapshotBuilder.Build(localShip?.AuxData, world.LocalPlayerAuxData,
                        itemTemplates, assets?.ResolveShortcutSkills(),
                        gameplay?.CurrentHulkId is uint hulkId && gameplay.LocalSelectedTargetId == hulkId ? world.Objects.FindFirst(hulkId) : null,
                        gameplay?.CurrentHarvestableId is uint harvestId && gameplay.LocalSelectedTargetId == harvestId ? world.Objects.FindFirst(harvestId) : null,
                        gameplay?.TradeDomain,gameplay?.TradeDomain.ActivePartnerId is uint tradeId?world.Objects.FindFirst(tradeId):null,_inventoryOwner),
                    LocalSelectedTargetId = gameplay?.LocalSelectedTargetId,
                    HasLocalTargetSelection = gameplay?.HasLocalTargetSelection == true,
                    TargetSelectionSequence = gameplay?.TargetSelectionSequence ?? 0,
                    ProspectSkillLevel = ReadLocalProspectSkillLevel(world.LocalPlayerAuxData),
                    CurrentTargetVerbs = ResolveCurrentTargetVerbs(gameplay, currentTargetId),
                    TargetThreat = ReadTargetThreat(localShip),
                    TargetThreatSound = ReadTargetThreat(localShip, sound: true),
                    TargetThreatLevel = ReadTargetThreatLevel(localShip),
                    Cockpit = BuildCockpit(localShip, clientTime, presentationMode),
                    WarpRoute = gameplay?.WarpRoute,
                    Buffs = Net7PlayerSemanticDomain.GetBuffPresentation(localShip?.AuxData, clientTime)
                        .Select(EnbSceneBuffSnapshot.From).ToImmutableArray(),
                    TargetBuffs = currentTarget is { } buffTarget && buffTarget.GameId != localShipObjectId
                        ? Net7PlayerSemanticDomain.GetBuffPresentation(buffTarget.AuxData, clientTime).Select(EnbSceneBuffSnapshot.From).ToImmutableArray() : [],
                    Shortcuts = assets is null ? null : EnbSceneShortcutSnapshotBuilder.Build(
                        assets.ResolveShortcuts(fixedSessionId), assets.ResolveShortcutSkills(),
                        localShip?.AuxData, world.LocalPlayerAuxData, itemTemplates),
                },
                Lighting: BuildLighting(world, worldObjects, clientTime),
                Objects: objects.MoveToImmutable(),
                StationAvatars: stationAvatars,
                ZBand: world.ZBand is null
                    ? null
                    : new EnbSceneZBandSnapshot(
                        world.ZBand.FieldA.ToImmutableArray(),
                        world.ZBand.FieldB.ToImmutableArray()),
                BoundingBox: world.BoundingBox is null
                    ? null
                    : new EnbSceneBoundingBoxSnapshot(
                        world.BoundingBox.BlockA.ToImmutableArray(),
                        world.BoundingBox.BlockB.ToImmutableArray()),
                SplineData: world.Spline?.Data.ToImmutableArray() ?? ImmutableArray<byte>.Empty,
                SplineUpdates: world.SplineUpdates
                    .Select(static update => new EnbSceneSplineUpdateSnapshot(update.Data.ToImmutableArray()))
                    .ToImmutableArray())
            {
                ServerTimeCorrectionMilliseconds = world.Time.CorrectionMilliseconds,
                WorldRegistryRevision = world.Objects.MembershipRevision,
                StationAvatarStreamId=world.StationAvatarStreamId,
                AvatarFirstNames=world.AvatarDescriptions.ToImmutableDictionary(a=>a.Key,a=>a.Value.FirstName),
                AvatarDescriptions=world.AvatarDescriptions.ToImmutableDictionary(a=>a.Key,a=>BuildAvatar(a.Value)!),
                AvatarCacheLifetimes=world.AvatarCacheLifetimes.ToImmutableDictionary(),
                StationAvatarEvents=world.StationAvatarEvents,

                MeshBufferResetRevision = gameplay?.MeshBufferResetRevision ?? 0,
                ShipEmotes = gameplay?.ShipEmoteEvents.ToImmutableArray() ?? [],
                Music = presentationMode is null or 0 or 1 or 2 or 5 ? EnbSceneMusicContext.From(world.LocalPlayerAuxData,
                    worldObjects.Any(o=>o.IsAttacking!=0), presentationMode is 1 or 5) : null,
                LocationTransition = world.LastPresentationHandoff is { } handoff
                    ? new(world.PresentationHandoffVersion, handoff.SourceSectorName, handoff.SourceSystemName,
                        handoff.DestinationSectorName, handoff.DestinationSystemName) : null,
            };
        }
    }

    private static EnbSceneLightingSnapshot BuildLighting(
        Net7WorldState world,
        IReadOnlyList<Net7WorldObject> worldObjects,
        uint clientTime)
    {
        ImmutableArray<EnbSceneLightSnapshot> lights = worldObjects
            .Where(static obj => obj.ObjectType == 0x08)
            .OrderBy(static obj => obj.CreationOrdinal)
            .Take(RetailSceneLightSlotCount)
            .Select(obj => BuildLight(world, obj, clientTime))
            // An undefined light still occupies its retail slot (Take above) but
            // is not presented: Next does not invent its indeterminate factors.
            .OfType<EnbSceneLightSnapshot>()
            .ToImmutableArray();

        return new EnbSceneLightingSnapshot(RetailMainWorldAmbientRgb24, lights);
    }

    private static EnbSceneLightSnapshot? BuildLight(
        Net7WorldState world,
        Net7WorldObject worldObject,
        uint clientTime)
    {
        Net7AuxDataSchema auxData = worldObject.AuxData
            ?? throw new InvalidDataException(
                $"Current retail type-8 Light 0x{worldObject.GameId:X8} has no Light AuxData schema.");
        if (!string.Equals(auxData.Name, "Light", StringComparison.Ordinal) || auxData.Properties.Count != 8)
            throw new InvalidDataException($"Type-8 Light 0x{worldObject.GameId:X8} has unexpected AuxData schema '{auxData.Name}'.");

        float intensity = ReadMaterializedFloat(auxData.Resolve(2), "Light.Intensity", 1f);
        Vector3 diffuse = ReadMaterializedVector3(auxData.Resolve(3), "Light.DiffuseRGB", Vector3.One);
        Vector3 specular = ReadMaterializedVector3(auxData.Resolve(4), "Light.SpecularRGB", Vector3.One);
        Vector3 ambient = ReadMaterializedVector3(auxData.Resolve(5), "Light.AmbientRGB", Vector3.One);

        float nearRange = RetailDefaultLightNearRange;
        float range = RetailDefaultLightRange;
        Net7AuxDataProperty attenuation = auxData.Resolve(6);
        Net7AuxDataProperty attenuationFactors = auxData.Resolve(7);
        if (attenuation.State > 1 || attenuationFactors.State > 1)
        {
            int mode = attenuation.State > 1
                ? ReadMaterializedInt32(attenuation, "Light.Attenuation")
                : 0; // ConstructType8AuxData explicitly initializes Attenuation to zero.

            switch (mode)
            {
                case 0:
                    nearRange = RetailInfiniteLightRange;
                    range = RetailInfiniteLightRange;
                    break;
                case 1:
                case 3:
                {
                    if (ReadRequiredMaterializedVector3(attenuationFactors, "Light.AttenuationFactors") is not Vector3 factors)
                        return null;
                    nearRange = factors.X;
                    range = factors.Y;
                    break;
                }
                case 2:
                {
                    if (ReadRequiredMaterializedVector3(attenuationFactors, "Light.AttenuationFactors") is not Vector3 factors)
                        return null;
                    nearRange = factors.X;
                    range = factors.X;
                    break;
                }
            }
        }

        float attenuation1 = MathF.Abs(nearRange - range) >= RetailLightAttenuationEpsilon
            ? 1f / nearRange
            : 0f;

        Vector3 position = Vector3.Zero;
        if (worldObject.Spatial is not null &&
            ReferenceEquals(world.Objects.FindFirst(worldObject.GameId), worldObject) &&
            world.TrySampleObjectPresentation(worldObject.GameId, clientTime, out Net7SpatialSample sample))
        {
            position = sample.Position;
        }

        return new EnbSceneLightSnapshot(
            CreationOrdinal: worldObject.CreationOrdinal,
            GameId: worldObject.GameId,
            Diffuse: diffuse * intensity,
            Specular: specular * intensity,
            Ambient: ambient * intensity,
            Position: position,
            Direction: Vector3.UnitZ,
            Range: range,
            Falloff: 1f,
            Attenuation0: 1f,
            Attenuation1: attenuation1,
            Attenuation2: 0f,
            Theta: MathF.PI / 4f,
            Phi: MathF.PI / 4f);
    }

    private static float ReadMaterializedFloat(Net7AuxDataProperty property, string expectedPath, float fallback)
    {
        ValidateLightProperty(property, expectedPath, Net7AuxDataValueKind.Float32LittleEndian);
        if (property.State <= 1)
            return fallback;
        return property.Value is float value
            ? value
            : throw new InvalidDataException($"Materialized {expectedPath} does not contain a float value.");
    }

    private static int ReadMaterializedInt32(Net7AuxDataProperty property, string expectedPath)
    {
        ValidateLightProperty(property, expectedPath, Net7AuxDataValueKind.Raw4);
        if (property.Value is not byte[] { Length: 4 } bytes)
            throw new InvalidDataException($"Materialized {expectedPath} does not contain exactly four raw bytes.");
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static Vector3 ReadMaterializedVector3(Net7AuxDataProperty property, string expectedPath, Vector3 fallback)
    {
        ValidateLightProperty(property, expectedPath, Net7AuxDataValueKind.Raw12);
        if (property.State <= 1)
            return fallback;
        return ReadRawVector3(property, expectedPath);
    }

    private static Vector3? ReadRequiredMaterializedVector3(Net7AuxDataProperty property, string expectedPath)
    {
        ValidateLightProperty(property, expectedPath, Net7AuxDataValueKind.Raw12);
        // A non-zero Light.Attenuation mode with unmaterialized factors reads the
        // constructor's indeterminate raw bytes (safety rule): present no light
        // rather than inventing them, without stalling the scene snapshot.
        if (property.State <= 1)
            return null;
        return ReadRawVector3(property, expectedPath);
    }

    private static Vector3 ReadRawVector3(Net7AuxDataProperty property, string expectedPath)
    {
        if (property.Value is not byte[] { Length: 12 } bytes)
            throw new InvalidDataException($"Materialized {expectedPath} does not contain exactly 12 raw bytes.");
        return new Vector3(
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4))),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4))),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4))));
    }

    private static void ValidateLightProperty(
        Net7AuxDataProperty property,
        string expectedPath,
        Net7AuxDataValueKind expectedKind)
    {
        if (!string.Equals(property.Path, expectedPath, StringComparison.Ordinal) || property.Kind != expectedKind)
        {
            throw new InvalidDataException(
                $"Current Light AuxData contract expected {expectedPath} as {expectedKind}, got '{property.Path}' as {property.Kind}.");
        }
    }
    internal EnbSceneObjectSnapshot BuildRenderDefinition(Net7WorldState world,Net7WorldObject source,
        EnbRetailAssetBundle? assets,uint clientTime,Net7ItemTemplateCatalog? itemTemplates)
    {
        lock(_gate)
        {
            var tracked=GetOrCreateTracking(source);
            var local=world.LocalShipObjectId is uint localId?world.Objects.FindFirst(localId):null;
            var target=world.CurrentTargetId is uint targetId?world.Objects.FindFirst(targetId):null;
            return BuildObject(world,source,tracked,assets,clientTime,local,target,itemTemplates,
                BuildFactionReactions(world.LocalPlayerAuxData));
        }
    }

    private EnbSceneObjectSnapshot BuildObject(
        Net7WorldState world,
        Net7WorldObject worldObject,
        TrackedObject tracked,
        EnbRetailAssetBundle? assets,
        uint clientTime,
        Net7WorldObject? localShip,
        Net7WorldObject? currentTarget,
        Net7ItemTemplateCatalog? itemTemplates,
        IReadOnlyDictionary<string, float> factionReactions)
    {
        EnbSceneNavigationSnapshot? navigation = worldObject.Navigation is null
            ? null
            : new EnbSceneNavigationSnapshot(
                worldObject.Navigation.Signature,
                worldObject.Navigation.PlayerHasVisited != 0,
                worldObject.Navigation.NavType,
                worldObject.Navigation.IsHuge != 0);

        EnbSceneSpatialSnapshot? spatial = null;
        if (worldObject.Spatial is not null &&
            ReferenceEquals(world.Objects.FindFirst(worldObject.GameId), worldObject) &&
            world.TrySampleObjectPresentation(worldObject.GameId, clientTime, out Net7SpatialSample sample))
        {
            EnbSceneSpatialSnapshot sampledSpatial = new(
                MapSpatialKind(worldObject.Spatial!.Kind),
                new EnbSceneTransformSnapshot(
                    sample.ClientTime,
                    sample.Position,
                    sample.Orientation,
                    sample.Velocity,
                    sample.Speed,
                    sample.Rotation,
                    sample.OrientationColumn0){ProviderTransform=sample.ProviderTransform,BankHeadingTarget=sample.BankHeadingTarget},
                BuildPlanet(worldObject))
            {
                Advanced = BuildAdvanced(worldObject),
                FormationLeaderId = worldObject.Spatial.Formation?.LeaderId,
                ModelAudit = worldObject.Spatial.DescribeModel(clientTime),
                PacketTime = worldObject.Spatial.ClientTimestamp,
                ReceiptTime = worldObject.Spatial.ReceiptTime,
                TransitionMilliseconds = worldObject.Spatial.TransitionMilliseconds,
            };

            // Current retail exception: type 0x29 participates in
            // ordinary Create/MainView presentation, but
            // UpdateSpaceObjectRuntime returns before normal per-frame
            // RenderObject transform propagation.  Preserve the first
            // materialized presentation transform for that object lifetime.
            if (worldObject.ObjectType == 0x29)
            {
                tracked.Type29PinnedSpatial ??= sampledSpatial;
                spatial = tracked.Type29PinnedSpatial;
            }
            else
            {
                spatial = sampledSpatial;
            }
        }
        else if (world.TrySampleNullProviderPresentation(worldObject, clientTime, out Net7SpatialSample nullSample))
        {
            // Before its first positional packet the retail object already owns
            // the null provider: MainView+80 (514270) admits its RenderObj with an
            // identity transform and 6F3C60/6F4610 keep propagating that provider.
            // Type 0x29 pins only a real provider sample, as above.
            spatial = new EnbSceneSpatialSnapshot(
                EnbSceneSpatialProviderKind.Null,
                new EnbSceneTransformSnapshot(nullSample.ClientTime, nullSample.Position, nullSample.Orientation,
                    nullSample.Velocity, nullSample.Speed, nullSample.Rotation, nullSample.OrientationColumn0)
                    {ProviderTransform = nullSample.ProviderTransform},
                null);
        }

        tracked.PrepareAppearance(worldObject);
        var decals=tracked.decals;
        var colorization=tracked.colorization;
        var renderStateLinks=tracked.renderStateLinks;
        var activeRenderStates=tracked.activeRenderStates;
        var renderStateActions=tracked.renderStateActions;
        var attachments=tracked.attachments;
        var subParts=tracked.subParts;
        var nameDecal=tracked.nameDecal;

        EnbSceneShipLegoSnapshot? shipLegos = tracked.ShipLegos;
        EnbSceneShipPresentationSnapshot? shipPresentation = BuildShipPresentation(worldObject);
        EnbScenePlayerPresentationSnapshot? playerPresentation =
            BuildPlayerPresentation(worldObject, factionReactions);
        EnbSceneAuxDataMaterializationAuditSnapshot? shipAuxDataAudit = worldObject.AuxDataAudit is null
            ? null
            : new EnbSceneAuxDataMaterializationAuditSnapshot(
                worldObject.AuxDataAudit.AttemptCount,
                worldObject.AuxDataAudit.LargestDeltaLength,
                worldObject.AuxDataAudit.LargestSelector,
                worldObject.AuxDataAudit.LargestAccepted,
                worldObject.AuxDataAudit.LargestError)
            { LargestRetailCompatiblePartial = worldObject.AuxDataAudit.LargestRetailCompatiblePartial };
        EnbSceneColorizationApplicationAuditSnapshot? colorizationAudit = worldObject.ColorizationAudit is null
            ? null
            : new EnbSceneColorizationApplicationAuditSnapshot(
                worldObject.ColorizationAudit.ApplyCount,
                worldObject.ColorizationAudit.ActiveShipLegoAttachmentsAtLastApply,
                worldObject.ColorizationAudit.ActiveSubPartsAtLastApply,
                worldObject.ColorizationAudit.ObjectAuxDataAttemptsAtLastApply,
                worldObject.ColorizationAudit.ShipLegoScope
                    .Select(static entry => new EnbSceneColorizedShipLegoScopeEntry(
                        entry.Ordinal,
                        entry.BoneName,
                        entry.Type,
                        entry.BaseAsset,
                        entry.MaterializationRevision))
                    .ToImmutableArray(),
                worldObject.ColorizationAudit.SubPartScope
                    .Select(static entry => new EnbSceneColorizedSubPartScopeEntry(
                        entry.Ordinal,
                        entry.Name,
                        entry.PartValue,
                        entry.MaterializationRevision))
                    .ToImmutableArray());

        return new EnbSceneObjectSnapshot(
            RenderIdentity: tracked.RenderIdentity,
            VisualGeneration: tracked.VisualGeneration,
            GameId: worldObject.GameId,
            IsLocalShip: ReferenceEquals(worldObject, localShip),
            IsCurrentTarget: ReferenceEquals(worldObject, currentTarget),
            ObjectType: worldObject.ObjectType,
            BaseAsset: worldObject.BaseAsset,
            Scale: worldObject.Scale,
            HsvH: worldObject.HsvH,
            HsvS: worldObject.HsvS,
            HsvV: worldObject.HsvV,
            RelationshipRaw: worldObject.RelationshipRaw,
            IsAttacking: worldObject.IsAttacking != 0,
            Name: worldObject.Name,
            Radar: new EnbSceneRadarSnapshot(
                worldObject.AppearsInRadar,
                worldObject.RadarRange,
                navigation),
            Spatial: spatial,
            BaseAssetReference: worldObject.ObjectType == 0x08
                ? null
                : BuildBaseAssetReference(assets, worldObject.BaseAsset),
            ClientBaseAssetReference: BuildClientBaseAssetReference(assets, worldObject.BaseAsset),
            Decals: decals,
            ColorizationEntries: colorization,
            RenderStateLinks: renderStateLinks,
            ActiveRenderStates: activeRenderStates,
            RenderStateActions: renderStateActions,
            Attachments: attachments,
            NameDecal: nameDecal,
            SubParts: subParts,
            Avatar: BuildAvatar(worldObject.AvatarDescription),
            SpecialTargetEndpointId: worldObject.SpecialTargetEndpointId,
            SpecialTargetTransition: worldObject.SpecialTargetTransition)
        {
            HarvestablePercentFull = TryReadHarvestablePercentFull(worldObject),
            WorldCreationOrdinal = worldObject.CreationOrdinal,
            SpeakerRevision = worldObject.SpeakerRevision,
            SpeakerType = worldObject.SpeakerType,
            SpeakerColor = worldObject.SpeakerColor is {} speakerColor?new(speakerColor.X,speakerColor.Y,speakerColor.Z):null,
            CreatedBaseAsset = new(worldObject.Creation.BaseAsset,worldObject.Creation.Scale,
                worldObject.Creation.HsvH,worldObject.Creation.HsvS,worldObject.Creation.HsvV),
            HarvestableTechLevel = TryReadHarvestableTechLevel(worldObject),
            ShipLegos = shipLegos,
            LegoControllerMutations = worldObject.LegoControllerMutations.ToImmutableArray(),
            FixedShipVisualRevision = worldObject.FixedShipVisualRevision,
            BaseAssetMaterializationRevision = worldObject.BaseAssetMaterializationRevision,
            BaseVisualGeneration = tracked.BaseVisualGeneration,
            ShipPresentation = shipPresentation,
            PlayerPresentation = playerPresentation,
            RelationshipNotificationRevision = worldObject.RelationshipNotificationRevision,
            PvpUpdateRevision = worldObject.AuxData?.Root.Children.FirstOrDefault(p=>p.Path.EndsWith(".IsInPVP",StringComparison.Ordinal))?.UpdateRevision ?? 0,
            PvpUpdated = worldObject.AuxData?.Root.Children.FirstOrDefault(p=>p.Path.EndsWith(".IsInPVP",StringComparison.Ordinal))?.State > 1,
            DisplayNameProperties = BuildDisplayNameProperties(worldObject.AuxData),
            IsRescueBeaconActive = worldObject.AuxData?.Properties.FirstOrDefault(p => p.Path == "ShipAuxData.IsRescueBeaconActive")?.Value is byte beacon && beacon != 0,
            TargetVitals = BuildTargetVitals(worldObject, clientTime),
            Interruption = BuildInterruption(worldObject),
            ScannerFactionState = worldObject.AuxData is {Name:"ShipAuxData"} scannerShip?scannerShip.Resolve(1932).State:0,
            ScannerTechLevelState = worldObject.AuxData is {Name:"Harvestable"} scannerHarvest?scannerHarvest.Properties.FirstOrDefault(p=>p.Path=="Harvestable.TechLevel")?.State??0:0,
            ScannerTechLevelRevision = worldObject.AuxData is {Name:"Harvestable"} scannerHarvestRevision?scannerHarvestRevision.Properties.FirstOrDefault(p=>p.Path=="Harvestable.TechLevel")?.UpdateRevision??0:0,
            HarvestableResourcesState = worldObject.AuxData is {Name:"Harvestable"} resourcesAux
                ? resourcesAux.Properties.FirstOrDefault(p=>p.Path=="Harvestable.HarvestableResources")?.State??0:0,
            HarvestableResourceSlotStates = worldObject.AuxData is {Name:"Harvestable"} slotAux
                ? Enumerable.Range(0,HarvestableResourceCount).Select(slot=>slotAux.Resolve(HarvestableFirstResourceItemTemplateIdPropertyId+slot*HarvestableResourceStride).Parent!.State).ToImmutableArray()
                : ImmutableArray<int>.Empty,
            HarvestableResources = BuildHarvestableResources(worldObject, itemTemplates),
            ShipAuxDataAudit = shipAuxDataAudit,
            ColorizationAudit = colorizationAudit,
            NameDecalApplyCount = worldObject.NameDecalApplyCount,
            NameDecalColorizationApplyCount = worldObject.NameDecal is null ? null : worldObject.NameDecalColorizationApplyCount,
            DecalApplyCount = worldObject.DecalApplyCount,
            DamageDecalPools = BuildDamageDecalPools(worldObject),
        };
    }

    private static ImmutableArray<EnbSceneDamageDecalPoolSnapshot> BuildDamageDecalPools(Net7WorldObject obj)
    {
        if (obj.ObjectType is not (0 or 1 or 25) || obj.AuxData is not {} aux) return [];
        var result = ImmutableArray.CreateBuilder<EnbSceneDamageDecalPoolSnapshot>(3);
        foreach (string name in new[] { "DamageSpot", "DamageLine", "DamageBlotch" })
        {
            var array = aux.Root.Children.FirstOrDefault(p => p.Path == $"{aux.Name}.{name}");
            if (array is null) { result.Add(new(0, 0, [])); continue; }
            result.Add(new(array.State, array.UpdateRevision, [.. array.Children.Select(child =>
            {
                // 006F70AF/00705AD0 and ConstructDefaultDamageVisualValue:
                // constructor direction=(1,0,0), asset=-1, scale=0.
                if (child.Value is not byte[] bytes)
                    return new EnbSceneDamageDecalSnapshot(child.State, child.UpdateRevision, Vector3.UnitX, -1, 0);
                var data = bytes.AsSpan();
                return new EnbSceneDamageDecalSnapshot(child.State, child.UpdateRevision,
                    new(BinaryPrimitives.ReadSingleLittleEndian(data), BinaryPrimitives.ReadSingleLittleEndian(data[4..]),
                        BinaryPrimitives.ReadSingleLittleEndian(data[8..])),
                    BinaryPrimitives.ReadInt32LittleEndian(data[12..]), BinaryPrimitives.ReadSingleLittleEndian(data[16..]));
            })]));
        }
        return result.ToImmutable();
    }

    private static EnbSceneShipPresentationSnapshot? BuildShipPresentation(Net7WorldObject worldObject)
    {
        Net7AuxDataSchema? auxData = worldObject.AuxData;
        if (auxData is null ||
            !string.Equals(auxData.Name, "ShipAuxData", StringComparison.Ordinal) ||
            auxData.Properties.Count <= ShipEquippedFirstItemTemplateIdPropertyId + ((ShipEquippedCount - 1) * ShipEquippedStride))
        {
            return null;
        }

        // Object AuxData roots keep null until a scalar value is first received.
        // Retail StateTwo can legitimately mark a fresh property without replacing
        // that constructor value, so presentation supplies only the proven retail
        // defaults at this boundary while preserving null as the storage sentinel.
        Net7AuxDataProperty isOrganicProperty = auxData.Resolve(ShipIsOrganicPropertyId);
        bool? isOrganic = isOrganicProperty.State == 0
            ? null
            : isOrganicProperty.Value switch
            {
                byte organicByte => organicByte != 0,
                null => false,
                _ => throw new InvalidDataException("Materialized ShipAuxData.IsOrganic is not a BooleanByte value."),
            };

        int? privateWarpState = ReadRetainedShipInt32(
            auxData.Resolve(ShipPrivateWarpStatePropertyId),
            "ShipAuxData.PrivateWarpState");
        int? globalWarpState = ReadRetainedShipInt32(
            auxData.Resolve(ShipGlobalWarpStatePropertyId),
            "ShipAuxData.GlobalWarpState");
        int? engineThrustState = ReadRetainedShipInt32(
            auxData.Resolve(ShipEngineThrustStatePropertyId),
            "ShipAuxData.EngineThrustState");

        Net7AuxDataProperty inventory = auxData.Resolve(ShipInventoryPropertyId);
        Net7AuxDataProperty equipMountModelProperty = auxData.Resolve(ShipEquipMountModelPropertyId);
        string? equipMountModel = equipMountModelProperty.State == 0
            ? null
            : equipMountModelProperty.Value switch
            {
                Net7AuxDataString model => model.Text,
                null => string.Empty,
                _ => throw new InvalidDataException("Materialized ShipAuxData.Inventory.EquipMountModel is not a string."),
            };

        int materializedMountCount = 0;
        var mounts = ImmutableArray.CreateBuilder<EnbSceneShipInventoryMountSnapshot>();
        for (int ordinal = 0; ordinal < ShipInventoryMountCount; ordinal++)
        {
            Net7AuxDataProperty mountData = auxData.Resolve(ShipMountsFirstPropertyId + ordinal);
            Net7AuxDataProperty boneName = auxData.Resolve(ShipMountBoneNamesFirstPropertyId + ordinal);
            bool mountMaterialized = mountData.State != 0;
            if (mountMaterialized)
                materializedMountCount++;
            if (!mountMaterialized && boneName.State == 0)
                continue;

            string name = boneName.State == 0
                ? string.Empty
                : boneName.Value switch
                {
                    Net7AuxDataString bone => bone.Text,
                    null => string.Empty,
                    _ => throw new InvalidDataException($"Materialized ShipAuxData.Inventory.MountBoneNames[{ordinal}] is not a string."),
                };
            mounts.Add(new EnbSceneShipInventoryMountSnapshot(ordinal, name, mountMaterialized));
        }

        var equipped = ImmutableArray.CreateBuilder<EnbSceneShipEquippedSlotSnapshot>();
        for (int ordinal = 0; ordinal < ShipEquippedCount; ordinal++)
        {
            Net7AuxDataProperty slot = auxData.Resolve(ShipEquippedFirstAggregatePropertyId + (ordinal * ShipEquippedStride));
            Net7AuxDataProperty itemTemplate = auxData.Resolve(ShipEquippedFirstItemTemplateIdPropertyId + (ordinal * ShipEquippedStride));
            if (slot.State == 0 && itemTemplate.State == 0)
                continue;

            int itemTemplateId = itemTemplate.State == 0
                ? -1
                : itemTemplate.Value switch
                {
                    int id => id,
                    null => -1,
                    _ => throw new InvalidDataException($"Materialized ShipAuxData.Inventory.Equipped[{ordinal}].ItemTemplateID is not an Int32."),
                };
            equipped.Add(new EnbSceneShipEquippedSlotSnapshot(ordinal, itemTemplateId));
        }

        float ShakeValue(int id) => auxData.Resolve(id) is { State: > 0, Value: float value } ? value : 0;
        var quadrantProperty=auxData.Root.Children.FirstOrDefault(p=>p.Path=="ShipAuxData.VisibleQuadrantDamagePercent");
        return new EnbSceneShipPresentationSnapshot(
            IsOrganic: isOrganic,
            PrivateWarpState: privateWarpState,
            GlobalWarpState: globalWarpState,
            EngineThrustState: engineThrustState,
            InventoryMaterialized: inventory.State != 0,
            EquipMountModel: equipMountModel,
            MaterializedMountCount: materializedMountCount,
            Mounts: mounts.ToImmutable(),
            EquippedSlots: equipped.ToImmutable())
        {
            EngineTrailType = ReadRetainedShipInt32(auxData.Resolve(ShipEngineTrailTypePropertyId), "ShipAuxData.EngineTrailType"),
            //6FC118 tests CurrentStats presence;6FC12A reads its retained
            //WarpPowerLevel at the object tick, after all packet updates.
            WarpPowerLevel = auxData.Resolve(1910).Parent?.State > 0
                ? auxData.Resolve(1910).Value is int warpPower ? warpPower : 1 : 1,
            //006FE615 defaults an absent array to zero damage. A materialized
            //array supplies health, which006FE69C subtracts from one.
            VisibleQuadrantHealth=quadrantProperty is {State: >0} && quadrantProperty.Children.Count==4
                ?quadrantProperty.Children.Select(p=>p.Value is float value?value:1f).ToImmutableArray():[],
            PrivateWarpRevision = auxData.Resolve(38).UpdateRevision,
            GlobalWarpRevision = auxData.Resolve(39).UpdateRevision,
            PrivateWarpMaterialized = auxData.Resolve(38).State >= 2,
            GlobalWarpMaterialized = auxData.Resolve(39).State >= 2,
            GlobalWarpUpdatesSuppressed = worldObject.GlobalWarpUpdatesSuppressed,
            WarpChanges = worldObject.WarpPresentationChanges.Select(c =>
                new EnbSceneWarpPresentationChange(c.Sequence, c.State, c.PowerLevel, c.EngineTrailType)).ToImmutableArray(),
            PrivateWarpChanges = worldObject.PrivateWarpPresentationChanges.Select(c =>
                new EnbSceneWarpPresentationChange(c.Sequence, c.State, c.PowerLevel, c.EngineTrailType)).ToImmutableArray(),
            Shake = new(auxData.Resolve(43).UpdateRevision,
                auxData.Resolve(43).State > 1 ? new Vector3(ShakeValue(43), ShakeValue(44), ShakeValue(45)) : null,
                auxData.Resolve(46).UpdateRevision, auxData.Resolve(46).State > 1 ? ShakeValue(46) : null),
        };
    }

    private static EnbSceneDisplayNameProperties? BuildDisplayNameProperties(Net7AuxDataSchema? auxData)
    {
        if(auxData is null)return null;
        string? Read(string name)
        {
            var property=auxData.Root.Children.FirstOrDefault(p=>p.Path==auxData.Name+"."+name);
            if(property is null || property.State==0)return null;
            return property.Value is Net7AuxDataString value?value.Text:null;
        }
        return new(Read("Name"),Read("Owner"),Read("Title"),Read("Rank"));
    }

    private static EnbScenePlayerPresentationSnapshot? BuildPlayerPresentation(
        Net7WorldObject worldObject,
        IReadOnlyDictionary<string, float> factionReactions)
    {
        Net7AuxDataSchema? auxData = worldObject.AuxData;
        if (auxData is null ||
            !string.Equals(auxData.Name, "ShipAuxData", StringComparison.Ordinal) ||
            auxData.Properties.Count <= ShipOtherGuildTagColorPropertyId)
        {
            return null;
        }

        return new EnbScenePlayerPresentationSnapshot(
            Owner: ReadRetainedShipString(auxData.Resolve(ShipOwnerPropertyId), "ShipAuxData.Owner"),
            Title: ReadRetainedShipString(auxData.Resolve(ShipTitlePropertyId), "ShipAuxData.Title"),
            Rank: ReadRetainedShipString(auxData.Resolve(ShipRankPropertyId), "ShipAuxData.Rank"),
            CombatLevel: ReadRetainedShipInt32(auxData.Resolve(ShipCombatLevelPropertyId), "ShipAuxData.CombatLevel"),
            ShieldPercent: ReadRetainedShipFloat(auxData.Resolve(ShipShieldPercentStartValuePropertyId), "ShipAuxData.ShieldPercent.StartValue"),
            GuildName: ReadRetainedShipString(auxData.Resolve(ShipGuildNamePropertyId), "ShipAuxData.GuildName"),
            GuildRank: ReadRetainedShipRawInt32(auxData.Resolve(ShipGuildRankPropertyId), "ShipAuxData.GuildRank", -1),
            GuildRankName: ReadRetainedShipString(auxData.Resolve(ShipGuildRankNamePropertyId), "ShipAuxData.GuildRankName"),
            SameGuildTagColor: ReadRetainedShipVector3(auxData.Resolve(ShipSameGuildTagColorPropertyId), "ShipAuxData.SameGuildTagColor"),
            OtherGuildTagColor: ReadRetainedShipVector3(auxData.Resolve(ShipOtherGuildTagColorPropertyId), "ShipAuxData.OtherGuildTagColor"))
        {
            IsInPvp = ReadRetainedShipBooleanByte(
                auxData.Resolve(Net7AuxDataPropertyIds.ShipIsInPvp),
                "ShipAuxData.IsInPVP"),
            FactionIdentifier = ReadRetainedShipString(
                auxData.Resolve(Net7AuxDataPropertyIds.ShipFactionIdentifier),
                "ShipAuxData.FactionIdentifier"),
            FactionReaction = ReadFactionReaction(factionReactions, auxData),
        };
    }

    private static float? ReadFactionReaction(
        IReadOnlyDictionary<string, float> factionReactions,
        Net7AuxDataSchema targetShipAuxData)
    {
        string? factionKey = ReadRetainedShipString(
            targetShipAuxData.Resolve(Net7AuxDataPropertyIds.ShipFactionIdentifier),
            "ShipAuxData.FactionIdentifier");
        if (string.IsNullOrEmpty(factionKey))
            return null;

        return factionReactions.TryGetValue(factionKey, out float reaction)
            ? reaction
            : null;
    }

    private static IReadOnlyDictionary<string, float> BuildFactionReactions(
        Net7AuxDataSchema localPlayerAuxData)
    {
        var result = new Dictionary<string, float>(StringComparer.Ordinal);
        for (int index = 0; index < Net7AuxDataPropertyIds.LocalPlayerReputationFactionCount; index++)
        {
            int offset = index * Net7AuxDataPropertyIds.LocalPlayerReputationFactionStride;
            Net7AuxDataProperty nameProperty = localPlayerAuxData.Resolve(
                Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Name + offset);
            if (nameProperty.Value is not Net7AuxDataString name || string.IsNullOrEmpty(name.Text))
                continue;
            if(result.ContainsKey(name.Text))continue;

            Net7AuxDataProperty reactionProperty = localPlayerAuxData.Resolve(
                Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Reaction + offset);
            // 0060F6D0 scans in vector order and stops at the first matching key.
            result.TryAdd(name.Text, reactionProperty.Value switch
            {
                float reaction => reaction, // raw copy (004A5300); 0060F6D0 uses the stored float
                null => 0f,
                _ => throw new InvalidDataException(
                    "Materialized LocalPlayerAuxData Reputation faction Reaction is not a Float32 value."),
            });
        }
        return result;
    }

    private static EnbSceneInterruptionSnapshot? BuildInterruption(Net7WorldObject worldObject)
    {
        if(worldObject.AuxData is not {Name:"ShipAuxData"} data || data.Properties.Count<=1931)return null;
        var name=data.Resolve(1928);var state=data.Resolve(1929);
        var time=data.Resolve(1930);var progress=data.Resolve(1931);
        return new(state.State!=0?ReadRetainedShipRawInt32(state,state.Path,0):null,
            name.State!=0?ReadRetainedShipString(name,name.Path):null,
            time.State!=0 && time.Value is Net7AuxDataTime activation?activation.RelativeValue:null,
            progress.State!=0?ReadRetainedShipFloat(progress,progress.Path):null);
    }

    private static EnbSceneTargetVitalsSnapshot? BuildTargetVitals(
        Net7WorldObject worldObject,
        uint clientTime)
    {
        Net7AuxDataSchema? auxData = worldObject.AuxData;
        if (auxData is null ||
            !string.Equals(auxData.Name, "ShipAuxData", StringComparison.Ordinal) ||
            auxData.Properties.Count <= ShipMaximumHullPointsPropertyId)
        {
            return null;
        }

        return new EnbSceneTargetVitalsSnapshot(
            EvaluateShieldPercent(auxData, clientTime),
            ReadRetainedShipFloat(auxData.Resolve(ShipMaximumShieldPowerPropertyId), "ShipAuxData.MaxShieldPower"),
            ReadRetainedShipFloat(auxData.Resolve(ShipHullPointsPropertyId), "ShipAuxData.HullPoints"),
            ReadRetainedShipFloat(auxData.Resolve(ShipMaximumHullPointsPropertyId), "ShipAuxData.MaxHullPoints"));
    }

    private static float? EvaluateShieldPercent(Net7AuxDataSchema auxData, uint clientTime)
        => EvaluateHudPercent(auxData, clientTime, ShipShieldPercentStartValuePropertyId,
            ShipShieldPercentChangePerTickPropertyId, ShipShieldPercentEndTimePropertyId, "ShieldPercent");

    public static EnbSceneCockpitSnapshot? BuildCockpit(Net7WorldObject? ship, uint clientTime, int? presentationMode = null)
    {
        if (ship?.AuxData is not { Name: "ShipAuxData" } data || data.Properties.Count <= 31) return null;
        // Current Ship.csv fields and UpdateShipHealthHud @ 005DC4A0.
        return new(EvaluateHudPercent(data, clientTime, 8, 7, 6, "EnergyPercent"),
            ReadRetainedShipFloat(data.Resolve(9), "ShipAuxData.MaxEnergyPower"),
            BuildTargetVitals(ship, clientTime),
            ReadRetainedShipFloat(data.Resolve(20), "ShipAuxData.MaxSpeed"),
            ReadRetainedShipBooleanByte(data.Resolve(31), "ShipAuxData.IsAutoFollowing") == true)
            { Warp = Net7WarpStatus.Read(data, presentationMode) };
    }

    internal static bool SampleCockpit(Net7WorldObject? ship,uint clientTime,EnbCockpitFrame target,EnbVitalsFrame vitals)
    {
        if(ship?.AuxData is not {Name:"ShipAuxData"} data || data.Properties.Count<=31)return false;
        // Same property arithmetic as the detached diagnostic builder; mutable
        // frame-owned fields replace its per-frame record graph.
        target.EnergyPercent=EvaluateHudPercent(data,clientTime,8,7,6,"EnergyPercent");
        target.MaximumEnergyPower=ReadRetainedShipFloat(data.Resolve(9),"ShipAuxData.MaxEnergyPower");
        target.MaximumSpeed=ReadRetainedShipFloat(data.Resolve(20),"ShipAuxData.MaxSpeed");
        target.IsAutoFollowing=ReadRetainedShipBooleanByte(data.Resolve(31),"ShipAuxData.IsAutoFollowing")==true;
        vitals.ShieldPercent=EvaluateShieldPercent(data,clientTime);
        vitals.MaximumShieldPower=ReadRetainedShipFloat(data.Resolve(ShipMaximumShieldPowerPropertyId),"ShipAuxData.MaxShieldPower");
        vitals.HullPoints=ReadRetainedShipFloat(data.Resolve(ShipHullPointsPropertyId),"ShipAuxData.HullPoints");
        vitals.MaximumHullPoints=ReadRetainedShipFloat(data.Resolve(ShipMaximumHullPointsPropertyId),"ShipAuxData.MaxHullPoints");
        target.Vitals=vitals;return true;
    }

    private static float? EvaluateHudPercent(Net7AuxDataSchema auxData, uint clientTime,
        int startId, int rateId, int endId, string name)
    {
        var startProperty=auxData.Resolve(startId);
        float? start = ReadRetainedShipFloat(startProperty,startProperty.Path);
        if (start is not float startValue)
            return null;

        Net7AuxDataProperty rateProperty = auxData.Resolve(rateId);
        Net7AuxDataProperty endProperty = auxData.Resolve(endId);
        if (rateProperty.Value is not float rate || !float.IsFinite(rate) || rate == 0f ||
            endProperty.Value is not Net7AuxDataTime end)
        {
            return startValue;
        }

        // EvaluateDeltaInterpolatedFloatPropertyAtTime @ 0x004A6380 (40DDD2 thunk).
        // ShieldPercent stores the starting clamp, signed change per client
        // tick and corrected end time. The HUD asks the property for its value
        // at the current client clock; displaying StartValue directly leaves
        // damage/recharge bars stale.
        return EnbNext.RetailMath.EnbRetailDeltaFloat.Evaluate(startValue, rate, end.RelativeValue, clientTime);
    }

    private static ImmutableArray<EnbSceneHarvestableResourceSnapshot> BuildHarvestableResources(
        Net7WorldObject worldObject,
        Net7ItemTemplateCatalog? itemTemplates)
    {
        if (worldObject.ObjectType != 0x26 ||
            worldObject.AuxData is not Net7AuxDataSchema auxData ||
            !string.Equals(auxData.Name, "Harvestable", StringComparison.Ordinal))
        {
            return ImmutableArray<EnbSceneHarvestableResourceSnapshot>.Empty;
        }

        var resources = ImmutableArray.CreateBuilder<EnbSceneHarvestableResourceSnapshot>();
        for (int slot = 0; slot < HarvestableResourceCount; slot++)
        {
            int itemPropertyId = HarvestableFirstResourceItemTemplateIdPropertyId + (slot * HarvestableResourceStride);
            int countPropertyId = itemPropertyId + 1;
            if (countPropertyId >= auxData.Properties.Count)
                break;

            int? itemId = ReadRetainedShipInt32(
                auxData.Resolve(itemPropertyId),
                $"HarvestableResources.{slot}.ItemTemplateID");
            int? stackCount = ReadRetainedShipInt32(
                auxData.Resolve(countPropertyId),
                $"HarvestableResources.{slot}.StackCount");
            // 5BFB00 excludes only the two inventory sentinels; other ID bits reach the registry.
            if (itemId is int id && id is not -1 and not -2 && stackCount is > 0)
            {
                string? name = itemTemplates is not null &&
                    itemTemplates.TryGet(unchecked((uint)id), out Net7ItemTemplate? template)
                        ? template?.Name.EffectiveText
                        : null;
                resources.Add(new EnbSceneHarvestableResourceSnapshot(
                    // 68AAA0 falls back to the registry entry constructed at
                    // 726555; the harvest formatter uses its name unchanged.
                    slot, itemId.Value, stackCount.Value, name ?? "Null Item"));
            }
        }
        return resources.ToImmutable();
    }

    private static int? ReadLocalProspectSkillLevel(Net7AuxDataSchema localPlayerAuxData)
    {
        Net7AuxDataProperty property = localPlayerAuxData.Resolve(LocalPlayerProspectSkillLevelPropertyId);
        return property.Value switch
        {
            int value => value,
            null when property.State == 0 => null,
            null => 0,
            _ => throw new InvalidDataException(
                "Materialized LocalPlayerAuxData RPGInfo.Skills.41.Level is not Int32."),
        };
    }

    private static string? ReadTargetThreat(Net7WorldObject? localShip, bool sound = false)
    {
        if (localShip?.AuxData is not Net7AuxDataSchema auxData ||
            !string.Equals(auxData.Name, "ShipAuxData", StringComparison.Ordinal))
        {
            return null;
        }

        var property = auxData.Resolve(sound ? Net7AuxDataPropertyIds.ShipTargetThreatSound : Net7AuxDataPropertyIds.ShipTargetThreat);
        return property.State != 0 ? ReadRetainedShipString(property, property.Path) : null;
    }

    private static int? ReadTargetThreatLevel(Net7WorldObject? localShip)
    {
        if (localShip?.AuxData is not Net7AuxDataSchema auxData ||
            !string.Equals(auxData.Name, "ShipAuxData", StringComparison.Ordinal))
        {
            return null;
        }

        var property = auxData.Resolve(Net7AuxDataPropertyIds.ShipTargetThreatLevel);
        // 006F66E0 initializes TargetThreatLevel (+14A4) to -1; an unreceived
        // value must retain the server assessment text in 0060ED60.
        return property.State != 0 ? ReadRetainedShipInt32(property, property.Path, -1) : null;
    }

    private static ImmutableArray<uint> ResolveCurrentTargetVerbs(
        Net7GameplayState? gameplay,
        uint? authoritativeTargetId)
    {
        if (gameplay is null)
            return ImmutableArray<uint>.Empty;

        uint? targetId = gameplay.HasLocalTargetSelection
            ? gameplay.LocalSelectedTargetId
            : authoritativeTargetId;
        return targetId is uint id && gameplay.VerbLists.TryGetValue(id, out IReadOnlyList<uint>? verbs)
            ? verbs.ToImmutableArray()
            : ImmutableArray<uint>.Empty;
    }

    private static EnbSceneAdvancedSpatialSnapshot? BuildAdvanced(Net7WorldObject worldObject)
    {
        Net7AdvancedPositionUpdate? advanced = worldObject.Spatial?.Advanced;
        return advanced is null
            ? null
            : new EnbSceneAdvancedSpatialSnapshot(
                advanced.OptionalMask,
                advanced.MovementId,
                advanced.CurrentSpeed,
                advanced.SetSpeed,
                advanced.Acceleration,
                advanced.RotY,
                advanced.DesiredY,
                advanced.RotZ,
                advanced.DesiredZ,
                advanced.ImpartedVelocity,
                advanced.ImpartedSpin,
                advanced.ImpartedRoll,
                advanced.ImpartedPitch,
                advanced.UpdatePeriod);
    }

    private static int? ReadRetainedShipInt32(Net7AuxDataProperty property, string name, int initialValue = 0)
    {
        // AuxData state is callback/revision state rather than value lifetime.
        // A typed retained value therefore remains the current value after a
        // later state-zero operation. A fresh state-two property still has its
        // retail constructor value even though EnbNext preserves null as the
        // never-materialized storage sentinel.
        return property.Value switch
        {
            int value => value,
            null when property.State == 0 => null,
            null => initialValue,
            _ => throw new InvalidDataException($"Materialized {name} is not an Int32 value."),
        };
    }

    private static string? ReadRetainedShipString(Net7AuxDataProperty property, string name)
        => property.Value switch
        {
            Net7AuxDataString value => value.Text,
            null when property.State == 0 => null,
            null => string.Empty,
            _ => throw new InvalidDataException($"Materialized {name} is not a string value."),
        };

    private static float? ReadRetainedShipFloat(Net7AuxDataProperty property, string name)
        => property.Value switch
        {
            // The AuxData reader copies the four raw bytes (004A5300) and retail
            // consumers read the float as stored; no finiteness gate exists. A
            // non-finite server value must not stall the whole scene snapshot.
            float value => value,
            null when property.State == 0 => null,
            null => 0f,
            _ => throw new InvalidDataException($"Materialized {name} is not a Float32 value."),
        };

    private static bool? ReadRetainedShipBooleanByte(Net7AuxDataProperty property, string name)
        => property.Value switch
        {
            byte value => value != 0,
            null when property.State == 0 => null,
            null => false,
            _ => throw new InvalidDataException($"Materialized {name} is not a BooleanByte value."),
        };

    private static EnbSceneExperienceSnapshot BuildExperience(Net7AuxDataSchema localPlayerAuxData)
    {
        float Read(int id, string name)
        {
            var p = localPlayerAuxData.Resolve(id);
            if (p.Path != "LocalPlayerAuxData.RPGInfo." + name || p.Kind != Net7AuxDataValueKind.Float32LittleEndian)
                throw new InvalidDataException("Unexpected experience property: " + p.Path);
            return p.State != 0 && p.Value is float f && float.IsFinite(f) ? Math.Clamp(f, 0, 1) : 0;
        }
        return new(Read(4449, "CombatExp"), Read(4453, "ExploreExp"), Read(4451, "TradeExp"));
    }

    private static ImmutableArray<uint> BuildGroupMemberIds(Net7AuxDataSchema localPlayerAuxData)
    {
        var result = ImmutableArray.CreateBuilder<uint>(Net7AuxDataPropertyIds.LocalPlayerGroupMemberCount);
        for (int index = 0; index < Net7AuxDataPropertyIds.LocalPlayerGroupMemberCount; index++)
        {
            int propertyId = Net7AuxDataPropertyIds.LocalPlayerGroupMember0GameId +
                (index * Net7AuxDataPropertyIds.LocalPlayerGroupMemberStride);
            if (localPlayerAuxData.Resolve(propertyId).Value is int signedId && signedId != 0)
                result.Add(unchecked((uint)signedId));
        }
        return result.ToImmutable();
    }

    private static int ReadRetainedShipRawInt32(Net7AuxDataProperty property, string name, int fallback)
    {
        // State-two marks callbacks, not a received value. Current retail
        // ConstructShipAuxData (006F66E0) initializes GuildRank to -1.
        if (property.Value is null)
            return fallback;
        if (property.Value is not byte[] { Length: 4 } bytes)
            throw new InvalidDataException($"Materialized {name} is not exactly four raw bytes.");
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static Vector3? ReadRetainedShipVector3(Net7AuxDataProperty property, string name)
    {
        // An untouched raw color is unavailable even after state-two. Do not
        // invent RGB bytes or reject the entire world snapshot for its absence.
        if (property.Value is null)
            return null;
        // 00700670 -> 0063AC60 retains all three received components.
        return ReadRawVector3(property, name);
    }

    private static int? ReadCurrentScanRange(Net7WorldObject? localShip)
    {
        if (localShip?.AuxData is not Net7AuxDataSchema auxData ||
            !string.Equals(auxData.Name, "ShipAuxData", StringComparison.Ordinal) ||
            auxData.Properties.Count <= Net7AuxDataPropertyIds.ShipCurrentScanRange)
        {
            return null;
        }

        return ReadRetainedShipInt32(
            auxData.Resolve(Net7AuxDataPropertyIds.ShipCurrentScanRange),
            "ShipAuxData.CurrentStats.ScanRange");
    }

    public long ActiveAppearancePreparationCount=>_trackedObjects.Values.Sum(owner=>owner.AppearancePreparations);
    public long ShipLegoPreparationCount {get;private set;}
    public long ActiveCompositionPreparationCount=>_trackedObjects.Values.Sum(owner=>owner.CompositionPreparations);
    private EnbSceneShipLegoSnapshot? BuildShipLegos(Net7WorldObject worldObject,TrackedObject tracked)
    {
        var aux=worldObject.AuxData;
        ulong revision=aux is {Name:"ShipAuxData"} && aux.Properties.Count>ShipLegosPropertyId
            ?aux.Resolve(ShipLegosPropertyId).MaterializedRevision:0;
        if(tracked.LegoProjectionReady && tracked.LegoProjectionRevision==revision &&
            tracked.LegoFixedRevision==worldObject.FixedShipVisualRevision)return tracked.ShipLegos;
        // Source-owned fields invalidate locally; unchanged publication does not
        // reconstruct attachments or their strings. Published values stay detached.
        var result=BuildShipLegosCore(worldObject);
        tracked.ShipLegos=result;tracked.LegoProjectionRevision=revision;
        tracked.LegoFixedRevision=worldObject.FixedShipVisualRevision;tracked.LegoProjectionReady=true;
        ShipLegoPreparationCount++;return result;
    }

    private static EnbSceneShipLegoSnapshot? BuildShipLegosCore(Net7WorldObject worldObject)
    {
        Net7AuxDataSchema? auxData = worldObject.AuxData;
        if (auxData is null ||
            !string.Equals(auxData.Name, "ShipAuxData", StringComparison.Ordinal) ||
            auxData.Properties.Count <= ShipLegosFirstAttachmentPropertyId + ((ShipLegosAttachmentCount - 1) * ShipLegosAttachmentStride) + 4)
        {
            return null;
        }

        Net7AuxDataProperty legos = auxData.Resolve(ShipLegosPropertyId);
        Net7AuxDataProperty scaleProperty = auxData.Resolve(ShipLegosScalePropertyId);

        // The AuxData state marker is revision/callback state, not value
        // lifetime. Current retail state-zero explicitly leaves scalar value
        // storage intact, while the Lego controller keeps the physical child
        // RenderObjects it previously materialized. Reconstruct presentation
        // from retained values as well as nonzero revision markers. Object-root
        // constructor defaults are null in EnbNext, so a retained typed value is
        // unambiguous evidence that the field was materialized at least once.
        bool retainedScale = scaleProperty.Value is not null;
        float scale = scaleProperty.Value switch
        {
            float value => value, // raw copy (004A5300), as for the other retained floats
            null => 1f,
            _ => throw new InvalidDataException("Retained ShipAuxData.Legos.Scale does not contain a float value."),
        };

        var attachments = ImmutableArray.CreateBuilder<EnbSceneShipLegoAttachmentSnapshot>();
        for (int ordinal = 0; ordinal < ShipLegosAttachmentCount; ordinal++)
        {
            int aggregateId = ShipLegosFirstAttachmentPropertyId + (ordinal * ShipLegosAttachmentStride);
            Net7AuxDataProperty aggregate = auxData.Resolve(aggregateId);
            Net7AuxDataProperty bone = auxData.Resolve(aggregateId + 1);
            Net7AuxDataProperty type = auxData.Resolve(aggregateId + 2);
            Net7AuxDataProperty asset = auxData.Resolve(aggregateId + 3);
            Net7AuxDataProperty data = auxData.Resolve(aggregateId + 4);

            bool revisionPresent =
                aggregate.State != 0 || bone.State != 0 || type.State != 0 || asset.State != 0 || data.State != 0;
            bool retainedValuePresent =
                bone.Value is not null || type.Value is not null || asset.Value is not null || data.Value is not null;
            if (!revisionPresent && !retainedValuePresent)
                continue;

            string boneName = ReadRetainedShipLegoString(bone, "BoneName");
            string dataText = ReadRetainedShipLegoString(data, "Data");
            int typeValue = ReadRetainedShipLegoInt(type, "Type");
            int assetValue = ReadRetainedShipLegoInt(asset, "Assets");
            attachments.Add(new EnbSceneShipLegoAttachmentSnapshot(ordinal, boneName, typeValue, assetValue, dataText)
            {
                MaterializationRevision = worldObject.ShipLegoAttachmentMaterializationRevisions[ordinal],
            });
        }

        if (legos.State == 0 && !retainedScale && attachments.Count == 0)
            return null;

        return new EnbSceneShipLegoSnapshot(scale, attachments.ToImmutable());
    }

    private static string ReadRetainedShipLegoString(Net7AuxDataProperty property, string fieldName)
    {
        return property.Value switch
        {
            Net7AuxDataString value => value.Text,
            null => string.Empty,
            _ => throw new InvalidDataException($"Retained ShipAuxData.Legos attachment {fieldName} is not a string."),
        };
    }

    private static int ReadRetainedShipLegoInt(Net7AuxDataProperty property, string fieldName)
    {
        return property.Value switch
        {
            int value => value,
            null => 0,
            _ => throw new InvalidDataException($"Retained ShipAuxData.Legos attachment {fieldName} is not an Int32."),
        };
    }

    private static float? TryReadHarvestablePercentFull(Net7WorldObject worldObject)
    {
        if (worldObject.ObjectType != 0x26 ||
            worldObject.AuxData is not Net7AuxDataSchema auxData ||
            auxData.Name != "Harvestable")
        {
            return null;
        }

        Net7AuxDataProperty? percentFull = auxData.Properties.FirstOrDefault(static property =>
            property.Path == "Harvestable.PercentFull");
        if (percentFull is null ||
            percentFull.State <= 1 ||
            percentFull.Value is not float value ||
            !float.IsFinite(value))
        {
            return null;
        }

        return value;
    }

    private static int? TryReadHarvestableTechLevel(Net7WorldObject worldObject)
    {
        if (worldObject.ObjectType != 0x26 ||
            worldObject.AuxData is not Net7AuxDataSchema auxData ||
            auxData.Name != "Harvestable")
        {
            return null;
        }

        Net7AuxDataProperty? techLevel = auxData.Properties.FirstOrDefault(static property =>
            property.Path == "Harvestable.TechLevel");
        return techLevel?.Value switch
        {
            int value => value,
            null => null,
            _ => throw new InvalidDataException("Materialized Harvestable.TechLevel is not an Int32 value."),
        };
    }

    private TrackedObject GetOrCreateTracking(Net7WorldObject worldObject)
    {
        if (_trackedObjects.TryGetValue(worldObject, out TrackedObject? tracked))
            return tracked;

        tracked = new TrackedObject(
            NextRenderIdentity(),
            1,
            default);
        tracked.Visual=VisualSignature.From(worldObject,tracked,BuildShipLegos(worldObject,tracked));
        _trackedObjects.Add(worldObject, tracked);
        return tracked;
    }

    private void ApplyMembershipChanges(Net7WorldRegistry registry)
    {
        if (!ReferenceEquals(_membershipRegistry, registry))
        {
            _membershipSubscription?.Dispose();
            _membershipRegistry = registry;
            _membershipSubscription = registry.SubscribeMembership();
        }
        while (_membershipSubscription!.TryRead(out var change))
        {
            if (change.Kind == Net7WorldMembershipChangeKind.Reset) _trackedObjects.Clear();
            else if (change.Kind == Net7WorldMembershipChangeKind.Remove)
                _trackedObjects.Remove(change.Object!);
            // Allocate render identities only for objects actually published,
            // preserving the existing presentation identity/clock sequence.
        }
    }

    private ulong NextSnapshotVersion()
        => _nextSnapshotVersion = checked(_nextSnapshotVersion + 1);

    private ulong NextRenderIdentity()
        => _nextRenderIdentity = checked(_nextRenderIdentity + 1);

    private static EnbSceneSpatialProviderKind MapSpatialKind(Net7SpatialProviderKind kind)
        => kind switch
        {
            Net7SpatialProviderKind.Fixed => EnbSceneSpatialProviderKind.Fixed,
            Net7SpatialProviderKind.Simple => EnbSceneSpatialProviderKind.Simple,
            Net7SpatialProviderKind.AdvancedLocal => EnbSceneSpatialProviderKind.AdvancedLocal,
            Net7SpatialProviderKind.AdvancedRemote => EnbSceneSpatialProviderKind.AdvancedRemote,
            Net7SpatialProviderKind.Component => EnbSceneSpatialProviderKind.Component,
            Net7SpatialProviderKind.Planet => EnbSceneSpatialProviderKind.Planet,
            _ => throw new InvalidOperationException($"Unsupported presentation spatial provider kind {kind}."),
        };

    private static EnbScenePlanetSnapshot? BuildPlanet(Net7WorldObject worldObject)
    {
        if (worldObject.Spatial?.Planet is not { } planet)
            return null;

        return new EnbScenePlanetSnapshot(
            planet.ServerTimestamp,
            planet.SpatialVector,
            planet.OrbitId,
            planet.OrbitDistance,
            planet.OrbitAngle,
            planet.OrbitRate,
            planet.RotationAngle,
            planet.RotationRate,
            planet.TiltAngle);
    }

    private static EnbSceneCameraStateSnapshot BuildCameraState(
        EnbRetailAssetBundle? assets,
        uint? fixedSessionId,
        ulong? localRenderIdentity,
        uint? localShipObjectId)
    {
        EnbRetailCameraModeSelection? selection = assets?.ResolveCameraModes(fixedSessionId);
        EnbRetailPlayerOptionsSelection? playerOptions = assets?.ResolvePlayerOptions(fixedSessionId);
        ImmutableArray<EnbSceneCameraModeSnapshot> modes = selection is null
            ? ImmutableArray<EnbSceneCameraModeSnapshot>.Empty
            : selection.Catalog.Modes
                .Select(static mode => new EnbSceneCameraModeSnapshot(
                    mode.Index,
                    mode.HeadingRadians,
                    mode.Distance,
                    mode.TiltRadians,
                    mode.HorizontalFieldOfViewRadians,
                    mode.ElevationOffsetRadians))
                .ToImmutableArray();

        return new EnbSceneCameraStateSnapshot(
            AnchorRenderIdentity: localRenderIdentity,
            AnchorGameId: localShipObjectId,
            UsesSessionOverride: selection?.UsesSessionOverride ?? false,
            SessionOverrideSelector: selection?.SessionOverrideSelector,
            ModeSourceProvenance: selection is null ? null : CopyProvenance(selection.Provenance),
            PlayerOptionsSelector: playerOptions?.Selector,
            PlayerOptionsSourceProvenance: playerOptions?.Provenance is null
                ? null
                : CopyProvenance(playerOptions.Provenance),
            StartInFirstPerson: playerOptions?.Options.BridgeMode ?? false,
            Modes: modes,
            ChaseDistanceScale: RetailChaseDistanceScale,
            NormalClipRange: new EnbSceneClipRangeSnapshot(RetailNormalNearPlane, RetailNormalFarPlane),
            TemporaryPassClipRanges: ImmutableArray.Create(
                new EnbSceneClipRangeSnapshot(RetailWorldPassNearPlane, RetailWorldPassFarPlane),
                new EnbSceneClipRangeSnapshot(RetailSpecialPassNearPlane, RetailSpecialPassFarPlane)))
        { WarpCameraEnabled = playerOptions?.Options.CameraWarp ?? true,
          GateCameraEnabled = playerOptions?.Options.CameraGate ?? false,
          BridgeCameraEnabled = playerOptions?.Options.CameraBridge ?? true,
          LootCameraEnabled = playerOptions?.Options.CameraLoot ?? true,
          DockCameraEnabled = playerOptions?.Options.CameraDock ?? true,
          KeepShipInView = playerOptions?.Options.CameraShip ?? true,
          LandCameraEnabled = playerOptions?.Options.CameraLand ?? true };
    }

    internal void CaptureEffectSources(Net7WorldState world, IReadOnlyDictionary<uint, Net7EffectState> effects,
        EnbRetailAssetBundle? assets, uint clientTime, Net7ItemTemplateCatalog? itemTemplates)
    {
        lock (_gate)
        {
            if (effects.Count == 0) return;
            ApplyMembershipChanges(world.Objects);
            var ids = new HashSet<uint>();
            var objects = new List<EnbSceneObjectSnapshot>();
            foreach (var effect in effects.Values)
            {
                Capture(effect.SourceId);
                if (effect.IsObjectToObject) Capture(effect.TargetId);
            }
            CaptureEffectSources(effects, objects);
            void Capture(uint id)
            {
                if (id != 0 && ids.Add(id) && world.Objects.FindFirst(id) is {} source)
                    objects.Add(BuildRenderDefinition(world, source, assets, clientTime, itemTemplates));
            }
        }
    }

    private void CaptureEffectSources(IReadOnlyDictionary<uint, Net7EffectState> effects,
        IReadOnlyList<EnbSceneObjectSnapshot> objects)
    {
        // Capture at the packet mutation boundary, before either a later packet or
        // the render thread can skip the source. Retail 006E7770 does this at
        // effect creation; 006E7870 stops refreshing when the source is destroyed.
        var live = effects.Values.Select(e => (e.EffectId,e.InstanceGeneration)).ToHashSet();
        foreach(var key in _effectSources.Keys.Where(k=>!live.Contains(k)).ToArray()) _effectSources.Remove(key);
        foreach(var key in _effectEndpoints.Keys.Where(k=>!live.Contains(k)).ToArray()) _effectEndpoints.Remove(key);
        _observedEffectEndpoints.RemoveWhere(key=>!live.Contains(key));
        foreach(var effect in effects.Values.Where(e=>e.GeometryKind is (Net7EffectGeometryKind.ObjectToObject or Net7EffectGeometryKind.ObjectToObjectDurationLinked)))
        {
            if(effect.EndpointReceipt is not {SourceCreationOrdinal: {} sourceOrdinal, TargetCreationOrdinal: {} targetOrdinal})continue;
            var key=(effect.EffectId,effect.InstanceGeneration);
            bool firstPublication=_observedEffectEndpoints.Add(key);
            var source=objects.FirstOrDefault(o=>o.GameId==effect.SourceId && o.WorldCreationOrdinal==sourceOrdinal);
            if(source?.Spatial is not null)
                _effectSources[key]=(_effectSources.TryGetValue(key,out var previousSource)?previousSource.Initial:source,source);
            var target=objects.FirstOrDefault(o=>o.GameId==effect.TargetId && o.WorldCreationOrdinal==targetOrdinal);
            if(_effectEndpoints.TryGetValue(key,out var captured))
            {
                if(target?.Spatial is not null)_effectEndpoints[key]=captured with {Target=target};
            }
            else
            {
                if(source?.Spatial is not null && target?.Spatial is not null)
                    _effectEndpoints[key]=new(source,target) {InitialTarget=target,CompleteAtFirstPublication=firstPublication};
            }
        }
        foreach(var effect in effects.Values.Where(e=>e.GeometryKind is Net7EffectGeometryKind.Object or Net7EffectGeometryKind.ObjectToPoint || e.CapturedTargetEffectPoint.HasValue))
        {
            var key = (effect.EffectId,effect.InstanceGeneration);
            var source = objects.FirstOrDefault(o=>o.GameId==effect.SourceId);
            if(effect.EndpointReceipt is {} receipt &&
                (receipt.SourceCreationOrdinal is null || source?.WorldCreationOrdinal != receipt.SourceCreationOrdinal))continue;
            if(source?.Spatial is null) continue;
            if(_effectSources.TryGetValue(key,out var captured))
            {
                // A recycled GameID is a new object, not the old effect's owner.
                if(source.RenderIdentity==captured.Initial.RenderIdentity) _effectSources[key]=(captured.Initial,source);
            }
            else _effectSources.Add(key,(source,source));
        }

    }

    private ImmutableArray<EnbSceneEffectSnapshot> BuildEffects(
        IReadOnlyDictionary<uint, Net7EffectState>? effects,
        EnbRetailAssetBundle? assets,
        IReadOnlyList<EnbSceneObjectSnapshot> objects)
    {
        if (effects is null || effects.Count == 0)
        {
            _effectSources.Clear();
            _effectEndpoints.Clear();
            _observedEffectEndpoints.Clear();
            return ImmutableArray<EnbSceneEffectSnapshot>.Empty;
        }

        CaptureEffectSources(effects, objects);

        return effects.Values
            .OrderBy(static effect => effect.EffectId)
            .Select(effect => new EnbSceneEffectSnapshot(
                effect.EffectId,
                effect.InstanceGeneration,
                effect.SourceId,
                effect.CapturedTargetEffectPoint.HasValue ? 0 : effect.TargetId,
                effect.DefinitionId,
                effect.Timestamp,
                effect.DurationMilliseconds,
                effect.BoneName,
                effect.Mask,
                effect.IsObjectToObject,
                effect.Scale,
                effect.CapturedTargetEffectPoint.HasValue ? default : effect.SpatialOffset,
                effect.EndpointShortA,
                effect.EndpointShortB,
                effect.DescriptorFlag,
                effect.EffectOffset,
                effect.EffectScalar,
                effect.CapturedTargetEffectPoint.HasValue ? EnbSceneEffectGeometryKind.ObjectToPoint : MapEffectGeometryKind(effect.GeometryKind),
                effect.CapturedTargetEffectPoint ?? effect.WorldPoint,
                effect.DurationLink,
                effect.HsvShift,
                effect.AuxiliaryEffectFieldA,
                effect.AuxiliaryEffectFieldB,
                BuildEffectReference(assets, effect.DefinitionId)) {
                    EndWithTarget = effect.EndWithTarget,
                    PoseContext = effect.PoseContext,
                    EndpointReceipt = effect.EndpointReceipt is { } receipt
                        ? new(receipt.SourceCreationOrdinal, receipt.TargetCreationOrdinal) : null,
                    PublishedEndpoints = _effectEndpoints.GetValueOrDefault((effect.EffectId,effect.InstanceGeneration)),
                    InitialSourceObject = _effectSources.GetValueOrDefault((effect.EffectId,effect.InstanceGeneration)).Initial,
                    LastSourceObject = _effectSources.GetValueOrDefault((effect.EffectId,effect.InstanceGeneration)).Last })
            .ToImmutableArray();
    }

    public static EnbPresentationEffectReference? BuildEffectReference(
        EnbRetailAssetBundle? assets,
        int definitionId)
    {
        if (assets is null)
            return null;

        EnbPresentationProvenance provenance = CopyProvenance(assets.EffectProvenance(definitionId));
        EnbRetailPresentationCatalog? catalog = assets.PresentationCatalog;
        if (catalog is null)
        {
            return new EnbPresentationEffectReference(
                definitionId,
                null,
                EnbPresentationEffectClassKind.Unknown,
                null,
                null,
                null,
                null,
                false,
                null,
                null,
                null,
                null,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                null,
                true,
                false,
                false,
                ImmutableArray<EnbPresentationProperty>.Empty,
                null,
                provenance,
                null,
                EnbPresentationRenderStateEffectActionKind.Unknown,
                null,
                false);
        }

        EnbEffectPresentationResolution resolution = catalog.ResolveEffect(definitionId);
        EnbEffectDefinition? definition = resolution.Definition;
        ImmutableArray<EnbPresentationProperty> properties = definition is null
            ? ImmutableArray<EnbPresentationProperty>.Empty
            : definition.Properties
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => new EnbPresentationProperty(pair.Key, pair.Value))
                .ToImmutableArray();

        return new EnbPresentationEffectReference(
            definitionId,
            definition?.EffectClass,
            MapEffectClassKind(definition?.EffectClassKind ?? EnbEffectClassKind.Unknown),
            definition?.BaseAssetId,
            definition?.StartLinkId,
            definition?.NextLinkId,
            definition?.DurationRaw,
            definition?.InfiniteDuration ?? false,
            definition?.DurationValue,
            definition?.StartDelay,
            definition?.StartLinkDelay,
            definition?.CycleTime,
            definition?.UnaryStartLinkToTarget ?? false,
            definition?.UnaryNextLinkToTarget ?? false,
            definition?.TrackTarget ?? false,
            definition?.TrackSource ?? false,
            definition?.GoAwayWhenTargetDoes ?? false,
            definition?.GoAwayWhenSourceDoes ?? false,
            definition?.OffsetDurationPassedToStartLink ?? false,
            definition?.PassDurationToStartLink ?? false,
            definition?.DurationLinkedEffectUseNextLink ?? false,
            definition?.DurationLinkedEffectUseStartLink ?? false,
            definition?.Sfx,
            resolution.MissingDefinition,
            resolution.InvalidDefinition,
            resolution.MissingBaseAsset,
            properties,
            resolution.BaseAsset is null
                ? null
                : BuildBaseAssetReference(assets, resolution.BaseAsset.Selector),
            provenance,
            definition?.RenderStateId,
            MapRenderStateEffectAction(definition?.RenderStateAction ?? EnbRenderStateEffectActionKind.Unknown),
            definition?.RenderStateActionTimeMilliseconds,
            definition?.RenderStateForceReactivate ?? false);
    }

    private static EnbPresentationRenderStateEffectActionKind MapRenderStateEffectAction(EnbRenderStateEffectActionKind kind)
        => kind switch
        {
            EnbRenderStateEffectActionKind.Unknown => EnbPresentationRenderStateEffectActionKind.Unknown,
            EnbRenderStateEffectActionKind.Activate => EnbPresentationRenderStateEffectActionKind.Activate,
            EnbRenderStateEffectActionKind.Deactivate => EnbPresentationRenderStateEffectActionKind.Deactivate,
            EnbRenderStateEffectActionKind.Linger => EnbPresentationRenderStateEffectActionKind.Linger,
            EnbRenderStateEffectActionKind.Ramp => EnbPresentationRenderStateEffectActionKind.Ramp,
            EnbRenderStateEffectActionKind.ActivateNextStateOnce => EnbPresentationRenderStateEffectActionKind.ActivateNextStateOnce,
            _ => throw new InvalidOperationException($"Unsupported RenderStateEffect action kind {kind}."),
        };

    private static EnbSceneEffectGeometryKind MapEffectGeometryKind(Net7EffectGeometryKind kind)
        => kind switch
        {
            Net7EffectGeometryKind.Object => EnbSceneEffectGeometryKind.Object,
            Net7EffectGeometryKind.Point => EnbSceneEffectGeometryKind.Point,
            Net7EffectGeometryKind.ObjectToObject => EnbSceneEffectGeometryKind.ObjectToObject,
            Net7EffectGeometryKind.ObjectToEffect => EnbSceneEffectGeometryKind.ObjectToEffect,
            Net7EffectGeometryKind.ObjectToPoint => EnbSceneEffectGeometryKind.ObjectToPoint,
            Net7EffectGeometryKind.ObjectToObjectDurationLinked => EnbSceneEffectGeometryKind.ObjectToObjectDurationLinked,
            _ => throw new InvalidOperationException($"Unsupported effect geometry kind {kind}."),
        };

    private static EnbPresentationEffectClassKind MapEffectClassKind(EnbEffectClassKind kind)
        => kind switch
        {
            EnbEffectClassKind.Unknown => EnbPresentationEffectClassKind.Unknown,
            EnbEffectClassKind.RObjLookAtEffect => EnbPresentationEffectClassKind.RObjLookAtEffect,
            EnbEffectClassKind.RObjEffect => EnbPresentationEffectClassKind.RObjEffect,
            EnbEffectClassKind.WarpEffect => EnbPresentationEffectClassKind.WarpEffect,
            EnbEffectClassKind.LineEffect => EnbPresentationEffectClassKind.LineEffect,
            EnbEffectClassKind.StraightSeekEffect => EnbPresentationEffectClassKind.StraightSeekEffect,
            EnbEffectClassKind.SpiralSeekEffect => EnbPresentationEffectClassKind.SpiralSeekEffect,
            EnbEffectClassKind.SplineRandomSeekEffect => EnbPresentationEffectClassKind.SplineRandomSeekEffect,
            EnbEffectClassKind.LinearRandomSeekEffect => EnbPresentationEffectClassKind.LinearRandomSeekEffect,
            EnbEffectClassKind.SoundEffect => EnbPresentationEffectClassKind.SoundEffect,
            EnbEffectClassKind.PassEffect => EnbPresentationEffectClassKind.PassEffect,
            EnbEffectClassKind.AttachEffect => EnbPresentationEffectClassKind.AttachEffect,
            EnbEffectClassKind.RenderStateEffect => EnbPresentationEffectClassKind.RenderStateEffect,
            EnbEffectClassKind.ParticleJetEffect => EnbPresentationEffectClassKind.ParticleJetEffect,
            _ => throw new InvalidOperationException($"Unsupported effect class kind {kind}."),
        };

    private static EnbSceneEnvironmentSnapshot? BuildEnvironment(
        Net7WorldState world,
        EnbRetailAssetBundle? assets,
        int? presentationMode)
    {
        if (world.ServerParameters is not { } parameters)
            return null;

        return new EnbSceneEnvironmentSnapshot(
            parameters.BackdropBaseAsset,
            parameters.SwapBackdrop != 0,
            parameters.SectorNum,
            parameters.RawPayload.ToImmutableArray(),
            BuildBaseAssetReference(assets, parameters.BackdropBaseAsset),
            parameters.FogNear,
            parameters.FogFar,
            parameters.LightBackdrop != 0,
            parameters.FogBackdrop != 0,
            parameters.BackdropFogNear,
            parameters.BackdropFogFar,
            presentationMode,
            world.ServerParametersSplineUpdateCount)
        {
            SkyBaseAsset = EnbSceneEnvironmentSnapshot.ResolveSkyBaseAsset(parameters.BackdropBaseAsset, world.ServerParametersPresentationMode),
        };
    }

    private static EnbPresentationBaseAssetReference? BuildBaseAssetReference(
        EnbRetailAssetBundle? assets,
        int selector)
    {
        if (assets is null || assets.PresentationCatalog is not { } catalog)
            return null;

        EnbBaseAssetDefinition definition = catalog.ResolveBaseAsset(selector);
        ImmutableArray<EnbPresentationProperty> properties = definition.Properties is null
            ? ImmutableArray<EnbPresentationProperty>.Empty
            : definition.Properties
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => new EnbPresentationProperty(pair.Key, pair.Value))
                .ToImmutableArray();

        return new EnbPresentationBaseAssetReference(
            definition.Selector,
            MapBaseAssetKind(definition.Kind),
            definition.IsCatalogEntry,
            definition.RslId,
            definition.LinkId,
            definition.RsId,
            definition.File,
            definition.Name,
            definition.Width,
            definition.OverbrightOff,
            properties,
            CopyProvenance(assets.BaseAssetProvenance(selector)));
    }

    private static EnbPresentationClientBaseAssetReference? BuildClientBaseAssetReference(
        EnbRetailAssetBundle? assets,
        int selector)
    {
        if (assets is null || assets.ClientBaseAssets is not { } clientBaseAssets ||
            !clientBaseAssets.TryResolve(selector, out EnbClientBaseAssetGeometry geometry))
        {
            return null;
        }

        return new EnbPresentationClientBaseAssetReference(
            selector,
            geometry.Radius,
            geometry.RadiusAdjust,
            CopyProvenance(assets.ClientBaseAssetProvenance(selector)));
    }

    private static EnbPresentationBaseAssetKind MapBaseAssetKind(EnbBaseAssetDefinitionKind kind)
        => kind switch
        {
            EnbBaseAssetDefinitionKind.Missing => EnbPresentationBaseAssetKind.Missing,
            EnbBaseAssetDefinitionKind.NoRenderObject => EnbPresentationBaseAssetKind.NoRenderObject,
            EnbBaseAssetDefinitionKind.NullObject => EnbPresentationBaseAssetKind.NullObject,
            EnbBaseAssetDefinitionKind.Rsl => EnbPresentationBaseAssetKind.Rsl,
            EnbBaseAssetDefinitionKind.FileName => EnbPresentationBaseAssetKind.FileName,
            EnbBaseAssetDefinitionKind.ProceduralWidth => EnbPresentationBaseAssetKind.ProceduralWidth,
            EnbBaseAssetDefinitionKind.Unsupported => EnbPresentationBaseAssetKind.Unsupported,
            _ => throw new InvalidOperationException($"Unsupported BaseAsset kind {kind}."),
        };

    private static EnbPresentationProvenance CopyProvenance(EnbRetailDataProvenance provenance)
        => new(
            provenance.Path,
            provenance.Section,
            provenance.Key,
            provenance.ResourceName,
            provenance.ArchiveName);

    public static EnbSceneAvatarSnapshot? BuildAvatar(Net7AvatarData? avatar)
    {
        if (avatar is null)
            return null;

        return new EnbSceneAvatarSnapshot(
            avatar.FirstName,
            avatar.LastName,
            avatar.AvatarType,
            avatar.UnknownU8,
            avatar.AvatarVersion,
            avatar.Race,
            avatar.Profession,
            avatar.Gender,
            avatar.MoodType,
            avatar.Personality,
            avatar.Nlp,
            avatar.BodyType,
            avatar.PantsType,
            avatar.HeadType,
            avatar.HairNum,
            avatar.EarNum,
            avatar.GoggleNum,
            avatar.BeardNum,
            avatar.WeaponHipNum,
            avatar.WeaponUniqueNum,
            avatar.WeaponBackNum,
            avatar.HeadTextureNum,
            avatar.TattooTextureNum,
            avatar.TattooOffset.ToImmutableArray(),
            avatar.HairColor.ToImmutableArray(),
            avatar.BeardColor.ToImmutableArray(),
            avatar.EyeColor.ToImmutableArray(),
            avatar.SkinColor.ToImmutableArray(),
            avatar.ShirtPrimaryColor.ToImmutableArray(),
            avatar.ShirtSecondaryColor.ToImmutableArray(),
            avatar.PantsPrimaryColor.ToImmutableArray(),
            avatar.PantsSecondaryColor.ToImmutableArray(),
            avatar.ShirtPrimaryMetal,
            avatar.ShirtSecondaryMetal,
            avatar.PantsPrimaryMetal,
            avatar.PantsSecondaryMetal,
            avatar.Skeleton,
            avatar.BodyWeight.ToImmutableArray(),
            avatar.HeadWeight.ToImmutableArray());
    }

    private sealed class TrackedObject
    {
        private bool AppearanceReady;
        private ulong RslRevision,AttachmentRevision,FixedRevision;
        public long AppearancePreparations;
        private IReadOnlyList<Net7DecalEntry>? SourceDecals;
        public ImmutableArray<EnbSceneDecalSnapshot> decals=[];
        private IReadOnlyList<Net7ColorizationEntry>? SourceColorization;
        public ImmutableArray<EnbSceneColorizationSnapshot> colorization=[];
        public ImmutableArray<EnbSceneRenderStateLinkSnapshot> renderStateLinks=[];
        public ImmutableArray<uint> activeRenderStates=[];
        public ImmutableArray<EnbSceneRenderStateActionSnapshot> renderStateActions=[];
        public ImmutableArray<EnbSceneAttachmentSnapshot> attachments=[];
        private IReadOnlyList<Net7SubPartValue>? SourceSubParts;
        public ImmutableArray<EnbSceneSubPartSnapshot> subParts=[];
        private Net7NameDecalUpdate? SourceNameDecal;
        public EnbSceneNameDecalSnapshot? nameDecal=null;
        public void PrepareAppearance(Net7WorldObject worldObject)
        {
            if(!AppearanceReady || !(ReferenceEquals(SourceDecals,worldObject.Decals)))
            {
                decals = worldObject.Decals
                .Select(static entry => new EnbSceneDecalSnapshot(
                    entry.Field0,
                    entry.Field1,
                    entry.Data.ToImmutableArray()))
                .ToImmutableArray();

                SourceDecals=worldObject.Decals;AppearancePreparations++;
            }
            if(!AppearanceReady || !(ReferenceEquals(SourceColorization,worldObject.ColorizationEntries)))
            {
                colorization = worldObject.ColorizationEntries
                .Select(static entry => new EnbSceneColorizationSnapshot(
                    entry.Field,
                    entry.Vector.ToImmutableArray()))
                .ToImmutableArray();

                SourceColorization=worldObject.ColorizationEntries;AppearancePreparations++;
            }
            if(!AppearanceReady || RslRevision!=worldObject.RenderStateProjectionRevision)
            {
                renderStateLinks = worldObject.RenderStateLinks
                .OrderBy(static entry => entry.Key)
                .Select(static entry => new EnbSceneRenderStateLinkSnapshot(entry.Key, entry.Value))
                .ToImmutableArray();

                activeRenderStates = worldObject.ActiveRenderStates
                .Order()
                .ToImmutableArray();

                renderStateActions = worldObject.RenderStateActions
                .Select(static action => new EnbSceneRenderStateActionSnapshot(action.Opcode, action.RenderStateId, action.ClientTime, action.InitializerArgument))
                .ToImmutableArray();

                RslRevision=worldObject.RenderStateProjectionRevision;AppearancePreparations++;
            }
            if(!AppearanceReady || !(AttachmentRevision==worldObject.AttachmentProjectionRevision))
            {
                attachments = worldObject.Attachments
                .Select(static attachment => new EnbSceneAttachmentSnapshot(
                    attachment.ChildId,
                    attachment.AttachmentSelector))
                .ToImmutableArray();

                AttachmentRevision=worldObject.AttachmentProjectionRevision;AppearancePreparations++;
            }
            if(!AppearanceReady || !(ReferenceEquals(SourceSubParts,worldObject.SubParts) && FixedRevision==worldObject.FixedShipVisualRevision))
            {
                subParts = worldObject.SubParts
                .Select((part, ordinal) => new EnbSceneSubPartSnapshot(part.Name, part.PartValue)
                {
                    MaterializationRevision = worldObject.SubPartMaterializationRevisions.GetValueOrDefault(ordinal),
                })
                .ToImmutableArray();

                SourceSubParts=worldObject.SubParts;FixedRevision=worldObject.FixedShipVisualRevision;AppearancePreparations++;
            }
            if(!AppearanceReady || !(ReferenceEquals(SourceNameDecal,worldObject.NameDecal)))
            {
                nameDecal = worldObject.NameDecal is null
                ? null
                : new EnbSceneNameDecalSnapshot(
                    worldObject.NameDecal.Name,
                    worldObject.NameDecal.RawName.ToImmutableArray(),
                    worldObject.NameDecal.Rgb);

                SourceNameDecal=worldObject.NameDecal;AppearancePreparations++;
            }
            AppearanceReady=true;
        }

        public TrackedObject(ulong renderIdentity, ulong visualGeneration, VisualSignature visual)
        {
            RenderIdentity = renderIdentity;
            VisualGeneration = visualGeneration;
            BaseVisualGeneration = visualGeneration;
            Visual = visual;
        }

        public ulong RenderIdentity { get; }
        public ulong VisualGeneration { get; set; }
        public ulong BaseVisualGeneration { get; set; }
        public VisualSignature Visual { get; set; }
        public EnbSceneSpatialSnapshot? Type29PinnedSpatial { get; set; }
        public long CompositionPreparations;
        public bool LegoProjectionReady,CompositionReady;
        public ulong LegoProjectionRevision,LegoFixedRevision,CompositionOrganicRevision,CompositionFixedRevision,CompositionFingerprint;
        public EnbSceneShipLegoSnapshot? ShipLegos,CompositionLegos;
        public IReadOnlyList<Net7SubPartValue>? CompositionParts;
    }

    private readonly record struct VisualSignature(
        int BaseAsset,
        int ScaleBits,
        int HsvHBits,
        int HsvSBits,
        int HsvVBits,
        ulong BaseAssetMaterializationRevision,
        ulong CompositionFingerprint)
    {
        public static VisualSignature From(Net7WorldObject worldObject,TrackedObject tracked,EnbSceneShipLegoSnapshot? legos)
            => new(
                worldObject.BaseAsset,
                BitConverter.SingleToInt32Bits(worldObject.Scale),
                BitConverter.SingleToInt32Bits(worldObject.HsvH),
                BitConverter.SingleToInt32Bits(worldObject.HsvS),
                BitConverter.SingleToInt32Bits(worldObject.HsvV),
                worldObject.BaseAssetMaterializationRevision,
                RetainedCompositionFingerprint(worldObject,tracked,legos));

        private static ulong RetainedCompositionFingerprint(Net7WorldObject obj,TrackedObject tracked,EnbSceneShipLegoSnapshot? legos)
        {
            ulong organic=obj.AuxData is {Name:"ShipAuxData"} aux && aux.Properties.Count>ShipIsOrganicPropertyId
                ?aux.Resolve(ShipIsOrganicPropertyId).MaterializedRevision:0;
            if(tracked.CompositionReady && ReferenceEquals(tracked.CompositionParts,obj.SubParts) &&
                ReferenceEquals(tracked.CompositionLegos,legos) && tracked.CompositionOrganicRevision==organic &&
                tracked.CompositionFixedRevision==obj.FixedShipVisualRevision)return tracked.CompositionFingerprint;
            ulong fingerprint=ComputeCompositionFingerprint(obj,legos);tracked.CompositionPreparations++;
            tracked.CompositionParts=obj.SubParts;tracked.CompositionLegos=legos;
            tracked.CompositionOrganicRevision=organic;tracked.CompositionFixedRevision=obj.FixedShipVisualRevision;
            tracked.CompositionReady=true;return tracked.CompositionFingerprint=fingerprint;
        }
        private static ulong ComputeCompositionFingerprint(Net7WorldObject worldObject,EnbSceneShipLegoSnapshot? legos)
        {
            const ulong OffsetBasis = 14695981039346656037UL;
            const ulong Prime = 1099511628211UL;
            ulong hash = OffsetBasis;

            static ulong MixByte(ulong current, byte value) => (current ^ value) * Prime;
            static ulong MixInt32(ulong current, int value)
            {
                uint raw = unchecked((uint)value);
                for (int shift = 0; shift < 32; shift += 8)
                    current = MixByte(current, (byte)(raw >> shift));
                return current;
            }
            static ulong MixUInt32(ulong current, uint value) => MixInt32(current, unchecked((int)value));
            static ulong MixUInt64(ulong current, ulong value)
            {
                current = MixUInt32(current, unchecked((uint)value));
                return MixUInt32(current, unchecked((uint)(value >> 32)));
            }
            static ulong MixString(ulong current, string value)
            {
                foreach (char c in value)
                {
                    current = MixByte(current, (byte)c);
                    current = MixByte(current, (byte)(c >> 8));
                }
                return MixByte(current, 0xFF);
            }

            for (int ordinal = 0; ordinal < worldObject.SubParts.Count; ordinal++)
            {
                Net7SubPartValue part = worldObject.SubParts[ordinal];
                hash = MixInt32(hash, ordinal);
                hash = MixString(hash, part.Name);
                hash = MixUInt32(hash, part.PartValue);
                hash = MixUInt64(hash, worldObject.SubPartMaterializationRevisions.GetValueOrDefault(ordinal));
            }

            //00735DF0/00735ED0/00735FC0 mutate the retained RenderObject.
            //Material packet revisions invalidate descriptors independently;
            //RGB, decals, names and color scope do not replace this object or
            //restart its RSL controller/animation generation.

            // Current retail constructs the six fixed ship visual children only
            // for object types 0/1 when IsOrganic is absent/false. The effective
            // boolean therefore belongs to visual invalidation; diagnostic-only
            // inventory fields deliberately do not.
            bool effectiveOrganic = false;
            if (worldObject.AuxData is Net7AuxDataSchema shipAux &&
                string.Equals(shipAux.Name, "ShipAuxData", StringComparison.Ordinal) &&
                shipAux.Properties.Count > ShipIsOrganicPropertyId)
            {
                Net7AuxDataProperty organic = shipAux.Resolve(ShipIsOrganicPropertyId);
                if (organic.State != 0)
                {
                    effectiveOrganic = organic.Value switch
                    {
                        byte organicByte => organicByte != 0,
                        null => false,
                        _ => throw new InvalidDataException("Materialized ShipAuxData.IsOrganic is not a BooleanByte value."),
                    };
                }
            }
            hash = MixByte(hash, effectiveOrganic ? (byte)1 : (byte)0);

            if (legos is null)
                return MixByte(hash, 0);

            hash = MixByte(hash, 1);
            hash = MixInt32(hash, BitConverter.SingleToInt32Bits(legos.Scale));
            foreach (EnbSceneShipLegoAttachmentSnapshot attachment in legos.Attachments)
            {
                hash = MixInt32(hash, attachment.Ordinal);
                hash = MixString(hash, attachment.BoneName);
                hash = MixInt32(hash, attachment.Type);
                hash = MixInt32(hash, attachment.BaseAsset);
                hash = MixUInt64(hash, attachment.MaterializationRevision);
            }
            return hash;
        }
    }
}
