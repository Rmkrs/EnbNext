using System.Collections.Immutable;

namespace EnbNext.Presentation;

public sealed record EnbSceneStationRoom(uint RoomType, uint Flags, ImmutableArray<uint> Parameters);
public sealed record EnbSceneStationDefinition(uint DefinitionValue, ImmutableArray<EnbSceneStationRoom> Rooms)
{
    public ImmutableArray<EnbSceneStationNpc> Npcs { get; init; } = [];
    public ImmutableArray<EnbSceneStationFacility> Facilities { get; init; } = [];
    // 6987A0 keeps the first definition for a slot and maps unknown variants
    // to ordinary NPCs. 634140 constructs, and 63AAF0 updates, slots 0..13.
    // Keep the wire definitions intact; expose the room consumer's view here.
    public IEnumerable<EnbSceneStationNpc> RoomNpcs(uint room)
        =>Npcs.Where(n=>n.RoomType==room && n.Slot<14).DistinctBy(n=>n.Slot)
            .OrderBy(n=>n.Slot).Select(n=>n.Variant<=3?n:n with {Variant=0});
}
public sealed record EnbSceneStationNpc(uint RoomType,uint Slot,uint Id,uint Role,uint Variant,string Name,EnbSceneAvatarSnapshot Avatar)
{
    // Some definitions leave the optional display string empty. AvatarData carries
    // the identity name independently (also used by N7CM's station observer).
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? string.Join(" ",new[]{Avatar.FirstName,Avatar.LastName}.Where(s=>!string.IsNullOrWhiteSpace(s))) : Name;
}
public sealed record EnbSceneStationFacility(uint RoomType,uint Slot,uint Type,uint FourthField);
