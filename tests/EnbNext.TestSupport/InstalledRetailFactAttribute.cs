using EnbNext.Assets;
using EnbNext.Installation;
using Xunit;

namespace EnbNext.Net7.Tests;

/// <summary>A test that reads the installed Earth &amp; Beyond and Net-7 launcher data; skipped where they are not installed (as on the release build machines).</summary>
public sealed class InstalledRetailFactAttribute : FactAttribute
{
    public InstalledRetailFactAttribute(bool windowsOnly = false) => Skip = InstalledRetail.SkipReason(windowsOnly);
}

/// <summary>The theory form of <see cref="InstalledRetailFactAttribute"/>.</summary>
public sealed class InstalledRetailTheoryAttribute : TheoryAttribute
{
    public InstalledRetailTheoryAttribute(bool windowsOnly = false) => Skip = InstalledRetail.SkipReason(windowsOnly);
}

internal static class InstalledRetail
{
    private static readonly Lazy<string?> Missing = new(() =>
        EnbInstalledAssetLocator.TryLocate() is null ? "Earth & Beyond is not installed; configure EnbNext/install.json or ENBNEXT_EB_ROOT."
        : EnbInstallDiscovery.FindLauncher() is null ? "The Net-7 launcher is not installed."
        : null);

    public static string? SkipReason(bool windowsOnly) =>
        windowsOnly && !OperatingSystem.IsWindows() ? "This installed-retail check requires Windows." : Missing.Value;
}
