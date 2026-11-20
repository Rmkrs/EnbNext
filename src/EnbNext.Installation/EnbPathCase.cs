using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace EnbNext.Installation;

/// <summary>Retail data was authored for Windows, where file names are not
/// case-sensitive, and retail itself names the same files in different cases
/// (for example "Data" on disk and "../data/client" in its search list). On a
/// case-sensitive file system this resolves each path component to the entry
/// that actually exists. On Windows it returns the path unchanged.</summary>
public static class EnbPathCase
{
    private static readonly bool Sensitive = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
    private static readonly ConcurrentDictionary<string, string[]> Listings = new(StringComparer.Ordinal);

    [return: NotNullIfNotNull(nameof(path))]
    public static string? Resolve(string? path)
    {
        if (!Sensitive || string.IsNullOrEmpty(path)) return path;
        try
        {
            if (File.Exists(path) || Directory.Exists(path)) return path;
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full) ?? "/";
            string[] parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string current = root;
            for (int i = 0; i < parts.Length; i++)
            {
                string exact = Path.Combine(current, parts[i]);
                if (Exists(exact)) { current = exact; continue; }
                string? match = Match(current, parts[i], false) ?? Match(current, parts[i], true);
                if (match is null)
                    return Path.Combine([current, .. parts[i..]]);
                current = Path.Combine(current, match);
            }
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    /// <summary>File.Exists through <see cref="Resolve"/>.</summary>
    public static bool FileExists([NotNullWhen(true)] string? path) => File.Exists(Resolve(path));

    /// <summary>Directory.Exists through <see cref="Resolve"/>.</summary>
    public static bool DirectoryExists([NotNullWhen(true)] string? path) => Directory.Exists(Resolve(path));

    /// <summary>Directory.EnumerateFiles through <see cref="Resolve"/>. Retail
    /// relies on Windows listing order (NTFS: case-insensitive name order) where
    /// the first match wins, such as package registration. On a case-sensitive
    /// file system the pattern matches case-insensitively and the entries are
    /// returned in that same order.</summary>
    public static IEnumerable<string> EnumerateFiles(string directory,string pattern="*",SearchOption option=SearchOption.TopDirectoryOnly)
    {
        if(!Sensitive)return Directory.EnumerateFiles(directory,pattern,option);
        return Ordered(Directory.EnumerateFiles(Resolve(directory),pattern,Options(option)));
    }

    /// <summary>Directory.EnumerateDirectories with the same rules as <see cref="EnumerateFiles"/>.</summary>
    public static IEnumerable<string> EnumerateDirectories(string directory,string pattern="*",SearchOption option=SearchOption.TopDirectoryOnly)
    {
        if(!Sensitive)return Directory.EnumerateDirectories(directory,pattern,option);
        return Ordered(Directory.EnumerateDirectories(Resolve(directory),pattern,Options(option)));
    }

    private static EnumerationOptions Options(SearchOption option)=>new()
    {
        MatchCasing=MatchCasing.CaseInsensitive,
        RecurseSubdirectories=option==SearchOption.AllDirectories,
        IgnoreInaccessible=false,
        AttributesToSkip=0,
    };

    private static string[] Ordered(IEnumerable<string> entries)
    {
        string[] result=entries.ToArray();
        Array.Sort(result,StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string? Match(string directory, string name, bool refresh)
    {
        if (!Directory.Exists(directory)) return null;
        string[] entries = refresh
            ? Listings[directory] = List(directory)
            : Listings.GetOrAdd(directory, List);
        foreach (string entry in entries)
            if (string.Equals(entry, name, StringComparison.OrdinalIgnoreCase)) return entry;
        return null;
    }

    private static string[] List(string directory) =>
        Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).OfType<string>().ToArray();
}
