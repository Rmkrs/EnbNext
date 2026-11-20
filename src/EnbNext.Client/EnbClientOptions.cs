using EnbNext.Net7;

namespace EnbNext.Client;

public sealed record EnbClientOptions
{
    public string? TextureOverrideDirectory { get; init; }
    public string? TexturePack { get; init; }
    public bool TextureOverridesEnabled {get;init;} = true;
    public string ServerHost { get; init; } = Net7ServerOptions.DefaultHost;
    public int MasterControlUdpPort { get; init; } = Net7ServerOptions.CurrentMasterControlUdpPort;
    public int AccountTcpPort { get; init; } = Net7ServerOptions.CurrentAccountTcpPort;
    public int MasterHandoffUdpPort { get; init; } = Net7ServerOptions.CurrentMasterHandoffUdpPort;

    /// <summary>
    /// Directory for automatic raw wire captures of the character-selection ->
    /// sector-entry operation. Set to null/empty to disable capture. The capture
    /// starts before TCP 0x2004 and therefore does not contain the login password
    /// or the EATicket-bearing TCP 0x2000/0x2002 transactions.
    /// </summary>
    public string? WireCaptureDirectory { get; init; } = Path.Combine(Environment.CurrentDirectory, "captures");

    /// <summary>
    /// Bounded passive capture tail after the sector route reaches InWorld.
    /// This lets naturally arriving post-entry object updates remain visible
    /// without leaving automatic captures open for the full play session.
    /// </summary>
    public TimeSpan SectorEntryPostInWorldCaptureDuration { get; init; } = TimeSpan.FromSeconds(30);

    // This keeps the launcher-era game-server and authentication route
    // semantics explicit and independent without reproducing retail INI,
    // registry, AuthLogin.dll mutation, or local-certificate machinery.
    // The current integrated Net7 account path remains the direct server
    // protocol; these values model the distinct native auth-route profile.
    public string AuthenticationHost { get; init; } = Net7ServerOptions.DefaultHost;
    public int AuthenticationPort { get; init; } = 443;
    public bool AuthenticationSecureTransport { get; init; } = true;

    public Uri AuthenticationRouteBaseUri => new UriBuilder(
        AuthenticationSecureTransport ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
        AuthenticationHost,
        AuthenticationPort).Uri;

    // 2C local-presentation discovery. These are optional overrides only; the
    // locator otherwise uses profile settings, ENBNEXT_EB_ROOT and detected
    // installations. Missing retail art never gates networking.
    public string? EarthAndBeyondRoot { get; init; }
    public string? EarthAndBeyondClientPath { get; init; }
}
