namespace EnbNext.Rendering;

public enum EnbRetailSpeechDecision { Start, Interrupt, Queue, Reject }

/// <summary>Current retail 0043F2D0 admission, before the sound factory is called.</summary>
public static class EnbRetailSpeechAdmission
{
    public static EnbRetailSpeechDecision Decide(string requestedName, float priority,
        bool currentActive, string? currentFile, float currentPriority,
        uint durationMilliseconds, uint elapsedMilliseconds, float? queuedPriority)
    {
        if (!currentActive) return EnbRetailSpeechDecision.Start;
        // 009FDF10 -> buffer +1C returns the selected filename, not the preset alias.
        if (string.Equals(requestedName, currentFile, StringComparison.Ordinal))
            return EnbRetailSpeechDecision.Reject;
        if (currentPriority < priority) return EnbRetailSpeechDecision.Interrupt;
        if (unchecked(durationMilliseconds - elapsedMilliseconds) >= 2000 ||
            queuedPriority is { } queued && priority <= queued)
            return EnbRetailSpeechDecision.Reject;
        return EnbRetailSpeechDecision.Queue;
    }
}
