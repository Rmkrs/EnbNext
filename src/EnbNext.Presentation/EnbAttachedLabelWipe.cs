namespace EnbNext.Presentation;

/// <summary>The WIPE RIGHT / STAY COMPLETE opening sequence (006CB25F, 006CB4FB, 006CC18B).</summary>
public sealed class EnbAttachedLabelWipe(bool stayComplete = true)
{
    private long? _started;
    private long? _paused;
    public bool Complete { get; private set; }

    public void Pause(long now) { if (_started.HasValue && (!Complete || !stayComplete)) _paused ??= now; }

    public int Update(long now, int rasterWidth, float speed)
    {
        if (Complete && stayComplete) return rasterWidth;
        if (_paused is long paused) { _started += now - paused; _paused = null; }
        _started ??= now;
        // The device selects PC24: each FMUL rounds to single precision before ftol.
        float scaled = (now - _started.Value) * speed;
        int extent = Math.Max(1, (int)(scaled * .01f));
        Complete = extent >= rasterWidth;
        return Math.Min(extent, rasterWidth);
    }
}
