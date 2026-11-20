using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public static class EnbBloodSoundRuntime
{
    //811B49 refreshes the RenderObj world matrix;811B64 passes its translation,
    //flags2 and gain1 to43EFE0. No source-bound Effect owner is retained.
    public static EnbCurrentRetailEffectSoundNode CreateReleased(IEnbRenderObject obj,
        string name,ulong sequence,uint clock,Vector3 worldPosition)
    {
        var node=CreateLoop(obj,name,sequence,clock);
        return node with {ReleasedWorldPosition=worldPosition,BoundGameId=null,
            Definition=node.Definition with {TrackSource=false,GoAwayWhenSourceDoes=false,
                Properties=new Dictionary<string,string>{{"SFX2D","FALSE"},{"TrackSource","FALSE"}}}};
    }

    //811BCB ->812210: client-local SoundEffect, source-bound, infinite
    //owner duration, TrackSource and KillOnSourceLost. Authored sounds.ini
    //still owns the actual voice loop count; the INI key does not override it.
    public static EnbCurrentRetailEffectSoundNode CreateLoop(IEnbRenderObject obj,
        string name,ulong sequence,uint clock)
    {
        var root=new EnbSceneEffectSnapshot(obj.GameId,obj.RenderIdentity,obj.GameId,0,0,clock,0,"",0,
            false,1,Vector3.Zero,0,0,false,Vector3.Zero,1,EnbSceneEffectGeometryKind.Object,
            Vector3.Zero,0,Vector3.Zero,0,0,null); // Source identity is retained on the sound node below.
        var definition=new EnbEffectDefinition(0,"SoundEffect",EnbEffectClassKind.SoundEffect,
            null,null,null,null,false,null,null,null,null,false,false,false,true,
            false,true,false,false,false,false,name,
            new Dictionary<string,string>{{"SFX2D","FALSE"},{"TrackSource","TRUE"}});
        return new(sequence,definition,root,obj.GameId,EnbCurrentRetailEffectLinkKind.Root,true,clock)
            {ClientDamage=true,SourceRenderIdentity=obj.RenderIdentity};
    }
}
