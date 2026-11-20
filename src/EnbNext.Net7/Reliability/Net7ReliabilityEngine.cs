using System.Buffers.Binary;
using EnbNext.Net7;
using EnbNext.Net7.Protocol;
using EnbNext.Net7.Timing;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Reliability;

/// <summary>
/// Clean implementation of the closed login-cache contract.
/// It preserves protocol-visible ordering/state quirks while rejecting malformed
/// memory hazards safely rather than reproducing Net7Proxy's unchecked accesses.
/// </summary>
public sealed class Net7ReliabilityEngine : IAsyncDisposable
{
    private sealed class CacheEntry
    {
        public Net7ReliabilityEntryState State;
        public ServerUdpFrame? Frame;
        public byte Variant;
    }

    private readonly object _sync = new();
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly Dictionary<uint, CacheEntry> _primary = new();
    private readonly Net7ServerOptions _options;
    private readonly Net7ProcessCompatibilityState _processCompatibilityState;
    private readonly INet7RuntimeClock _clock;
    private readonly Func<bool> _loginCacheActive;
    private readonly Func<ushort, ReadOnlyMemory<byte>, CancellationToken, Task<bool>> _trySendActiveAsync;
    private readonly Func<Packet, CancellationToken, Task<Net7DecodedPacketDisposition>> _specialHandler;
    private readonly Func<Packet, string, CancellationToken, Task> _ordinaryHandler;
    private readonly Action<string>? _trace;
    private readonly Action<bool>? _presentationBatch;
    private readonly List<Task> _recoveryTasks = [];
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;

    private uint _cursor;
    private uint _lastFirstSeen;
    private bool _badDecodeRetryMarker;

    private byte[]? _reassemblyPrefix;
    private int _reassemblyRemaining;
    private int _reassemblyOriginalRemaining;
    private uint _reassemblyStartSequence;

    private bool _restartRequested;
    private bool _hasRestartAttempt;
    private bool _restartSendInFlight;
    private uint _lastRestartTick;
    private bool _intermediateLoginComplete;

    private bool _gateTransitionActive;
    private bool _gateAcknowledgementArm;
    private bool _gateProcessingPaused;
    private bool _gateSequenceScanMode;
    private bool _gateAcknowledgementLatch;
    private uint _gateBoundarySequence;
    private bool _oppositeSideLoginSeen;
    private bool _postGateTransitionByte;
    private uint _savedClientTime;
    private long _gateTransitionGeneration;
    private bool _serverPacketProcessingStopped;

    private int _disposed;

    public Net7ReliabilityEngine(
        Net7ServerOptions options,
        INet7RuntimeClock clock,
        Func<bool> loginCacheActive,
        Func<ushort, ReadOnlyMemory<byte>, CancellationToken, Task<bool>> trySendActiveAsync,
        Func<Packet, CancellationToken, Task<Net7DecodedPacketDisposition>> specialHandler,
        Func<Packet, string, CancellationToken, Task> ordinaryHandler,
        Action<string>? trace = null,
        Net7ProcessCompatibilityState? processCompatibilityState = null,
        Action<bool>? presentationBatch = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processCompatibilityState = processCompatibilityState ?? new Net7ProcessCompatibilityState();
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _loginCacheActive = loginCacheActive ?? throw new ArgumentNullException(nameof(loginCacheActive));
        _trySendActiveAsync = trySendActiveAsync ?? throw new ArgumentNullException(nameof(trySendActiveAsync));
        _specialHandler = specialHandler ?? throw new ArgumentNullException(nameof(specialHandler));
        _ordinaryHandler = ordinaryHandler ?? throw new ArgumentNullException(nameof(ordinaryHandler));
        _trace = trace;
        _presentationBatch = presentationBatch;
    }

    public bool SectorCachingEnabled => (_options.FeatureFlags & Net7FeatureFlags.SectorCaching) != 0;
    public bool PacketOptimisationEnabled => (_options.FeatureFlags & Net7FeatureFlags.UdpPacketOptimisation) != 0;
    public int SplitChunkCeiling => PacketOptimisationEnabled ? 1400 : 512;

    public Net7ReliabilitySnapshot Snapshot()
    {
        lock (_sync)
        {
            var entries = _primary
                .OrderBy(kv => kv.Key)
                .Select(kv => new Net7ReliabilityEntrySnapshot(
                    kv.Key,
                    kv.Value.State,
                    kv.Value.Variant,
                    kv.Value.Frame?.Opcode ?? 0,
                    kv.Value.Frame?.Payload.Length ?? 0))
                .ToArray();

            return new Net7ReliabilitySnapshot(
                _cursor,
                _lastFirstSeen,
                _badDecodeRetryMarker,
                _reassemblyRemaining,
                _reassemblyOriginalRemaining,
                _reassemblyStartSequence,
                _restartRequested,
                _lastRestartTick,
                _processCompatibilityState.ServerGateCachingEnabled,
                _gateTransitionActive,
                _gateAcknowledgementArm,
                _gateProcessingPaused,
                _gateSequenceScanMode,
                _gateAcknowledgementLatch,
                _gateBoundarySequence,
                _oppositeSideLoginSeen,
                _postGateTransitionByte,
                _savedClientTime,
                entries);
        }
    }

    public void StartWorkerIfConfigured()
    {
        ThrowIfDisposed();
        if (!SectorCachingEnabled || _workerTask is not null)
            return;

        var cts = new CancellationTokenSource();
        _workerCts = cts;
        _workerTask = Task.Run(() => RunWorkerAsync(cts.Token));
    }

    public void SetIntermediateLoginComplete(bool complete)
    {
        lock (_sync)
        {
            _intermediateLoginComplete = complete;
            if (complete)
                _restartRequested = false; // clean generation ownership.
        }
    }

    /// <summary>
    /// Special-stream shutdown latch. It suppresses subsequent
    /// decoded/gate processing immediately while the broad shutdown remains on
    /// its separate one-second delay. Incoming cache frames may still be stored.
    /// </summary>
    public void RequestServerPacketProcessingStop()
    {
        lock (_sync)
            _serverPacketProcessingStopped = true;
    }

    /// <summary>
    /// Master-login transition: await a fresh sequence-zero anchor without
    /// clearing retained cache maps. The current proxy writes cursor -1 here; a
    /// subsequent accepted sequence-zero packet owns the generation reset.
    /// </summary>
    public void PrepareForMasterLogin()
    {
        lock (_sync)
        {
            _cursor = uint.MaxValue;
            _intermediateLoginComplete = false;
        }
    }

    /// <summary>Accepts direct outer 0x2016/0x201A admission.</summary>
    public async Task AcceptOuterAsync(ServerUdpFrame frame, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        byte variant = frame.Opcode switch
        {
            0x2016 => 0,
            0x201A => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(frame), $"Opcode 0x{frame.Opcode:X4} is not a REL login-cache outer packet."),
        };

        // The current proxy's cache-copy path sign-extends the 16-bit
        // outer total length. Values with bit 15 set therefore leave the stable
        // protocol domain and enter an allocator/copy memory hazard. Retail may
        // admit such a datagram structurally, but the reliability layer must reject
        // it before generation/cache/recovery state is touched.
        if ((frame.TotalLength & 0x8000) != 0)
        {
            _trace?.Invoke($"REL-05 rejected signed-high outer total length 0x{frame.TotalLength:X4}.");
            return;
        }

        bool accepted;
        bool shouldReissueRestart;
        lock (_sync)
        {
            if (!_loginCacheActive() && !_gateTransitionActive)
                return;

            CacheEntry zero = GetEntryNoCreate(0);
            bool normalZeroReset = frame.Sequence == 0 && zero.State != Net7ReliabilityEntryState.RecoveryOutstanding;
            bool alternateGateReset =
                SignedLessOrEqual(frame.Sequence, 14) &&
                SignedGreaterOrEqual(_cursor, 16) &&
                !_gateSequenceScanMode &&
                _gateTransitionActive;

            if (normalZeroReset || alternateGateReset)
                ResetForIncomingGenerationNoLock();
            else if (!_gateTransitionActive && _cursor == uint.MaxValue && frame.Sequence != 0)
                return;

            // Current outside-login suppression is bypassed during a gate-cache transition.
            // In EnbNext this is represented by the active route gate supplied by the sender;
            // packets that reach this engine are already on the authoritative routed channel.

            CacheEntry existing = GetEntryNoCreate(frame.Sequence);
            if (existing.State == Net7ReliabilityEntryState.Processed)
            {
                accepted = false;
            }
            else
            {
                var replacement = new CacheEntry
                {
                    State = Net7ReliabilityEntryState.Pending,
                    Frame = CloneFrame(frame),
                    Variant = variant,
                };

                bool absent = existing.State == Net7ReliabilityEntryState.Absent;
                _primary[frame.Sequence] = replacement;
                if (absent)
                    _lastFirstSeen = frame.Sequence;
                accepted = true;
            }

            shouldReissueRestart = accepted && !normalZeroReset && !alternateGateReset && _restartRequested;
        }

        if (!accepted)
            return;

        if (shouldReissueRestart)
            await RequestFastRestartAsync("login-cache arrival while restart remains requested", cancellationToken).ConfigureAwait(false);

        if (!SectorCachingEnabled)
            await ProcessOneAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One ordered consumer step, matching the current immediate/worker seam.</summary>
    public async Task ProcessOneAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            uint sequence;
            uint gateScanCursor = 0;
            CacheEntry entry;
            bool gateBranch = false;
            bool gateScan;

            lock (_sync)
            {
                if (_cursor == uint.MaxValue || _gateProcessingPaused)
                    return;

                // Availability is always decided from the ordinary
                // current cursor first. A sticky gate boundary can change which retained
                // packet is scanned only after the current cursor is known to be present
                // (or otherwise resolved). It must never let a retained boundary packet
                // bypass a missing/-3 current sequence.
                CacheEntry currentEntry = GetEntryNoCreate(_cursor);
                if (currentEntry.State == Net7ReliabilityEntryState.Absent)
                {
                    if (SignedLessThan(_cursor, _lastFirstSeen))
                    {
                        _primary[_cursor] = new CacheEntry { State = Net7ReliabilityEntryState.RecoveryOutstanding };
                        LaunchRecoveryNoLock(_cursor);
                        uint boundary = unchecked(_cursor + 25);
                        if (SignedGreaterThan(_lastFirstSeen, boundary))
                            _cursor = unchecked(_cursor + 1);
                    }
                    return;
                }

                if (currentEntry.State == Net7ReliabilityEntryState.RecoveryOutstanding)
                    return;

                if (_gateTransitionActive && _gateSequenceScanMode)
                {
                    gateBranch = true;
                    gateScanCursor = _cursor;
                    sequence = _gateBoundarySequence > 0 ? _gateBoundarySequence : _cursor;
                    entry = GetEntryNoCreate(sequence);
                    if (entry.State != Net7ReliabilityEntryState.Pending || entry.Frame is null)
                        return;
                    gateScan = entry.Variant == 0;
                    _cursor = unchecked(_cursor + 1);
                }
                else
                {
                    sequence = _cursor;
                    entry = currentEntry;
                    gateScan = false;

                    if (entry.State == Net7ReliabilityEntryState.Processed || entry.State == Net7ReliabilityEntryState.Abandoned)
                    {
                        _cursor = unchecked(_cursor + 1);
                        return;
                    }

                    if (entry.Frame is null)
                        return;
                }
            }

            if (gateBranch)
            {
                if (gateScan)
                    await ScanGateMarkersAsync(entry.Frame!, gateScanCursor, cancellationToken).ConfigureAwait(false);
                return;
            }

            await ProcessPendingPacketAsync(sequence, entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _processGate.Release();
        }
    }

    public void ObserveGateActionSubtype(byte subtype)
    {
        lock (_sync)
        {
            switch (subtype)
            {
                case 7:
                case 8:
                    _gateAcknowledgementArm = true;
                    break;
                case 0x12:
                    BeginGateTransitionOperationNoLock(
                        activate: _processCompatibilityState.ServerGateCachingEnabled,
                        arm: _processCompatibilityState.ServerGateCachingEnabled);
                    break;
                case 0x13:
                    if (_processCompatibilityState.ServerGateCachingEnabled)
                        _gateAcknowledgementArm = true;
                    break;
                case 0x1C:
                case 0x1D:
                    BeginGateTransitionOperationNoLock(
                        activate: _processCompatibilityState.ServerGateCachingEnabled,
                        arm: false);
                    break;
            }
        }
    }

    /// <summary>
    /// Clean ownership boundary. Server gate capability is process-scoped,
    /// but a Dock/Land/Gate transition is not. Retire every operation-local gate
    /// latch so a later avatar/session cannot consume or release stale cache state.
    /// </summary>
    public void ResetGateTransitionOwnership()
    {
        lock (_sync)
            ClearGateTransitionOperationNoLock();
    }

    /// <summary>
    /// Gate-cache opposite-side branch. Returns true when ordinary
    /// outgoing 0x0002 must be suppressed.
    /// </summary>
    public bool TryConsumeOutgoingGameplayLogin(uint clientTime)
    {
        lock (_sync)
        {
            if (!_gateTransitionActive)
                return false;
            _oppositeSideLoginSeen = true;
            _savedClientTime = clientTime;
            return true;
        }
    }

    public Packet ApplySavedGateTimeIfNeeded(Packet packet)
    {
        if (packet.Header.Opcode != 0x0034)
            return packet;

        lock (_sync)
        {
            // The first dispatched 0x0034 consumes the saved gate T1
            // regardless of ingress. A clean client rejects short payloads
            // safely, but must not leave the one-shot saved value armed merely
            // because the legacy proxy would have entered an unsafe short write.
            uint savedClientTime = _savedClientTime;
            _savedClientTime = 0;
            if (savedClientTime == 0 || packet.Payload.Length < 4)
                return packet;
            byte[] payload = packet.Payload.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), savedClientTime);
            return Packet.ForOpcode(packet.Header.Opcode, payload);
        }
    }

    /// <summary>
    /// Direct-outer/TCP ordering consumes the saved gate T1 after the
    /// retail-visible packet copy has already been made. Integrated EnbNext has
    /// no copy queue, so model that ordering explicitly: consume the state while
    /// preserving the packet bytes that world materialization sees.
    /// </summary>
    public void ConsumeSavedGateTimeWithoutRewrite(ushort opcode)
    {
        if (opcode != 0x0034)
            return;
        lock (_sync)
            _savedClientTime = 0;
    }

    public void HandleServerHandoff()
    {
        lock (_sync)
        {
            if (!_gateTransitionActive)
            {
                _primary.Clear();
                _cursor = uint.MaxValue;
                // This explicitly rejects Net7Proxy's stale non-cached C8 byte
                // lifetime. A normal handoff ends that operation even though the
                // server gate-capability latch itself remains process-scoped.
                ClearGateTransitionOperationNoLock();
            }
            // Variant map is represented inside each entry, so primary clear covers both maps.
        }
    }

    /// <summary>Special decoded-inner reliability controls.</summary>
    public async Task<Net7DecodedPacketDisposition> HandleReliabilitySpecialAsync(
        Packet packet,
        CancellationToken cancellationToken = default)
    {
        switch (packet.Header.Opcode)
        {
            case 0x2020:
                if (packet.Payload.Length < 4)
                    throw new InvalidDataException("Decoded inner 0x2020 payload is shorter than four bytes.");
                uint stage = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Span[..4]);
                await _trySendActiveAsync(0x2021, Net7ReliabilityWire.BuildLoginStageAcknowledgement(stage), cancellationToken).ConfigureAwait(false);
                return Net7DecodedPacketDisposition.Consumed;

            case 0x202B:
                // Outside the gate pre-scan this special is a handled no-op.
                return Net7DecodedPacketDisposition.Consumed;

            case 0x202E:
                // The decoded-inner
                // translator always consumes 0x202E. Local SectorCaching controls only
                // whether the sticky server-gate capability state is mutated.
                lock (_sync)
                {
                    if (SectorCachingEnabled)
                        _processCompatibilityState.EnableServerGateCaching();
                }
                return Net7DecodedPacketDisposition.Consumed;

            case 0x202D:
                // Likewise 0x202D is always consumed. The server-enable prerequisite
                // gates only C7/C8-equivalent state mutation, never translator ownership.
                lock (_sync)
                {
                    if (_processCompatibilityState.ServerGateCachingEnabled)
                        BeginGateTransitionOperationNoLock(activate: true, arm: true);
                }
                return Net7DecodedPacketDisposition.Consumed;

            default:
                return Net7DecodedPacketDisposition.Unhandled;
        }
    }

    public async Task DrainBackgroundRecoveryAsync()
    {
        Task[] tasks;
        lock (_sync) tasks = _recoveryTasks.ToArray();
        if (tasks.Length != 0)
            await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task ProcessPendingPacketAsync(
        uint sequence,
        CacheEntry entry,
        CancellationToken cancellationToken)
    {
        ServerUdpFrame frame = entry.Frame
            ?? throw new InvalidOperationException("Pending reliability entry has no frame.");
        byte variant = entry.Variant;
        int chunk = SplitChunkCeiling;

        bool activeContinuation = false;
        bool continuationCompleted = false;
        lock (_sync)
        {
            if (variant == 1 && _reassemblyRemaining > 0)
            {
                activeContinuation = true;
                _reassemblyRemaining -= frame.Payload.Length;
                _cursor = unchecked(_cursor + 1);
                continuationCompleted = _reassemblyRemaining <= 0;
            }
            else if (variant == 0 && _reassemblyRemaining > 0)
            {
                // A fresh variant-A packet mechanically abandons the previous partial split.
                ClearReassemblyNoLock();
            }
        }

        if (activeContinuation)
        {
            if (!continuationCompleted)
                return; // retain pointer-valued continuation behind the cursor.

            await FinishReassemblyAsync(sequence, cancellationToken).ConfigureAwait(false);
            return;
        }

        ushort firstWord = frame.Payload.Length >= 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload.Span[..2])
            : (ushort)0;

        // Current server capture 2026-09-16: a complete 516-byte A4 arrives in
        // one variant-A datagram with POPT disabled. The proxy's fixed 512-byte
        // threshold otherwise discards its final four bytes and waits forever
        // for a nonexistent B continuation. Preserve complete ordinary streams;
        // the normal decoder still validates every inner boundary/opcode.
        if (variant == 0 && firstWord > chunk && firstWord <= frame.Payload.Length)
        {
            bool complete = await DecodeInnerStreamAsync(frame.Payload, splitCompletion: false, cancellationToken).ConfigureAwait(false);
            await CompleteOrdinaryDecodeResultAsync(sequence, complete, cancellationToken).ConfigureAwait(false);
            return;
        }

        // On the fresh/no-active-split path the threshold test precedes the
        // variant-B stray check, allowing a threshold-matching stray B to
        // mechanically become a new split starter.
        if (firstWord > chunk && firstWord < 20000)
        {
            if (frame.Payload.Length < chunk)
            {
                await CompleteOrdinaryDecodeResultAsync(sequence, false, cancellationToken).ConfigureAwait(false);
                return;
            }

            lock (_sync)
            {
                _reassemblyPrefix = frame.Payload.Span[..chunk].ToArray();
                _reassemblyRemaining = firstWord - chunk;
                _reassemblyOriginalRemaining = firstWord - chunk;
                _reassemblyStartSequence = sequence;
                MarkProcessedNoLock(sequence);
                _cursor = unchecked(_cursor + 1);
            }
            return;
        }

        if (variant == 1)
        {
            lock (_sync)
            {
                MarkProcessedNoLock(sequence);
                _cursor = unchecked(_cursor + 1);
            }
            return;
        }

        bool ok = await DecodeInnerStreamAsync(frame.Payload, splitCompletion: false, cancellationToken).ConfigureAwait(false);
        await CompleteOrdinaryDecodeResultAsync(sequence, ok, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishReassemblyAsync(uint completionSequence, CancellationToken cancellationToken)
    {
        byte[] prefix;
        uint start;
        int replayRemaining;
        lock (_sync)
        {
            prefix = _reassemblyPrefix?.ToArray()
                ?? throw new InvalidOperationException("Active split has no starter prefix.");
            start = _reassemblyStartSequence;
            replayRemaining = _reassemblyOriginalRemaining;
        }

        int logicalLength = checked(prefix.Length + Math.Max(0, replayRemaining));
        using var stream = new MemoryStream(logicalLength + 64);
        stream.Write(prefix);

        bool abandonedContinuation = false;
        uint seq = unchecked(start + 1);
        while (true)
        {
            CacheEntry current;
            lock (_sync) current = GetEntryNoCreate(seq);

            if (current.State == Net7ReliabilityEntryState.Abandoned)
            {
                abandonedContinuation = true;
                break;
            }
            if (current.State != Net7ReliabilityEntryState.Pending || current.Frame is null)
            {
                // Fast 0x2022 applies specifically to an abandoned required
                // continuation, not arbitrary impossible local replay state. Fail closed
                // here rather than inventing another server-visible restart trigger.
                throw new InvalidDataException(
                    $"Reliability split replay expected pending sequence {seq}, got {current.State}.");
            }

            byte[] raw = current.Frame.Payload.ToArray();
            stream.Write(raw);
            replayRemaining -= raw.Length;
            lock (_sync) MarkProcessedNoLock(seq);

            if (replayRemaining <= 0 || seq == completionSequence)
                break;
            seq = unchecked(seq + 1);
        }

        lock (_sync) ClearReassemblyNoLock();

        if (abandonedContinuation)
        {
            await RequestFastRestartAsync("required split continuation was abandoned", cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] reconstructed = stream.ToArray();

        // Replay is replacement-sensitive: a late shorter replacement can
        // leave the replayed byte count below the starter's original logical length.
        // This classifies the resulting truncated positive record as malformed
        // input that EnbNext should reject safely, while preserving split-failure
        // ownership (fast restart + completion of the current outer sequence).
        // Feed the bytes actually reconstructed to the common decoder instead of
        // throwing out of the reliability worker before that policy can run.
        int decodeLength = Math.Min(logicalLength, reconstructed.Length);

        // This permits a final continuation to overshoot the exact remainder.
        // Net7Proxy's retained starter header limits the decoder's logical pass to
        // the first large inner record, so excess bytes are not a second record.
        bool ok = await DecodeInnerStreamAsync(
            reconstructed.AsMemory(0, decodeLength),
            splitCompletion: true,
            cancellationToken).ConfigureAwait(false);
        if (!ok)
            await RequestFastRestartAsync("fully reconstructed split failed decoded-inner validation", cancellationToken).ConfigureAwait(false);
        // Split completion deliberately leaves the bad-decode marker untouched.
    }

    private async Task<bool> DecodeInnerStreamAsync(
        ReadOnlyMemory<byte> stream,
        bool splitCompletion,
        CancellationToken cancellationToken,
        long? requiredGateGeneration = null)
    {
        _presentationBatch?.Invoke(true);
        try
        {
        int offset = 0;
        byte[] buffer = stream.ToArray();
        while (offset < buffer.Length)
        {
            lock (_sync)
            {
                if (_serverPacketProcessingStopped)
                    return true;
                if (requiredGateGeneration is long generation
                    && (!_gateTransitionActive || _gateTransitionGeneration != generation))
                {
                    return false;
                }
            }

            if (buffer.Length - offset < 4)
                return false;

            ushort rawLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2));
            short signedLength = unchecked((short)rawLength);
            if (signedLength < 0)
            {
                await RequestFastRestartAsync("signed-negative decoded inner length", cancellationToken).ConfigureAwait(false);
                return true;
            }
            if (rawLength < 4)
                return false;
            if (offset + rawLength > buffer.Length)
                return false;

            ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 2, 2));
            byte[] payload = buffer.AsSpan(offset + 4, rawLength - 4).ToArray();
            Packet packet = Packet.ForOpcode(opcode, payload);

            Net7DecodedPacketDisposition disposition;
            try
            {
                disposition = await HandleReliabilitySpecialAsync(packet, cancellationToken).ConfigureAwait(false);
                if (disposition == Net7DecodedPacketDisposition.Unhandled)
                    disposition = await _specialHandler(packet, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                // Safety rule (undefined native reads): a special record too short to
                // decode is consumed and the stream continues with the next record.
                _trace?.Invoke($"decoded inner special 0x{opcode:X4} rejected safely: {ex.Message}");
                disposition = Net7DecodedPacketDisposition.Consumed;
            }

            if (disposition == Net7DecodedPacketDisposition.Unhandled)
            {
                if (opcode is < 0x0001 or > 0x00FE)
                {
                    await RequestFastRestartAsync($"translator-declined decoded inner opcode 0x{opcode:X4}", cancellationToken).ConfigureAwait(false);
                    return false;
                }

                await _ordinaryHandler(packet, splitCompletion ? "reassembled decoded-inner" : "decoded-inner", cancellationToken).ConfigureAwait(false);
            }

            offset += rawLength;
        }

        return true;
        }
        finally { _presentationBatch?.Invoke(false); }
    }

    private async Task CompleteOrdinaryDecodeResultAsync(uint sequence, bool success, CancellationToken cancellationToken)
    {
        bool launchRecovery = false;
        lock (_sync)
        {
            if (success)
            {
                MarkProcessedNoLock(sequence);
                _cursor = unchecked(_cursor + 1);
                _badDecodeRetryMarker = false;
                return;
            }

            if (!_badDecodeRetryMarker)
            {
                _primary[sequence] = new CacheEntry { State = Net7ReliabilityEntryState.RecoveryOutstanding };
                _badDecodeRetryMarker = true;
                launchRecovery = true;
            }
            else
            {
                MarkProcessedNoLock(sequence);
                _badDecodeRetryMarker = false;
                _cursor = unchecked(_cursor + 1);
            }
        }

        if (launchRecovery)
        {
            lock (_sync) LaunchRecoveryNoLock(sequence);
        }
        await Task.CompletedTask;
    }

    private void LaunchRecoveryNoLock(uint sequence)
    {
        Task task = RecoverMissingSequenceAsync(sequence, _lifetimeCts.Token);
        _recoveryTasks.Add(task);
        _ = task.ContinueWith(
            _ => { lock (_sync) _recoveryTasks.Remove(task); },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RecoverMissingSequenceAsync(uint sequence, CancellationToken cancellationToken)
    {
        bool unresolvedAtLastPoll = true;
        for (int i = 0; i < 6; i++)
        {
            lock (_sync)
            {
                CacheEntry current = GetEntryNoCreate(sequence);
                unresolvedAtLastPoll = current.State is Net7ReliabilityEntryState.Absent or Net7ReliabilityEntryState.RecoveryOutstanding;
            }

            if (unresolvedAtLastPoll)
            {
                try
                {
                    await _trySendActiveAsync(
                        0x2017,
                        Net7ReliabilityWire.BuildMissingPacketRequest(sequence),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
                {
                    _trace?.Invoke($"REL-02 0x2017 attempt suppressed/failed: {ex.Message}");
                }
            }

            await _clock.DelayAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            if (!unresolvedAtLastPoll)
                break;
        }

        if (unresolvedAtLastPoll)
        {
            lock (_sync)
            {
                // Binary-faithful final-sleep decision: no seventh recheck.
                _primary[sequence] = new CacheEntry { State = Net7ReliabilityEntryState.Abandoned };
            }
        }
    }

    private async Task RequestFastRestartAsync(string reason, CancellationToken cancellationToken)
    {
        uint gateTick = _clock.GetMilliseconds();
        lock (_sync)
        {
            if (_intermediateLoginComplete || _restartSendInFlight)
                return;
            if (_hasRestartAttempt && !(unchecked(_lastRestartTick + 8000U) < gateTick))
                return;

            // Clean ownership: reserve one in-flight attempt so concurrent
            // corruption/reissue triggers cannot emit duplicate restart packets, but
            // do not commit restart/rate/reassembly state until the active-sector
            // sender confirms that the sequenced 0x2022 was actually emitted.
            _restartSendInFlight = true;
        }

        bool emitted = false;
        try
        {
            emitted = await _trySendActiveAsync(
                0x2022,
                Net7ReliabilityWire.BuildFastLoginRestartPayload(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            _trace?.Invoke($"REL fast restart send suppressed/failed: {ex.Message}");
        }
        finally
        {
            lock (_sync)
            {
                _restartSendInFlight = false;
                if (emitted)
                {
                    _hasRestartAttempt = true;
                    _lastRestartTick = _clock.GetMilliseconds();

                    // A completion racing the transport send owns the newer
                    // generation. Do not resurrect restart-requested state after it.
                    if (!_intermediateLoginComplete)
                    {
                        _restartRequested = true;
                        ClearReassemblyNoLock();
                    }
                }
            }
        }

        if (emitted)
            _trace?.Invoke($"REL fast restart -> sequenced sector 0x2022 emitted: {reason}");
        else
            _trace?.Invoke($"REL fast restart -> 0x2022 suppressed; restart generation not committed: {reason}");
    }

    private async Task ScanGateMarkersAsync(ServerUdpFrame frame, uint cursorAtScan, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            // Stop-latch handling precedes decoded/gate work.
            if (_serverPacketProcessingStopped)
                return;

            // This gates the entire scanner at entry once the local prefix-ready/ACK latch is set.
            // Do not re-check inside the loop: multiple qualifying 0x202B markers in one invocation
            // may each attempt 0x202C before this invocation returns.
            if (_gateAcknowledgementLatch)
                return;
        }

        byte[] payload = frame.Payload.ToArray();
        int offset = 0;
        for (int record = 0; record < 11 && offset + 4 <= payload.Length; record++)
        {
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, 2));
            if (length < 4 || offset + length > payload.Length)
                return;
            ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 2, 2));
            if (opcode == 0x202B)
            {
                bool ready;
                lock (_sync)
                {
                    if (_gateBoundarySequence == 0)
                        _gateBoundarySequence = cursorAtScan;
                    uint boundary = _gateBoundarySequence;
                    ready = true;
                    for (uint seq = 0; seq < boundary; seq++)
                    {
                        CacheEntry e = GetOrCreateAbsentNoLock(seq);
                        if (e.State is Net7ReliabilityEntryState.Absent or Net7ReliabilityEntryState.RecoveryOutstanding)
                        {
                            // The current prefix scanner does not short-circuit.
                            // It continues probing every numeric key below the boundary,
                            // so absent lookups later in the prefix also materialize zero-state entries.
                            ready = false;
                        }
                    }
                }

                if (ready)
                {
                    try
                    {
                        await _trySendActiveAsync(0x202C, Net7ReliabilityWire.BuildGateCacheAcknowledgementPayload(), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
                    {
                        _trace?.Invoke($"REL-08 0x202C attempt suppressed/failed: {ex.Message}");
                    }
                    lock (_sync) _gateAcknowledgementLatch = true;
                }
            }
            offset += length;
        }
    }

    public async Task ProcessGateHandoffAsync(CancellationToken cancellationToken = default)
    {
        ServerUdpFrame? replayZero = null;
        long operationGeneration = 0;
        bool releaseRequested = false;
        lock (_sync)
        {
            if (!_gateTransitionActive)
                return;

            if (_gateAcknowledgementLatch && _gateAcknowledgementArm)
            {
                releaseRequested = true;
                operationGeneration = _gateTransitionGeneration;
                CacheEntry zero = GetEntryNoCreate(0);
                if (zero.State == Net7ReliabilityEntryState.Pending && zero.Frame is not null)
                    replayZero = CloneFrame(zero.Frame);
            }
        }

        if (releaseRequested)
        {
            // Case 17: a missing, non-pending, null, or structurally
            // malformed sequence-zero release unit is not permission to enter the
            // paused rendezvous state. Keep the operation armed and fail closed.
            if (replayZero is null || !IsStructurallyValidGateReleaseStream(replayZero.Payload.Span))
            {
                _trace?.Invoke("MOVE-11 gate release deferred: cached sequence zero is missing or malformed.");
                return;
            }

            bool decoded;
            try
            {
                decoded = await DecodeInnerStreamAsync(
                    replayZero.Payload,
                    splitCompletion: false,
                    cancellationToken,
                    requiredGateGeneration: operationGeneration).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                _trace?.Invoke($"MOVE-11 gate release rejected safely: {ex.Message}");
                return;
            }

            if (!decoded)
                return;

            lock (_sync)
            {
                if (!_gateTransitionActive
                    || _gateTransitionGeneration != operationGeneration
                    || !_gateAcknowledgementLatch
                    || !_gateAcknowledgementArm)
                {
                    return;
                }

                _gateAcknowledgementLatch = false;
                _gateSequenceScanMode = false;
                _gateAcknowledgementArm = false;
                _gateProcessingPaused = true;
            }
        }

        lock (_sync)
        {
            if (_gateTransitionActive && _oppositeSideLoginSeen)
            {
                _gateProcessingPaused = false;
                _gateTransitionActive = false;
                _cursor = 1;
                _oppositeSideLoginSeen = false;
                _postGateTransitionByte = true;
            }
        }
    }

    private static bool IsStructurallyValidGateReleaseStream(ReadOnlySpan<byte> stream)
    {
        int offset = 0;
        while (offset < stream.Length)
        {
            if (stream.Length - offset < 4)
                return false;
            ushort rawLength = BinaryPrimitives.ReadUInt16LittleEndian(stream.Slice(offset, 2));
            if (unchecked((short)rawLength) < 0 || rawLength < 4 || offset + rawLength > stream.Length)
                return false;
            offset += rawLength;
        }
        return offset == stream.Length;
    }

    private void BeginGateTransitionOperationNoLock(bool activate, bool arm)
    {
        _gateTransitionGeneration++;
        _gateTransitionActive = activate;
        _gateAcknowledgementArm = arm;
        _gateProcessingPaused = false;
        _gateSequenceScanMode = false;
        _gateAcknowledgementLatch = false;
        _gateBoundarySequence = 0;
        _oppositeSideLoginSeen = false;
        _postGateTransitionByte = false;
        _savedClientTime = 0;
    }

    private void ClearGateTransitionOperationNoLock()
    {
        BeginGateTransitionOperationNoLock(activate: false, arm: false);
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                uint started = _clock.GetMilliseconds();
                await ProcessOneAsync(cancellationToken).ConfigureAwait(false);
                await ProcessGateHandoffAsync(cancellationToken).ConfigureAwait(false);
                uint finished = _clock.GetMilliseconds();
                uint elapsed = finished >= started ? finished - started : 0;
                int sleepMs = elapsed >= 100 ? 1 : Math.Clamp(100 - checked((int)elapsed), 1, 100);
                await _clock.DelayAsync(TimeSpan.FromMilliseconds(sleepMs), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ResetForIncomingGenerationNoLock()
    {
        _cursor = 0;
        _lastFirstSeen = 0;
        _primary.Clear();
        ClearReassemblyNoLock();
        _restartRequested = false;
        // The bad-decode marker is deliberately not a generation-reset field.
        // It is cleared only by a successful ordinary decode or repeat-bad handling.
        _gateBoundarySequence = 0;
        if (_gateTransitionActive)
            _gateSequenceScanMode = true;
    }

    private void ClearReassemblyNoLock()
    {
        _reassemblyPrefix = null;
        _reassemblyRemaining = 0;
        _reassemblyOriginalRemaining = 0;
        _reassemblyStartSequence = 0;
    }

    private void MarkProcessedNoLock(uint sequence)
        => _primary[sequence] = new CacheEntry { State = Net7ReliabilityEntryState.Processed };

    private CacheEntry GetEntryNoCreate(uint sequence)
        => _primary.TryGetValue(sequence, out CacheEntry? entry)
            ? entry
            : new CacheEntry { State = Net7ReliabilityEntryState.Absent };

    private CacheEntry GetOrCreateAbsentNoLock(uint sequence)
    {
        if (_primary.TryGetValue(sequence, out CacheEntry? entry))
            return entry;
        entry = new CacheEntry { State = Net7ReliabilityEntryState.Absent };
        _primary[sequence] = entry;
        return entry;
    }

    private static bool SignedLessThan(uint left, uint right)
        => unchecked((int)left) < unchecked((int)right);
    private static bool SignedGreaterThan(uint left, uint right)
        => unchecked((int)left) > unchecked((int)right);
    private static bool SignedLessOrEqual(uint left, int right)
        => unchecked((int)left) <= right;
    private static bool SignedGreaterOrEqual(uint left, int right)
        => unchecked((int)left) >= right;

    private static ServerUdpFrame CloneFrame(ServerUdpFrame frame)
        => new(frame.Opcode, frame.FixedSessionId, frame.Sequence, frame.Payload.ToArray());

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { _workerCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        try { _lifetimeCts.Cancel(); }
        catch (ObjectDisposedException) { }
        if (_workerTask is not null)
        {
            try { await _workerTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _workerCts?.Dispose();
        try { await DrainBackgroundRecoveryAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        _lifetimeCts.Dispose();
        _processGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(Net7ReliabilityEngine));
    }
}
