using System.Collections.Immutable;
using System.Numerics;

namespace EnbNext.Net7.Gameplay;

// Immutable render-frame receipt. Packet-time queries must not evaluate a
// later pose or conflate exact controller/mount names with hierarchy names.
public sealed record Net7EffectSourceControllerPose(
    Matrix4x4 World,
    ImmutableDictionary<string, Matrix4x4> Mounts,
    ImmutableDictionary<string, Matrix4x4> Hierarchy,
    ImmutableDictionary<string, Net7EffectSourceControllerPose> Children)
{
    public Matrix4x4 ResolveLocal(string name)
        => Mounts.TryGetValue(name,out var mount) ? mount
            : Hierarchy.TryGetValue(name,out var pivot) ? pivot : World;

    public Matrix4x4 ResolveEffectChain(string chain)
    {
        //843970/4FC2F0 preserve empty interior tokens, omit the trailing one.
        //6E7AA0/6E8F80 append EMITTER;72B000 stops at the first missing child.
        if(chain.Length==0)return World;
        var path=chain.Split('/',StringSplitOptions.None);
        int count=path.Length-(chain[^1]=='/'?1:0);
        var current=this;
        for(int i=0;i<count;i++)
        {
            if(!current.Children.TryGetValue(path[i],out var child))return current.ResolveLocal(path[i]);
            current=child;
        }
        return current.ResolveLocal("EMITTER");
    }
}
