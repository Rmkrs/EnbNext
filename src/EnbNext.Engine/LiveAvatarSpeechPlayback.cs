using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>Owned text/action queue and three-source facial playback, 00782200.</summary>
internal sealed class LiveAvatarSpeechPlayback
{
    private readonly EnbRetailAssetBundle assets;
    private EnbSceneAvatarSnapshot appearance;
    private readonly EnbRetailAvatarSpeech parser;
    private readonly EnbRetailIniDocument personality;
    private readonly Dictionary<string,EnbAvatarPortraitAsset> clips=new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<EnbRetailAvatarSpeechCommand> queue=new();
    private readonly EnbRetailAvatarBodyClock bodyClock=new();
    private readonly EnbRetailAvatarAnimationCues animationCues=new();
    public IReadOnlyList<EnbRetailAvatarAnimationCues.Cue> AnimationSounds {get;private set;}=[];
    private readonly Dictionary<string,EnbAvatarPortraitAsset> bodyAssets=new(StringComparer.OrdinalIgnoreCase);
    private EnbAvatarPortraitAsset? clip,previous,last;
    private EnbRetailAvatarFacialMap? face;
    private EnbCurrentRetailHierarchyAnimationState? facialClock;
    private bool headBindingLost;
    private double facialRate;
    private double clock,lastNow;
    private int speechFramesRemaining;
    private int speechBoundary=-1;
    private float finalBrow;
    private int mood;
    private bool skipBlend,skipFirst;
    private bool idle;
    private readonly bool portraitIdle;
    private string? externalTag;
    private string? locomotionTag;
    private bool forceBodySelection;
    private bool repeatLocomotion;
    private int movementState=2,footstepStage;
    public bool Footstep {get;private set;}
    private long externalCycle=-1;
    public EnbRetailAvatarIdle Idle {get;}
    public EnbRetailAvatarHeadMotion HeadMotion {get;}=new();
    public Func<int,int,int> RandomInclusive {get;set;}=EnbRetailPresentationRandom.Shared.Next;
    public bool Active=>!idle && (clip is not null || queue.Count>0);
    public string? AnimationTag {get;private set;}
    public string? Diagnostic {get;private set;}
    public bool LocomotionSelected=>locomotionTag is not null && AnimationTag==locomotionTag;
    public int Mood=>mood;
    public EnbAvatarPortraitAsset? LastPose=>last;
    public LiveAvatarSpeechPlayback(EnbRetailAssetBundle assets,EnbSceneAvatarSnapshot appearance,bool portraitIdle=false)
    {
        this.portraitIdle=portraitIdle;
        this.assets=assets;this.appearance=appearance;
        // 0077B870 copies AvatarData to +F8; 00782CA0 reads its mood at +114.
        mood=unchecked((int)appearance.MoodType);
        personality=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientDataDirectory,"personalities",$"Personality{appearance.Personality:00}.ini"));
        Idle=new(personality);
        string lexicon=Path.Combine(assets.Paths.ClientDataDirectory,"nlp",$"lexicon{appearance.Nlp:00}.ini");
        parser=new(personality,EnbNext.Installation.EnbPathCase.FileExists(lexicon)?EnbRetailIniDocument.Load(lexicon):EnbRetailIniDocument.Parse(""));
    }
    public void Replace(string text,double now,EnbAvatarPortraitAsset? from=null,bool skipBlend=false)
        => ReplaceText(text,true,skipBlend);
    // 55D060 requests /normal with both request flags clear when leaving or
    // reselecting a staged roster actor. Its current body/speech may finish.
    public void ResetRosterMood()=>ReplaceText("/normal",false,false);
    public IReadOnlyList<EnbNext.Net7.Gameplay.Net7PlayerNlpRecord> ProduceChat(string text)
    {
        // 781760 updates avatar+114 while producing, before server echo. It
        // does not queue local speech or restart the retained body animation.
        var records=parser.Produce(text,RandomInclusive);
        if(parser.FinalMood>=0)mood=parser.FinalMood;
        return records.Select(r=>new EnbNext.Net7.Gameplay.Net7PlayerNlpRecord(r.Type,r.Argument,r.Text)).ToArray();
    }
    private void ReplaceText(string text,bool force,bool skipBlend)
    {
        // 784A50 has the same deferred +214 selection as received NLP, with
        // a separate caller-supplied +215 flag. Neither flag discards a blend
        // already in progress; 783B50 waits for that blend before selecting.
        queue.Clear();externalTag=null;forceBodySelection=force;idle=false;skipFirst=skipBlend;Diagnostic=null;
        if(force)speechBoundary=-1;
        // 67A6C0 activates the narrator through 77E3D0(1). 784700's
        // terminal branch tests that activation state (+204), not AvatarType;
        // state 1 adds no extra breathe command unless a trailing mood needs it.
        foreach(var command in parser.Parse(text,RandomInclusive))queue.Enqueue(command);
        if(parser.FinalMood>=0)mood=parser.FinalMood;
    }
    public void Clear(){queue.Clear();clip=null;face=null;facialClock=null;externalTag=locomotionTag=null;forceBodySelection=false;previous=last=null;AnimationTag=null;idle=false;bodyClock.Reset();}
    public void StartIdle(double now)
    {
        movementState=2;Footstep=false;
        queue.Clear();previous=last;externalTag=locomotionTag=null;forceBodySelection=false;clock=0;lastNow=now;StartNext(clock);
    }
    // 00783B50 requests state 2 after movement input; 007826A0 selects it
    // after the retained body slots have advanced. Repeated walk cycles set +215.
    public void SetLocomotion(string? tag)
    {
        if(tag==locomotionTag)return;
        locomotionTag=tag;repeatLocomotion=false;forceBodySelection=true;
        movementState=tag is null?2:0;
    }
    public void ClearPendingCommands()=>queue.Clear();
    // Editor/class attachment sets avatar+214 without discarding its slots.
    public void RequestIdleSelection()=>forceBodySelection=true;
    // 0077E9D0 / 0077F750 replace appearance resources, not actor+C0/C4.
    // Editor gender changes construct a new actor instead (0067A3F0/0067A440).
    public void RefreshAppearance(EnbSceneAvatarSnapshot value)
    {
        if(ReferenceEquals(appearance,value))return;
        // 77F750 clones HeadSource; 98CA20/9C5540 leaves its HAnim unbound.
        // Body slots and the generated map survive. The next 783470 selection
        // binds a map to the replacement head; a talk continuation does not.
        bool headTypeChanged=appearance.HeadType!=value.HeadType || appearance.Gender!=value.Gender ||
            appearance.Race!=value.Race || appearance.AvatarType!=value.AvatarType;
        bool replacesHead=headTypeChanged || appearance.HairNum!=value.HairNum ||
            appearance.EarNum!=value.EarNum || appearance.GoggleNum!=value.GoggleNum || appearance.BeardNum!=value.BeardNum ||
            !appearance.HairColor.AsSpan().SequenceEqual(value.HairColor.AsSpan()) ||
            !appearance.BeardColor.AsSpan().SequenceEqual(value.BeardColor.AsSpan());
        appearance=value;
        var retained=clips.ToArray();
        clips.Clear();
        foreach(var (key,old) in retained)
        {
            var replacement=key.StartsWith('#')?Load("",int.Parse(key.AsSpan(1)),old,headTypeChanged):Load(key,null,old,headTypeChanged);
            if(replacement is null)continue;
            // 77F000's missing HeadSource branch logs and skips 77F750:
            // the old attached HeadObj and its animation remain intact.
            if(replacesHead && replacement.HeadLoadDiagnostic is null){headBindingLost=true;facialClock=null;}
            bodyAssets[old.AnimationSoundKey]=replacement;
            if(ReferenceEquals(clip,old))clip=replacement;
        }
    }
    public void PlayAmbientAction(int number,double now)
    {
        speechFramesRemaining=0;
        queue.Clear();locomotionTag=null;forceBodySelection=false;externalTag=null;
        clip=Load("",number);AnimationTag=$"#{number}";idle=false;skipBlend=false;
        if(clip is not null)
        {
            face=clip.CreateFacialMap!(mood,"",finalBrow,true,RandomInclusive);
            BindFacialClock(clip,now);
            finalBrow=face.FinalBrow;
        }
        BindBody();
    }
    public void Pause(double now)=>lastNow=now;
    // Room-local elapsed time restarts; retail's global render clock does not.
    // Shift the facial epoch too, preserving its independently retained phase.
    public void RebaseTime(double previousNow,double now)
    {
        if(facialClock is not null)facialClock.PreviousTime=unchecked(facialClock.PreviousTime+FacialTime(now)-FacialTime(previousNow));
        lastNow=now;
    }
    // 007899F0/00789C10 replace/seek only the body animation. They do not
    // rebuild the independent facial maps or change the idle action state.
    public bool ControlledBodyActive=>bodyClock.Controlled;
    public void BindControlledBody(string tag,float target,uint flags)
    {
        // 7899F0 returns before 7831C0 when personality lookup has no tag.
        // That differs from a known tag whose optional HAnim cannot load:
        // the latter still applies control flags to the retained primary.
        if(!personality.EnumerateSections().Any(section=>section.Value.TryGetValue("Tag",out var value) &&
            value.Equals(tag,StringComparison.OrdinalIgnoreCase)))
        {
            Diagnostic=$"Avatar controlled action '{tag}' has no personality mapping.";
            return;
        }
        clip=Load(tag);AnimationTag=tag;
        bodyClock.Bind(clip is null?null:BodyClip(clip));
        bodyClock.SetTarget(target,flags);
    }
    public void SetControlledBodyTarget(float target,uint flags)=>bodyClock.SetTarget(target,flags);
    // Station movement/emote owners select and time the body. Retail 007826A0
    // still rebuilds the facial maps, and 00783B50/007854B0 update the head.
    public EnbAvatarPortraitAsset? EvaluateBody(string tag,double seconds,
        EnbAvatarPortraitAsset? from,float weight,double now,bool locomotion,bool restart,bool commandActive=true,bool loopBody=true)
    {
        var body=Load(tag);
        if(body is null)return last;
        return EvaluateBody(body,tag,seconds,from,weight,now,locomotion,restart,commandActive,loopBody);
    }
    // Appearance editors replace mesh/morph resources without constructing a
    // new actor clock, head controller, or random stream owner.
    public EnbAvatarPortraitAsset EvaluateBody(EnbAvatarPortraitAsset body,string tag,double seconds,
        EnbAvatarPortraitAsset? from,float weight,double now,bool locomotion,bool restart,bool commandActive=true,bool loopBody=true)
    {
        float delta=(float)Math.Clamp(now-lastNow,0,.25);lastNow=now;
        long cycle=loopBody?(long)(seconds/body.CycleDurationSeconds):0;
        if(restart || externalTag!=tag || externalCycle!=cycle)
        {
            externalTag=tag;externalCycle=cycle;
            if(locomotion)Idle.ResetForActivity();
            face=body.CreateFacialMap!(mood,"",finalBrow,!locomotion && commandActive,RandomInclusive);
            BindFacialClock(body,now);
            finalBrow=face.FinalBrow;
        }
        if(portraitIdle && Idle.HeadMotionEnabled)HeadMotion.Advance(delta,RandomInclusive);
        double phase=seconds-cycle*body.CycleDurationSeconds;
        last=body.EvaluateFacialWithHead!(phase,from,weight,
            SampleFacialClock(now),
            portraitIdle && Idle.HeadMotionEnabled?HeadMotion.Matrix:System.Numerics.Matrix4x4.Identity);
        PositionBody(body,phase);
        return last;
    }
    private EnbRetailAvatarBodyClock.Clip BodyClip(EnbAvatarPortraitAsset body)
    {
        bodyAssets[body.AnimationSoundKey]=body;
        var animation=body.BodySamples.Last().Animation;
        return new(body.AnimationSoundKey,animation.NumFrames,animation.FrameRate);
    }
    private void PositionBody(EnbAvatarPortraitAsset body,double seconds)
    {
        var description=BodyClip(body);
        bodyClock.Position(description,(float)Math.Clamp(seconds*description.Rate,0,description.Frames-1));
    }
    private void BindBody()=>bodyClock.Bind(clip is null?null:BodyClip(clip),skipBlend);
    // 00783470 binds the generated head HAnim independently at frame zero,
    // playback 2. Its rate is HANIM00's +14 (00987B90), and 009C6270 uses
    // the render millisecond clock, not the avatar's capped body-update delta.
    private static uint FacialTime(double now)=>unchecked((uint)(long)Math.Round(now*1000));
    private void BindFacialClock(EnbAvatarPortraitAsset body,double now,bool hasSpeechText=false)
    {
        headBindingLost=false;
        // 00782FF0 computes +248 from speech frames minus the selected body's
        // complete NumFrames, not duration or the body's last frame index.
        speechFramesRemaining=Math.Max(0,(face?.SpeechEndFrame??0)-checked((int)body.BodySamples.Last().Animation.NumFrames));
        // 782FF0 retains +24C for text, but sets it to -1 for an empty string.
        speechBoundary=hasSpeechText?face?.SpeechEndFrame??-1:-1;
        facialRate=body.FacialFramesPerSecond;
        facialClock=face is {AnimationFrameCount:>0}
            ?new(){Mode=2,Playback=2,HasAnimation=true,HasHierarchy=true,PreviousTime=FacialTime(now)}:null;
    }
    private EnbRetailAvatarFace SampleFacialClock(double now)
    {
        if(face is null || facialClock is null)return last?.FacialPose??new(0,0,0);
        uint time=FacialTime(now);
        facialClock.RefreshSingle(()=>time,()=>facialRate,()=>face.AnimationFrameCount,
            _=>{},()=>0,()=>0,(_,_,_)=>0);
        return face.Sample(facialClock.CurrentFrame);
    }
    public void ReplaceReceived(IReadOnlyList<EnbRetailAvatarSpeechCommand> commands, int receivedMood, double now)
    {
        // 784B80 replaces pending records and sets +214, not +215. 783B50
        // advances existing slots first, waits for any blend, then selects
        // the received action. Existing body/facial bindings survive ingress.
        queue.Clear();externalTag=null;idle=false;forceBodySelection=true;skipFirst=false;Diagnostic=null;
        speechBoundary=-1;
        mood=receivedMood;
        foreach(var command in commands)queue.Enqueue(command);
    }
    private EnbAvatarPortraitAsset? Load(string tag, int? number=null,EnbAvatarPortraitAsset? retained=null,bool retryHead=false)
    {
        string key=number is int id?$"#{id}":tag;
        if(clips.TryGetValue(key,out var value))return value;
        try
        {
            value=EnbAvatarPortraitAsset.LoadRetainingHead(assets,appearance,tag,number,retained??clip??last,!retryHead);
            if(value.HeadLoadDiagnostic is not null)Diagnostic="Avatar head unavailable: "+value.HeadLoadDiagnostic;
            return clips[key]=value;
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException or AvatarAnimationUnavailableException)
        {Diagnostic=$"Avatar action '{key}' unavailable: {ex.Message}";return null;}
    }
    private void StartNext(double now)
    {
        speechFramesRemaining=0;
        skipBlend=skipFirst;skipFirst=false;
        if(locomotionTag is {} movement)
        {
            movementState=1;footstepStage=0;
            skipBlend=repeatLocomotion;repeatLocomotion=true;
            idle=false;AnimationTag=movement;clip=Load(movement);
            Idle.ResetForActivity();
            if(clip is not null)
            {
                face=clip.CreateFacialMap!(mood,"",finalBrow,true,RandomInclusive);
                BindFacialClock(clip,lastNow);
                finalBrow=face.FinalBrow;
            }
            BindBody();return;
        }
        if(queue.Count>0)
        {
            HeadMotion.ClearTargets();
            if(Idle.Posture==0)Idle.ResetForActivity();
        }
        if(queue.TryDequeue(out var command))
        {
            if(command.Mood>=0)mood=command.Mood;
            clip=Load(command.AnimationTag,command.AnimationNumber);
            AnimationTag=command.AnimationNumber is int number?$"#{number}":command.AnimationTag;
            if(clip is null)
            {
                BindBody();
                // 782200 consumes one record even on failure. 782FF0 uses C4
                // or retained C0 for the speech budget; the following command
                // stays queued until the normal body-selection gate opens.
                if(bodyClock.SelectedClip is {} retained)
                {
                    clip=bodyAssets[retained.Name];
                    int? missingNumber=command.AnimationNumber;
                    if(missingNumber is null)
                        foreach(var section in personality.EnumerateSections())
                            if(section.Value.TryGetValue("Tag",out var tag) && tag.Equals(command.AnimationTag,StringComparison.OrdinalIgnoreCase) &&
                                int.TryParse(section.Value.GetValueOrDefault("AnimNumber"),out int value))missingNumber=value;
                    if(missingNumber is int action)
                    {
                        face=clip.CreateFacialMap!(mood,command.Text,finalBrow,true,RandomInclusive,$"ABANIM{appearance.Personality:00}-{action:00}");
                        BindFacialClock(clip,lastNow,command.Text.Length!=0);finalBrow=face.FinalBrow;
                    }
                }
                return;
            }
            face=clip.CreateFacialMap!(mood,command.Text,finalBrow,true,RandomInclusive);
            BindFacialClock(clip,lastNow,command.Text.Length!=0);
            finalBrow=face.FinalBrow;
            BindBody();
            return;
        }
        idle=true;AnimationTag=portraitIdle?Idle.Next(RandomInclusive):"breathe1";clip=Load(AnimationTag);
        if(portraitIdle && Idle.ResetHeadTargets)HeadMotion.ClearTargets();
        if(clip is not null)
        {
            face=clip.CreateFacialMap!(mood,"",finalBrow,false,RandomInclusive);
            BindFacialClock(clip,lastNow);
            finalBrow=face.FinalBrow;
        }
        BindBody();
    }
    public EnbAvatarPortraitAsset? Evaluate(double now)
    {
        AnimationSounds=[];
        //783B50/783BE8 caps the actor's frame delta at250ms before783620.
        float elapsed=(float)Math.Clamp(now-lastNow,0,.25);
        clock+=elapsed;lastNow=now;now=clock;
        if(portraitIdle)Idle.Advance(elapsed,idle);
        if(portraitIdle && Idle.HeadMotionEnabled)HeadMotion.Advance(elapsed,RandomInclusive);
        var submitted=bodyClock.Advance(elapsed);
        Footstep=false;
        if(movementState!=2 && bodyClock.AdvancedSingleProgress is {} phase &&
            (footstepStage==0 && phase>.1f || footstepStage==1 && phase>.6f))
        {
            Footstep=true;
            footstepStage++;
            if(footstepStage==1 && phase>.6f)footstepStage=2;
        }
        // 783620 sets +214 after submitting a lone body beyond +24C when a
        // queued record remains. 783B50 then clears +248 and selects it. An
        // empty-text facial map has +24C=-1 and cannot force this transition.
        bool speechForced=queue.Count>0 && speechBoundary>0 && submitted is {Secondary:null} && submitted.PrimaryFrame>speechBoundary;
        if(bodyClock.CanSelect || (forceBodySelection || speechForced) && !bodyClock.Blending)
        {
            bool forced=forceBodySelection || speechForced;
            forceBodySelection=false;
            previous=last;
            // The mouth's independent map may outlive its body gesture. Native
            // 00782200 chooses another talk clip without rebuilding that map.
            string? continuation=!forced && locomotionTag is null && speechFramesRemaining>0 && clip is not null?parser.Talk(speechFramesRemaining,RandomInclusive):null;
            if(continuation is not null)
            {
                HeadMotion.ClearTargets(); // 782200 entry, including talk continuation.
                clip=Load(continuation);AnimationTag=continuation;skipBlend=false;BindBody();
                // 00782200's continuation branch preserves all facial maps and
                // subtracts C4's full frame count, or retained C0 on failure.
                // With neither slot, contain the unusable optional continuation.
                if(bodyClock.SelectedClip is {} retained)
                {
                    clip=bodyAssets[retained.Name];
                    speechFramesRemaining=Math.Max(0,speechFramesRemaining-checked((int)retained.Frames));
                }
                else speechFramesRemaining=0;
            }
            else StartNext(now);
        }
        if(submitted is null)return last;
        // 007831C0 can leave the attempted slot empty. 00783620 continues the
        // retained primary, and 00783B50 retries idle selection when it ends.
        var sample=SampleFacialClock(lastNow);
        var head=portraitIdle && Idle.HeadMotionEnabled?HeadMotion.Matrix:System.Numerics.Matrix4x4.Identity;
        var primary=bodyAssets[submitted.Primary.Name];
        last=headBindingLost?primary.EvaluateUnboundHead!(submitted.PrimaryFrame/submitted.Primary.Rate,null,1,head)
            :primary.EvaluateFacialWithHead!(submitted.PrimaryFrame/submitted.Primary.Rate,null,1,sample,head);
        if(submitted.Secondary is {} secondary)
        {
            var next=bodyAssets[secondary.Name];
            last=headBindingLost?next.EvaluateUnboundHead!(submitted.SecondaryFrame/secondary.Rate,last,submitted.Weight,head)
                :next.EvaluateFacialWithHead!(submitted.SecondaryFrame/secondary.Rate,last,submitted.Weight,sample,head);
        }
        if(assets.AnimationSounds is {} sounds)AnimationSounds=animationCues.Submit(submitted,sounds);
        return last;
    }
}
