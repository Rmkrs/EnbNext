using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using EnbNext.Net7.Protocol;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Reliability;
using EnbNext.Net7.Transport;
using EnbNext.Net7.Timing;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.Social;
using EnbNext.Net7.Diagnostics;
using EnbNext.Net7.LocalData;

namespace EnbNext.Net7;

/// <summary>
/// Direct current-Net7 session. No Net7Proxy, retail process, local fake listeners,
/// or Westwood client-leg crypto participate in this runtime path.
/// </summary>
public sealed partial class Net7Session : IAsyncDisposable
{
    // Exact raw SkillAbility.Validity value emitted by Tractor A.
    // The dossier deliberately does not assign it a friendlier enum meaning.
    private const uint TractorAbilityValidityRawValue = 0x00000100;


    private readonly Net7ServerOptions _options;
    private readonly object _worldStateSync = new();
    private readonly Net7ProcessCompatibilityState _processCompatibilityState;
    private readonly Action<string>? _trace;
    private readonly INet7RuntimeClock _clock;
    private readonly Net7WireCapture? _wireCapture;
    private readonly Net7TransportState _transportState = new();
    private readonly TaskCompletionSource<Exception> _fatalLoginSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _accountTransactionGate = new(1, 1);
    private readonly SemaphoreSlim _entryGate = new(1, 1);
    private readonly SemaphoreSlim _targetRequestGate = new(1, 1);
    private readonly object _waiterSync = new();
    private readonly object _handoffRetrySync = new();
    private readonly object _ownedOneShotSync = new();
    private readonly object _logoffSync = new();
    private readonly List<Net7HandoffRetryOperation> _handoffRetries = [];
    private readonly HashSet<Task> _ownedOneShotTasks = [];
    private readonly CancellationTokenSource _gameplayLifetimeCts = new();

    private MasterControlUdpChannel? _master;
    private RoutedSectorUdpChannel? _routed;
    private Net7TcpConnection? _account;
    private CancellationTokenSource? _masterPumpCts;
    private Task? _masterPumpTask;
    private CancellationTokenSource? _routedPumpCts;
    private Task? _routedPumpTask;
    private CancellationTokenSource? _watchdogCts;
    private Task? _watchdogTask;
    private Net7ReliabilityEngine? _reliability;
    private Net7KeepaliveController? _keepalive;
    private Net7PacketOptimisationTracker? _packetOptimisation;
    private Net7LoginResetController? _loginReset;
    private readonly Net7MovementFeedbackController _movement;
    private Net7CDataCatalog? _cdata;
    private CancellationTokenSource? _keepaliveLoopCts;
    private Task? _keepaliveLoopTask;
    private CancellationTokenSource? _accountPumpCts;
    private Task? _accountPumpTask;
    private readonly object _accountResponseSync = new();
    private TaskCompletionSource<Packet>? _accountSelectionWaiter;
    private TaskCompletionSource<Net7AvatarRoster>? _accountRosterWaiter;
    private TaskCompletionSource<Net7GlobalError>? _avatarManagementErrorWaiter;
    private bool _avatarManagementIsCreate;

    private TaskCompletionSource<ServerUdpFrame>? _masterLoginWaiter;
    private TaskCompletionSource<ServerUdpFrame>? _sectorPortWaiter;
    private TaskCompletionSource<bool>? _finalLoginCompletion;
    private TaskCompletionSource<bool>? _startAcknowledged;
    private TaskCompletionSource<bool>? _intermediateLoginComplete;
    // Proxy manager+B10 owns incoming delivery independently of outgoing
    // forwarding+380CD (0040B8F0 / 00412700 / 00413550).
    private volatile bool _gameplayClientTargetActive;
    private int _disposed;
    private int _serverDirectedBroadShutdown;
    private int _gameplayGeneration;
    private int _automaticHandoffContinuationActive;
    private int _presentationPassLoopActive;
    private Net7LogoffDestination? _pendingLogoffDestination;
    private bool _gateRequestWithoutCamera;

    public Net7Session(
        Net7ServerOptions options,
        Action<string>? trace = null,
        INet7RuntimeClock? clock = null,
        Net7WireCapture? wireCapture = null,
        Net7ProcessCompatibilityState? processCompatibilityState = null,
        bool useCameraController = false)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processCompatibilityState = processCompatibilityState ?? new Net7ProcessCompatibilityState();
        _trace = trace;
        _dockRequestThroughController = _gateRequestThroughController = useCameraController;
        _clock = clock ?? new SystemNet7RuntimeClock();
        _wireCapture = wireCapture;
        Presentation = new Net7PresentationCatalogs();
        World = new Net7WorldState(
            presentation: Presentation,
            processCompatibilityState: _processCompatibilityState);
        PlayerSemantics = new Net7PlayerSemanticDomain(World);
        World.IsAvatarViewActive = () => PlayerSemantics.IsAvatarViewActive;
        Gameplay = new Net7GameplayState(
            Presentation,
            gameId => World.Objects.FindFirst(gameId)?.Name,
            () => World.LocalPlayerId,
            gameId => World.Objects.FindFirst(gameId),
            World.Time.ConvertWithoutDrift,
            gameId => World.TrySampleObject(gameId, _clock.GetMilliseconds(), out var sample) ? sample.Position : null,
            identity => World.Objects.Snapshot().FirstOrDefault(o => o.ChatIdentity.Equals(identity, StringComparison.Ordinal))?.GameId,
            () => PlayerSemantics.IsAvatarViewActive,
            () => World.LocalPlayerAuxData.Properties.First(p=>p.Path=="LocalPlayerAuxData.GroupInfo").State!=0 && PlayerSemantics.GetGroupState().IsGroupLeader,
            id=>World.AvatarCacheLifetimes.TryGetValue(id,out ulong lifetime)?lifetime:null,
            () => _clock.GetMilliseconds());
        ItemTemplates = new Net7ItemTemplateCatalog();
        Gameplay.Social.SpeakerPresented += (identity,type) =>
        {
            System.Numerics.Vector3? color=type<_chatColors.Length?_chatColors[type]:null;
            if (PlayerSemantics.IsAvatarViewActive) World.PublishStationSpeaker(identity,type,color); else World.PublishSpaceSpeaker(identity,type,color);
        };
        _movement = new Net7MovementFeedbackController(_clock);
    }

    /// <summary>The unescaped username supplied by the EnbNext user.</summary>
    public string? Username { get; private set; }

    /// <summary>
    /// Owned EATicket C-string projection copied from the exact
    /// Ticket result. This is the value submitted to TCP 0x2002.
    /// </summary>
    public string? EaTicket { get; private set; }

    /// <summary>
    /// Username token derived from the EATicket exactly as current Net7Proxy does
    /// for the later TCP 0x2004 avatar-login request.
    /// </summary>
    public string? AvatarLoginUsername { get; private set; }

    /// <summary>
    /// Continuously sampled process-local client-time clock used by the current
    /// Net7 session. Reading this value does not mutate or republish world state.
    /// </summary>
    public uint CurrentClientTime => _clock.GetMilliseconds();

    private readonly Net7ConnectionMeter _connection = new();
    /// <summary>Next: the connection as measured from the session's own traffic (the Modern UI connection indicator).</summary>
    public Net7ConnectionSample Connection => _connection.Sample();

    /// <summary>Next: the client now waits for a server answer. When it is still late after
    /// <see cref="Net7ConnectionMeter.ProbeAfter"/>, one retail Request_Time (0x0044) tells whether the server still
    /// answers at all; the reading shows in <see cref="Connection"/>.</summary>
    private void AwaitAnswer(Net7AwaitedAnswer answer)
    {
        _connection.Await(answer);
        StartOwnedOneShot(async () =>
        {
            try
            {
                await _clock.DelayAsync(TimeSpan.FromMilliseconds(Net7ConnectionMeter.ProbeAfter), _gameplayLifetimeCts.Token).ConfigureAwait(false);
                if (_routed is not { } routed || !_connection.ShouldProbe()) return;
                await routed.SendToActiveBestEffortAsync(0x0044, Net7SectorEntryWire.BuildRequestTime(_clock.GetMilliseconds()),
                    _gameplayLifetimeCts.Token).ConfigureAwait(false);
                _trace?.Invoke($"connection probe: Request_Time sent while the server's {answer} answer is late");
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
    }

    public Net7AvatarRoster? Roster { get; private set; }
    public Net7CharacterSelectionResult? CharacterSelection { get; private set; }
    public Net7LoginState LoginState { get; private set; } = Net7LoginState.Created;
    public uint FixedSessionId => _transportState.FixedSessionId;
    public int? ActiveGameplaySectorPort => _transportState.ConfirmedGameplayPort;
    public int ServerFrequencyMultiplier { get; private set; } = 20;
    public Net7RouteResult? Route { get; private set; }
    public Net7GalaxyMapData? GalaxyMap { get; private set; }
    private Net7GalaxyMapData? _localGalaxyMap;
    public Net7GalaxyMapLocation? GalaxyMapLocation { get; private set; }
    public bool GalaxyMapShowAll { get; set; }
    public uint? PlayerId { get; private set; }
    public Net7ReliabilitySnapshot? ReliabilityState => _reliability?.Snapshot();
    public Net7PresentationCatalogs Presentation { get; }
    public Net7WorldState World { get; }
    public Net7GameplayState Gameplay { get; }
    public Net7PlayerSemanticDomain PlayerSemantics { get; }
    public Net7ItemTemplateCatalog ItemTemplates { get; }
    public Net7LogoffDestination? PendingLogoffDestination => _pendingLogoffDestination;
    public Net7LogoffDestination? LastConfirmedLogoffDestination { get; private set; }
    public int? LastConfirmedLogoffTransitionState { get; private set; }
    public event Action<Net7LogoffDestination?>? LogoffConfirmed;

    /// <summary>
    /// Dedicated legacy 0x1004 feedback-speed state formerly mirrored
    /// through the retail bridge. It is intentionally distinct from any UI or
    /// displayed speed property.
    /// </summary>
    public int LegacyPositionFeedbackSpeed
    {
        get => _movement.LegacyPositionFeedbackSpeed;
        set => _movement.LegacyPositionFeedbackSpeed = value;
    }

    public void LoadCData(string path) => _cdata = Net7CDataCatalog.Load(path);
    public void SetCDataCatalog(Net7CDataCatalog? catalog) => _cdata = catalog;
    public void LoadBaseAssetCatalog(string path) => Presentation.LoadBaseAssets(path);
    public void LoadEffectCatalog(string path) => Presentation.LoadEffects(path);

    public event Action<Packet>? TcpPacketSent;
    public event Action<Packet>? TcpPacketReceived;
    public event Action<ServerUdpFrame, string>? UdpFrameSent;
    public event Action<ServerUdpFrame, string>? UdpFrameReceived;
    public event Action<Net7LoginState>? LoginStateChanged;
    public event Action<uint>? WorldStateChanged;
    public event Action<uint>? PresentationStateChanged;
    // Captures packet-time effect bindings without exposing an incomplete bundle.
    public event Action<uint>? PresentationStateDeferred;
    public event Action<Packet>? GameplayPacketReceived;
    public event Action<Net7AvatarRoster>? RosterUpdated;
    public event Action<Net7GlobalError>? GlobalErrorReceived;

    public async Task<Net7AvatarRoster> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_master is not null || _routed is not null || _account is not null || LoginState != Net7LoginState.Created)
            throw new InvalidOperationException("This Net7 session has already been started.");
        if (string.IsNullOrEmpty(username))
            throw new ArgumentException("Username is required.", nameof(username));
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password is required by the ordinary current AuthLogin path.", nameof(password));

        bool bootstrapComplete = false;
        try
        {
            SetLoginState(Net7LoginState.Bootstrapping);

            // Both UDP owners use one configured server IPv4, and TCP
            // 3807 takes that same address from the routed owner. Resolve a
            // product-level hostname once so DNS rotation cannot split a session.
            IPAddress serverAddress = await Net7AddressResolver.ResolveIpv4Async(
                _options.Host, cancellationToken).ConfigureAwait(false);
            _trace?.Invoke($"server IPv4 pinned -> {serverAddress} (host '{_options.Host}')");
            _trace?.Invoke($"server bootstrap -> {serverAddress}:{_options.MasterControlUdpPort}/udp");
            _master = await MasterControlUdpChannel.ConnectAsync(
                serverAddress, _options.MasterControlUdpPort, _transportState, _trace, cancellationToken, _wireCapture).ConfigureAwait(false);
            HookMasterEvents(_master);
            await _master.BootstrapAsync(
                _options.BootstrapAttempts, _options.BootstrapAttemptTimeout, cancellationToken).ConfigureAwait(false);
            bootstrapComplete = true;
            ServerFrequencyMultiplier = _master.ServerFrequencyMultiplier;

            _routed = await RoutedSectorUdpChannel.CreateAsync(
                serverAddress, _transportState, _trace, cancellationToken, _wireCapture).ConfigureAwait(false);
            HookRoutedEvents(_routed);
            InitializeReliabilityAndKeepalive();
            StartMasterPump();
            StartRoutedPump();

            return await LoginAccountAsync(serverAddress, username, password, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Bootstrap is a hard startup gate. If it never completed,
            // the fresh master/control owner must not survive the failed startup
            // as a published live socket. The stock top-level path destroys that
            // first UDPClient during failure unwind; integrated EnbNext mirrors the
            // ownership result directly rather than waiting for an external client
            // DisposeAsync call.
            if (!bootstrapComplete)
                await DisposeMasterControlTransportAsync().ConfigureAwait(false);

            // A failed account/login establishment transaction
            // must not leave TCP 3807 published after framing/transport failure.
            // Fresh login owns this connection, so retire it deterministically
            // before exposing the faulted session state.
            await DisposeAccountConnectionAsync().ConfigureAwait(false);
            SetLoginState(Net7LoginState.Faulted);
            throw;
        }
    }

    // Retail 00766BD0 case 2 restarts authentication before publishing a new roster.
    public async Task<Net7AvatarRoster> RefreshCharacterRosterAsync(string username, string password,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _accountTransactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (LoginState != Net7LoginState.CharacterSelection || FixedSessionId != 0 ||
                LastConfirmedLogoffDestination != Net7LogoffDestination.CharacterSelection || _routed is null)
                throw new InvalidOperationException("Character roster refresh requires confirmed character-selection logoff.");
            try
            {
                await DisposeAccountConnectionAsync().ConfigureAwait(false);
                Roster = null;
                CharacterSelection = null;
                return await LoginAccountAsync(_routed.ServerAddress, username, password, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await DisposeAccountConnectionAsync().ConfigureAwait(false);
                SetLoginState(Net7LoginState.Faulted);
                throw;
            }
        }
        finally { _accountTransactionGate.Release(); }
    }

    private async Task<Net7AvatarRoster> LoginAccountAsync(IPAddress serverAddress, string username,
        string password, CancellationToken cancellationToken)
    {
        SetLoginState(Net7LoginState.AccountConnecting);
        _trace?.Invoke($"account TCP -> {serverAddress}:{_options.AccountTcpPort}");
        _account = await Net7TcpConnection.ConnectAsync(serverAddress, _options.AccountTcpPort, cancellationToken, _wireCapture).ConfigureAwait(false);
        _account.PacketSent += p => TcpPacketSent?.Invoke(p);
        _account.PacketReceived += p => TcpPacketReceived?.Invoke(p);

        // Exact establishment order. No ACK wait is inserted between
        // these controls and the following account credential request.
        byte[] startControl = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(startControl, FixedSessionId);
        await SendTcpAsync(0x3002, startControl, cancellationToken).ConfigureAwait(false);
        await SendTcpAsync(0x2010, new byte[4], cancellationToken).ConfigureAwait(false);

        SetLoginState(Net7LoginState.Authenticating);
        _trace?.Invoke("account -> TCP 0x2000 AuthLogin-escaped username/password tokens");
        await SendTcpAsync(0x2000, Net7AccountWire.BuildAccountLoginPayload(username, password), cancellationToken).ConfigureAwait(false);
        Packet accountReply = await ReceiveAccountResponseAsync(
            [0x2001], "account-valid TCP 0x2001", cancellationToken).ConfigureAwait(false);
        ThrowIfFatalLoginSignaled();
        _trace?.Invoke($"account <- TCP 0x{accountReply.Header.Opcode:X4} len={accountReply.Payload.Length}");

        Net7AuthLoginResponse auth = Net7AuthLoginResponse.Decode(accountReply.Payload.Span);
        if (!auth.IsValid)
            throw new Net7AuthenticationException(auth);

        string eaTicket = Net7AccountWire.OwnEaTicketFromAuthLoginTicket(auth.Ticket);
        Username = username;
        EaTicket = eaTicket;
        _trace?.Invoke($"account <- AuthLogin Valid=TRUE, owned EATicket bytes={Net7AccountWire.BuildAvatarListRequest(eaTicket).Length}");

        ThrowIfFatalLoginSignaled();
        SetLoginState(Net7LoginState.LoadingRoster);
        _trace?.Invoke("avatar list -> TCP 0x2002 full owned EATicket");
        await SendTcpAsync(0x2002, Net7AccountWire.BuildAvatarListRequest(eaTicket), cancellationToken).ConfigureAwait(false);
        Net7AvatarRoster? roster;
        do
        {
            Packet avatarReply = await ReceiveAccountResponseAsync(
                [0x0070], "avatar list TCP 0x0070", cancellationToken).ConfigureAwait(false);
            ThrowIfFatalLoginSignaled();
            _trace?.Invoke($"avatar list <- TCP 0x0070 len={avatarReply.Payload.Length}");
            roster = TryDecodeRoster(avatarReply);
        }
        while (roster is null);
        // A ticket without a token keeps the previous username (00401170/00414240).
        // Before any username exists the proxy buffer content is not proven.
        string avatarLoginUsername = Net7AccountWire.ExtractAvatarLoginUsernameFromEaTicket(eaTicket) ?? AvatarLoginUsername ??
            throw new InvalidDataException("EA ticket contains no username token among '-' delimiters and no earlier username exists.");

        // Updates durable ticket-derived identity only after the
        // avatar-list transaction has succeeded.
        Roster = roster;
        AvatarLoginUsername = avatarLoginUsername;
        _trace?.Invoke($"decoded roster: {roster.Slots.Count} slots, {roster.Slots.Count(s => !s.IsEmpty)} occupied, {roster.RoutingRecords.Count} routing records");
        _trace?.Invoke("account <- committed ticket-derived avatar-login username after roster success");
        StartAccountResponsePump();
        SetLoginState(Net7LoginState.CharacterSelection);
        return roster;
    }

    /// <summary>
    /// Terminal transaction. It sends exactly one TCP 0x2004, accepts only
    /// role-10 0x006F/0x2009, assigns its identity, then checks success.
    /// No Master/sector packet is emitted here.
    /// </summary>
    public async Task<Net7CharacterSelectionResult> SelectCharacterAsync(
        int characterSlot,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _accountTransactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TaskCompletionSource<Packet>? selectionWaiter = null;
        try
        {
            if (_account is null || Roster is null)
                throw new InvalidOperationException("Account login and character roster must complete before avatar selection.");
            if (LoginState != Net7LoginState.CharacterSelection)
                throw new InvalidOperationException($"Cannot select a character from Net7 login state {LoginState}.");
            string avatarLoginUsername = AvatarLoginUsername
                ?? throw new InvalidOperationException("Ticket-derived avatar-login username is not available.");

            // This requires fresh route resolution before request emission.
            Net7AvatarSlot selected = Roster.GetSelectableSlot(characterSlot);
            Net7GalaxyRoutingRecord route = Roster.ResolveUsableRouteForSlot(characterSlot);
            _reliability?.ResetGateTransitionOwnership();
            CharacterSelection = null;
            SetLoginState(Net7LoginState.SelectingCharacter);

            bool requestSent = false;
            try
            {
                selectionWaiter = InstallAccountSelectionWaiter();
                _trace?.Invoke($"avatar selection -> TCP 0x2004 slot={characterSlot} identity='{avatarLoginUsername}'");
                await SendTcpAsync(
                    0x2004,
                    Net7AccountWire.BuildAvatarLoginPayload(characterSlot, avatarLoginUsername),
                    cancellationToken).ConfigureAwait(false);
                requestSent = true;

                Packet packet = await WaitForAccountSelectionResponseAsync(
                    selectionWaiter, "avatar-selection TCP 0x006F/0x2009", cancellationToken).ConfigureAwait(false);
                ThrowIfFatalLoginSignaled();
                Net7AvatarSelectionResponse response = Net7AvatarSelectionResponse.Decode(packet);
                ThrowIfFatalLoginSignaled();

                // Proxy 00414A60 stores the identity before testing its result;
                // 00401520 reports failure without undoing that assignment.
                CommitValidatedFixedSessionId(response.FixedSessionId, $"TCP 0x{response.ResponseOpcode:X4} avatar-selection response");
                response.ValidateAccepted();
                Net7SectorJoinClientData join = Net7SectorJoinClientData.CreateSuccessful(
                    response.FixedSessionId, selected.Meta.Sector, selected.Meta.AdminLevel);
                var result = new Net7CharacterSelectionResult(
                    characterSlot,
                    selected,
                    route,
                    response.FixedSessionId,
                    response.ResponseOpcode,
                    join);
                CharacterSelection = result;
                _trace?.Invoke($"avatar selection <- TCP 0x{response.ResponseOpcode:X4} fixed/session=0x{response.FixedSessionId:X8}; character selection committed");
                SetLoginState(Net7LoginState.CharacterSelected);
                return result;
            }
            catch (Net7AvatarSelectionRejectedException)
            {
                // Keep the identity written by the response, including on failure.
                // The selection screen remains available for another attempt.
                SetLoginState(Net7LoginState.CharacterSelection);
                throw;
            }
            catch
            {
                // Timeout/cancellation/protocol/global-error after emission leaves
                // no wire transaction token with which to distinguish a late reply.
                // Fail the session closed so stale responses cannot satisfy a retry.
                if (requestSent)
                    SetLoginState(Net7LoginState.Faulted);
                else
                    SetLoginState(Net7LoginState.CharacterSelection);
                throw;
            }
        }
        finally
        {
            if (selectionWaiter is not null)
                ClearAccountSelectionWaiter(selectionWaiter);
            _accountTransactionGate.Release();
        }
    }

    /// <summary>
    /// Direct account/avatar creation. Exactly one TCP 0x200B is emitted
    /// for an explicit call. Completion is the transaction-owned refreshed 0x0070
    /// roster; a known fixed-message rejection may arrive via outer UDP 0x200E
    /// or account TCP 0x0075 while this create is pending.
    /// </summary>
    public async Task<Net7AvatarCreateResult> CreateCharacterAsync(
        Net7AvatarCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await _accountTransactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TaskCompletionSource<Net7AvatarRoster>? rosterWaiter = null;
        TaskCompletionSource<Net7GlobalError>? errorWaiter = null;
        bool requestSent = false;
        try
        {
            RequireAvatarManagementReady();
            string currentUsername = AvatarLoginUsername
                ?? throw new InvalidOperationException("Ticket-derived current account username is not available.");
            byte[] payload = Net7AccountWire.BuildAvatarCreatePayload(request, currentUsername);
            (rosterWaiter, errorWaiter) = InstallAvatarManagementWaiters(isCreate: true);

            _trace?.Invoke(
                $"avatar create -> TCP 0x200B shard={request.TargetShardId} slot={request.TargetSlot} control={request.Control} body=0x{payload.Length:X}");
            await SendTcpAsync(0x200B, payload, cancellationToken).ConfigureAwait(false);
            requestSent = true;

            Net7AvatarRoster roster = await WaitForAvatarManagementRosterAsync(
                rosterWaiter, errorWaiter, "avatar-create TCP 0x0070", cancellationToken).ConfigureAwait(false);
            _trace?.Invoke($"avatar create <- authoritative TCP 0x0070 roster for slot {request.TargetSlot}");
            return new Net7AvatarCreateResult(request.TargetSlot, roster);
        }
        catch (Net7GlobalErrorException)
        {
            // Recoverable retail name errors release the create page for retry;
            // other delivered errors retain the roster barrier.
            throw;
        }
        catch
        {
            if (requestSent)
                SetLoginState(Net7LoginState.Faulted);
            throw;
        }
        finally
        {
            if (rosterWaiter is not null && errorWaiter is not null)
                ClearAvatarManagementWaiters(rosterWaiter, errorWaiter);
            _accountTransactionGate.Release();
        }
    }

    private static Net7AvatarSlot EmptyAvatarSlot()
        => new(
            new Net7AvatarSlotMeta(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, string.Empty),
            Net7AvatarRosterCodec.DecodeAvatarData(new byte[Net7AvatarRosterCodec.AvatarDataBaseSize]).Avatar);

    /// <summary>
    /// Direct account/avatar deletion. No optimistic roster mutation is
    /// performed for a server avatar. Empty slots are suppressed locally; an occupied
    /// negative-shard slot is cleared locally without a request, as retail 007683F0.
    /// </summary>
    public async Task<Net7AvatarDeleteResult> DeleteCharacterAsync(
        int characterSlot,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _accountTransactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TaskCompletionSource<Net7AvatarRoster>? rosterWaiter = null;
        TaskCompletionSource<Net7GlobalError>? errorWaiter = null;
        bool requestSent = false;
        try
        {
            RequireAvatarManagementReady();
            Net7AvatarRoster currentRoster = Roster!;
            if (characterSlot is < 0 or >= Net7AvatarRosterCodec.SlotCount)
                throw new ArgumentOutOfRangeException(nameof(characterSlot), "Character deletion uses stable roster slot identity 0..4.");

            Net7AvatarSlot selected = currentRoster.Slots[characterSlot];
            if (selected.IsEmpty)
            {
                _trace?.Invoke($"avatar delete slot {characterSlot} suppressed: slot is empty");
                return new Net7AvatarDeleteResult(characterSlot, false, currentRoster);
            }

            if (unchecked((int)selected.Meta.ShardId) < 0)
            {
                // Retail 007683F0: a negative-shard slot sends no 0x0071; the slot is
                // reset to default avatar/sector data locally (0078DAF0 rewrites the
                // local slot file) and the roster-refreshed flag is raised.
                var slots = currentRoster.Slots.ToArray();
                slots[characterSlot] = EmptyAvatarSlot();
                Net7AvatarRoster cleared = currentRoster with { Slots = slots };
                lock (_accountResponseSync)
                    Roster = cleared;
                _trace?.Invoke($"avatar delete slot {characterSlot}: negative shard cleared locally without a server request");
                RosterUpdated?.Invoke(cleared);
                return new Net7AvatarDeleteResult(characterSlot, false, cleared);
            }

            string currentUsername = AvatarLoginUsername
                ?? throw new InvalidOperationException("Ticket-derived current account username is not available.");
            byte[] payload = Net7AccountWire.BuildAvatarDeletePayload(characterSlot, currentUsername);
            (rosterWaiter, errorWaiter) = InstallAvatarManagementWaiters(isCreate: false);

            _trace?.Invoke($"avatar delete -> TCP 0x200D slot={characterSlot} identity='{currentUsername}'");
            await SendTcpAsync(0x200D, payload, cancellationToken).ConfigureAwait(false);
            requestSent = true;

            Net7AvatarRoster roster = await WaitForAvatarManagementRosterAsync(
                rosterWaiter, errorWaiter, "avatar-delete TCP 0x0070", cancellationToken).ConfigureAwait(false);
            _trace?.Invoke($"avatar delete <- authoritative TCP 0x0070 roster for slot {characterSlot}");
            return new Net7AvatarDeleteResult(characterSlot, true, roster);
        }
        catch (Net7GlobalErrorException)
        {
            throw;
        }
        catch
        {
            if (requestSent)
                SetLoginState(Net7LoginState.Faulted);
            throw;
        }
        finally
        {
            if (rosterWaiter is not null && errorWaiter is not null)
                ClearAvatarManagementWaiters(rosterWaiter, errorWaiter);
            _accountTransactionGate.Release();
        }
    }

    // Every current steering producer (005806E0 commands 0x10..0x15, 006B58A0
    // mouse flight) asks 0057D6C0 for ShipAuxData.LockOrient and emits nothing
    // while it is set; 005821C0 disables the steering buttons from the same
    // flag. IsIncapacitated is not a steering gate (00581670 sends its own
    // release on onset, see ReconcileIncapacitatedFlightAsync).
    public Task<bool> SendHorizontalSteeringAsync(float intensity, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (World.LocalShipLockOrientActive) return Task.FromResult(false);
        return SendSteeringAsync(0x0012, intensity, cancellationToken);
    }

    public Task<bool> SendVerticalSteeringAsync(float intensity, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (World.LocalShipLockOrientActive) return Task.FromResult(false);
        return SendSteeringAsync(0x0013, intensity, cancellationToken);
    }

    private async Task<bool> SendSteeringAsync(ushort opcode, float intensity, CancellationToken cancellationToken)
    {
        // 005806E0/00583510/006B58A0/00581670 steering and the 0x0014/0x0015/
        // 0x0028/0x0057/0x0058/0x0065/0x008D..0x008F producers all pass
        // SClient+112C (0037's assigned object), not Start's +1138.
        uint gameId = RequireLocalAvatarObjectId();
        if (!(opcode == 0x0012 ? _movement.TryAcceptHorizontal(intensity) : _movement.TryAcceptVertical(intensity)))
            return false;
        _keepalive?.MarkActiveForTwoSeconds();
        return await TrySendGameplayAsync(opcode, Net7GameplayWire.BuildSteering(gameId, intensity), cancellationToken).ConfigureAwait(false);
    }

    // Move producers gate themselves (005806E0/006B58A0 on LockSpeed, the
    // thrust buttons through 005821C0, 0059DB10 autofollow not at all); the
    // queue has no incapacitation gate.
    public async Task<bool> SendMoveAsync(byte moveType, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (moveType is > 6 and not 12 and not 13)
            throw new ArgumentOutOfRangeException(nameof(moveType), "Current MOVE-03 producers use move types 0..6, 12, or 13.");
        bool sent = await TrySendGameplayAsync(0x0014, Net7GameplayWire.BuildMove(RequireLocalAvatarObjectId(), moveType), cancellationToken).ConfigureAwait(false);
        await TrySendPositionSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return sent;
    }

    public Task<bool> SendFireAsync(byte slot, CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x0015, Net7GameplayWire.BuildFire(RequireLocalAvatarObjectId(), slot), cancellationToken);

    public async Task<bool> RequestTargetAsync(uint targetId, CancellationToken cancellationToken = default)
    {
        // Preserve retail's separate local target/input state explicitly.
        // This does not mutate authoritative World.CurrentTargetId; only later
        // server-authored ShipAuxData.TargetGameID materialization can do that.
        uint? previousTargetId;
        lock (_worldStateSync)
        {
            previousTargetId = Gameplay.HasLocalTargetSelection
                ? Gameplay.LocalSelectedTargetId
                : World.CurrentTargetId;
            Gameplay.SetLocalSelectedTarget(targetId);
            // The retail scanner changes immediately on local input. Network
            // reconciliation and the 0x0017 request happen afterwards.
            NotifyPresentationStateChanged();
        }
        await _targetRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // One local selection owns the complete retail detach/select/attach
            // transaction. The routed channel serializes individual datagrams,
            // which is insufficient when rapid selections overlap: a later
            // request can otherwise detach A before the earlier request attaches
            // A again, leaving stale verb registration after B was selected.
            await ReconcileHulkAsync(cancellationToken).ConfigureAwait(false);
            // Current retail SetSelectedTargetObject detaches the old target with
            // VerbRequest action 2 and attaches the new target with action 1. The
            // outer attach helper sends action 1 after registering its callback;
            // stopping at the inner registration helper misses that packet.
            if (previousTargetId is uint previous && previous != 0 && previous != targetId)
            {
                _ = await TrySendGameplayAsync(
                    0x005A,
                    Net7GameplayWire.BuildVerbRequest(RequireLocalShipObjectId(), previous, 2),
                    cancellationToken).ConfigureAwait(false);
            }
            bool selected = await TrySendGameplayAsync(
                0x0017,
                Net7GameplayWire.BuildTargetRequest(RequireLocalShipObjectId(), targetId),
                cancellationToken).ConfigureAwait(false);
            if (!selected || targetId == 0)
                return selected;

            // The public result continues to mean that the 0x0017 selection
            // itself was accepted for transport. Verb subscription is the
            // following best-effort companion; its failure must not make the
            // UI roll back a target that was already selected on the wire.
            try
            {
                _ = await RequestVerbAsync(targetId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return true;
            }
            catch (InvalidOperationException exception)
            {
                // A sector handoff can clear the old local-ship identity after
                // 0x0017 has already been emitted but before the optional 0x005A
                // subscription is built. Selection succeeded; suppress only
                // this best-effort companion instead of rolling the pane back.
                _trace?.Invoke($"target verb attach skipped after selection send: {exception.Message}");
                return true;
            }
            return true;
        }
        finally
        {
            _targetRequestGate.Release();
        }
    }

    public Task<bool> RequestTargetsTargetAsync(uint groupMemberId, CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x0018, Net7GameplayWire.BuildTargetRequest(RequireLocalShipObjectId(), groupMemberId), cancellationToken);

    public Task<bool> FireAllWeaponsAsync(CancellationToken cancellationToken = default)
        // 0059DB10: actor and target both SClient+112C; optional value zero.
        => Gameplay.PresentationMode is 0 or 1 or 5
            ? SendActionCoreAsync(Net7PlayerAction.FireAllWeapons, RequireLocalAvatarObjectId(), 0, cancellationToken)
            : Task.FromResult(false);

    public async Task<bool> ToggleAutoLevelAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // 007436A0 hotkey 1 has no presentation-mode test (unlike 0059DB10's
        // fire-all/autofollow): SClient+112C and Move type 13. The server owns
        // ShipAuxData.AutoLevel; no optimistic toggle or local preference.
        bool sent = await TrySendGameplayAsync(0x0014,
            Net7GameplayWire.BuildMove(RequireLocalAvatarObjectId(), 13), cancellationToken).ConfigureAwait(false);
        await TrySendPositionSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return sent;
    }

    public async Task<bool> ToggleAutofollowAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Gameplay.PresentationMode is not (0 or 1 or 5)) return false;
        // 0059DB10 command 0: assigned SClient+112C identity, Move type 12.
        // No target/range or CanThrust gate: this must also disengage following.
        bool sent = await TrySendGameplayAsync(0x0014,
            Net7GameplayWire.BuildMove(RequireLocalAvatarObjectId(), 12), cancellationToken).ConfigureAwait(false);
        await TrySendPositionSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return sent;
    }

    public Task<bool> SendActionAsync(
        Net7PlayerAction action,
        uint targetId,
        uint optionalVar = 0,
        CancellationToken cancellationToken = default)
    {
        if (action is Net7PlayerAction.Dock or Net7PlayerAction.Landing
            or Net7PlayerAction.GateRequest or Net7PlayerAction.Gate
            or Net7PlayerAction.DockRequest or Net7PlayerAction.LandRequest)
        {
            throw new InvalidOperationException(
                "Dock/Land/Gate actions require the I11 typed request/commit APIs so target provenance cannot be bypassed.");
        }
        if (action is Net7PlayerAction.GroupInvite or Net7PlayerAction.GroupAccept or Net7PlayerAction.GroupRefuse
            or Net7PlayerAction.GroupDisband or Net7PlayerAction.GroupRemoveOrLeaveSelf
            or Net7PlayerAction.GroupKick or Net7PlayerAction.GroupFinder)
        {
            throw new InvalidOperationException(
                "Group actions require the SOCIAL-05 typed APIs so target/self and leadership provenance cannot be bypassed.");
        }
        if (action is Net7PlayerAction.TradeRequest or Net7PlayerAction.TradeConfirm
            or Net7PlayerAction.TradeCancel or Net7PlayerAction.TradeSync or Net7PlayerAction.TradeCredits)
        {
            throw new InvalidOperationException(
                "Player-trade actions require the ECON-02 typed APIs so active-partner and credit provenance cannot be bypassed.");
        }
        return SendActionCoreAsync(action, targetId, optionalVar, cancellationToken);
    }

    public Task<bool> RequestTradeAsync(uint partnerId, CancellationToken cancellationToken = default)
    {
        // 00658430 reports self-trade before this point; 0063DFE0 lists only
        // other room players.
        return SendActionCoreAsync(Net7PlayerAction.TradeRequest, partnerId, 0, cancellationToken);
    }

    public Task<bool> ConfirmTradeAsync(CancellationToken cancellationToken = default)
        => SendActionCoreAsync(Net7PlayerAction.TradeConfirm, Gameplay.TradeDomain.RequireActivePartnerId(), 0, cancellationToken);

    public Task<bool> CancelTradeAsync(CancellationToken cancellationToken = default)
        => SendActionCoreAsync(Net7PlayerAction.TradeCancel, Gameplay.TradeDomain.RequireActivePartnerId(), 0, cancellationToken);

    public Task<bool> SynchronizeTradeAsync(CancellationToken cancellationToken = default)
        => SendActionCoreAsync(Net7PlayerAction.TradeSync, Gameplay.TradeDomain.RequireActivePartnerId(), 0, cancellationToken);

    public async Task<bool> SetTradeCreditsAsync(long requestedAmount, CancellationToken cancellationToken = default)
    {
        uint partnerId = Gameplay.TradeDomain.RequireActivePartnerId();
        uint amount = Gameplay.TradeDomain.ValidateOfferedCredits(requestedAmount, PlayerSemantics.GetAuthoritativeMoney());
        // Retail updates its local offer-edit cache before queueing Action 0x18.
        // This is client presentation/input state, not authoritative TradeMoney.
        Gameplay.TradeDomain.RecordLocalOfferedCredits(amount);
        return await SendActionCoreAsync(
            Net7PlayerAction.TradeCredits, partnerId, amount, cancellationToken).ConfigureAwait(false);
    }

    private bool _dockRequestWithoutCamera = true;
    private bool _dockRequestThroughController, _gateRequestThroughController;
    public async Task<bool> RequestDockAsync(uint targetId, CancellationToken cancellationToken = default,
        bool completeWithoutCamera = true, bool useCameraController = false)
    {
        ThrowIfDisposed();
        _ = RequireLocalPlayerId();
        // Request-side camera/target state belongs to the retail/client
        // operation and is established before Net7Proxy's ordinary forwarding gate.
        PlayerSemantics.BeginTransition(Net7TransitionKind.Dock, targetId);
        Gameplay.ClearDockCamera();
        Gameplay.ClearLandCamera();
        _dockRequestWithoutCamera = completeWithoutCamera;
        _dockRequestThroughController = useCameraController;
        Gameplay.ClearGateCamera();
        return await SendActionCoreAsync(Net7PlayerAction.DockRequest, targetId, 0, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RequestLandAsync(uint planetId, CancellationToken cancellationToken = default,
        bool completeWithoutCamera = false, bool startCamera = false, bool useCameraController = false)
    {
        ThrowIfDisposed();
        _ = RequireLocalPlayerId();
        PlayerSemantics.BeginTransition(Net7TransitionKind.Land, planetId);
        Gameplay.ClearDockCamera();
        Gameplay.ClearLandCamera();
        var request = PlayerSemantics.TransitionRequest;
        ulong requestRevision = PlayerSemantics.TransitionRequestRevision;
        Gameplay.ClearGateCamera();
        Net7WorldObject? landingShip;
        lock (_worldStateSync) landingShip = World.LocalShipObjectId is uint shipId ? World.Objects.FindFirst(shipId) : null;
        bool sent = await SendActionCoreAsync(Net7PlayerAction.LandRequest, planetId, 0, cancellationToken).ConfigureAwait(false);
        if (sent) lock (_worldStateSync) landingShip?.SuppressGlobalWarpUpdates();
        if(sent && (useCameraController || startCamera && !completeWithoutCamera) && requestRevision==PlayerSemantics.TransitionRequestRevision)
        {
            Gameplay.BeginLandCamera(requestRevision,planetId,CurrentClientTime);
            NotifyPresentationStateChanged();
        }
        if (!sent || !completeWithoutCamera || useCameraController) return sent;
        // 006585BB preserves the selected planet and queues camera event 8.
        // 00799E40's no-camera branch invokes 007994D0 directly: Action 08
        // uses that local target, without waiting for server CameraControl.
        if (request != PlayerSemantics.TransitionRequest || requestRevision != PlayerSemantics.TransitionRequestRevision) return false;
        bool committed = await CommitLandAsync(cancellationToken).ConfigureAwait(false);
        _trace?.Invoke($"Land without cinematic: commit target=0x{planetId:X8} sent={committed}");
        return committed;
    }

    public async Task<bool> RequestGateAsync(
        uint targetId,
        uint cameraDerivedOptionalVar,
        CancellationToken cancellationToken = default, bool useCameraController = false)
    {
        ThrowIfDisposed();
        if (cameraDerivedOptionalVar > 1)
            throw new ArgumentOutOfRangeException(nameof(cameraDerivedOptionalVar), "Current MOVE-11 Gate Request OptionalVar is the camera-derived value 0 or 1.");
        _ = RequireLocalPlayerId();
        PlayerSemantics.BeginTransition(Net7TransitionKind.Gate, targetId);
        Gameplay.ClearDockCamera();
        Gameplay.ClearLandCamera();
        Gameplay.ClearGateCamera();
        _gateRequestWithoutCamera = cameraDerivedOptionalVar == 1;
        _gateRequestThroughController = useCameraController;
        return await SendActionCoreAsync(
            Net7PlayerAction.GateRequest, targetId, cameraDerivedOptionalVar, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> CommitDockAsync(CancellationToken cancellationToken = default)
        => CommitTransitionAsync(Net7TransitionKind.Dock, Net7PlayerAction.Dock, cancellationToken);

    public Task<bool> CommitLandAsync(CancellationToken cancellationToken = default)
        => CommitTransitionAsync(Net7TransitionKind.Land, Net7PlayerAction.Landing, cancellationToken);

    public async Task<bool> CompleteLandCameraAsync(ulong revision,uint target,Net7LandCameraPhase phase,CancellationToken cancellationToken=default)
    {
        if(Gameplay.LandCamera is not {} camera || camera.Revision!=revision || camera.TargetId!=target || camera.Phase!=phase)return false;
        if(phase==Net7LandCameraPhase.SpaceApproach && (PlayerSemantics.TransitionRequestRevision!=revision ||
            PlayerSemantics.TransitionRequest!=new Net7TransitionRequestState(Net7TransitionKind.Land,target)))return false;
        Gameplay.ClearLandCamera();
        bool result=phase!=Net7LandCameraPhase.SpaceApproach || await CommitLandAsync(cancellationToken).ConfigureAwait(false);
        NotifyPresentationStateChanged();
        return result;
    }

    public async Task<bool> CompleteDockCameraAsync(ulong revision, uint target, CancellationToken cancellationToken = default, bool departure = false)
    {
        if (Gameplay.DockCamera is not {} camera || camera.Revision != revision || camera.TargetId != target || camera.IsDeparture != departure) return false;
        if (departure)
        {
            // Retail event 0x13 restores the normal camera; departure sends no Action 07.
            Gameplay.ClearDockCamera();
            NotifyPresentationStateChanged();
            return true;
        }
        var ship = World.LocalShipObjectId is uint id ? World.Objects.FindFirst(id) : null;
        if (Net7WarpStatus.Read(ship?.AuxData, Gameplay.PresentationMode).Incapacitated) return false;
        Gameplay.ClearDockCamera();
        bool sent = await CommitDockAsync(cancellationToken).ConfigureAwait(false);
        NotifyPresentationStateChanged();
        return sent;
    }

    public Task<bool> CommitGateAsync(CancellationToken cancellationToken = default)
        => CommitTransitionAsync(Net7TransitionKind.Gate, Net7PlayerAction.Gate, cancellationToken);

    public Task<bool> CompleteGateCameraAsync(ulong sequence, uint targetId, CancellationToken cancellationToken = default)
    {
        if ((_gateRequestWithoutCamera && !_gateRequestThroughController) || Gameplay.GateCamera is not { } camera ||
            camera.Sequence != sequence || camera.TargetId != targetId)
            return Task.FromResult(false);
        var ship = World.LocalShipObjectId is uint id ? World.Objects.FindFirst(id) : null;
        return Net7WarpStatus.Read(ship?.AuxData, Gameplay.PresentationMode).Incapacitated
            ? Task.FromResult(false) : CommitGateAsync(cancellationToken);
    }

    private async Task<bool> CommitTransitionAsync(
        Net7TransitionKind kind,
        Net7PlayerAction action,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _ = RequireLocalPlayerId();
        uint targetId = PlayerSemantics.RequireCommitTarget(kind);
        if (targetId == uint.MaxValue) return false;
        // Retail clears the pending commit target after queuing the Action, before
        // the proxy's ordinary gameplay forwarding gate can accept/suppress it.
        PlayerSemantics.CompleteTransition(kind);
        return await SendActionCoreAsync(action, targetId, 0, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SendActionCoreAsync(
        Net7PlayerAction action,
        uint targetId,
        uint optionalVar,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(typeof(Net7PlayerAction), action))
            throw new ArgumentOutOfRangeException(nameof(action));
        // Every ConstructPlayerActionCommand producer (00581670, 00581CE0, 005946D0,
        // 00597650, 0059B380, 0059DB10, 005E2CE0, 005E37F0, 005E39F0, 0063DFE0,
        // 00658430, 0069C8F0, 006A1B80, 00744E70, 00747510, 007992F0, 007993E0,
        // 007994D0, 00799590) passes SClient+112C (0037's assigned object), never
        // Start's +1138 identity.
        uint actor = RequireLocalAvatarObjectId();
        byte[] payload = Net7GameplayWire.BuildAction(actor, action, targetId, optionalVar);
        byte subtype = checked((byte)action);
        _reliability?.ObserveGateActionSubtype(subtype);
        if (Net7HandoffRetryOperation.ActionSubtypeUsesRetry(subtype))
        {
            AwaitAnswer(subtype switch { 0x07 => Net7AwaitedAnswer.Dock, 0x08 => Net7AwaitedAnswer.Landing, _ => Net7AwaitedAnswer.Gate });
            // Clean ownership: the retry copy belongs to the exact
            // gameplay/session generation and fixed ID that issued the Action.
            // Never let a delayed copy borrow a replacement session identity.
            int ownerGameplayGeneration = Volatile.Read(ref _gameplayGeneration);
            uint ownerFixedSessionId = FixedSessionId;
            bool OwnerIsCurrent()
                => ownerFixedSessionId != 0
                   && Volatile.Read(ref _gameplayGeneration) == ownerGameplayGeneration
                   && FixedSessionId == ownerFixedSessionId;

            StartHandoffRetry(Net7HandoffRetryOperation.CreateAction(
                _clock,
                (opcode, retryPayload, retryCancellationToken) => TrySendFixedActionHandoffAsync(
                    opcode, retryPayload, ownerFixedSessionId, ownerGameplayGeneration, retryCancellationToken),
                payload,
                OwnerIsCurrent));
        }
        return await TrySendGameplayAsync(0x002C, payload, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> InviteCurrentTargetToGroupAsync(CancellationToken cancellationToken = default)
        => SendGroupRequestPlanAsync(Net7SocialGroupPolicy.PlanTargetAction(
            RequireLocalAvatarObjectId(), Gameplay.LocalSelectedTargetId, Net7PlayerAction.GroupInvite), cancellationToken);

    public Task<bool> InviteStationPlayerToGroupAsync(uint avatarId,CancellationToken cancellationToken=default)
        =>SendActionCoreAsync(Net7PlayerAction.GroupInvite,avatarId,0,cancellationToken);

    public Task<bool> AcceptGroupInviteAsync(CancellationToken cancellationToken = default)
    {
        Gameplay.Social.ClearGroupInvite();
        return SendSelfGroupActionAsync(Net7PlayerAction.GroupAccept, cancellationToken);
    }

    public Task<bool> RefuseGroupInviteAsync(CancellationToken cancellationToken = default)
    {
        Gameplay.Social.ClearGroupInvite();
        return SendSelfGroupActionAsync(Net7PlayerAction.GroupRefuse, cancellationToken);
    }

    public Task<bool> LeaveGroupAsync(CancellationToken cancellationToken = default)
        => SendSelfGroupActionAsync(PlayerSemantics.GetGroupState().LeaveAction, cancellationToken);

    public Task<bool> KickCurrentTargetFromGroupAsync(CancellationToken cancellationToken = default)
        => SendGroupRequestPlanAsync(Net7SocialGroupPolicy.PlanTargetAction(
            RequireLocalAvatarObjectId(), Gameplay.LocalSelectedTargetId, Net7PlayerAction.GroupKick), cancellationToken);

    public Task<bool> RequestGroupFinderAsync(CancellationToken cancellationToken = default)
        => SendSelfGroupActionAsync(Net7PlayerAction.GroupFinder, cancellationToken);

    private Task<bool> SendSelfGroupActionAsync(Net7PlayerAction action, CancellationToken cancellationToken)
    {
        uint self = RequireLocalAvatarObjectId();
        return SendActionCoreAsync(action, self, 0, cancellationToken);
    }

    public Task<bool> KickGroupMemberByIndexAsync(int memberIndex, CancellationToken cancellationToken = default)
    {
        Net7GroupStateView group = PlayerSemantics.GetGroupState();
        if ((uint)memberIndex >= (uint)group.Members.Count)
            return Task.FromResult(false);
        return SendGroupRequestPlanAsync(
            Net7SocialGroupPolicy.PlanMemberRowKick(RequireLocalAvatarObjectId(), group.Members[memberIndex],
                World.Objects.FindFirst(group.Members[memberIndex].GameId) is not null),
            cancellationToken);
    }

    public Task<bool> SendGroupFinderOptionAsync(uint optionType, bool enabled, CancellationToken cancellationToken = default)
    {
        if (optionType is not 0 and not 1 and not 3 and not 4 and not 5)
            throw new ArgumentOutOfRangeException(nameof(optionType), "Current SOCIAL-05 producers use option types 0, 1, 3, 4, or 5.");
        return TrySendGameplayAsync(0x002E, Net7GameplayWire.BuildGroupFinderOption(RequireLocalAvatarObjectId(), optionType, enabled), cancellationToken);
    }

    public Task<bool> SendPlayerCommandAsync(Net7ChatCommandType type, string text, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(typeof(Net7ChatCommandType), type))
            throw new ArgumentOutOfRangeException(nameof(type), "Current SOCIAL-01 producers emit only Types 1..4.");
        return TrySendGameplayAsync(0x0033, Net7GameplayWire.BuildPlayerCommand(RequireLocalAvatarObjectId(), type, text), cancellationToken);
    }

    public Task<bool> SendSubmitChatstreamAsync(
        IReadOnlyList<Net7PlayerNlpRecord> records,
        CancellationToken cancellationToken = default)
    {
        byte[] stream = Net7GameplayWire.BuildPlayerNlpStream(records);
        return TrySendGameplayAsync(0x005E, Net7GameplayWire.BuildChatstream(RequireLocalAvatarObjectId(), stream), cancellationToken);
    }

    public Task<bool> SendStarbaseRequestAsync(uint starbaseId, byte action, CancellationToken cancellationToken = default)
    {
        if (action == 1)
            throw new InvalidOperationException("Leave Starbase must use the current server-materialized active StarbaseID via LeaveStarbaseAsync().");
        if (action is not 0 and not 4 and not 6 and not 7 and not 8 and not 9 and not 10 and not 11)
            throw new ArgumentOutOfRangeException(nameof(action), "Current MOVE-09 non-leave producer set is {0,4,6,7,8,9,10,11}.");

        // Producer closure: retail's request/open branch only emits
        // Action 0 while no current starbase is materialized. Actions 6/9/10/11
        // have constant-zero StarbaseID at every current direct constructor
        // producer; keep the public integrated seam inside that proven domain.
        if (action == 0 && PlayerSemantics.ActiveStarbaseId is not null)
            throw new InvalidOperationException("Current MOVE-09 Action 0 request/open is emitted only when no starbase is materialized.");
        if ((action is 6 or 9 or 10 or 11) && starbaseId != 0)
            throw new ArgumentOutOfRangeException(nameof(starbaseId), $"Current MOVE-09 Action {action} producers serialize StarbaseID 0.");

        return TrySendGameplayAsync(0x004E, Net7GameplayWire.BuildStarbaseRequest(RequireStartSessionValue(), starbaseId, action), cancellationToken);
    }

    public async Task<bool> LeaveStarbaseAsync(CancellationToken cancellationToken = default)
    {
        uint starbaseId = PlayerSemantics.RequireActiveStarbaseId();
        byte[] payload = Net7GameplayWire.BuildStarbaseRequest(RequireStartSessionValue(), starbaseId, 1);

        // Clean ownership: the immutable leave request belongs to the
        // exact gameplay/session generation and fixed ID that armed its retry.
        // Retail re-reads global mutable state, but the authority explicitly
        // requires EnbNext to cancel/fail closed instead of letting stale leave
        // payloads migrate into a replacement session.
        int ownerGameplayGeneration = Volatile.Read(ref _gameplayGeneration);
        uint ownerFixedSessionId = FixedSessionId;
        bool OwnerIsCurrent()
            => ownerFixedSessionId != 0
               && Volatile.Read(ref _gameplayGeneration) == ownerGameplayGeneration
               && FixedSessionId == ownerFixedSessionId;

        AwaitAnswer(Net7AwaitedAnswer.Launch);
        StartHandoffRetry(Net7HandoffRetryOperation.CreateLeaveStarbase(
            _clock,
            (opcode, retryPayload, retryCancellationToken) => TrySendFixedLeaveHandoffAsync(
                opcode, retryPayload, ownerFixedSessionId, ownerGameplayGeneration, retryCancellationToken),
            payload,
            OwnerIsCurrent));
        return await TrySendGameplayAsync(0x004E, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RequestHulkOpenAsync(CancellationToken cancellationToken = default)
    {
        // Activating the loot control while Hulk UI is already open closes
        // it; retail does not emit another 0x0051 request in that branch.
        if (Gameplay.CurrentHulkId is not null)
        {
            await CloseHulkAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        // 00581E60: with no current target object (+D4->+88) the loot control
        // only reports a debug failure; no 0x0051 is queued.
        if (Gameplay.LocalSelectedTargetId is not uint targetId || World.Objects.FindFirst(targetId) is null)
            return false;
        return await TrySendGameplayAsync(
            0x0051,
            Net7GameplayWire.BuildHulkOpenRequest(RequireStartSessionValue()),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TransferHulkItemAsync(byte inventoryType, uint slot, CancellationToken cancellationToken = default)
    {
        if (inventoryType is not 5 and not 6)
            throw new ArgumentOutOfRangeException(nameof(inventoryType), "Hulk transfers use inventory type 5 (Equipped) or 6 (Cargo).");
        uint targetId = RequireLocalSelectedTargetId();
        if (Gameplay.CurrentHulkId != targetId || World.Objects.FindFirst(targetId) is null)
            throw new InvalidOperationException("Hulk transfer requires the still-current locally selected permitted Hulk target.");

        Net7InventoryTransferIdentity identity = ResolveInventoryTransferIdentity(Net7InventoryTransferIdSource.ClientShip);
        return await TrySendGameplayAsync(0x0027, Net7GameplayWire.BuildInventoryTransfer(
            identity.Id, inventoryType, slot, 0, uint.MaxValue, uint.MaxValue), cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseHulkAsync(CancellationToken cancellationToken = default)
    {
        lock (_worldStateSync)
        {
            if (Gameplay.CurrentHulkId is null) return;
            Gameplay.CloseHulk();
            NotifyPresentationStateChanged();
        }
        _ = await SendUiTriggerAsync(0x0C, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> TransferHarvestableItemAsync(uint slot, CancellationToken cancellationToken = default)
    {
        uint targetId = RequireLocalSelectedTargetId();
        if (Gameplay.CurrentHarvestableId != targetId || World.Objects.FindFirst(targetId)?.ObjectType != 0x26)
            return Task.FromResult(false);
        // 0059F470 panel type 4 selects inventory 0x12 and flag 2;
        // 0059FE30 uses the same all-stack transaction as the Hulk panel: 00690D20's first
        // word is SClient+1130 (the 0047 Client Ship), not the harvested object (INV-D102).
        return TrySendGameplayAsync(0x0027, Net7GameplayWire.BuildInventoryTransfer(
            ClientShipTransferId, 0x12, slot, 0, uint.MaxValue, uint.MaxValue), cancellationToken);
    }

    public async Task<bool> CloseHarvestableAsync(CancellationToken cancellationToken = default)
    {
        uint id;
        lock (_worldStateSync)
        {
            if (Gameplay.CurrentHarvestableId is not uint current) return false;
            id = current;
            Gameplay.CloseHarvestable();
            NotifyPresentationStateChanged();
        }
        // 00581D74 and 00581670 cancel the held harvest target with Action 0x11.
        return await SendActionAsync(Net7PlayerAction.Prospect, id, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> TrainSkillAsync(int skillIndex, CancellationToken cancellationToken = default)
    {
        // This is the confirmed-training send seam. Retail's Upgrade
        // presentation uses CanTrain to decide whether confirmation can be opened,
        // but result 2 does not re-read Availability/Level/MaxSkillLevels/SkillPoints
        // before serializing the retained AllSkills key. Keep that UI predicate in
        // PlayerSemantics and do not turn it into a second protocol admission gate.
        return TrySendGameplayAsync(
            0x0057,
            Net7GameplayWire.BuildSkillAction(RequireLocalAvatarObjectId(), checked((uint)skillIndex)),
            cancellationToken);
    }

    public Task<bool> ActivateSkillAsync(uint abilityIndex, CancellationToken cancellationToken = default)
    {
        if (abilityIndex == uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(abilityIndex), "0xFFFFFFFF is the current retail no-ability sentinel and is not emitted.");
        return TrySendGameplayAsync(0x0058, Net7GameplayWire.BuildSkillAbility(RequireLocalAvatarObjectId(), abilityIndex), cancellationToken);
    }

    public Task<bool> UseInventoryItemAsync(uint targetGameId, byte inventory, byte slot, CancellationToken cancellationToken = default)
    {
        // 0059FE30/005A0740 read SClient+1130, the 0047 Client Ship (INV-D102); callers pass
        // ClientShipTransferId. Do not substitute RequireLocalPlayerId() or
        // authoritative World.CurrentTargetId / ShipAuxData.TargetGameID here.
        return TrySendGameplayAsync(
            0x005D,
            Net7GameplayWire.BuildUseInventoryItem(targetGameId, inventory, slot),
            cancellationToken);
    }

    public Task<bool> SendUiTriggerAsync(uint action, CancellationToken cancellationToken = default)
    {
        if (!World.ClientSendUiTriggersEnabled)
            return Task.FromResult(false);
        return TrySendGameplayAsync(
            0x0065,
            Net7GameplayWire.BuildUiTrigger(RequireLocalAvatarObjectId(), action),
            cancellationToken);
    }

    public Task<bool> DestroyInventoryItemConfirmedAsync(uint selectedTargetId,uint inventory,uint slot,uint count,CancellationToken cancellationToken=default)
        => StartsInventoryTransaction(slot,count)
            ? TrySendGameplayAsync(0x0027,Net7GameplayWire.BuildConfirmedInventoryDestruction(selectedTargetId,inventory,slot,count),cancellationToken)
            : Task.FromResult(false);

    public Task<bool> TransferInventoryAsync(
        Net7InventoryTransferIdentity identity,
        uint fromInventory,
        uint fromSlot,
        uint toInventory,
        uint toSlot,
        uint count,
        CancellationToken cancellationToken = default)
    {
        // The normal UI producer must provide the exact retail-local
        // selected-target-cache identity explicitly. There is intentionally no
        // implicit overload that can borrow authoritative World.CurrentTargetId.
        if (!StartsInventoryTransaction(fromSlot, count))
            return Task.FromResult(false);
        Net7InventoryTransferPlan plan = Net7InventoryDomain.CreateInv04Transfer(
            identity, fromInventory, fromSlot, toInventory, toSlot, count);
        return SendInventoryTransferPlanAsync(plan, cancellationToken);
    }

    public Task<bool> TransferInventoryFromUiDestinationAsync(
        Net7InventoryTransferIdentity identity,
        uint fromInventory,
        uint fromSlot,
        uint uiDestination,
        uint toSlot,
        uint count,
        CancellationToken cancellationToken = default)
    {
        if (!StartsInventoryTransaction(fromSlot, count))
            return Task.FromResult(false);
        Net7InventoryTransferPlan plan = Net7InventoryDomain.CreateInv04UiTransfer(
            identity, fromInventory, fromSlot, uiDestination, toSlot, count);
        return SendInventoryTransferPlanAsync(plan, cancellationToken);
    }

    public Task<bool> ExecuteVendorTransactionAsync(
        Net7VendorTransactionPath path,
        uint sourceSlot,
        bool primaryConfirmationAccepted = false,
        CancellationToken cancellationToken = default)
    {
        // Dedicated vendor-window stack paths do not queue until their first
        // confirmation is accepted. Shortcut stack paths have no such gate.
        if (Net7InventoryDomain.VendorPathRequiresPrimaryConfirmation(path)
            && !primaryConfirmationAccepted)
            return Task.FromResult(false);

        // The normal vendor controller uses the same SClient+1130 Client Ship
        // as the shared inventory transaction controller (INV-D102).
        // 00690D20 reads it without a nonzero gate. Station requests
        // carry NPC identity separately; never substitute that ID or ShipAuxData.TargetGameID.
        Net7InventoryTransferIdentity identity = new(ClientShipTransferId,
            Net7InventoryTransferIdSource.ClientShip);

        // 005A02E0/005A03B0/005A0740: one-item paths start with Num=1 without
        // reading the source slot; whole-stack paths read the count below.
        uint count = Net7InventoryDomain.VendorPathUsesWholeStack(path)
            ? ReadVendorWholeStackCount(path, sourceSlot)
            : 1;
        // InventoryTransaction_Start 00690AA0 starts nothing for NO_SLOT or Num=0.
        if (Net7InventoryDomain.CreateVendorTransfer(identity, path, sourceSlot, count) is not { } plan)
            return Task.FromResult(false);
        return SendInventoryTransferPlanAsync(plan, cancellationToken);
    }

    public Task<bool> OfferTradeItemAsync(
        uint fromInventory,
        uint fromSlot,
        uint tradeSlot,
        uint count,
        CancellationToken cancellationToken = default)
    {
        if (!StartsInventoryTransaction(fromSlot, count))
            return Task.FromResult(false);
        Net7InventoryTransferPlan plan = Net7InventoryDomain.CreateTradeOffer(
            ResolveInventoryTransferIdentity(Net7InventoryTransferIdSource.ClientShip),
            fromInventory, fromSlot, tradeSlot, count);
        return SendInventoryTransferPlanAsync(plan, cancellationToken);
    }

    public Task<bool> RetractTradeItemAsync(uint tradeSlot, uint count, CancellationToken cancellationToken = default)
    {
        if (!StartsInventoryTransaction(tradeSlot, count))
            return Task.FromResult(false);
        Net7InventoryTransferPlan plan = Net7InventoryDomain.CreateTradeRetraction(
            ResolveInventoryTransferIdentity(Net7InventoryTransferIdSource.ClientShip), tradeSlot, count);
        return SendInventoryTransferPlanAsync(plan, cancellationToken);
    }

    public Task<bool> ReorderTradeItemAsync(
        uint fromTradeSlot,
        uint toTradeSlot,
        uint count,
        CancellationToken cancellationToken = default)
    {
        if (!StartsInventoryTransaction(fromTradeSlot, count))
            return Task.FromResult(false);
        Net7InventoryTransferPlan plan = Net7InventoryDomain.CreateTradeReorder(
            ResolveInventoryTransferIdentity(Net7InventoryTransferIdSource.ClientShip), fromTradeSlot, toTradeSlot, count);
        return SendInventoryTransferPlanAsync(plan, cancellationToken);
    }

    public Task<bool> SortInventoryAsync(
        Net7InventorySortTarget target,
        Net7InventorySortPreset preset,
        bool reverse,
        CancellationToken cancellationToken = default)
    {
        Net7InventorySortTarget currentTarget = Net7InventoryDomain.RequireCurrentSortTarget((uint)target);
        Net7InventorySortKeys keys = Net7InventoryDomain.ResolveSortPreset(preset);
        return TrySendGameplayAsync(0x0028, Net7GameplayWire.BuildInventorySort(
            RequireLocalAvatarObjectId(), (uint)currentTarget, keys.Sort1, keys.Sort2, keys.Sort3, reverse), cancellationToken);
    }

    public Task<bool> SortInventoryAsync(
        uint targetInventory,
        uint sort1,
        uint sort2,
        uint sort3,
        bool reverse,
        CancellationToken cancellationToken = default)
    {
        Net7InventorySortTarget target = Net7InventoryDomain.RequireCurrentSortTarget(targetInventory);
        // Retail 5D0D20/657DA0 resolves three INT options verbatim. Preset defaults
        // are not an admission whitelist for persisted character settings.
        return TrySendGameplayAsync(0x0028, Net7GameplayWire.BuildInventorySort(
            RequireLocalAvatarObjectId(), (uint)target, sort1, sort2, sort3, reverse), cancellationToken);
    }

    public Task<bool> SortCargoAsync(Net7InventorySortPreset preset, bool reverse, CancellationToken cancellationToken = default)
        => SortInventoryAsync(Net7InventorySortTarget.Cargo, preset, reverse, cancellationToken);

    public Task<bool> SortVaultAsync(Net7InventorySortPreset preset, bool reverse, CancellationToken cancellationToken = default)
        => SortInventoryAsync(Net7InventorySortTarget.Vault, preset, reverse, cancellationToken);

    public Task<bool> SetItemStateFlag2000Async(
        uint requestId,
        byte slot,
        bool enabled,
        CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x0029, Net7GameplayWire.BuildItemStateRequest(requestId, 0x2000, enabled, 2, slot), cancellationToken);

    public async Task<bool> SetManufacturingModeAsync(Net7ManufacturingMode mode, CancellationToken cancellationToken = default)
    {
        if (mode is not Net7ManufacturingMode.None and not Net7ManufacturingMode.Manufacture and not Net7ManufacturingMode.Analyze and not Net7ManufacturingMode.Refining)
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == Net7ManufacturingMode.None)
            return await CloseManufacturingTerminalAsync(cancellationToken).ConfigureAwait(false);

        Net7ManufacturingDomain manufacturing = Gameplay.ManufacturingDomain;
        if (manufacturing.IsTerminalOpen && manufacturing.RequestedMode != mode)
        {
            // OpenStarbaseInterfaceByCommand closes the active starbase
            // sub-interface before opening a different manufacturing-family
            // terminal. Preserve that lifecycle so the proven 0x007E reset and
            // 0x0079 None close pair cannot be skipped by a direct mode switch.
            bool closed = await CloseManufacturingTerminalAsync(cancellationToken).ConfigureAwait(false);
            if (!closed)
                return false;
        }

        // Terminal-open traffic is not conditioned on a currently
        // resolvable 0x007F manufacturing object. Rich state may arrive later.
        bool sent = await TrySendGameplayAsync(0x0079, Net7GameplayWire.BuildManufacturingMode(RequireStartSessionValue(), mode), cancellationToken).ConfigureAwait(false);
        if (sent)
        {
            Gameplay.SetRequestedManufacturingMode(mode);

            // Only the Manufacture terminal owns the category browser.
            // On open, retail auto-drills a uniquely eligible 2x5x5 path and
            // converges on the same final-leaf 0x007A sender as manual browsing.
            if (mode == Net7ManufacturingMode.Manufacture
                && Gameplay.ManufacturingDomain.TryAutoSelectUniqueVisibleLeaf(out uint leafId))
            {
                if (await TrySendGameplayAsync(
                    0x007A, Net7GameplayWire.BuildManufacturingCategory(RequireStartSessionValue(), leafId), cancellationToken).ConfigureAwait(false))
                    await InitializeManufacturingCategoryFilterAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return sent;
    }

    public async Task<bool> CloseManufacturingTerminalAsync(CancellationToken cancellationToken = default)
    {
        Net7ManufacturingDomain manufacturing = Gameplay.ManufacturingDomain;
        if (!manufacturing.IsTerminalOpen)
            return false;

        // Current close choreography: reset action first, then
        // close the terminal mode. There is no 0x007E ACK between these sends.
        bool resetSent = await TrySendGameplayAsync(
            0x007E, Net7GameplayWire.BuildManufacturingAction(RequireStartSessionValue(), 0), cancellationToken).ConfigureAwait(false);
        if (!resetSent)
            return false;
        manufacturing.RecordActionSent(0);

        // Direct EnbNext networking fails closed here: if the required reset
        // could not be emitted, never let the mode-close overtake it.
        bool modeSent = await TrySendGameplayAsync(
            0x0079, Net7GameplayWire.BuildManufacturingMode(RequireStartSessionValue(), Net7ManufacturingMode.None), cancellationToken).ConfigureAwait(false);
        if (modeSent)
            Gameplay.SetRequestedManufacturingMode(Net7ManufacturingMode.None);
        return modeSent;
    }

    public bool SelectManufacturingPrimaryCategory(int primaryIndex)
        => Gameplay.ManufacturingDomain.SelectPrimaryCategory(primaryIndex);

    public bool SelectManufacturingCategoryLevel(int categoryIndex)
        => Gameplay.ManufacturingDomain.SelectCategory(categoryIndex);

    public async Task<bool> SelectManufacturingSubcategoryAsync(int subcategoryIndex, CancellationToken cancellationToken = default)
    {
        if (!Gameplay.ManufacturingDomain.TrySelectSubcategory(subcategoryIndex, out uint leafId))
            return false;
        if (!await TrySendGameplayAsync(
            0x007A, Net7GameplayWire.BuildManufacturingCategory(RequireStartSessionValue(), leafId), cancellationToken).ConfigureAwait(false)) return false;
        return await InitializeManufacturingCategoryFilterAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> InitializeManufacturingCategoryFilterAsync(CancellationToken cancellationToken)
    {
        var manufacturing = Gameplay.ManufacturingDomain;
        if (!manufacturing.InitialCategoryFilterPending) return true;
        // Requested retail UX: the first recipe category starts with every level.
        // Use the verified All command once, not an echo of incoming AuxData or
        // a retry driven by an empty list. Later user filter choices are retained.
        bool sent = await TrySendGameplayAsync(0x0080,
            Net7GameplayWire.BuildManufacturingFilter(RequireStartSessionValue(), true, 0x1FF), cancellationToken).ConfigureAwait(false);
        if (sent) manufacturing.RecordFilterSent(true, 0x1FF);
        return sent;
    }

    public async Task<bool> SelectManufactureItemAsync(uint itemId, CancellationToken cancellationToken = default)
    {
        Net7ManufacturingDomain manufacturing = Gameplay.ManufacturingDomain;
        if (!manufacturing.CanSelectManufactureItem(itemId))
            return false;
        bool sent = await TrySendGameplayAsync(
            0x007B, Net7GameplayWire.BuildManufactureItem(RequireStartSessionValue(), itemId), cancellationToken).ConfigureAwait(false);
        if (sent)
            manufacturing.RecordManufactureItemSent(itemId);
        return sent;
    }

    public async Task<bool> SelectRefineryItemAsync(uint itemId, CancellationToken cancellationToken = default)
    {
        Net7ManufacturingDomain manufacturing = Gameplay.ManufacturingDomain;
        if (!manufacturing.CanSelectRefineryItem(itemId))
            return false;
        bool sent = await TrySendGameplayAsync(
            0x007C, Net7GameplayWire.BuildRefineryItem(RequireStartSessionValue(), itemId), cancellationToken).ConfigureAwait(false);
        if (sent)
            manufacturing.RecordRefineryItemSent(itemId);
        return sent;
    }

    public async Task<bool> SendManufacturingActionAsync(uint actionId, CancellationToken cancellationToken = default)
    {
        if (actionId > 3)
            throw new ArgumentOutOfRangeException(nameof(actionId), "Current MFG-05 action IDs are 0..3.");
        Net7ManufacturingDomain manufacturing = Gameplay.ManufacturingDomain;
        if (!manufacturing.CanSendManualAction(actionId))
            return false;
        bool sent = await TrySendGameplayAsync(
            0x007E, Net7GameplayWire.BuildManufacturingAction(RequireStartSessionValue(), actionId), cancellationToken).ConfigureAwait(false);
        if (sent)
            manufacturing.RecordActionSent(actionId);
        return sent;
    }

    // 005B4850: the manufacturing browser tabs (005B1C20) and the first Recent
    // click (005B16C0 case 5) queue ManufactureAction 0 and only reset the Go
    // and quality widgets; the pending action (+BA4) and countdown (+BA8)
    // stay as they are, so no domain action state is recorded here.
    public async Task<bool> SendManufacturingSelectionResetAsync(CancellationToken cancellationToken = default)
    {
        if (!Gameplay.ManufacturingDomain.IsTerminalOpen || Gameplay.ManufacturingDomain.RequestedMode != Net7ManufacturingMode.Manufacture)
            return false;
        return await TrySendGameplayAsync(
            0x007E, Net7GameplayWire.BuildManufacturingAction(RequireStartSessionValue(), 0), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Advance the client-owned action/presentation timer by a host-frame delta.</summary>
    public async Task<bool> AdvanceManufacturingActionAsync(uint elapsedMilliseconds, CancellationToken cancellationToken = default)
    {
        Net7ManufacturingDomain manufacturing = Gameplay.ManufacturingDomain;
        bool analysisFollowUpDue = manufacturing.AdvanceActionTimer(elapsedMilliseconds);

        // A manufacture result may already be materialized while the one-
        // second presentation gate is still running. Reconcile after the
        // local tick so expiry can release that result without any resend.
        await ReconcileManufacturingAsync(cancellationToken).ConfigureAwait(false);
        if (!analysisFollowUpDue)
            return false;

        bool sent = await TrySendGameplayAsync(
            0x007E, Net7GameplayWire.BuildManufacturingAction(RequireStartSessionValue(), 1), cancellationToken).ConfigureAwait(false);
        if (sent)
            manufacturing.RecordActionSent(1);
        return sent;
    }

    public async Task<bool> SetManufacturingTechFilterAsync(bool enable, uint bitField, CancellationToken cancellationToken = default)
    {
        bool singleTechLevel = bitField is >= 0x001 and <= 0x100 && (bitField & (bitField - 1)) == 0;
        if (!singleTechLevel && bitField != 0x1FF)
            return false;
        Net7ManufacturingDomain manufacturing = Gameplay.ManufacturingDomain;
        if (!manufacturing.ShouldSendFilter(enable, bitField))
            return false;
        bool sent = await TrySendGameplayAsync(
            0x0080, Net7GameplayWire.BuildManufacturingFilter(RequireStartSessionValue(), enable, bitField), cancellationToken).ConfigureAwait(false);
        if (sent)
            manufacturing.RecordFilterSent(enable, bitField);
        return sent;
    }

    public Task<bool> ForfeitMissionAsync(int missionSlot, bool confirmed, CancellationToken cancellationToken = default)
    {
        uint internalMissionId = PlayerSemantics.RequireForfeitMissionId(missionSlot, confirmed, _clock.GetMilliseconds());
        return TrySendGameplayAsync(0x0086, Net7GameplayWire.BuildMissionRequest(RequireStartSessionValue(), internalMissionId), cancellationToken);
    }

    public Task<bool> ClearMissionAsync(int missionSlot, CancellationToken cancellationToken = default)
    {
        uint internalMissionId = PlayerSemantics.RequireClearMissionId(missionSlot, _clock.GetMilliseconds());
        return TrySendGameplayAsync(0x0087, Net7GameplayWire.BuildMissionRequest(RequireStartSessionValue(), internalMissionId), cancellationToken);
    }

    public Task<bool> RequestIncapacitationDetailsAsync(CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x008D, Net7GameplayWire.BuildIncapacitationRequest(RequireLocalAvatarObjectId()), cancellationToken);

    public Task<bool> SendDistressBeaconAsync(bool on, CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x008E, Net7GameplayWire.BuildDistressBeacon(RequireLocalAvatarObjectId(), on), cancellationToken);

    public Task<bool> RequestRescueAsync(CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x008F, Net7GameplayWire.BuildIncapacitationRequest(RequireLocalAvatarObjectId()), cancellationToken);

    public bool SelectWarpRoute(IReadOnlyList<uint> route)
        => PlayerSemantics.SelectWarpRoute(route);

    public async Task<bool> StartWarpAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        IReadOnlyList<uint> route;
        byte[] warpPayload;

        // Retail has already captured PlayerID + an owned copy of the
        // selected route before Net7Proxy performs its synchronous pre-warp
        // 0x1004 check. Capture the complete payload before our first await so
        // an asynchronous sector transition cannot splice a newer Start value
        // into an older selected route.
        lock (_worldStateSync)
        {
            route = PlayerSemantics.RequireSelectedWarpRoute();
            warpPayload = Net7GameplayWire.BuildWarp(RequireStartSessionValue(), route);
            // Retail retains the radar route before handing the owned packet to
            // transport. A fast 009C must have its vector available even while
            // the send is completing. Capture under the sector reset boundary;
            // this does not optimistically change the ship's warp state.
            Gameplay.CaptureWarpRoute(route);
        }

        await TrySendPositionSnapshotAsync(cancellationToken).ConfigureAwait(false);
        bool sent = await TrySendGameplayAsync(0x009B, warpPayload, cancellationToken).ConfigureAwait(false);
        if (sent)
            PlayerSemantics.ClearWarpRoute();
        return sent;
    }

    public Task<bool> SendChatControlRequestAsync(
        Net7ClientChatRequestKind kind,
        string? player = null,
        string? channel = null,
        string? value = null,
        CancellationToken cancellationToken = default)
    {
        Net7ClientChatRequestPlan plan = Net7SocialChatPolicy.PlanChatControlRequest(
            RequireStartSessionValue(), kind, player, channel, value);
        return plan.ShouldSend
            ? TrySendGameplayAsync(plan.Opcode, plan.Payload, cancellationToken)
            : Task.FromResult(false);
    }

    private Task<bool> SendClientChatRequestCoreAsync(
        uint requestCode,
        string arg1,
        string arg2,
        string arg3,
        ReadOnlyMemory<byte> blob,
        CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x00A3, Net7GameplayWire.BuildClientChatRequest(
            RequireStartSessionValue(), requestCode, arg1, arg2, arg3, blob.Span), cancellationToken);

    public Task<bool> SetPresenceVisibilityAsync(bool restricted, CancellationToken cancellationToken = default)
    {
        uint requestCode = 0;
        MutateWorldAndPublish(() => requestCode = Gameplay.Social.SelectPresenceRequestCode(restricted));
        return SendClientChatRequestCoreAsync(requestCode, string.Empty, string.Empty, string.Empty, ReadOnlyMemory<byte>.Empty, cancellationToken);
    }


    public Task<bool> SendGroupCtaPanelRequestAsync(
        Net7GroupCtaPanelRequestType requestType,
        CancellationToken cancellationToken = default)
        => SendGroupRequestPlanAsync(Net7SocialGroupPolicy.PlanPanelCta(
            RequireLocalAvatarObjectId(), PlayerSemantics.GetGroupState().IsGroupLeader, requestType), cancellationToken);

    public Task<bool> SendGroupCtaTopLevel12Async(CancellationToken cancellationToken = default)
        => SendGroupRequestPlanAsync(Net7SocialGroupPolicy.PlanTopLevelCta12(RequireLocalAvatarObjectId()), cancellationToken);

    public Task<bool> ToggleFormationAsync(CancellationToken cancellationToken = default)
    {
        var group = PlayerSemantics.GetGroupState();
        int state = World.LocalPlayerAuxData.Properties.First(p => p.Path == "LocalPlayerAuxData.GroupInfo").State;
        return SendGroupRequestPlanAsync(Net7SocialGroupPolicy.PlanFormationToggle(RequireLocalAvatarObjectId(),
            group.IsInGroup, group.IsGroupLeader, state,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(group.Formation), group.FormationName), cancellationToken);
    }

    public Task<bool> SendGroupCtaMember12Async(int memberIndex, CancellationToken cancellationToken = default)
    {
        Net7GroupStateView group = PlayerSemantics.GetGroupState();
        if ((uint)memberIndex >= (uint)group.Members.Count)
            return Task.FromResult(false);
        return SendGroupRequestPlanAsync(
            Net7SocialGroupPolicy.PlanMemberCta12(RequireLocalAvatarObjectId(), group.Members[memberIndex]),
            cancellationToken);
    }

    public Task<bool> ReplyPendingGuildPromptAsync(bool accept, CancellationToken cancellationToken = default)
    {
        Net7GuildPrompt? prompt = Gameplay.GuildPrompt;
        Gameplay.ClearGuildPrompt();
        if (prompt is null)
            return Task.FromResult(false);

        Net7GuildReplyPlan plan = Net7SocialGuildPolicy.PlanPromptReply(RequireLocalAvatarObjectId(), prompt, accept);
        return plan.ShouldSend
            ? TrySendGameplayAsync(plan.Opcode, plan.Payload, cancellationToken)
            : Task.FromResult(false);
    }

    public Task<bool> RequestGuildRankNamesAsync(CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x00D4, Net7GameplayWire.BuildGuildRankNamesRequest(RequireLocalAvatarObjectId()), cancellationToken);

    public Task<bool> SendDebugAsync(uint gameId, uint command, uint userData, CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x001A, Net7GameplayWire.BuildDebug(gameId, command, userData), cancellationToken);

    public Task<bool> SendColoredMessageAsync(byte color, string text, CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x001D, Net7GameplayWire.BuildMessageString(color, text), cancellationToken);

    public async Task<bool> SelectTalkTreeAsync(byte selection, CancellationToken cancellationToken = default)
    {
        byte[] payload = Net7GameplayWire.BuildSelectTalkTree(RequireLocalPlayerId(), selection);
        bool sent = await TrySendGameplayAsync(0x0055, payload, cancellationToken).ConfigureAwait(false);
        if (sent) AwaitAnswer(Net7AwaitedAnswer.Conversation);
        _trace?.Invoke($"Talk reply 0055 selection={selection} payload={Convert.ToHexString(payload)} sent={sent}");
        return sent;
    }

    public Task<bool> RequestVerbAsync(uint objectId, CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x005A, Net7GameplayWire.BuildVerbRequest(RequireLocalShipObjectId(), objectId, 1), cancellationToken);

    public Task<bool> SendGadgetControlAsync(string name, CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x0068, Net7GameplayWire.BuildGadgetControl(name, 0x10, RequireLocalPlayerId()), cancellationToken);

    public Task<bool> SubmitShipRecustomizationAsync(ReadOnlyMemory<byte> serializedShipCustomization, CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x0082, Net7GameplayWire.BuildRecustomizeShipDone(serializedShipCustomization.Span, RequireStartSessionValue()), cancellationToken);

    public Task<bool> SubmitAvatarRecustomizationAsync(ReadOnlyMemory<byte> serializedAvatarData, CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x0084, Net7GameplayWire.BuildRecustomizeAvatarDone(serializedAvatarData.Span, RequireStartSessionValue()), cancellationToken);

    public Task<bool> SendPetitionStuckAsync(
        uint gameId1,
        uint gameId2,
        string subject,
        string complaint,
        string playerList,
        CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x0088, Net7GameplayWire.BuildPetitionStuck(gameId1, gameId2, subject, complaint, playerList), cancellationToken);

    public Task<bool> SendGalaxyMap2Async(ReadOnlyMemory<byte> data, bool flag, CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x0098, Net7GameplayWire.BuildGalaxyMap2(1, data.Span, RequireStartSessionValue(), flag), cancellationToken);

    public Task<bool> ToggleGalaxyMapAsync(CancellationToken cancellationToken = default)
    {
        if (!_transportState.GameplayForwardingActive || PlayerId is null) return Task.FromResult(false);
        // 007A5540: toggle retained request flag, empty type-1 payload. Only
        // subsequent server records change visibility; never filter locally.
        GalaxyMapShowAll = !GalaxyMapShowAll;
        var sent = SendGalaxyMap2Async(ReadOnlyMemory<byte>.Empty, GalaxyMapShowAll, cancellationToken);
        // 005408B0: then a channel-0 notice for the new setting (00B79318 / 00B79364), whether or not the send succeeded (MAP-D18).
        bool all = GalaxyMapShowAll;
        MutateWorldAndPublish(() => Gameplay.Social.ApplyBaseMessage(new(0, all
            ? "Galaxymap PIP will now show the whole galaxy."
            : "Galaxymap PIP will now show only areas that you have explored.")));
        return sent;
    }

    public Task<bool> SendStarbaseAvatarChangeAsync(
        uint room,
        float orientation,
        Vector3 position,
        uint actionFlag,
        CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x009D, Net7GameplayWire.BuildStarbaseAvatarChange(
            RequireLocalAvatarObjectId(), room, orientation, position, actionFlag), cancellationToken);

    public Task<bool> SendStarbaseRoomChangeAsync(uint newRoom, uint oldRoom, CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x009F, Net7GameplayWire.BuildStarbaseRoomChange(RequireLocalAvatarObjectId(), newRoom, oldRoom), cancellationToken);

    public Task<bool> SendEmoteAsync(uint emote, CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x00A1, Net7GameplayWire.BuildTriggerEmote(RequireLocalAvatarObjectId(), emote), cancellationToken);

    public Task<bool> RespondConfirmedActionAsync(
        uint type,
        uint id,
        bool accepted,
        CancellationToken cancellationToken = default)
    {
        if (!Gameplay.TryConsumeConfirmedAction(type, id, out Net7ConfirmedActionPending? pending))
            return Task.FromResult(false);
        return TrySendGameplayAsync(
            0x00C0,
            Net7GameplayWire.BuildConfirmedActionResponse(RequireLocalAvatarObjectId(), pending!.Id, accepted),
            cancellationToken);
    }

    public Task<bool> SpendReferralPointAsync(uint referralActionValue, CancellationToken cancellationToken = default)
        => TrySendGameplayAsync(0x00E8, Net7GameplayWire.BuildSpendReferralPoint(referralActionValue), cancellationToken);

    public Task<bool> SendGmDestroyItemAsync(CancellationToken cancellationToken = default)
    {
        Net7GmInventoryEditorUpdate? editor = Gameplay.GmInventoryEditor;
        return editor is null
            ? Task.FromResult(false)
            : TrySendGameplayAsync(0x00EC, Net7GameplayWire.BuildGmEditorCommand(editor.PlayerPackTargetValue, 1, string.Empty), cancellationToken);
    }

    public Task<bool> SendGmResetMissionAsync(CancellationToken cancellationToken = default)
    {
        Net7GmMissionLogUpdate? editor = Gameplay.GmMissionLog;
        return editor is null
            ? Task.FromResult(false)
            : TrySendGameplayAsync(0x00ED, Net7GameplayWire.BuildGmEditorCommand(editor.Field0, 1, string.Empty), cancellationToken);
    }

    public async Task<bool> RequestLogoffAsync(Net7LogoffDestination destination, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(typeof(Net7LogoffDestination), destination))
            throw new ArgumentOutOfRangeException(nameof(destination));

        // Local preflight must not arm a transaction that cannot emit.
        // Resolve the retained Start/session value before publishing pending state.
        uint startSessionValue = RequireStartSessionValue();
        // 0074BB50 sends 0x00B9 on every choice, also while an earlier one is unconfirmed, and keeps the latest
        // destination (+0x138C) for the confirmation.
        Net7LogoffDestination? previous;
        lock (_logoffSync)
        {
            previous = _pendingLogoffDestination;
            _pendingLogoffDestination = destination;
        }

        try
        {
            bool sent = await TrySendGameplayAsync(
                0x00B9, Net7GameplayWire.BuildLogoffRequest(startSessionValue), cancellationToken).ConfigureAwait(false);
            if (!sent)
                RestorePendingLogoff(destination, previous);
            return sent;
        }
        catch
        {
            RestorePendingLogoff(destination, previous);
            throw;
        }
    }

    /// <summary>A request that could not be sent leaves the earlier pending destination (if any) as it was.</summary>
    private void RestorePendingLogoff(Net7LogoffDestination expected, Net7LogoffDestination? previous)
    {
        lock (_logoffSync)
        {
            if (_pendingLogoffDestination == expected)
                _pendingLogoffDestination = previous;
        }
    }

    /// <summary>
    /// Native scene/presentation pass. The inbound 0x003C packet arms
    /// a coalescing one-shot notification plus a three-pass refresh countdown.
    /// This is runtime-owned presentation work, not a public gameplay command.
    /// </summary>
    private async Task<bool> AdvancePresentationFrameAsync(CancellationToken cancellationToken)
    {
        Gameplay.AdvancePresentationRefresh();
        if (!Gameplay.ConsumePresentationNotification())
            return false;

        if (PlayerId is not uint startSessionValue)
        {
            _trace?.Invoke("ENTRY-09 presentation notification suppressed: no Start-materialized PlayerID");
            return false;
        }

        return await TrySendGameplayAsync(
            0x00A3,
            Net7SocialChatPolicy.BuildPresentationActivationRequest(startSessionValue),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _gameplayClientTargetActive = false;

        await TrySendLocalClientLossNotificationAsync().ConfigureAwait(false);
        _gameplayLifetimeCts.Cancel();
        await StopBackgroundTaskAsync(_watchdogCts, _watchdogTask, "intermediate-login watchdog").ConfigureAwait(false);
        await StopBackgroundTaskAsync(_keepaliveLoopCts, _keepaliveLoopTask, "keepalive/MVAS cadence loop").ConfigureAwait(false);
        await StopBackgroundTaskAsync(_routedPumpCts, _routedPumpTask, "routed UDP pump").ConfigureAwait(false);
        await StopBackgroundTaskAsync(_masterPumpCts, _masterPumpTask, "master/control pump").ConfigureAwait(false);
        await StopBackgroundTaskAsync(_accountPumpCts, _accountPumpTask, "account role-10 pump").ConfigureAwait(false);
        if (_reliability is not null)
            await _reliability.DisposeAsync().ConfigureAwait(false);
        await DrainOwnedOneShotTasksAsync().ConfigureAwait(false);

        if (_account is not null)
            await _account.DisposeAsync().ConfigureAwait(false);
        if (_routed is not null)
            await _routed.DisposeAsync().ConfigureAwait(false);
        if (_master is not null)
            await _master.DisposeAsync().ConfigureAwait(false);

        _watchdogCts = null;
        _watchdogTask = null;
        _reliability = null;
        _keepalive = null;
        _packetOptimisation = null;
        _loginReset = null;
        _keepaliveLoopCts = null;
        _keepaliveLoopTask = null;
        _routedPumpCts = null;
        _routedPumpTask = null;
        _masterPumpCts = null;
        _masterPumpTask = null;
        _accountPumpCts = null;
        _accountPumpTask = null;
        _account = null;
        _routed = null;
        _master = null;
        _gameplayLifetimeCts.Dispose();
        SetLoginState(Net7LoginState.Disposed);
    }

    private async Task StopBackgroundTaskAsync(CancellationTokenSource? cts, Task? task, string name)
    {
        if (cts is null)
            return;
        cts.Cancel();
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { _trace?.Invoke($"{name} ended with {ex.GetType().Name}: {ex.Message}"); }
        }
        cts.Dispose();
    }

    /// <summary>
    /// Owner for delayed/retry one-shot work. The start decision and
    /// publication are serialized against DisposeAsync, so no new one-shot can
    /// escape after the session has entered its terminal lifetime.
    /// </summary>
    private void StartOwnedOneShot(Func<Task> start)
    {
        ArgumentNullException.ThrowIfNull(start);

        Task task;
        lock (_ownedOneShotSync)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            task = start();
            _ownedOneShotTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_ownedOneShotSync)
                    _ownedOneShotTasks.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DrainOwnedOneShotTasksAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_ownedOneShotSync)
                tasks = _ownedOneShotTasks.ToArray();

            if (tasks.Length == 0)
                return;

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_gameplayLifetimeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _trace?.Invoke($"owned one-shot work ended with {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // InventoryTransaction_Start 00690AA0 starts nothing for a NO_SLOT source
    // or Num=0 (ReportFailIfTriggered, no packet); the UI action simply ends.
    private static bool StartsInventoryTransaction(uint fromSlot, uint count)
        => fromSlot != uint.MaxValue && count != 0;

    private uint ReadVendorWholeStackCount(Net7VendorTransactionPath path, uint sourceSlot)
    {
        bool buy = path is Net7VendorTransactionPath.VendorWindowBuyStack
            or Net7VendorTransactionPath.ShortcutBuyStack;
        if (buy)
        {
            // 005A02E0 -> 0068AAA0: a present vendor-slot ItemTemplateID other than
            // -1/-2 resolves the client item definition, whose +0x50 is the 0x0025
            // MaxStack (007371B0 -> ConstructClientItemDefinition 00831340). Without a
            // definition the stack request is one item.
            Net7AuxDataProperty? itemId = World.LocalPlayerAuxData.Properties.FirstOrDefault(
                p => p.Path == $"LocalPlayerAuxData.VendorInventory.{sourceSlot}.ItemTemplateID");
            return itemId is { State: not 0, Value: int templateId } && templateId is not (-1 or -2)
                && ItemTemplates.TryGet(unchecked((uint)templateId), out Net7ItemTemplate? template) && template is not null
                ? template.MaxStack
                : 1;
        }

        // 005A03B0/005A0740 -> 00689D20: the cargo slot's StackCount value, 0 when
        // the slot is not in the collection (no transaction then follows).
        Net7AuxDataSchema? ship = World.LocalShipObjectId is uint localShipObjectId
            ? World.Objects.FindFirst(localShipObjectId)?.AuxData
            : null;
        Net7AuxDataProperty? stack = ship?.Properties.FirstOrDefault(
            p => p.Path == $"ShipAuxData.Inventory.Cargo.{sourceSlot}.StackCount");
        return stack?.Value is int stackCount ? unchecked((uint)stackCount) : 0;
    }

    private Net7InventoryTransferIdentity ResolveInventoryTransferIdentity(Net7InventoryTransferIdSource source)
        => source switch
        {
            // 00690D20 reads SClient+1130 for the first 0x0027 word; 00733660/00726A00 show that
            // field is written only by incoming 0x0047 Client Ship (INV-D102; it is not a
            // selected-target cache). ClientConstruct leaves it 0 until 0x0047 arrives.
            Net7InventoryTransferIdSource.ClientShip => new(ClientShipTransferId, source),
            Net7InventoryTransferIdSource.LocalPlayer => new(RequireLocalPlayerId(), source),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

    private Task<bool> SendInventoryTransferPlanAsync(Net7InventoryTransferPlan plan, CancellationToken cancellationToken)
        => TrySendGameplayAsync(0x0027, Net7GameplayWire.BuildInventoryTransfer(
            plan.Identity.Id, plan.FromInventory, plan.FromSlot, plan.ToInventory, plan.ToSlot, plan.Count), cancellationToken);

    /// <summary>SClient+1130 as the 0x0027/0x005D/0x0029 producers read it: the 0x0047 Client Ship, 0 before one.</summary>
    public uint ClientShipTransferId => World.ClientShipId ?? 0;

    private uint RequireLocalPlayerId()
        => World.LocalPlayerId ?? throw new InvalidOperationException("Gameplay command requires a materialized local player.");

    // Retail 0037 installs SClient+112C; Start installs the distinct +1138 value.
    // Keep the established pre-assignment path, but never replace an assigned ID with Start.
    private uint RequireLocalAvatarObjectId() => World.ClientAvatarId ?? RequireLocalPlayerId();

    private uint RequireLocalShipObjectId()
        => World.LocalShipObjectId ?? throw new InvalidOperationException("Gameplay command requires a materialized local ship object.");

    private uint RequireStartSessionValue()
        => PlayerId ?? throw new InvalidOperationException("Gameplay command requires the sector Start/session value.");

    private uint RequireCurrentTargetId()
        => World.CurrentTargetId ?? throw new InvalidOperationException("Gameplay command requires a current authoritative target.");

    private uint RequireLocalSelectedTargetId()
        => Gameplay.LocalSelectedTargetId
           ?? throw new InvalidOperationException("Gameplay command requires a current local selected-target/input identity.");

    private Task<bool> SendGroupRequestPlanAsync(Net7GroupRequestPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.ShouldSend)
        {
            if (!string.IsNullOrEmpty(plan.LocalError))
                _trace?.Invoke($"group local: {plan.LocalError}");
            return Task.FromResult(false);
        }
        return TrySendGameplayAsync(plan.Opcode, plan.Payload, cancellationToken);
    }

    private async Task<bool> TrySendGameplayAsync(
        ushort opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Net7GameplayProvenance.RequireImplemented(opcode);
        RoutedSectorUdpChannel? routed = _routed;
        if (routed is null || !_transportState.TryGetActiveGameplayRoute(out _, out _))
        {
            _trace?.Invoke($"gameplay 0x{opcode:X4} suppressed: no active gameplay route");
            return false;
        }

        try
        {
            await routed.SendToActiveAsync(opcode, payload, cancellationToken).ConfigureAwait(false);
            if (opcode == 0x002C && Net7GameplayWire.DecodeAction(payload.Span) is {Action: >= 0x14 and <= 0x18} tradeAction)
                _trace?.Invoke($"Trade TX action=0x{tradeAction.Action:X2} actor=0x{tradeAction.GameId:X8} partner=0x{tradeAction.Target:X8} value={tradeAction.OptionalVar} socket-send-completed");
            if (opcode == 0x004E && payload.Length == 9 && payload.Span[8] is >= 6 and <= 9)
                _trace?.Invoke($"Station jobs TX opcode=004E payload={Convert.ToHexString(payload.Span)} socket-send-completed");
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            _trace?.Invoke($"gameplay 0x{opcode:X4} attempt failed/suppressed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TrySendFixedActionHandoffAsync(
        ushort opcode,
        ReadOnlyMemory<byte> payload,
        uint ownerFixedSessionId,
        int ownerGameplayGeneration,
        CancellationToken cancellationToken)
    {
        // Retries are owned by the Action's original session. Even if a
        // transition races this check, the explicit header below remains the
        // captured old ID rather than borrowing a newly installed FixedSessionId.
        if (ownerFixedSessionId == 0
            || Volatile.Read(ref _gameplayGeneration) != ownerGameplayGeneration
            || FixedSessionId != ownerFixedSessionId)
        {
            return false;
        }

        MasterControlUdpChannel? master = _master;
        if (master is null)
            return false;

        try
        {
            await master.SendFixedAsync(opcode, ownerFixedSessionId, payload, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            _trace?.Invoke($"fixed Action handoff 0x{opcode:X4} attempt failed/suppressed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TrySendFixedLeaveHandoffAsync(
        ushort opcode,
        ReadOnlyMemory<byte> payload,
        uint ownerFixedSessionId,
        int ownerGameplayGeneration,
        CancellationToken cancellationToken)
    {
        // A delayed leave copy is session-owned. Do not re-read a new
        // fixed ID after a teardown/handoff and accidentally relabel the old
        // 9-byte request as traffic for the replacement session.
        if (ownerFixedSessionId == 0
            || Volatile.Read(ref _gameplayGeneration) != ownerGameplayGeneration
            || FixedSessionId != ownerFixedSessionId)
        {
            return false;
        }

        MasterControlUdpChannel? master = _master;
        if (master is null)
            return false;

        try
        {
            await master.SendFixedAsync(opcode, ownerFixedSessionId, payload, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            _trace?.Invoke($"fixed leave-starbase handoff 0x{opcode:X4} attempt failed/suppressed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TrySendPositionSnapshotAsync(CancellationToken cancellationToken)
    {
        Net7GameplayProvenance.RequireImplemented(0x1004);
        MasterControlUdpChannel? master = _master;
        uint fixedSessionId = FixedSessionId;
        // Direct Move/Warp trigger semantics are intentionally
        // independent from the ordinary active-sector forwarding gate. Retail
        // asks the master/control snapshot helper even when the sector send is
        // suppressed. Periodic MVAS retains its stronger gate at the caller.
        if (master is null || fixedSessionId == 0)
            return false;

        Net7PositionFeedbackSample? sample = _movement.Capture(World);
        if (sample is null || !_movement.TrySelectSnapshot(sample, out bool full))
            return false;

        try
        {
            await master.SendFixedAsync(
                0x1004,
                fixedSessionId,
                Net7GameplayWire.BuildPositionFeedback(sample, full),
                cancellationToken).ConfigureAwait(false);
            _trace?.Invoke($"MOVE-04 position snapshot -> fixed 0x1004 {(full ? "full" : "compact")}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            _trace?.Invoke($"fixed 0x1004 attempt failed/suppressed: {ex.Message}");
            return false;
        }
    }

    private void StartHandoffRetry(Net7HandoffRetryOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        StartOwnedOneShot(() =>
        {
            lock (_handoffRetrySync)
                _handoffRetries.Add(operation);
            return RunHandoffRetryAsync(operation);
        });
    }

    private async Task RunHandoffRetryAsync(Net7HandoffRetryOperation operation)
    {
        try
        {
            _ = await operation.RunAsync(_gameplayLifetimeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_gameplayLifetimeCts.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_handoffRetrySync)
                _handoffRetries.Remove(operation);
        }
    }

    private void ObserveServerHandoffRetries()
    {
        lock (_handoffRetrySync)
        {
            foreach (Net7HandoffRetryOperation operation in _handoffRetries)
                operation.ObserveServerHandoff();
        }
    }

    private void InitializeReliabilityAndKeepalive()
    {
        if (_routed is null || _master is null)
            throw new InvalidOperationException("Reliability initialization requires both UDP channels.");
        if (_reliability is not null || _keepalive is not null)
            throw new InvalidOperationException("Reliability/keepalive state is already initialized.");

        _reliability = new Net7ReliabilityEngine(
            _options,
            _clock,
            () => _gameplayClientTargetActive,
            TrySendActiveReliabilityAsync,
            HandleDecodedSpecialAsync,
            DispatchGameplayPacketAsync,
            _trace,
            _processCompatibilityState,
            SetPresentationBatch);

        _keepalive = new Net7KeepaliveController(
            _clock,
            TrySendSectorKeepaliveAsync,
            TrySendMasterKeepaliveAsync,
            () => _transportState.ControlSessionId != 0 && _transportState.GameplayForwardingActive && Route is not null,
            ServerFrequencyMultiplier,
            _trace);

        if ((_options.FeatureFlags & Net7FeatureFlags.UdpPacketOptimisation) != 0)
            _packetOptimisation = new Net7PacketOptimisationTracker();

        // The dormant active-reset primitive has its own exact
        // 20 x 4 x 250 ms current envelope. Do not inherit tunable Master-login
        // retry options merely because the cadence happens to match.
        _loginReset = new Net7LoginResetController(
            _clock,
            TrySendLoginReset3006Async,
            trace: _trace);

        StartKeepaliveLoop();
    }

    private async Task<bool> TrySendActiveReliabilityAsync(
        ushort opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        RoutedSectorUdpChannel? routed = _routed;
        if (routed is null || !_transportState.TryGetActiveGameplayRoute(out _, out _))
            return false;
        try
        {
            await routed.SendToActiveAsync(opcode, payload, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            _trace?.Invoke($"active-sector UDP 0x{opcode:X4} attempt failed/suppressed: {ex.Message}");
            return false;
        }
    }

    private async Task<Net7DecodedPacketDisposition> HandleDecodedSpecialAsync(
        Packet packet,
        CancellationToken cancellationToken)
    {
        if (packet.Header.Opcode == 0x2018)
        {
            HandleAdvancedObjectA(packet.Payload, "decoded-inner 0x2018");
            return Net7DecodedPacketDisposition.Consumed;
        }

        if (packet.Header.Opcode == 0x2019)
        {
            HandleAdvancedObjectB(packet.Payload, "decoded-inner 0x2019");
            return Net7DecodedPacketDisposition.Consumed;
        }

        if (Net7DecodedSpecialOwnership.IsGameplayDomainSpecial(packet.Header.Opcode))
        {
            await HandleGameplayExtensionAsync(packet, cancellationToken).ConfigureAwait(false);
            _trace?.Invoke($"decoded-inner 0x{packet.Header.Opcode:X4} -> ENB-06 native gameplay-domain owner");
            return Net7DecodedPacketDisposition.Consumed;
        }

        switch (packet.Header.Opcode)
        {
            case 0x2011:
                CompleteSectorLogin("decoded-inner 0x2011");
                return Net7DecodedPacketDisposition.Consumed;

            case 0x100A:
                _reliability?.RequestServerPacketProcessingStop();
                RequestServerDirectedBroadShutdown(new Net7ServerShutdownException());
                return Net7DecodedPacketDisposition.Consumed;

            default:
                return Net7DecodedPacketDisposition.Unhandled;
        }
    }

    private void HandleAdvancedObjectA(ReadOnlyMemory<byte> payload, string source)
    {
        if (!_transportState.GameplayForwardingActive)
        {
            _trace?.Invoke($"{source} advanced-object A consumed without materialization: no active gameplay context");
            return;
        }

        Net7AdvancedObjectA update;
        try
        {
            update = Net7WorldWire.DecodeAdvancedObjectA(payload.Span);
        }
        catch (InvalidDataException ex)
        {
            // Malformed/truncated translator input must fail
            // closed without recreating Net7Proxy's signed-length/stack hazards.
            // This server extension has no ACK/retry transaction of its own, so
            // consume a malformed frame before it can mutate world state.
            _trace?.Invoke($"{source} advanced-object A rejected safely: {ex.Message}");
            return;
        }

        MutateWorldAndPublish(() => World.ApplyAdvancedObjectA(update));
        _trace?.Invoke($"{source} advanced-object A -> WORLD-05 semantic materialization");
    }
    public Task<bool> TransferManufacturingItemAsync(uint fromInventory, uint fromSlot, uint toInventory, uint toSlot, uint count)
    {
        // 0059F470 maps terminal RESULT/OVER to wire inventory 12/14;
        // 00690D20 sends the ordinary inventory transaction with +1130 identity.
        if (!Gameplay.ManufacturingDomain.IsTerminalOpen || count == 0 || fromSlot == uint.MaxValue ||
            !(fromInventory == 1 && toInventory is 12 or 14 && toSlot == 0 ||
              fromInventory is 12 or 14 && fromSlot == 0 && toInventory == 1) ||
            Gameplay.ManufacturingDomain.AnalysisTransfersInhibited) return Task.FromResult(false);
        return SendInventoryTransferPlanAsync(Net7InventoryDomain.CreateTransfer(
            new(ClientShipTransferId, Net7InventoryTransferIdSource.ClientShip),
            fromInventory, fromSlot, toInventory, toSlot, count), default);
    }

    private void ConsumeDirectOuterAdvancedObjectA(ReadOnlyMemory<byte> payload, string source)
    {
        if (!_transportState.GameplayForwardingActive)
        {
            _trace?.Invoke($"{source} consumed without materialization: no active gameplay context");
            return;
        }

        Net7AdvancedObjectA update;
        try
        {
            // Current Net7Proxy does parse direct-outer 0x2018 and builds the
            // synthetic retail bundle, but its caller discards the generated
            // length and never flushes that scratch buffer. Decode here so a
            // malformed payload still fails closed without giving this ingress
            // form the decoded-inner world-materialization semantics.
            update = Net7WorldWire.DecodeAdvancedObjectA(payload.Span);
        }
        catch (InvalidDataException ex)
        {
            _trace?.Invoke($"{source} advanced-object A rejected safely: {ex.Message}");
            return;
        }

        _trace?.Invoke(
            $"{source} advanced-object A 0x{update.GameId:X8} consumed without materialization: current Net7Proxy scratch bundle is not flushed");
    }

    private void HandleAdvancedObjectB(ReadOnlyMemory<byte> payload, string source)
    {
        // Special translation only generates its retail bundle when an
        // active gameplay-client target exists. The decoded special remains
        // consumed when that target is absent, but it must not materialize world
        // state merely because the numeric opcode is recognized.
        if (!_transportState.GameplayForwardingActive)
        {
            _trace?.Invoke($"{source} advanced-object B consumed without materialization: no active gameplay context");
            return;
        }

        Net7AdvancedObjectB update;
        try
        {
            update = Net7WorldWire.DecodeAdvancedObjectB(payload.Span);
        }
        catch (InvalidDataException ex)
        {
            // This closes the proxy string parser's signed-length/overflow
            // hazards as bugs, not protocol. Reject malformed special payloads
            // before any partial Create/Relationship/Position/AuxData state and
            // do not invent a 0x2019-specific ACK/retry transaction.
            _trace?.Invoke($"{source} advanced-object B rejected safely: {ex.Message}");
            return;
        }

        MutateWorldAndPublish(() => World.ApplyAdvancedObjectB(update));
        _trace?.Invoke($"{source} advanced-object B -> WORLD-06 semantic materialization");
    }

    private Task HandleGameplayExtensionAsync(Packet packet, CancellationToken cancellationToken)
    {
        Net7GameplayProvenance.RequireImplemented(packet.Header.Opcode);
        if (!_transportState.GameplayForwardingActive)
        {
            _trace?.Invoke($"gameplay extension 0x{packet.Header.Opcode:X4} consumed without materialization: no active gameplay context");
            return Task.CompletedTask;
        }

        return packet.Header.Opcode switch
        {
            0x2012 => HandleProspectExtensionAsync(packet.Payload, cancellationToken),
            0x2013 or 0x2014 => HandleTractorExtensionAsync(packet.Header.Opcode, packet.Payload, cancellationToken),
            0x2025 => HandleCDataExtensionAsync(packet.Payload),
            0x202A => HandleWarpCompletionExtensionAsync(packet.Payload, cancellationToken),
            _ => throw new InvalidOperationException($"Opcode 0x{packet.Header.Opcode:X4} has no ENB-06 gameplay extension owner."),
        };
    }

    private Task HandleProspectExtensionAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        Net7ProspectExtension prospect = Net7GameplayWire.DecodeProspectExtension(payload.Span);
        bool local = prospect.SourceId == FixedSessionId;
        Gameplay.RecordProspect(prospect, local);
        uint clientTime = _clock.GetMilliseconds();
        uint effectiveTimestamp = World.Time.ConvertServerTimestamp(prospect.Timestamp != 0 ? prospect.Timestamp : clientTime);
        lock (_worldStateSync)
        {
            if (local)
            {
                World.MaterializeLocalTimePropertyFromWireTimestamp(
                    Net7AuxDataPropertyIds.LocalPlayerSkill41LastActivation,
                    prospect.Timestamp);
            }

            Gameplay.AddSyntheticEffect(new Net7EffectState(
                prospect.EffectId,
                prospect.SourceId,
                prospect.TargetId,
                0x00BF,
                effectiveTimestamp,
                0,
                string.Empty,
                0x0003,
                true, GeometryKind: Net7EffectGeometryKind.ObjectToObject));

            if (local)
                NotifyWorldStateChanged(clientTime);
            else
                NotifyPresentationStateChanged(clientTime);
        }

        if (local)
            Gameplay.AddMessage(new Net7MessageState(0, "Prospect ability activated."));

        StartOwnedOneShot(() => RemoveEffectAfterDelayAsync(
            prospect.EffectId, ClampSignedDuration(prospect.DurationMilliseconds), Volatile.Read(ref _gameplayGeneration), cancellationToken));
        return Task.CompletedTask;
    }

    private Task HandleTractorExtensionAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        Net7TractorExtension tractor = Net7GameplayWire.DecodeTractorExtension(opcode, payload.Span);
        bool local = tractor.SourceId == FixedSessionId;
        Gameplay.RecordTractor(tractor, local);

        lock (_worldStateSync)
        {
            if (local && opcode == 0x2013)
            {
                // Exact local Validity fixture. These are native AuxData
                // state changes now, not a synthetic retail 0x001B bridge packet.
                World.MaterializeLocalRaw4Property(
                    Net7AuxDataPropertyIds.LocalPlayerSkillAbility0Validity, TractorAbilityValidityRawValue);
                World.MaterializeLocalRaw4Property(
                    Net7AuxDataPropertyIds.LocalPlayerSkillAbility28Validity, TractorAbilityValidityRawValue);
                Gameplay.AddCameraEvent(3, tractor.BassetId);
            }

            Net7WorldObject basset = World.MaterializeCreate(new Net7CreateUpdate(
                tractor.BassetId,
                1.0f,
                unchecked((short)tractor.BaseAsset),
                4,
                0f,
                0f,
                0f));

            if (local && opcode == 0x2014)
                Gameplay.AddCameraEvent(3, tractor.BassetId);
            else if (local && opcode == 0x2013)
                Gameplay.AddCameraEvent(3, tractor.BassetId);

            // The proxy's synthesized 0x000B passes through the ordinary retail
            // effect decoder: its timestamp is converted from server to client
            // time. Keep the component's separate wire timestamp unchanged.
            uint effectTimestamp = World.Time.ConvertWithoutDrift(
                tractor.Timestamp != 0 ? tractor.Timestamp : _clock.GetMilliseconds());
            int effectDuration = tractor.DurationMilliseconds == 0
                ? 0
                : unchecked((short)(tractor.DurationMilliseconds & 0xFFFF));
            Gameplay.AddSyntheticEffect(new Net7EffectState(
                tractor.EffectId,
                tractor.SourceId,
                tractor.BassetId,
                2,
                effectTimestamp,
                effectDuration,
                "TRACTOR",
                tractor.DurationMilliseconds == 0 ? (ushort)0x0003 : (ushort)0x0007,
                true, GeometryKind: Net7EffectGeometryKind.ObjectToObject) { EndWithTarget = true });

            World.MaterializeRelationship(new Net7RelationshipUpdate(tractor.BassetId, 2, 0));

            // Exact Variant-A product order is Create -> Effect ->
            // Relationship -> Name -> Component. The old integrated path assigned
            // Name immediately after Create, making an intermediate state visible
            // that retail never materializes.
            if (opcode == 0x2013)
                MaterializeTractorBassetName(basset, tractor.Name);

            uint componentTimestamp = tractor.Timestamp != 0 ? tractor.Timestamp : _clock.GetMilliseconds();
            World.MaterializeComponent(new Net7ComponentPositionUpdate(
                tractor.BassetId,
                componentTimestamp,
                tractor.Position,
                Quaternion.Identity,
                Vector3.Zero,
                9.8f,
                tractor.TractorSpeed,
                tractor.SourceId,
                tractor.EffectId));

            // Exact Variant-B order is Create -> [Camera] -> Effect ->
            // Relationship -> Component -> Name -> [Camera]. Variant A keeps
            // the opposite Name-before-Component ordering.
            if (opcode == 0x2014)
                MaterializeTractorBassetName(basset, tractor.Name);

            if (local && opcode == 0x2014)
                Gameplay.AddCameraEvent(3, tractor.BassetId);

            NotifyWorldStateChanged();
        }
        StartOwnedOneShot(() => RemoveBassetAfterDelayAsync(
            tractor.BassetId, ClampSignedDuration(tractor.DurationMilliseconds), Volatile.Read(ref _gameplayGeneration), cancellationToken));
        return Task.CompletedTask;
    }

    private static void MaterializeTractorBassetName(Net7WorldObject basset, string sourceName)
    {
        int embeddedNul = sourceName.IndexOf('\0');
        basset.Name = embeddedNul >= 0 ? sourceName[..embeddedNul] : sourceName;
    }

    private Task HandleCDataExtensionAsync(ReadOnlyMemory<byte> payload)
    {
        uint recordId = Net7GameplayWire.DecodeCDataRequest(payload.Span);
        Net7CDataRecord? record = null;
        if (_cdata is not null)
            _ = _cdata.TryGetRecord(recordId, out record);

        // A missing local record makes the stock proxy send nothing.
        // In integrated mode that also means no client-side CData/materialized
        // state mutation. Preserve the prior successful record/template state.
        if (record is not null)
        {
            Gameplay.RecordCData(record);
            _ = ItemTemplates.MaterializeCDataRecord(recordId, record);
        }

        _trace?.Invoke(record is null
            ? $"ASSET-01 CData record {recordId} absent; local client state unchanged and no server response is generated"
            : $"ASSET-01 CData record {recordId} materialized from its embedded item definition ({record.Payload.Length} bytes)");
        return Task.CompletedTask;
    }

    private Task HandleWarpCompletionExtensionAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        Net7WarpCompletionExtension completion = Net7GameplayWire.DecodeWarpCompletion(payload.Span);
        StartOwnedOneShot(() => ApplyWarpCompletionAfterDelayAsync(
            completion, Volatile.Read(ref _gameplayGeneration), cancellationToken));
        return Task.CompletedTask;
    }

    private async Task RemoveEffectAfterDelayAsync(
        uint effectId, TimeSpan delay, int generation, CancellationToken ownerCancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_gameplayLifetimeCts.Token, ownerCancellationToken);
        try
        {
            await _clock.DelayAsync(delay, linked.Token).ConfigureAwait(false);
            if (generation == Volatile.Read(ref _gameplayGeneration))
            {
                lock (_worldStateSync)
                {
                    if (Gameplay.RemoveEffect(effectId))
                        NotifyPresentationStateChanged();
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
    }

    private async Task RemoveBassetAfterDelayAsync(
        uint bassetId, TimeSpan delay, int generation, CancellationToken ownerCancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_gameplayLifetimeCts.Token, ownerCancellationToken);
        try
        {
            await _clock.DelayAsync(delay, linked.Token).ConfigureAwait(false);
            if (generation == Volatile.Read(ref _gameplayGeneration))
            {
                lock (_worldStateSync)
                {
                    if (World.MaterializeRemove(bassetId) is not null)
                        NotifyWorldStateChanged();
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
    }

    private async Task ApplyWarpCompletionAfterDelayAsync(
        Net7WarpCompletionExtension completion,
        int generation,
        CancellationToken ownerCancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_gameplayLifetimeCts.Token, ownerCancellationToken);
        try
        {
            await _clock.DelayAsync(TimeSpan.FromMilliseconds(500), linked.Token).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _gameplayGeneration))
                return;
            uint gameId = FixedSessionId;
            if (gameId == 0 || !_transportState.GameplayForwardingActive)
                return;

            uint clientTime = _clock.GetMilliseconds();
            lock (_worldStateSync)
            {
                World.MaterializeAdvanced(new Net7AdvancedPositionUpdate(
                    0x0103,
                    gameId,
                    completion.Timestamp,
                    completion.Position,
                    completion.Orientation,
                    completion.MovementId,
                    0f,
                    0f,
                    0f,
                    0f,
                    -MathF.PI,
                    0f,
                    -1000f,
                    Vector3.Zero,
                    0f,
                    0f,
                    0f,
                    100),
                    clientTime);
                NotifyWorldStateChanged(clientTime);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
    }

    private static TimeSpan ClampSignedDuration(uint durationMilliseconds)
    {
        uint effective = unchecked((int)durationMilliseconds) < 200 ? 200u : durationMilliseconds;
        return TimeSpan.FromMilliseconds(effective);
    }

    private async Task<bool> TrySendLoginReset3006Async(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        RoutedSectorUdpChannel? routed = _routed;
        if (routed is null)
            return false;
        try
        {
            await routed.SendNormalAsync(
                _options.MasterHandoffUdpPort,
                0x3006,
                payload,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            _trace?.Invoke($"routed 0x3006 reset attempt failed/suppressed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TrySendSectorKeepaliveAsync(CancellationToken cancellationToken)
    {
        RoutedSectorUdpChannel? routed = _routed;
        int? port = _transportState.ConfirmedGameplayPort;
        if (routed is null || port is null)
            return false;

        try
        {
            // This samples keepalive eligibility once before invoking the
            // two-send helper. Do not introduce a second forwarding/route gate
            // between the sector and master attempts. The sector helper itself
            // reads the retained dynamic gameplay port and uses the normal
            // sequenced builder.
            await routed.SendNormalAsync(
                port.Value,
                0x3005,
                ReadOnlyMemory<byte>.Empty,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            _trace?.Invoke($"sector 0x3005 failed/suppressed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TrySendMasterKeepaliveAsync(CancellationToken cancellationToken)
    {
        MasterControlUdpChannel? master = _master;
        if (master is null)
            return false;

        // This rereads the master/control fixed/session ID only after the
        // sector helper returns and then unconditionally attempts the fixed-ID
        // master keepalive. The outer keepalive predicate is not re-evaluated.
        uint fixedSessionId = _transportState.ControlSessionId;
        try
        {
            await master.SendFixedAsync(0x3005, fixedSessionId, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            _trace?.Invoke($"master 0x3005 failed/suppressed: {ex.Message}");
            return false;
        }
    }

    private void StartKeepaliveLoop()
    {
        if (_keepalive is null)
            throw new InvalidOperationException("Keepalive controller is not initialized.");
        if (_keepaliveLoopTask is not null)
            throw new InvalidOperationException("Keepalive cadence loop is already running.");

        var cts = new CancellationTokenSource();
        _keepaliveLoopCts = cts;
        _keepaliveLoopTask = Task.Run(() => RunKeepaliveLoopAsync(cts.Token));
    }

    private async Task RunKeepaliveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Net7KeepaliveController? keepalive = _keepalive;
                if (keepalive is null)
                    return;

                // The original monitor sleeps at the loop tail. Starting the clean
                // scheduler with one cadence sleep models the already-existing
                // monitor phase without inventing a second exact timer. The due
                // test itself remains the strict >30,000-ms threshold.
                int multiplier = keepalive.CurrentLoopMultiplier;
                ServerFrequencyMultiplier = multiplier;
                await _clock.DelayAsync(
                    TimeSpan.FromMilliseconds(multiplier * 250),
                    cancellationToken).ConfigureAwait(false);

                // Current MVAS-loop order: position feedback is evaluated
                // before periodic keepalive on each monitor iteration. Unlike the
                // synchronous Move/Warp trigger, the periodic worker has the
                // stronger active-route eligibility gate.
                if (_transportState.ControlSessionId != 0 && _transportState.GameplayForwardingActive && Route is not null)
                    await TrySendPositionSnapshotAsync(cancellationToken).ConfigureAwait(false);
                await keepalive.EvaluateOnceAsync(cancellationToken).ConfigureAwait(false);
                ServerFrequencyMultiplier = keepalive.CurrentLoopMultiplier;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ApplyServerFrequency(uint requested)
    {
        Net7KeepaliveController? keepalive = _keepalive;
        if (keepalive is null)
        {
            ServerFrequencyMultiplier = requested is >= 1 and <= 20 ? checked((int)requested) : 2;
            return;
        }

        keepalive.ObserveServerFrequency(requested);
        ServerFrequencyMultiplier = keepalive.CurrentLoopMultiplier;
    }

    private void StartAccountResponsePump()
    {
        if (_account is null)
            throw new InvalidOperationException("Account TCP is not open.");
        if (_accountPumpTask is not null)
            throw new InvalidOperationException("Account role-10 response pump is already running.");

        var cts = new CancellationTokenSource();
        _accountPumpCts = cts;
        _accountPumpTask = Task.Run(() => PumpAccountResponsesAsync(cts.Token));
    }

    private async Task PumpAccountResponsesAsync(CancellationToken cancellationToken)
    {
        Net7TcpConnection? account = _account;
        if (account is null)
            return;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Packet packet = await account.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (packet.Header.Opcode == 0x0075)
                {
                    // Net7Proxy's role-10 dispatcher @ 0x00401480 discards this
                    // opcode, but the current server can deliver its exact
                    // fixed-message 0x0075 compatibility form here instead of
                    // outer UDP 0x200E. Recognize that proven byte shape only
                    // for a pending create; preserve every other role-10 0x0075
                    // as an unrelated packet.
                    bool createPending;
                    lock (_accountResponseSync)
                        createPending = _avatarManagementIsCreate &&
                            _accountRosterWaiter is { Task.IsCompleted: false };
                    if (createPending)
                    {
                        // The first eight bytes suffice to distinguish a direct
                        // code pair from other forms without logging a name or
                        // the remainder of an arbitrary server payload.
                        ReadOnlySpan<byte> prefix = packet.Payload.Span[..Math.Min(8, packet.Payload.Length)];
                        _trace?.Invoke($"account router <- server TCP role-10 0x0075 during avatar-create: len={packet.Payload.Length} prefix8={Convert.ToHexString(prefix)}");
                        if (Net7GlobalError.TryDecodeAccountTcp0075(packet.Payload.Span, out Net7GlobalError error))
                        {
                            _trace?.Invoke($"account router accepted fixed-message TCP 0x0075 create rejection: code={error.Code}");
                            RouteGlobalError(error);
                        }
                        else
                            _trace?.Invoke("account router ignored unrecognized TCP 0x0075 create response form");
                    }
                    else
                        _trace?.Invoke($"account router ignored server TCP role-10 0x0075 outside avatar-create: len={packet.Payload.Length}");
                    continue;
                }

                switch (packet.Header.Opcode)
                {
                    case 0x0070:
                    {
                        // Decode completely before publication.
                        // The TCP connection supplied a packet-owned byte[] rather
                        // than Net7Proxy's shared scratch mailbox.
                        if (TryDecodeRoster(packet) is not Net7AvatarRoster roster)
                            break;
                        TaskCompletionSource<Net7AvatarRoster>? waiter;
                        lock (_accountResponseSync)
                        {
                            Roster = roster;
                            waiter = _accountRosterWaiter;
                            if (waiter is not null && !waiter.Task.IsCompleted)
                            {
                                // The authoritative roster is a terminal
                                // success for the active avatar-management transaction.
                                // Retire both competing terminal owners atomically before
                                // either asynchronous continuation can race a later EOF or
                                // GlobalError into the already-successful transaction.
                                _accountRosterWaiter = null;
                                _avatarManagementErrorWaiter = null;
                                _avatarManagementIsCreate = false;
                            }
                        }

                        _trace?.Invoke($"account router <- TCP 0x0070 authoritative roster ({roster.Slots.Count(s => !s.IsEmpty)} occupied)");
                        RosterUpdated?.Invoke(roster);
                        waiter?.TrySetResult(roster);
                        break;
                    }

                    case 0x006F:
                    case 0x2009:
                    {
                        // Proxy 00414A60 reads the fixed ID at +0x14 of its shared
                        // mailbox; a shorter response leaves that read undefined
                        // (stale prior mailbox bytes). Safety rule: ignore the
                        // packet and keep waiting, as for any undecodable read.
                        if (packet.Payload.Length < Net7AvatarSelectionResponse.MinimumPayloadSize)
                        {
                            _trace?.Invoke($"account router ignored short TCP 0x{packet.Header.Opcode:X4} avatar-selection response len={packet.Payload.Length}");
                            break;
                        }
                        TaskCompletionSource<Packet>? waiter;
                        lock (_accountResponseSync)
                            waiter = _accountSelectionWaiter;
                        if (waiter is not null)
                            waiter.TrySetResult(packet);
                        else
                            _trace?.Invoke($"account router discarded unrelated TCP 0x{packet.Header.Opcode:X4} with no active selection owner");
                        break;
                    }

                    case 0x2001:
                        _trace?.Invoke("account router discarded unrelated TCP 0x2001 with no account-valid transaction owner");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
        }
        catch (Exception ex)
        {
            bool pending = HasPendingAccountResponseWaiter();
            CompleteAccountResponseWaitersWithException(ex);

            // Receive EOF, framing failure, or socket failure
            // ends this published TCP-3807 owner. The pump cannot call the normal
            // disposer because that would await itself, so atomically unpublish
            // its captured connection and dispose that connection directly.
            await RetireAccountConnectionFromPumpAsync(account).ConfigureAwait(false);

            if (pending || ex is InvalidDataException)
                SignalFatalLogin(ex);
            else
                _trace?.Invoke($"account role-10 pump ended with no active transaction owner: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Safety rule: a roster whose fixed structure runs past the payload has no
    // defined content (the client reads it into uninitialized packet storage).
    // It is consumed without effect; the transaction keeps waiting and the
    // session continues, as for every other undecodable inbound packet.
    private Net7AvatarRoster? TryDecodeRoster(Packet packet)
    {
        try { return Net7AvatarRosterCodec.Decode(packet.Payload.Span); }
        catch (InvalidDataException ex)
        {
            _trace?.Invoke($"account TCP 0x0070 roster consumed without effect: {ex.Message}");
            return null;
        }
    }

    private bool HasPendingAccountResponseWaiter()
    {
        lock (_accountResponseSync)
        {
            return (_accountSelectionWaiter is not null && !_accountSelectionWaiter.Task.IsCompleted)
                || (_accountRosterWaiter is not null && !_accountRosterWaiter.Task.IsCompleted);
        }
    }

    private TaskCompletionSource<Packet> InstallAccountSelectionWaiter()
    {
        var waiter = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_accountResponseSync)
        {
            if (_accountSelectionWaiter is not null || _accountRosterWaiter is not null || _avatarManagementErrorWaiter is not null)
                throw new InvalidOperationException("Another account transaction response owner is already installed.");
            _accountSelectionWaiter = waiter;
        }
        return waiter;
    }

    private void ClearAccountSelectionWaiter(TaskCompletionSource<Packet> waiter)
    {
        lock (_accountResponseSync)
        {
            if (ReferenceEquals(_accountSelectionWaiter, waiter))
                _accountSelectionWaiter = null;
        }
    }

    private (TaskCompletionSource<Net7AvatarRoster> Roster, TaskCompletionSource<Net7GlobalError> Error)
        InstallAvatarManagementWaiters(bool isCreate)
    {
        var roster = new TaskCompletionSource<Net7AvatarRoster>(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new TaskCompletionSource<Net7GlobalError>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_accountResponseSync)
        {
            if (_accountSelectionWaiter is not null || _accountRosterWaiter is not null || _avatarManagementErrorWaiter is not null)
                throw new InvalidOperationException("Another account transaction response owner is already installed.");
            _accountRosterWaiter = roster;
            _avatarManagementErrorWaiter = error;
            _avatarManagementIsCreate = isCreate;
        }
        return (roster, error);
    }

    private void ClearAvatarManagementWaiters(
        TaskCompletionSource<Net7AvatarRoster> roster,
        TaskCompletionSource<Net7GlobalError> error)
    {
        lock (_accountResponseSync)
        {
            if (ReferenceEquals(_accountRosterWaiter, roster))
                _accountRosterWaiter = null;
            if (ReferenceEquals(_avatarManagementErrorWaiter, error))
            {
                _avatarManagementErrorWaiter = null;
                _avatarManagementIsCreate = false;
            }
        }
    }

    private void CompleteAccountResponseWaitersWithException(Exception exception)
    {
        TaskCompletionSource<Packet>? selection;
        TaskCompletionSource<Net7AvatarRoster>? roster;
        TaskCompletionSource<Net7GlobalError>? error;
        lock (_accountResponseSync)
        {
            selection = _accountSelectionWaiter;
            roster = _accountRosterWaiter;
            error = _avatarManagementErrorWaiter;
        }

        selection?.TrySetException(exception);
        roster?.TrySetException(exception);
        error?.TrySetException(exception);
    }

    private async Task<Packet> WaitForAccountSelectionResponseAsync(
        TaskCompletionSource<Packet> waiter,
        string description,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.AccountResponseTimeout);
        Task timeout = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);
        Task completed = await Task.WhenAny(waiter.Task, _fatalLoginSignal.Task, timeout).ConfigureAwait(false);

        if (completed == waiter.Task)
        {
            timeoutCts.Cancel();
            return await waiter.Task.ConfigureAwait(false);
        }
        if (completed == _fatalLoginSignal.Task)
        {
            timeoutCts.Cancel();
            Exception fatal = await _fatalLoginSignal.Task.ConfigureAwait(false);
            throw fatal;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException($"Timed out after {_options.AccountResponseTimeout.TotalSeconds:0.#}s waiting for {description}.");
    }

    private async Task<Net7AvatarRoster> WaitForAvatarManagementRosterAsync(
        TaskCompletionSource<Net7AvatarRoster> rosterWaiter,
        TaskCompletionSource<Net7GlobalError> errorWaiter,
        string description,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.AccountResponseTimeout);
        Task timeout = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);
        Task completed = await Task.WhenAny(
            rosterWaiter.Task, errorWaiter.Task, _fatalLoginSignal.Task, timeout).ConfigureAwait(false);

        if (completed == rosterWaiter.Task)
        {
            timeoutCts.Cancel();
            return await rosterWaiter.Task.ConfigureAwait(false);
        }
        if (completed == errorWaiter.Task)
        {
            timeoutCts.Cancel();
            Net7GlobalError error = await errorWaiter.Task.ConfigureAwait(false);
            throw new Net7GlobalErrorException(error);
        }
        if (completed == _fatalLoginSignal.Task)
        {
            timeoutCts.Cancel();
            Exception fatal = await _fatalLoginSignal.Task.ConfigureAwait(false);
            throw fatal;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _trace?.Invoke($"avatar-management response timeout: accountPumpAlive={_accountPumpTask is { IsCompleted: false }} masterPumpAlive={_masterPumpTask is { IsCompleted: false }} routedPumpAlive={_routedPumpTask is { IsCompleted: false }} rosterCompleted={rosterWaiter.Task.IsCompleted} errorCompleted={errorWaiter.Task.IsCompleted}");
        throw new TimeoutException($"Timed out after {_options.AccountResponseTimeout.TotalSeconds:0.#}s waiting for {description}.");
    }

    private void RequireAvatarManagementReady()
    {
        if (_account is null || Roster is null || _accountPumpTask is null || _accountPumpTask.IsCompleted)
            throw new InvalidOperationException("Account login and the authoritative roster must complete before avatar management.");
        if (LoginState != Net7LoginState.CharacterSelection)
            throw new InvalidOperationException($"Avatar management requires CharacterSelection state; current state is {LoginState}.");
        if (FixedSessionId != 0)
            throw new InvalidOperationException("Avatar management is only available before a character fixed/session ID is committed.");
    }

    private void RouteGlobalError(Net7GlobalError error)
    {
        TaskCompletionSource<Net7GlobalError>? managementWaiter;
        lock (_accountResponseSync)
        {
            bool rosterStillPending = _accountRosterWaiter is not null && !_accountRosterWaiter.Task.IsCompleted;
            bool createAccountError = error.Source == Net7GlobalErrorSource.AccountTcp0075 && _avatarManagementIsCreate;
            managementWaiter = (error.Source == Net7GlobalErrorSource.OuterUdp200E || createAccountError) && rosterStillPending
                ? _avatarManagementErrorWaiter
                : null;
            if (managementWaiter is not null)
            {
                // GlobalError is the competing terminal result.
                // First terminal event to claim the response-owner lock wins; a
                // later authoritative roster remains valid resynchronization but
                // cannot retroactively turn this failed transaction into success.
                _accountRosterWaiter = null;
                _avatarManagementErrorWaiter = null;
                _avatarManagementIsCreate = false;
            }
        }

        error = error with
        {
            SessionContext = managementWaiter is not null
                ? Net7GlobalErrorSessionContext.AvatarManagement
                : ClassifyGlobalErrorSessionContext(),
        };
        GlobalErrorReceived?.Invoke(error);

        if (managementWaiter is not null)
        {
            _trace?.Invoke($"GlobalError routed independently to active avatar-management transaction: selector={error.Selector?.ToString() ?? "n/a"}");
            managementWaiter.TrySetResult(error);
            return;
        }

        // Direct 0x0075 remains asynchronous; it cannot satisfy an account or
        // avatar-selection transaction merely because it is a GlobalError.
        if (error.Source == Net7GlobalErrorSource.Direct0075 &&
            error.SessionContext is Net7GlobalErrorSessionContext.AccountLogin or
                Net7GlobalErrorSessionContext.CharacterSelection or
                Net7GlobalErrorSessionContext.AvatarSelection or
                Net7GlobalErrorSessionContext.Unspecified)
        {
            _trace?.Invoke($"Direct 0x0075 observed outside a gameplay-side login context ({error.SessionContext}); surfaced without transaction completion");
            return;
        }

        // Account-management errors are asynchronous. Once the
        // ordinary roster has been established and no selection/entry transaction
        // owns the error, surface it without poisoning CharacterSelection state.
        if (LoginState == Net7LoginState.CharacterSelection)
        {
            _trace?.Invoke($"GlobalError observed asynchronously in CharacterSelection: {error.Message}");
            return;
        }

        // The direct outer Global error is a compatibility
        // error form, not a gameplay-session shutdown control. Stock only forwards
        // it to an existing retail Global owner; absent that owner it has no
        // gameplay-side state consequence. Preserve the normalized event for UI/
        // diagnostics, but do not invent a fatal gameplay transition once sector
        // entry has completed (or when there is no active login context at all).
        if (error.Source == Net7GlobalErrorSource.OuterUdp200E &&
            error.SessionContext is Net7GlobalErrorSessionContext.Gameplay or
                Net7GlobalErrorSessionContext.Unspecified)
        {
            _trace?.Invoke($"Outer Global error observed outside an active Global/login owner ({error.SessionContext}); surfaced without session fault");
            return;
        }

        SignalFatalLogin(new Net7GlobalErrorException(error));
    }

    private Net7GlobalErrorSessionContext ClassifyGlobalErrorSessionContext()
        => LoginState switch
        {
            Net7LoginState.Bootstrapping => Net7GlobalErrorSessionContext.Bootstrap,
            Net7LoginState.AccountConnecting or
            Net7LoginState.Authenticating or
            Net7LoginState.LoadingRoster => Net7GlobalErrorSessionContext.AccountLogin,
            Net7LoginState.CharacterSelection => Net7GlobalErrorSessionContext.CharacterSelection,
            Net7LoginState.SelectingCharacter or
            Net7LoginState.CharacterSelected => Net7GlobalErrorSessionContext.AvatarSelection,
            Net7LoginState.MasterHandoff or
            Net7LoginState.SectorJoining => Net7GlobalErrorSessionContext.SectorEntry,
            Net7LoginState.SectorReady or
            Net7LoginState.SectorTransition => Net7GlobalErrorSessionContext.Gameplay,
            _ => Net7GlobalErrorSessionContext.Unspecified,
        };

    private void StartMasterPump()
    {
        if (_master is null)
            throw new InvalidOperationException("Master/control UDP is not open.");

        var cts = new CancellationTokenSource();
        _masterPumpCts = cts;
        _masterPumpTask = Task.Run(() => PumpMasterControlAsync(cts.Token));
    }

    private void StartRoutedPump()
    {
        if (_routed is null)
            throw new InvalidOperationException("Routed UDP is not open.");

        var cts = new CancellationTokenSource();
        _routedPumpCts = cts;
        _routedPumpTask = Task.Run(() => PumpRoutedAsync(cts.Token));
    }

    private async Task PumpMasterControlAsync(CancellationToken cancellationToken)
    {
        if (_master is null)
            return;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ServerUdpFrame frame = await _master.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                switch (frame.Opcode)
                {
                    case 0x1001:
                        _trace?.Invoke("master/control <- late UDP 0x1001 bootstrap acknowledgement consumed");
                        break;

                    case 0x2005:
                        await HandleFixedSessionAssignmentAsync(frame, controlChannel: true, cancellationToken).ConfigureAwait(false);
                        break;

                    case 0x2010:
                        await HandleDirectOuter2010Async(frame, "master/control UDP 0x2010", cancellationToken).ConfigureAwait(false);
                        break;

                    case 0x2011:
                        CompleteSectorLogin("direct outer master/control UDP 0x2011");
                        break;

                    case 0x200E:
                    {
                        // Safety rule (00414430 reads the selector unchecked):
                        // a short frame carries no defined selector.
                        if (frame.Payload.Length < 4)
                        {
                            _trace?.Invoke("master/control ignored short outer UDP 0x200E");
                            break;
                        }
                        Net7GlobalError error = Net7GlobalError.DecodeOuterUdp200E(
                            frame.Payload.Span, Net7GlobalErrorIngress.MasterControlUdpOuter);
                        _trace?.Invoke($"master/control <- outer UDP 0x200E len={frame.Payload.Length} selector={error.Selector}");
                        RouteGlobalError(error);
                        break;
                    }

                    case 0x1007:
                        if (frame.Payload.Length < 4)
                        {
                            // Safety rule: 0040EED0 reads stale receive-buffer bytes.
                            _trace?.Invoke("master/control ignored short UDP 0x1007");
                            break;
                        }
                        uint requested = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.Span[..4]);
                        ApplyServerFrequency(requested);
                        _trace?.Invoke($"server frequency <- UDP 0x1007 multiplier={ServerFrequencyMultiplier}");
                        break;

                    case 0x1009:
                        SignalFatalLogin(Net7IncompatibleVersionException.FromPayload(frame.Payload.Span));
                        CancelQuietly(_masterPumpCts);
                        break;

                    case 0x100A:
                        if (frame.Payload.Length < 4)
                        {
                            // Safety rule: 0040EB30 compares stale receive-buffer bytes.
                            _trace?.Invoke("master/control ignored short UDP 0x100A");
                            break;
                        }
                        uint shutdownId = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.Span[..4]);
                        if (shutdownId == _transportState.ControlSessionId)
                            RequestServerDirectedBroadShutdown(new Net7ServerShutdownException());
                        break;

                    case 0x100B:
                        RequestServerDirectedBroadShutdown(new Net7ServerStartingException());
                        break;

                    case 0x3005:
                        _keepalive?.ObserveInboundKeepalive();
                        _trace?.Invoke("master/control <- UDP 0x3005 keepalive accepted/ignored");
                        break;

                    case 0x2001:
                    case 0x2003:
                    case 0x200C:
                    case 0x3007:
                        _trace?.Invoke($"master/control <- UDP 0x{frame.Opcode:X4} communal-mailbox control had no transaction-correlated owner");
                        break;

                    case 0x2009:
                    case 0x2027:
                        _trace?.Invoke($"master/control <- UDP 0x{frame.Opcode:X4} cannot satisfy the routed Master/port transaction owner");
                        break;

                    case 0x2012:
                    case 0x2013:
                    case 0x2014:
                        try
                        {
                            await HandleGameplayExtensionAsync(Packet.ForOpcode(frame.Opcode, frame.Payload), cancellationToken).ConfigureAwait(false);
                        }
                        catch (InvalidDataException ex)
                        {
                            // Safety rule (undefined native reads): as on the routed channel,
                            // drop the malformed extension and keep receiving; Net7Proxy never
                            // disconnects on these.
                            _trace?.Invoke($"master/control UDP 0x{frame.Opcode:X4} rejected safely: {ex.Message}");
                            break;
                        }
                        _trace?.Invoke($"master/control UDP 0x{frame.Opcode:X4} -> ENB-06 direct translator owner");
                        break;

                    case 0x2018:
                        ConsumeDirectOuterAdvancedObjectA(
                            frame.Payload,
                            "direct outer master/control UDP 0x2018");
                        break;

                    case 0x2016:
                    case 0x201A:
                        _trace?.Invoke($"master/control <- UDP 0x{frame.Opcode:X4} login-cache stream is not owned by the routed reliability channel");
                        break;

                    default:
                        await DispatchGenericDirectOuterAsync(
                            frame, "master/control UDP", Net7GlobalErrorIngress.MasterControlUdpOuter, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SignalFatalLogin(ex);
        }
    }

    private async Task PumpRoutedAsync(CancellationToken cancellationToken)
    {
        RoutedSectorUdpChannel? routed = _routed;
        if (routed is null)
            return;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                (ServerUdpFrame frame, IPEndPoint sender) = await routed.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                _trace?.Invoke($"routed UDP <- {sender.Port} 0x{frame.Opcode:X4} len={frame.Payload.Length}");
                try
                {
                    await DispatchRoutedFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    // Safety rule (undefined native reads): drop the malformed frame and
                    // keep receiving, as the routed channel already does for datagrams
                    // that fail outer framing. Net7Proxy never disconnects on these.
                    _trace?.Invoke($"routed UDP 0x{frame.Opcode:X4} rejected safely: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_routed is null)
        {
            // Clean failure cleanup intentionally disposes the routed
            // gameplay transport. That owned teardown is not a second fatal cause.
        }
        catch (Exception ex)
        {
            SignalFatalLogin(ex);
        }
    }

    private async Task DispatchRoutedFrameAsync(ServerUdpFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Opcode)
        {
            case 0x1001:
                _trace?.Invoke("routed <- late UDP 0x1001 bootstrap acknowledgement consumed");
                return;

            case 0x2005:
                await HandleFixedSessionAssignmentAsync(frame, controlChannel: false, cancellationToken).ConfigureAwait(false);
                return;

            case 0x2009:
            {
                // Clean transaction boundary: stock's communal mailbox
                // could wake before a six-byte response was safe to consume. Do not
                // mutate the transaction owner until the proven consumed prefix exists.
                if (frame.Payload.Length < 6)
                {
                    _trace?.Invoke($"routed UDP 0x2009 rejected before Master-login completion: payload len={frame.Payload.Length}, need >=6");
                    return;
                }

                TaskCompletionSource<ServerUdpFrame>? waiter;
                lock (_waiterSync) waiter = _masterLoginWaiter;
                if (waiter is null)
                    _trace?.Invoke("routed UDP 0x2009 had no active Master-login owner and was not retained");
                else
                    waiter.TrySetResult(CloneFrame(frame));
                return;
            }

            case 0x2027:
            {
                // Clean transaction boundary: stock can publish the
                // communal ready latch even when fewer than the two bytes later
                // consumed by the confirmation helper were actually received.
                // Reject that malformed response before it can mutate/wake the
                // transaction-owned waiter.
                if (frame.Payload.Length < 2)
                {
                    _trace?.Invoke($"routed UDP 0x2027 rejected before sector-port completion: payload len={frame.Payload.Length}, need >=2");
                    return;
                }

                TaskCompletionSource<ServerUdpFrame>? waiter;
                lock (_waiterSync) waiter = _sectorPortWaiter;
                if (waiter is null)
                    _trace?.Invoke("routed UDP 0x2027 had no active sector-port owner and was not retained");
                else
                    waiter.TrySetResult(CloneFrame(frame));
                return;
            }

            case 0x2010:
                await HandleDirectOuter2010Async(frame, "routed UDP 0x2010", cancellationToken).ConfigureAwait(false);
                return;

            case 0x2011:
                CompleteSectorLogin("direct outer UDP 0x2011");
                return;

            case 0x200E:
            {
                Net7GlobalError error = Net7GlobalError.DecodeOuterUdp200E(
                    frame.Payload.Span, Net7GlobalErrorIngress.RoutedUdpOuter);
                _trace?.Invoke($"routed <- outer UDP 0x200E len={frame.Payload.Length} selector={error.Selector}");
                RouteGlobalError(error);
                return;
            }

            case 0x1007:
                if (frame.Payload.Length < 4)
                    throw new InvalidDataException("UDP 0x1007 frequency update is shorter than the required u32 payload.");
                uint requested = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.Span[..4]);
                ApplyServerFrequency(requested);
                return;

            case 0x1009:
                SignalFatalLogin(Net7IncompatibleVersionException.FromPayload(frame.Payload.Span));
                CancelQuietly(_routedPumpCts);
                return;

            case 0x100A:
                if (frame.Payload.Length < 4)
                    throw new InvalidDataException("UDP 0x100A shutdown control is shorter than the required u32 payload.");
                if (BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.Span[..4]) == FixedSessionId)
                    RequestServerDirectedBroadShutdown(new Net7ServerShutdownException());
                return;

            case 0x100B:
                RequestServerDirectedBroadShutdown(new Net7ServerStartingException());
                return;

            case 0x3005:
                _keepalive?.ObserveInboundKeepalive();
                return;

            case 0x2016:
            case 0x201A:
                if (_reliability is null)
                    throw new InvalidOperationException("Reliability engine is not initialized.");
                await _reliability.AcceptOuterAsync(frame, cancellationToken).ConfigureAwait(false);
                return;

            case 0x3007:
                _loginReset?.ObserveDirectOuter3007();
                _trace?.Invoke("routed <- UDP 0x3007 observed only by an active transaction-correlated reset owner");
                return;

            case 0x2001:
            case 0x2003:
            case 0x200C:
                // Only communal-mailbox publication is proven for these
                // direct outer controls. Clean EnbNext has no communal mailbox,
                // so they cannot complete an unrelated transaction by accident.
                _trace?.Invoke($"routed <- UDP 0x{frame.Opcode:X4} communal-mailbox control had no transaction-correlated owner");
                return;

            case 0x2012:
            case 0x2013:
            case 0x2014:
                await HandleGameplayExtensionAsync(Packet.ForOpcode(frame.Opcode, frame.Payload), cancellationToken).ConfigureAwait(false);
                _trace?.Invoke($"routed UDP 0x{frame.Opcode:X4} -> ENB-06 direct translator owner");
                return;

            case 0x2018:
                ConsumeDirectOuterAdvancedObjectA(
                    frame.Payload,
                    "direct outer routed UDP 0x2018");
                return;
        }

        // Generic direct fallback is form-specific: preserve the same
        // outer opcode/payload and then execute the common second-stage owner.
        // Numeric collisions such as direct 0x2025 must never inherit decoded-
        // inner meanings merely because the number matches.
        await DispatchGenericDirectOuterAsync(
            frame, "routed UDP", Net7GlobalErrorIngress.RoutedUdpOuter, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleFixedSessionAssignmentAsync(ServerUdpFrame frame, bool controlChannel, CancellationToken cancellationToken)
    {
        MasterControlUdpChannel master = _master
            ?? throw new InvalidOperationException("Master/control UDP is not open.");
        _transportState.ApplyFixedSessionControl(frame.FixedSessionId, controlChannel);
        _trace?.Invoke($"fixed/session control <- UDP 0x2005 id=0x{frame.FixedSessionId:X8}; sending normal 0x200F with resulting control ID");
        await master.SendNormalAsync(0x200F, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
    }

    private async Task DispatchGenericDirectOuterAsync(
        ServerUdpFrame frame,
        string channel,
        Net7GlobalErrorIngress globalErrorIngress,
        CancellationToken cancellationToken)
    {
        _trace?.Invoke($"{channel} 0x{frame.Opcode:X4} -> FULL-04 generic direct fallback");
        Packet packet = Packet.ForOpcode(frame.Opcode, frame.Payload);
        if (frame.Opcode == 0x0075)
        {
            // Direct retail-compatible 0x0075 is a genuine ordinary
            // packet form, distinct from outer 0x200E selector semantics.
            RouteGlobalError(Net7GlobalError.DecodeDirect0075(
                frame.Payload.Span, ingress: globalErrorIngress));
            GameplayPacketReceived?.Invoke(packet);
            return;
        }

        await DispatchGameplayPacketAsync(
            packet,
            "direct outer UDP",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DispatchGameplayPacketAsync(Packet packet, string source, CancellationToken cancellationToken)
    {
        try
        {
            await DispatchGameplayPacketUncontainedAsync(packet, source, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            // Safety rule (undefined native reads): a packet whose bytes cannot be
            // decoded is consumed with the state its decoder already applied; later
            // packets in the same stream and the session continue. Net7Proxy's
            // translator logs and returns on a malformed record (00401480) and never
            // disconnects; the retail client has no packet-validation disconnect.
            _trace?.Invoke($"0x{packet.Header.Opcode:X4} from {source} rejected safely: {ex.Message}");
        }
    }

    private async Task DispatchGameplayPacketUncontainedAsync(Packet packet, string source, CancellationToken cancellationToken)
    {
        if (packet.Header.Opcode is >= 0x0093 and <= 0x0096)
            _trace?.Invoke($"Station jobs RX opcode={packet.Header.Opcode:X4} bytes={packet.Payload.Length} prefix={Convert.ToHexString(packet.Payload.Span[..Math.Min(packet.Payload.Length, 256)])}");
        bool decodedInnerIngress = source is "decoded-inner" or "reassembled decoded-inner";
        bool secondStageDispatchIngress = decodedInnerIngress || source == "direct outer UDP";

        if (packet.Header.Opcode == 0x0075 && decodedInnerIngress)
        {
            // The special translator has already declined this opcode
            // and has admitted it through the ordinary retail range.
            // Normalize the direct packet without lending it outer-0x200E semantics.
            Net7GlobalErrorIngress ingress = source == "reassembled decoded-inner"
                ? Net7GlobalErrorIngress.ReassembledDecodedInner
                : Net7GlobalErrorIngress.DecodedInner;
            RouteGlobalError(Net7GlobalError.DecodeDirect0075(packet.Payload.Span, ingress: ingress));
            GameplayPacketReceived?.Invoke(packet);
            return;
        }

        // The packet-specific second-stage dispatcher is not used by the
        // outer-0x2010 embedded-retail forwarding helper. Keep proxy-owned state
        // consequences tied to the ingress forms that actually execute it.
        if (_reliability is not null)
        {
            if (decodedInnerIngress)
                packet = _reliability.ApplySavedGateTimeIfNeeded(packet);
            else if (source == "direct outer UDP")
                _reliability.ConsumeSavedGateTimeWithoutRewrite(packet.Header.Opcode);
        }

        // The direct-outer fallback checks whether an active retail
        // gameplay target exists before it queues the ordinary Create/Remove
        // packet, then runs the common proxy dispatcher regardless. Preserve
        // that split explicitly: POPT may still mutate its proxy-side GameID
        // tracker with no gameplay target, but no Create/Remove (original or
        // synthetic) is allowed to materialize the integrated client world.
        bool suppressDirectOuterCreateRemoveClientDelivery =
            source == "direct outer UDP"
            && !_gameplayClientTargetActive
            && packet.Header.Opcode is 0x0004 or 0x0007;

        Net7PacketOptimisationTracker? optimisation = _packetOptimisation;
        if (optimisation is not null && secondStageDispatchIngress && packet.Header.Opcode is 0x0004 or 0x0007)
        {
            IReadOnlyList<Packet> deliveries = optimisation.Apply(packet, decodedInnerIngress);
            if (suppressDirectOuterCreateRemoveClientDelivery)
            {
                _trace?.Invoke($"WORLD-01 direct outer 0x{packet.Header.Opcode:X4} had no active gameplay client target; proxy-side POPT state only");
                return;
            }

            foreach (Packet delivery in deliveries)
            {
                await DeliverGameplayPacketCoreAsync(
                    delivery, source, secondStageDispatchIngress, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (suppressDirectOuterCreateRemoveClientDelivery)
        {
            _trace?.Invoke($"WORLD-01 direct outer 0x{packet.Header.Opcode:X4} had no active gameplay client target; client materialization suppressed");
            return;
        }

        // Direct outer 0x2019 is deliberately *not* the Advanced
        // Object B special path. Stock only raw-forwards it when a retail gameplay
        // target exists, and its common dispatcher has no 0x2019 side effect.
        // Preserve that transport/form distinction without delivering a raw high-
        // opcode packet into an integrated client context that does not exist.
        if (source == "direct outer UDP"
            && !_gameplayClientTargetActive
            && packet.Header.Opcode == 0x2019)
        {
            _trace?.Invoke("WORLD-06 direct outer 0x2019 had no active gameplay client target; raw client delivery suppressed");
            return;
        }

        // Native Relationship-family packets are ordinary retail-client
        // materialization. Net7Proxy has no proxy-side 0x0089/0x008A/0x008B
        // state machine, so without an active gameplay-client target there is
        // nothing to apply in the integrated client. This covers both direct-outer
        // fallback and decoded-inner ordinary delivery.
        if (!_gameplayClientTargetActive
            && packet.Header.Opcode is 0x0089 or 0x008A or 0x008B)
        {
            _trace?.Invoke($"WORLD-07 {source} 0x{packet.Header.Opcode:X4} had no active gameplay client target; relationship materialization suppressed");
            return;
        }

        // Navigation/NavDelete are likewise ordinary retail-client
        // materialization with no proxy-side state. Native direct-outer or
        // decoded-inner forms are only delivered when a gameplay client target
        // exists. (outer-0x2010 embedded delivery already applies the same gate.)
        if (!_gameplayClientTargetActive
            && packet.Header.Opcode is 0x0099 or 0x009A)
        {
            _trace?.Invoke($"WORLD-08 {source} 0x{packet.Header.Opcode:X4} had no active gameplay client target; navigation materialization suppressed");
            return;
        }

        // Native 0x001B/0x001C are ordinary retail-client AuxData
        // materialization. The current proxy may receive them on server-facing
        // transports without a retail gameplay Connection, but in that state
        // there is no client AuxData root to mutate. Keep the integrated world
        // boundary tied to the same active gameplay-client ownership.
        if (!_gameplayClientTargetActive
            && packet.Header.Opcode is 0x001B or 0x001C)
        {
            _trace?.Invoke($"WORLD-09 {source} 0x{packet.Header.Opcode:X4} had no active gameplay client target; AuxData materialization suppressed");
            return;
        }

        // CameraControl is server-authored retail-client presentation/
        // transition-target state. With no gameplay client target the proxy has no
        // 0x0092-side state to materialize in the integrated client.
        if (!_gameplayClientTargetActive && packet.Header.Opcode == 0x0092)
        {
            _trace?.Invoke($"MOVE-11 {source} 0x0092 had no active gameplay client target; CameraControl materialization suppressed");
            return;
        }

        // Numeric server 0x002C/0x004E have no proxy-side
        // dispatcher effect. Their ordinary retail-compatible delivery therefore
        // has a client-side consequence only while a gameplay client target exists.
        if (!_gameplayClientTargetActive
            && packet.Header.Opcode is 0x002C or 0x004E)
        {
            _trace?.Invoke($"MOVE generic {source} 0x{packet.Header.Opcode:X4} had no active gameplay client target; delivery suppressed");
            return;
        }

        // These eight families have no proxy-side materialization.
        // The current server dispatcher only delivers them to an active retail
        // gameplay Connection. In the integrated client, do not materialize or
        // expose them merely because a server-facing transport carried them.
        if (!_gameplayClientTargetActive
            && packet.Header.Opcode is 0x0015 or 0x0059 or 0x0064 or 0x008D or 0x008E or 0x008F or 0x0090 or 0x0091)
        {
            _trace?.Invoke($"COMBAT-10 {source} 0x{packet.Header.Opcode:X4} had no active gameplay client target; delivery suppressed");
            return;
        }

        // Generic direct outer delivery only queues
        // native retail positional packets when an active gameplay-client target
        // exists. Net7Proxy has no 0x0008/0x003E/0x0040/0x0046-specific common-
        // dispatch side effect, so without that target there is no client timing
        // or spatial mutation.
        if (source == "direct outer UDP"
            && !_gameplayClientTargetActive
            && packet.Header.Opcode is 0x0008 or 0x003E or 0x0040 or 0x0046)
        {
            _trace?.Invoke($"WORLD positional direct outer 0x{packet.Header.Opcode:X4} had no active gameplay client target; client materialization suppressed");
            return;
        }

        await DeliverGameplayPacketCoreAsync(
            packet, source, secondStageDispatchIngress, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeliverGameplayPacketCoreAsync(
        Packet packet,
        string source,
        bool secondStageDispatchIngress,
        CancellationToken cancellationToken)
    {
        switch (packet.Header.Opcode)
        {
            case 0x0005:
                await HandleStartAsync(packet, source, cancellationToken).ConfigureAwait(false);
                break;

            case 0x0015:
            case 0x008D:
            case 0x008E:
            case 0x008F:
            case 0x0027:
            case 0x0028:
            case 0x0029:
                // Retail processors00867C50/00868A30/00868A60/00868A90,
                // 0040914C/00412526/00417C51 return without consuming fields.
                // Short reads and unused tails cannot turn these request-only
                // packets into acknowledgements, state updates or decode errors.
                break;

            case 0x002C:
            case 0x004E:
                // Retail0040BC67/0041181A consume no decoded fields and return.
                // Neither request has an inbound acknowledgement or state effect.
                break;

            case 0x0057:
            case 0x0058:
                // 0040C347 / 00417111 return without using any decoded fields.
                // These request packets neither complete an action nor mutate state.
                break;

            case 0x0051:
            case 0x0065:
                // 00867FE0 / 00417319 return without using decoded fields.
                // Do not add validation or completion to these native no-ops.
                break;

            case 0x005D:
                // 00401C80 returns without reading fields or publishing completion.
                break;

            case 0x003A:
            {
                // This has two ownership layers. The proxy-side common
                // dispatcher mutates forwarding/reliability/retry state even for
                // direct outer UDP when no retail gameplay Connection exists.
                // Retail-side world teardown/JOIN restart, however, only occurs
                // when there is an active gameplay client target. Capture that
                // target before the common dispatcher disables forwarding.
                bool gameplayClientTargetActive =
                    _gameplayClientTargetActive;
                _connection.HandoffArrived();

                if (secondStageDispatchIngress)
                {
                    // Handoff always clears POPT object state, even when
                    // This preserves the reliability maps for a gate transition.
                    _packetOptimisation?.Clear();
                    _reliability?.HandleServerHandoff();
                    ObserveServerHandoffRetries();
                    _transportState.SetGameplayForwardingActive(false);
                }

                if (!gameplayClientTargetActive)
                {
                    _trace?.Invoke($"ENTRY-08 0x003A from {source} had no active gameplay client target; proxy-side handoff state only");
                    break;
                }

                // Retail ProcessServerHandoffPacket owns current-sector materialized
                // client teardown regardless of which server ingress carried the
                // retail frame. Keep these client-domain resets outside the proxy
                // second-stage gate so outer-0x2010 embedded 0x003A behaves like a
                // packet delivered to retail rather than like common dispatch.
                _movement.ResetSectorState();
                Net7WorldApplyResult handoffWorldResult;
                uint handoffClientTime = _clock.GetMilliseconds();
                lock (_worldStateSync)
                {
                    Gameplay.ResetForServerHandoff();
                    PlayerSemantics.ResetForSectorBoundary();
                    Interlocked.Increment(ref _gameplayGeneration);
                    _ = Gameplay.Apply(packet);
                    handoffWorldResult = World.Apply(packet, handoffClientTime);
                    if (handoffWorldResult.PacketMaterialized)
                        NotifyWorldStateChanged(handoffClientTime);
                    else
                        NotifyPresentationStateChanged(handoffClientTime);
                }
                await ApplyWorldPostMaterializationAsync(packet, handoffWorldResult, cancellationToken).ConfigureAwait(false);
                SetLoginState(Net7LoginState.SectorTransition);
                Route = null;
                GameplayPacketReceived?.Invoke(packet);

                // Retail ClientTask consumes handoff result 4 and returns to JOIN
                // on its next task iteration. This is not a user/UI command. Run
                // the clean equivalent as session-owned asynchronous work so the
                // UDP receive pump remains free to receive 0x2009/0x2027 and the
                // subsequent sector-entry stream.
                StartAutomaticHandoffContinuation();
                break;
            }

            case 0x003C:
            {
                // Client-side presentation state exists only when the
                // server packet has an active gameplay-client target. Net7Proxy's
                // direct-outer common dispatcher is a no-op for 0x003C when retail
                // is absent; decoded-inner likewise returns without client delivery.
                if (!_gameplayClientTargetActive)
                {
                    _trace?.Invoke($"ENTRY-09 0x003C from {source} had no active gameplay client target; consumed without presentation mutation");
                    break;
                }

                lock(_worldStateSync)
                {
                    _ = Gameplay.Apply(packet);
                    PlayerSemantics.ObserveClientType(BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Span));
                }
                await ApplyWorldPacketAsync(packet, cancellationToken).ConfigureAwait(false);
                GameplayPacketReceived?.Invoke(packet);

                if (Gameplay.PresentationNotificationPending || Gameplay.PresentationRefreshCountdown > 0)
                    StartPresentationPassLoop();
                break;
            }

            case 0x0092:
                try
                {
                    Net7CameraControl camera = Net7GameplayWire.DecodeCameraControl(packet.Payload.Span);
                    if(camera.Message is 4 or 5) SuppressLocalGlobalWarpUpdates();
                    if (camera.Message is 0 or 1 or 4 or 5)
                        Gameplay.QueueNativeCameraEvent(camera.Message switch { 0 => 12, 1 => 13, 4 => 6, _ => 10 },camera.GameId);
                    // 739FF0 wire selectors are not native camera event numbers.
                    if (camera.Message is 2 or 6 or 7 or 8)
                    {
                        if (camera.Message == 7) Gameplay.ClearLootCamera();
                        Gameplay.QueueNativeCameraEvent(camera.Message switch { 2 => 0, 6 => 23, 7 => 16, _ => 25 }, camera.GameId);
                        NotifyPresentationStateChanged();
                    }
                    if (camera.Message == 3) Gameplay.AddCameraEvent(3, camera.GameId);
                    if(camera.Message==1)
                    {
                        // 00739FF0 event 0x0D selects surface arrival, state 7.
                        Gameplay.BeginLandArrival(camera.GameId,CurrentClientTime,queueEvent:false);
                        NotifyPresentationStateChanged();
                    }
                    // 00739FF0: camera message 5 plays Docking_Init, independently of
                    // the commit callback. Message 0 does not play this cue.
                    if(camera.Message==5)
                    {
                        Gameplay.ObserveDockingCameraSound();
                        // Publish before the asynchronous commit can advance the
                        // location and replace the current presentation snapshot.
                        GameplayPacketReceived?.Invoke(packet);
                    }
                    if (camera.Message == 0 && camera.GameId != uint.MaxValue)
                    {
                        // 00739FF0 queues event 0x0C; 00799E40 selects state 6
                        // with exit mode. This is departure, not docking clearance.
                        Gameplay.BeginDockDeparture(camera.GameId, CurrentClientTime, queueEvent: false);
                        NotifyPresentationStateChanged();
                    }
                    // 00739FF0 writes independent targets, including dock departure.
                    if (camera.Message == 0)
                        PlayerSemantics.ObserveServerTransitionTarget(Net7TransitionKind.Dock, camera.GameId);
                    if (camera.Message == 5)
                    {
                        PlayerSemantics.ObserveServerTransitionTarget(Net7TransitionKind.Dock, camera.GameId);
                        // 00739FF0 maps message 5 to event 0x0A (dock camera). 00799E40 uses
                        // immediate Action 07 callback when the cinematic is
                        // disabled. Enabled message 5 defers that callback until
                        // the authored camera path completes.
                        if (camera.GameId != uint.MaxValue)
                        {
                            var ship = World.LocalShipObjectId is uint shipId ? World.Objects.FindFirst(shipId) : null;
                            if (!Net7WarpStatus.Read(ship?.AuxData, Gameplay.PresentationMode).Incapacitated)
                            {
                                if (camera.Message == 5 && (!_dockRequestWithoutCamera || _dockRequestThroughController))
                                {
                                    if (Gameplay.DockCamera?.TargetId != camera.GameId)
                                        Gameplay.BeginDockCamera(PlayerSemantics.TransitionRequestRevision, camera.GameId, CurrentClientTime, queueEvent: false);
                                    NotifyPresentationStateChanged();
                                }
                                else
                                {
                                    Gameplay.ClearDockCamera();
                                    bool sent = await CommitDockAsync(cancellationToken).ConfigureAwait(false);
                                    _trace?.Invoke($"Dock without cinematic: commit target=0x{camera.GameId:X8} sent={sent}");
                                    NotifyPresentationStateChanged();
                                }
                            }
                            else
                                _trace?.Invoke("Dock commit blocked: local avatar is incapacitated.");
                        }
                    }
                    else if (camera.Message == 4)
                    {
                        PlayerSemantics.ObserveServerTransitionTarget(Net7TransitionKind.Gate, camera.GameId);
                        if ((!_gateRequestWithoutCamera || _gateRequestThroughController) && camera.GameId != uint.MaxValue &&
                            Gameplay.GateCamera?.TargetId != camera.GameId)
                            Gameplay.BeginGateCamera(camera.GameId, CurrentClientTime, queueEvent: false);
                        // Retail 00739FF0 queues camera event 6. With Camera_Gate
                        // disabled, 00799E40 immediately invokes 007993E0; the
                        // commit does not wait for the ship to reach the gate.
                        if (_gateRequestWithoutCamera && !_gateRequestThroughController &&
                            camera.GameId != uint.MaxValue)
                        {
                            var ship = World.LocalShipObjectId is uint shipId ? World.Objects.FindFirst(shipId) : null;
                            if (!Net7WarpStatus.Read(ship?.AuxData, Gameplay.PresentationMode).Incapacitated)
                            {
                                bool sent = await CommitGateAsync(cancellationToken).ConfigureAwait(false);
                                _trace?.Invoke($"Gate camera disabled: commit target=0x{camera.GameId:X8} sent={sent}");
                            }
                            else
                                _trace?.Invoke("Gate commit blocked: local avatar is incapacitated.");
                        }
                    }
                    if(camera.Message!=5)GameplayPacketReceived?.Invoke(packet);
                }
                catch (InvalidDataException ex)
                {
                    _trace?.Invoke($"MOVE-11 0x0092 from {source} rejected safely: {ex.Message}");
                }
                break;

            case 0x0079:
            case 0x007A:
            case 0x007B:
            case 0x007C:
            case 0x007E:
            case 0x0080:
            case 0x0086:
            case 0x0087:
                // Retail request processors00868670/008686A0/0040CFAE/
                // 0040517D/00868760/00417837/008687C0/0040B5FF use no fields.
                // Authoritative terminal and mission state arrives separately.
                break;

            case 0x008C:
                try
                {
                    await HandleHulkPermissionAsync(packet, cancellationToken).ConfigureAwait(false);
                    GameplayPacketReceived?.Invoke(packet);
                }
                catch (InvalidDataException ex)
                {
                    _trace?.Invoke($"INV-05 0x008C from {source} rejected safely: {ex.Message}");
                }
                break;

            case 0x00BA:
                HandleLogoffConfirmation(packet);
                GameplayPacketReceived?.Invoke(packet);
                break;

            case 0x00D0:
                try
                {
                    var (_, guildWorldResult) = ApplyGameplayAndWorldAndPublish(packet, _clock.GetMilliseconds());
                    await ApplyWorldPostMaterializationAsync(packet, guildWorldResult, cancellationToken).ConfigureAwait(false);
                    await ReconcileHulkAsync(cancellationToken).ConfigureAwait(false);
                    GameplayPacketReceived?.Invoke(packet);

                    Net7GuildMessage? guildMessage = Gameplay.GuildMessage;
                    if (guildMessage is not null && Net7SocialGuildPolicy.ShouldAutoRefreshRoster(
                            guildMessage.MessageType,
                            Gameplay.Social.GuildWindowActive,
                            PlayerSemantics.GetGuildMembership().IsInGuild))
                    {
                        await SendGuildCommandAsync(Net7ExactServerChatCommand.GuildListAll, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (InvalidDataException ex)
                {
                    _trace?.Invoke($"SOCIAL-06 0x00D0 rejected safely: {ex.Message}");
                }
                break;


            case 0x0099:
            case 0x009A:
                try
                {
                    _ = Gameplay.Apply(packet);
                    await ApplyWorldPacketAsync(packet, cancellationToken).ConfigureAwait(false);
                    GameplayPacketReceived?.Invoke(packet);
                }
                catch (InvalidDataException ex)
                {
                    // Navigation constructors0089E9F0/0089ED50 retain initialized
                    // fields on short reads. Client materialization does not
                    // turn a delivered packet into REL loss/recovery traffic.
                    _trace?.Invoke($"WORLD-08 0x{packet.Header.Opcode:X4} rejected safely: {ex.Message}");
                }
                break;

            case 0x001B:
            case 0x001C:
                try
                {
                    _ = Gameplay.Apply(packet);
                    await ApplyWorldPacketAsync(packet, cancellationToken).ConfigureAwait(false);
                    if (packet.Header.Opcode == 0x001B && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Span) == 0)
                        await ApplyAutomaticActionsAsync(cancellationToken).ConfigureAwait(false);
                    await ReconcileHulkAsync(cancellationToken).ConfigureAwait(false);
                    GameplayPacketReceived?.Invoke(packet);
                }
                catch (InvalidDataException ex)
                {
                    // AuxData00876E90 keeps the available decoded prefix on short
                    // reads. A client-side decode boundary must not masquerade as
                    // REL loss and manufacture retransmit/restart traffic. Native
                    // signed-length and PlayerVar out-of-bounds cases remain
                    // separate compatibility questions, not a stricter policy.
                    _trace?.Invoke($"WORLD-09 0x{packet.Header.Opcode:X4} rejected safely: {ex.Message}");
                }
                break;

            case 0x0097:
            {
                if (!_gameplayClientTargetActive)
                {
                    _trace?.Invoke("GalaxyMap 0x0097 had no active gameplay target; materialization suppressed");
                    break;
                }
                try
                {
                    if (GalaxyMap is not { } map)
                        throw new InvalidDataException("GalaxyMap local model is unavailable.");
                    if (Net7GalaxyMapUpdates.Command(packet.Payload.Span, out _) != 4)
                    {
                        GalaxyMap = Net7GalaxyMapUpdates.Apply(map, packet.Payload.Span);
                        GameplayPacketReceived?.Invoke(packet);
                        break;
                    }
                    Net7GalaxyMapLocation location = Net7GalaxyMapLocationWire.Decode(packet.Payload.Span);
                    GalaxyMapLocation = location;
                    GalaxyMap = map = Net7GalaxyMapUpdates.ResizeAllData(map, location.RecordCount);
                    // Safety rule: a presence bitset larger than one UDP datagram
                    // cannot be delivered (retail's send of it fails); the map state
                    // above is still applied as 008365C0 does.
                    byte[] reply = Net7GalaxyMapLocationWire.BuildPresenceReply(location, map, GalaxyMapShowAll);
                    // Current proxy 00402820/0040F220 forwards this ordinary
                    // 0098 unchanged. This is not final-login completion.
                    bool sent = await TrySendGameplayAsync(0x0098, reply, cancellationToken).ConfigureAwait(false);
                    _trace?.Invoke($"GalaxyMap command 4 materialized: {location.SystemName} / {location.SectorName}; records {location.RecordCount}; presence reply {(sent ? "sent" : "suppressed")}");
                    GameplayPacketReceived?.Invoke(packet);
                }
                catch (InvalidDataException ex)
                {
                    _trace?.Invoke($"GalaxyMap 0x0097 rejected safely: {ex.Message}");
                }
                break;
            }

            default:
            {
                uint clientTime = _clock.GetMilliseconds();
                if(secondStageDispatchIngress && ScheduleStaticEffectReplay(packet,cancellationToken))return;
                (bool gameplayMaterialized, Net7WorldApplyResult worldResult) =
                    ApplyGameplayAndWorldAndPublish(packet, clientTime);
                if (gameplayMaterialized && secondStageDispatchIngress)
                    ScheduleOrdinaryEffectRemoval(packet, cancellationToken);
                if (!gameplayMaterialized && !worldResult.PacketMaterialized)
                {
                    // Direct-outer generic fallback queues the unchanged
                    // outer opcode/payload to an active retail gameplay target
                    // before running the common second-stage dispatcher. Preserve
                    // that raw delivery for the proxy/control opcode namespace,
                    // while still failing closed for ordinary/current retail
                    // factory packets (including the legacy high factory ranges).
                    if (source == "direct outer UDP"
                        && _gameplayClientTargetActive
                        && !IsCurrentRetailClientPacketOpcode(packet.Header.Opcode))
                    {
                        _trace?.Invoke($"FULL-04 raw generic direct outer 0x{packet.Header.Opcode:X4} delivered without decoded-inner/materialization semantics");
                        GameplayPacketReceived?.Invoke(packet);
                        break;
                    }

                    // A factory-unmapped or assertion-only retail class
                    // is not a supported ordinary inbound SClient packet. Fail
                    // closed before unrelated trade/manufacturing/Hulk reconciliation
                    // and before surfacing a false gameplay-delivery event.
                    _trace?.Invoke($"FULL-01 unsupported ordinary inbound 0x{packet.Header.Opcode:X4} from {source} consumed without materialization");
                    break;
                }

                await ApplyWorldPostMaterializationAsync(packet, worldResult, cancellationToken).ConfigureAwait(false);
                await ReconcileHulkAsync(cancellationToken).ConfigureAwait(false);
                if (packet.Header.Opcode is 0x001E or 0x001F)
                    await ApplyAutomaticActionsAsync(cancellationToken).ConfigureAwait(false);
                GameplayPacketReceived?.Invoke(packet);
                break;
            }
        }
    }

    private static bool IsCurrentRetailClientPacketOpcode(ushort opcode)
    {
        // The ordinary client packet namespace occupies the low opcode
        // space. Its current factory also retains two legacy high families.
        // Treat those as retail packets even when they arrive numerically through
        // the generic direct-outer path, so assertion-only/dead classes do
        // not become supported merely because transport context changed.
        if (opcode < 0x1000)
            return true;

        return opcode is >= 0x1388 and <= 0x138E
            or >= 0x1391 and <= 0x1393
            or >= 0x7530 and <= 0x7531
            or >= 0x7535 and <= 0x7536
            or >= 0x7538 and <= 0x753A
            or 0x753C
            or >= 0x753E and <= 0x7541
            or 0x7545
            or 0x7547
            or 0x7549
            or 0x754B;
    }

    private void HandleLogoffConfirmation(Packet packet)
    {
        // Retail 0089B020 reads no payload; proxy 00412700 clears the IDs
        // independently of payload size. Unused bytes cannot veto logoff.

        // This gives server 0x00BA unconditional ownership of the proxy/session
        // teardown. Retail's pending-destination assertion is separate client UI
        // bookkeeping and must not prevent an integrated client from clearing an
        // already-ended server session if local request state was lost/desynced.
        Net7LogoffDestination? destination;
        lock (_logoffSync)
        {
            destination = _pendingLogoffDestination;
            _pendingLogoffDestination = null;
            if (destination is Net7LogoffDestination confirmedDestination)
            {
                LastConfirmedLogoffDestination = confirmedDestination;
                LastConfirmedLogoffTransitionState = Net7LogoffSemantics.ToRetailTransitionState(confirmedDestination);
            }
        }

        _transportState.ClearSessionIdentity();
        if (destination is not null)
            _gameplayClientTargetActive = false;
        _reliability?.ResetGateTransitionOwnership();
        // Graceful 0x00BA does not clear the POPT GameID tracker.
        // The current tracker lifetime is the routed UDP owner plus the explicit
        // server 0x003A handoff clear. Keep the stale map across character
        // selection just as the authoritative client/proxy path does.
        _movement.ResetSectorState();
        ResetGameplayAndWorldForCharacterSelection();
        PlayerSemantics.ResetForCharacterSelection();
        Interlocked.Increment(ref _gameplayGeneration);
        PlayerId = null;
        Route = null;
        SetLoginState(Net7LoginState.CharacterSelection, () => LogoffConfirmed?.Invoke(destination));
        if (destination is Net7LogoffDestination traceDestination)
        {
            _trace?.Invoke($"WORLD-13 0x00BA confirmed graceful logoff -> {traceDestination} (retail transition {LastConfirmedLogoffTransitionState})");
        }
        else
        {
            _trace?.Invoke("WORLD-13 0x00BA ended the active server session without a locally pending 0x00B9 destination");
        }
    }

    private async Task HandleHulkPermissionAsync(Packet packet, CancellationToken cancellationToken)
    {
        uint hulkId = Net7GameplayWire.DecodeHulkPermission(packet.Payload.Span);
        lock (_worldStateSync)
        {
            if (World.Objects.FindFirst(hulkId) is null || Gameplay.LocalSelectedTargetId != hulkId)
                return;
            Gameplay.OpenHulk(hulkId);
            NotifyPresentationStateChanged();
        }
        _ = await SendUiTriggerAsync(0x0B, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileHulkAsync(CancellationToken cancellationToken)
    {
        if (Gameplay.CurrentHarvestableId is uint harvestId &&
            (Gameplay.LocalSelectedTargetId != harvestId || World.Objects.FindFirst(harvestId) is null))
            await CloseHarvestableAsync(cancellationToken).ConfigureAwait(false);
        lock (_worldStateSync)
        {
            if (Gameplay.CurrentHulkId is not uint hulkId) return;
            if (World.Objects.FindFirst(hulkId) is not null && Gameplay.LocalSelectedTargetId == hulkId)
                return;
            Gameplay.CloseHulk();
            NotifyPresentationStateChanged();
        }
        _ = await SendUiTriggerAsync(0x0C, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyWorldPacketAsync(Packet packet, CancellationToken cancellationToken)
    {
        uint clientTime = _clock.GetMilliseconds();
        Net7WorldApplyResult result = ApplyWorldAndPublish(packet, clientTime);
        if (result.AuxDataWarning is string warning)
            _trace?.Invoke($"WORLD-09 0x{packet.Header.Opcode:X4} partial materialization: {warning}"+
                (result.AuxDataFailureReceipt is {} receipt ? "\n"+receipt : ""));
        if (result.AuxDataCompatibilityNote is string note)
            _trace?.Invoke($"WORLD-09 0x{packet.Header.Opcode:X4} compatibility: {note}");
        await ApplyWorldPostMaterializationAsync(packet, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyWorldPostMaterializationAsync(
        Packet packet,
        Net7WorldApplyResult result,
        CancellationToken cancellationToken)
    {
        if (result.TargetSelectionChange is { } selection)
        {
            // RefreshSelectedTargetUi -> MFD -> SetSelectedTargetObject performs
            // verb detach/attach for server selection too. Do not send a new
            // TargetRequest back to the server that supplied this selection.
            await _targetRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Gameplay.LocalSelectedTargetId == selection.Current)
                {
                    if (selection.Previous is uint oldId)
                        _ = await TrySendGameplayAsync(0x005A, Net7GameplayWire.BuildVerbRequest(
                            RequireLocalShipObjectId(),oldId,2),cancellationToken).ConfigureAwait(false);
                    if (selection.Current is uint newId)
                        _ = await TrySendGameplayAsync(0x005A, Net7GameplayWire.BuildVerbRequest(
                            RequireLocalShipObjectId(),newId,1),cancellationToken).ConfigureAwait(false);
                }
            }
            finally { _targetRequestGate.Release(); }
        }
        await ReconcileTradeAsync(cancellationToken).ConfigureAwait(false);
        await ReconcileManufacturingAsync(cancellationToken).ConfigureAwait(false);
        await ReconcileIncapacitatedFlightAsync(cancellationToken).ConfigureAwait(false);
        if (!result.RequestTimeDue)
            return;

        RoutedSectorUdpChannel? routed = _routed;
        if (routed is null)
            return;

        // Periodic Request_Time uses the same ordinary gameplay
        // forwarding ownership as every other retail 0x0044 producer. Missing
        // route/session state suppresses the send; once construction begins the
        // routed sequence is consumed before socket outcome, and no send failure
        // becomes a Request_Time-specific retry or packet-processing failure.
        await routed.SendToActiveBestEffortAsync(
            0x0044,
            Net7SectorEntryWire.BuildRequestTime(_clock.GetMilliseconds()),
            cancellationToken).ConfigureAwait(false);
        _trace?.Invoke($"ENTRY-07 periodic Request_Time <- world opcode 0x{packet.Header.Opcode:X4}");
    }

    private async Task ReconcileTradeAsync(CancellationToken cancellationToken)
    {
        if (World.LocalShipObjectId is not uint localShipObjectId)
            return;
        Net7AuxDataSchema? shipAuxData = World.Objects.FindFirst(localShipObjectId)?.AuxData;
        if (shipAuxData is null || !Gameplay.TradeDomain.TryGetPendingLocalSync(shipAuxData, out uint partnerId))
            return;

        bool sent = await SendActionCoreAsync(Net7PlayerAction.TradeSync, partnerId, 0, cancellationToken).ConfigureAwait(false);
        if (sent)
            Net7TradeDomain.NormalizeLocalSync(shipAuxData);
    }

    private async Task ReconcileManufacturingAsync(CancellationToken cancellationToken)
    {
        Net7ManufacturingDomain manufacturing = Gameplay.ManufacturingDomain;
        Net7ManufacturingReconcileResult result = manufacturing.ReconcileAuthoritativeState();
        if (result.RefreshManufactureSelection && manufacturing.RememberedManufactureItemId is uint manufactureItem)
        {
            bool sent = await TrySendGameplayAsync(
                0x007B, Net7GameplayWire.BuildManufactureItem(RequireStartSessionValue(), manufactureItem), cancellationToken).ConfigureAwait(false);
            if (sent)
                manufacturing.RecordManufactureItemSent(manufactureItem);
        }
        if (result.RefreshRefinerySelection && manufacturing.RememberedRefineryItemId is uint refineryItem)
        {
            bool sent = await TrySendGameplayAsync(
                0x007C, Net7GameplayWire.BuildRefineryItem(RequireStartSessionValue(), refineryItem), cancellationToken).ConfigureAwait(false);
            if (sent)
                manufacturing.RecordRefineryItemSent(refineryItem);
        }
    }

    private async Task HandleStartAsync(Packet packet, string source, CancellationToken cancellationToken)
    {
        RoutedSectorUdpChannel routed = _routed
            ?? throw new InvalidOperationException("Start arrived without an active routed gameplay transport.");
        if (CharacterSelection is null)
            throw new InvalidOperationException("Start arrived without an active selected-character sector context.");

        uint playerId = Net7SectorEntryWire.DecodeStartPlayerId(packet.Payload.Span);
        PlayerId = playerId;
        MutateWorldAndPublish(() =>
        {
            World.SetLocalPlayerId(playerId);
            // Retail clears its loading/transition state after installing
            // PlayerID and before it queues Request_Time followed by StartAck.
            World.ObserveSectorStart();
        });
        GameplayPacketReceived?.Invoke(packet);
        _trace?.Invoke($"Start <- {source}: PlayerID=0x{playerId:X8}");

        // Preserve the retail->proxy send ownership boundary.
        // Each ordinary gameplay forward is best-effort: route suppression or a
        // socket send failure cannot prevent the later StartAck companion or the
        // local completion state from being reached. A normal routed sequence is
        // still consumed once an individual frame is constructed.
        await routed.SendToActiveBestEffortAsync(
            0x0044,
            Net7SectorEntryWire.BuildRequestTime(_clock.GetMilliseconds()),
            cancellationToken).ConfigureAwait(false);
        await routed.SendToActiveBestEffortAsync(
            0x0006,
            Net7SectorEntryWire.BuildStartAck(playerId),
            cancellationToken).ConfigureAwait(false);

        // Proxy 00402820 uses the control identity in the companion payload,
        // while routed transport supplies its own identity in the header.
        uint fixedSessionId = _transportState.ControlSessionId;
        Net7RouteResult route = Route
            ?? throw new InvalidOperationException("Start arrived without the current joined-sector route state.");
        uint selectedSector = route.SectorJoinData.SelectedSector;
        ushort syntheticOpcode = Net7SectorEntryWire.SelectStartCompanionOpcode(selectedSector);
        await routed.SendToActiveBestEffortAsync(
            syntheticOpcode,
            Net7SectorEntryWire.BuildFixedSessionPayload(fixedSessionId),
            cancellationToken).ConfigureAwait(false);

        if (syntheticOpcode == 0x3004)
        {
            await DisposeAccountConnectionAsync().ConfigureAwait(false);
            _trace?.Invoke("ENTRY-06 0x3004 path closed/unpublished the account TCP connection");
        }

        // This owns intermediate-login completion at the end of StartAck
        // processing, after both forwarding attempts and the 0x3004-only account
        // unpublication semantic. It is local progression, not proof of delivery.
        _intermediateLoginComplete?.TrySetResult(true);
        _reliability?.SetIntermediateLoginComplete(true);
        _startAcknowledged?.TrySetResult(true);
        _wireCapture?.Mark(
            "entry-start-complete",
            $"playerId=0x{playerId:X8};companion=0x{syntheticOpcode:X4}");
    }

    private void CompleteSectorLogin(string source)
    {
        TaskCompletionSource<bool>? completion = _finalLoginCompletion;
        if (completion is null)
        {
            _trace?.Invoke($"unowned login-completion event <- {source}; ignored before ENB-03 entry transaction");
            return;
        }
        if (GalaxyMap is null)
        {
            SignalFatalLogin(new InvalidOperationException(
                "Sector login completed before the native GalaxyMap model was supplied."));
            return;
        }
        if (_localGalaxyMap is { } asset)
            GalaxyMap = Net7GalaxyMapUpdates.ReplayLocalAsset(GalaxyMap, asset,
                error => _trace?.Invoke($"GalaxyMap local record contained: {error}"));
        _trace?.Invoke($"final login complete <- {source}; native GalaxyMap local records replayed");
        completion.TrySetResult(true);
    }

    private async Task HandleDirectOuter2010Async(
        ServerUdpFrame frame,
        string source,
        CancellationToken cancellationToken)
    {
        // This requires an active gameplay session target before either the
        // sentinel or an ordinary embedded packet is consumed. In the integrated
        // client this target survives outgoing-forwarding suspension at handoff.
        if (!_gameplayClientTargetActive)
        {
            _trace?.Invoke($"{source} had no active gameplay target and was consumed without forwarding/completion");
            return;
        }

        // Proxy 0040F2F7 selects the completion sentinel before 0040F304
        // reads the embedded length. That field is unused for this branch.
        if (frame.Payload.Length >= PacketHeader.WireSize &&
            BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload.Span.Slice(2, 2)) == 0x0097)
        {
            CompleteSectorLogin($"{source} / inner 0x0097 sentinel");
            return;
        }

        Packet inner = Net7EmbeddedClientFrame.Decode(frame.Payload);
        await DispatchGameplayPacketAsync(inner, $"{source} embedded", cancellationToken).ConfigureAwait(false);
    }

    private static ServerUdpFrame CloneFrame(ServerUdpFrame frame)
        => new(frame.Opcode, frame.FixedSessionId, frame.Sequence, frame.Payload.ToArray());

    private void SignalFatalLogin(Exception exception)
    {
        _trace?.Invoke($"login/control fatal: {exception.GetType().Name}: {exception.Message}");
        SetLoginState(Net7LoginState.Faulted);
        try { _keepaliveLoopCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        _fatalLoginSignal.TrySetResult(exception);
    }

    private void RequestServerDirectedBroadShutdown(Exception exception)
    {
        Interlocked.Exchange(ref _serverDirectedBroadShutdown, 1);
        _trace?.Invoke($"server-directed broad shutdown: {exception.GetType().Name}: {exception.Message}");
        SetLoginState(Net7LoginState.Faulted);
        CancelQuietly(_watchdogCts);
        CancelQuietly(_keepaliveLoopCts);
        CancelQuietly(_routedPumpCts);
        CancelQuietly(_masterPumpCts);
        CancelQuietly(_accountPumpCts);
        CancelQuietly(_gameplayLifetimeCts);
        _fatalLoginSignal.TrySetResult(exception);
    }

    private static void CancelQuietly(CancellationTokenSource? cts)
    {
        if (cts is null)
            return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task TrySendLocalClientLossNotificationAsync()
    {
        if (Volatile.Read(ref _serverDirectedBroadShutdown) != 0)
            return;

        MasterControlUdpChannel? master = _master;
        uint fixedSessionId = _transportState.ControlSessionId;
        if (master is null || fixedSessionId == 0 || !_transportState.GameplayForwardingActive)
            return;

        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, fixedSessionId);
        try
        {
            await master.SendFixedAsync(0x1008, fixedSessionId, payload, CancellationToken.None).ConfigureAwait(false);
            _trace?.Invoke($"local session loss -> fixed/control UDP 0x1008 fixed=0x{fixedSessionId:X8}");
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            _trace?.Invoke($"local session-loss 0x1008 could not be sent: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CommitValidatedFixedSessionId(uint value, string source)
    {
        uint previous = _transportState.CommitValidatedFixedSessionId(value);
        if (previous != value)
            _trace?.Invoke($"fixed/session state <- {source}: 0x{previous:X8} -> 0x{value:X8}");
    }

    /// <summary>
    /// Direct Master/sector-entry transaction for the originally selected
    /// character. A server handoff's JOIN restart reuses the same core.
    /// </summary>
    public Task<Net7RouteResult> EnterSelectedCharacterAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return EnterSelectedCharacterAsync(Net7LocalDataAssets.CurrentGalaxyMap, cancellationToken);
    }

    /// <summary>
    /// Internal deterministic seam for conformance fixtures. Production callers
    /// do not get to substitute an arbitrary GalaxyMap model:
    /// pin the current local asset by identity before sector entry.
    /// </summary>
    internal async Task<Net7RouteResult> EnterSelectedCharacterAsync(
        Net7GalaxyMapData galaxyMap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(galaxyMap);
        ArgumentNullException.ThrowIfNull(galaxyMap);
        ThrowIfDisposed();
        await _entryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool entryTransactionStarted = false;
        try
        {
            Net7CharacterSelectionResult selected = CharacterSelection
                ?? throw new InvalidOperationException("A validated ENB-02 character selection is required before Master routing.");
            if (LoginState != Net7LoginState.CharacterSelected)
                throw new InvalidOperationException($"Cannot enter the selected character from Net7 login state {LoginState}.");
            ValidateMasterEntryOptions();

            _localGalaxyMap = galaxyMap;

            // Build before any Master traffic so local validation cannot produce a
            // partial transaction.
            _ = Net7AccountWire.BuildMasterLoginPayload(selected.SectorJoinData.SelectedSector, _options.FeatureFlags);
            entryTransactionStarted = true;
            return await EnterSectorCoreAsync(
                selected.Character,
                selected.SectorJoinData,
                galaxyMap,
                completeWorldHandoff: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopIntermediateLoginWatchdogAsync().ConfigureAwait(false);
            if (entryTransactionStarted)
            {
                if (LoginState != Net7LoginState.CharacterSelection)
                    _transportState.SetGameplayForwardingActive(false);
                if (LoginState is not Net7LoginState.Faulted and not Net7LoginState.CharacterSelection)
                    SetLoginState(Net7LoginState.Faulted);
            }
            throw;
        }
        finally
        {
            _entryGate.Release();
        }
    }

    private void StartPresentationPassLoop()
    {
        // SClientUpdateScenePresentation is the unique current producer for the
        // Keep one owned native presentation loop
        // rather than exposing a manual network command that could fabricate it.
        if (Interlocked.CompareExchange(ref _presentationPassLoopActive, 1, 0) != 0)
            return;

        StartOwnedOneShot(RunPresentationPassLoopAsync);
    }

    private async Task RunPresentationPassLoopAsync()
    {
        try
        {
            // Give additional presentation activations in the same scheduling turn
            // a chance to coalesce into the single-byte retail latch equivalent.
            await Task.Yield();
            while (!_gameplayLifetimeCts.IsCancellationRequested
                && (Gameplay.PresentationNotificationPending || Gameplay.PresentationRefreshCountdown > 0))
            {
                _ = await AdvancePresentationFrameAsync(_gameplayLifetimeCts.Token).ConfigureAwait(false);
                await Task.Yield();
            }
        }
        catch (OperationCanceledException) when (_gameplayLifetimeCts.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref _presentationPassLoopActive, 0);
            // Close the tiny race where a fresh 0x003C activation arrives after
            // the final work check but before ownership is released.
            if (!_gameplayLifetimeCts.IsCancellationRequested
                && (Gameplay.PresentationNotificationPending || Gameplay.PresentationRefreshCountdown > 0))
            {
                StartPresentationPassLoop();
            }
        }
    }

    private void StartAutomaticHandoffContinuation()
    {
        // One ClientTask owns one JOIN transition at a time. Duplicate 0x003A
        // remains locally re-entrant (world teardown/pending join data are
        // refreshed), but it must not create concurrent Master-login traffic.
        if (Interlocked.CompareExchange(ref _automaticHandoffContinuationActive, 1, 0) != 0)
        {
            _trace?.Invoke("ENTRY-08 duplicate handoff observed while JOIN restart is already active");
            return;
        }

        StartOwnedOneShot(RunAutomaticHandoffContinuationAsync);
    }

    private async Task RunAutomaticHandoffContinuationAsync()
    {
        try
        {
            // Match ClientTask's next-iteration ownership and, critically, do not
            // run the JOIN transaction inline on the UDP receive pump that must
            // receive its responses.
            await Task.Yield();
            if (_gameplayLifetimeCts.IsCancellationRequested)
                return;

            _trace?.Invoke("ENTRY-08 handoff result -> automatic JOIN restart");
            _ = await ContinuePendingHandoffAsync(_gameplayLifetimeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_gameplayLifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"ENTRY-08 automatic JOIN restart failed: {ex.GetType().Name}: {ex.Message}");
            if (LoginState is not Net7LoginState.Faulted
                and not Net7LoginState.CharacterSelection
                and not Net7LoginState.Disposed)
            {
                SignalFatalLogin(ex);
            }
        }
        finally
        {
            Volatile.Write(ref _automaticHandoffContinuationActive, 0);
        }
    }

    /// <summary>
    /// Clean JOIN restart after an admitted server handoff. The handoff
    /// packet installs exact next SectorJoinClientData and tears down current
    /// materialized world state before this method is allowed to negotiate the
    /// next Master/Redirect/gameplay route.
    /// </summary>
    internal async Task<Net7RouteResult> ContinuePendingHandoffAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _entryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool entryTransactionStarted = false;
        try
        {
            Net7CharacterSelectionResult selected = CharacterSelection
                ?? throw new InvalidOperationException("A selected character is required to continue a sector handoff.");
            Net7ServerHandoff handoff = World.PendingHandoff
                ?? throw new InvalidOperationException("There is no pending ENTRY-08 server handoff to continue.");
            Net7GalaxyMapData galaxyMap = GalaxyMap
                ?? throw new InvalidOperationException("A native GalaxyMap model is required to continue a sector handoff.");
            if (LoginState != Net7LoginState.SectorTransition)
                throw new InvalidOperationException($"Cannot continue a handoff from Net7 login state {LoginState}.");
            ValidateMasterEntryOptions();
            _ = Net7AccountWire.BuildMasterLoginPayload(handoff.NextJoinData.SelectedSector, _options.FeatureFlags);

            entryTransactionStarted = true;
            return await EnterSectorCoreAsync(
                selected.Character,
                handoff.NextJoinData,
                galaxyMap,
                completeWorldHandoff: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopIntermediateLoginWatchdogAsync().ConfigureAwait(false);
            if (entryTransactionStarted)
            {
                if (LoginState != Net7LoginState.CharacterSelection)
                    _transportState.SetGameplayForwardingActive(false);
                if (LoginState is not Net7LoginState.Faulted and not Net7LoginState.CharacterSelection)
                    SetLoginState(Net7LoginState.Faulted);
            }
            throw;
        }
        finally
        {
            _entryGate.Release();
        }
    }

    private async Task<Net7RouteResult> EnterSectorCoreAsync(
        Net7AvatarSlot character,
        Net7SectorJoinClientData joinData,
        Net7GalaxyMapData galaxyMap,
        bool completeWorldHandoff,
        CancellationToken cancellationToken)
    {
        MasterControlUdpChannel master = _master
            ?? throw new InvalidOperationException("Master/control UDP is not open.");
        RoutedSectorUdpChannel routed = _routed
            ?? throw new InvalidOperationException("Routed UDP is not open.");

        uint fixedSessionId = joinData.FixedSessionId;
        uint selectedSector = joinData.SelectedSector;
        byte[] masterRequest = Net7AccountWire.BuildMasterLoginPayload(selectedSector, _options.FeatureFlags);

        GalaxyMap = galaxyMap;
        GalaxyMapLocation = null;
        PlayerId = null;
        Route = null;
        _reliability?.PrepareForMasterLogin();
        SetLoginState(Net7LoginState.MasterHandoff);

        // Proxy 00414B70 assigns the new JOIN identity before 0x200F.
        _transportState.CommitValidatedFixedSessionId(fixedSessionId);

        // Exact adjacency: normal 0x200F first; only then create
        // the transaction-owned response waiter and begin 0x2008 retries.
        await master.SendNormalAsync(0x200F, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);

        var masterWaiter = NewFrameWaiter();
        InstallMasterLoginWaiter(masterWaiter);
        ServerUdpFrame masterFrame;
        try
        {
            masterFrame = await SendRoutedRequestWithRetryAsync(
                0x2008, masterRequest, masterWaiter.Task, "Master-login UDP 0x2009", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ClearMasterLoginWaiter(masterWaiter);
        }

        Net7MasterLoginResponse masterReply = Net7MasterLoginResponse.Decode(masterFrame.Payload.Span);
        int gameplayPort = IsValidGameplayPort(masterReply.CandidateGameplayPort)
            ? masterReply.CandidateGameplayPort
            : await ConfirmSectorPortAsync(selectedSector, cancellationToken).ConfigureAwait(false);

        routed.CommitGameplayPort(gameplayPort);
        var route = new Net7RouteResult(
            character,
            fixedSessionId,
            masterReply.RetainedMasterResponseDword,
            new IPEndPoint(routed.ServerAddress, gameplayPort),
            joinData);
        Route = route;

        _finalLoginCompletion = NewBoolWaiter();
        _startAcknowledged = NewBoolWaiter();
        _intermediateLoginComplete = NewBoolWaiter();
        StartIntermediateLoginWatchdog();

        // Retail00727F00 creates/replaces the gameplay connection before
        // 00766AB0 queues Login. JOIN retained any previous target throughout.
        _gameplayClientTargetActive = true;
        SetLoginState(Net7LoginState.SectorJoining);
        routed.SetGameplayForwardingActive(true);
        _reliability?.StartWorkerIfConfigured();
        uint sectorLoginTime = _clock.GetMilliseconds();
        long? sectorLoginCaptureTime = _wireCapture?.CurrentElapsedMicroseconds;
        // Optional local replay bootstrap: no credentials or authentication ticket.
        // Capture before the first gameplay datagram so replay can use the same
        // receive/reliability/world path after the recorded route boundary.
        if (_wireCapture?.IsActive == true)
            _wireCapture.Mark("replay-sector-bootstrap-v1", System.Text.Json.JsonSerializer.Serialize(
                new Net7RecordedSectorBootstrap(CharacterSelection!, joinData, masterReply.RetainedMasterResponseDword,
                    ServerFrequencyMultiplier, sectorLoginTime) {InputTimingVersion=2,ClockCaptureElapsedMicroseconds=sectorLoginCaptureTime,MasterSequence=master.Sequence.Capture(),RoutedSequence=routed.Sequence.Capture()}));
        byte[] sectorLogin = Net7SectorLoginWire.Build(joinData, sectorLoginTime);
        bool gateLoginConsumed = _reliability?.TryConsumeOutgoingGameplayLogin(sectorLoginTime) == true;
        if (!gateLoginConsumed)
        {
            // This has no per-send success/failure result. Route suppression
            // or a socket/short-send failure leaves local sector-joining state armed;
            // later completion/watchdog ownership decides the outcome.
            await routed.SendToActiveBestEffortAsync(
                0x0002, sectorLogin, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _trace?.Invoke("sector login consumed by active gate-cache opposite-side rendezvous");
        }

        await WaitForStableSectorEntryAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfFatalLoginSignaled();
        await StopIntermediateLoginWatchdogAsync().ConfigureAwait(false);
        if (completeWorldHandoff)
            MutateWorldAndPublish(World.CompleteHandoffTransition);
        SetLoginState(Net7LoginState.SectorReady);
        _trace?.Invoke(
            $"sector entry complete: gameplay route {route.GameplaySectorEndPoint}, " +
            $"PlayerID=0x{PlayerId.GetValueOrDefault():X8}, integrated Start/StartAck complete");
        return route;
    }

    private async Task<int> ConfirmSectorPortAsync(uint selectedSector, CancellationToken cancellationToken)
    {
        if (_routed is null)
            throw new InvalidOperationException("Routed UDP channel is not open.");

        byte[] payload = Net7AccountWire.BuildSectorPortConfirmationPayload(selectedSector);
        // Proxy 00401820..0040185B retries confirmation until the port is valid.
        for (long transaction = 1; ; transaction++)
        {
            var waiter = NewFrameWaiter();
            InstallSectorPortWaiter(waiter);
            try
            {
                ServerUdpFrame response = await SendRoutedRequestWithRetryAsync(
                    0x2026, payload, waiter.Task, "sector-port confirmation UDP 0x2027", cancellationToken).ConfigureAwait(false);
                if (response.Payload.Length < 2)
                {
                    // Proxy 004144A0 records the payload pointer into its 0x10000 receive
                    // buffer and the 00401820 loop reads the port through it without a
                    // payload length check: a short reply's port bytes are
                    // undefined (safety rule). Treat it as no valid port and keep the
                    // 00401820 confirmation retry running instead of failing entry.
                    _trace?.Invoke($"sector-port confirmation transaction {transaction} reply is shorter than its u16 port; confirmation continues");
                    continue;
                }

                ushort port = BinaryPrimitives.ReadUInt16LittleEndian(response.Payload.Span[..2]);
                if (IsValidGameplayPort(port))
                    return port;
                _trace?.Invoke($"sector-port confirmation transaction {transaction} returned invalid port {port}");
            }
            catch (TimeoutException)
            {
                _trace?.Invoke($"sector-port confirmation transaction {transaction} timed out; confirmation continues");
            }
            finally
            {
                ClearSectorPortWaiter(waiter);
            }
        }

    }

    private async Task<ServerUdpFrame> SendRoutedRequestWithRetryAsync(
        ushort opcode,
        ReadOnlyMemory<byte> payload,
        Task<ServerUdpFrame> responseTask,
        string description,
        CancellationToken cancellationToken)
    {
        RoutedSectorUdpChannel routed = _routed
            ?? throw new InvalidOperationException("Routed UDP channel is not open.");

        for (int attempt = 1; attempt <= _options.MasterRequestAttempts; attempt++)
        {
            // The current retry helper checks completion before every
            // send. Preserve that boundary with the transaction-owned waiter so
            // a response that lands after the prior poll window cannot provoke
            // one extra sequenced request.
            if (responseTask.IsCompleted)
                return await responseTask.ConfigureAwait(false);
            ThrowIfFatalLoginSignaled();
            await routed.SendNormalAsync(_options.MasterHandoffUdpPort, opcode, payload, cancellationToken).ConfigureAwait(false);

            for (int poll = 0; poll < _options.MasterResponsePollsPerAttempt; poll++)
            {
                if (responseTask.IsCompleted)
                    return await responseTask.ConfigureAwait(false);
                ThrowIfFatalLoginSignaled();

                Task delay = _clock.DelayAsync(_options.MasterResponsePollInterval, cancellationToken);
                Task completed = await Task.WhenAny(responseTask, _fatalLoginSignal.Task, delay).ConfigureAwait(false);
                if (completed == responseTask)
                    return await responseTask.ConfigureAwait(false);
                if (completed == _fatalLoginSignal.Task)
                {
                    Exception fatal = await _fatalLoginSignal.Task.ConfigureAwait(false);
                    throw fatal;
                }
                await delay.ConfigureAwait(false);
            }
        }

        // Final-sleep detail: stock returns to its caller after the
        // twentieth poll window and that caller still observes a response that
        // became ready during the final sleep. Do the equivalent check before
        // declaring the clean transaction timed out.
        if (responseTask.IsCompleted)
            return await responseTask.ConfigureAwait(false);
        ThrowIfFatalLoginSignaled();

        throw new TimeoutException(
            $"Timed out waiting for {description} after {_options.MasterRequestAttempts} sends x " +
            $"{_options.MasterResponsePollsPerAttempt} polls.");
    }

    private static bool IsValidGameplayPort(int port)
        => port >= Net7ServerOptions.CurrentMinGameplaySectorUdpPort
            && port <= Net7ServerOptions.CurrentMaxGameplaySectorUdpPort;

    private void ValidateMasterEntryOptions()
    {
        if (_options.MasterRequestAttempts <= 0)
            throw new InvalidOperationException("MasterRequestAttempts must be positive.");
        if (_options.MasterResponsePollsPerAttempt <= 0)
            throw new InvalidOperationException("MasterResponsePollsPerAttempt must be positive.");
        if (_options.MasterResponsePollInterval < TimeSpan.Zero)
            throw new InvalidOperationException("MasterResponsePollInterval cannot be negative.");
        if (_options.IntermediateLoginWatchdogSends <= 0 || _options.IntermediateLoginPollsPerCycle <= 0)
            throw new InvalidOperationException("Intermediate-login watchdog budgets must be positive.");
        if (_options.IntermediateLoginPollInterval < TimeSpan.Zero)
            throw new InvalidOperationException("IntermediateLoginPollInterval cannot be negative.");
    }

    private async Task WaitForStableSectorEntryAsync(CancellationToken cancellationToken)
    {
        Task start = _startAcknowledged?.Task
            ?? throw new InvalidOperationException("StartAck completion waiter is not installed.");

        // A current integrated server session became externally
        // observable in-sector, accepted normal social traffic, and remained
        // alive without any outer 0x2010 or 0x2011. Those compatibility
        // completion forms remain supported when present, but they are not a
        // mandatory integrated readiness gate.
        // Net7Proxy 00414860 owns the entry watchdog; 00412700 completes it
        // on Start. There is no additional deadline after that latch completes.
        Task completed = await Task.WhenAny(start, _fatalLoginSignal.Task)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (completed == start)
        {
            await start.ConfigureAwait(false);
            _wireCapture?.Mark("entry-readiness-satisfied", "Start/StartAck");
            return;
        }
        Exception fatal = await _fatalLoginSignal.Task.ConfigureAwait(false);
        throw fatal;
    }

    private void StartIntermediateLoginWatchdog()
    {
        if (_master is null || _intermediateLoginComplete is null)
            throw new InvalidOperationException("Intermediate-login watchdog prerequisites are not installed.");
        if (_watchdogCts is not null)
            throw new InvalidOperationException("Intermediate-login watchdog is already running.");

        var cts = new CancellationTokenSource();
        _watchdogCts = cts;
        _watchdogTask = Task.Run(() => RunIntermediateLoginWatchdogAsync(cts.Token));
    }

    private async Task RunIntermediateLoginWatchdogAsync(CancellationToken cancellationToken)
    {
        MasterControlUdpChannel? master = _master;
        TaskCompletionSource<bool>? intermediate = _intermediateLoginComplete;
        if (master is null || intermediate is null)
            return;

        try
        {
            for (int cycle = 0; cycle < _options.IntermediateLoginWatchdogSends; cycle++)
            {
                for (int poll = 0; poll < _options.IntermediateLoginPollsPerCycle; poll++)
                {
                    if (intermediate.Task.IsCompleted)
                        return;
                    await _clock.DelayAsync(_options.IntermediateLoginPollInterval, cancellationToken).ConfigureAwait(false);
                }

                if (intermediate.Task.IsCompleted)
                    return;

                // Stock rereads master/control +0x10 for every watchdog
                // send. Do not snapshot the identity at watchdog start; a later
                // fixed/session control change must be reflected in both header
                // and payload of the next fixed-header 0x2022.
                uint retryFixedSessionId = _transportState.ControlSessionId;
                byte[] retryFixedPayload = Net7SectorEntryWire.BuildFixedSessionPayload(retryFixedSessionId);
                await master.SendFixedAsync(0x2022, retryFixedSessionId, retryFixedPayload, cancellationToken).ConfigureAwait(false);
                _trace?.Invoke($"intermediate-login watchdog -> fixed 0x2022 #{cycle + 1}");
            }

            // Fourth-send edge: stock does not re-poll the completion
            // latch after the fourth fixed 0x2022. Failure begins immediately
            // from the completion value already observed before that send.
            //
            // Ordinary post-Master failure path: the stock communal
            // ready byte is still set from successful Master negotiation, so the
            // 0x3006 helper is pre-suppressed and emits nothing. The server-visible
            // failure action is therefore one fixed-header master/control 0x1008,
            // followed by local cleanup and a recoverable return to login.
            var failure = new TimeoutException(
                $"Intermediate-login watchdog exhausted its {_options.IntermediateLoginWatchdogSends} configured retry cycle(s).");
            await CompleteIntermediateLoginFailureReturnAsync(master, failure).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SignalFatalLogin(ex);
        }
    }

    private async Task CompleteIntermediateLoginFailureReturnAsync(
        MasterControlUdpChannel master,
        Exception failure)
    {
        // Source both copies from the current logical session identity
        // at failure entry. The fixed-header builder owns the current +0x08 residue
        // behavior and does not consume the master's normal sequence.
        uint failureFixedSessionId = FixedSessionId;
        byte[] failureFixedPayload = Net7SectorEntryWire.BuildFixedSessionPayload(failureFixedSessionId);
        try
        {
            // Once the watchdog has entered its failure-return helper, cleanup is
            // not cancellation-dependent. Match the current void-send chain: a
            // local send failure may lose the notification, but it must not strand
            // the failed gameplay/account transports or convert recovery into broad
            // process failure.
            await master.SendFixedAsync(
                0x1008,
                failureFixedSessionId,
                failureFixedPayload,
                CancellationToken.None).ConfigureAwait(false);
            _trace?.Invoke($"intermediate-login failure -> fixed/control UDP 0x1008 fixed=0x{failureFixedSessionId:X8}");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException)
        {
            _trace?.Invoke($"intermediate-login failure 0x1008 could not be sent: {ex.GetType().Name}: {ex.Message}");
        }

        // Proxy004147C0 retires the gameplay client and detaches the account;
        // it does not clear UDP identities, forwarding, route or reliability.
        // Keep those transport owners until their ordinary session disposal.
        _gameplayClientTargetActive = false;
        await DisposeAccountConnectionAsync().ConfigureAwait(false);

        _movement.ResetSectorState();
        ResetGameplayAndWorldForCharacterSelection();
        PlayerSemantics.ResetForCharacterSelection();
        Interlocked.Increment(ref _gameplayGeneration);
        PlayerId = null;
        Route = null;
        CharacterSelection = null;

        // This is recovery, not broad shutdown. Publish a return-to-selection
        // state so the product layer can return to its login screen and create a
        // fresh account session rather than presenting a terminal Faulted state.
        SetLoginState(Net7LoginState.CharacterSelection);
        _trace?.Invoke("intermediate-login failure cleanup complete -> return to login/character-selection workflow");
        _fatalLoginSignal.TrySetResult(failure);
    }

    private async Task StopIntermediateLoginWatchdogAsync()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _watchdogCts, null);
        Task? task = Interlocked.Exchange(ref _watchdogTask, null);
        if (cts is null)
            return;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }

        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        cts.Dispose();
    }

    private async Task DisposeAccountConnectionAsync()
    {
        // Unpublish first so no new transaction can acquire a connection that is
        // already being torn down. Then stop/join its receive owner before the
        // socket itself is disposed.
        Net7TcpConnection? account = Interlocked.Exchange(ref _account, null);
        CancellationTokenSource? pumpCts = Interlocked.Exchange(ref _accountPumpCts, null);
        Task? pumpTask = Interlocked.Exchange(ref _accountPumpTask, null);
        await StopBackgroundTaskAsync(pumpCts, pumpTask, "account role-10 pump").ConfigureAwait(false);

        if (account is not null)
            await account.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RetireAccountConnectionFromPumpAsync(Net7TcpConnection account)
    {
        Net7TcpConnection? published = Interlocked.CompareExchange(ref _account, null, account);
        if (ReferenceEquals(published, account))
            await account.DisposeAsync().ConfigureAwait(false);
    }

    private async Task DisposeMasterControlTransportAsync()
    {
        // Unpublish the owner before teardown so a failed
        // bootstrap cannot leave a reachable master/control transport behind.
        MasterControlUdpChannel? master = Interlocked.Exchange(ref _master, null);
        CancellationTokenSource? pumpCts = Interlocked.Exchange(ref _masterPumpCts, null);
        Task? pumpTask = Interlocked.Exchange(ref _masterPumpTask, null);
        await StopBackgroundTaskAsync(pumpCts, pumpTask, "master/control pump").ConfigureAwait(false);

        if (master is not null)
            await master.DisposeAsync().ConfigureAwait(false);
    }

    private static TaskCompletionSource<ServerUdpFrame> NewFrameWaiter()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> NewBoolWaiter()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void InstallMasterLoginWaiter(TaskCompletionSource<ServerUdpFrame> waiter)
    {
        lock (_waiterSync)
        {
            if (_masterLoginWaiter is not null)
                throw new InvalidOperationException("A Master-login response waiter is already installed.");
            _masterLoginWaiter = waiter;
        }
    }

    private void ClearMasterLoginWaiter(TaskCompletionSource<ServerUdpFrame> waiter)
    {
        lock (_waiterSync)
        {
            if (ReferenceEquals(_masterLoginWaiter, waiter))
                _masterLoginWaiter = null;
        }
    }

    private void InstallSectorPortWaiter(TaskCompletionSource<ServerUdpFrame> waiter)
    {
        lock (_waiterSync)
        {
            if (_sectorPortWaiter is not null)
                throw new InvalidOperationException("A sector-port response waiter is already installed.");
            _sectorPortWaiter = waiter;
        }
    }

    private void ClearSectorPortWaiter(TaskCompletionSource<ServerUdpFrame> waiter)
    {
        lock (_waiterSync)
        {
            if (ReferenceEquals(_sectorPortWaiter, waiter))
                _sectorPortWaiter = null;
        }
    }

    private async Task SendTcpAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        Net7TcpConnection account = _account
            ?? throw new InvalidOperationException("Account TCP connection is not open.");

        try
        {
            await account.SendAsync(Packet.ForOpcode(opcode, payload), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // NetworkStream reports terminal socket-write failures as IOException;
            // ObjectDisposedException covers a concurrent terminal close. Retire
            // the published owner and fail any transaction waiters rather than
            // leaving a dead TCP-3807 reference available for reuse.
            CompleteAccountResponseWaitersWithException(ex);
            await DisposeAccountConnectionAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Packet> ReceiveAccountResponseAsync(
        ushort[] acceptedOpcodes,
        string description,
        CancellationToken cancellationToken)
    {
        if (_account is null)
            throw new InvalidOperationException("Account TCP connection is not open.");
        if (acceptedOpcodes.Length == 0)
            throw new ArgumentException("At least one accepted response opcode is required.", nameof(acceptedOpcodes));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.AccountResponseTimeout);

        try
        {
            while (true)
            {
                if (_fatalLoginSignal.Task.IsCompleted)
                {
                    Exception alreadyFatal = await _fatalLoginSignal.Task.ConfigureAwait(false);
                    throw alreadyFatal;
                }

                Task<Packet> receive = _account.ReceiveAsync(timeoutCts.Token);
                Task completed = await Task.WhenAny(receive, _fatalLoginSignal.Task).ConfigureAwait(false);
                if (completed == _fatalLoginSignal.Task)
                {
                    timeoutCts.Cancel();
                    try { await receive.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    Exception fatal = await _fatalLoginSignal.Task.ConfigureAwait(false);
                    throw fatal;
                }

                Packet packet = await receive.ConfigureAwait(false);
                ThrowIfFatalLoginSignaled();
                if (acceptedOpcodes.Contains(packet.Header.Opcode))
                    return packet;


                // Proxy 00401480 ignores unknown opcodes; 00403F29 returns to the
                // receive loop. Unrelated packets must not retire the connection.
                // An account-family response owned by another transaction is
                // consumed as unrelated and cannot satisfy this waiter. It is not
                // retained for a future request, preventing stale prior responses.
                _trace?.Invoke(
                    $"account routed unrelated TCP 0x{packet.Header.Opcode:X4} len={packet.Payload.Length} away from {description}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out after {_options.AccountResponseTimeout.TotalSeconds:0.#}s waiting for {description}.");
        }
    }

    private void ThrowIfFatalLoginSignaled()
    {
        if (_fatalLoginSignal.Task.IsCompletedSuccessfully)
            throw _fatalLoginSignal.Task.Result;
    }

    private void SuppressLocalGlobalWarpUpdates()
    {
        lock(_worldStateSync)
            if(World.LocalShipObjectId is uint id)World.Objects.FindFirst(id)?.SuppressGlobalWarpUpdates();
    }

    private (bool GameplayMaterialized, Net7WorldApplyResult WorldResult) ApplyGameplayAndWorldAndPublish(
        Packet packet,
        uint clientTime)
    {
        lock (_worldStateSync)
        {
            clientTime = ResolveRecordedTimeSynchronization(packet, clientTime);
            if (packet.Header.Opcode == 0x0034) _connection.TimeAnswered(packet.Payload.Span, clientTime);
            _connection.PacketArrived(packet.Header.Opcode);
            // Effect receipts must survive the bounded sector-entry capture.
            // These decoded gameplay opcodes carry effect parameters, not chat
            // or authentication. Record before Apply so decode failures survive.
            if (packet.Header.Opcode is >= 0x0009 and <= 0x000F)
                _trace?.Invoke($"Effect receipt opcode={packet.Header.Opcode:X4} clientTime={clientTime} bytes={packet.Payload.Length} payload={Convert.ToHexString(packet.Payload.Span[..Math.Min(packet.Payload.Length,512)])}");
            bool gameplayMaterialized = Gameplay.Apply(packet);
            if (packet.Header.Opcode is 0x006A or 0x006B && gameplayMaterialized)
                _trace?.Invoke($"Client sound receipt opcode={packet.Header.Opcode:X4} event={Gameplay.ClientSoundEvents.Count} value={Gameplay.ClientSoundEvents[^1]}");
            if (packet.Header.Opcode == 0x0056)
                _trace?.Invoke($"Talk action 0056 payload={Convert.ToHexString(packet.Payload.Span)}");
            if (packet.Header.Opcode is >= 0x0093 and <= 0x0096)
                _trace?.Invoke($"Station jobs applied opcode={packet.Header.Opcode:X4} pending={Gameplay.StationJobRequestPending} rows={Gameplay.JobList.Count} description={Gameplay.JobDescription?.JobId} reply={Gameplay.LastJobAcceptReply}");
            Net7WorldApplyResult worldResult = World.Apply(packet, clientTime);
            if (packet.Header.Opcode == 0x0042) World.CaptureServerParametersPresentationMode(Gameplay.PresentationMode);
            // Publish station identity in the same mutation boundary as its
            // packet, including close/tow when no later world packet arrives.
            if (packet.Header.Opcode == 0x004F && Gameplay.LastStarbaseSet is Net7StarbaseSetUpdate starbaseSet)
                PlayerSemantics.ObserveStarbaseSet(starbaseSet);
            else if (packet.Header.Opcode == 0x0050 && Gameplay.StarbaseTow is Net7StarbaseTowUpdate starbaseTow)
                PlayerSemantics.ObserveStarbaseTow(starbaseTow);
            if (worldResult.PacketMaterialized)
                NotifyWorldStateChanged(clientTime);
            else if (gameplayMaterialized && IsPresentationOnlyPacket(packet.Header.Opcode))
                NotifyPresentationStateChanged(clientTime);
            return (gameplayMaterialized, worldResult);
        }
    }

    private static bool IsPresentationOnlyPacket(ushort opcode)
        // Finder and guild replies have no world-object delta. Publish these
        // lists and confirmations even when no movement packet follows.
        => opcode is 0x0053 or 0x00C8 or 0x00CC or 0x00D2 or 0x00D3 or 0x00D8 || IsOtherPresentationOnlyPacket(opcode);

    private static bool IsOtherPresentationOnlyPacket(ushort opcode)
        => opcode is >= 0x0009 and <= 0x000F or >= 0x0093 and <= 0x0096 or 0x001D or 0x001E or 0x00BD or 0x001F or 0x005F or 0x00A2 or 0x00A4 or 0x00A5 or 0x00A6 or 0x00D0 or 0x004F or 0x0050 or 0x0052 or 0x0054 or 0x0056 or 0x005B or 0x005C or 0x0066 or 0x006A or 0x006B or 0x0081 or 0x0083 or 0x0090 or 0x0091;

    private Net7WorldApplyResult ApplyWorldAndPublish(Packet packet, uint clientTime)
    {
        lock (_worldStateSync)
        {
            clientTime = ResolveRecordedTimeSynchronization(packet, clientTime);
            if (packet.Header.Opcode == 0x0034) _connection.TimeAnswered(packet.Payload.Span, clientTime);
            uint? previous = World.CurrentTargetId;
            Net7WorldApplyResult result = World.Apply(packet, clientTime);
            if (packet.Header.Opcode == 0x0042) World.CaptureServerParametersPresentationMode(Gameplay.PresentationMode);
            if (packet.Header.Opcode is 0x001B or 0x001C && World.CurrentTargetId != previous)
            {
                uint? next = World.CurrentTargetId is uint.MaxValue ? null : World.CurrentTargetId;
                uint? local = Gameplay.HasLocalTargetSelection ? Gameplay.LocalSelectedTargetId : previous;
                Gameplay.SetLocalSelectedTarget(next ?? 0);
                if (local != next) result = result with { TargetSelectionChange = new(local,next) };
            }
            if (result.PacketMaterialized)
                NotifyWorldStateChanged(clientTime);
            return result;
        }
    }

    private void ResetGameplayAndWorldForCharacterSelection()
    {
        lock (_worldStateSync)
        {
            ResetFlightInput();
            Gameplay.ResetForCharacterSelection();
            World.ResetForCharacterSelection();
            NotifyWorldStateChanged();
        }
    }

    private void MutateWorldAndPublish(Action mutation, uint? clientTime = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_worldStateSync)
        {
            mutation();
            NotifyWorldStateChanged(clientTime);
        }
    }

    // Must be called while _worldStateSync is held. Subscribers run synchronously
    // before the next world/presentation mutation can enter, so they can build a
    // detached snapshot without racing Net7Session-owned state.
    private void NotifyWorldStateChanged(uint? clientTime = null)
    {
        Action<uint>? worldChanged = WorldStateChanged;
        Action<uint>? presentationChanged = PresentationStateChanged;
        if (worldChanged is null && presentationChanged is null)
            return;

        uint resolvedClientTime = clientTime ?? _clock.GetMilliseconds();
        worldChanged?.Invoke(resolvedClientTime);
        NotifyPresentationStateChanged(resolvedClientTime);
    }

    // Transient presentation state such as effects is not authoritative world
    // state, but it shares the same mutation gate so snapshot builders can copy
    // World + Gameplay.Effects as one coherent presentation source. Clock sampling
    // stays lazy so adding a publication seam cannot perturb protocol-time reads
    // when no presentation consumer is attached.
    private int _presentationBatchDepth;
    private uint? _presentationBatchTime;

    // Only presentation is deferred; decoded mutations and protocol actions keep their order.
    private void SetPresentationBatch(bool begin)
    {
        lock (_worldStateSync)
        {
            if (begin) { _presentationBatchDepth++; return; }
            if (--_presentationBatchDepth == 0 && _presentationBatchTime is uint time)
            {
                _presentationBatchTime = null;
                NotifyPresentationStateChanged(time);
            }
        }
    }

    private void NotifyPresentationStateChanged(uint? clientTime = null)
    {
        Action<uint>? presentationChanged = PresentationStateChanged;
        if (presentationChanged is null)
            return;

        if (_presentationBatchDepth != 0)
        {
            _presentationBatchTime = clientTime ?? _clock.GetMilliseconds();
            PresentationStateDeferred?.Invoke(_presentationBatchTime.Value);
            return;
        }
        presentationChanged(clientTime ?? _clock.GetMilliseconds());
    }

    private void SetLoginState(Net7LoginState state, Action? beforeNotification = null)
    {
        if (LoginState == Net7LoginState.Disposed && state != Net7LoginState.Disposed)
            return;
        if (LoginState == state)
        {
            beforeNotification?.Invoke();
            return;
        }
        LoginState = state;
        _wireCapture?.Mark("net7-login-state", state.ToString());
        // Graceful BA retirement must reach the facade before the general state
        // callback can misclassify a concurrent handoff as entry failure.
        beforeNotification?.Invoke();
        LoginStateChanged?.Invoke(state);
        _trace?.Invoke($"login state -> {state}");
    }

    private void HookMasterEvents(MasterControlUdpChannel channel)
    {
        channel.FrameSent += frame => { _connection.Sent(frame.Opcode, frame.Payload.Span, frame.TotalLength); UdpFrameSent?.Invoke(frame, "master/control"); };
        channel.FrameReceived += frame => { _connection.Received(frame.TotalLength); UdpFrameReceived?.Invoke(frame, "master/control"); };
    }

    private void HookRoutedEvents(RoutedSectorUdpChannel channel)
    {
        channel.FrameSent += (frame, endpoint) => { _connection.Sent(frame.Opcode, frame.Payload.Span, frame.TotalLength); UdpFrameSent?.Invoke(frame, $"routed->{endpoint.Port}"); };
        channel.FrameReceived += (frame, endpoint) => { _connection.Received(frame.TotalLength); UdpFrameReceived?.Invoke(frame, $"routed<-{endpoint.Port}"); };
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(Net7Session));
    }
}
