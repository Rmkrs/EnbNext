using EnbNext.Assets;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class InstalledRetailFactAttribute : FactAttribute
{
    public InstalledRetailFactAttribute(bool windowsOnly = false)
    {
        if (windowsOnly && !OperatingSystem.IsWindows()) Skip = "This installed-retail check requires Windows.";
        else if (EnbInstalledAssetLocator.TryLocate() is null) Skip = "Earth & Beyond is not installed; configure EnbNext/install.json or ENBNEXT_EB_ROOT.";
    }
}
