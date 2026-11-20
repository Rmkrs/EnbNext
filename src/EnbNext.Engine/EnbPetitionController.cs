namespace EnbNext.Engine;

internal enum EnbPetitionSendResult { Ignored, Sent, Failed }

/// <summary>005D4DE0/005D7420/005D6550 form transaction ownership; transport stays at its existing boundary.</summary>
internal sealed class EnbPetitionController
{
    public uint Category { get; private set; }
    public bool Sending { get; private set; }
    public string? Error { get; private set; }
    private int generation;
    public event Action? SendStarted;
    public event Action<EnbPetitionSendResult>? Completed;
    public void Open(uint category) => Category = category;
    public void Back() => Category = 0;
    public void Retire() { Category = 0; Sending = false; generation++; }
    public static bool TextValid(string text, int minimum) => text.Count(c => c > ' ' && c < 128) >= minimum;
    public static bool DataValid(string subject, string body) => TextValid(subject,4) && TextValid(body,4);

    public async Task<EnbPetitionSendResult> SendAsync(uint owner, ulong handoff, string subject, string body, string players,
        Func<uint?> session, Func<uint,ulong,uint,string,string,string,Task<bool>>? send)
    {
        if (Sending || Category == 0 || owner != session()) return EnbPetitionSendResult.Ignored;
        int requestGeneration = generation;
        Sending = true; SendStarted?.Invoke();
        try
        {
            bool sent = send is not null && await send(owner,handoff,Category,subject,body,players);
            if (requestGeneration != generation || owner != session()) return EnbPetitionSendResult.Ignored;
            Sending = false;
            if (sent) { Completed?.Invoke(EnbPetitionSendResult.Sent); return EnbPetitionSendResult.Sent; }
            Error = "The petition could not be sent. Your text has been kept.";
            Completed?.Invoke(EnbPetitionSendResult.Failed);
            return EnbPetitionSendResult.Failed;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or ArgumentException)
        {
            if (requestGeneration != generation || owner != session()) return EnbPetitionSendResult.Ignored;
            Sending = false; Error = "The petition could not be sent: " + ex.Message;
            Completed?.Invoke(EnbPetitionSendResult.Failed);
            return EnbPetitionSendResult.Failed;
        }
    }
}
