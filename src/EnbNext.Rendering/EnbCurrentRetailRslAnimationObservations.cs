using System.Collections.ObjectModel;

namespace EnbNext.Rendering;

/// <summary>Effect-age observations shared by bounds, drawing and retained
/// particle poses. Record in owner-history order; trim only after all consumers.</summary>
public sealed class EnbCurrentRetailRslAnimationObservations
{
    private readonly EnbCurrentRetailRslAnimationSequenceRuntime _clock;
    private readonly Dictionary<uint,EnbCurrentRetailRslAnimationSequenceRuntime.Sample> _samples=[];
    private uint? _latest;
    public IReadOnlyDictionary<uint,EnbCurrentRetailRslAnimationSequenceRuntime.Sample> Samples {get;}
    public EnbCurrentRetailRslAnimationObservations(EnbCurrentRetailRslAnimationSequenceRuntime effectAgeClock)
    {
        _clock=effectAgeClock;
        Samples=new ReadOnlyDictionary<uint,EnbCurrentRetailRslAnimationSequenceRuntime.Sample>(_samples);
    }
    public EnbCurrentRetailRslAnimationSequenceRuntime.Sample Record(uint age)
    {
        if(_samples.TryGetValue(age,out var existing))return existing;
        if(_samples.Count>=4096 || (_latest.HasValue && age<_latest.Value))
            throw new InvalidDataException("Animation observation history exceeded its bound or moved backwards");
        var sample=_clock.Advance(age);
        _samples.Add(age,sample);_latest=age;
        return sample;
    }
    public void RetainLatestSample()
    {
        if(_latest is not uint age || _samples.Count<2)return;
        var last=_samples[age];_samples.Clear();_samples.Add(age,last);
    }
}
