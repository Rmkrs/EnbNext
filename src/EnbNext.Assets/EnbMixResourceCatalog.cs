using System.Text;

namespace EnbNext.Assets;

/// <summary>
/// Read-only index over Earth &amp; Beyond's MIX1 archives. Load() preserves the
/// historical art-only scope; LoadRetailPackages() exposes current retail's complete
/// package namespace. The archive format carries explicit resource names, so no
/// filename hash database or extraction step is required.
/// </summary>
public sealed class EnbMixResourceCatalog
{
    private const uint Mix1Magic = 0x3158494D;
    private const int HeaderLength = 16;
    private const int DirectoryEntryLength = 12;
    private const int MaximumEntryCount = 1_000_000;

    private readonly Dictionary<string, EnbMixResourceLocation> _resources;
    private readonly Dictionary<string, IReadOnlyList<EnbMixResourceLocation>> _locationsByName;

    private EnbMixResourceCatalog(
        Dictionary<string, EnbMixResourceLocation> resources,
        Dictionary<string, IReadOnlyList<EnbMixResourceLocation>> locationsByName,
        int archiveCount,
        int discoveredArchiveCount)
    {
        _resources = resources;
        _locationsByName = locationsByName;
        ArchiveCount = archiveCount;
        DiscoveredArchiveCount = discoveredArchiveCount;
        Resources = resources.Values
            .OrderBy(static resource => resource.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AllResources = locationsByName.Values
            .SelectMany(static locations => locations)
            .OrderBy(static resource => resource.ArchiveName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static resource => resource.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Number of MIX1 archives in this catalog scope whose indexes were accepted.</summary>
    public int ArchiveCount { get; }

    /// <summary>Number of package files in this catalog scope discovered before parsing.</summary>
    public int DiscoveredArchiveCount { get; }

    public int ResourceCount => _resources.Count;

    /// <summary>
    /// Effective resource set: for a name carried by several packages, the package
    /// enumerated first wins (retail 004026D5 registers packages in _findfirst/_findnext
    /// order and 004E5440 inserts a name only when absent), matching TryLocateResource.
    /// </summary>
    public IReadOnlyList<EnbMixResourceLocation> Resources { get; }

    /// <summary>
    /// Every indexed package entry in this catalog scope, including names shadowed
    /// by another package. Corpus audits use this rather than the effective
    /// first-registered Resources view so a shadowed retail preload source cannot
    /// be hidden by the lookup winner.
    /// </summary>
    public IReadOnlyList<EnbMixResourceLocation> AllResources { get; }

    public static EnbMixResourceCatalog? Load(string mixFilesDirectory)
        => LoadCore(
            mixFilesDirectory,
            static path => Path.GetFileName(path).StartsWith("mixfile_art_", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Current retail constructs its package search manager by enumerating *.MIX in
    /// Data/client/mixfiles, not only mixfile_art_*.mix. This broader catalog is
    /// kept separate from Load() so historical art-only audits retain their
    /// established census while exact resource-manager lookups can use retail scope.
    /// </summary>
    public static EnbMixResourceCatalog? LoadRetailPackages(string mixFilesDirectory)
        => LoadCore(mixFilesDirectory, static _ => true);

    private static EnbMixResourceCatalog? LoadCore(
        string mixFilesDirectory,
        Func<string, bool> includeArchive)
    {
        if (string.IsNullOrWhiteSpace(mixFilesDirectory) || !EnbNext.Installation.EnbPathCase.DirectoryExists(mixFilesDirectory))
            return null;

        ArgumentNullException.ThrowIfNull(includeArchive);

        var resources = new Dictionary<string, EnbMixResourceLocation>(StringComparer.OrdinalIgnoreCase);
        var locations = new Dictionary<string, List<EnbMixResourceLocation>>(StringComparer.OrdinalIgnoreCase);
        string[] archivePaths;

        try
        {
            archivePaths = EnbNext.Installation.EnbPathCase
                .EnumerateFiles(mixFilesDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(static path => string.Equals(Path.GetExtension(path), ".mix", StringComparison.OrdinalIgnoreCase))
                .Where(includeArchive)
                // Retail 004026D5 registers packages in the directory's own
                // _findfirst/_findnext order without sorting; EnumerateFiles
                // returns the same file-system order (NTFS name order on Linux too).
                .ToArray();
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return null;
        }

        int acceptedArchives = 0;
        foreach (string archivePath in archivePaths)
        {
            try
            {
                IReadOnlyList<EnbMixResourceLocation> archiveResources = ReadArchiveIndex(archivePath);
                acceptedArchives++;
                foreach (EnbMixResourceLocation resource in archiveResources)
                {
                    if (!locations.TryGetValue(resource.Name, out List<EnbMixResourceLocation>? matches))
                    {
                        matches = [];
                        locations.Add(resource.Name, matches);
                    }
                    matches.Add(resource);

                    // Retail 004E5440: a name already registered by an earlier
                    // package keeps that package.
                    resources.TryAdd(resource.Name, resource);
                }
            }
            catch (Exception ex) when (ex is
                IOException or
                UnauthorizedAccessException or
                InvalidDataException)
            {
                // Retail 009D7A60/009D7E20: a package that cannot be opened or has a
                // bad header registers no names; enumeration continues.
            }
        }

        if (resources.Count == 0)
            return null;

        var frozenLocations = locations.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<EnbMixResourceLocation>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
        return new EnbMixResourceCatalog(
            resources,
            frozenLocations,
            acceptedArchives,
            archivePaths.Length);
    }

    /// <summary>
    /// Returns every package location carrying the requested logical resource name
    /// within this catalog scope, in package enumeration order; the first is the
    /// retail winner (004026D5/004E5440).
    /// </summary>
    public bool TryGetResourceLocations(
        string logicalResourceName,
        out IReadOnlyList<EnbMixResourceLocation> locations)
    {
        foreach (string candidate in GetResourceNameCandidates(logicalResourceName))
        {
            if (_locationsByName.TryGetValue(candidate, out IReadOnlyList<EnbMixResourceLocation>? resolved))
            {
                locations = resolved;
                return true;
            }
        }

        locations = Array.Empty<EnbMixResourceLocation>();
        return false;
    }

    public bool TryLocateResource(string logicalResourceName, out EnbMixResourceLocation location)
    {
        location = default!;

        foreach (string candidate in GetResourceNameCandidates(logicalResourceName))
        {
            if (_resources.TryGetValue(candidate, out EnbMixResourceLocation? resolved))
            {
                location = resolved;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetResourceNameCandidates(string logicalResourceName)
    {
        if (string.IsNullOrWhiteSpace(logicalResourceName))
            yield break;

        string trimmed = logicalResourceName.Trim();
        yield return trimmed;

        // Earth & Beyond W3D resources can retain their original .tga texture
        // reference while the installed MIX art archive stores the converted
        // runtime texture under the same logical name with a .dds extension.
        if (string.Equals(
                Path.GetExtension(trimmed),
                ".tga",
                StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.ChangeExtension(trimmed, ".dds");
        }
    }

    public bool TryReadResource(EnbMixResourceLocation location, out byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(location);
        bytes = [];

        try
        {
            using var stream = new FileStream(
                EnbNext.Installation.EnbPathCase.Resolve(location.ArchivePath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (location.DataOffset < 0 ||
                location.DataLength <= 0 ||
                location.DataOffset > stream.Length - location.DataLength ||
                location.DataLength > int.MaxValue)
            {
                return false;
            }

            stream.Position = location.DataOffset;
            bytes = new byte[(int)location.DataLength];
            stream.ReadExactly(bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bytes = [];
            return false;
        }
    }

    public bool TryReadResource(
        string logicalResourceName,
        out byte[] bytes,
        out EnbMixResourceLocation location)
    {
        bytes = [];
        location = default!;

        if (!TryLocateResource(logicalResourceName, out EnbMixResourceLocation resolved))
            return false;

        try
        {
            using var stream = new FileStream(
                EnbNext.Installation.EnbPathCase.Resolve(resolved.ArchivePath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (resolved.DataOffset < 0 ||
                resolved.DataLength <= 0 ||
                resolved.DataOffset > stream.Length - resolved.DataLength ||
                resolved.DataLength > int.MaxValue)
            {
                return false;
            }

            stream.Position = resolved.DataOffset;
            bytes = new byte[(int)resolved.DataLength];
            stream.ReadExactly(bytes);
            location = resolved;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bytes = [];
            location = default!;
            return false;
        }
    }

    private static IReadOnlyList<EnbMixResourceLocation> ReadArchiveIndex(string archivePath)
    {
        using var stream = new FileStream(
            EnbNext.Installation.EnbPathCase.Resolve(archivePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        if (stream.Length < HeaderLength || reader.ReadUInt32() != Mix1Magic)
            throw new InvalidDataException("The archive is not an Earth & Beyond MIX1 file.");

        uint directoryOffset = reader.ReadUInt32();
        uint filenameTableOffset = reader.ReadUInt32();
        _ = reader.ReadUInt32();

        if (directoryOffset > stream.Length - sizeof(uint) ||
            filenameTableOffset > stream.Length - sizeof(uint))
        {
            throw new InvalidDataException("The MIX1 index offsets are outside the archive.");
        }

        stream.Position = directoryOffset;
        uint entryCount = reader.ReadUInt32();

        if (entryCount > MaximumEntryCount ||
            (long)directoryOffset + sizeof(uint) + ((long)entryCount * DirectoryEntryLength) > stream.Length)
        {
            throw new InvalidDataException("The MIX1 directory count is invalid.");
        }

        var entries = new EnbMixDirectoryEntry[checked((int)entryCount)];
        for (int index = 0; index < entries.Length; index++)
        {
            _ = reader.ReadUInt32(); // Explicit names make the archive entry ID unnecessary here.
            uint dataOffset = reader.ReadUInt32();
            uint dataLength = reader.ReadUInt32();
            entries[index] = new EnbMixDirectoryEntry(dataOffset, dataLength);
        }

        stream.Position = filenameTableOffset;
        uint filenameCount = reader.ReadUInt32();
        if (filenameCount != entryCount)
            throw new InvalidDataException("The MIX1 filename table does not match its directory.");

        var results = new List<EnbMixResourceLocation>(entries.Length);
        for (int index = 0; index < entries.Length; index++)
        {
            int storedLength = reader.ReadByte();
            if (storedLength == 0)
                throw new InvalidDataException("A MIX1 filename has zero length.");

            byte[] storedName = reader.ReadBytes(storedLength);
            if (storedName.Length != storedLength)
                throw new EndOfStreamException("The MIX1 filename table ended unexpectedly.");

            int nullIndex = Array.IndexOf(storedName, (byte)0);
            int nameLength = nullIndex >= 0 ? nullIndex : storedName.Length;
            string name = Encoding.ASCII.GetString(storedName, 0, nameLength);
            EnbMixDirectoryEntry entry = entries[index];

            if (name.Length == 0 ||
                entry.DataLength == 0 ||
                entry.DataOffset > stream.Length - entry.DataLength)
            {
                continue;
            }

            results.Add(new EnbMixResourceLocation(
                name,
                archivePath,
                entry.DataOffset,
                entry.DataLength));
        }

        return results;
    }

    private readonly record struct EnbMixDirectoryEntry(long DataOffset, long DataLength);
}

public sealed record EnbMixResourceLocation(
    string Name,
    string ArchivePath,
    long DataOffset,
    long DataLength)
{
    public string ArchiveName => Path.GetFileName(ArchivePath);
}
