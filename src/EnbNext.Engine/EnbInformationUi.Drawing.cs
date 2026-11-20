using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Engine;

internal sealed partial class EnbInformationUi
{
    private bool helpAvatarAttempted,helpAvatarAvailable;
    public event Action<string,bool,Vector3>? AnimationSoundRequested;
    public EnbUiRect Design(string key)=>this[key[..key.IndexOf('/')]].Design(key);
    public EnbUiRect Screen(string key,EnbUiSize size)
    {var panel=this[key[..key.IndexOf('/')]];return panel.Screen(key,size,panel.Offset);}
    public void EndPortraits(bool preserveHelp=false)
    {if(!preserveHelp)help?.RetirePortrait();complaint?.RetirePortrait();}
    public void AdvancePortraits(int page,double now)
    {
        Advance(help,page==11);Advance(complaint,page==12);
        void Advance(EnbInformationPanel? panel,bool visible)
        {
            if(panel?.PortraitPlayback is not {} playback)return;
            // 006B4860 chooses hidden +64; 005D3800 does not advance the actor.
            if(!visible){playback.Pause(now);return;}
            panel.PortraitPose=playback.Evaluate(now);
            if(panel.PortraitPose?.AnimationSoundPosition is {} position)
                foreach(var cue in playback.AnimationSounds)
                    AnimationSoundRequested?.Invoke(cue.Sound.Sound,cue.Sound.Flat,position(cue.Bone));
        }
    }
    public string? Draw(ImmutableList<EnbUiModelDraw>.Builder list,EnbUiSize size,double scaling,int page,uint category,int logPage,
        EnbSceneEnvironmentSnapshot? environment,
        float blink,string? error,ref bool followCaret,long now)
    {
        string root=page==10?"log":page==12?"complaint":"help";
        foreach(string prefix in category!=0?(page==12?new[]{root,"player","petition"}:new[]{root,"petition"}):new[]{root})
        {
            if(Find(prefix) is not {} panel)continue;
            int scroll=prefix=="log"?LogInput!.Scroll:PetitionInput?.Editor.Scroll??0;
            panel.Draw(list,size,scaling,panel.Offset,page,category,logPage,null,null,blink,error,Body,Entry,Player,ref scroll,ref followCaret,true,now);
            if(prefix is not ("help" or "complaint") || list.Count==0)continue;
            if(!helpAvatarAttempted)
            {
                helpAvatarAttempted=true;
                try{_ = resources.HelpAvatar;helpAvatarAvailable=true;}
                catch(Exception ex) when(Failure(ex) || ex is FormatException or OverflowException){error="Help portrait unavailable: "+ex.Message;}
            }
            var avatar=helpAvatarAvailable?resources.HelpAvatar:null;
            if(panel.PortraitPlayback is null && avatar is not null)
            {
                try{panel.PortraitPlayback=new(resources.Assets,avatar,portraitIdle:true);panel.PortraitPlayback.StartIdle(now*.001);}
                catch(Exception ex) when(Failure(ex) || ex is NotSupportedException){error="Help portrait unavailable: "+ex.Message;}
            }
            var design=panel.Design(prefix+"/AVATAR");
            // 005D34F0/005D5880 each construct their own actor through005D4310.
            list[^1]=list[^1] with {Portrait=avatar,PortraitEnvironment=environment,PortraitBounds=panel.Screen(prefix+"/AVATAR",size,panel.Offset),
                PortraitAspect=design.Width/design.Height,PortraitCacheKey=prefix+"/megan",PortraitSharedActor=true,PortraitPose=panel.PortraitPose};
        }
        return error;
    }
    private static bool Failure(Exception ex)=>ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;
}
