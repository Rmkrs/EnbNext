using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    public Func<EnbRecoveryRequest,Task<bool>>? RecoveryRequested { get; set; }
    private Net7IncapacitationDetails? _recoverySeen, _recovery;
    private LiveAvatarSpeechPlayback? _recoveryPlayback;
    private EnbAvatarPortraitAsset? _recoveryPose;
    private string _recoveryText="";
    private int _recoveryChoice=-1;
    private void UpdateRecovery()
    {
        if (!_interactive || _scene?.Hud.Cockpit?.Warp?.CanRequestRescue != true)
        { _recovery = null; _recoveryPlayback=null;_recoveryPose=null;_recoverySeen = _scene?.IncapacitationDetails; return; }
        if (_scene.IncapacitationDetails is {} details && !ReferenceEquals(details, _recoverySeen))
        {
            _recoverySeen = details;
            CloseAll();LoadConversationArt(recovery:true);
            _recovery = details;_recoveryChoice=-1;_recoveryText=details.RemainingOuterStrings[0];_recoveryAnswers.Clear();_dialogueWipe.Clear();
            _recoveryPlayback=null;_recoveryPose=null;
            // 584690 -> 552FC0 constructs an independent avatar from the
            // packet descriptor; 552980 submits the complete opening text.
            try
            {
                if(_assets is {} assets && _scene.RecoveryAvatar is {} avatar)
                {
                    _recoveryPlayback=new(assets,avatar,portraitIdle:true);
                    _recoveryPlayback.StartIdle(Environment.TickCount64*.001);
                    _recoveryPlayback.Replace(_recoveryText,Environment.TickCount64*.001);
                }
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
            {Error=ex.Message;}
            Opened?.Invoke();InteractionSoundRequested?.Invoke("message_1.wav");
        }
        AdvanceRecoveryPortrait(Environment.TickCount64*.001);
    }
    private void AdvanceRecoveryPortrait(double now)
    {
        if(_recovery is null || _recoveryPlayback is not {} playback)return;
        _recoveryPose=playback.Evaluate(now);
        if(_recoveryPose?.AnimationSoundPosition is {} position)
            foreach(var cue in playback.AnimationSounds)AnimationSoundRequested?.Invoke(cue.Sound.Sound,cue.Sound.Flat,position(cue.Bone));
        // 552C60 closes after the chosen response reaches avatar+220.
        // 581670 dispatches the retained choice only after the dialog hides.
        if(_recoveryChoice>=0 && !playback.Active)_=CompleteRecovery();
    }
    private async Task CompleteRecovery()
    {
        if(_recovery is not {} details)return;
        int choice=_recoveryChoice;
        _recovery=null;_recoveryPlayback=null;_recoveryPose=null;
        if(session() is uint character && _scene is {} scene && RecoveryRequested is {} send)
            await Send(()=>send(new(character,scene.LocationTransition?.Version??0,_ship,details,choice)));
    }
    private void RecoveryPanel()
    {
        if (_recovery is not {} details) return;
        if (ModernUi) { ModernRecoveryPanel(); return; } // Next Modern UI: the same recovery dialogue in the Modern UI dialogue window.
        foreach (var entry in _art.Where(a => a.Key.StartsWith("talk/") && a.Key.Contains("FRAME")).OrderBy(a => a.Key,StringComparer.Ordinal))
        {
            var r=entry.Value.Design.Translate(new(320,240)); Draw(entry.Key,r); Hit("recoveryFrame/"+entry.Key,r);
            if(entry.Key=="talk/FRAME02" && _scene?.RecoveryAvatar is {} avatar && ConversationRect("talk","PICTURE") is {} inner)
            {
                _draws[^1]=_draws[^1] with {Portrait=avatar,PortraitEnvironment=_scene.Environment,
                    PortraitCacheKey="recovery/avatar",PortraitSharedActor=true,PortraitPose=_recoveryPose,PortraitBounds=(Screen(inner)),PortraitAspect=inner.Width/inner.Height};
            }
        }
        if(_scene?.RecoveryAvatar is {} speaker)
            DialogueText("NAME","recoveryName",string.Join(" ",new[]{speaker.FirstName,speaker.LastName}.Where(s=>!string.IsNullOrEmpty(s))));
        var lines=DialogueLines(_recoveryText);
        for(int i=0;i<6;i++)DialogueText($"TALK{i:00}","recoveryText"+i,lines.ElementAtOrDefault(i)??"");
        if(_recoveryChoice<0 && _conversationLayouts["talk"].TryResolve("Reply_TLI01A_AV",out var reply))
            DialogueText("REPLY","recoveryReply",reply.Properties.GetValueOrDefault("String0",""));
        if(_recoveryChoice<0)
        for(int i=0;i<3;i++)
            if(details.RemainingOuterStrings[1+i].Length>0 && ConversationRect("talk",$"ANSWER{i:00}") is {} rect)
            {
                string id="recoveryChoice/"+i;
                Draw("talk/ANSWER"+i.ToString("00"),rect,id,!_busy);
                RecoveryAnswer(i,id,details.RemainingOuterStrings[1+i]);
            }
        ConversationButton("talk","EXIT","recoveryClose","",!_busy);
        Flush();
    }
    private async Task<bool> ActivateRecovery(string id)
    {
        if(_recovery is not {} details)return false;
        int choice=id=="recoveryClose"?-1:id.StartsWith("recoveryChoice/")?int.Parse(id.AsSpan(15)):-2;
        if(choice==-1)
        {
            InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
            await CompleteRecovery();
        }
        else if(choice is >=0 and <3 && _recoveryChoice<0 && details.RemainingOuterStrings[1+choice].Length>0)
        {
            InteractionSoundRequested?.Invoke("fontclick.wav");
            _recoveryChoice=choice;_recoveryText=details.RemainingOuterStrings[4+choice];
            foreach(string key in _dialogueWipe.Keys.Where(k=>k.StartsWith("TALK",StringComparison.Ordinal)).ToArray())_dialogueWipe.Remove(key);
            if(_recoveryText.Length==0)await CompleteRecovery();
            else _recoveryPlayback?.Replace(_recoveryText,Environment.TickCount64*.001);
        }
        return true;
    }
}
