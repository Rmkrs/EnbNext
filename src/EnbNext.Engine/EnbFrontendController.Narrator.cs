using EnbNext.Presentation;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbFrontendController
{
    private readonly Dictionary<string,EnbAvatarPortraitAsset> _narratorClips=[];
    private readonly HashSet<string> _unavailableNarratorClips=[];
    private string _narratorTag="walk_on";
    private string? _reminderCue;
    private EnbRetailLoginNarration _loginNarration;
    private double _narratorTime, _reminderTime=60;
    private long _lastNarratorTick=Environment.TickCount64;
    private EnbAvatarPortraitAsset? _previousNarratorPose, _lastNarratorPose;
    private EnbRetailIniDocument? _animationSounds;
    private EnbW3dAnimationSoundCatalog? _loginSoundCatalog;
    private float _loginSoundFrame;
    private EnbRetailNarratorTransition _loginNarratorTransition=new();
    private bool _loginNarratorCommand;
    private bool HasSceneNarrator => _screen=="login" || _screen=="creation" && _creationPage==CreationPage.Name;
    private void ResetNarrator()
    {
        _narratorTag="walk_on";_narratorTime=0;_reminderTime=60;_reminderCue=null;
        _previousNarratorPose=null;_lastNarratorPose=null;_lastNarratorTick=TickCount();
        _loginSoundFrame=0;
        _narratorAppearance ??= EnbRetailAvatarDefaults.Narrator(_assets);
        // 0067A6C0 retains the constructor's default head/morph/fidget flags.
        _narratorSpeech=new(_assets,_narratorAppearance,portraitIdle:true);
        _narratorSpeech.Idle.Next(_narratorSpeech.RandomInclusive);
        _loginNarratorTransition=new();
        _loginNarratorCommand=true;
        _narratorSpeech.Replace("/walk_on",TickCount()*.001,skipBlend:true);
    }
    private EnbAvatarPortraitAsset NarratorClip(string tag)
    {
        _narratorAppearance ??= EnbRetailAvatarDefaults.Narrator(_assets);
        if(!_narratorClips.TryGetValue(tag,out var clip))
            _narratorClips[tag]=clip=EnbAvatarPortraitAsset.Load(_assets,_narratorAppearance,tag);
        return clip;
    }
    private bool ChangeNarrator(string tag)
    {
        if(HasSceneNarrator && tag is "walk_on" or "walk_off")
        {
            if(!_loginNarratorTransition.Request(tag=="walk_off"))return false;
            _narratorSpeech!.Replace("/"+tag,TickCount()*.001,_lastNarratorPose,skipBlend:tag=="walk_on");
            if(_narratorSpeech.Diagnostic is {} diagnostic)Diagnostic?.Invoke(diagnostic);
            _loginNarratorCommand=true;_narratorTag=tag;_narratorTime=0;
            return true;
        }
        if(_unavailableNarratorClips.Contains(tag)) return false;
        try { NarratorClip(tag); }
        catch(AvatarAnimationUnavailableException)
        {
            _unavailableNarratorClips.Add(tag);
            Diagnostic?.Invoke("Narrator HAnim identity unavailable: " + tag);
            return false;
        }
        _previousNarratorPose=_lastNarratorPose;_narratorTag=tag;_narratorTime=0;
        return true;
    }
    private EnbAvatarPortraitAsset? UpdateNarrator()
    {
        long now=TickCount();double delta=Math.Clamp((now-_lastNarratorTick)/1000d,0,.25);_lastNarratorTick=now;
        _narratorAppearance??=EnbRetailAvatarDefaults.Narrator(_assets);
        _narratorSpeech??=new(_assets,_narratorAppearance,portraitIdle:true);
        _narratorTime+=delta;
        if(HasSceneNarrator)
        {
            UpdateLoginReminder((float)delta);
            // 679930 updates the actor before applying its requested visibility.
            // Use the command queue's idle state, not an unrelated clip duration.
            var loginPose=_narratorSpeech.Evaluate(now*.001);
            if(!_narratorSpeech.Active)_loginNarratorCommand=false;
            if(!_speechActive && !_narratorSpeech.Active && _speech.Count!=0)NextSpeech();
            _loginNarratorTransition.Update(!_narratorSpeech.Active);
            _narratorTag=_narratorSpeech.AnimationTag??_narratorTag;
            return _lastNarratorPose=loginPose??_lastNarratorPose;
        }
        if(!_loginExit && !_speechActive)
        {
            _narratorSpeech.Idle.Advance((float)delta,_narratorTag is not ("walk_on" or "walk_off"));
        }
        var clip=NarratorClip(_narratorTag);
        if(!_loginExit && !_creationCompleting && !_speechActive && _narratorTime>=clip.CycleDurationSeconds)
        {
            string tag=_narratorSpeech.Idle.Next(_narratorSpeech.RandomInclusive);
            if(_narratorSpeech.Idle.ResetHeadTargets)_narratorSpeech.HeadMotion.ClearTargets();
            if(ChangeNarrator(tag))clip=NarratorClip(tag);
        }
        float blend=.5f-.5f*MathF.Cos(MathF.PI*(float)Math.Clamp(_narratorTime/Math.Min(.5,clip.CycleDurationSeconds+1/clip.AnimationFramesPerSecond),0,1));
        var pose=!_loginExit && _speechActive ? _narratorSpeech.Evaluate(now*.001)
            : _narratorSpeech.EvaluateBody(_narratorTag,_narratorTime,
                _previousNarratorPose,blend,now*.001,false,_narratorTime==0,commandActive:_narratorTag is "walk_on" or "walk_off",loopBody:false);
        return _lastNarratorPose=pose??clip;
    }
    private void UpdateLoginReminder(float delta)
    {
        // 783B50 / 784262 gates on render-object hidden state, not speech,
        // input busy state or dialogs. The native comparison is strictly < 0.
        if(_loginNarratorTransition.Hidden || _reminderCue is not {} cue)return;
        _reminderTime=(float)_reminderTime-delta;
        if(_reminderTime<0){SelectSpeech(cue);_reminderTime=60;}
    }
    private void PlayLoginAnimationSounds()
    {
        _loginSoundCatalog ??= EnbW3dAnimationSoundCatalog.Parse(File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(Path.Combine(_assets.Paths.ClientIniDirectory,"W3DAnimSound.ini"))));
        if(_loginAnimationClip is not {} clip || _loginAnimationClock is not {} clock)return;
        string name=clip.Name.Contains('.')?clip.Name:"fe01A."+clip.Name;
        // 67AF70 binds an explicit frame each update. 9C5980 preserves the
        // previous bound frame even across HAnim changes; 909F90 dispatches.
        if(_loginSoundCatalog.TryGet(name,out var track))
            foreach(var cue in track.Crossed(_loginSoundFrame,clock.Frame))_gpu.QueueFrontendSound(cue.Sound);
        _loginSoundFrame=clock.Frame;
    }
}
