using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Net7;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.Protocol;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.World;

public enum Net7SpatialProviderKind
{
    Fixed = 1,
    Simple = 3,
    AdvancedLocal = 4,
    AdvancedRemote = 8,
    Component = 11,
    Planet = 12,
}

public sealed class Net7SpatialState
{
    public Net7SpatialProviderKind Kind { get; internal set; }
    public Vector3 Position { get; internal set; }
    public Quaternion Orientation { get; internal set; } = Quaternion.Identity;
    public Vector3 Velocity { get; internal set; }
    public uint? ClientTimestamp { get; internal set; }
    public uint TransitionMilliseconds { get; internal set; }
    public uint? MovementId { get; internal set; }
    public float CurrentSpeed { get; internal set; }
    public Net7AdvancedPositionUpdate? Advanced { get; internal set; }
    public Net7ComponentPositionUpdate? Component { get; internal set; }
    public Net7PlanetPositionUpdate? Planet { get; internal set; }
    public Net7FormationPositionUpdate? Formation { get; internal set; }

    internal INet7SpatialModel? Model { get; set; }
    internal Net7AdvancedKeyframeSpatialModel? LocalCommandState { get; set; }
    public uint? ReceiptTime { get; internal set; }
    public string DescribeModel(uint clientTime) => Model is Net7RemoteAdvancedSpatialModel remote
        ? $"{remote.Physical.GetType().Name}/lag{remote.GetEffectiveOffset(clientTime)}/" + (remote.Physical switch
        {
            Net7EndpointTransitionSpatialModel endpoint => endpoint.IntervalAudit,
            Net7TransitionSpatialModel transition => transition.IntervalAudit,
            Net7CubicSpatialModel cubic => cubic.IntervalAudit,
            _ => "direct"
        })
        : Model?.GetType().Name ?? "none";
}

public sealed record Net7RenderStateAction(ushort Opcode, uint RenderStateId, uint ClientTime = 0, uint? InitializerArgument = null);
public sealed record Net7AttachmentPresentation(uint ChildId, uint AttachmentSelector);

/// <summary>
/// Passive evidence for object-targeted AuxData reaching a materialized object.
/// The largest observed delta is retained so later small updates cannot hide
/// the full startup materialization attempt.
/// </summary>
public sealed record Net7AuxDataMaterializationAudit(
    int AttemptCount,
    int LargestDeltaLength,
    byte LargestSelector,
    bool LargestAccepted,
    string? LargestError)
{
    public bool LargestRetailCompatiblePartial { get; init; }
}

/// <summary>
/// Passive evidence for current retail's immediate 0x0011 RenderObject walk.
/// The packet mutates only children that exist at processing time; later Lego
/// attachment code has no call back into the colorization walker.
/// </summary>
public sealed record Net7ColorizedShipLegoScopeEntry(
    int Ordinal,
    string BoneName,
    int Type,
    int BaseAsset,
    ulong MaterializationRevision);

public sealed record Net7ColorizedSubPartScopeEntry(
    int Ordinal,
    string Name,
    uint PartValue,
    ulong MaterializationRevision);

public sealed record Net7ColorizationApplicationAudit(
    int ApplyCount,
    int ActiveShipLegoAttachmentsAtLastApply,
    int ActiveSubPartsAtLastApply,
    int ObjectAuxDataAttemptsAtLastApply,
    IReadOnlyList<Net7ColorizedShipLegoScopeEntry> ShipLegoScope,
    IReadOnlyList<Net7ColorizedSubPartScopeEntry> SubPartScope);

public sealed class Net7WorldObject
{
    internal Net7WorldObject(Net7CreateUpdate create, Net7AuxDataSchema? auxData, ulong creationOrdinal)
    {
        Creation = create;
        GameId = create.GameId;
        CreationOrdinal = creationOrdinal;
        Scale = create.Scale;
        BaseAsset = create.BaseAsset;
        ObjectType = create.ObjectType;
        HsvH = create.HsvH;
        HsvS = create.HsvS;
        HsvV = create.HsvV;
        RelationshipRaw = (uint)Net7Reaction.Shun;
        AuxData = auxData;
    }

    internal Action<Net7WorldObject>? RenderMutationSink;
    public uint GameId { get; }
    public Net7CreateUpdate Creation { get; }
    public ulong CreationOrdinal { get; }
    private ulong _renderSourceSpeakerRevision;
    public ulong SpeakerRevision
    {
        get=>_renderSourceSpeakerRevision;
        internal set {_renderSourceSpeakerRevision=value;RenderMutationSink?.Invoke(this);}
    }
    private byte _renderSourceSpeakerType;
    public byte SpeakerType
    {
        get=>_renderSourceSpeakerType;
        internal set {_renderSourceSpeakerType=value;RenderMutationSink?.Invoke(this);}
    }
    private Vector3? _renderSourceSpeakerColor;
    public Vector3? SpeakerColor
    {
        get=>_renderSourceSpeakerColor;
        internal set {_renderSourceSpeakerColor=value;RenderMutationSink?.Invoke(this);}
    }
    // Detached presentation consumers must not lose a charge/enter/exit sequence
    // when several AuxData updates land between two rendered frames.
    public List<Net7WarpPresentationChange> WarpPresentationChanges { get; } = [];
    public List<Net7WarpPresentationChange> PrivateWarpPresentationChanges { get; } = [];
    private int? _presentedWarpState;
    private int? _presentedPrivateWarpState;
    private ulong _warpPresentationSequence;
    private ulong _privateWarpPresentationSequence;
    public bool GlobalWarpUpdatesSuppressed { get; private set; }
    public void SuppressGlobalWarpUpdates() { GlobalWarpUpdatesSuppressed=true; RenderMutationSink?.Invoke(this); }
    internal void ObserveWarpPresentation()
    {
        if (ObjectType != 1 || AuxData is null) return;
        var privateProperty = AuxData.Resolve(38);
        int? privateState = privateProperty.Value is int privateValue ? privateValue : privateProperty.State != 0 ? 0 : null;
        if (privateState is not null && privateState != _presentedPrivateWarpState)
        {
            _presentedPrivateWarpState = privateState;
            PrivateWarpPresentationChanges.Add(new(++_privateWarpPresentationSequence, privateState.Value, 0, 0));
            if (PrivateWarpPresentationChanges.Count > 32) PrivateWarpPresentationChanges.RemoveAt(0);
        }
        var property = AuxData.Resolve(39);
        int? state = property.Value is int value ? value : property.State != 0 ? 0 : null;
        if (state is null || state == _presentedWarpState) return;
        _presentedWarpState = state;
        WarpPresentationChanges.Add(new(++_warpPresentationSequence, state.Value,
            AuxData.Resolve(1910).Value is int power ? power : 1,
            AuxData.Resolve(1922).Value is int trail ? trail : 0));
        if (WarpPresentationChanges.Count > 32) WarpPresentationChanges.RemoveAt(0);
    }
    private float _renderSourceScale;
    public float Scale
    {
        get=>_renderSourceScale;
        internal set {_renderSourceScale=value;RenderMutationSink?.Invoke(this);}
    }
    private int _renderSourceBaseAsset;
    public int BaseAsset
    {
        get=>_renderSourceBaseAsset;
        internal set {_renderSourceBaseAsset=value;RenderMutationSink?.Invoke(this);}
    }
    private Net7BaseAssetDefinition? _renderSourceBaseAssetPresentation;
    public Net7BaseAssetDefinition? BaseAssetPresentation
    {
        get=>_renderSourceBaseAssetPresentation;
        internal set {_renderSourceBaseAssetPresentation=value;RenderMutationSink?.Invoke(this);}
    }
    private byte _renderSourceObjectType;
    public byte ObjectType
    {
        get=>_renderSourceObjectType;
        internal set {_renderSourceObjectType=value;RenderMutationSink?.Invoke(this);}
    }
    private float _renderSourceHsvH;
    public float HsvH
    {
        get=>_renderSourceHsvH;
        internal set {_renderSourceHsvH=value;RenderMutationSink?.Invoke(this);}
    }
    private float _renderSourceHsvS;
    public float HsvS
    {
        get=>_renderSourceHsvS;
        internal set {_renderSourceHsvS=value;RenderMutationSink?.Invoke(this);}
    }
    private float _renderSourceHsvV;
    public float HsvV
    {
        get=>_renderSourceHsvV;
        internal set {_renderSourceHsvV=value;RenderMutationSink?.Invoke(this);}
    }
    private uint _renderSourceRelationshipRaw;
    public uint RelationshipRaw
    {
        get=>_renderSourceRelationshipRaw;
        internal set {_renderSourceRelationshipRaw=value;RenderMutationSink?.Invoke(this);}
    }
    // 006FEEE0 emits even for an identical value; listeners are object-owned.
    private ulong _renderSourceRelationshipNotificationRevision;
    public ulong RelationshipNotificationRevision
    {
        get=>_renderSourceRelationshipNotificationRevision;
        internal set {_renderSourceRelationshipNotificationRevision=value;RenderMutationSink?.Invoke(this);}
    }
    private byte _renderSourceIsAttacking;
    public byte IsAttacking
    {
        get=>_renderSourceIsAttacking;
        internal set {_renderSourceIsAttacking=value;RenderMutationSink?.Invoke(this);}
    }
    private string? _renderSourceName;
    public string? Name
    {
        get=>_renderSourceName;
        internal set {_renderSourceName=value;RenderMutationSink?.Invoke(this);}
    }
    // 006FF4B0: players prefer materialized Owner; other objects prefer Name.
    public string ChatIdentity
    {
        get
        {
            foreach (string propertyName in ObjectType == 1 ? new[] { "Owner", "Name" } : new[] { "Name", "Owner" })
                if (AuxData?.Root.Children.FirstOrDefault(p => p.Path.EndsWith("." + propertyName, StringComparison.Ordinal))
                    is { State: not 0, Value: Net7AuxDataString value }) return value.Text;
            return "Unknown";
        }
    }
    private bool _renderSourceAppearsInRadar;
    public bool AppearsInRadar
    {
        get=>_renderSourceAppearsInRadar;
        internal set {_renderSourceAppearsInRadar=value;RenderMutationSink?.Invoke(this);}
    }
    private float _renderSourceRadarRange;
    public float RadarRange
    {
        get=>_renderSourceRadarRange;
        internal set {_renderSourceRadarRange=value;RenderMutationSink?.Invoke(this);}
    }
    private Net7NavigationUpdate? _renderSourceNavigation;
    public Net7NavigationUpdate? Navigation
    {
        get=>_renderSourceNavigation;
        internal set {_renderSourceNavigation=value;RenderMutationSink?.Invoke(this);}
    }
    private Net7SpatialState? _renderSourceSpatial;
    public Net7SpatialState? Spatial
    {
        get=>_renderSourceSpatial;
        internal set {_renderSourceSpatial=value;RenderMutationSink?.Invoke(this);}
    }
    public Net7AuxDataSchema? AuxData { get; }
    private Net7AuxDataMaterializationAudit? _renderSourceAuxDataAudit;
    public Net7AuxDataMaterializationAudit? AuxDataAudit
    {
        get=>_renderSourceAuxDataAudit;
        internal set {_renderSourceAuxDataAudit=value;RenderMutationSink?.Invoke(this);}
    }
    private Net7ColorizationApplicationAudit? _renderSourceColorizationAudit;
    public Net7ColorizationApplicationAudit? ColorizationAudit
    {
        get=>_renderSourceColorizationAudit;
        internal set {_renderSourceColorizationAudit=value;RenderMutationSink?.Invoke(this);}
    }
    private Net7AvatarData? _renderSourceAvatarDescription;
    public Net7AvatarData? AvatarDescription
    {
        get=>_renderSourceAvatarDescription;
        internal set {_renderSourceAvatarDescription=value;RenderMutationSink?.Invoke(this);}
    }
    private IReadOnlyList<Net7DecalEntry> _renderSourceDecals = Array.Empty<Net7DecalEntry>();
    public IReadOnlyList<Net7DecalEntry> Decals
    {
        get=>_renderSourceDecals;
        internal set {_renderSourceDecals=value;RenderMutationSink?.Invoke(this);}
    }
    private IReadOnlyList<Net7ColorizationEntry> _renderSourceColorizationEntries = Array.Empty<Net7ColorizationEntry>();
    public IReadOnlyList<Net7ColorizationEntry> ColorizationEntries
    {
        get=>_renderSourceColorizationEntries;
        internal set {_renderSourceColorizationEntries=value;RenderMutationSink?.Invoke(this);}
    }
    private ulong _renderSourceRenderStateProjectionRevision;
    public ulong RenderStateProjectionRevision
    {
        get=>_renderSourceRenderStateProjectionRevision;
        internal set {_renderSourceRenderStateProjectionRevision=value;RenderMutationSink?.Invoke(this);}
    }
    private ulong _renderSourceAttachmentProjectionRevision;
    public ulong AttachmentProjectionRevision
    {
        get=>_renderSourceAttachmentProjectionRevision;
        internal set {_renderSourceAttachmentProjectionRevision=value;RenderMutationSink?.Invoke(this);}
    }
    public IReadOnlyDictionary<uint, uint> RenderStateLinks => _renderStateLinks;
    public IReadOnlySet<uint> ActiveRenderStates => _activeRenderStates;
    public IReadOnlyList<Net7RenderStateAction> RenderStateActions => _renderStateActions;
    public IReadOnlyList<Net7AttachmentPresentation> Attachments => _attachments;
    private Net7NameDecalUpdate? _renderSourceNameDecal;
    public Net7NameDecalUpdate? NameDecal
    {
        get=>_renderSourceNameDecal;
        internal set {_renderSourceNameDecal=value;RenderMutationSink?.Invoke(this);}
    }
    private int _renderSourceNameDecalApplyCount;
    public int NameDecalApplyCount
    {
        get=>_renderSourceNameDecalApplyCount;
        internal set {_renderSourceNameDecalApplyCount=value;RenderMutationSink?.Invoke(this);}
    }
    private int _renderSourceNameDecalColorizationApplyCount;
    public int NameDecalColorizationApplyCount
    {
        get=>_renderSourceNameDecalColorizationApplyCount;
        internal set {_renderSourceNameDecalColorizationApplyCount=value;RenderMutationSink?.Invoke(this);}
    }
    private int _renderSourceDecalApplyCount;
    public int DecalApplyCount
    {
        get=>_renderSourceDecalApplyCount;
        internal set {_renderSourceDecalApplyCount=value;RenderMutationSink?.Invoke(this);}
    }
    private IReadOnlyList<Net7SubPartValue> _renderSourceSubParts = Array.Empty<Net7SubPartValue>();
    public IReadOnlyList<Net7SubPartValue> SubParts
    {
        get=>_renderSourceSubParts;
        internal set {_renderSourceSubParts=value;RenderMutationSink?.Invoke(this);}
    }
    public IReadOnlyList<ulong> ShipLegoAttachmentMaterializationRevisions => ShipLegoAttachmentRevisions;
    public IReadOnlyDictionary<int, ulong> SubPartMaterializationRevisions => MutableSubPartRevisions;
    public IReadOnlyList<Net7LegoControllerMutation> LegoControllerMutations=>MutableLegoControllerMutations;
    private ulong _renderSourceFixedShipVisualRevision;
    public ulong FixedShipVisualRevision
    {
        get=>_renderSourceFixedShipVisualRevision;
        internal set {_renderSourceFixedShipVisualRevision=value;RenderMutationSink?.Invoke(this);}
    }
    private ulong _renderSourceBaseAssetMaterializationRevision;
    public ulong BaseAssetMaterializationRevision
    {
        get=>_renderSourceBaseAssetMaterializationRevision;
        internal set {_renderSourceBaseAssetMaterializationRevision=value;RenderMutationSink?.Invoke(this);}
    }
    private uint? _renderSourcePendingLocalMovementId;
    public uint? PendingLocalMovementId
    {
        get=>_renderSourcePendingLocalMovementId;
        internal set {_renderSourcePendingLocalMovementId=value;RenderMutationSink?.Invoke(this);}
    }
    private uint _renderSourceSpecialTargetEndpointId = uint.MaxValue;
    public uint SpecialTargetEndpointId
    {
        get=>_renderSourceSpecialTargetEndpointId;
        internal set {_renderSourceSpecialTargetEndpointId=value;RenderMutationSink?.Invoke(this);}
    }
    private float _renderSourceSpecialTargetTransition = 1f;
    public float SpecialTargetTransition
    {
        get=>_renderSourceSpecialTargetTransition;
        internal set {_renderSourceSpecialTargetTransition=value;RenderMutationSink?.Invoke(this);}
    }
    internal Dictionary<uint, uint> MutableRenderStateLinks => _renderStateLinks;
    internal HashSet<uint> MutableActiveRenderStates => _activeRenderStates;
    internal List<Net7RenderStateAction> MutableRenderStateActions => _renderStateActions;
    internal List<Net7AttachmentPresentation> MutableAttachments => _attachments;
    internal ulong[] ShipLegoAttachmentRevisions { get; } = new ulong[18];
    internal Dictionary<int, ulong> MutableSubPartRevisions { get; } = [];
    internal List<Net7LegoControllerMutation> MutableLegoControllerMutations { get; }=[];

    private readonly Dictionary<uint, uint> _renderStateLinks = [];
    private readonly HashSet<uint> _activeRenderStates = [];
    private readonly List<Net7RenderStateAction> _renderStateActions = [];
    private readonly List<Net7AttachmentPresentation> _attachments = [];
}

/// <summary>
/// Current retail's 257-bucket object registry. New collisions are inserted
/// immediately after the current bucket head, duplicates are allowed, and
/// lookup/removal always acts on the first matching node.
/// </summary>
public sealed partial class Net7WorldRegistry
{
    public const int BucketCount = 257;

    private sealed class Node(Net7WorldObject value)
    {
        public Net7WorldObject Value { get; } = value;
        public Node? Next { get; set; }
    }

    private readonly Node?[] _buckets = new Node[BucketCount];
    //7274D0 ->48A120 prepends a newly occupied bucket to the global list.
    //Collisions remain contiguous after its head. Radar7BCA70 walks this
    //global order, not numeric hash slots or object creation order.
    private readonly LinkedList<int> _bucketOrder = new();
    private ulong _nextCreationOrdinal;
    public ulong LifetimeEpoch {get;private set;}

    public int Count { get; private set; }
    public ulong MembershipRevision { get; private set; }

    public Net7WorldObject Create(Net7CreateUpdate create, Net7AuxDataSchema? auxData)
    {
        ArgumentNullException.ThrowIfNull(create);
        ulong creationOrdinal = _nextCreationOrdinal = checked(_nextCreationOrdinal + 1);
        var obj = new Net7WorldObject(create, auxData, creationOrdinal);
        int bucket = Bucket(create.GameId);
        var node = new Node(obj);
        Node? head = _buckets[bucket];
        if (head is null)
        {
            _buckets[bucket] = node;
            _bucketOrder.AddFirst(bucket);
        }
        else
        {
            node.Next = head.Next;
            head.Next = node;
        }
        Count++;
        MembershipRevision++;
        PublishMembership(new(MembershipRevision, Net7WorldMembershipChangeKind.Add, obj, head?.Value));
        return obj;
    }

    public Net7WorldObject? FindFirst(uint gameId)
    {
        for (Node? node = _buckets[Bucket(gameId)]; node is not null; node = node.Next)
        {
            if (node.Value.GameId == gameId)
                return node.Value;
        }
        return null;
    }

    public Net7WorldObject? RemoveFirst(uint gameId)
    {
        int bucket = Bucket(gameId);
        Node? node = _buckets[bucket];
        Node? previous = null;
        while (node is not null)
        {
            if (node.Value.GameId == gameId)
            {
                if (previous is null)
                    _buckets[bucket] = node.Next;
                else
                    previous.Next = node.Next;
                if (_buckets[bucket] is null) _bucketOrder.Remove(bucket);
                Count--;
                MembershipRevision++;
                PublishMembership(new(MembershipRevision, Net7WorldMembershipChangeKind.Remove, node.Value, null));
                return node.Value;
            }
            previous = node;
            node = node.Next;
        }
        return null;
    }

    public IReadOnlyList<Net7WorldObject> Snapshot()
    {
        List<Net7WorldObject> objects = new(Count);
        foreach (int bucket in _bucketOrder)
        {
            for (Node? node = _buckets[bucket]; node is not null; node = node.Next)
                objects.Add(node.Value);
        }
        return objects;
    }

    public void Clear()
    {
        bool hadObjects = Count != 0;
        if (hadObjects) MembershipRevision++;
        foreach (int bucket in _bucketOrder)
            for (Node? node=_buckets[bucket];node is not null;node=node.Next)
                node.Value.RenderMutationSink=null;
        Array.Clear(_buckets, 0, _buckets.Length);
        _bucketOrder.Clear();
        Count = 0;
        _nextCreationOrdinal = 0;LifetimeEpoch++;
        if (hadObjects) PublishMembership(new(MembershipRevision, Net7WorldMembershipChangeKind.Reset, null, null));
    }

    private static int Bucket(uint gameId) => (int)(gameId % BucketCount);
}

public sealed record Net7WorldApplyResult(
    bool RequestTimeDue = false,
    Net7ServerHandoff? ServerHandoff = null,
    bool PacketMaterialized = true,
    string? AuxDataWarning = null)
{
    public string? AuxDataCompatibilityNote { get; init; }
    public string? AuxDataFailureReceipt { get; init; }
    public Net7TargetSelectionChange? TargetSelectionChange { get; init; }
}

public sealed record Net7TargetSelectionChange(uint? Previous, uint? Current);

/// <summary>
/// The client-side materialized world. This is the clean domain-state
/// equivalent of the retail object registry, spatial providers and AuxData roots.
/// </summary>
public sealed class Net7WorldState
{
    private const int ShipLegosFirstAttachmentPropertyId = 1257;
    private const int ShipLegosAttachmentStride = 5;
    private const int ShipLegosAttachmentCount = 18;

    private const float LargeAdvancedErrorSquared = 6_250_000f;
    private const float RedundantAdvancedErrorSquared = 10f;
    private const float ExistingSpeedStationaryEpsilon = 0.0001f;
    private const float IncomingMotionEpsilon = 0.00001f;

    private readonly Net7AuxDataSchemaCatalog _schemas;
    private readonly Net7AuxDataCodec _auxCodec;
    private readonly Net7PresentationCatalogs _presentation;
    private readonly Dictionary<uint, Net7StarbaseAvatarState> _stationAvatars = [];
    private System.Collections.Immutable.ImmutableList<Net7StationAvatarEvent> _stationAvatarEvents=[];
    private ulong _stationAvatarSequence;
    public Guid StationAvatarStreamId {get;private set;}=Guid.NewGuid();
    public System.Collections.Immutable.ImmutableList<Net7StationAvatarEvent> StationAvatarEvents=>_stationAvatarEvents;
    public void PublishStationSpeaker(string identity, byte type, Vector3? color=null)
        => _stationAvatarEvents = _stationAvatarEvents.Add(new(++_stationAvatarSequence, Speaker: new(identity,type,color)));
    public void PublishSpaceSpeaker(string identity, byte type, Vector3? color=null)
    {
        // 727130 uses the first native registry match and a case-sensitive
        // 6FF4B0 identity. Resolve at ingress; later creations cannot receive it.
        if (Objects.Snapshot().FirstOrDefault(o=>o.ChatIdentity.Equals(identity,StringComparison.Ordinal)) is {} obj)
        { obj.SpeakerRevision++; obj.SpeakerType=type; obj.SpeakerColor=color; }
    }
    private readonly Dictionary<uint, Net7AvatarData> _avatarDescriptions = [];
    private readonly Dictionary<uint,ulong> _avatarCacheLifetimes = [];
    private ulong _avatarCacheSequence;
    public IReadOnlyDictionary<uint,ulong> AvatarCacheLifetimes => _avatarCacheLifetimes;
    private readonly List<Net7SplineUpdate> _splineUpdates = [];

    public Net7WorldState(
        Net7AuxDataSchemaCatalog? schemas = null,
        Net7AuxDataCodec? auxCodec = null,
        Net7PresentationCatalogs? presentation = null,
        Net7ProcessCompatibilityState? processCompatibilityState = null)
    {
        _schemas = schemas ?? new Net7AuxDataSchemaCatalog();
        _auxCodec = auxCodec ?? new Net7AuxDataCodec();
        _presentation = presentation ?? new Net7PresentationCatalogs();
        Time = new Net7ClientTimeState(processCompatibilityState);
        LocalPlayerAuxData = Net7AuxDataSchemaCatalog.Clone(_schemas.LocalPlayer);
        PlayerVar0AuxData = Net7AuxDataSchemaCatalog.Clone(_schemas.PlayerVar0);
    }

    public Net7WorldRegistry Objects { get; } = new();
    public Net7AuxDataSchema LocalPlayerAuxData { get; }
    public Net7AuxDataSchema PlayerVar0AuxData { get; }
    public Net7ClientTimeState Time { get; }
    public uint? LocalPlayerId { get; private set; }
    public Net7ServerHandoff? PendingHandoff { get; private set; }
    // Presentation receipt survives completion so a UI cadence cannot miss a quick handoff.
    public Net7ServerHandoff? LastPresentationHandoff { get; private set; }
    public ulong PresentationHandoffVersion { get; private set; }
    public bool IsTransitioning { get; private set; }
    public Net7PresentationCatalogs PresentationCatalogs => _presentation;
    public uint? ClientAvatarId { get; private set; }
    public uint? ClientShipId { get; private set; }

    /// <summary>
    /// World-object identity for the local ship. Current 0x0047 Client Ship is
    /// authoritative when present. The 0x0005 Start value is a distinct gameplay
    /// player/session identity on current Net7 and can differ from the ship GameID.
    /// Fall back to the Start identity only for older/synthetic flows that do not
    /// materialize 0x0047.
    /// </summary>
    public uint? LocalShipObjectId => ClientShipId ?? LocalPlayerId;

    public Net7ServerParametersPresentationUpdate? ServerParameters { get; private set; }
    public int ServerParametersSplineUpdateCount { get; private set; }
    /// <summary>SClient+0x136C when the latest 0x0042 ran SClientSetEnvironmentBaseId.</summary>
    public int? ServerParametersPresentationMode { get; private set; }
    public Net7BaseAssetDefinition? EnvironmentPresentation { get; private set; }
    public IReadOnlyDictionary<uint, Net7StarbaseAvatarState> StationAvatars => _stationAvatars;
    public IReadOnlyDictionary<uint, Net7AvatarData> AvatarDescriptions => _avatarDescriptions;
    public Net7ObjectSpatialBounds ObjectSpatialBounds { get; private set; } = Net7ObjectSpatialBounds.Initial;
    public Net7ZBandUpdate? ZBand { get; private set; }
    public Net7BoundingBoxUpdate? BoundingBox { get; private set; }
    public Net7SplineUpdate? Spline { get; private set; }
    public IReadOnlyList<Net7SplineUpdate> SplineUpdates => _splineUpdates;

    public uint? CurrentTargetId
    {
        get
        {
            if (LocalShipObjectId is not uint localId)
                return null;
            Net7AuxDataProperty? property = Objects.FindFirst(localId)?.AuxData?.Resolve(Net7AuxDataPropertyIds.ShipTargetGameId);
            if (property is null || property.Kind != Net7AuxDataValueKind.Int32LittleEndian || property.State == 0 || property.Value is not int value || value == 0)
                return null;
            return unchecked((uint)value);
        }
    }

    public Net7WorldObject? CurrentTarget => CurrentTargetId is uint id ? Objects.FindFirst(id) : null;

    public bool LocalShipLockSpeedActive => LocalShipBooleanActive(Net7AuxDataPropertyIds.ShipLockSpeed);

    public bool LocalShipLockOrientActive => LocalShipBooleanActive(Net7AuxDataPropertyIds.ShipLockOrient);

    private bool LocalShipBooleanActive(int propertyId)
    {
        if (LocalShipObjectId is not uint localId)
            return false;
        Net7AuxDataSchema? auxData = Objects.FindFirst(localId)?.AuxData;
        if (auxData is null || auxData.Properties.Count <= propertyId)
            return false;
        Net7AuxDataProperty property = auxData.Resolve(propertyId);
        return property is
        {
            Kind: Net7AuxDataValueKind.BooleanByte,
            State: not 0,
            Value: byte value,
        } && value != 0;
    }

    public bool ClientSendUiTriggersEnabled
    {
        get
        {
            Net7AuxDataProperty property = LocalPlayerAuxData.Resolve(Net7AuxDataPropertyIds.LocalPlayerClientSendUiTriggers);
            return property.Kind == Net7AuxDataValueKind.Int32LittleEndian
                && property.State != 0
                && property.Value is int value
                && value > 0;
        }
    }

    public void SetLocalPlayerId(uint playerId) => LocalPlayerId = playerId;

    // Native-domain entry points. Gameplay extensions materialize the
    // same logical world state directly instead of synthesizing legacy retail
    // packet bundles inside the integrated client.
    public Net7WorldObject MaterializeCreate(Net7CreateUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return CreateObject(update);
    }

    public void MaterializeRelationship(Net7RelationshipUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        _ = ApplyRelationship(update);
    }

    public void MaterializeComponent(Net7ComponentPositionUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        _ = ApplyComponent(update);
    }

    public void MaterializeAdvanced(Net7AdvancedPositionUpdate update, uint currentClientTime)
    {
        ArgumentNullException.ThrowIfNull(update);
        _ = ApplyAdvanced(update, currentClientTime);
    }

    public Net7WorldObject? MaterializeRemove(uint gameId) => Objects.RemoveFirst(gameId);

    public void MaterializeLocalRaw4Property(int propertyId, uint value)
    {
        Net7AuxDataProperty property = LocalPlayerAuxData.Resolve(propertyId);
        if (property.Kind != Net7AuxDataValueKind.Raw4)
            throw new InvalidOperationException($"LocalPlayer PropertyId {propertyId} is {property.Kind}, not Raw4.");

        byte[] bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        property.Value = bytes;

        // The retail bridge expresses this as an ordinary default
        // AuxData value update. Finalizing such an update
        // marks the addressed property and every parent aggregate state 2.
        // Preserve that semantic callback chain even though integrated EnbNext
        // does not manufacture the intermediate retail 0x001B packet.
        for (Net7AuxDataProperty? current = property; current is not null; current = current.Parent)
            current.State = 2;
    }

    public void MaterializeLocalTimePropertyFromWireTimestamp(int propertyId, uint wireTimestamp)
    {
        Net7AuxDataProperty property = LocalPlayerAuxData.Resolve(propertyId);
        if (property.Kind != Net7AuxDataValueKind.Time32LittleEndianRelative)
        {
            throw new InvalidOperationException(
                $"LocalPlayer PropertyId {propertyId} is {property.Kind}, not a four-byte Time property.");
        }

        // The local Prospect product is exactly the default
        // one-value AuxData delta. Feed that same wire value through the
        // canonical codec so zero handling, signed-u32 interpretation, the
        // client correction origin, and parent state propagation stay identical.
        byte[] delta = new byte[21];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(delta.AsSpan(1, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(delta.AsSpan(5, 4), checked((uint)propertyId));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(delta.AsSpan(9, 4), wireTimestamp);
        _auxCodec.Apply(LocalPlayerAuxData, delta, new Net7AuxDataCodecContext(Time.CorrectionMilliseconds));
    }

    internal void SetPendingLocalMovement(uint gameId, uint movementId)
    {
        Net7WorldObject obj = Objects.FindFirst(gameId)
            ?? throw new InvalidOperationException($"Cannot set predicted movement for missing object 0x{gameId:X8}.");
        obj.PendingLocalMovementId = movementId;
    }

    public bool ObserveLocalPredictedControlChanges(uint gameId, uint changedControlDimensions)
    {
        if (changedControlDimensions == 0)
            return false;

        Net7WorldObject? obj = Objects.FindFirst(gameId);
        if (obj?.Spatial is not { Kind: Net7SpatialProviderKind.AdvancedLocal, MovementId: uint authoritativeMovementId })
            return false;

        // The local kind-4 provider marks prediction pending and
        // advances its stored MovementID once for each control dimension that
        // actually changed. PendingLocalMovementId is EnbNext's clean equivalent
        // of that provider-owned predicted/reconciliation cursor.
        uint predicted = obj.PendingLocalMovementId ?? authoritativeMovementId;
        obj.PendingLocalMovementId = unchecked(predicted + changedControlDimensions);
        return true;
    }

    public bool ApplyLocalFlightControls(uint gameId, uint time, float? horizontal, float? vertical, int? thrust)
    {
        var obj = Objects.FindFirst(gameId);
        if (obj?.Spatial is not { Kind: Net7SpatialProviderKind.AdvancedLocal, Model: { } model } || obj.AuxData is not { } aux)
            return false;
        // 00717430/007179A0 test property type and state, not float finiteness.
        float? Read(int id) => aux.Resolve(id) is { State: not 0, Value: float value } ? value : null;
        float? turn = horizontal == 0 ? 0 : horizontal * Read(18) * -0.001f;
        float? tilt = vertical is not null && vertical != 0 ? vertical * Read(17) * -0.001f : null;
        float? level = vertical == 0 ? aux.Resolve(25) is { State: not 0, Value: true } ? (Read(17) ?? 0) * 0.001f : 0 : null;
        float? speed = thrust switch { 1 => Read(20) * 0.001f, -1 => Read(21) * 0.001f, 0 => 0, _ => null };
        float? acceleration = speed is not null ? Read(22) * 0.000001f : null;
        uint count = (turn.HasValue ? 1u : 0u) + (tilt.HasValue || level.HasValue ? 1u : 0u) + (speed.HasValue && acceleration.HasValue ? 1u : 0u);
        if (count == 0) return false;
        //717430/7179A0: clone retained +24, not every active correction branch.
        if(obj.Spatial.LocalCommandState is { } command)
        {
            uint rtt=unchecked((uint)Time.PatchedRttMilliseconds);
            var next=command.WithControls(unchecked(time+(rtt>>1)),turn,tilt,speed,acceleration,level);
            uint endTime=unchecked(time+rtt);
            var prediction=new Net7SpatialSamplingContext(time);
            if(!model.TrySample(this,time,prediction,out var first))return false;
            //712DB0 samples its start branch twice for an empty interval.
            Net7SpatialSample last;
            if(endTime<=time)
            {
                if(!model.TrySample(this,endTime,prediction,out last))return false;
            }
            else if(!next.TrySample(this,endTime,prediction,out last))return false;
            obj.Spatial.Model=new Net7EndpointTransitionSpatialModel(model,next,time,endTime,first,last);
            obj.Spatial.LocalCommandState=next;
        }
        ObserveLocalPredictedControlChanges(gameId, count);
        return true;
    }

    public bool TrySampleObject(uint gameId, uint clientTime, out Net7SpatialSample sample)
        => TrySampleObjectCore(gameId, clientTime, new Net7SpatialSamplingContext(clientTime,prediction:false), out sample);

    /// <summary>Object getter semantics for scene consumers. Provider sampling
    /// remains separate for prediction/physics; cinematic providers12..14 are
    /// applied downstream by their own runtimes.</summary>
    public bool TrySampleObjectPresentation(uint gameId,uint clientTime,out Net7SpatialSample sample)
    {
        return TrySampleObjectPresentation(Objects.FindFirst(gameId),clientTime,
            new Net7SpatialSamplingContext(clientTime,prediction:false),out sample);
    }

    /// <summary>
    /// ConstructClientGameObject 6F2820 installs NullSpatialStateProvider
    /// (vtable 00B006B8) at +98 before any positional packet. It reports
    /// identity (6F2E50) and zero position, velocity and rotation
    /// (6F2EA0/6F2ED0/6F2F00); 6F4C20/6F4610 still apply the type-1 bounds
    /// to that provider result with provider id 0. A duplicate GameID is a
    /// separate ClientGameObject with its own provider; 7274D0 only changes
    /// which one a later ID lookup finds.
    /// </summary>
    public bool TrySampleNullProviderPresentation(Net7WorldObject obj,uint clientTime,out Net7SpatialSample sample)
    {
        if(obj.Spatial is not null){sample=default;return false;}
        sample=new Net7SpatialSample(clientTime,ObjectSpatialBounds.Position(Vector3.Zero,obj.ObjectType,0),
            Quaternion.Identity,Vector3.Zero,0,Vector3.Zero)
        {OrientationColumn0=Vector3.UnitX,ProviderTransform=ObjectSpatialBounds.Transform(Matrix4x4.Identity,obj.ObjectType,0)};
        return true;
    }

    internal bool TrySampleObjectPresentation(Net7WorldObject? obj,uint clientTime,
        Net7SpatialSamplingContext context,out Net7SpatialSample sample)
    {
        if(!TrySampleObjectCore(obj,clientTime,context,out sample))return false;
        // Net7's Planet enum is12, but the actual715D90 virtual returns5.
        int providerKind=obj!.Spatial!.Kind==Net7SpatialProviderKind.Planet?5:(int)obj.Spatial.Kind;
        sample=sample with{BankHeadingTarget=obj.Spatial.Model!.BankHeadingTarget,
            Position=ObjectSpatialBounds.Position(sample.Position,obj.ObjectType,providerKind),
            ProviderTransform=sample.ProviderTransform is {} matrix?ObjectSpatialBounds.Transform(matrix,obj.ObjectType,providerKind):null};
        return true;
    }

    internal bool TrySampleObjectCore(
        uint gameId,
        uint clientTime,
        Net7SpatialSamplingContext context,
        out Net7SpatialSample sample)
    {
        return TrySampleObjectCore(Objects.FindFirst(gameId),clientTime,context,out sample);
    }

    private bool TrySampleObjectCore(Net7WorldObject? obj,uint clientTime,
        Net7SpatialSamplingContext context,out Net7SpatialSample sample)
    {
        uint gameId=obj?.GameId??0;
        INet7SpatialModel? model = obj?.Spatial?.Model;
        if (model is null || !context.TryEnter(gameId))
        {
            sample = default;
            return false;
        }

        try
        {
            model=model.Resolve(clientTime);
            obj!.Spatial!.Model=model;
            return model.TrySample(this, clientTime, context, out sample);
        }
        finally
        {
            context.Exit(gameId);
        }
    }

    public Net7WorldApplyResult Apply(Packet packet, uint currentClientTime)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return packet.Header.Opcode switch
        {
            0x0004 => ApplyCreate(Net7WorldWire.DecodeCreate(packet.Payload.Span)),
            0x0007 => ApplyRemove(Net7WorldWire.DecodeRemove(packet.Payload.Span)),
            0x0008 => ApplySimplePacket(packet.Payload.Span, currentClientTime),
            0x0010 => ApplyDecal(Net7EffectsRenderWire.DecodeDecal(packet.Payload.Span)),
            0x0011 => ApplyColorization(Net7EffectsRenderWire.DecodeColorization(packet.Payload.Span)),
            0x0019 => ApplySetTarget(Net7WorldWire.DecodeSetTarget(packet.Payload.Span)),
            0x0026 => ApplyChangeBaseAsset(Net7PresentationWire.DecodeChangeBaseAsset(packet.Payload.Span)),
            0x002A => ApplyZBand(Net7EffectsRenderWire.DecodeZBand(packet.Payload.Span)),
            0x002B => ApplyBoundingBox(Net7EffectsRenderWire.DecodeBoundingBox(packet.Payload.Span)),
            0x002F => ApplyInitializeRenderState(Net7EffectsRenderWire.DecodeInitializeRenderState(packet.Payload.Span), currentClientTime),
            0x0030 => ApplyRenderStateAction(0x0030, Net7EffectsRenderWire.DecodeRenderStateAction(packet.Payload.Span, 0x0030), currentClientTime),
            0x0031 => ApplyRenderStateAction(0x0031, Net7EffectsRenderWire.DecodeRenderStateAction(packet.Payload.Span, 0x0031), currentClientTime),
            0x0032 => ApplyRenderStateAction(0x0032, Net7EffectsRenderWire.DecodeRenderStateAction(packet.Payload.Span, 0x0032), currentClientTime),
            0x0037 => ApplyClientAvatar(Net7PresentationWire.DecodeClientAvatarId(packet.Payload.Span)),
            0x0038 => ApplySpline(Net7EffectsRenderWire.DecodeSpline(packet.Payload.Span)),
            0x001B or 0x001C => ApplyAux(Net7WorldWire.DecodeAuxDataEnvelope(packet.Header.Opcode, packet.Payload.Span),packet.Payload.Span,currentClientTime),
            0x0034 => ApplySetClientTime(packet.Payload.Span, currentClientTime),
            0x003A => ApplyHandoff(Net7WorldWire.DecodeServerHandoff(packet.Payload.Span)),
            0x003E => ApplyAdvancedPacket(packet.Payload.Span, currentClientTime),
            0x003F => ApplyPlanetPacket(packet.Payload.Span),
            0x0040 => ApplyFixedPacket(packet.Payload.Span),
            0x0041 => ApplyFormation(Net7WorldWire.DecodeFormationPosition(packet.Payload.Span), currentClientTime),
            0x0042 => ApplyServerParameters(Net7PresentationWire.DecodeServerParameters(packet.Payload.Span)),
            0x0046 => ApplyComponentPacket(packet.Payload.Span),
            0x0047 => ApplyClientShip(Net7PresentationWire.DecodeClientShipId(packet.Payload.Span)),
            0x004A => ApplyCreateAttachment(Net7EffectsRenderWire.DecodeCreateAttachment(packet.Payload.Span)),
            0x0061 => ApplyAvatarDescription(Net7PresentationWire.DecodeAvatarDescription(packet.Payload.Span)),
            0x0085 => ApplyAvatarRecustomization(Net7PresentationWire.DecodeAvatarRecustomizeUpdate(packet.Payload.Span)),
            0x0089 => ApplyRelationship(Net7WorldWire.DecodeRelationship(packet.Payload.Span)),
            0x008A => ApplyReactionBatch(Net7WorldWire.DecodeReactionBatch(packet.Payload.Span)),
            0x008B => ApplyAttackerBatch(Net7WorldWire.DecodeAttackerBatch(packet.Payload.Span)),
            0x0099 => ApplyNavigation(Net7WorldWire.DecodeNavigation(packet.Payload.Span)),
            0x009A => ApplyNavigationDelete(Net7WorldWire.DecodeNavigationDelete(packet.Payload.Span)),
            0x009E => ApplyStarbaseAvatarChange(Net7PresentationWire.DecodeStarbaseAvatarChange(packet.Payload.Span)),
            0x00A0 => ApplyStarbaseRoomDeparture(Net7UiPresentationWire.DecodeStarbaseRoomUpdate(packet.Payload.Span)),
            0x00B2 => ApplyNameDecal(Net7EffectsRenderWire.DecodeNameDecal(packet.Payload.Span)),
            0x00B4 => ApplySubParts(Net7EffectsRenderWire.DecodeSubParts(packet.Payload.Span)),
            _ => new Net7WorldApplyResult(PacketMaterialized: false),
        };
    }

    public Net7WorldObject ApplyAdvancedObjectA(Net7AdvancedObjectA update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Net7CreateUpdate create = new(
            update.GameId,
            update.Scale,
            unchecked((short)update.BaseAsset),
            update.ObjectType,
            update.HsvH,
            update.HsvS,
            update.HsvV);
        CreateObject(create);

        // Preserve the retail synthetic-bundle lookup semantics: each packet in
        // Create -> Relationship -> Position -> Aux -> Navigation resolves the
        // registry independently, so a duplicate Create can leave the old head as
        // the object affected by the following packets.
        Net7WorldObject target = Objects.FindFirst(update.GameId)
            ?? throw new InvalidOperationException("Created object was not discoverable through the current registry.");
        target.RelationshipRaw = update.Reaction;
        target.IsAttacking = 0;
        ApplyFixedToObject(target, new Net7FixedPositionUpdate(update.GameId, update.Position, update.Orientation));

        // Step 4 is not merely a convenient display-name assignment: the
        // current translator synthesizes a real object AuxData delta and the retail
        // client applies it to the first matching registry object. Preserve that
        // materialized AuxData state in the integrated client as well, including the
        // Type 0x25 Name/AppearsInRadar/RadarRange three-child update.
        ApplyAdvancedObjectAAuxData(target, update);

        if ((update.Flags & 0x10) != 0)
        {
            target.Navigation = new Net7NavigationUpdate(
                update.GameId,
                update.SharedNavigationRadarScalar,
                (byte)(((update.Flags & 0x80) != 0) ? 1 : 0),
                (uint)(update.Flags & 0x0F),
                (byte)(((update.Flags & 0x40) != 0) ? 1 : 0));
        }

        return target;
    }

    private void ApplyAdvancedObjectAAuxData(Net7WorldObject target, Net7AdvancedObjectA update)
    {
        Net7AuxDataSchema? auxData = target.AuxData;
        if (auxData is null)
            return;

        int effectiveNameLength = Array.IndexOf(update.NameBytes, (byte)0);
        if (effectiveNameLength < 0)
            effectiveNameLength = update.NameBytes.Length;
        byte[] effectiveNameBytes = update.NameBytes.AsSpan(0, effectiveNameLength).ToArray();

        byte[] delta;
        if (update.ObjectType == 0x25)
        {
            delta = new byte[checked(effectiveNameBytes.Length + 9)];
            delta[0] = 0x01;
            delta[1] = 0x72;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(delta.AsSpan(2, 2), checked((ushort)effectiveNameBytes.Length));
            effectiveNameBytes.CopyTo(delta.AsSpan(4));
            delta[4 + effectiveNameBytes.Length] = (byte)(((update.Flags & 0x20) != 0) ? 1 : 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                delta.AsSpan(5 + effectiveNameBytes.Length, 4),
                BitConverter.SingleToInt32Bits(update.SharedNavigationRadarScalar));
        }
        else
        {
            delta = new byte[checked(effectiveNameBytes.Length + 4)];
            delta[0] = 0x01;
            delta[1] = 0x12;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(delta.AsSpan(2, 2), checked((ushort)effectiveNameBytes.Length));
            effectiveNameBytes.CopyTo(delta.AsSpan(4));
        }

        _auxCodec.Apply(auxData, delta, new Net7AuxDataCodecContext(Time.CorrectionMilliseconds));

        // Project the materialized root fields into the convenience properties
        // exposed by Net7WorldObject. The AuxData tree remains the authoritative
        // representation, matching what the retail client actually materializes.
        Net7AuxDataProperty nameProperty = auxData.Root.Children[0];
        if (nameProperty.Value is Net7AuxDataString name)
            target.Name = System.Text.Encoding.Latin1.GetString(name.Bytes);

        if (auxData.Name == "Decoration" && auxData.Root.Children.Count >= 3)
        {
            if (auxData.Root.Children[1].Value is byte appearsInRadar)
                target.AppearsInRadar = appearsInRadar != 0;
            if (auxData.Root.Children[2].Value is float radarRange)
                target.RadarRange = radarRange;
        }
    }

    public Net7WorldObject ApplyAdvancedObjectB(Net7AdvancedObjectB update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Net7CreateUpdate create = new(
            update.GameId,
            update.Scale,
            unchecked((short)update.BaseAsset),
            0x26,
            update.HsvH,
            update.HsvS,
            0f);
        CreateObject(create);
        Net7WorldObject target = Objects.FindFirst(update.GameId)
            ?? throw new InvalidOperationException("Created object was not discoverable through the current registry.");
        target.RelationshipRaw = (uint)Net7Reaction.Friendly;
        target.IsAttacking = 0;
        ApplyFixedToObject(target, new Net7FixedPositionUpdate(update.GameId, update.Position, update.Orientation));
        ApplyAdvancedObjectBAuxData(target, update);
        return target;
    }

    private void ApplyAdvancedObjectBAuxData(Net7WorldObject target, Net7AdvancedObjectB update)
    {
        Net7AuxDataSchema? auxData = target.AuxData;
        if (auxData is null)
            return;

        // The fourth synthetic packet is the compact child-0 object-name
        // delta 01 12. Preserve C-string truncation intentionally at this legacy
        // semantic boundary while keeping the other Harvestable children wholly
        // untouched by 0x2019.
        int effectiveNameLength = Array.IndexOf(update.NameBytes, (byte)0);
        if (effectiveNameLength < 0)
            effectiveNameLength = update.NameBytes.Length;
        byte[] effectiveNameBytes = update.NameBytes.AsSpan(0, effectiveNameLength).ToArray();

        byte[] delta = new byte[checked(effectiveNameBytes.Length + 4)];
        delta[0] = 0x01;
        delta[1] = 0x12;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            delta.AsSpan(2, 2), checked((ushort)effectiveNameBytes.Length));
        effectiveNameBytes.CopyTo(delta.AsSpan(4));

        _auxCodec.Apply(auxData, delta, new Net7AuxDataCodecContext(Time.CorrectionMilliseconds));

        if (auxData.Root.Children[0].Value is Net7AuxDataString name)
            target.Name = System.Text.Encoding.Latin1.GetString(name.Bytes);
    }

    private Net7WorldApplyResult ApplyCreate(Net7CreateUpdate create)
    {
        CreateObject(create);
        return new Net7WorldApplyResult();
    }

    private Net7WorldObject CreateObject(Net7CreateUpdate create)
    {
        Net7WorldObject obj = Objects.Create(create, _schemas.CreateObjectSchema(create.ObjectType));
        // Current retail ObjectType 0x08 constructs a dedicated local render
        // object instead of resolving BaseAsset through basset.ini. The numeric
        // selector is still retained on the object for identity/state parity.
        obj.BaseAssetPresentation = create.ObjectType == 0x08
            ? null
            : _presentation.ResolveBaseAsset(create.BaseAsset);
        if (_avatarDescriptions.TryGetValue(create.GameId, out Net7AvatarData? avatar))
            obj.AvatarDescription = avatar;
        return obj;
    }

    private Net7WorldApplyResult ApplyDecal(Net7DecalUpdate update)
    {
        Net7WorldObject? obj = Objects.FindFirst(update.GameId);
        if (obj is not null)
        {
            obj.Decals = update.Decals;
            obj.DecalApplyCount++;
        }
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyColorization(Net7ColorizationUpdate update)
    {
        Net7WorldObject? obj = Objects.FindFirst(update.GameId);
        if (obj is not null)
        {
            obj.ColorizationEntries = update.Entries;
            obj.ColorizationAudit = new Net7ColorizationApplicationAudit(
                ApplyCount: (obj.ColorizationAudit?.ApplyCount ?? 0) + 1,
                ActiveShipLegoAttachmentsAtLastApply: CountActiveShipLegoAttachments(obj.AuxData),
                ActiveSubPartsAtLastApply: obj.SubParts.Count(static part => part.PartValue != 0),
                ObjectAuxDataAttemptsAtLastApply: obj.AuxDataAudit?.AttemptCount ?? 0,
                ShipLegoScope: CaptureActiveShipLegoScope(obj),
                SubPartScope: CaptureActiveSubPartScope(obj));
        }
        return new Net7WorldApplyResult();
    }

    private static int CountActiveShipLegoAttachments(Net7AuxDataSchema? auxData)
    {
        if (auxData is null ||
            !string.Equals(auxData.Name, "ShipAuxData", StringComparison.Ordinal) ||
            auxData.Properties.Count <= ShipLegosFirstAttachmentPropertyId +
            ((ShipLegosAttachmentCount - 1) * ShipLegosAttachmentStride) + 3)
        {
            return 0;
        }

        int count = 0;
        for (int ordinal = 0; ordinal < ShipLegosAttachmentCount; ordinal++)
        {
            int aggregateId = ShipLegosFirstAttachmentPropertyId + (ordinal * ShipLegosAttachmentStride);
            string boneName = auxData.Resolve(aggregateId + 1).Value is Net7AuxDataString bone
                ? bone.Text
                : string.Empty;
            int type = auxData.Resolve(aggregateId + 2).Value is int typeValue ? typeValue : 0;
            int baseAsset = auxData.Resolve(aggregateId + 3).Value is int assetValue ? assetValue : 0;
            if (!string.IsNullOrEmpty(boneName) && type is 1 or 2 or 3 && baseAsset > 0)
                count++;
        }

        return count;
    }

    private static IReadOnlyList<Net7ColorizedShipLegoScopeEntry> CaptureActiveShipLegoScope(Net7WorldObject obj)
    {
        ShipLegoVisualIdentity[] identities = CaptureShipLegoVisualIdentities(obj.AuxData);
        var scope = new List<Net7ColorizedShipLegoScopeEntry>();
        for (int ordinal = 0; ordinal < identities.Length; ordinal++)
        {
            ShipLegoVisualIdentity identity = identities[ordinal];
            if (string.IsNullOrEmpty(identity.BoneName) ||
                identity.Type is not (1 or 2 or 3) ||
                identity.BaseAsset <= 0)
            {
                continue;
            }

            scope.Add(new Net7ColorizedShipLegoScopeEntry(
                ordinal,
                identity.BoneName,
                identity.Type,
                identity.BaseAsset,
                obj.ShipLegoAttachmentRevisions[ordinal]));
        }

        return scope;
    }

    private static IReadOnlyList<Net7ColorizedSubPartScopeEntry> CaptureActiveSubPartScope(Net7WorldObject obj)
    {
        var scope = new List<Net7ColorizedSubPartScopeEntry>();
        for (int ordinal = 0; ordinal < obj.SubParts.Count; ordinal++)
        {
            Net7SubPartValue part = obj.SubParts[ordinal];
            if (part.PartValue == 0)
                continue;
            scope.Add(new Net7ColorizedSubPartScopeEntry(
                ordinal,
                part.Name,
                part.PartValue,
                obj.MutableSubPartRevisions.GetValueOrDefault(ordinal)));
        }

        return scope;
    }

    private static ShipLegoVisualIdentity[] CaptureShipLegoVisualIdentities(Net7AuxDataSchema? auxData)
    {
        var identities = new ShipLegoVisualIdentity[ShipLegosAttachmentCount];
        if (auxData is null ||
            !string.Equals(auxData.Name, "ShipAuxData", StringComparison.Ordinal) ||
            auxData.Properties.Count <= ShipLegosFirstAttachmentPropertyId +
            ((ShipLegosAttachmentCount - 1) * ShipLegosAttachmentStride) + 3)
        {
            return identities;
        }

        for (int ordinal = 0; ordinal < ShipLegosAttachmentCount; ordinal++)
        {
            int aggregateId = ShipLegosFirstAttachmentPropertyId + (ordinal * ShipLegosAttachmentStride);
            identities[ordinal] = new ShipLegoVisualIdentity(
                auxData.Resolve(aggregateId + 1).Value is Net7AuxDataString bone ? bone.Text : string.Empty,
                auxData.Resolve(aggregateId + 2).Value is int type ? type : 0,
                auxData.Resolve(aggregateId + 3).Value is int asset ? asset : 0,
                auxData.Resolve(aggregateId + 1).UpdateRevision,
                auxData.Resolve(aggregateId + 2).UpdateRevision,
                auxData.Resolve(aggregateId + 3).UpdateRevision);
        }

        return identities;
    }

    //00732880 tests the BoneName/Type/BaseAsset notification states, not
    //value inequality. A repeated value still replaces its physical child.
    //Data-only notifications dispatch +3C without creating a new child.
    private readonly record struct ShipLegoVisualIdentity(string BoneName,int Type,int BaseAsset,
        ulong BoneRevision,ulong TypeRevision,ulong AssetRevision);

    private static ulong ShipAttachmentNotificationRevision(Net7AuxDataSchema? aux) =>
        aux is { Name: "ShipAuxData" } && aux.Properties.Count > 1256
            ? aux.Resolve(1256).UpdateRevision : 0;

    private Net7WorldApplyResult ApplyZBand(Net7ZBandUpdate update)
    {
        ZBand = update;
        ObjectSpatialBounds=ObjectSpatialBounds with{MinimumZ=BinaryPrimitives.ReadSingleLittleEndian(update.FieldA),MaximumZ=BinaryPrimitives.ReadSingleLittleEndian(update.FieldB)};
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyBoundingBox(Net7BoundingBoxUpdate update)
    {
        BoundingBox = update;
        ObjectSpatialBounds=ObjectSpatialBounds with{MinimumX=BinaryPrimitives.ReadSingleLittleEndian(update.BlockA),MinimumY=BinaryPrimitives.ReadSingleLittleEndian(update.BlockA.AsSpan(4)),
            MaximumX=BinaryPrimitives.ReadSingleLittleEndian(update.BlockB),MaximumY=BinaryPrimitives.ReadSingleLittleEndian(update.BlockB.AsSpan(4))};
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyInitializeRenderState(Net7InitializeRenderStateUpdate update, uint currentClientTime)
    {
        Net7WorldObject? obj = Objects.FindFirst(update.GameId);
        if (obj is not null)
        {
            obj.RenderStateProjectionRevision++;
            obj.MutableRenderStateLinks[update.RenderStateId] = update.LinkId;
            obj.MutableRenderStateActions.Add(new Net7RenderStateAction(0x002f,update.RenderStateId,currentClientTime,update.LinkId));
        }
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyRenderStateAction(
        ushort opcode,
        Net7RenderStateActionUpdate update,
        uint currentClientTime)
    {
        Net7WorldObject? obj = Objects.FindFirst(update.GameId);
        if (obj is null)
            return new Net7WorldApplyResult();

        obj.RenderStateProjectionRevision++;
        obj.MutableRenderStateActions.Add(new Net7RenderStateAction(opcode, update.RenderStateId, currentClientTime));
        if (opcode == 0x0030)
            obj.MutableActiveRenderStates.Add(update.RenderStateId);
        else if (opcode == 0x0032)
            obj.MutableActiveRenderStates.Remove(update.RenderStateId);
        // 0x0031 is deliberately retained as a one-shot command rather than
        // being collapsed into persistent active-state membership.
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplySpline(Net7SplineUpdate update)
    {
        // Current retail applies 0x0038 mutations cumulatively to its spline
        // database. Keep the legacy latest-value accessor while retaining the
        // ordered mutation stream required to rebuild the presentation database.
        Spline = update;
        _splineUpdates.Add(update);
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyCreateAttachment(Net7CreateAttachmentUpdate update)
    {
        Net7WorldObject? parent = Objects.FindFirst(update.ParentId);
        Net7WorldObject? child = Objects.FindFirst(update.ChildId);
        if (parent is not null && child is not null)
        {
            parent.AttachmentProjectionRevision++;
            parent.MutableAttachments.Add(new Net7AttachmentPresentation(update.ChildId, update.AttachmentSelector));
        }
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyNameDecal(Net7NameDecalUpdate update)
    {
        Net7WorldObject? obj = Objects.FindFirst(update.GameId);
        if (obj is not null)
        {
            obj.NameDecal = update;
            obj.NameDecalApplyCount++;
            obj.NameDecalColorizationApplyCount = obj.ColorizationAudit?.ApplyCount ?? 0;
        }
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplySubParts(Net7SubPartsUpdate update)
    {
        Net7WorldObject? obj = Objects.FindFirst(update.GameId);
        if (obj is not null)
        {
            for(int ordinal=obj.SubParts.Count-1;ordinal>=0;ordinal--)
            {
                var part=obj.SubParts[ordinal];
                obj.MutableLegoControllerMutations.Add(new((ulong)obj.MutableLegoControllerMutations.Count+1,
                    Net7LegoControllerMutationKind.Remove,"0x00B4",ordinal,
                    obj.MutableSubPartRevisions.GetValueOrDefault(ordinal),part.Name,null,1,unchecked((int)part.PartValue)) {RenderStateActionCount=obj.RenderStateActions.Count});
            }
            int revisionCount = Math.Max(obj.SubParts.Count, update.Parts.Count);
            for (int ordinal = 0; ordinal < revisionCount; ordinal++)
            {
                //733A50 -> 6FEF70 removes the entire old list in reverse,
                //then constructs every incoming entry through7294D0.
                //Identical values still denote replacement child lifetimes.
                obj.MutableSubPartRevisions[ordinal] = checked(
                    obj.MutableSubPartRevisions.GetValueOrDefault(ordinal) + 1);
            }
            obj.SubParts = update.Parts;
            for(int ordinal=0;ordinal<update.Parts.Count;ordinal++)
            {
                var part=update.Parts[ordinal];
                obj.MutableLegoControllerMutations.Add(new((ulong)obj.MutableLegoControllerMutations.Count+1,
                    Net7LegoControllerMutationKind.Create,"0x00B4",ordinal,
                    obj.MutableSubPartRevisions.GetValueOrDefault(ordinal),part.Name,null,1,unchecked((int)part.PartValue)) {RenderStateActionCount=obj.RenderStateActions.Count});
            }
            //6FEF70 always finishes with InitializeShipVisualEffects, even
            //for an empty list; no child-value comparison gates that call.
            obj.FixedShipVisualRevision = checked(obj.FixedShipVisualRevision + 1);
        }
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyChangeBaseAsset(Net7ChangeBaseAssetUpdate update)
    {
        Net7WorldObject? obj = Objects.FindFirst(update.GameId);
        if (obj is not null)
        {
            //737400 always creates and installs a new RenderObject via6F33F0;
            //equal BaseAsset/scale/HSV values do not retain the old instance.
            obj.BaseAssetMaterializationRevision = checked(obj.BaseAssetMaterializationRevision + 1);
            //737610/7376F0/7377C0/737890 dispatch to the current +AC RSL.
            //737400 constructs a fresh RenderObject;6F33F0 transfers scene
            //membership, not that old object's initializer/activation history.
            obj.RenderStateProjectionRevision++;
            obj.MutableRenderStateLinks.Clear();
            obj.MutableActiveRenderStates.Clear();
            obj.MutableRenderStateActions.Clear();
            obj.BaseAsset = update.BaseAsset;
            obj.Scale = update.Scale;
            obj.HsvH = update.HsvH;
            obj.HsvS = update.HsvS;
            obj.HsvV = update.HsvV;
            obj.BaseAssetPresentation = _presentation.ResolveBaseAsset(update.BaseAsset);
        }
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyClientAvatar(uint avatarId)
    {
        ClientAvatarId = avatarId;
        return new Net7WorldApplyResult();
    }

    /// <summary>
    /// 00737F00 calls SClientSetEnvironmentBaseId, whose 00513540 replaces a
    /// negative sky BaseAsset by the default of the client mode current at that
    /// moment. The session records that mode after applying 0x0042.
    /// </summary>
    public void CaptureServerParametersPresentationMode(int? presentationMode)
        => ServerParametersPresentationMode = presentationMode;

    private Net7WorldApplyResult ApplyServerParameters(Net7ServerParametersPresentationUpdate update)
    {
        // Current retail loads/reinitializes OrbitalManager from 0x0042 and samples
        // SKYEMISSIVE immediately against the spline database state that exists at
        // that moment. Preserve the exact prefix length so the detached renderer
        // can reproduce that cached load-time value even when later 0x0038 updates
        // have already arrived.
        ServerParametersSplineUpdateCount = _splineUpdates.Count;
        ServerParameters = update;
        var bounds=update.RawPayload.AsSpan();
        ObjectSpatialBounds=new(BinaryPrimitives.ReadSingleLittleEndian(bounds),BinaryPrimitives.ReadSingleLittleEndian(bounds[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(bounds[8..]),BinaryPrimitives.ReadSingleLittleEndian(bounds[12..]),
            BinaryPrimitives.ReadSingleLittleEndian(bounds[16..]),BinaryPrimitives.ReadSingleLittleEndian(bounds[20..]));
        EnvironmentPresentation = _presentation.ResolveBaseAsset(update.EnvironmentBaseAsset);
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyClientShip(uint shipId)
    {
        ClientShipId = shipId;
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyAvatarDescription(Net7AvatarDescriptionUpdate update)
    {
        // Current retail CreateAndCacheAvatar stores AvatarData by GameID even
        // when no live ClientObject exists yet, and returns without replacing
        // an already-cached GameID. Keep that independent first-write-wins cache
        // and project the retained avatar onto a live/future object when available.
        if (!_avatarDescriptions.TryAdd(update.GameId, update.Avatar))
            return new Net7WorldApplyResult();
        _avatarCacheLifetimes[update.GameId]=++_avatarCacheSequence;

        Net7WorldObject? obj = Objects.FindFirst(update.GameId);
        if (obj is not null)
            obj.AvatarDescription = update.Avatar;
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyAvatarRecustomization(Net7AvatarRecustomizeUpdate update)
    {
        ReplaceAvatarAppearance(update.TrailingNetworkValue, update.Avatar);
        return new Net7WorldApplyResult();
    }

    // 006437A0 replaces the avatar cache entry while preserving the station pose.
    // Local Save (0054EC30) makes the same replacement before rebuilding the model.
    public void ReplaceAvatarAppearance(uint id, EnbNext.Net7.Protocol.Net7AvatarData avatar)
    {
        _avatarDescriptions[id] = avatar;
        _avatarCacheLifetimes[id]=++_avatarCacheSequence;
        if (Objects.FindFirst(id) is {} obj) obj.AvatarDescription = avatar;
        _stationAvatarEvents=_stationAvatarEvents.Add(new(++_stationAvatarSequence,ReplacedAvatarId:id,Avatar:avatar,Guild:CaptureStationGuild(id)));
    }

    private Net7WorldApplyResult ApplyStarbaseAvatarChange(Net7StarbaseAvatarState update)
    {
        _stationAvatars.TryGetValue(update.AvatarId,out var previous);
        long revision=(previous?.MotionRevision??0)+1;
        var history=previous is not null && previous.RoomType==update.RoomType?previous.MotionHistory:[];
        // 63D1F0/788730 queues each received target, keeping the last four.
        _stationAvatars[update.AvatarId] = update with {MotionRevision=revision,
            MotionHistory=history.TakeLast(3).Append(new Net7StarbaseAvatarPose(revision,update.Orientation,update.Position,update.ActionFlag)).ToArray()};
        // 63E5C0 can admit an absent room actor only if its appearance already
        // exists. A later 0061 must not retroactively authorize this update.
        _stationAvatarEvents=_stationAvatarEvents.Add(new(++_stationAvatarSequence,Pose:_stationAvatars[update.AvatarId],
            Avatar:_avatarDescriptions.GetValueOrDefault(update.AvatarId),Guild:CaptureStationGuild(update.AvatarId)));
        return new Net7WorldApplyResult();
    }

    private Net7StationGuildState? CaptureStationGuild(uint id)
    {
        var obj=Objects.FindFirst(id);
        if(obj is null || obj.ObjectType>1 || obj.AuxData is not {Name:"ShipAuxData"} aux)return null;
        string? Text(Net7AuxDataSchema schema,int property)
            =>schema.Resolve(property).Value is Net7AuxDataString value?value.Text:
                schema.Resolve(property).State!=0?string.Empty:null;
        Vector3? Color(int property)
        {
            if(aux.Resolve(property).Value is not byte[] {Length:12} bytes)return null;
            var value=new Vector3(BinaryPrimitives.ReadSingleLittleEndian(bytes),BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4)),BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(8)));
            // 00700670 copies the stored components, including non-finite values.
            return value;
        }
        var local=LocalShipObjectId is uint localId?Objects.FindFirst(localId):null;
        string? localName=local is {ObjectType:<=1,AuxData:{Name:"ShipAuxData"} localAux}?Text(localAux,1923):null;
        int rank=aux.Resolve(1924).Value is byte[] {Length:4} rankBytes?BinaryPrimitives.ReadInt32LittleEndian(rankBytes):-1;
        return new(Text(aux,1923),rank,Text(aux,1925),Color(1926),Color(1927),localName);
    }

    private Net7WorldApplyResult ApplyStarbaseRoomDeparture(Net7StarbaseRoomUpdate update)
    {
        // 73AB50 schedules removal from the active room; it does not rewrite
        // the cached avatar's pose or destroy its retained body/NLP owner.
        _stationAvatarEvents=_stationAvatarEvents.Add(new(++_stationAvatarSequence,Departure:update));
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyRemove(uint gameId)
    {
        // 7362F0 ->744B60 ->74C530 retires the independent avatar cache
        // outside the active avatar view, even if no ClientObject exists.
        // Station removal only detaches the world object; its actor survives.
        if (!IsAvatarViewActive())
        {
            _avatarDescriptions.Remove(gameId);
            _avatarCacheLifetimes.Remove(gameId);
        }
        Objects.RemoveFirst(gameId);
        return new Net7WorldApplyResult();
    }

    internal Func<bool> IsAvatarViewActive { get; set; } = static () => false;

    private Net7WorldApplyResult ApplySetTarget(Net7SetTargetUpdate update)
    {
        if (update.EndpointObjectId == uint.MaxValue)
            return new Net7WorldApplyResult();
        Net7WorldObject? endpoint = Objects.FindFirst(update.EndpointObjectId);
        if (endpoint is not null && endpoint.ObjectType == 0x1C)
        {
            endpoint.SpecialTargetEndpointId = update.TargetId;
            endpoint.SpecialTargetTransition = 0f;
        }
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplySimplePacket(ReadOnlySpan<byte> payload, uint currentClientTime)
    {
        try
        {
            return ApplySimple(Net7WorldWire.DecodeSimplePosition(payload), currentClientTime);
        }
        catch (InvalidDataException)
        {
            // Safety rule: 008AC1B0 leaves unread fields uninitialized.
            // Stop before publishing a new model; keep existing world/clock
            // state and continue. Unused packet tails are accepted.
            return new Net7WorldApplyResult();
        }
    }

    private Net7WorldApplyResult ApplySimple(Net7SimplePositionUpdate update, uint currentClientTime)
    {
        uint converted = Time.ConvertAndObserveDrift(update.ServerTimestamp, currentClientTime);
        Net7WorldObject? obj = Objects.FindFirst(update.GameId);
        if (obj is null)
            return new Net7WorldApplyResult();

        INet7SpatialModel incoming = new Net7SimpleSpatialModel(
            converted,
            update.Position,
            update.Orientation,
            update.Velocity);
        Net7SpatialState? previous = obj.Spatial;
        INet7SpatialModel model = previous?.Kind == Net7SpatialProviderKind.Simple
            && previous.Model is not null
            ? new Net7TransitionSpatialModel(previous.Model, incoming, converted, 2000u)
            : incoming;

        obj.Spatial = new Net7SpatialState
        {
            Kind = Net7SpatialProviderKind.Simple,
            Position = update.Position,
            Orientation = update.Orientation,
            Velocity = update.Velocity,
            ClientTimestamp = converted,
            TransitionMilliseconds = model == incoming ? 0u : 2000u,
            CurrentSpeed = update.Velocity.Length(),
            Model = model,
        };
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyAdvancedPacket(ReadOnlySpan<byte> payload, uint currentClientTime)
        => ApplyAdvanced(Net7WorldWire.DecodeAdvancedPosition(payload), currentClientTime);

    private Net7WorldApplyResult ApplyAdvanced(Net7AdvancedPositionUpdate update, uint currentClientTime)
    {
        // 00734130 gates exactly these fields before clock conversion and object lookup.
        if (!float.IsFinite(update.Position.X) || !float.IsFinite(update.Position.Y) || !float.IsFinite(update.Position.Z)
            || !float.IsFinite(update.Orientation.X) || !float.IsFinite(update.Orientation.Y)
            || !float.IsFinite(update.Orientation.Z) || !float.IsFinite(update.Orientation.W)
            || !float.IsFinite(update.CurrentSpeed)
            || !float.IsFinite(update.ImpartedVelocity.X) || !float.IsFinite(update.ImpartedVelocity.Y)
            || !float.IsFinite(update.ImpartedVelocity.Z))
            return new Net7WorldApplyResult();

        uint converted = Time.ConvertAndObserveDrift(update.ServerTimestamp, currentClientTime);
        Net7WorldObject? obj = Objects.FindFirst(update.GameId);
        if (obj is null)
            return new Net7WorldApplyResult();

        bool local = LocalShipObjectId.HasValue && LocalShipObjectId.Value == update.GameId;
        bool requestTime = local && Time.ObserveQualifyingLocalAdvancedUpdate();
        Net7AdvancedKeyframeSpatialModel incoming = new(update, converted);

        if (local)
            return ApplyLocalAdvanced(obj, update, incoming, converted, currentClientTime, requestTime);

        ApplyRemoteAdvanced(obj, update, incoming, converted, currentClientTime);
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyLocalAdvanced(
        Net7WorldObject obj,
        Net7AdvancedPositionUpdate update,
        Net7AdvancedKeyframeSpatialModel incoming,
        uint converted,
        uint currentClientTime,
        bool requestTime)
    {
        Net7SpatialState? previous = obj.Spatial;
        bool existingLocalProvider = previous?.Kind == Net7SpatialProviderKind.AdvancedLocal
            && previous.Model is not null;
        if (!existingLocalProvider)
        {
            // Provider ownership: retail destroys a direct/nested provider
            // whose kind is not 4 and constructs the local advanced provider from
            // the incoming packet. Reconciliation and transition blending belong
            // only to an already-existing kind-4 provider.
            obj.PendingLocalMovementId = null;
            obj.Spatial = BuildAdvancedSpatialState(
                Net7SpatialProviderKind.AdvancedLocal,
                update,
                converted,
                0u,
                incoming);
            obj.Spatial.LocalCommandState=incoming;
            return new Net7WorldApplyResult(RequestTimeDue: requestTime);
        }

        INet7SpatialModel previousModel = previous!.Model!;
        Net7SpatialSample prior = default;
        bool hasPrior = previousModel.TrySample(this, converted, new Net7SpatialSamplingContext(), out prior);
        float errorSquared = hasPrior
            ? Vector3.DistanceSquared(prior.Position, update.Position)
            : 0f;
        bool largeError = hasPrior && errorSquared > LargeAdvancedErrorSquared;

        if (!ShouldAcceptLocalAdvanced(obj, update, hasPrior ? prior.Speed : 0f, errorSquared, largeError))
            return new Net7WorldApplyResult(RequestTimeDue: requestTime);

        uint transitionMs = largeError ? 0u : IsAdvancedMotionActive(update) ? 2000u : 500u;
        INet7SpatialModel model=incoming;
        if(transitionMs!=0)
        {
            //717CC0 starts at max(receipt, packet time). Its speed admission
            //chooses captured endpoints when the >epsilon classifications differ.
            uint startTime=Math.Max(currentClientTime,converted);
            uint endTime=unchecked(startTime+transitionMs);
            if(previousModel.TrySample(this,startTime,new(),out var from)&&incoming.TrySample(this,endTime,new(),out var to))
                model=(from.Speed>Net7SpatialMath.PhysicsEpsilon)==(to.Speed>Net7SpatialMath.PhysicsEpsilon)
                    ?new Net7TransitionSpatialModel(previousModel,incoming,startTime,transitionMs)
                    :new Net7EndpointTransitionSpatialModel(previousModel,incoming,startTime,endTime,from,to);
        }

        obj.Spatial = BuildAdvancedSpatialState(
            Net7SpatialProviderKind.AdvancedLocal,
            update,
            converted,
            transitionMs,
            model);
        obj.Spatial.LocalCommandState=incoming;

        if (largeError || obj.PendingLocalMovementId.HasValue)
            obj.PendingLocalMovementId = null;

        return new Net7WorldApplyResult(RequestTimeDue: requestTime);
    }

    private void ApplyRemoteAdvanced(
        Net7WorldObject obj,
        Net7AdvancedPositionUpdate update,
        Net7AdvancedKeyframeSpatialModel incoming,
        uint converted,
        uint currentClientTime)
    {
        uint transitionMs = 0;
        Net7RemoteAdvancedSpatialModel remote;

        if (obj.Spatial?.Model is Net7RemoteAdvancedSpatialModel previousRemote)
        {
            Net7SpatialSample prior = default;
            bool hasPrior = previousRemote.TrySamplePhysical(
                this,
                converted,
                new Net7SpatialSamplingContext(),
                out prior);
            bool largeError = hasPrior
                && Vector3.DistanceSquared(prior.Position, update.Position) > LargeAdvancedErrorSquared;
            transitionMs = largeError
                ? 0u
                : checked((uint)Math.Max(0, previousRemote.GetEffectiveOffset(currentClientTime)));
            INet7SpatialModel physical = transitionMs == 0
                ? incoming
                : new Net7TransitionSpatialModel(previousRemote.Physical, incoming, converted, transitionMs);
            if (transitionMs != 0)
            {
                // 00716947..0071695A: max(receipt, packet time) minus the
                // current presentation lag. 00716400 also rejects cubic
                // below 1000 units for remote providers (constructor +18=0).
                uint startTime = unchecked(Math.Max(currentClientTime, converted) - transitionMs);
                uint endTime = unchecked(startTime + transitionMs);
                if (previousRemote.TrySamplePhysical(this, startTime, new Net7SpatialSamplingContext(), out var from) &&
                    incoming.TrySample(this, endTime, new Net7SpatialSamplingContext(), out var to))
                {
                    bool rejectCubic=update.ImpartedVelocity.LengthSquared() > Net7SpatialMath.PhysicsEpsilon ||
                     Vector3.DistanceSquared(from.Position, to.Position) < 1000f * 1000f ||
                     from.Speed * to.Speed < 0f ||
                     (from.Speed > Net7SpatialMath.PhysicsEpsilon) != (to.Speed > Net7SpatialMath.PhysicsEpsilon);
                    // 007169C7..00716A95: a late packet uses evolving samples
                    // only when the two boundary speeds agree on > epsilon.
                    // Both families use the receipt/lag interval, not packet time.
                    bool evolving = startTime >= converted &&
                        (from.Speed > Net7SpatialMath.PhysicsEpsilon) == (to.Speed > Net7SpatialMath.PhysicsEpsilon);
                    physical = !rejectCubic
                        ?new Net7CubicSpatialModel(previousRemote.Physical,incoming,startTime,endTime,from,to)
                        :evolving
                        ? new Net7TransitionSpatialModel(previousRemote.Physical, incoming, startTime, transitionMs)
                        : new Net7EndpointTransitionSpatialModel(previousRemote.Physical, incoming,
                            startTime, endTime, from, to);
                }
            }
            remote = previousRemote.WithPhysicalAndRetarget(physical, currentClientTime, update.UpdatePeriod);
        }
        else
        {
            remote = new Net7RemoteAdvancedSpatialModel(
                incoming,
                Net7RemoteLagState.CreateInitial(currentClientTime, update.UpdatePeriod));
        }

        obj.Spatial = BuildAdvancedSpatialState(
            Net7SpatialProviderKind.AdvancedRemote,
            update,
            converted,
            transitionMs,
            remote);
        obj.Spatial.ReceiptTime = currentClientTime;
    }

    private static Net7SpatialState BuildAdvancedSpatialState(
        Net7SpatialProviderKind kind,
        Net7AdvancedPositionUpdate update,
        uint converted,
        uint transitionMs,
        INet7SpatialModel model)
        => new()
        {
            Kind = kind,
            Position = update.Position,
            Orientation = update.Orientation,
            ClientTimestamp = converted,
            TransitionMilliseconds = transitionMs,
            MovementId = update.MovementId,
            CurrentSpeed = update.CurrentSpeed,
            Advanced = update,
            Model = model,
        };

    private static bool ShouldAcceptLocalAdvanced(
        Net7WorldObject obj,
        Net7AdvancedPositionUpdate update,
        float priorSpeed,
        float errorSquared,
        bool largeError)
    {
        if (largeError)
            return true;

        uint? pending = obj.PendingLocalMovementId;
        if (!pending.HasValue)
            return true;
        if (update.MovementId < pending.Value)
            return false;
        if (update.MovementId > pending.Value)
            return true;

        return !(MathF.Abs(priorSpeed) < ExistingSpeedStationaryEpsilon
            && !IsAdvancedMotionActive(update)
            && errorSquared < RedundantAdvancedErrorSquared);
    }

    private static bool IsAdvancedMotionActive(Net7AdvancedPositionUpdate update)
        => MathF.Abs(update.CurrentSpeed) > IncomingMotionEpsilon
            || MathF.Abs(update.SetSpeed) > IncomingMotionEpsilon
            || update.ImpartedVelocity.LengthSquared() > IncomingMotionEpsilon;

    private Net7WorldApplyResult ApplyPlanetPacket(ReadOnlySpan<byte> payload)
    {
        try
        {
            return ApplyPlanet(Net7WorldWire.DecodePlanetPosition(payload));
        }
        catch (InvalidDataException)
        {
            // Safety rule: 008ADDF0 leaves missing fields uninitialized and
            // 00734A40 reads them. Keep existing world state and continue.
            return new Net7WorldApplyResult();
        }
    }

    private Net7WorldApplyResult ApplyPlanet(Net7PlanetPositionUpdate update)
    {
        Net7WorldObject? obj = Objects.FindFirst(update.GameId);
        if (obj is null)
            return new Net7WorldApplyResult();
        uint converted = Time.ConvertWithoutDrift(update.ServerTimestamp);
        INet7SpatialModel model = new Net7PlanetSpatialModel(
            converted,
            update.SpatialVector,
            update.OrbitId,
            update.OrbitDistance,
            update.OrbitAngle,
            update.OrbitRate,
            update.RotationAngle,
            update.RotationRate,
            update.TiltAngle);
        obj.Spatial = new Net7SpatialState
        {
            Kind = Net7SpatialProviderKind.Planet,
            Position = update.SpatialVector,
            Orientation = Quaternion.Identity,
            ClientTimestamp = converted,
            Planet = update,
            Model = model,
        };
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyFixedPacket(ReadOnlySpan<byte> payload)
    {
        try
        {
            return ApplyFixed(Net7WorldWire.DecodeFixedPosition(payload));
        }
        catch (InvalidDataException)
        {
            // Safety rule: 008ABBF0 leaves unread fields uninitialized.
            // Preserve existing spatial state and continue. Complete non-finite
            // fields and unused packet tails remain accepted.
            return new Net7WorldApplyResult();
        }
    }

    private Net7WorldApplyResult ApplyFixed(Net7FixedPositionUpdate update)
    {
        Net7WorldObject? obj = Objects.FindFirst(update.GameId);
        if (obj is not null)
            ApplyFixedToObject(obj, update);
        return new Net7WorldApplyResult();
    }

    private static void ApplyFixedToObject(Net7WorldObject obj, Net7FixedPositionUpdate update)
    {
        Quaternion orientation = update.Orientation;
        obj.Spatial = new Net7SpatialState
        {
            Kind = Net7SpatialProviderKind.Fixed,
            Position = update.Position,
            Orientation = orientation,
            Model = new Net7FixedSpatialModel(update.Position, orientation),
        };
    }

    private Net7WorldApplyResult ApplyFormation(Net7FormationPositionUpdate update, uint currentClientTime)
    {
        Net7WorldObject? obj = Objects.FindFirst(update.GameId);
        if (obj is null)
            return new Net7WorldApplyResult();

        bool local = LocalShipObjectId.HasValue && LocalShipObjectId.Value == update.GameId;
        INet7SpatialModel parentRelative = new Net7ParentRelativeSpatialModel(update.LeaderId, update.Offset);
        INet7SpatialModel model;
        uint transitionMilliseconds = 0;
        Net7SpatialProviderKind kind;

        Net7SpatialState? previous = obj.Spatial;
        if (local)
        {
            kind = Net7SpatialProviderKind.AdvancedLocal;
            if (previous?.Kind == Net7SpatialProviderKind.AdvancedLocal && previous.Model is not null)
            {
                transitionMilliseconds = 5000u;
                model = new Net7TransitionSpatialModel(
                    previous.Model,
                    parentRelative,
                    currentClientTime,
                    transitionMilliseconds);
            }
            else
            {
                model = parentRelative;
            }
        }
        else
        {
            kind = Net7SpatialProviderKind.AdvancedRemote;
            if (obj.Spatial?.Model is Net7RemoteAdvancedSpatialModel remote)
            {
                transitionMilliseconds = 5000u;
                model = remote.WithFormation(new Net7TransitionSpatialModel(
                    remote.Physical,
                    parentRelative,
                    currentClientTime,
                    transitionMilliseconds),currentClientTime);
            }
            else
            {
                model = new Net7RemoteAdvancedSpatialModel(
                    parentRelative,
                    new Net7RemoteLagState(currentClientTime, 0, 0, currentClientTime));
            }
        }

        obj.Spatial = new Net7SpatialState
        {
            Kind = kind,
            Formation = update,
            Position = update.Offset,
            ClientTimestamp = currentClientTime,
            TransitionMilliseconds = transitionMilliseconds,
            Model = model,
        };
        bool requestTime = local && Time.ObserveQualifyingLocalFormationUpdate();
        return new Net7WorldApplyResult(RequestTimeDue: requestTime);
    }

    private Net7WorldApplyResult ApplyComponentPacket(ReadOnlySpan<byte> payload)
    {
        try
        {
            return ApplyComponent(Net7WorldWire.DecodeComponentPosition(payload));
        }
        catch (InvalidDataException)
        {
            // Safety rule: 008AE240 extends the uninitialized 008AC1B0 packet.
            // Stop before publishing a new model and continue without an issue.
            // Complete non-finite fields and unused tails remain accepted.
            return new Net7WorldApplyResult();
        }
    }

    private Net7WorldApplyResult ApplyComponent(Net7ComponentPositionUpdate update)
    {
        uint converted = Time.ConvertWithoutDrift(update.ServerTimestamp);
        Net7WorldObject? obj = Objects.FindFirst(update.GameId);
        if (obj is null || obj.ObjectType is not 0x04 and not 0x23)
            return new Net7WorldApplyResult();

        INet7SpatialModel incoming = new Net7ComponentSpatialModel(update, converted);
        Net7SpatialState? previous = obj.Spatial;
        INet7SpatialModel? previousModel = previous?.Kind == Net7SpatialProviderKind.Component
            ? previous.Model
            : null;
        bool repeated = previousModel is not null;
        INet7SpatialModel model = previousModel is not null
            ? new Net7TransitionSpatialModel(previousModel, incoming, converted, 250u)
            : incoming;
        obj.Spatial = new Net7SpatialState
        {
            Kind = Net7SpatialProviderKind.Component,
            Position = update.Position,
            Orientation = update.Orientation,
            Velocity = update.Velocity * 0.1f,
            ClientTimestamp = converted,
            TransitionMilliseconds = repeated ? 250u : 0u,
            Component = update,
            Model = model,
        };
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyRelationship(Net7RelationshipUpdate update)
    {
        Net7WorldObject? obj = Objects.FindFirst(update.ObjectId);
        if (obj is not null)
        {
            obj.RelationshipNotificationRevision++;
            obj.RelationshipRaw = update.ReactionRaw;
            obj.IsAttacking = update.IsAttacking;
        }
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyReactionBatch(Net7ReactionBatch update)
    {
        foreach (Net7ReactionEntry entry in update.Entries)
        {
            Net7WorldObject? obj = Objects.FindFirst(entry.ObjectId);
            if (obj is null)
                return new Net7WorldApplyResult();
            obj.RelationshipNotificationRevision++;
            obj.RelationshipRaw = entry.ReactionRaw;
        }
        if (update.TrailingRepeats != 0)
            ApplyRelationshipCycle(update.Entries[^2].ObjectId, update.Entries[^1].ObjectId, update.TrailingRepeats,
                obj => obj.RelationshipRaw = update.Entries[^1].ReactionRaw);
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyAttackerBatch(Net7AttackerBatch update)
    {
        foreach (uint objectId in update.ObjectIds)
        {
            Net7WorldObject? obj = Objects.FindFirst(objectId);
            if (obj is null)
                return new Net7WorldApplyResult();
            obj.RelationshipNotificationRevision++;
            obj.IsAttacking = update.IsAttacking;
        }
        if (update.TrailingRepeats != 0)
            ApplyRelationshipCycle(update.ObjectIds[^2], update.ObjectIds[^1], update.TrailingRepeats,
                obj => obj.IsAttacking = update.IsAttacking);
        return new Net7WorldApplyResult();
    }

    // Applies `count` further entries cycling first, second, first, ... with the
    // same stop-at-first-missing-object rule as the explicit entries.
    private void ApplyRelationshipCycle(uint first, uint second, uint count, Action<Net7WorldObject> assign)
    {
        Net7WorldObject? a = Objects.FindFirst(first);
        if (a is null)
            return;
        Net7WorldObject? b = count > 1 ? Objects.FindFirst(second) : null;
        ulong aCount = b is null ? 1 : (count + 1UL) / 2;
        ulong bCount = b is null ? 0 : count / 2UL;
        a.RelationshipNotificationRevision = unchecked(a.RelationshipNotificationRevision + aCount);
        assign(a);
        if (b is null)
            return;
        b.RelationshipNotificationRevision = unchecked(b.RelationshipNotificationRevision + bCount);
        assign(b);
    }

    private Net7WorldApplyResult ApplyNavigation(Net7NavigationUpdate update)
    {
        Net7WorldObject? obj = Objects.FindFirst(update.GameId);
        if (obj is not null)
            obj.Navigation = update;
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyNavigationDelete(uint gameId)
    {
        Net7WorldObject? obj = Objects.FindFirst(gameId);
        if (obj is not null)
            obj.Navigation = null;
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyAux(Net7AuxDataEnvelope envelope,ReadOnlySpan<byte> payload,uint clientTime)
    {
        // Object/local AuxData decode receives the same
        // client-owned 64-bit server/local correction origin used by current
        // Time AuxData properties. Net7ClientTimeState owns that correction in
        // the integrated client.
        var context = new Net7AuxDataCodecContext(Time.CorrectionMilliseconds);

        if (envelope.Opcode == 0x001C)
        {
            // 00736650 indexes the one-pointer table allocated by 007405E0.
            // Undefined in retail; project safety rule: do not dereference an
            // absent selector, preserve existing state and accept later packets.
            if (envelope.Target != 0)
                return new Net7WorldApplyResult();
            _auxCodec.ApplyIncomingDelta(PlayerVar0AuxData, envelope.Delta, context);
            return new Net7WorldApplyResult();
        }

        if (envelope.Target == 0)
        {
            _auxCodec.ApplyIncomingDelta(LocalPlayerAuxData, envelope.Delta, context);
            return new Net7WorldApplyResult();
        }

        Net7WorldObject? obj = Objects.FindFirst(envelope.Target);
        if (obj is null)
            return new Net7WorldApplyResult();
        if (obj.AuxData is null)
        {
            throw new InvalidDataException(
                $"Server sent AuxData for object 0x{obj.GameId:X8} type 0x{obj.ObjectType:X2}, but the current retail object-AuxData factory has no implemented schema for that type.");
        }

        int attemptCount = (obj.AuxDataAudit?.AttemptCount ?? 0) + 1;
        byte selector = envelope.Delta.Length == 0 ? (byte)0 : envelope.Delta[0];
        ShipLegoVisualIdentity[] legoBefore = CaptureShipLegoVisualIdentities(obj.AuxData);
        ulong attachmentsBefore = ShipAttachmentNotificationRevision(obj.AuxData);
        try
        {
            string? warning = _auxCodec.ApplyObjectDelta(obj.AuxData, envelope.Delta, context,
                envelope.TrailingBytes, out bool knownIgnoredTail);
            obj.ObserveWarpPresentation();
            ProjectObjectName(obj);
            ShipLegoVisualIdentity[] legoAfter = CaptureShipLegoVisualIdentities(obj.AuxData);
            //732790 calls InitializeShipVisualEffects after an Attachments
            //notification, including Data-only and aggregate state-two.
            if (attachmentsBefore != ShipAttachmentNotificationRevision(obj.AuxData))
                obj.FixedShipVisualRevision = checked(obj.FixedShipVisualRevision + 1);
            for (int ordinal = 0; ordinal < legoAfter.Length; ordinal++)
            {
                if (legoBefore[ordinal].Equals(legoAfter[ordinal]))
                    continue;
                obj.ShipLegoAttachmentRevisions[ordinal] = checked(
                    obj.ShipLegoAttachmentRevisions[ordinal] + 1);
                var before=legoBefore[ordinal];var after=legoAfter[ordinal];
                bool rebuild=before.BoneRevision!=after.BoneRevision||before.TypeRevision!=after.TypeRevision;
                obj.MutableLegoControllerMutations.Add(new((ulong)obj.MutableLegoControllerMutations.Count+1,
                    rebuild?Net7LegoControllerMutationKind.Rebuild:Net7LegoControllerMutationKind.ReplaceModel,
                    "aux",ordinal,obj.ShipLegoAttachmentRevisions[ordinal],after.BoneName,
                    rebuild?before.BoneName:null,after.Type,after.BaseAsset) {RenderStateActionCount=obj.RenderStateActions.Count});
            }
            obj.AuxDataAudit = RetainLargestAuxDataAttempt(
                obj.AuxDataAudit,
                attemptCount,
                envelope.Delta.Length,
                selector,
                accepted: warning is null,
                error: warning,
                compatiblePartial: knownIgnoredTail);
            // 007364C0 ignores the visitor's read failure. All bounded partial
            // reads keep the completed values; recognition of a captured tail
            // is diagnostic metadata, never a condition for accepting a prefix.
            return new Net7WorldApplyResult()
            {
                // Passive evidence for the recurring final Hulk cargo-slot truncation.
                // Preserve the original envelope (including any ignored tail), never
                // feed these bytes back into materialization or extend the declared view.
                AuxDataFailureReceipt = warning is not null && obj.ObjectType==0x19
                    ? System.Text.Json.JsonSerializer.Serialize(new {
                        Kind="HulkAuxData-partial-receipt",Opcode=envelope.Opcode,ClientTime=clientTime,
                        TimeCorrection=context.TimeBase,obj.GameId,obj.CreationOrdinal,obj.ObjectType,obj.BaseAsset,
                        DeclaredDeltaBytes=System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]),
                        AppliedViewBytes=envelope.Delta.Length,TrailingBytes=envelope.TrailingBytes.Length,
                        PayloadBytes=payload.Length,CapturedBytes=Math.Min(payload.Length,2048),Complete=payload.Length<=2048,
                        PayloadSha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)),
                        PayloadHex=Convert.ToHexString(payload[..Math.Min(payload.Length,2048)]) })
                    : null,
                AuxDataCompatibilityNote = knownIgnoredTail
                    ? obj.ObjectType==0x26
                        ? $"object 0x{obj.GameId:X8}: retail-compatible partial Harvestable update; completed resources retained within declared delta {envelope.Delta.Length}; 4 trailing TechLevel bytes ignored; existing TechLevel unchanged"
                        : obj.ObjectType==0x19
                        ? $"object 0x{obj.GameId:X8}: retail-compatible partial Hulk update; completed fields retained within declared delta {envelope.Delta.Length}; 2 trailing empty-slot bytes ignored; Cargo39 unchanged"
                        : $"object 0x{obj.GameId:X8}: retail-compatible partial Ship update; completed fields retained within declared delta {envelope.Delta.Length}; 8 trailing engine bytes ignored; existing engine fields unchanged"
                    : null
            };
        }
        catch (InvalidDataException ex)
        {
            obj.AuxDataAudit = RetainLargestAuxDataAttempt(
                obj.AuxDataAudit,
                attemptCount,
                envelope.Delta.Length,
                selector,
                accepted: false,
                error: ex.Message);
            throw;
        }
    }

    private static void ProjectObjectName(Net7WorldObject obj)
    {
        Net7AuxDataProperty? nameProperty = obj.AuxData?.Root.Children.FirstOrDefault(
            static property => property.Path.EndsWith(".Name", StringComparison.Ordinal));
        if (nameProperty?.Value is Net7AuxDataString name)
            obj.Name = name.Text;
    }

    private static Net7AuxDataMaterializationAudit RetainLargestAuxDataAttempt(
        Net7AuxDataMaterializationAudit? existing,
        int attemptCount,
        int deltaLength,
        byte selector,
        bool accepted,
        string? error,
        bool compatiblePartial = false)
    {
        if (existing is not null && existing.LargestDeltaLength > deltaLength)
            return existing with { AttemptCount = attemptCount };

        return new Net7AuxDataMaterializationAudit(
            attemptCount,
            deltaLength,
            selector,
            accepted,
            error) { LargestRetailCompatiblePartial = compatiblePartial };
    }

    private Net7WorldApplyResult ApplySetClientTime(ReadOnlySpan<byte> payload, uint currentClientTime)
    {
        Time.ApplySetClientTime(payload, currentClientTime);
        return new Net7WorldApplyResult();
    }

    private Net7WorldApplyResult ApplyHandoff(Net7ServerHandoff handoff)
    {
        // Transition clears current-sector materialized objects,
        // rebuilds the existing logical local roots, resets only the narrow time
        // state and retains the next exact SectorJoinClientData image.
        Objects.Clear();
        _stationAvatars.Clear();
        _stationAvatarEvents=[];StationAvatarStreamId=Guid.NewGuid();
        ServerParameters = null;
        ServerParametersSplineUpdateCount = 0;
        ServerParametersPresentationMode = null;
        EnvironmentPresentation = null;
        ZBand = null;
        BoundingBox = null;
        Spline = null;
        // Current retail constructs/loads SClient+0x1148 splines.wsd in
        // ClientBaseCtor and 0x0038 mutates that same database. The current
        // SplineDatabaseLoadFile reference inventory has no handoff-time reload,
        // so preserve the cumulative mutation stream across this transition.
        LocalPlayerAuxData.ResetMaterializedState();
        PlayerVar0AuxData.ResetMaterializedState();
        Time.ResetForServerHandoff();
        PendingHandoff = handoff;
        LastPresentationHandoff = handoff;
        PresentationHandoffVersion++;
        IsTransitioning = true;
        return new Net7WorldApplyResult(ServerHandoff: handoff);
    }

    /// <summary>
    /// Retail Start clears the loading/transition state after installing
    /// the server-supplied PlayerID and before Request_Time / StartAck are queued.
    /// Keep the retained handoff descriptor until the enclosing JOIN transaction
    /// completes, but stop suppressing normal world processing at Start.
    /// </summary>
    public void ObserveSectorStart()
        => IsTransitioning = false;

    public void CompleteHandoffTransition()
    {
        PendingHandoff = null;
        IsTransitioning = false;
    }

    public void ResetForCharacterSelection()
    {
        LastPresentationHandoff = null;
        PresentationHandoffVersion = 0;
        Objects.Clear();
        LocalPlayerAuxData.ResetMaterializedState();
        PlayerVar0AuxData.ResetMaterializedState();
        Time.ResetForCharacterSelection();
        LocalPlayerId = null;
        ClientAvatarId = null;
        ClientShipId = null;
        ServerParameters = null;
        ServerParametersSplineUpdateCount = 0;
        ServerParametersPresentationMode = null;
        EnvironmentPresentation = null;
        ZBand = null;
        BoundingBox = null;
        Spline = null;
        // Keep the cumulative current-client spline database semantics within
        // this world/session instance. The latest-packet compatibility accessor
        // resets, but no current SplineDatabaseLoadFile call is owned here.
        _stationAvatars.Clear();
        _stationAvatarEvents=[];StationAvatarStreamId=Guid.NewGuid();
        PendingHandoff = null;
        IsTransitioning = false;
    }
}
