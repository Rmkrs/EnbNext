using System.Text.Json;

namespace EnbNext.Assets;

public sealed class EnbCharacterInterfaceSettings
{
    public bool RoutePlanner { get; set; }
    public bool AutoPilot { get; set; }
    public Guid LayoutProfile { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool AutoPilotAvailable => RoutePlanner && AutoPilot;
    private string? path;
    /// <summary>Where a character's interface settings are kept.</summary>
    public static string PathFor(ulong account, ulong avatar, string? directory = null)
        => Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EnbNext", "characters"), $"{account:X16}-{avatar:X16}-interface.json");
    public static EnbCharacterInterfaceSettings Load(ulong account, ulong avatar, string? directory = null)
    {
        string path = PathFor(account, avatar, directory);
        var settings = EnbNext.Installation.EnbPathCase.FileExists(path) ? JsonSerializer.Deserialize<EnbCharacterInterfaceSettings>(File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(path))) ?? new() : new();
        settings.path = path;
        return settings;
    }
    public void Save(bool routePlanner, bool autoPilot, Guid profile)
    {
        if (path is null) throw new InvalidOperationException("No character interface settings are open.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new EnbCharacterInterfaceSettings { RoutePlanner = routePlanner, AutoPilot = autoPilot, LayoutProfile = profile });
                stream.Flush(true);
            }
            File.Move(temporary, EnbNext.Installation.EnbPathCase.Resolve(path), true);
            RoutePlanner = routePlanner; AutoPilot = autoPilot; LayoutProfile = profile;
        }
        finally { if (EnbNext.Installation.EnbPathCase.FileExists(temporary)) File.Delete(temporary); }
    }
}
