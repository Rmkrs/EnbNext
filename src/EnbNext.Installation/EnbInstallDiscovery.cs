using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace EnbNext.Installation;

public static class EnbInstallDiscovery
{
    public static IEnumerable<string> RetailCandidates(EnbInstallSettings settings, string? environmentRoot,
        IEnumerable<string> detected)
    {
        if (!string.IsNullOrWhiteSpace(settings.EarthAndBeyondFolder)) yield return settings.EarthAndBeyondFolder;
        if (!string.IsNullOrWhiteSpace(environmentRoot)) yield return environmentRoot;
        foreach (string root in detected) yield return root;
    }

    public static IEnumerable<string> DetectRetailRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Retail and the Net-7 launcher are Windows programs; on Linux they live in a Wine prefix.
            foreach (string prefix in WinePrefixes())
            {
                foreach (var root in WineRoots(prefix, false)) yield return root;
                foreach (string file in WineLauncherConfigurationFiles(prefix))
                    if (ReadLauncherClientPath(file) is {} client &&
                        WinePath(prefix, WindowsDirectoryName(WindowsDirectoryName(client))) is {} root) yield return root;
                yield return Path.Combine(prefix, "drive_c", "Games", "Earth & Beyond");
            }
            yield return HomeFolder("Earth & Beyond");
            yield break;
        }
        foreach (var root in RegisteredRoots(false)) yield return root;
        string launcherSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchNet7");
        foreach (string file in LauncherConfigurationFiles(launcherSettings))
            if (ReadLauncherClientRoot(file) is {} root) yield return root;
        yield return Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Games", "Earth & Beyond");
    }

    public static string? FindLauncher(EnbInstallSettings? settings = null)
    {
        settings ??= EnbInstallSettings.Load();
        if (IsLauncherFolder(settings.Net7Folder)) return Path.GetFullPath(EnbPathCase.Resolve(settings.Net7Folder!));
        if (!OperatingSystem.IsWindows())
        {
            foreach (string prefix in WinePrefixes())
            {
                foreach (string root in WineRoots(prefix, true))
                    if (IsLauncherFolder(root)) return Path.GetFullPath(EnbPathCase.Resolve(root));
                string prefixed = Path.Combine(prefix, "drive_c", "Games", "Net-7");
                if (IsLauncherFolder(prefixed)) return Path.GetFullPath(EnbPathCase.Resolve(prefixed));
            }
            string home = HomeFolder("Net-7");
            return IsLauncherFolder(home) ? Path.GetFullPath(EnbPathCase.Resolve(home)) : null;
        }
        foreach (string root in RegisteredRoots(true))
            if (IsLauncherFolder(root)) return Path.GetFullPath(root);
        string fallback = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Games", "Net-7");
        return IsLauncherFolder(fallback) ? fallback : null;
    }

    public static bool IsLauncherFolder(string? root) => !string.IsNullOrWhiteSpace(root) &&
        EnbPathCase.FileExists(Path.Combine(root, "bin", "cdata.dat")) && EnbPathCase.FileExists(Path.Combine(root, "database", "GalaxyMap.dat"));

    public static string RequireLauncher() => FindLauncher() ?? throw new DirectoryNotFoundException(
        "The Net-7 launcher folder was not found. Select the folder containing bin/cdata.dat and database/GalaxyMap.dat.");

    public static string? ReadLauncherClientRoot(string file) =>
        ReadLauncherClientPath(file) is {} client ? Path.GetDirectoryName(Path.GetDirectoryName(client)) : null;

    private static string? ReadLauncherClientPath(string file)
    {
        try
        {
            string? client = XDocument.Load(file).Descendants("setting")
                .FirstOrDefault(x => (string?)x.Attribute("name") == "ClientPath")?.Element("value")?.Value;
            return string.IsNullOrWhiteSpace(client) ? null : client;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException) { return null; }
    }

    private static string[] LauncherConfigurationFiles(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetFiles(root, "user.config", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegisteredRoots(bool launcher)
    {
        var roots = new List<string>();
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var basis = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = basis.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(name);
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string value in new[] { "DisplayName", "InstallLocation", "UninstallString", "Readme" })
                        if (entry?.GetValue(value) is string text) values[value] = text;
                    AddUninstallRoots(roots, launcher, name, values, path => path);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { }
        }
        return roots;
    }

    private static void AddUninstallRoots(List<string> roots, bool launcher, string name,
        IReadOnlyDictionary<string, string> values, Func<string, string?> map)
    {
        values.TryGetValue("DisplayName", out string? display);
        if (launcher ? name != "EnBEMU" : display != "Earth & Beyond") return;
        void Add(string? path) { if (!string.IsNullOrWhiteSpace(path) && map(path) is {} mapped) roots.Add(mapped); }
        if (values.TryGetValue("InstallLocation", out string? location)) Add(location);
        if (launcher && values.TryGetValue("UninstallString", out string? command))
            Add(WindowsDirectoryName(command.StartsWith('"') ? command.Split('"')[1] : command));
        if (!launcher && values.TryGetValue("Readme", out string? readme) && !string.IsNullOrWhiteSpace(readme))
            Add(WindowsDirectoryName(WindowsDirectoryName(readme)));
    }

    // Windows paths read from the registry or launcher settings, split on '\' on every platform.
    private static string? WindowsDirectoryName(string? path)
    {
        if (OperatingSystem.IsWindows()) return Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(path)) return null;
        int end = path.TrimEnd('\\', '/').LastIndexOfAny(['\\', '/']);
        return end <= 0 ? null : path[..end];
    }

    private static string HomeFolder(string name) =>
        EnbPathCase.Resolve(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", name));

    // Wine (WINEPREFIX or ~/.wine), Lutris (~/Games/*), Bottles and Steam Proton prefixes.
    private static IEnumerable<string> WinePrefixes()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("WINEPREFIX") is { Length: > 0 } configured) candidates.Add(configured);
        candidates.Add(Path.Combine(home, ".wine"));
        candidates.AddRange(Subdirectories(Path.Combine(home, "Games")));
        candidates.AddRange(Subdirectories(Path.Combine(home, ".local", "share", "bottles", "bottles")));
        foreach (string steam in new[] { Path.Combine(home, ".steam", "steam"), Path.Combine(home, ".local", "share", "Steam") })
            candidates.AddRange(Subdirectories(Path.Combine(steam, "steamapps", "compatdata")).Select(x => Path.Combine(x, "pfx")));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string prefix in candidates)
        {
            string full;
            try { full = Path.GetFullPath(prefix); } catch (Exception ex) when (ex is ArgumentException or PathTooLongException) { continue; }
            if (Directory.Exists(Path.Combine(full, "drive_c")) && seen.Add(full)) yield return full;
        }
    }

    private static string[] Subdirectories(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static List<string> WineRoots(string prefix, bool launcher)
    {
        var roots = new List<string>();
        foreach (var (name, values) in WineUninstallEntries(prefix))
            AddUninstallRoots(roots, launcher, name, values, path => WinePath(prefix, path));
        return roots;
    }

    private static IEnumerable<string> WineLauncherConfigurationFiles(string prefix) =>
        Subdirectories(Path.Combine(prefix, "drive_c", "users"))
            .SelectMany(user => new[] { Path.Combine(user, "AppData", "Local", "LaunchNet7"), Path.Combine(user, "Local Settings", "Application Data", "LaunchNet7") })
            .SelectMany(LauncherConfigurationFiles);

    /// <summary>Maps a Windows drive path into the prefix through dosdevices (drive_c for C:).</summary>
    private static string? WinePath(string prefix, string? windowsPath)
    {
        string path = windowsPath?.Trim() ?? "";
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0])) return null;
        char letter = char.ToLowerInvariant(path[0]);
        string drive = Path.Combine(prefix, "dosdevices", letter + ":");
        if (!Directory.Exists(drive))
        {
            if (letter != 'c') return null;
            drive = Path.Combine(prefix, "drive_c");
        }
        string rest = path[2..].Replace('\\', '/').Trim('/');
        return EnbPathCase.Resolve(rest.Length == 0 ? drive : Path.Combine(drive, rest));
    }

    // Wine's system.reg (HKLM) and user.reg (HKCU) are text files of [key] sections and "name"="value" lines.
    private static List<(string Name, Dictionary<string, string> Values)> WineUninstallEntries(string prefix)
    {
        string[] uninstallKeys = [@"Software\Microsoft\Windows\CurrentVersion\Uninstall\", @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\"];
        var entries = new List<(string, Dictionary<string, string>)>();
        foreach (string file in new[] { "user.reg", "system.reg" })
        {
            string[] lines;
            try
            {
                string path = Path.Combine(prefix, file);
                if (!File.Exists(path)) continue;
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Dictionary<string, string>? current = null;
            foreach (string line in lines)
            {
                if (line.StartsWith('['))
                {
                    current = null;
                    int end = line.IndexOf(']');
                    if (end < 0) continue;
                    string key = line[1..end].Replace(@"\\", @"\");
                    foreach (string root in uninstallKeys)
                        if (key.Length > root.Length && key.StartsWith(root, StringComparison.OrdinalIgnoreCase) && key.IndexOf('\\', root.Length) < 0)
                        {
                            current = new(StringComparer.OrdinalIgnoreCase);
                            entries.Add((key[root.Length..], current));
                        }
                    continue;
                }
                if (current is null || !line.StartsWith('"') || !ReadQuoted(line, 0, out string name, out int next)) continue;
                if (next >= line.Length || line[next] != '=') continue;
                int start = next + 1;
                if (line.AsSpan(start).StartsWith("str(2):")) start += 7;
                if (start < line.Length && line[start] == '"' && ReadQuoted(line, start, out string value, out _)) current[name] = value;
            }
        }
        return entries;
    }

    private static bool ReadQuoted(string line, int start, out string value, out int next)
    {
        var text = new StringBuilder();
        for (int i = start + 1; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') { value = text.ToString(); next = i + 1; return true; }
            if (c != '\\' || i + 1 >= line.Length) { text.Append(c); continue; }
            char escaped = line[++i];
            if (escaped == 'x')
            {
                int digits = 0;
                while (digits < 4 && i + 1 + digits < line.Length && char.IsAsciiHexDigit(line[i + 1 + digits])) digits++;
                if (digits > 0) { text.Append((char)int.Parse(line.AsSpan(i + 1, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += digits; }
                continue;
            }
            text.Append(escaped switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', _ => escaped });
        }
        value = ""; next = line.Length; return false;
    }
}
