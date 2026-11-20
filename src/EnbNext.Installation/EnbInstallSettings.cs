using System.Text.Json;

namespace EnbNext.Installation;

public sealed record EnbInstallSettings(string? EarthAndBeyondFolder = null, string? Net7Folder = null)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EnbNext", "install.json");

    public static EnbInstallSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<EnbInstallSettings>(File.ReadAllText(path)) ?? new(); }
        catch (JsonException ex) { throw new InvalidDataException($"Installation settings are invalid. Please correct or remove {path}.", ex); }
    }

    public void Save(string? path = null)
    {
        path = Path.GetFullPath(path ?? DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
