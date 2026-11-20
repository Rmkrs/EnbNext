namespace EnbNext.Rendering;

public interface IEnbRetailSpeechVoice
{
    bool Active { get; }
    string FileName { get; }
    float Priority { get; }
    uint DurationMilliseconds { get; }
    uint ElapsedMilliseconds { get; }
    void Stop();
    void ReleaseManagerReference();
}

/// <summary>
/// Shared dialogue ownership from 0043F2D0 / 0043F720 / 0043F920.
/// The factory owns file selection, call-interval admission and playback startup.
/// A queued request returns no caller handle and is owned by this manager.
/// </summary>
public sealed class EnbRetailSpeechManager<T> where T : class, IEnbRetailSpeechVoice
{
    private sealed record Request(string Name, float Priority, Func<T?> Create, object? Owner);
    private Request? _pending;
    public T? Current { get; private set; }
    public bool HasPending => _pending is not null;

    public T? RequestSpeech(string name, float priority, Func<T?> create, object? owner = null)
    {
        if (Current is { Active: false }) ReleaseCurrent();
        var decision = EnbRetailSpeechAdmission.Decide(name, priority, Current is not null,
            Current?.FileName, Current?.Priority ?? 0, Current?.DurationMilliseconds ?? 0,
            Current?.ElapsedMilliseconds ?? 0, _pending?.Priority);
        switch (decision)
        {
            case EnbRetailSpeechDecision.Reject: return null;
            case EnbRetailSpeechDecision.Queue:
                _pending = new(name, priority, create, owner);
                return null;
            case EnbRetailSpeechDecision.Interrupt:
                Current!.Stop();
                ReleaseCurrent();
                break;
        }
        Current = create();
        // Native leaves a pending definition without a current owner when a
        // replacement factory fails; 0043F720 then asserts instead of draining.
        // Contain that failed ownership chain locally, without inventing playback.
        if (Current is null) _pending = null;
        return Current;
    }

    public void Update()
    {
        if (Current is null || Current.Active) return;
        ReleaseCurrent();
        var request = _pending;
        _pending = null;
        if (request is not null) Current = request.Create();
    }

    public void Stop()
    {
        Current?.Stop();
        ReleaseCurrent();
        // Retail clears the queued name but leaves an obsolete definition pointer.
        // Retaining that pointer serves no playback behavior and trips its next
        // no-current assertion. Keep the queued request as one owned value.
        _pending = null;
    }

    public void DiscardPendingOwner(object owner)
    {
        if (ReferenceEquals(_pending?.Owner, owner)) _pending = null;
    }

    private void ReleaseCurrent()
    {
        var previous = Current;
        Current = null;
        previous?.ReleaseManagerReference();
    }
}
