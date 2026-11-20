using EnbNext.Assets;
using EnbNext.Host.Sdl;
using EnbNext.Installation;

namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private async Task<EnbInstalledAssetPaths?> LocateRetailAsync()
    {
        if (EnbInstalledAssetLocator.TryLocate() is {} found)
        {
            if (!hidden)
            {
                var settings = EnbInstallSettings.Load();
                var resolved = settings with { EarthAndBeyondFolder = found.RootDirectory, Net7Folder = EnbInstallDiscovery.FindLauncher(settings) ?? settings.Net7Folder };
                if (resolved != settings || !File.Exists(EnbInstallSettings.DefaultPath)) resolved.Save();
            }
            return found;
        }
        if (hidden) throw new DirectoryNotFoundException("Earth & Beyond was not found. Configure EnbNext/install.json or ENBNEXT_EB_ROOT.");
        while (!_frontendClosed)
        {
            window.Message("EnbNext — game installation", "Please select your Earth & Beyond installation folder to load the login screen.");
            string? folder = await SdlFolderPicker.Show(window);
            if (_frontendClosed || folder is null) return null;
            if (EnbInstalledAssetLocator.TryResolveRoot(folder) is {} paths)
            {
                (EnbInstallSettings.Load() with { EarthAndBeyondFolder = paths.RootDirectory }).Save();
                return paths;
            }
            window.Message("EnbNext — game installation", "That folder does not contain the installed game. Choose the Earth & Beyond folder containing release and Data.");
        }
        return null;
    }

    // Asked straight after the game folder, before the login screen; a
    // cancelled choice falls back to EnsureLauncherAsync at login.
    private async Task AskLauncherFolderAsync()
    {
        if (hidden || EnbInstallDiscovery.FindLauncher() is not null) return;
        while (!_frontendClosed)
        {
            window.Message("EnbNext — Net-7 launcher", "Please select your Net-7 folder containing bin/cdata.dat and database/GalaxyMap.dat.");
            string? folder = await SdlFolderPicker.Show(window);
            if (_frontendClosed || folder is null) return;
            if (!EnbInstallDiscovery.IsLauncherFolder(folder)) continue;
            (EnbInstallSettings.Load() with { Net7Folder = Path.GetFullPath(folder) }).Save();
            return;
        }
    }

    private async Task<bool> EnsureLauncherAsync()
    {
        if (EnbInstallDiscovery.FindLauncher() is not null) return true;
        if (hidden) throw new DirectoryNotFoundException("The Net-7 launcher folder was not found.");
        while (!_frontendClosed)
        {
            await _frontend!.ShowMessageAsync("The Net-7 launcher folder was not found. Please select your Net-7 folder containing bin/cdata.dat and database/GalaxyMap.dat.");
            if (_frontendClosed) return false;
            string? folder = await SdlFolderPicker.Show(window);
            if (_frontendClosed || folder is null) return false;
            if (!EnbInstallDiscovery.IsLauncherFolder(folder)) continue;
            (EnbInstallSettings.Load() with { Net7Folder = Path.GetFullPath(folder) }).Save();
            return true;
        }
        return false;
    }
}
