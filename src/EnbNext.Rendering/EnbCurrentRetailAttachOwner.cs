using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
namespace EnbNext.Rendering;

/// <summary>Retained unary source pointer and translation:006E7770/006E7690/006E7850/006E7870.</summary>
public sealed class EnbCurrentRetailAttachOwner : IDisposable
{
    private ulong? ordinal;
    private readonly bool initiallyAbsent,trackSource;
    private bool seenOwner,detached,activated;
    private IEnbRenderObject? source;
    private IEnbRenderObjectLifetime? lifetime;
    public IEnbRenderObject? Owner {get;private set;}
    public Vector3? SoundPosition {get;private set;}
    public EnbCurrentRetailAttachOwner(EnbCurrentRetailEffectOwner node)
    {
        ordinal=node.Root.EndpointReceipt?.SourceCreationOrdinal??node.Root.InitialSourceObject?.WorldCreationOrdinal;
        initiallyAbsent=node.Root.EndpointReceipt is {SourceCreationOrdinal:null};
        SoundPosition=node.Root.InitialSourceObject?.Spatial?.Transform.Position;
        trackSource=EnbRetailIniDocument.ReadBool(node.Definition.Properties,"TrackSource");
    }
    public void Update(EnbCurrentRetailEffectOwner node,IReadOnlyList<IEnbRenderObject> objects)
    {
        // Persistent source owners need no lookup after binding. Diagnostic/import
        // objects without lifetime notification are resolved explicitly each update.
        if(!initiallyAbsent&&!detached&&lifetime is null)
        {
            source=null;
            for(int i=0;i<objects.Count;i++)if(objects[i].GameId==node.BoundGameId){source=objects[i];break;}
            if(ordinal is {} expected&&source?.WorldCreationOrdinal!=expected)source=null;
            if(source is IEnbRenderObjectLifetime owned)
            {
                if(owned.IsRetired)source=null;
                else {lifetime=owned;owned.Retired+=SourceRetired;}
            }
            if(source is not null){ordinal??=source.WorldCreationOrdinal;seenOwner=true;}
            else if(seenOwner)detached=true;
        }
        if(node.Active&&(!activated||trackSource))SoundPosition=source?.Spatial?.Transform.Position??SoundPosition;
        activated|=node.Active;Owner=node.Active?source:null;
    }
    private void SourceRetired(){if(lifetime is {} owned)owned.Retired-=SourceRetired;source=null;Owner=null;detached=true;lifetime=null;}
    public void Dispose()
    {
        if(lifetime is {} owned)owned.Retired-=SourceRetired;
        lifetime=null;source=null;Owner=null;detached=true;
    }
}
