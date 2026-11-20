using System.Collections.Immutable;
namespace EnbNext.Presentation;

/// <summary>Detached copies only for recording, replay oracle and asynchronous diagnostics.</summary>
public static class EnbSceneCapture
{
    public static EnbSceneSnapshot CaptureDiagnostics(IEnbSceneFrame scene)
    {
        if(scene is EnbSceneSnapshot detached)return detached with {RenderPoses=detached.RenderPoses.Detach()};
        var objects=ImmutableArray.CreateBuilder<EnbSceneObjectSnapshot>(scene.Objects.Count);
        foreach(var obj in scene.Objects)objects.Add(CaptureObject(obj));
        return new(scene.Version,
            scene.ClientTime,
            scene.LocalPlayerId,
            scene.LocalShipObjectId,
            scene.ClientAvatarId,
            scene.IsTransitioning,
            scene.Environment,
            scene.Camera,
            scene.Effects,
            CaptureHud(scene.Hud),
            scene.Lighting,
            objects.MoveToImmutable(),
            scene.StationAvatars,
            scene.ZBand,
            scene.BoundingBox,
            scene.SplineData,
            scene.SplineUpdates)
        {
            AvatarFirstNames=scene.AvatarFirstNames,
            AvatarDescriptions=scene.AvatarDescriptions,
            AvatarCacheLifetimes=scene.AvatarCacheLifetimes,
            StationAvatarStreamId=scene.StationAvatarStreamId,
            StationAvatarEvents=scene.StationAvatarEvents,
            ShipEmotes=scene.ShipEmotes,
            ServerTimeCorrectionMilliseconds=scene.ServerTimeCorrectionMilliseconds,
            MeshBufferResetRevision=scene.MeshBufferResetRevision,
            RenderSceneSequence=scene.RenderSceneSequence,
            SourceMembershipRevision=scene.SourceMembershipRevision,
            RenderPoses=scene.RenderPoses.Detach(),
            WorldRegistryRevision=scene.WorldRegistryRevision,
            LocationTransition=scene.LocationTransition,
            Music=scene.Music,
            ActiveStarbaseId=scene.ActiveStarbaseId,
            StationConversation=scene.StationConversation,
            IncapacitationDetails=scene.IncapacitationDetails,
            RecoveryAvatar=scene.RecoveryAvatar,
            StationTerminal=scene.StationTerminal,
            AvatarCustomization=scene.AvatarCustomization,
            ShipCustomization=scene.ShipCustomization,
            StationJobs=scene.StationJobs,
            StationJobsWithdrawn=scene.StationJobsWithdrawn,
            StationJobDescription=scene.StationJobDescription,
            StationJobReply=scene.StationJobReply,
            StationJobRequestPending=scene.StationJobRequestPending,
            DamageEvents=scene.DamageEvents?.Capture(),
            ClientSoundStreamId=scene.ClientSoundStreamId,
            ClientSoundEvents=scene.ClientSoundEvents,
            DockingSoundRevision=scene.DockingSoundRevision,
            StationDefinition=scene.StationDefinition
        };
    }
    public static EnbSceneObjectSnapshot CaptureObject(IEnbRenderObject obj)
    {
        EnbSceneSpatialSnapshot? spatial=null;
        if(obj.Spatial is {} p)
        {
            var t=p.Transform;
            spatial=new(p.ProviderKind,new(t.ClientTime,t.Position,t.Orientation,t.Velocity,t.Speed,t.Rotation,t.OrientationColumn0)
                {ProviderTransform=t.ProviderTransform,BankHeadingTarget=t.BankHeadingTarget},p.Planet)
            {Advanced=p.Advanced,FormationLeaderId=p.FormationLeaderId,ModelAudit=p.ModelAudit,
                PacketTime=p.PacketTime,ReceiptTime=p.ReceiptTime,TransitionMilliseconds=p.TransitionMilliseconds};
        }
        return new(obj.RenderIdentity,
            obj.VisualGeneration,
            obj.GameId,
            obj.IsLocalShip,
            obj.IsCurrentTarget,
            obj.ObjectType,
            obj.BaseAsset,
            obj.Scale,
            obj.HsvH,
            obj.HsvS,
            obj.HsvV,
            obj.RelationshipRaw,
            obj.IsAttacking,
            obj.Name,
            obj.Radar,
            spatial,
            obj.BaseAssetReference,
            obj.ClientBaseAssetReference,
            obj.Decals,
            obj.ColorizationEntries,
            obj.RenderStateLinks,
            obj.ActiveRenderStates,
            obj.RenderStateActions,
            obj.Attachments,
            obj.NameDecal,
            obj.SubParts,
            obj.Avatar,
            obj.SpecialTargetEndpointId,
            obj.SpecialTargetTransition)
        {
            Spatial=spatial,
            HarvestablePercentFull=obj.HarvestablePercentFull,
            WorldCreationOrdinal=obj.WorldCreationOrdinal,
            SpeakerRevision=obj.SpeakerRevision,
            SpeakerType=obj.SpeakerType,
            SpeakerColor=obj.SpeakerColor,
            SpeakingColor=obj.SpeakingColor,
            CreatedBaseAsset=obj.CreatedBaseAsset,
            HarvestableTechLevel=obj.HarvestableTechLevel,
            ShipLegos=obj.ShipLegos,
            DisplayNameProperties=obj.DisplayNameProperties,
            LegoControllerMutations=obj.LegoControllerMutations,
            FixedShipVisualRevision=obj.FixedShipVisualRevision,
            BaseAssetMaterializationRevision=obj.BaseAssetMaterializationRevision,
            BaseVisualGeneration=obj.BaseVisualGeneration,
            ShipPresentation=obj.ShipPresentation,
            DamageDecalPools=obj.DamageDecalPools,
            ShipVisualBankRadians=obj.ShipVisualBankRadians,
            RenderPoseSlot=obj.RenderPoseSlot,
            ShipEmote=obj.ShipEmote,
            PlayerPresentation=obj.PlayerPresentation,
            IsRescueBeaconActive=obj.IsRescueBeaconActive,
            RelationshipNotificationRevision=obj.RelationshipNotificationRevision,
            PvpUpdateRevision=obj.PvpUpdateRevision,
            PvpUpdated=obj.PvpUpdated,
            TargetVitals=obj.TargetVitals,
            Interruption=obj.Interruption,
            ScannerFactionState=obj.ScannerFactionState,
            ScannerTechLevelState=obj.ScannerTechLevelState,
            ScannerTechLevelRevision=obj.ScannerTechLevelRevision,
            HarvestableResourcesState=obj.HarvestableResourcesState,
            HarvestableResourceSlotStates=obj.HarvestableResourceSlotStates,
            HarvestableResources=obj.HarvestableResources,
            ShipAuxDataAudit=obj.ShipAuxDataAudit,
            ColorizationAudit=obj.ColorizationAudit,
            NameDecalApplyCount=obj.NameDecalApplyCount,
            NameDecalColorizationApplyCount=obj.NameDecalColorizationApplyCount,
            DecalApplyCount=obj.DecalApplyCount
        };
    }
    public static EnbSceneHudSnapshot CaptureHud(IEnbHudFrame source)
        => new(source.CurrentTargetId,source.ScanRange,source.LocalShipLockSpeedActive,source.ClientSendUiTriggersEnabled)
        {
            Cockpit=CaptureCockpit(source.Cockpit),
            ReputationState=source.ReputationState,
            ReputationRevision=source.ReputationRevision,
            Experience=source.Experience,
            Inventory=source.Inventory,
            Chat=source.Chat,
            WarpRoute=source.WarpRoute,
            Shortcuts=source.Shortcuts,
            Buffs=source.Buffs,
            TargetBuffs=source.TargetBuffs,
            GroupMemberIds=source.GroupMemberIds,
            Group=source.Group,
            Guild=source.Guild,
            LocalSelectedTargetId=source.LocalSelectedTargetId,
            HasLocalTargetSelection=source.HasLocalTargetSelection,
            TargetSelectionSequence=source.TargetSelectionSequence,
            ProspectSkillLevel=source.ProspectSkillLevel,
            CurrentTargetVerbs=source.CurrentTargetVerbs,
            TargetThreat=source.TargetThreat,
            TargetThreatSound=source.TargetThreatSound,
            TargetThreatLevel=source.TargetThreatLevel,
        };
    public static EnbSceneCockpitSnapshot? CaptureCockpit(IEnbCockpitFrame? source)
        => source is null?null:new(source.EnergyPercent,source.MaximumEnergyPower,
            source.Vitals is {} v?new(v.ShieldPercent,v.MaximumShieldPower,v.HullPoints,v.MaximumHullPoints):null,
            source.MaximumSpeed,source.IsAutoFollowing){Warp=source.Warp,WarpCommand=source.WarpCommand};
}
