namespace EnbNext.Net7.Gameplay;

// Current client HandleEmoteSlashCommand 00747C70. These IDs are not menu indices.
public static class Net7ShipEmote
{
    public static uint? FromCommand(string command) => command switch
    {
        "/wave" => 0, "/loop" => 1, "/dive" => 2,
        "/roll" or "/rightroll" => 3, "/leftroll" => 4,
        "/spin" or "/rightspin" => 5, "/leftspin" => 6, _ => null,
    };
}

public sealed record Net7ShipEmoteEvent(long Sequence, uint GameId, ulong CreationOrdinal, uint Emote);
