using EnbNext.Presentation;
namespace EnbNext.Rendering;

/// <summary>006E7770 source capture;006E7690 removal;006E7870 tracked refresh.</summary>
public sealed class EnbCurrentRetailEffectSourceOwner : IDisposable
{
    private readonly bool tracks;
    private IEnbRenderObject? source;
    private IEnbRenderObjectLifetime? lifetime;
    public IEnbRenderObject? Captured {get;private set;}
    public bool IsDetached {get;private set;}
    public EnbCurrentRetailEffectSourceOwner(EnbCurrentRetailEffectOwner node,bool tracks)
    {this.tracks=tracks;}

    public void Update(EnbCurrentRetailEffectOwner node,IReadOnlyList<IEnbRenderObject> objects)
    {
        if(IsDetached)return;
        var receipt=!node.UsesTargetBinding
            ?(tracks?node.Root.LastSourceObject:node.Root.InitialSourceObject)
            :node.BoundGameId==node.Root.TargetGameId?node.Root.PublishedEndpoints?.Target:null;
        var owner=lifetime is not null?source:FindOriginalOwner(node,objects);
        var identity=node.BoundRenderIdentity??Captured?.RenderIdentity??receipt?.RenderIdentity;
        if(owner is not null&&identity is not null&&owner.RenderIdentity!=identity)owner=null;
        if(receipt is not null&&identity is not null&&receipt.RenderIdentity!=identity)receipt=null;
        if(owner is IEnbRenderObjectLifetime persistent&&lifetime is null)
        {
            if(persistent.IsRetired)owner=null;
            else {source=owner;lifetime=persistent;persistent.Retired+=SourceRetired;}
        }
        if(owner is null&&(Captured is not null||receipt is not null))IsDetached=true;
        if(Captured is not null&&!tracks)return;
        owner=!tracks&&!node.UsesTargetBinding?receipt??owner:owner??receipt;
        if(owner?.Spatial is not null)Captured=!tracks&&owner is not EnbSceneObjectSnapshot?CapturePose(owner):owner;
    }

    // Imported diagnostic objects have no lifetime notifications. Production
    // binds once and subsequently reads the persistent source directly.
    internal static IEnbRenderObject? FindOriginalOwner(EnbCurrentRetailEffectOwner node,IReadOnlyList<IEnbRenderObject> objects)
    {
        var ordinal=!node.UsesTargetBinding?node.Root.EndpointReceipt?.SourceCreationOrdinal:node.Root.EndpointReceipt?.TargetCreationOrdinal;
        for(int i=0;i<objects.Count;i++)
        {
            var obj=objects[i];
            if(obj.GameId==node.BoundGameId&&(node.BoundRenderIdentity is null||obj.RenderIdentity==node.BoundRenderIdentity)&&
                (ordinal is null||obj.WorldCreationOrdinal==ordinal))return obj;
        }
        return null;
    }
    private static IEnbRenderObject CapturePose(IEnbRenderObject owner)
    {
        var captured=new EnbRenderObjectState(owner);
        if(owner.Spatial is {} current){var spatial=new EnbRenderSpatialState();spatial.AdoptDefinition(current);captured.Spatial=spatial;}
        return captured;
    }
    private void SourceRetired()
    {
        if(lifetime is {} old)old.Retired-=SourceRetired;
        if(tracks&&Captured is not null&&ReferenceEquals(Captured,source))Captured=CapturePose(Captured);
        source=null;lifetime=null;IsDetached=true;
    }
    public void Dispose()
    {
        if(lifetime is {} old)old.Retired-=SourceRetired;
        source=null;lifetime=null;Captured=null;IsDetached=true;
    }
}
