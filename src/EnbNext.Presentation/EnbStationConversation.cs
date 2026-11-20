using EnbNext.Net7.Presentation;
namespace EnbNext.Presentation;

// A null starbase identifies a server-initiated conversation in space.
public sealed record EnbStationInteractionRequest(uint Character, uint? StarbaseId, ulong HandoffVersion, uint Room, uint Slot, uint NpcId);
public sealed record EnbStationConversation(ulong Revision, Net7TalkTreeUpdate? Tree, Net7TalkTreeActionUpdate? Action, bool TradeAvailable)
{
    public ulong TradeOpenRevision { get; init; }
    public ulong TextRevision { get; init; }
    public ulong EndRevision { get; init; }
    public ulong DoneRevision { get; init; }
    public bool DoneCaption { get; init; }
    public bool SkillAvailable { get; init; }
    public EnbSceneAvatarSnapshot? Portrait { get; init; }
}
public sealed record EnbStationReplyRequest(EnbStationInteractionRequest Owner, ulong Revision, uint Selection, bool Close = false)
{
    /// <summary>Space talk Exit (0064D400 events 6/0B): the E6 selection is queued like a reply and the dialogue stays
    /// open; the server's negative 0056 then deactivates it, and 0064C8D0 sends the closing E6.</summary>
    public bool Exit { get; init; }
}
public sealed record EnbStationPurchaseRequest(EnbStationInteractionRequest Owner, EnbInventoryItemRequest Item, bool Stack, bool Confirmed, ulong Price, int Count);
