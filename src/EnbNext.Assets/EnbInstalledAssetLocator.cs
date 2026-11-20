namespace EnbNext.Assets;

/// <summary>
/// Locates the installed Earth &amp; Beyond retail data that EnbNext may reuse for
/// local presentation. This is deliberately presentation-only: failure to find
/// the legacy install must never change Net7 protocol/session behavior.
/// </summary>
public static class EnbInstalledAssetLocator
{
    public const string RootEnvironmentVariable = "ENBNEXT_EB_ROOT";
    public const string ClientEnvironmentVariable = "ENBNEXT_EB_CLIENT";

    public static EnbInstalledAssetPaths? TryLocate(
        string? explicitRoot = null,
        string? explicitClientPath = null)
    {
        foreach (string candidate in EnumerateCandidateRoots(explicitRoot, explicitClientPath))
        {
            EnbInstalledAssetPaths? resolved = TryResolveRoot(candidate);
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    public static EnbInstalledAssetPaths? TryResolveRoot(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            return null;

        try
        {
            string root = Path.GetFullPath(rootDirectory.Trim());
            string client = Path.Combine(root, "release", "client.exe");
            string clientData = Path.Combine(root, "Data", "client");
            string clientIni = Path.Combine(clientData, "ini");
            string clientOutput = Path.Combine(clientData, "output");
            string commonData = Path.Combine(root, "Data", "common");
            string basset = Path.Combine(clientIni, "basset.ini");
            string cbasset = Path.Combine(clientIni, "cbasset.ini");
            string effect = Path.Combine(clientIni, "effect.ini");
            string renderStates = Path.Combine(clientIni, "rs.ini");
            string envmaps = Path.Combine(clientIni, "envmaps.ini");
            string spaceCamera = Path.Combine(clientIni, "spacecam.ini");
            string radar = Path.Combine(clientIni, "fc_radar.ini");
            string cockpit = Path.Combine(clientIni, "fc_cpit.ini");
            string fontsys = Path.Combine(clientIni, "fontsys.ini");
            string gutterColor = Path.Combine(clientIni, "gutter_color.ini");
            string metalEquivalentRgbs = Path.Combine(clientIni, "metal_equivalent_rgbs.ini");
            string decalTextures = Path.Combine(commonData, "decal_textures.ini");
            string decalSwatches = Path.Combine(commonData, "decal_swatches.ini");
            string secretDecalTextures = Path.Combine(commonData, "secret_decal_textures.ini");
            string mixFiles = Path.Combine(clientData, "mixfiles");

            if (!EnbNext.Installation.EnbPathCase.FileExists(client) || !EnbNext.Installation.EnbPathCase.FileExists(basset) || !EnbNext.Installation.EnbPathCase.DirectoryExists(mixFiles))
                return null;

            return new EnbInstalledAssetPaths(
                root,
                client,
                clientData,
                clientIni,
                clientOutput,
                commonData,
                basset,
                cbasset,
                effect,
                renderStates,
                envmaps,
                spaceCamera,
                radar,
                cockpit,
                fontsys,
                gutterColor,
                metalEquivalentRgbs,
                decalTextures,
                decalSwatches,
                secretDecalTextures,
                mixFiles);
        }
        catch (Exception ex) when (ex is
            ArgumentException or
            NotSupportedException or
            PathTooLongException or
            System.Security.SecurityException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateCandidateRoots(string? explicitRoot, string? explicitClientPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot)) yield return explicitRoot;
        if (TryRootFromClientPath(explicitClientPath) is {} explicitClientRoot) yield return explicitClientRoot;
        foreach (string root in EnbNext.Installation.EnbInstallDiscovery.RetailCandidates(
            EnbNext.Installation.EnbInstallSettings.Load(), Environment.GetEnvironmentVariable(RootEnvironmentVariable),
            EnbNext.Installation.EnbInstallDiscovery.DetectRetailRoots()))
            yield return root;
    }

    private static string? TryRootFromClientPath(string? clientPath)
    {
        string? normalized = TryNormalize(clientPath);
        if (normalized is null || !EnbNext.Installation.EnbPathCase.FileExists(normalized))
            return null;

        try
        {
            DirectoryInfo? releaseDirectory = Directory.GetParent(normalized);
            return releaseDirectory?.Parent?.FullName;
        }
        catch (Exception ex) when (ex is
            ArgumentException or
            NotSupportedException or
            PathTooLongException or
            System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is
            ArgumentException or
            NotSupportedException or
            PathTooLongException or
            System.Security.SecurityException)
        {
            return null;
        }
    }
}

public sealed record EnbInstalledAssetPaths(
    string RootDirectory,
    string ClientExecutablePath,
    string ClientDataDirectory,
    string ClientIniDirectory,
    string ClientOutputDirectory,
    string CommonDataDirectory,
    string BassetIniPath,
    string CbassetIniPath,
    string EffectIniPath,
    string RsIniPath,
    string EnvironmentMapsIniPath,
    string SpaceCameraIniPath,
    string RadarIniPath,
    string CockpitIniPath,
    string FontSystemIniPath,
    string GutterColorIniPath,
    string MetalEquivalentRgbsIniPath,
    string DecalTexturesIniPath,
    string DecalSwatchesIniPath,
    string SecretDecalTexturesIniPath,
    string MixFilesDirectory)
{
    /// <summary>Current-retail scanner faction display-name catalog.</summary>
    public string FactionIniPath => Path.Combine(ClientIniDirectory, "cfaction_t.ini");

    /// <summary>Current-retail multi-function/target-display text layout.</summary>
    public string MfdIniPath => Path.Combine(ClientIniDirectory, "fc_mfd.ini");

    /// <summary>
    /// Current retail loads the star-field definition from Data/client/ini/stars.ini.
    /// Keep the path derived from the selected legacy installation rather than
    /// copying the authored star definitions into EnbNext.
    /// </summary>
    public string StarsIniPath => Path.Combine(ClientIniDirectory, "stars.ini");

    /// <summary>Current retail audio definition catalog loaded by the sound library.</summary>
    public string SoundsIniPath => Path.Combine(ClientIniDirectory, "sounds.ini");

    /// <summary>Resolves sounds.ini through the same loose-file precedence used by current retail.</summary>
    public bool TryResolveSoundsIniPath(out string path)
        => TryResolveRetailLooseResource("sounds.ini", out path);

    /// <summary>Current-retail type 0x0A influence/nebula environment definition.</summary>
    public string NebulaIniPath => Path.Combine(ClientIniDirectory, "nebula.ini");

    /// <summary>Resolves nebula.ini through the current retail loose-file search precedence.</summary>
    public bool TryResolveNebulaIniPath(out string path)
        => TryResolveRetailLooseResource("nebula.ini", out path);

    /// <summary>
    /// Data/common candidate for the current retail scalar/environment spline database.
    /// Current client resource resolution does not start here: it searches its complete
    /// loose-file path first, beginning in the client executable directory.
    /// </summary>
    public string SplinesWsdPath => Path.Combine(CommonDataDirectory, "splines.wsd");

    /// <summary>
    /// Enumerates the current retail loose-file search path in the order installed by
    /// RunClient @ 0x00526DE0. Net7Proxy launches client.exe with its executable
    /// directory as CWD, so the retail ".\\" entry maps to that directory here.
    /// The MIX manager is a later fallback and is intentionally not represented by
    /// these filesystem candidates.
    /// </summary>
    public IEnumerable<string> EnumerateRetailLooseResourceDirectories()
    {
        string clientDirectory = Path.GetDirectoryName(ClientExecutablePath)
            ?? Path.Combine(RootDirectory, "release");

        yield return clientDirectory;
        yield return Path.Combine(ClientDataDirectory, "art");
        yield return Path.Combine(ClientDataDirectory, "avatar");
        yield return Path.Combine(ClientDataDirectory, "Font");
        yield return Path.Combine(ClientDataDirectory, "htmldocs");
        yield return ClientIniDirectory;
        yield return Path.Combine(ClientDataDirectory, "movie");
        yield return Path.Combine(ClientDataDirectory, "sound");
        yield return Path.Combine(ClientDataDirectory, "sound", "talktree", "eng");
        yield return CommonDataDirectory;
        yield return Path.Combine(CommonDataDirectory, "heightfields");
        yield return ClientDataDirectory;
        yield return ClientOutputDirectory;
    }

    public IEnumerable<string> EnumerateRetailLooseResourcePaths(string logicalResourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalResourceName);

        foreach (string directory in EnumerateRetailLooseResourceDirectories())
            yield return Path.Combine(directory, logicalResourceName);
    }

    /// <summary>
    /// Resolves a loose retail resource exactly by the current client search-path
    /// precedence. An existing earlier candidate wins even if its contents are bad;
    /// parsing is the consumer's responsibility, matching the retail file factory.
    /// </summary>
    public bool TryResolveRetailLooseResource(string logicalResourceName, out string path)
    {
        foreach (string candidate in EnumerateRetailLooseResourcePaths(logicalResourceName))
        {
            if (!EnbNext.Installation.EnbPathCase.FileExists(candidate))
                continue;

            path = candidate;
            return true;
        }

        path = string.Empty;
        return false;
    }

    /// <summary>
    /// Resolves the current retail sector-orbital definition keyed by 0x0042
    /// SectorNum through the same loose-resource manager used for splines.wsd.
    /// </summary>
    public bool TryResolveOrbitalIniPath(uint sectorNum, out string path)
        => TryResolveRetailLooseResource($"orbital{sectorNum:0000}.ini", out path);

    /// <summary>
    /// Resolves the current-retail MainView directional-light rig for a sector.
    /// The client formats this logical resource as light_sector%04d.ini and
    /// resolves it through the same loose-file search path as orbital data.
    /// </summary>
    public bool TryResolveMainViewLightSectorIniPath(uint sectorNum, out string path)
        => TryResolveRetailLooseResource($"light_sector{sectorNum:0000}.ini", out path);
}
