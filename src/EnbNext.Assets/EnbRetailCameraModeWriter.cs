using System.Globalization;

namespace EnbNext.Assets;

/// <summary>Edits one retail camera slot without discarding other sections or keys.</summary>
public static class EnbRetailCameraModeWriter
{
    public static string Update(string text, EnbRetailCameraMode mode)
    {
        if (mode.Index <= 0 || !new[] { mode.HeadingRadians, mode.Distance, mode.TiltRadians,
                mode.HorizontalFieldOfViewRadians, mode.ElevationOffsetRadians }.All(float.IsFinite))
            throw new ArgumentException("Invalid third-person camera slot", nameof(mode));
        var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["Heading"] = mode.HeadingRadians * (180 / MathF.PI), ["Distance"] = mode.Distance,
            ["Tilt"] = mode.TiltRadians * (180 / MathF.PI),
            ["FOV"] = mode.HorizontalFieldOfViewRadians * (180 / MathF.PI),
            ["ElevationOffset"] = mode.ElevationOffsetRadians * (180 / MathF.PI),
        };
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        string section = $"[Mode{mode.Index:00}]";
        int start = lines.FindIndex(l => l.Trim().Equals(section, StringComparison.OrdinalIgnoreCase));
        if (start < 0) throw new InvalidDataException("Camera slot is absent from the source INI");
        int end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
        for (int i = start + 1; i < end; i++)
        {
            int equals = lines[i].IndexOf('=');
            if (equals < 0) continue;
            string key = lines[i][..equals].Trim();
            if (values.TryGetValue(key, out float value))
                lines[i] = key + "=" + value.ToString("R", CultureInfo.InvariantCulture);
        }
        foreach (var (key, value) in values)
            if (!lines.Skip(start + 1).Take(end - start - 1).Any(l =>
                l.Split('=', 2)[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)))
                lines.Insert(end++, key + "=" + value.ToString("R", CultureInfo.InvariantCulture));
        return string.Join(newline, lines);
    }
}
