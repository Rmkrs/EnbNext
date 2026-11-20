namespace EnbNext.Net7;

/// <summary>
/// Compatibility state whose authoritative lifetime is the EnbNext client process,
/// rather than one network-session object. Server gate-cache
/// permission, once accepted, has no current clear until process exit.
/// likewise proves that the patched RTT global and both periodic Request_Time
/// counters are process-global and have no reset outside their owning writers.
/// </summary>
public sealed class Net7ProcessCompatibilityState
{
    private readonly object _clientTimeSync = new();
    private int _serverGateCachingEnabled;
    private long _patchedRttMilliseconds;
    private int _advancedRequestTimeCounter;
    private int _formationRequestTimeCounter;

    public bool ServerGateCachingEnabled => Volatile.Read(ref _serverGateCachingEnabled) != 0;

    public long PatchedRttMilliseconds
    {
        get
        {
            lock (_clientTimeSync)
                return _patchedRttMilliseconds;
        }
    }

    public int AdvancedRequestTimeCounter
    {
        get
        {
            lock (_clientTimeSync)
                return _advancedRequestTimeCounter;
        }
    }

    public int FormationRequestTimeCounter
    {
        get
        {
            lock (_clientTimeSync)
                return _formationRequestTimeCounter;
        }
    }

    public void EnableServerGateCaching()
        => Interlocked.Exchange(ref _serverGateCachingEnabled, 1);

    public void SetPatchedRttMilliseconds(long value)
    {
        lock (_clientTimeSync)
            _patchedRttMilliseconds = value;
    }

    public bool ObserveQualifyingLocalAdvancedUpdate()
    {
        lock (_clientTimeSync)
            return AdvanceRequestTimeCounter(ref _advancedRequestTimeCounter);
    }

    public bool ObserveQualifyingLocalFormationUpdate()
    {
        lock (_clientTimeSync)
            return AdvanceRequestTimeCounter(ref _formationRequestTimeCounter);
    }

    private static bool AdvanceRequestTimeCounter(ref int counter)
    {
        counter++;
        if (counter <= 0x20)
            return false;
        counter = 0;
        return true;
    }
}
