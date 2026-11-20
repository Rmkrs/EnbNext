using System.Text.Json;
using EnbNext.Engine;
using EnbNext.Presentation;
using SDL;
namespace EnbNext.App;
internal sealed partial class SdlApplication
{
    // Shared map markers (all characters) and the active waypoint, stored next to the startup preferences.
    private sealed record WaypointFile(List<EnbMapNote> Notes, EnbWaypoint? Active);
    private static readonly JsonSerializerOptions waypointJson = new() { IncludeFields = true, WriteIndented = true };
    private readonly EnbWaypointBook waypoints = new();
    private EnbWaypointArrow? waypointArrow;
    private bool waypointsLoaded;
    private EnbQualityOfLifeOptions? previewQualityOfLife;
    private EnbQualityOfLifeOptions EffectiveQualityOfLife => previewQualityOfLife ?? _preferences.QualityOfLife ?? new();
    private string WaypointPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_preferencesPath))!, "map-markers.json");

    private void LoadWaypoints()
    {
        if (waypointsLoaded) return; waypointsLoaded = true;
        try
        {
            if (File.Exists(WaypointPath) && JsonSerializer.Deserialize<WaypointFile>(File.ReadAllText(WaypointPath), waypointJson) is { } file)
                waypoints.Load(file.Notes ?? [], file.Active);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { AppendDiagnostic("Map markers could not be read: " + ex.Message); }
        waypoints.SaveNotes = _ => SaveWaypoints();
        waypoints.SaveActive = _ => SaveWaypoints();
    }

    private void SaveWaypoints()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WaypointPath)!);
            string temporary = WaypointPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(new WaypointFile([.. waypoints.Notes], waypoints.Active), waypointJson)); File.Move(temporary, WaypointPath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppendDiagnostic("Map markers could not be saved: " + ex.Message); }
    }

    private void TickWaypointArrow()
    {
        LoadWaypoints();
        var options = EffectiveQualityOfLife;
        var client = _client;
        bool settings = worldScreen?.Owner.Options.NavigationOptionsOpen == true;
        // Navigation Options shows a sample target when none is active, so size and lines preview live.
        var target = waypoints.Active ?? (settings ? PreviewWaypoint(worldScreen!.Owner.PresentedLocalShip) : null);
        bool show = worldScreen is not null && client?.RetailAssets is not null && options.WaypointArrow && target is not null;
        if (!show)
        {
            if (waypointArrow is not null) { if (worldScreen is { } world) world.Owner.Hud.WaypointArrow = null; waypointArrow.Dispose(); waypointArrow = null; }
            return;
        }
        if (waypointArrow is null)
        {
            waypointArrow = new(client!.RetailAssets!)
            { Center = _preferences.WaypointArrowX is double x && _preferences.WaypointArrowY is double y ? new(x, y) : EnbWaypointArrow.DefaultCenter };
            waypointArrow.Cleared += () => waypoints.SetActive(null);
            waypointArrow.Moved += center => { _preferences = _preferences with { WaypointArrowX = center.X, WaypointArrowY = center.Y }; SavePreferences(); };
        }
        // The rendered frame's ship pose: packet-time snapshots lag while the ship turns.
        waypointArrow.Configure(options.ArrowSizePercent / 100.0, options.WaypointArrowDistance, options.WaypointArrowHeight);
        waypointArrow.Update(target, worldScreen!.Owner.PresentedLocalShip);
        worldScreen!.Owner.Hud.WaypointArrow = waypointArrow;
        worldScreen.Owner.Hud.WaypointArrowOnTop = settings;
    }

    /// <summary>A sample target ahead, to the right and above the ship (Navigation Options preview only).</summary>
    private static EnbWaypoint? PreviewWaypoint(EnbPresentedShip? ship)
    {
        if (ship is not { } s) return null;
        var forward = new System.Numerics.Vector2(s.Forward.X, s.Forward.Y);
        forward = forward.LengthSquared() > 1e-8f ? System.Numerics.Vector2.Normalize(forward) : System.Numerics.Vector2.UnitX;
        var right = new System.Numerics.Vector2(forward.Y, -forward.X);
        var offset = forward * 10000 + right * 5800;
        return new(s.Sector, "", s.Position + new System.Numerics.Vector3(offset.X, offset.Y, 4000), "Preview");
    }

    private bool HandleWaypointArrow(SDL_Event e)
    {
        if (waypointArrow is not { } arrow || worldScreen is not { } world || world.Owner.Ui.Layout.Editing) return false;
        var type = (SDL_EventType)e.type;
        if (type == SDL_EventType.SDL_EVENT_MOUSE_MOTION) { arrow.Pointer(new(e.motion.x, e.motion.y)); return arrow.Dragging; }
        if (type == SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP) return arrow.Release();
        if (type != SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN || PointerUnderGameUi(world, e)) return false;
        return e.button.button is 1 or 3 && arrow.Press(new(e.button.x, e.button.y), e.button.button == 3);
    }

    /// <summary>The map marker editor is modal: while it is open it owns the pointer.</summary>
    private bool HandleMapNoteEditor(SDL_Event e)
    {
        if (worldScreen is not { } world || world.Owner.NavigationMap.NoteEditor is not { IsOpen: true } editor) return false;
        var type = (SDL_EventType)e.type;
        if (type == SDL_EventType.SDL_EVENT_MOUSE_MOTION) { editor.Pointer(new(e.motion.x, e.motion.y)); return true; }
        if (type == SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN) { if (e.button.button == 1) editor.Press(new(e.button.x, e.button.y)); return true; }
        return type is SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP or SDL_EventType.SDL_EVENT_MOUSE_WHEEL;
    }

    /// <summary>Local /way: only while the waypoint arrow option is on; otherwise the server gets the line as before.</summary>
    private bool TryWaypointCommand(string text)
    {
        if (!(_preferences.QualityOfLife ?? new()).WaypointArrow || !EnbWaypointCommand.Matches(text) || _client is not { } client) return false;
        LoadWaypoints();
        string message = EnbWaypointCommand.Execute(text, waypoints, client.LatestPresentationSnapshot?.Environment?.SectorNum, client.GalaxyMap);
        if (client.SelectedFixedSessionId is uint character) client.ReportLocalChatMessage(character, 19, message);
        return true;
    }
}
