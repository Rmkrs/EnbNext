using System.Collections.Frozen;
using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public sealed partial class EnbCurrentRetailEffectRslObjectRuntime
{
    // Snapshot the existing runtime at the packet prefix recorded by each
    // controller mutation. No callbacks, selectors, loaders or RNG are replayed.
    private static void CaptureLegoControllerQueries(ObjectState state,IEnbRenderObject obj,int boundary)
    {
        ObjectState? query=null;
        var mutations=obj.LegoControllerMutations;
        for(int i=0;i<mutations.Length;i++)
        {
            var change=mutations[i];
            if(change.RenderStateActionCount!=boundary || state.LegoControllerQueryUpdates.ContainsKey(change.Sequence))continue;
            query??=CaptureLegoControllerQuery(state);
            state.LegoControllerQueryUpdates.Add(change.Sequence,query);
        }
    }

    private static ObjectState CaptureLegoControllerQuery(ObjectState state)=>state.CopyAtControllerMutation();
}
