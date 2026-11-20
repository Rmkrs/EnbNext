namespace EnbNext.Net7.Transport;

/// <summary>
/// Channel identities and routed forwarding state. Proxy 00408060 publishes
/// separate routed/control owners; 00414A00 may change only the control identity.
/// </summary>
public sealed class Net7TransportState
{
    private TaskCompletionSource _gameplayTargetAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task WaitForGameplayForwardingAsync(CancellationToken cancellationToken)
    {
        lock(_sync)return _gameplayForwardingActive?Task.CompletedTask:_gameplayTargetAvailable.Task.WaitAsync(cancellationToken);
    }
    private readonly object _sync = new();
    private uint _fixedSessionId;
    private uint _controlSessionId;
    private int? _confirmedGameplayPort;
    private bool _gameplayForwardingActive;

    public uint FixedSessionId
    {
        get { lock (_sync) return _fixedSessionId; }
    }

    public uint ControlSessionId
    {
        get { lock (_sync) return _controlSessionId; }
    }

    public int? ConfirmedGameplayPort
    {
        get { lock (_sync) return _confirmedGameplayPort; }
    }

    public bool GameplayForwardingActive
    {
        get { lock (_sync) return _gameplayForwardingActive; }
    }

    /// <summary>
    /// Admission rule: zero is admitted; otherwise the incoming ID must
    /// equal the receiving channel identity (proxy 0040C910).
    /// </summary>
    public bool IsIncomingFixedSessionCompatible(uint incomingFixedSessionId, bool controlChannel = false)
    {
        lock (_sync)
            return incomingFixedSessionId == 0 || incomingFixedSessionId == (controlChannel ? _controlSessionId : _fixedSessionId);
    }

    /// <summary>
    /// Assigns the response identity before its success check, as proxy 00414A60 does.
    /// </summary>
    public uint CommitValidatedFixedSessionId(uint fixedSessionId)
    {
        lock (_sync)
        {
            uint previous = _fixedSessionId;
            _fixedSessionId = fixedSessionId;
            _controlSessionId = fixedSessionId;
            return previous;
        }
    }

    /// <summary>
    /// Proxy 00414A00 updates the receiving channel and control, without
    /// changing forwarding. Receive admission already checked compatibility.
    /// </summary>
    public void ApplyFixedSessionControl(uint fixedSessionId, bool controlChannel = false)
    {
        lock (_sync)
        {
            _controlSessionId = fixedSessionId;
            if (!controlChannel) _fixedSessionId = fixedSessionId;
        }
    }

    /// <summary>
    /// Clear session identity and deactivate gameplay forwarding. The numeric
    /// route is deliberately retained because route retention and
    /// route authorization are separate state.
    /// </summary>
    public void ClearSessionIdentity()
    {
        lock (_sync)
        {
            _fixedSessionId = 0;
            _controlSessionId = 0;
            if(_gameplayForwardingActive)_gameplayTargetAvailable=new(TaskCreationOptions.RunContinuationsAsynchronously);
            _gameplayForwardingActive = false;
        }
    }

    /// <summary>Fail-closed route invalidation without changing session identity.</summary>
    public void InvalidateGameplayRoute()
    {
        lock (_sync)
        {
            _confirmedGameplayPort = null;
            if(_gameplayForwardingActive)_gameplayTargetAvailable=new(TaskCreationOptions.RunContinuationsAsynchronously);
            _gameplayForwardingActive = false;
        }
    }

    /// <summary>
    /// Route-state invariant: only a port inside the exact current
    /// gameplay-sector interval may become confirmed route state. Keep this
    /// validation at the state owner as well as at the Master transaction so no
    /// alternate/future caller can manufacture an authoritative-looking route.
    /// </summary>
    public void CommitGameplayPort(int port)
    {
        if (port is < Net7ServerOptions.CurrentMinGameplaySectorUdpPort or > Net7ServerOptions.CurrentMaxGameplaySectorUdpPort)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                $"Current Net7 gameplay-sector UDP ports must be in " +
                $"{Net7ServerOptions.CurrentMinGameplaySectorUdpPort}..{Net7ServerOptions.CurrentMaxGameplaySectorUdpPort}.");
        }

        lock (_sync)
            _confirmedGameplayPort = port;
    }

    public void SetGameplayForwardingActive(bool active)
    {
        lock (_sync)
        {
            // Proxy 0040287B/8D activates on outgoing Login even with ID zero.
            // The send predicate (0040F220), not this transition, suppresses it.
            if(!active && _gameplayForwardingActive)
                _gameplayTargetAvailable=new(TaskCreationOptions.RunContinuationsAsynchronously);
            _gameplayForwardingActive = active;
            if(active)_gameplayTargetAvailable.TrySetResult();
        }
    }

    /// <summary>
    /// Atomically snapshots the three prerequisites for ordinary active-sector
    /// routing. A retained port by itself never authorizes a send.
    /// </summary>
    public bool TryGetActiveGameplayRoute(out uint fixedSessionId, out int port)
    {
        lock (_sync)
        {
            if (!_gameplayForwardingActive || _fixedSessionId == 0 || _confirmedGameplayPort is null)
            {
                fixedSessionId = 0;
                port = 0;
                return false;
            }

            fixedSessionId = _fixedSessionId;
            port = _confirmedGameplayPort.Value;
            return true;
        }
    }
}
