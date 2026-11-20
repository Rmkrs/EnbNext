using EnbNext.Installation;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.LocalData;

/// <summary>
/// Launcher-owned local data, retained together for the selected installation.
/// </summary>
public static class Net7LocalDataAssets
{
    private static readonly object Sync = new();
    private static InstalledNet7Data? current;

    public static Net7GalaxyMapData CurrentGalaxyMap => Current.GalaxyMap;
    public static Net7CDataCatalog CurrentCData => Current.CData;

    private static InstalledNet7Data Current
    {
        get
        {
            string folder = EnbInstallDiscovery.RequireLauncher();
            lock (Sync)
            {
                if (current?.Folder != folder) current = LoadFromFolder(folder);
                return current;
            }
        }
    }

    public static InstalledNet7Data LoadFromFolder(string folder)
    {
        folder = Path.GetFullPath(folder);
        Net7CDataCatalog cdata = Read(Path.Combine(folder, "bin", "cdata.dat"), bytes => Net7CDataCatalog.Decode(bytes));
        Net7GalaxyMapData map = Read(Path.Combine(folder, "database", "GalaxyMap.dat"), bytes => Net7GalaxyMapCodec.DecodeCurrentGeometry(bytes));
        return new(folder, cdata, map);
    }

    private static T Read<T>(string path, Func<byte[], T> decode)
    {
        try { return decode(File.ReadAllBytes(EnbNext.Installation.EnbPathCase.Resolve(path))); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
        {
            throw new Net7LocalDataException($"Unable to load Net-7 launcher data '{path}'. Please select a complete Net-7 installation and update it with the launcher. {ex.Message}", ex);
        }
    }
}

public sealed record InstalledNet7Data(string Folder, Net7CDataCatalog CData, Net7GalaxyMapData GalaxyMap);

public sealed class Net7LocalDataException(string message, Exception inner) : IOException(message, inner);
