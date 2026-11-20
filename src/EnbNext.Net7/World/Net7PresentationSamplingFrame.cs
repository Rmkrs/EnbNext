namespace EnbNext.Net7.World;

/// <summary>Reusable source-side sampling workspace. Begin and sample under
/// the session world boundary. It retains no poses and does not skip provider
/// evaluation, expiry, recursive sampling or object-getter bounds semantics.</summary>
public sealed class Net7PresentationSamplingFrame(Net7WorldState world)
{
    private readonly Net7SpatialSamplingContext _context=new(prediction:false);
    private uint _time;
    public void Begin(uint time){_context.BeginFrame(time);_time=time;}
    public bool TrySample(Net7WorldObjectBinding source,out Net7SpatialSample sample)
        =>world.TrySampleObjectPresentation(source.Target,_time,_context,out sample);
}
