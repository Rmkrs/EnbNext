using EnbNext.Presentation;
using EnbNext.Rendering;
using System.Collections.Immutable;

namespace EnbNext.Engine;

internal sealed partial class EnbFrontendController
{
    private enum SelectionMotion { Entry, Overview, Advance, Selected, Retract, Exit, Exited }
    private SelectionMotion _selectionMotion;
    internal bool SelectionEntering=>_selectionMotion==SelectionMotion.Entry;
    private int _stagedSlot = -1, _nextSlot = -1;
    private int _creationPendingSlot = -1;
    private long _selectionStarted;
    private EnbRetailFrontendAnimationClock? _emptySlotAnimation;
    private long _emptySlotAnimationTick;
    private EnbRetailFrontendAnimationClock? _entryExitAnimation;
    private EnbW3dRenderHierarchyAnimation? _entryExitClip,_emptySlotClip;
    private long _entryExitAnimationTick;
    private EnbRetailFrontendAnimationClock? _occupiedSceneAnimation,_occupiedPanelAnimation;
    private EnbW3dRenderHierarchyAnimation? _occupiedSceneClip,_occupiedPanelClip;
    private long _occupiedAnimationTick;
    private EnbCurrentRetailHierarchyAnimationState? _overviewAnimation;
    private EnbW3dRenderHierarchyAnimation? _overviewClip;
    private EnbW3dHierarchyPoseOwner? _selectionLastScenePose;
    private EnbW3dHierarchyPoseOwner? _selectionLastPanelPose;
    private EnbStationBeamEffect? _deleteEffect;
    private long _deleteEffectStarted;
    private int _deleteEffectSlot=-1;
    private readonly HashSet<int> _deleteEffectPlayedSounds=[];
    private ImmutableArray<EnbWorldRenderer.FrontendBeamInstance> _deleteBeams=[];
    private string _pressedName = "";
    private bool _pointerInside;
    private bool SelectionMoving => _screen == "characters" &&
        _selectionMotion is not (SelectionMotion.Overview or SelectionMotion.Selected);
    private LiveAvatarSpeechPlayback CharacterSpeech(int slot)
    {
        if(!_characterSpeech.TryGetValue(slot,out var speech))
        {
            // 0055E370 constructs active roster actors with default idle flags.
            _characterSpeech[slot]=speech=new(_assets,_characters[slot].Appearance!,portraitIdle:true);
            speech.StartIdle(TickCount()*.001);
        }
        return speech;
    }
    private void BeginCharacterDeleteGesture(string phrase="please no")
    {
        if(_selected<0 || _characters[_selected].Appearance is null)return;
        CharacterSpeech(_selected).Replace("/angry "+phrase,TickCount()*.001);
    }
    public void ResolveCharacterDeleteGesture(bool accepted)
    {
        if(accepted)
        {
            EnbStationBeamEffect? effect;
            try { effect=new EnbStationBeamEffect(_assets,"av_tele01"); }
            catch(Exception ex) { effect=null;Diagnostic?.Invoke("Delete teleport unavailable: "+ex.Message); }
            if(_selected>=0 && _characters[_selected].Appearance is not null)
                CharacterSpeech(_selected).Replace("you'll pay for this",TickCount()*.001);
            // 0055D9C0's virtual +8C resolves to 0055ECB0: reverse the
            // currently playing FE01C selection clip and FE12A panel clip.
            // 00788F90 state 1 attaches AV_TELE01 to the retained actor.
            // Load before starting either clock: asset decoding must not eat
            // into retail's 3.2-second presentation window.
            _nextSlot=-1;
            BeginSelectionMotion(SelectionMotion.Retract);
            _deleteEffectStarted=TickCount();
            _deleteEffectSlot=_selected;
            _deleteEffectPlayedSounds.Clear();
            _deleteEffect=effect;
        }
        else
        {
            if(_selected>=0 && _characters[_selected].Appearance is not null)
                CharacterSpeech(_selected).Replace("/happy yeah",TickCount()*.001);
        }
    }
    public void CancelCharacterDeletePresentation()
    {
        // A failed delete leaves the original roster entry intact.
        _deleteEffect=null;
        _deleteEffectSlot=-1;
        _deleteBeams=[];
    }
    private void BeginSelectionMotion(SelectionMotion motion)
    {
        // 55EBD0 changes the advancing/control flags only after camera lookup
        // succeeds. A failed lookup leaves the already-bound HLOD playing.
        if(motion==SelectionMotion.Advance && _stagedSlot>=0 && !_characters[_stagedSlot].IsEmpty &&
            !StartOccupiedCameraAnimation($"fe01c-{_stagedSlot+5}"))return;
        if(motion==SelectionMotion.Advance)Owner.Text.ClearWrapped();
        _selectionMotion = motion;
        _selectionStarted = TickCount();
        _emptySlotAnimation=null;
        _emptySlotClip=null;
        _emptySlotAnimationTick=_selectionStarted;
        bool entryRequest=motion is SelectionMotion.Entry or SelectionMotion.Exit or SelectionMotion.Exited;
        bool emptyRequest=motion==SelectionMotion.Advance && _stagedSlot>=0 && _characters[_stagedSlot].IsEmpty;
        bool entryLoaded=false;
        EnbW3dRenderHierarchyAnimation? requestedEntry=null;
        if(entryRequest || emptyRequest)
        {
            entryLoaded=Model("fe01c").TryAnimation("fe01c-1",out requestedEntry);
            //67AD30 clears manual progress/completion even on failure, but
            //keeps the HLOD's last submitted pose/loop.55D9C0 still waits
            //for completion; absent optional animation cannot fake the latch.
            _occupiedSceneAnimation=null;
            if(!entryLoaded)Diagnostic?.Invoke("Roster entry animation unavailable: fe01c-1");
        }
        if(motion==SelectionMotion.Overview)StartOverviewAnimation("fe01c-10");
        else if(!(entryRequest || emptyRequest) || entryLoaded) {_overviewAnimation=null;_overviewClip=null;}
        if(motion is SelectionMotion.Entry or SelectionMotion.Exit or SelectionMotion.Exited)
        {
            _occupiedSceneAnimation=_occupiedPanelAnimation=null;
            _occupiedSceneClip=null;
            Model("fe12a").TryAnimation("fe12a-1",out _occupiedPanelClip);
            _selectionLastPanelPose=null;
            _entryExitClip=requestedEntry;
            _entryExitAnimation=requestedEntry is {} clip?new((int)clip.NumFrames,clip.FrameRate,motion!=SelectionMotion.Entry):null;
            _entryExitAnimationTick=_selectionStarted;
        }
        else if(emptyRequest)
        {
            _emptySlotClip=requestedEntry;
            _emptySlotAnimation=requestedEntry is {} clip?new((int)clip.NumFrames,clip.FrameRate,true):null;
        }
        else if(motion==SelectionMotion.Advance && _stagedSlot>=0 && !_characters[_stagedSlot].IsEmpty)
        {
            //55EBD0 binds both slots, then starts their independent clocks.
            StartOccupiedPanelAnimation("fe12a-1");
            _occupiedAnimationTick=_selectionStarted;
        }
        else if(motion==SelectionMotion.Retract && _occupiedSceneClip is {} sceneClip)
        {
            //55ECB0 restarts the retained clips in reverse, without a load.
            _occupiedSceneAnimation=new((int)sceneClip.NumFrames,sceneClip.FrameRate,true);
            _occupiedPanelAnimation=_occupiedPanelClip is {} panelClip
                ?new((int)panelClip.NumFrames,panelClip.FrameRate,true):null;
            _occupiedAnimationTick=_selectionStarted;
        }
    }
    private bool StartOccupiedCameraAnimation(string name)
    {
        if(!Model("fe01c").TryAnimation(name,out var clip))
        {
            Diagnostic?.Invoke("Roster camera animation unavailable: "+name);
            return false;
        }
        _occupiedSceneClip=clip;
        _occupiedSceneAnimation=new((int)clip!.NumFrames,clip.FrameRate,false);
        return true;
    }
    private void StartOccupiedPanelAnimation(string name)
    {
        // 55EBD0 calls 67AE20 even when panel-slot lookup 67AD30 fails.
        // Failure preserves its prior HAnim; absent prior HAnim is a no-op.
        if(Model("fe12a").TryAnimation(name,out var loaded))_occupiedPanelClip=loaded;
        else Diagnostic?.Invoke("Roster panel animation unavailable: "+name);
        _occupiedPanelAnimation=_occupiedPanelClip is {} clip
            ?new((int)clip.NumFrames,clip.FrameRate,false):null;
    }
    private void StartOverviewAnimation(string name)
    {
        //55D9C0 ->67AD30/67ADD0 binds FE01C-10 in HLOD mode1.
        //67AF70 skips that slot; the ordinary HLOD clock owns its loop.
        // Lookup resets the manual slot's progress even on failure, but does
        // not replace the HLOD's submitted animation or loop clock (67AD30).
        _occupiedSceneAnimation=null;
        if(Model("fe01c").TryAnimation(name,out var loaded))
        {
            _overviewClip=loaded;
            _occupiedSceneClip=_overviewClip;
            _overviewAnimation=new(){Mode=2,Playback=1,HasAnimation=true,HasHierarchy=true,
                PreviousTime=unchecked((uint)TickCount())};
        }
        else Diagnostic?.Invoke("Roster overview animation unavailable: "+name);
    }
    private void SelectCharacter(int slot)
    {
        // 55D16F/55D1D2: reset the outgoing actor before changing the slot,
        // even when the same name is clicked (the camera still stays put).
        ResetSelectedCharacterMood();
        if (_characters[slot].IsEmpty)
        {
            _creationPendingSlot = slot;
            if (_selectionMotion == SelectionMotion.Selected)
            {
                _nextSlot = slot;
                BeginSelectionMotion(SelectionMotion.Retract);
            }
            else
            {
                _stagedSlot = slot;
                BeginSelectionMotion(SelectionMotion.Advance);
            }
            return;
        }
        if (slot == _selected) return; // Retail 0055D060: feedback, but no scene restart.
        _selected = slot;
        CharacterSelected?.Invoke(slot);
        if (_selectionMotion == SelectionMotion.Selected)
        {
            _nextSlot = slot;
            BeginSelectionMotion(SelectionMotion.Retract);
        }
        else
        {
            _stagedSlot = slot;
            BeginSelectionMotion(SelectionMotion.Advance);
        }
    }
    private void PreviousCharacterView()
    {
        if (SelectionMoving) return;
        if (_selected >= 0)
        {
            ResetSelectedCharacterMood();
            _selected = _nextSlot = -1;
            CharacterSelected?.Invoke(-1);
            BeginSelectionMotion(SelectionMotion.Retract);
        }
        else BeginSelectionMotion(SelectionMotion.Exit);
    }
    private void ResetSelectedCharacterMood()
    {
        if(_selectionMotion==SelectionMotion.Selected && _selected>=0 && _characters[_selected].Appearance is not null)
            CharacterSpeech(_selected).ResetRosterMood();
    }
    private (EnbW3dHierarchyPoseOwner Scene, EnbW3dHierarchyPoseOwner Panel) SelectionPose()
    {
        var scene = Model("fe01c"); var panel = Model("fe12a");
        double time = (TickCount() - _selectionStarted) / 1000d;
        // 0055E8E0 starts FE01C-1 when the selected roster slot is empty.
        // Occupied slots use their individual camera clips.
        bool emptySlot = _creationPendingSlot >= 0 && _stagedSlot >= 0 && _characters[_stagedSlot].IsEmpty;
        string clip = _selectionMotion is SelectionMotion.Entry or SelectionMotion.Exit or SelectionMotion.Exited ||
            emptySlot
            ? "fe01c-1" : _stagedSlot >= 0 ? $"fe01c-{_stagedSlot + 5}" : "fe01c-10";
        bool overview=_selectionMotion==SelectionMotion.Overview;
        scene.TryAnimation(clip,out var requestedSceneClip);
        double duration = overview || requestedSceneClip is null?0:(requestedSceneClip.NumFrames-1d)/requestedSceneClip.FrameRate;
        double panelDuration = _occupiedPanelClip is {} durationClip ? (durationClip.NumFrames-1d)/durationClip.FrameRate : 0;
        // 0055E8E0 starts FE01C-1 through 0067AE60, which sets the reverse
        // playback flag. 0067AE20 clears that flag for the ordinary entry.
        bool emptySlotAdvance = _selectionMotion == SelectionMotion.Advance && emptySlot;
        bool reverse = _selectionMotion is SelectionMotion.Retract or SelectionMotion.Exit or SelectionMotion.Exited || emptySlotAdvance;
        double sceneTime = _selectionMotion == SelectionMotion.Selected ? emptySlot ? 0 : duration :
            _selectionMotion == SelectionMotion.Overview ? 0 : reverse ? Math.Max(0, duration - time) : time;
        double panelTime = _selectionMotion switch
        {
            SelectionMotion.Advance or SelectionMotion.Selected when emptySlot => 0,
            SelectionMotion.Advance => time,
            SelectionMotion.Selected => panelDuration,
            SelectionMotion.Retract => Math.Max(0, panelDuration - time),
            _ => 0
        };
        var scenePose=overview || requestedSceneClip is null?(_selectionLastScenePose??scene.Pose(null,0)):scene.PoseFrame(requestedSceneClip,(float)Math.Clamp(sceneTime*requestedSceneClip.FrameRate,0,requestedSceneClip.NumFrames-1));
        if(_overviewAnimation is {} overviewClock && _overviewClip is {} overviewClip)
        {
            uint now=unchecked((uint)TickCount());
            overviewClock.RefreshSingle(()=>now,()=>overviewClip.FrameRate,()=>checked((int)overviewClip.NumFrames),
                _=>{},()=>0,()=>0,(_,_,_)=>0);
            scenePose=scene.PoseFrame(overviewClip,overviewClock.CurrentFrame);
        }
        bool entryExit=_selectionMotion is SelectionMotion.Entry or SelectionMotion.Exit;
        bool entryExitComplete=false;
        if(entryExit && _entryExitAnimation is {} entryClock && _entryExitClip is {} entryClip)
        {
            //55C650/55D060 bind FE01C-1 forward/reverse;55D9C0
            //consumes its latch before the CharacterView update.
            entryExitComplete=entryClock.ConsumeCompletion();
            long now=TickCount();
            entryClock.Advance(unchecked((uint)(now-_entryExitAnimationTick)));
            _entryExitAnimationTick=now;
            scenePose=scene.PoseFrame(entryClip,entryClock.Frame);
        }
        bool emptyComplete=false;
        if(emptySlotAdvance && _emptySlotAnimation is {} emptyClock && _emptySlotClip is {} animation)
        {
            // Controller 55D9C0 consumes completion before CharacterView's
            // 679930 -> 67AF70 advances this frame's animation.
            emptyComplete=emptyClock.ConsumeCompletion();
            long now=TickCount();
            emptyClock.Advance(unchecked((uint)(now-_emptySlotAnimationTick)));
            _emptySlotAnimationTick=now;
            scenePose=scene.PoseFrame(animation,emptyClock.Frame);
        }
        var panelPose=_occupiedPanelClip is {} heldPanel
            ?panel.PoseFrame(heldPanel,(float)Math.Clamp(panelTime*heldPanel.FrameRate,0,heldPanel.NumFrames-1))
            :_selectionLastPanelPose??panel.Pose(null,0);
        bool occupiedClock=_occupiedSceneAnimation is not null && !emptySlotAdvance &&
            _selectionMotion is SelectionMotion.Advance or SelectionMotion.Retract or SelectionMotion.Selected;
        bool occupiedComplete=occupiedClock && _occupiedSceneAnimation!.ConsumeCompletion();
        if(occupiedClock || _occupiedPanelAnimation is not null)
        {
            long now=TickCount();
            uint delta=unchecked((uint)(now-_occupiedAnimationTick));
            if(occupiedClock)
            {
                _occupiedSceneAnimation!.Advance(delta);
                scenePose=scene.PoseFrame(_occupiedSceneClip,_occupiedSceneAnimation.Frame);
            }
            if(_occupiedPanelAnimation is {} panelClock && _occupiedPanelClip is {} panelClip)
            {
                panelClock.Advance(delta);
                panelPose=panel.PoseFrame(panelClip,panelClock.Frame);
            }
            _occupiedAnimationTick=now;
        }
        _selectionLastScenePose=scenePose;
        _selectionLastPanelPose=panelPose;
        var result = (scenePose, panelPose);
        // 55E8E0 starts only camera slot 1 for an empty roster entry;
        // FE12A panel playback belongs to occupied-slot handler 55EBD0.
        //55D9C0 consumes scene slot1; panel slot0 duration does not delay
        //the selection greeting or the next requested camera transition.
        if(entryExit ? !entryExitComplete : emptySlotAdvance ? !emptyComplete : occupiedClock ? !occupiedComplete : time < Math.Max(duration,
            _selectionMotion is SelectionMotion.Advance or SelectionMotion.Retract ? panelDuration : 0))return result;
        switch (_selectionMotion)
        {
            case SelectionMotion.Entry:
                _stagedSlot = -1; BeginSelectionMotion(SelectionMotion.Overview); break;
            case SelectionMotion.Advance:
                if(!_characters[_stagedSlot].IsEmpty && _characters[_stagedSlot].Appearance is not null)
                    CharacterSpeech(_stagedSlot).Replace("/happy at your service",TickCount()*.001);
                BeginSelectionMotion(SelectionMotion.Selected);
                if (_creationPendingSlot >= 0)
                {
                    int slot = _creationPendingSlot;
                    dispatch.Post(_=>CreationSlotRequested?.Invoke(slot),null);
                }
                break;
            case SelectionMotion.Retract:
                _stagedSlot = _nextSlot; _nextSlot = -1;
                // 55D9C0 binds the overview before attempting the next slot.
                // It remains active if that slot's camera cannot be loaded.
                BeginSelectionMotion(SelectionMotion.Overview);
                if(_stagedSlot>=0)BeginSelectionMotion(SelectionMotion.Advance);
                return SelectionPose();
            case SelectionMotion.Exit:
                _selectionMotion = SelectionMotion.Exited;
                dispatch.Post(_=>PreviousRequested?.Invoke(),null); break;
        }
        return result;
    }
    private void UpdateHover(EnbUiPoint point)
    {
        _pointerInside = true;
        _pointer = point;
        string next = Hit(point) ?? "";
        if(next!=_hover)_hoverBeganTick=TickCount();
        if (!_busy && Owner.Dialogs.Active is null && !SelectionMoving && next != _hover && next.Length > 0)
        {
            bool actor = !_hits.Any(h => h.Value.Contains(point)) && _actorHits.ContainsKey(next);
            if (actor && next != $"NAME{_selected:00}") _gpu.QueueFrontendSound("Avatar_Highlight");
            else if (_screen == "login" || _screen == "creation" &&
                (_creationPage == CreationPage.Ship
                    ? next is "CONFIRM" or "CANCEL" or "BUTTON_CANCEL" or "EXIT" or "RANDOM" or "NAME" or "LEFT" or "RIGHT"
                    : !next.StartsWith("SKILL", StringComparison.Ordinal)))
                _gpu.QueueFrontendSound("Login_Mouse_Over_Buttons");
        }
        _hover = next;
    }
}
