using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Engine;

/// <summary>
/// Shared map markers (all characters) and the single waypoint the arrow points at. The map,
/// the arrow and /way all act on this owner; the host persists notes and the active waypoint.
/// </summary>
public sealed class EnbWaypointBook
{
    public ImmutableList<EnbMapNote> Notes { get; private set; } = [];
    public EnbWaypoint? Active { get; private set; }
    public event Action? Changed;
    public Action<IReadOnlyList<EnbMapNote>>? SaveNotes { get; set; }
    public Action<EnbWaypoint?>? SaveActive { get; set; }

    public void Load(IEnumerable<EnbMapNote> notes, EnbWaypoint? active)
    {
        Notes = notes.ToImmutableList(); Active = active; Changed?.Invoke();
    }

    public IEnumerable<EnbMapNote> InSector(uint sector) => Notes.Where(n => n.Sector == sector);

    public EnbMapNote Add(uint sector, string sectorName, Vector3 position, string name, string note = "")
    {
        var added = new EnbMapNote(Guid.NewGuid(), sector, sectorName, position, Clean(name, "Marker"), note.Trim());
        Notes = Notes.Add(added); Persist(); return added;
    }

    public void Update(EnbMapNote note)
    {
        int index = Notes.FindIndex(n => n.Id == note.Id); if (index < 0) return;
        note = note with { Name = Clean(note.Name, "Marker"), Note = note.Note.Trim() };
        Notes = Notes.SetItem(index, note);
        if (Active?.NoteId == note.Id) SetActive(Target(note));
        Persist();
    }

    public void Remove(Guid id)
    {
        int index = Notes.FindIndex(n => n.Id == id); if (index < 0) return;
        Notes = Notes.RemoveAt(index);
        if (Active?.NoteId == id) SetActive(null);
        Persist();
    }

    public void SetActive(EnbWaypoint? waypoint)
    {
        if (Active == waypoint) return;
        Active = waypoint; SaveActive?.Invoke(waypoint); Changed?.Invoke();
    }

    public void Activate(EnbMapNote note) => SetActive(Target(note));
    public static EnbWaypoint Target(EnbMapNote note) => new(note.Sector, note.SectorName, note.Position, note.Name, note.Id);

    private void Persist() { SaveNotes?.Invoke(Notes); Changed?.Invoke(); }
    private static string Clean(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
