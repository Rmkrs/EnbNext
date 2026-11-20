using System.Collections.Concurrent;

namespace EnbNext.Assets;

/// <summary>An optional, session-stable source pair ahead of installed texture files.</summary>
public sealed class EnbTextureOverridePack
{
    private sealed record Entry(byte[] Tga, byte[] Dds, string TgaPath, string DdsPath,
        EnbCurrentRetailTextureThumbnail Thumbnail);
    private readonly KeyValuePair<string, Lazy<Entry?>>[] entries = [];
    private readonly ConcurrentQueue<string> diagnostics = new();
    public string DirectoryPath { get; }
    public int LoadedTextureCount => entries.Count(e => e.Value.IsValueCreated && e.Value.Value is not null);
    public IReadOnlyList<string> Diagnostics => diagnostics.ToArray();
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EnbNext", "TextureOverrides");
    public static string? ConfiguredDirectory
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable("ENBNEXT_TEXTURE_OVERRIDES");
            return string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ? null
                : string.IsNullOrWhiteSpace(value) ? DefaultDirectory : value;
        }
    }

    public EnbTextureOverridePack(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        if (!EnbNext.Installation.EnbPathCase.DirectoryExists(DirectoryPath)) return;
        try
        {
            var index = new List<KeyValuePair<string, Lazy<Entry?>>>();
            var files = EnbNext.Installation.EnbPathCase.EnumerateFiles(DirectoryPath).Where(p =>
                Path.GetExtension(p).Equals(".tga", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(p).Equals(".dds", StringComparison.OrdinalIgnoreCase));
            foreach (var group in files.GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase))
            {
                var tga = group.Where(p => Path.GetExtension(p).Equals(".tga", StringComparison.OrdinalIgnoreCase)).ToArray();
                var dds = group.Where(p => Path.GetExtension(p).Equals(".dds", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (tga.Length != 1 || dds.Length != 1)
                { diagnostics.Enqueue($"Ignored texture override '{group.Key}': exactly one TGA/DDS pair is required."); continue; }
                string key = group.Key!;
                index.Add(new(key, new Lazy<Entry?>(() => Load(key, tga[0], dds[0]), true)));
            }
            entries = index.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { diagnostics.Enqueue($"Texture override folder unavailable: {ex.Message}"); }
    }

    private Entry? Find(string name)
    {
        int terminator = name.IndexOf('\0');
        if (terminator >= 0) name = name[..terminator];
        // Logical texture names are flat; never interpret a name as a filesystem path.
        if (name.IndexOfAny(['/', '\\', ':']) >= 0 ||
            !(name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))) return null;
        string key=name[..^4];
        int low=0, high=entries.Length-1;
        while(low<=high)
        {
            int mid=low+(high-low)/2;
            int order=StringComparer.OrdinalIgnoreCase.Compare(entries[mid].Key,key);
            if(order==0)return entries[mid].Value.Value;
            if(order<0)low=mid+1;else high=mid-1;
        }
        return null;
    }
    public EnbCurrentRetailTextureThumbnail? ResolveThumbnail(string name) => Find(name)?.Thumbnail;
    public bool TryRead(string name, bool compressed, out byte[] bytes, out EnbMixResourceLocation location)
    {
        var entry = Find(name); bytes = []; location = null!;
        if (entry is null) return false;
        bytes = compressed ? entry.Dds : entry.Tga;
        string path = compressed ? entry.DdsPath : entry.TgaPath;
        location = new(Path.GetFileName(path), path, 0, bytes.Length);
        return true;
    }
    private Entry? Load(string name, string tgaPath, string ddsPath)
    {
        try
        {
            // Bound optional input before allocating or invoking the existing decoders.
            if (new FileInfo(EnbNext.Installation.EnbPathCase.Resolve(tgaPath)).Length > 128 * 1024 * 1024 || new FileInfo(EnbNext.Installation.EnbPathCase.Resolve(ddsPath)).Length > 128 * 1024 * 1024)
                throw new InvalidDataException("File exceeds the supported texture size.");
            byte[] tga = File.ReadAllBytes(EnbNext.Installation.EnbPathCase.Resolve(tgaPath)), dds = File.ReadAllBytes(EnbNext.Installation.EnbPathCase.Resolve(ddsPath));
            if (!EnbRetailTextureDecoder.TryDecode(tga, name + ".tga", out var a) ||
                !EnbRetailTextureDecoder.TryDecode(dds, name + ".dds", out var b) ||
                a.Width != b.Width || a.Height != b.Height ||
                !EnbCurrentRetailDdsSourceState.TryRead(dds, 0, out var source) || source!.Format is not (20 or 22 or 24))
                throw new InvalidDataException("Pair must have matching dimensions and legacy DXT1, DXT3 or DXT5 DDS storage.");
            var thumbnail = EnbCurrentRetailTextureThumbnailGenerator.Create(name + ".tga",
                n => new EnbCurrentRetailThumbnailSource(n.EndsWith("dds", StringComparison.OrdinalIgnoreCase) ? dds : tga, 0));
            if (thumbnail is null || thumbnail.Width != a.Width || thumbnail.Height != a.Height)
                throw new InvalidDataException("Replacement creation metadata is invalid.");
            return new(tga, dds, tgaPath, ddsPath, thumbnail);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { diagnostics.Enqueue($"Ignored texture override '{name}': {ex.Message}"); return null; }
    }
}
