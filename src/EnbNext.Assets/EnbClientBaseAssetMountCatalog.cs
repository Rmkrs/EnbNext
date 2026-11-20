using System.Globalization;

namespace EnbNext.Assets;

/// <summary>
/// Read-only ship/subpart mount metadata from retail cbasset.ini.
///
/// Current retail loads BASE-&lt;id&gt;/Mount%02d sections sequentially starting at
/// Mount00 and stops at the first missing section. BoneName is the authored
/// lookup key and BoneIndex selects the captured hierarchy record. Physical
/// attachment resolves BoneName independently through RenderObject +B0.
/// </summary>
public sealed class EnbClientBaseAssetMountCatalog
{
    private readonly Dictionary<int, EnbClientBaseAssetMountSet> _byBaseAsset;
    private readonly EnbRetailIniDocument _document;

    private EnbClientBaseAssetMountCatalog(Dictionary<int, EnbClientBaseAssetMountSet> byBaseAsset, EnbRetailIniDocument document)
    {
        _byBaseAsset = byBaseAsset;
        _document = document;
        MountCount = byBaseAsset.Values.Sum(static set => set.Mounts.Count);
    }

    public int BaseAssetCount => _byBaseAsset.Count;

    public int MountCount { get; }

    public static EnbClientBaseAssetMountCatalog? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !EnbNext.Installation.EnbPathCase.FileExists(path))
            return null;

        try
        {
            EnbClientBaseAssetMountCatalog catalog = Parse(EnbRetailIniDocument.Load(path));
            return catalog.MountCount == 0 ? null : catalog;
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return null;
        }
    }

    public static EnbClientBaseAssetMountCatalog ParseText(string text)
        => Parse(EnbRetailIniDocument.Parse(text));

    public static EnbClientBaseAssetMountCatalog Parse(EnbRetailIniDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var baseAssetIds = new HashSet<int>();
        foreach (string sectionName in document.SectionNames)
        {
            if (TryParseMountSectionName(sectionName, out int baseAssetId, out int ordinal) && ordinal == 0)
                baseAssetIds.Add(baseAssetId);
        }

        var byBaseAsset = new Dictionary<int, EnbClientBaseAssetMountSet>();
        foreach (int baseAssetId in baseAssetIds.Order())
        {
            EnbClientBaseAssetMountSet? set = ParseBaseAssetMounts(document, baseAssetId);
            if (set is not null && set.Mounts.Count != 0)
                byBaseAsset[baseAssetId] = set;
        }

        return new EnbClientBaseAssetMountCatalog(byBaseAsset, document);
    }

    // 72D5F0 initializes BoneMap+20 from these values, even without mounts.
    // 72CC10 uses this authored radius; neither mesh bounds nor factory scale
    // replaces it. The ordinary object loader (6F39F0) is a separate path.
    public float ResolveControllerRadius(int baseAssetId)
    {
        if (!_document.TryGetSection($"BASE-{baseAssetId}", out var values)) return 1f;
        float radius = EnbRetailIniDocument.TryReadFloat(values, "Radius", out float r) ? r : 1f;
        float adjustment = EnbRetailIniDocument.TryReadFloat(values, "RadiusAdjust", out float a) ? a : 0f;
        return radius + adjustment;
    }

    public bool TryGetMountSet(int baseAssetId, out EnbClientBaseAssetMountSet mountSet)
        => _byBaseAsset.TryGetValue(baseAssetId, out mountSet!);

    public bool TryResolveMount(int baseAssetId, string boneName, out EnbClientBaseAssetMount mount)
    {
        ArgumentNullException.ThrowIfNull(boneName);

        if (_byBaseAsset.TryGetValue(baseAssetId, out EnbClientBaseAssetMountSet? set))
            return set.TryResolve(boneName, out mount);

        mount = default!;
        return false;
    }

    private static EnbClientBaseAssetMountSet? ParseBaseAssetMounts(
        EnbRetailIniDocument document,
        int baseAssetId)
    {
        var mounts = new List<EnbClientBaseAssetMount>();
        var byBoneName = new Dictionary<string, EnbClientBaseAssetMount>(StringComparer.Ordinal);

        for (int ordinal = 0; ; ordinal++)
        {
            string sectionName = $"BASE-{baseAssetId}/Mount{ordinal:00}";
            if (!document.TryGetSection(sectionName, out IReadOnlyDictionary<string, string> values))
                break;

            // Current client reads BoneIndex with a -1 default and stops this
            // BaseAsset's mount construction when the resulting value is < 1.
            if (!values.TryGetValue("BoneName", out string? rawBoneName) ||
                string.IsNullOrEmpty(rawBoneName) ||
                !EnbRetailIniDocument.TryReadInt(values, "BoneIndex", out int boneIndex) ||
                boneIndex < 1)
            {
                break;
            }

            string boneName = rawBoneName.Trim();
            if (boneName.Length == 0)
                break;

            var mount = new EnbClientBaseAssetMount(baseAssetId, ordinal, boneName, boneIndex);
            mounts.Add(mount);

            // The current retail lookup is keyed by exact SharedString BoneName.
            // If installation data repeats a name, the later sequential record
            // becomes the lookup result while both authored records remain visible.
            byBoneName[boneName] = mount;
        }

        return mounts.Count == 0
            ? null
            : new EnbClientBaseAssetMountSet(baseAssetId, mounts, byBoneName);
    }

    private static bool TryParseMountSectionName(string sectionName, out int baseAssetId, out int ordinal)
    {
        baseAssetId = 0;
        ordinal = 0;

        const string Prefix = "BASE-";
        const string MountMarker = "/Mount";

        if (!sectionName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        int slash = sectionName.IndexOf('/', Prefix.Length);
        if (slash <= Prefix.Length)
            return false;

        if (!sectionName.AsSpan(slash).StartsWith(MountMarker, StringComparison.OrdinalIgnoreCase))
            return false;

        ReadOnlySpan<char> baseAssetText = sectionName.AsSpan(Prefix.Length, slash - Prefix.Length);
        ReadOnlySpan<char> ordinalText = sectionName.AsSpan(slash + MountMarker.Length);

        return int.TryParse(baseAssetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out baseAssetId)
            && int.TryParse(ordinalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal)
            && ordinal >= 0;
    }
}

public sealed record EnbClientBaseAssetMount(
    int BaseAssetId,
    int Ordinal,
    string BoneName,
    int BoneIndex);

public sealed class EnbClientBaseAssetMountSet
{
    private readonly Dictionary<string, EnbClientBaseAssetMount> _byBoneName;

    internal EnbClientBaseAssetMountSet(
        int baseAssetId,
        IReadOnlyList<EnbClientBaseAssetMount> mounts,
        Dictionary<string, EnbClientBaseAssetMount> byBoneName)
    {
        BaseAssetId = baseAssetId;
        Mounts = mounts;
        _byBoneName = byBoneName;
    }

    public int BaseAssetId { get; }

    public IReadOnlyList<EnbClientBaseAssetMount> Mounts { get; }

    public bool TryResolve(string boneName, out EnbClientBaseAssetMount mount)
    {
        ArgumentNullException.ThrowIfNull(boneName);
        return _byBoneName.TryGetValue(boneName, out mount!);
    }
}
