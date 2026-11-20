using EnbNext.Assets;
using EnbNext.Presentation;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>700820's object-owned timer, consumed after 6F3C60 each frame.</summary>
public sealed class EnbSpaceSpeakerRuntime
{
    public const string Text = "\"....................\""; // B9EC7C: twenty dots.
    private sealed class State { public ulong Revision; public float Remaining; public EnbRgb Color; }
    private readonly Dictionary<ulong,State> states=[];
    private Guid stream;
    public EnbSceneFrame ApplyFrame(EnbSceneFrame scene,uint elapsed,IReadOnlyList<EnbRgb> colors)
    {
        if(stream!=scene.ClientSoundStreamId){states.Clear();stream=scene.ClientSoundStreamId;}
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        var seen=new HashSet<ulong>();var objects=scene.Objects;
        for(int i=0;i<objects.Count;i++)
        {
            var obj=scene.MutableObject(i);seen.Add(obj.RenderIdentity);
            if(obj.SpeakerRevision==0)continue;
            if(!states.TryGetValue(obj.RenderIdentity,out var state))states[obj.RenderIdentity]=state=new();
            if(state.Revision!=obj.SpeakerRevision)
            {
                state.Revision=obj.SpeakerRevision;state.Remaining=2;
                state.Color=obj.SpeakerColor??(obj.SpeakerType<colors.Count?colors[obj.SpeakerType]:EnbRetailChatColors.Default(obj.SpeakerType));
            }
            // Object type29 returns before the common timer update.
            if(obj.ObjectType!=0x29 && state.Remaining>0)
            { R next=(R)state.Remaining-(R)elapsed*(R).001f;state.Remaining=next<(R)0?0:(float)next; }
            obj.SpeakingColor=state.Remaining>0?state.Color:null;
        }
        foreach(ulong key in states.Keys.Where(k=>!seen.Contains(k)).ToArray())states.Remove(key);
        return scene;
    }
}
