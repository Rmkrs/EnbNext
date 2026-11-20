namespace EnbNext.Presentation;
/// <summary>View-owned handoff buffers. Queued readers retain their chosen slot.</summary>
public sealed class EnbSceneFramePool
{
    private readonly List<EnbSceneFrame> _frames=[new(),new()];
    private int _next;
    public int BufferCount=>_frames.Count;
    public EnbSceneFrame Prepare(IEnbSceneFrame source)
    {
        for(int n=0;n<_frames.Count;n++)
        {
            int i=(_next+n)%_frames.Count;
            if(_frames[i].IsRetained)continue;
            _next=(i+1)%_frames.Count;_frames[i].PrepareContext(source);return _frames[i];
        }
        var frame=new EnbSceneFrame();frame.PrepareContext(source);_frames.Add(frame);return frame;
    }
}
/// <summary>Explicit saved-scene/preview input adapter; the live client acquires
/// network samples directly and does not run this importer.</summary>
public sealed class EnbPresentationFrameImport
{
    private readonly EnbSceneFramePool _pool=new();
    public EnbSceneFrame? Acquire(IEnbSceneFrame? source)
    {
        if(source is null)return null;
        var frame=_pool.Prepare(source);frame.Import(source);return frame.Retain();
    }
}
