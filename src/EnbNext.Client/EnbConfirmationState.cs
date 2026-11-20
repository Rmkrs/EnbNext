namespace EnbNext.Client;

/// <summary>Content of the shared authored OK/Yes-No dialog, independent of its host.</summary>
public sealed class EnbConfirmationState
{
    public string? Text { get; private set; }
    public bool Notice { get; private set; }
    public string Definition { get; private set; } = "OK";
    public float? WrapWidth { get; private set; }
    public ulong Lifetime { get; private set; }
    public void Show(string text, bool notice, string? definition = null, float? wrapWidth = null)
    { Lifetime++; Text = text; Notice = notice; Definition = definition ?? (notice ? "OK" : "YES_NO"); WrapWidth = wrapWidth; }
    public void Clear() { Text = null; Notice = false; Definition = "OK"; WrapWidth = null; }
}
