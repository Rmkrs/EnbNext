using EnbNext.Presentation;
namespace EnbNext.Rendering;

public sealed partial class EnbCurrentRetailEffectOwner
{
    private bool constructionReady,constructionSuccess;
    private EnbCurrentRetailRObjEffectDefinition construction=null!;
    private string? constructionError;
    public bool TryGetRObjDefinition(out EnbCurrentRetailRObjEffectDefinition result,out string? error)
    {
        if(!constructionReady)
        {
            constructionSuccess=EnbCurrentRetailRObjEffectDefinition.TryReadConstruction(this,out construction,out constructionError);
            constructionReady=true;
        }
        result=construction;error=constructionError;return constructionSuccess;
    }
    private void InvalidateConstruction()
    {
        constructionReady=false;construction=null!;constructionError=null;ClearSource();
    }
    public EnbCurrentRetailEffectSourceOwner? SourceBinding {get;private set;}
    public void UpdateSource(IReadOnlyList<IEnbRenderObject> objects)
    {
        if(!HasUnboundEndpoints && TryGetRObjDefinition(out var definition,out _))
            (SourceBinding??=new(this,definition.TrackSource)).Update(this,objects);
    }
    public IEnbRenderObject? ResolveSource(IReadOnlyList<IEnbRenderObject> objects)
        =>SourceBinding?.Captured??EnbCurrentRetailEffectSourceOwner.FindOriginalOwner(this,objects);
    public void ClearSource(){SourceBinding?.Dispose();SourceBinding=null;}
}
