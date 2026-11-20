using System.Globalization;

namespace EnbNext.Assets;

/// <summary>
/// Read-only local client geometry metadata from cbasset.ini. This catalog is
/// presentation-only and never participates in Net7 protocol decisions.
/// </summary>
public sealed class EnbClientBaseAssetCatalog
{
    private readonly Dictionary<int, EnbClientBaseAssetGeometry> _entries;

    private EnbClientBaseAssetCatalog(Dictionary<int, EnbClientBaseAssetGeometry> entries)
    {
        _entries = entries;
    }

    public int Count => _entries.Count;

    public static EnbClientBaseAssetCatalog? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !EnbNext.Installation.EnbPathCase.FileExists(path))
            return null;

        try
        {
            return Parse(File.ReadAllLines(EnbNext.Installation.EnbPathCase.Resolve(path)));
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return null;
        }
    }

    public bool TryResolve(int selector, out EnbClientBaseAssetGeometry geometry)
    {
        if (_entries.TryGetValue(selector, out EnbClientBaseAssetGeometry? resolved))
        {
            geometry = resolved;
            return true;
        }

        geometry = default!;
        return false;
    }

    private static EnbClientBaseAssetCatalog? Parse(IEnumerable<string> lines)
    {
        var entries = new Dictionary<int, EnbClientBaseAssetGeometry>();
        int? currentSelector = null;
        float? radius = 0f;
        float radiusAdjust = 0f;

        void Commit()
        {
            if (currentSelector is not int selector || radius is not float parsedRadius ||
                !float.IsFinite(parsedRadius))
            {
                return;
            }

            entries[selector] = new EnbClientBaseAssetGeometry(selector, parsedRadius, radiusAdjust);
        }

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Commit();
                currentSelector = null;
                radius = 0f;
                radiusAdjust = 0f;

                string section = line[1..^1].Trim();
                if (section.StartsWith("BASE-", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(section.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int selector))
                {
                    currentSelector = selector;
                }
                continue;
            }

            if (currentSelector is null)
                continue;

            int equals = line.IndexOf('=');
            if (equals <= 0)
                continue;

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ||
                !float.IsFinite(parsed))
            {
                continue;
            }

            if (key.Equals("Radius", StringComparison.OrdinalIgnoreCase))
                radius = parsed;
            else if (key.Equals("RadiusAdjust", StringComparison.OrdinalIgnoreCase))
                radiusAdjust = parsed;
        }

        Commit();
        return entries.Count == 0 ? null : new EnbClientBaseAssetCatalog(entries);
    }
}

public sealed record EnbClientBaseAssetGeometry(int Selector, float Radius, float RadiusAdjust)
{
    public bool TryScale(float objectScale, out float worldRadius)
    {
        worldRadius = 0f;
        if (!float.IsFinite(objectScale) || objectScale <= 0f)
            return false;

        float adjusted = Radius + RadiusAdjust;
        if (!float.IsFinite(adjusted))
            return false;

        worldRadius = adjusted * objectScale;
        //006F3AD3..006F3B13 clamps the final scaled radius, not either
        //authored input. A negative Radius may be offset by RadiusAdjust.
        if (worldRadius < 0f) worldRadius = 0f;
        return float.IsFinite(worldRadius) && worldRadius >= 0f;
    }
}
