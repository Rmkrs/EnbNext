using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Avatar manual HLOD submissions: 783620 -> 9C5980/9C5A80 ->
/// 9C5DD0/909F90. Only the scalar union fields used by these two modes are
/// retained; no synthetic native pointer values are needed.</summary>
public sealed class EnbRetailAvatarAnimationCues
{
    public sealed record Cue(string Animation,string Bone,EnbW3dAnimationSoundCue Sound);
    private bool paired;
    private float singleFrame,wordD0,wordD4;
    public IReadOnlyList<Cue> Submit(EnbRetailAvatarBodyClock.Pose pose,EnbW3dAnimationSoundCatalog catalog)
    {
        var result=new List<Cue>();
        float Dispatch(string name,float previous,float current)
        {
            if(!catalog.TryGet(name,out var track))return previous;
            float last=previous;
            foreach(var cue in track.Crossed(previous,current))
            {result.Add(new(name,track.BoneName,cue));last=cue.Frame;}
            return last;
        }
        if(pose.Secondary is {} second)
        {
            // Pair binding copies the old D0/D4, regardless of prior mode.
            // Both animations dispatch, even at blend weight zero or one.
            float firstPrevious=wordD0,secondPrevious=wordD4;
            wordD0=pose.PrimaryFrame;wordD4=pose.SecondaryFrame;
            Dispatch(pose.Primary.Name,firstPrevious,wordD0);
            Dispatch(second.Name,secondPrevious,wordD4);
            paired=true;
        }
        else
        {
            // Releasing the former second pointer clears CC on pair->single.
            wordD0=paired?0:singleFrame;
            singleFrame=pose.PrimaryFrame;wordD4=0;paired=false;
            wordD0=Dispatch(pose.Primary.Name,wordD0,singleFrame);
        }
        return result;
    }
}
