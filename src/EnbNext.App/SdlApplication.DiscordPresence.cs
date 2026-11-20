namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private DiscordPresenceClient? _discordPresence;
    private readonly DiscordPresenceSession _presenceSession=new();
    private long _presenceNextSample;
    private void TickDiscordPresence()
    {
        long now=Environment.TickCount64;
        if(now<_presenceNextSample)return;
        _presenceNextSample=now+1000;
        UpdateDiscordPresence();
    }
    private void UpdateDiscordPresence()
    {
        var value=_presenceSession.Update(_client?.State,worldScreen is not null,_selectedCharacter,
            _client?.LatestPresentationSnapshot,_preferences.DiscordPresence,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        value=MultiboxPresence(value);
        if(value is not null)(_discordPresence??=new()).SetActivity(value);
        else _discordPresence?.SetActivity(null);
    }
    private void ClearDiscordPresence()
    {
        _presenceSession.Reset();_discordPresence?.SetActivity(null);
    }
}
