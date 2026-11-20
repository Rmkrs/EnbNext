namespace EnbNext.Assets;

/// <summary>RunClient00526F4F..76 configures009D0C60's loose search before
/// 004E57A0 checks the package namespace. The working directory is the retail
/// executable directory supplied by the installation, not Next's process CWD.</summary>
public sealed class EnbRetailFileResolver(string workingDirectory, EnbMixResourceCatalog? packages)
{
    //Complete null-terminated RunClient literal00B7023C, in native order.
    private static readonly string[] SearchPaths =
    [
        ".", "../data/client/art", "../data/client/avatar", "../data/client/Font",
        "../data/client/htmldocs", "../data/client/ini", "../data/client/movie",
        "../data/client/sound", "../data/client/sound/talktree/eng", "../data/common",
        "../data/common/heightfields", "../data/client", "../data/client/output",
    ];
    private readonly string _workingDirectory = Path.GetFullPath(workingDirectory);

    /// <summary>Read through the configured provider while retaining a uniform
    /// source identity for loose files and package members.</summary>
    public bool TryReadResource(string name,out byte[] bytes,out EnbMixResourceLocation location)
    {
        if(TryRead(name,out bytes,out var package,out var loose))
        {
            location=package??new(name,loose!,0,bytes.Length);
            return true;
        }
        location=null!;
        return false;
    }

    /// <summary>Aggregate009A3B00's extra load after ordinary acquisition
    /// misses: current directory + the entire prototype name + .w3d.</summary>
    public bool TryReadAggregateWorkingDirectoryFile(string prototypeName, out byte[] bytes,
        out EnbMixResourceLocation? packageLocation, out string? loosePath)
    {
        bytes = []; packageLocation = null; loosePath = null;
        string name = _workingDirectory + Path.DirectorySeparatorChar + prototypeName + ".w3d";
        try { _ = File.GetAttributes(EnbNext.Installation.EnbPathCase.Resolve(name)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
        return TryRead(name, out bytes, out packageLocation, out loosePath);
    }

    public bool TryRead(string name, out byte[] bytes, out EnbMixResourceLocation? packageLocation, out string? loosePath)
    {
        bytes = []; packageLocation = null; loosePath = null;
        string nativeName = name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        //009D0D7D..9F: a drive prefix or leading double backslash bypasses
        //the configured semicolon list. Other names retain their directories.
        bool direct = name.Length >= 2 && (name[1] == ':' || name[0] == '\\' && name[1] == '\\')
            // A rooted path on a Unix file system is the same kind of direct name.
            || !OperatingSystem.IsWindows() && Path.IsPathRooted(nativeName);
        foreach (string directory in direct ? new[] { _workingDirectory } : SearchPaths.Select(path => Path.Combine(_workingDirectory,path)))
        {
            string candidate;
            try { candidate = direct ? Path.GetFullPath(nativeName,directory)
                : Path.GetFullPath(directory + Path.DirectorySeparatorChar + nativeName); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            try
            {
                //009D0E38..63 tests successful read-open for each candidate.
                bytes = File.ReadAllBytes(EnbNext.Installation.EnbPathCase.Resolve(candidate));
                loosePath = candidate;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (packages is not null && packages.TryReadResource(name,out bytes,out var location))
        {
            packageLocation = location;
            return true;
        }
        bytes = [];
        return false;
    }
}
