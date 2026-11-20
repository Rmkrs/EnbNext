namespace EnbNext.Net7;

/// <summary>
/// Direct Net7 endpoints plus current-binary retry contracts and explicitly named
/// clean-client fail-closed policies. Values marked as safety policy are not server
/// protocol facts and must not be treated as such.
/// </summary>
public sealed record Net7ServerOptions
{
    // Current endpoint roles. Host is an EnbNext product default; ports are
    // current server topology. Keeping the distinction explicit prevents a DNS
    // default from acquiring protocol authority.
    public const string DefaultHost = "sunrise.net-7.org";
    public const int CurrentMasterControlUdpPort = 3806;
    public const int CurrentAccountTcpPort = 3807;
    public const int CurrentMasterHandoffUdpPort = 3808;

    // Exact accepted dynamic gameplay-sector interval.
    public const int CurrentMinGameplaySectorUdpPort = 3501;
    public const int CurrentMaxGameplaySectorUdpPort = 4000;

    public string Host { get; init; } = DefaultHost;
    public int MasterControlUdpPort { get; init; } = CurrentMasterControlUdpPort;
    public int AccountTcpPort { get; init; } = CurrentAccountTcpPort;
    public int MasterHandoffUdpPort { get; init; } = CurrentMasterHandoffUdpPort;

    // Exact bootstrap cadence.
    public int BootstrapAttempts { get; init; } = 5;
    public TimeSpan BootstrapAttemptTimeout { get; init; } = TimeSpan.FromSeconds(1);

    // Retain the current proxy's nominal 20-second, no-resend
    // account/avatar wait policy. This is implementation policy, not a server
    // protocol timeout; response ownership remains transaction-specific.
    public TimeSpan AccountResponseTimeout { get; init; } = TimeSpan.FromSeconds(20);

    // Exact generic request cadence. These are
    // protocol-compatibility constants, not product/runtime configuration.
    // Internal overrides exist only so deterministic tests can exercise retry
    // boundaries without sleeping through the real twenty-second envelope.
    public const int CurrentGenericRequestAttempts = 20;
    public const int CurrentGenericResponsePollsPerAttempt = 4;
    public static readonly TimeSpan CurrentGenericResponsePollInterval = TimeSpan.FromMilliseconds(250);

    internal int MasterRequestAttempts { get; init; } = CurrentGenericRequestAttempts;
    internal int MasterResponsePollsPerAttempt { get; init; } = CurrentGenericResponsePollsPerAttempt;
    internal TimeSpan MasterResponsePollInterval { get; init; } = CurrentGenericResponsePollInterval;

    // Current watchdog cadence. The fourth send receives no extra
    // grace period before fail-closed cleanup.
    public int IntermediateLoginWatchdogSends { get; init; } = 4;
    public int IntermediateLoginPollsPerCycle { get; init; } = 50;
    public TimeSpan IntermediateLoginPollInterval { get; init; } = TimeSpan.FromSeconds(5);


    /// <summary>
    /// Current optional 0x2008 feature bits. The reliability layer implements the
    /// consequences of 0x01 POPT, 0x02 SP-mode advertisement, and 0x04
    /// sector-cache/reorder capability. Unknown bits remain fail-closed.
    /// </summary>
    public Net7FeatureFlags FeatureFlags { get; init; } = Net7FeatureFlags.None;
}

[Flags]
public enum Net7FeatureFlags : byte
{
    None = 0,
    UdpPacketOptimisation = 0x01,
    SpMode = 0x02,
    SectorCaching = 0x04,
}
